using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace Lots.Mcp.Homelab;

[McpServerToolType]
public sealed class HomelabTools(DockerApi docker)
{
    [McpServerTool(Name = "list_containers", ReadOnly = true, Destructive = false),
     Description("Lists Docker containers with state, status and health. Use onlyRunning=false to include stopped containers.")]
    public async Task<string> ListContainers(
        [Description("Only running containers (default true)")] bool onlyRunning = true,
        CancellationToken ct = default)
    {
        var containers = await docker.ListContainersAsync(all: !onlyRunning, ct);
        if (containers.Count == 0) return "No containers.";
        var sb = new StringBuilder();
        foreach (var c in containers.OrderBy(c => c.Name))
            sb.AppendLine($"{c.Name} | id={c.Id} | image={c.Image} | state={c.State} | status={c.Status}" +
                          (c.Health is null ? "" : $" | health={c.Health}"));
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_container_logs", ReadOnly = true, Destructive = false),
     Description("Returns the last lines of a container's logs (stdout+stderr, timestamped). Output is size-capped; the most recent lines are kept.")]
    public async Task<string> GetContainerLogs(
        [Description("Container name or id")] string container,
        [Description("Number of lines from the end (1-500, default 100)")] int tail = 100,
        CancellationToken ct = default)
    {
        try
        {
            var logs = await docker.GetLogsAsync(container, tail, ct);
            return string.IsNullOrWhiteSpace(logs) ? "(no log output)" : logs;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return $"No container named or identified '{container}'.";
        }
    }
}
