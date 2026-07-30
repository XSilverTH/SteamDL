using System.Net.Sockets;
using System.Text.Json;

namespace SteamDL.Services;

internal sealed record ControlRequest(string Command);

internal sealed record ControlResponse(
    bool Success,
    string State,
    long DownloadedBytes,
    long PipelineBytes,
    string? Error = null);

internal static class ControlProtocol
{
    public static async Task<ControlResponse> SendAsync(
        string socketPath,
        string command,
        CancellationToken cancellationToken = default)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        await using var stream = new NetworkStream(socket, false);
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.AutoFlush = true;

        await writer.WriteLineAsync(JsonSerializer.Serialize(new ControlRequest(command),
            ControlJsonContext.Default.ControlRequest));
        var response = await reader.ReadLineAsync(cancellationToken)
                       ?? throw new InvalidOperationException("SteamDL did not return a control response.");

        return JsonSerializer.Deserialize(response, ControlJsonContext.Default.ControlResponse)
               ?? throw new InvalidOperationException("SteamDL returned an invalid control response.");
    }

    public static async Task WriteAsync(StreamWriter writer, ControlResponse response)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, ControlJsonContext.Default.ControlResponse));
    }

    public static ControlRequest? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            return JsonSerializer.Deserialize(line, ControlJsonContext.Default.ControlRequest);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}