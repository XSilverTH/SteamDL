using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SteamDL.Services;

internal static partial class PrivilegedRelayProgram
{
    private const int PrSetPdeathsig = 1;
    private const ulong Sigterm = 15;

    public static bool IsInvocation(IReadOnlyList<string> arguments)
    {
        return arguments.Count > 0 && string.Equals(arguments[0], "--privileged-relay", StringComparison.Ordinal);
    }

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (!OperatingSystem.IsLinux())
        {
            await Console.Error.WriteLineAsync("SteamDL supports Linux only.");
            return 1;
        }

        if (geteuid() != 0 || !IsTrustedHelper())
        {
            await Console.Error.WriteLineAsync(
                "The SteamDL privileged helper must run from its root-owned installed location.");
            return 126;
        }

        if (!TryParseArguments(arguments, out var ownerPid, out var ownerUid, out var socketPath, out var upstreamPort))
        {
            await Console.Error.WriteLineAsync("The SteamDL privileged helper received invalid arguments.");
            return 2;
        }

        if (Prctl(PrSetPdeathsig, Sigterm, 0, 0, 0) != 0 || getppid() != ownerPid)
        {
            await Console.Error.WriteLineAsync("The SteamDL privileged helper lost its parent before startup.");
            return 1;
        }

        using var stop = new CancellationTokenSource();
        var stopToken = stop.Token;
        Action cancel = stop.Cancel;
        using var termRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancel();
        });
        using var interruptRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            cancel();
        });

        var traffic = new RelayTrafficCounter();
        var hosts = new HostsTransaction(ownerUid);
        await using var control = new ControlServer(
            socketPath,
            (request, _) => HandleControlAsync(request, traffic, cancel));
        TcpListener? listener = null;
        Task? relayTask = null;
        var exitCode = 0;

        try
        {
            await hosts.ApplyAsync(stopToken);
            listener = new TcpListener(IPAddress.Loopback, 80);
            listener.Start();
            control.Start();
            SetControlSocketOwner(socketPath, ownerUid);
            relayTask = AcceptRelayConnectionsAsync(listener, upstreamPort, traffic, stopToken);
            await Console.Out.WriteLineAsync("READY");
            await Task.Delay(Timeout.InfiniteTimeSpan, stopToken);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            await Console.Error.WriteLineAsync(
                "SteamDL cannot bind 127.0.0.1:80 because another process is already using it. Stop that proxy or server, then try Connect again.");
            exitCode = 1;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            exitCode = 1;
        }
        finally
        {
            await stop.CancelAsync();
            listener?.Stop();

            if (relayTask is not null)
                try
                {
                    await relayTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

            try
            {
                await hosts.RestoreAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                await Console.Error.WriteLineAsync($"SteamDL could not restore /etc/hosts: {exception.Message}");
                exitCode = 1;
            }
        }

        return exitCode;
    }

    private static Task<ControlResponse> HandleControlAsync(
        ControlRequest request,
        RelayTrafficCounter traffic,
        Action cancel)
    {
        var command = request.Command.Trim().ToLowerInvariant();
        if (command is not ("status" or "disconnect"))
            return Task.FromResult(new ControlResponse(false, "error", 0, traffic.Bytes, "Unknown relay command."));
        if (command == "disconnect") cancel();

        return Task.FromResult(new ControlResponse(true, "connected", 0, traffic.Bytes));
    }

    private static async Task AcceptRelayConnectionsAsync(
        TcpListener listener,
        int upstreamPort,
        RelayTrafficCounter traffic,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = RelayConnectionAsync(client, upstreamPort, traffic, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task RelayConnectionAsync(
        TcpClient client,
        int upstreamPort,
        RelayTrafficCounter traffic,
        CancellationToken cancellationToken)
    {
        using (client)
        using (var upstream = new TcpClient(AddressFamily.InterNetwork))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, linked.Token);
                await using var downstreamStream = client.GetStream();
                await using var upstreamStream = upstream.GetStream();
                var outbound = CopyAsync(downstreamStream, upstreamStream, traffic, linked.Token);
                var inbound = CopyAsync(upstreamStream, downstreamStream, traffic, linked.Token);
                await Task.WhenAny(outbound, inbound);
                await linked.CancelAsync();
                await Task.WhenAll(IgnoreCancellationAsync(outbound), IgnoreCancellationAsync(inbound));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task CopyAsync(
        Stream source,
        Stream destination,
        RelayTrafficCounter traffic,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                traffic.Add(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool TryParseArguments(
        string[] arguments,
        out int ownerPid,
        out int ownerUid,
        out string socketPath,
        out int upstreamPort)
    {
        ownerPid = 0;
        ownerUid = 0;
        socketPath = string.Empty;
        upstreamPort = 0;

        if (arguments.Length != 9) return false;

        return string.Equals(arguments[1], "--owner-pid", StringComparison.Ordinal)
               && int.TryParse(arguments[2], out ownerPid)
               && string.Equals(arguments[3], "--owner-uid", StringComparison.Ordinal)
               && int.TryParse(arguments[4], out ownerUid)
               && string.Equals(arguments[5], "--socket", StringComparison.Ordinal)
               && Path.IsPathFullyQualified(socketPath = arguments[6])
               && string.Equals(arguments[7], "--upstream-port", StringComparison.Ordinal)
               && int.TryParse(arguments[8], out upstreamPort)
               && upstreamPort is > 1024 and < 65536;
    }

    private static bool IsTrustedHelper()
    {
        if (!OperatingSystem.IsLinux()) return false;

        var processPath = Environment.ProcessPath;
        if (!string.Equals(processPath, PrivilegedHelperInstaller.InstalledHelperPath, StringComparison.Ordinal))
            return false;

        var mode = File.GetUnixFileMode(processPath!);

        return !mode.HasFlag(UnixFileMode.GroupWrite) && !mode.HasFlag(UnixFileMode.OtherWrite);
    }

    private static void SetControlSocketOwner(string socketPath, int ownerUid)
    {
        if (Chown(socketPath, (uint)ownerUid, uint.MaxValue) != 0)
            throw new InvalidOperationException(
                "SteamDL could not grant its owner access to the relay control socket.");
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Chown(string path, uint owner, uint group);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int Prctl(int option, ulong argument2, ulong argument3, ulong argument4, ulong argument5);

    [LibraryImport("libc")]
    private static partial int geteuid();

    [LibraryImport("libc")]
    private static partial int getppid();

    private sealed class RelayTrafficCounter
    {
        private long _bytes;

        public long Bytes => Interlocked.Read(ref _bytes);

        public void Add(int bytes)
        {
            Interlocked.Add(ref _bytes, bytes);
        }
    }
}