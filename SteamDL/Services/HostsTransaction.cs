using System.Text;

namespace SteamDL.Services;

internal sealed class HostsTransaction
{
    private const string HostsFile = "/etc/hosts";
    private const string StateDirectory = "/var/lib/steamdl";
    private const string MarkerPrefix = "# SteamDL managed: ";
    private readonly string _backupFile;
    private readonly string _hostsFile;

    private readonly int _ownerUid;
    private readonly string _stateDirectory;

    internal HostsTransaction(int ownerUid, string hostsFile = HostsFile, string stateDirectory = StateDirectory)
    {
        _ownerUid = ownerUid;
        _hostsFile = hostsFile;
        _stateDirectory = stateDirectory;
        _backupFile = Path.Combine(_stateDirectory, $"hosts-{ownerUid}.backup");
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        SetFileMode(
            _stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await RestoreStaleTransactionAsync(cancellationToken);

        var original = await File.ReadAllTextAsync(_hostsFile, cancellationToken);
        await WriteAtomicAsync(_backupFile, Encoding.UTF8.GetBytes(original), cancellationToken);
        await WriteAtomicAsync(_hostsFile, Encoding.UTF8.GetBytes(BuildManagedHosts(original, _ownerUid)),
            cancellationToken);
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_backupFile)) return;

        var original = await File.ReadAllBytesAsync(_backupFile, cancellationToken);
        await WriteAtomicAsync(_hostsFile, original, cancellationToken);
        File.Delete(_backupFile);
    }

    private static string BuildManagedHosts(string original, int ownerUid)
    {
        var normalized = original.EndsWith('\n') ? original : $"{original}\n";
        return $"{MarkerPrefix}{ownerUid}\n127.0.0.1 lancache.steamcontent.com\n{normalized}";
    }

    private async Task RestoreStaleTransactionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_backupFile)) return;

        var current = await File.ReadAllTextAsync(_hostsFile, cancellationToken);
        if (current.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            await RestoreAsync(cancellationToken);
            return;
        }

        File.Delete(_backupFile);
    }

    private static async Task WriteAtomicAsync(string destination, byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}-{Guid.NewGuid():N}.tmp");
        var destinationMode = OperatingSystem.IsLinux() && File.Exists(destination)
            ? File.GetUnixFileMode(destination)
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough))
            {
                SetFileMode(temporary, destinationMode);
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void SetFileMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, mode);
    }
}