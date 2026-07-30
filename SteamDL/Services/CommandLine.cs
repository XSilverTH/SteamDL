using System.Net.Sockets;

namespace SteamDL.Services;

internal static class CommandLine
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var paths = RuntimePaths.CreateForCurrentUser();
        var settingsStore = new SettingsStore(paths);

        return arguments[0] switch
        {
            "connect" when arguments.Length == 1 => await ConnectAsync(paths, settingsStore),
            "disconnect" when arguments.Length == 1 => await DisconnectAsync(paths),
            "status" when arguments.Length == 1 => await StatusAsync(paths),
            "token" when arguments.Length == 2 => await SaveTokenAsync(settingsStore, arguments[1]),
            "mitmdump" when arguments.Length == 2 => await SaveMitmdumpPathAsync(settingsStore, arguments[1]),
            "help" or "--help" or "-h" => PrintUsage(),
            _ => PrintUsage(1)
        };
    }

    private static async Task<int> ConnectAsync(RuntimePaths paths, SettingsStore settingsStore)
    {
        await using var connection = new ConnectionCoordinator(paths, settingsStore, new MitmdumpResolver(paths));
        using var stop = new CancellationTokenSource();
        Action cancel = stop.Cancel;
        Console.CancelKeyPress += OnCancel;

        try
        {
            await connection.ConnectAsync(stop.Token);
            Console.WriteLine("Connected. Press Ctrl+C to disconnect.");
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            await connection.DisconnectAsync(CancellationToken.None);
        }

        return 0;

        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancel();
        }
    }

    private static async Task<int> DisconnectAsync(RuntimePaths paths)
    {
        try
        {
            var response = await ControlProtocol.SendAsync(paths.ApplicationControlSocket, "disconnect");
            if (!response.Success)
            {
                await Console.Error.WriteLineAsync(response.Error ?? "SteamDL could not disconnect.");
                return 1;
            }

            Console.WriteLine("Disconnected.");
            return 0;
        }
        catch (SocketException)
        {
            await Console.Error.WriteLineAsync("SteamDL is not connected.");
            return 1;
        }
        catch (IOException)
        {
            await Console.Error.WriteLineAsync("SteamDL is not connected.");
            return 1;
        }
    }

    private static async Task<int> StatusAsync(RuntimePaths paths)
    {
        try
        {
            var response = await ControlProtocol.SendAsync(paths.ApplicationControlSocket, "status");
            if (!response.Success)
            {
                await Console.Error.WriteLineAsync(response.Error ?? "SteamDL could not report its status.");
                return 1;
            }

            Console.WriteLine(
                $"{response.State}: {DataSizeFormatter.Format(response.DownloadedBytes)} used; {DataSizeFormatter.Format(response.PipelineBytes)} through SteamDL");
            return 0;
        }
        catch (SocketException)
        {
            Console.WriteLine("disconnected");
            return 0;
        }
        catch (IOException)
        {
            Console.WriteLine("disconnected");
            return 0;
        }
    }

    private static async Task<int> SaveTokenAsync(SettingsStore settingsStore, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await Console.Error.WriteLineAsync("The token cannot be empty.");
            return 1;
        }

        var settings = settingsStore.Load();
        await settingsStore.SaveAsync(settings with { Token = token.Trim(), SetupCompleted = true });
        Console.WriteLine("Token saved.");
        return 0;
    }

    private static async Task<int> SaveMitmdumpPathAsync(SettingsStore settingsStore, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            await Console.Error.WriteLineAsync($"mitmdump was not found at '{fullPath}'.");
            return 1;
        }

        var settings = settingsStore.Load();
        await settingsStore.SaveAsync(settings with { MitmdumpPath = fullPath });
        Console.WriteLine("mitmdump path saved.");
        return 0;
    }

    private static int PrintUsage(int exitCode = 0)
    {
        Console.WriteLine("Usage: steamdl <connect|disconnect|status|token TOKEN|mitmdump PATH>");
        return exitCode;
    }
}

internal static class DataSizeFormatter
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Format(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} {Units[unit]}" : $"{value:0.##} {Units[unit]}";
    }
}