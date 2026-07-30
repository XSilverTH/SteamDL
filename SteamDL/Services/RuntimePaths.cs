using System.Runtime.InteropServices;

namespace SteamDL.Services;

internal sealed partial class RuntimePaths
{
    private const string ApplicationName = "steamdl";

    private RuntimePaths(string configDirectory, string dataDirectory, string runtimeDirectory)
    {
        ConfigDirectory = configDirectory;
        DataDirectory = dataDirectory;
        RuntimeDirectory = runtimeDirectory;
    }

    public string ConfigDirectory { get; }

    public string DataDirectory { get; }

    private string RuntimeDirectory { get; }

    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    public string AddonFile => Path.Combine(DataDirectory, "addon.py");

    public string DownloadCounterFile => Path.Combine(DataDirectory, "rx.txt");

    public string BundledMitmdumpFile => Path.Combine(DataDirectory, "bin", "mitmdump");

    public string ApplicationControlSocket => Path.Combine(RuntimeDirectory, "control.sock");

    public string RelayControlSocket => Path.Combine(RuntimeDirectory, "relay.sock");

    public static RuntimePaths CreateForCurrentUser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                         ?? Path.Combine(home, ".config");
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                       ?? Path.Combine(home, ".local", "share");
        var runtimeHome = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                          ?? Path.Combine(Path.GetTempPath(), $"steamdl-{GetUserId()}");

        return new RuntimePaths(
            Path.Combine(configHome, ApplicationName),
            Path.Combine(dataHome, ApplicationName),
            Path.Combine(runtimeHome, ApplicationName));
    }

    public void EnsureUserDirectories()
    {
        EnsurePrivateDirectory(ConfigDirectory);
        EnsurePrivateDirectory(DataDirectory);
        EnsurePrivateDirectory(RuntimeDirectory);
        EnsurePrivateDirectory(Path.GetDirectoryName(BundledMitmdumpFile)!);
    }

    public static int GetUserId()
    {
        return OperatingSystem.IsLinux()
            ? getuid()
            : throw new PlatformNotSupportedException("SteamDL supports Linux only.");
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [LibraryImport("libc")]
    private static partial int getuid();
}