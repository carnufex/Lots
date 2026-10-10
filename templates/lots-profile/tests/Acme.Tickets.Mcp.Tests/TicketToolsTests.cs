using Acme.Tickets.Mcp;

namespace Acme.Tickets.Mcp.Tests;

/// <summary>
/// Test the tools as plain code: what they return is what the model reads. Who may call them is the shell's policy, tested by the
/// profile's policyTests, and whether the agent uses them well is the eval dataset's job.
/// </summary>
public class TicketToolsTests
{
    private static TicketTools Tools() => new(new TicketStore());

    [Fact]
    public void Lists_only_open_tickets_when_asked()
    {
        var text = Tools().ListTickets("open");

        Assert.Contains("#101", text);
        Assert.DoesNotContain("#103", text);
    }

    [Fact]
    public void Unknown_ticket_is_a_clear_answer_not_an_exception()
    {
        Assert.Equal("No ticket #999.", Tools().GetTicket(999));
    }

    [Fact]
    public void Closing_needs_a_resolution_and_closes_once()
    {
        var tools = Tools();

        Assert.Equal("A resolution note is required.", tools.CloseTicket(101, " "));
        Assert.Equal("Closed #101.", tools.CloseTicket(101, "Freed disk space and re-ran the backup."));
        Assert.Contains("closed", tools.GetTicket(101));
    }
}
