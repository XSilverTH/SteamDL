using System.Diagnostics;
using System.Globalization;

namespace SteamDL.Services;

internal static class PrivilegedHelperInstaller
{
    internal const string InstalledHelperPath = "/usr/libexec/steamdl/steamdl-hosts-helper";

    public static async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(InstalledHelperPath)) return InstalledHelperPath;

        if (AppContext.GetData("APP_CONTEXT_DEPS_FILES") is string { Length: > 0 })
            throw new InvalidOperationException(
                "Install SteamDL from a package or publish it as a self-contained single file before connecting.");

        var currentExecutable = Environment.ProcessPath
                                ?? throw new InvalidOperationException(
                                    "SteamDL must be published as an executable before it can install its privileged helper.");

        if (!Path.IsPathFullyQualified(currentExecutable) || !File.Exists(currentExecutable))
            throw new InvalidOperationException(
                "SteamDL could not determine the executable used to install its privileged helper.");

        await RunPkexecAsync(
            "/usr/bin/install",
            ["-d", "-m", "755", "-o", "root", "-g", "root", Path.GetDirectoryName(InstalledHelperPath)!],
            cancellationToken);
        await RunPkexecAsync(
            "/usr/bin/install",
            ["-m", "755", "-o", "root", "-g", "root", currentExecutable, InstalledHelperPath],
            cancellationToken);

        return !File.Exists(InstalledHelperPath)
            ? throw new InvalidOperationException("SteamDL's privileged helper was not installed.")
            : InstalledHelperPath;
    }

    public static Process StartRelay(string helperPath, int ownerPid, int ownerUid, string socketPath, int upstreamPort)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/pkexec")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(helperPath);
        startInfo.ArgumentList.Add("--privileged-relay");
        startInfo.ArgumentList.Add("--owner-pid");
        startInfo.ArgumentList.Add(ownerPid.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--owner-uid");
        startInfo.ArgumentList.Add(ownerUid.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--socket");
        startInfo.ArgumentList.Add(socketPath);
        startInfo.ArgumentList.Add("--upstream-port");
        startInfo.ArgumentList.Add(upstreamPort.ToString(CultureInfo.InvariantCulture));

        return Process.Start(startInfo) ??
               throw new InvalidOperationException("SteamDL could not start its privileged helper.");
    }

    private static async Task RunPkexecAsync(string command, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists("/usr/bin/pkexec"))
            throw new InvalidOperationException("pkexec is required to modify /etc/hosts.");

        var startInfo = new ProcessStartInfo("/usr/bin/pkexec")
        {
            UseShellExecute = false,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(command);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                "SteamDL could not request administrator permission.");
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await standardError;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "Administrator permission was not granted."
                    : error.Trim());
    }
}