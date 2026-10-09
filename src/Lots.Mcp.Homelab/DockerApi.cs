using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lots.Mcp.Homelab;

public sealed record ContainerInfo(
    string Id, string Name, string Image, string State, string Status, string? Health);

/// <summary>
/// Read-only client for the Docker Engine API. It only ever issues GET requests and is meant to talk to a
/// read-only socket proxy, never the raw Docker socket.
/// </summary>
public sealed class DockerApi(HttpClient http)
{
    public const int MaxLogBytes = 12 * 1024;
    public const int MaxLogLines = 500;

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(bool all, CancellationToken ct)
    {
        var json = await http.GetStringAsync($"containers/json?all={(all ? "true" : "false")}", ct);
        var items = JsonSerializer.Deserialize<List<ContainerDto>>(json) ?? [];
        return items.Select(c => new ContainerInfo(
            c.Id[..Math.Min(12, c.Id.Length)],
            (c.Names.FirstOrDefault() ?? "").TrimStart('/'),
            c.Image, c.State, c.Status, HealthFromStatus(c.Status))).ToList();
    }

    public async Task<string> GetLogsAsync(string container, int tail, CancellationToken ct)
    {
        tail = Math.Clamp(tail, 1, MaxLogLines);
        var id = Uri.EscapeDataString(container);
        var inspect = JsonSerializer.Deserialize<InspectDto>(await http.GetStringAsync($"containers/{id}/json", ct))!;
        var bytes = await http.GetByteArrayAsync(
            $"containers/{id}/logs?stdout=true&stderr=true&timestamps=true&tail={tail}", ct);
        var text = inspect.Config.Tty ? Encoding.UTF8.GetString(bytes) : DemuxFrames(bytes);
        return Truncate(text);
    }

    /// <summary>Non-TTY containers use Docker's multiplexed stream: 8-byte header + payload per frame.</summary>
    public static string DemuxFrames(byte[] data)
    {
        var sb = new StringBuilder();
        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            var len = (data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7];
            pos += 8;
            len = Math.Min(len, data.Length - pos);
            sb.Append(Encoding.UTF8.GetString(data, pos, len));
            pos += len;
        }
        return sb.ToString();
    }

    /// <summary>Keeps the END of the logs (most recent lines) and says clearly that output was cut.</summary>
    public static string Truncate(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaxLogBytes) return text;
        var tail = text[^MaxLogBytes..];
        var firstNewline = tail.IndexOf('\n');
        if (firstNewline >= 0) tail = tail[(firstNewline + 1)..];
        return $"[truncated: showing only the last ~{MaxLogBytes / 1024} KiB of the requested logs]\n{tail}";
    }

    private static string? HealthFromStatus(string status) =>
        status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase) ? "unhealthy"
        : status.Contains("(healthy)", StringComparison.OrdinalIgnoreCase) ? "healthy"
        : status.Contains("(health: starting)", StringComparison.OrdinalIgnoreCase) ? "starting"
        : null;

    private sealed record ContainerDto(
        [property: JsonPropertyName("Id")] string Id,
        [property: JsonPropertyName("Names")] List<string> Names,
        [property: JsonPropertyName("Image")] string Image,
        [property: JsonPropertyName("State")] string State,
        [property: JsonPropertyName("Status")] string Status);

    private sealed record InspectDto([property: JsonPropertyName("Config")] ConfigDto Config);

    private sealed record ConfigDto([property: JsonPropertyName("Tty")] bool Tty);
}
