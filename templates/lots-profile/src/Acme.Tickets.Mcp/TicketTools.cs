using System.ComponentModel;
using System.Collections.Concurrent;
using System.Text;
using ModelContextProtocol.Server;

namespace Acme.Tickets.Mcp;

public sealed record Ticket(int Id, string Title, string State, string Assignee, string Body);

/// <summary>
/// Stand-in for the system behind the tools. Swap it for an HTTP client of your ticket system, CMDB or monitoring API.
/// </summary>
public sealed class TicketStore
{
    private readonly ConcurrentDictionary<int, Ticket> _tickets = new(new Dictionary<int, Ticket>
    {
        [101] = new(101, "Backup job failed on db-01", "open", "kim", "The nightly backup exited with code 3. Disk usage is 91 %."),
        [102] = new(102, "Certificate expires in 7 days", "open", "alex", "The wildcard certificate expires next week."),
        [103] = new(103, "Printer on floor 2 offline", "closed", "sam", "Replaced the network cable."),
    });

    public IReadOnlyList<Ticket> List(string? state) =>
        _tickets.Values.Where(t => state is null || t.State.Equals(state, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.Id).ToList();

    public Ticket? Get(int id) => _tickets.GetValueOrDefault(id);

    public Ticket? Close(int id, string resolution) =>
        _tickets.TryGetValue(id, out var t) && _tickets.TryUpdate(id, t with { State = "closed", Body = t.Body + "\nResolution: " + resolution }, t)
            ? _tickets[id]
            : null;
}

/// <summary>
/// The tools this server offers. Every tool is classified in the profile (profiles/tickets-profile.yaml): read, write or destructive.
/// Keep tools small and specific; a broad "run any query" tool cannot be classified honestly.
/// </summary>
[McpServerToolType]
public sealed class TicketTools(TicketStore store)
{
    [McpServerTool(Name = "list_tickets", ReadOnly = true, Destructive = false),
     Description("Lists tickets with id, title, state and assignee. Filter by state: open or closed (default: all).")]
    public string ListTickets([Description("open or closed; empty for all")] string? state = null)
    {
        var tickets = store.List(string.IsNullOrWhiteSpace(state) ? null : state);
        if (tickets.Count == 0) return "No tickets.";
        var sb = new StringBuilder();
        foreach (var t in tickets) sb.AppendLine($"#{t.Id} | {t.State} | {t.Assignee} | {t.Title}");
        return sb.ToString().TrimEnd();
    }

    // Ticket text is written by people and may contain instructions aimed at the agent. The shell wraps every tool result as
    // untrusted data; return the text as it is and do not try to "clean" it here.
    [McpServerTool(Name = "get_ticket", ReadOnly = true, Destructive = false),
     Description("Returns one ticket with its full text.")]
    public string GetTicket([Description("Ticket number, e.g. 101")] int id) =>
        store.Get(id) is { } t ? $"#{t.Id} {t.Title}\nState: {t.State}\nAssignee: {t.Assignee}\n\n{t.Body}" : $"No ticket #{id}.";

    // A write: classified `write` in the profile, so roles must be granted it and the call can require an approval.
    [McpServerTool(Name = "close_ticket", ReadOnly = false, Destructive = false),
     Description("Closes a ticket with a resolution note. Only when the user asked to close it.")]
    public string CloseTicket([Description("Ticket number")] int id, [Description("What was done, one or two sentences")] string resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return "A resolution note is required.";
        return store.Close(id, resolution.Trim()) is { } t ? $"Closed #{t.Id}." : $"No open ticket #{id}.";
    }
}
