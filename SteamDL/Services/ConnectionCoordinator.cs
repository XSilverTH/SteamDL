using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SteamDL.Services;

internal enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Faulted
}

internal sealed record ConnectionSnapshot(
    ConnectionState State,
    long DownloadedBytes,
    long PipelineBytes,
    string? Error = null);

internal sealed partial class ConnectionCoordinator(
    RuntimePaths paths,
    SettingsStore settingsStore,
    MitmdumpResolver mitmdumpResolver)
    : IAsyncDisposable
{
    private const string ReverseProxyAddress = "http://dl.steamdl.ir";
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Lock _snapshotGate = new();
    private ControlServer? _applicationControl;
    private CancellationTokenSource? _metricsStop;
    private Task? _metricsTask;
    private Process? _mitmdump;
    private Process? _relay;
    private ConnectionSnapshot _snapshot = new(ConnectionState.Disconnected, 0, 0);
    private int _unexpectedCleanupQueued;

    public ConnectionSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate)
            {
                return _snapshot;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    public event Action<ConnectionSnapshot>? SnapshotChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Snapshot.State is ConnectionState.Connected or ConnectionState.Connecting) return;

            paths.EnsureUserDirectories();
            await EnsureNoRunningInstanceAsync(cancellationToken);
            SetSnapshot(new ConnectionSnapshot(ConnectionState.Connecting, ReadDownloadedBytes(), 0));

            var settings = settingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.Token))
                throw new InvalidOperationException("Enter your SteamDL token in Settings before connecting.");

            var mitmdumpPath = await mitmdumpResolver.ResolveMitmdumpAsync(settings, cancellationToken);
            var addonPath = await mitmdumpResolver.GetAddonPathAsync(cancellationToken);
            var upstreamPort = ReserveLoopbackPort();

            _mitmdump = StartMitmdump(mitmdumpPath, addonPath, settings.Token.Trim(), upstreamPort);
            _mitmdump.EnableRaisingEvents = true;
            _mitmdump.Exited += OnManagedProcessExited;
            await WaitForPortAsync(upstreamPort, cancellationToken);

            var helperPath = await PrivilegedHelperInstaller.EnsureInstalledAsync(cancellationToken);
            _relay = PrivilegedHelperInstaller.StartRelay(
                helperPath,
                Environment.ProcessId,
                RuntimePaths.GetUserId(),
                paths.RelayControlSocket,
                upstreamPort);
            _relay.EnableRaisingEvents = true;
            _relay.Exited += OnManagedProcessExited;
            await WaitForRelayReadyAsync(_relay, cancellationToken);

            _applicationControl = new ControlServer(paths.ApplicationControlSocket, HandleApplicationControlAsync);
            _applicationControl.Start();

            var connected = new ConnectionSnapshot(
                ConnectionState.Connected,
                ReadDownloadedBytes(),
                await ReadPipelineBytesAsync(cancellationToken));
            SetSnapshot(connected);
            _metricsStop = new CancellationTokenSource();
            var metricsToken = _metricsStop.Token;
            _metricsTask = Task.Run(() => PollMetricsAsync(metricsToken), cancellationToken);
        }
        catch (Exception exception)
        {
            await DisconnectCoreAsync(cancellationToken);
            SetSnapshot(new ConnectionSnapshot(ConnectionState.Faulted, ReadDownloadedBytes(), 0, exception.Message));
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Snapshot.State == ConnectionState.Disconnected) return;

            SetSnapshot(Snapshot with { State = ConnectionState.Disconnecting, Error = null });
            await DisconnectCoreAsync(cancellationToken);
            SetSnapshot(new ConnectionSnapshot(ConnectionState.Disconnected, ReadDownloadedBytes(), 0));
        }
        catch (Exception exception)
        {
            SetSnapshot(new ConnectionSnapshot(ConnectionState.Faulted, ReadDownloadedBytes(), 0, exception.Message));
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken = default)
    {
        if (_metricsStop is not null)
        {
            await _metricsStop.CancelAsync();
            if (_metricsTask is not null)
                try
                {
                    await _metricsTask;
                }
                catch (OperationCanceledException)
                {
                }

            _metricsStop.Dispose();
            _metricsStop = null;
            _metricsTask = null;
        }

        if (_applicationControl is not null)
        {
            await _applicationControl.DisposeAsync();
            _applicationControl = null;
        }

        if (_relay is not null)
        {
            await StopPrivilegedRelayAsync(_relay, cancellationToken);
            _relay.Dispose();
            _relay = null;
        }

        if (_mitmdump is not null)
        {
            await StopProcessAsync(_mitmdump, cancellationToken);
            _mitmdump.Dispose();
            _mitmdump = null;
        }
    }

    private async Task<ControlResponse> HandleApplicationControlAsync(ControlRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Command.Trim().ToLowerInvariant())
        {
            case "status":
                return ToControlResponse(Snapshot, true);
            case "disconnect":
                await DisconnectAsync(cancellationToken);
                return ToControlResponse(Snapshot, true);
            default:
                return new ControlResponse(false, "error", 0, 0, "Unknown SteamDL command.");
        }
    }

    private static ControlResponse ToControlResponse(ConnectionSnapshot snapshot, bool success)
    {
        return new ControlResponse(
            success,
            snapshot.State.ToString().ToLowerInvariant(),
            snapshot.DownloadedBytes,
            snapshot.PipelineBytes,
            snapshot.Error);
    }

    private async Task EnsureNoRunningInstanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await ControlProtocol.SendAsync(paths.ApplicationControlSocket, "status", cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
            if (status.Success) throw new InvalidOperationException("SteamDL is already running.");
        }
        catch (SocketException)
        {
            DeleteSocketIfPresent(paths.ApplicationControlSocket);
        }
        catch (IOException)
        {
            DeleteSocketIfPresent(paths.ApplicationControlSocket);
        }
    }

    private Process StartMitmdump(string executable, string addon, string token, int upstreamPort)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = paths.DataDirectory
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add($"reverse:{ReverseProxyAddress}@127.0.0.1:{upstreamPort}");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(addon);
        startInfo.ArgumentList.Add("--set");
        startInfo.ArgumentList.Add($"token={token}");
        startInfo.ArgumentList.Add("--set");
        startInfo.ArgumentList.Add("keep_host_header=true");
        startInfo.ArgumentList.Add("--set");
        startInfo.ArgumentList.Add("flow_detail=0");
        startInfo.ArgumentList.Add("--set");
        startInfo.ArgumentList.Add("stream_large_bodies=1k");

        return Process.Start(startInfo) ?? throw new InvalidOperationException("SteamDL could not start mitmdump.");
    }

    private static async Task WaitForRelayReadyAsync(Process relay, CancellationToken cancellationToken)
    {
        var ready = await relay.StandardOutput.ReadLineAsync(cancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        if (string.Equals(ready, "READY", StringComparison.Ordinal)) return;

        var error = await relay.StandardError.ReadToEndAsync(cancellationToken);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(error) ? "SteamDL's privileged relay did not start." : error.Trim());
    }

    private async Task PollMetricsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (Snapshot.State != ConnectionState.Connected) continue;

                SetSnapshot(Snapshot with
                {
                    DownloadedBytes = ReadDownloadedBytes(),
                    PipelineBytes = await ReadPipelineBytesAsync(cancellationToken)
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetSnapshot(Snapshot with { Error = exception.Message });
        }
    }

    private async Task<long> ReadPipelineBytesAsync(CancellationToken cancellationToken)
    {
        if (_relay is null || _relay.HasExited) return 0;

        try
        {
            var response = await ControlProtocol.SendAsync(paths.RelayControlSocket, "status", cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            return response.Success ? response.PipelineBytes : 0;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    private long ReadDownloadedBytes()
    {
        try
        {
            using var stream = new FileStream(
                paths.DownloadCounterFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return long.TryParse(reader.ReadToEnd().Trim(), out var bytes) && bytes >= 0 ? bytes : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void OnManagedProcessExited(object? sender, EventArgs eventArgs)
    {
        if (Snapshot.State is ConnectionState.Disconnecting or ConnectionState.Disconnected) return;

        SetSnapshot(Snapshot with { State = ConnectionState.Faulted, Error = "SteamDL stopped unexpectedly." });
        if (Interlocked.Exchange(ref _unexpectedCleanupQueued, 1) == 0) _ = CleanupUnexpectedExitAsync();
    }

    private async Task CleanupUnexpectedExitAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Snapshot.State != ConnectionState.Faulted) return;

            var failure = Snapshot.Error;
            await DisconnectCoreAsync();
            SetSnapshot(new ConnectionSnapshot(ConnectionState.Faulted, ReadDownloadedBytes(), 0, failure));
        }
        finally
        {
            Interlocked.Exchange(ref _unexpectedCleanupQueued, 0);
            _gate.Release();
        }
    }

    private void SetSnapshot(ConnectionSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _snapshot = snapshot;
        }

        SnapshotChanged?.Invoke(snapshot);
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(int port, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var probe = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new InvalidOperationException("mitmdump did not start listening in time.");
    }

    private async Task StopPrivilegedRelayAsync(Process relay, CancellationToken cancellationToken)
    {
        if (relay.HasExited) return;

        ControlResponse response;
        try
        {
            response = await ControlProtocol.SendAsync(paths.RelayControlSocket, "disconnect", cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (relay.HasExited) return;

            throw new InvalidOperationException(
                "SteamDL could not ask its privileged relay to disconnect. Keep SteamDL open and try Disconnect again.",
                exception);
        }

        if (!response.Success)
            throw new InvalidOperationException(
                response.Error ?? "SteamDL's privileged relay refused to disconnect.");

        try
        {
            await relay.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                "SteamDL could not confirm that its privileged relay restored your hosts file. Keep SteamDL open and try Disconnect again.");
        }
    }

    private static async Task StopProcessAsync(
        Process process,
        CancellationToken cancellationToken,
        TimeSpan? gracefulShutdownTimeout = null)
    {
        if (process.HasExited) return;

        Terminate(process);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(gracefulShutdownTimeout ?? TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(cancellationToken);
            }
        }
    }

    private static void Terminate(Process process)
    {
        if (!process.HasExited) Kill(process.Id, 15);
    }

    private static void DeleteSocketIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int processId, int signal);
}