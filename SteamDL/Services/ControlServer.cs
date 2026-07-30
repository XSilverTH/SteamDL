using System.Net.Sockets;

namespace SteamDL.Services;

internal sealed class ControlServer(
    string socketPath,
    Func<ControlRequest, CancellationToken, Task<ControlResponse>> handler)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;
    private Socket? _listener;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener?.Dispose();

        if (_acceptLoop is not null)
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }

        DeleteSocketIfPresent(socketPath);
        _stop.Dispose();
    }

    public void Start()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("SteamDL control sockets require Linux.");
        if (_listener is not null)
            throw new InvalidOperationException("The SteamDL control server is already running.");

        DeleteSocketIfPresent(socketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(8);
        _acceptLoop = AcceptLoopAsync(_listener, _stop.Token);
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = new NetworkStream(client, false))
        using (var reader = new StreamReader(stream, leaveOpen: true))
        await using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            writer.AutoFlush = true;
            try
            {
                var request = ControlProtocol.Parse(await reader.ReadLineAsync(cancellationToken));
                if (request is null)
                {
                    await ControlProtocol.WriteAsync(
                        writer,
                        new ControlResponse(false, "error", 0, 0, "Invalid control request."));
                    return;
                }

                await ControlProtocol.WriteAsync(writer, await handler(request, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await ControlProtocol.WriteAsync(
                    writer,
                    new ControlResponse(false, "error", 0, 0, exception.Message));
            }
        }
    }

    private static void DeleteSocketIfPresent(string socketPath)
    {
        try
        {
            File.Delete(socketPath);
        }
        catch (FileNotFoundException)
        {
        }
    }
}