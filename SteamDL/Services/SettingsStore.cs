using System.Text.Json;

namespace SteamDL.Services;

internal sealed record AppSettings(
    string Token,
    string? MitmdumpPath,
    bool SetupCompleted)
{
    public static AppSettings Empty { get; } = new(string.Empty, null, false);
}

internal sealed class SettingsStore(RuntimePaths paths)
{
    public AppSettings Load()
    {
        paths.EnsureUserDirectories();
        if (!File.Exists(paths.SettingsFile)) return AppSettings.Empty;

        try
        {
            var json = File.ReadAllText(paths.SettingsFile);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? AppSettings.Empty;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("SteamDL settings are not valid JSON.", exception);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        paths.EnsureUserDirectories();
        var temporaryFile = Path.Combine(paths.ConfigDirectory, $".settings-{Guid.NewGuid():N}");

        try
        {
            await using (var stream = new FileStream(
                             temporaryFile,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough))
            {
                SetPrivateFileMode(temporaryFile);
                await JsonSerializer.SerializeAsync(stream, settings, SettingsJsonContext.Default.AppSettings,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryFile, paths.SettingsFile, true);
            SetPrivateFileMode(paths.SettingsFile);
        }
        finally
        {
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}