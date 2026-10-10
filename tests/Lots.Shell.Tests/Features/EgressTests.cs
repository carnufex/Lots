using System.Net;
using Lots.Net;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Net;
using Lots.Shell.Core.Profiles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>#86: the shell's outbound connections follow Egress:* rules; metadata addresses are never reachable by default.</summary>
public class EgressTests
{
    private static ShellEgress Egress(Action<EgressOptions>? configure = null)
    {
        var o = new EgressOptions();
        configure?.Invoke(o);
        return new ShellEgress(Options.Create(o));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[fe80::1]:8080/")]
    [InlineData("http://0.0.0.0:8089/mcp")]
    [InlineData("http://224.0.0.1/")]
    public void Link_local_and_reserved_addresses_are_blocked_by_default(string url) =>
        Assert.Throws<EgressDeniedException>(() => Egress().Check(EgressPurpose.Mcp, url));

    [Theory]
    [InlineData("http://localhost:8089/mcp")]
    [InlineData("http://192.168.1.215:11434/v1")]
    [InlineData("http://mcp-homelab:8080/mcp")]
    [InlineData("https://cmdb.example.org/mcp")]
    public void Internal_backends_are_reachable_by_default(string url) => Egress().Check(EgressPurpose.Mcp, url);

    [Fact]
    public void A_purpose_can_narrow_hosts_and_forbid_private_networks()
    {
        var egress = Egress(o => o.Webhook = new EgressRule { AllowedHosts = ["hooks.slack.com", "*.office.com"], AllowPrivateNetworks = false });
        egress.Check(EgressPurpose.Webhook, "https://hooks.slack.com/services/x");
        egress.Check(EgressPurpose.Webhook, "https://acme.webhook.office.com/x");
        Assert.Throws<EgressDeniedException>(() => egress.Check(EgressPurpose.Webhook, "https://evil.example/x"));
        Assert.Throws<EgressDeniedException>(() => egress.Check(EgressPurpose.Webhook, "https://hooks.slack.com.evil.example/x"));
        egress.Check(EgressPurpose.Mcp, "http://10.0.0.5/mcp"); // other purposes keep the default
    }

    [Fact]
    public void The_default_rule_applies_to_every_purpose()
    {
        var egress = Egress(o => o.Default = new EgressRule { AllowedHosts = ["*.corp.example"] });
        egress.Check(EgressPurpose.Knowledge, "https://wiki.corp.example/page");
        Assert.Throws<EgressDeniedException>(() => egress.Check(EgressPurpose.Knowledge, "https://pastebin.com/raw/x"));
        Assert.Throws<EgressDeniedException>(() => egress.Check(EgressPurpose.Identity, "https://login.example.com/token"));
    }

    [Fact]
    public async Task The_handler_checks_the_resolved_address_not_just_the_name()
    {
        // "localhost" passes the host allowlist, but resolves to loopback: a rebinding-style target when private networks are off.
        var egress = Egress(o => o.Knowledge = new EgressRule { AllowPrivateNetworks = false });
        using var client = new HttpClient(egress.Handler(EgressPurpose.Knowledge));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://localhost:1/"));
        Assert.IsType<EgressDeniedException>(ex.InnerException);
    }

    [Fact]
    public void Addresses_are_classified()
    {
        Assert.True(EgressPolicy.IsLinkLocalOrReserved(IPAddress.Parse("169.254.169.254")));
        Assert.True(EgressPolicy.IsLinkLocalOrReserved(IPAddress.Parse("::ffff:169.254.169.254")));
        Assert.False(EgressPolicy.IsLinkLocalOrReserved(IPAddress.Parse("10.0.0.1")));
        Assert.True(EgressPolicy.IsPrivate(IPAddress.Parse("10.0.0.1")));
        Assert.True(EgressPolicy.IsPrivate(IPAddress.Parse("::1")));
        Assert.False(EgressPolicy.IsPrivate(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public async Task An_mcp_server_on_a_metadata_address_is_never_contacted()
    {
        var servers = new[] { new McpServerConfig("meta", "http://169.254.169.254/mcp") };
        await using var source = new McpToolSource(() => servers, NullLoggerFactory.Instance, egress: Egress());
        Assert.Empty(await source.ListAsync(default));
        var status = Assert.Single(await source.StatusAsync(default));
        Assert.Contains("egress denied", status.Error);
    }
}
