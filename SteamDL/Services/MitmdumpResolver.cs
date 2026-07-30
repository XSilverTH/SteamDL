using System.Reflection;

namespace SteamDL.Services;

internal sealed class MitmdumpResolver(RuntimePaths paths)
{
    private const string BundledMitmdumpResource = "SteamDL.Mitm.Mitmdump";
    private const string BundledAddonResource = "SteamDL.Mitm.Addon";

    public async Task<string> ResolveMitmdumpAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        paths.EnsureUserDirectories();

        if (!string.IsNullOrWhiteSpace(settings.MitmdumpPath))
            return ValidateExecutable(Path.GetFullPath(settings.MitmdumpPath));

        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "mitmdump"),
                     Path.Combine(AppContext.BaseDirectory, "bin", "mitmdump"),
                     paths.BundledMitmdumpFile
                 })
            if (File.Exists(candidate))
                return ValidateExecutable(candidate);

        var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledMitmdumpResource);
        if (embedded is not null)
        {
            await ExtractPrivateExecutableAsync(embedded, paths.BundledMitmdumpFile, cancellationToken);
            return ValidateExecutable(paths.BundledMitmdumpFile);
        }

        if (File.Exists("/usr/bin/mitmdump")) return ValidateExecutable("/usr/bin/mitmdump");

        var fromPath = FindOnPath("mitmdump");
        if (fromPath is not null) return ValidateExecutable(fromPath);

        throw new InvalidOperationException(
            "mitmdump was not found. Install it system-wide, place it next to SteamDL, or set its path in Settings.");
    }

    public async Task<string> GetAddonPathAsync(CancellationToken cancellationToken)
    {
        paths.EnsureUserDirectories();

        var copiedAddon = Path.Combine(AppContext.BaseDirectory, "Mitm", "addon.py");
        if (File.Exists(copiedAddon)) return copiedAddon;

        if (File.Exists(paths.AddonFile)) return paths.AddonFile;

        var addon = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledAddonResource)
                    ?? throw new InvalidOperationException("SteamDL's bundled mitmdump addon is missing.");
        await ExtractPrivateFileAsync(addon, paths.AddonFile, cancellationToken);
        return paths.AddonFile;
    }

    private static string ValidateExecutable(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"mitmdump was not found at '{path}'.");

        if (OperatingSystem.IsLinux()
            && !File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute)
            && !File.GetUnixFileMode(path).HasFlag(UnixFileMode.GroupExecute)
            && !File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherExecute))
            throw new InvalidOperationException($"mitmdump at '{path}' is not executable.");

        return path;
    }

    private static string? FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        return string.IsNullOrWhiteSpace(path)
            ? null
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, executableName)).FirstOrDefault(File.Exists);
    }

    private static async Task ExtractPrivateExecutableAsync(
        Stream source,
        string destination,
        CancellationToken cancellationToken)
    {
        await ExtractPrivateFileAsync(source, destination, cancellationToken);
        SetPrivateFileMode(
            destination,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task ExtractPrivateFileAsync(Stream source, string destination,
        CancellationToken cancellationToken)
    {
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (source)
            await using (var target = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough))
            {
                SetPrivateFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, true);
            SetPrivateFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void SetPrivateFileMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, mode);
    }
}