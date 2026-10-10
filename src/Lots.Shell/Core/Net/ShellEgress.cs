using Lots.Net;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Net;

/// <summary>One egress rule: which hosts, and whether private networks (where backends usually live) are reachable.</summary>
public sealed class EgressRule
{
    /// <summary>Host patterns (<c>*</c> wildcards). Null inherits the default rule.</summary>
    public List<string>? AllowedHosts { get; set; }
    public bool? AllowPrivateNetworks { get; set; }
    /// <summary>Link-local addresses (cloud metadata 169.254.169.254, fe80::/10). Never needed by a real backend; off unless set.</summary>
    public bool? AllowLinkLocal { get; set; }
}

/// <summary>
/// Outbound connections the shell opens on behalf of configuration (#86): MCP servers, identity token endpoints, notification and
/// audit webhooks, knowledge URL sources. <c>Egress:Default</c> applies to all, <c>Egress:&lt;purpose&gt;</c> narrows one. The
/// defaults keep internal backends reachable (they are usually on private networks) and block link-local/metadata addresses.
/// </summary>
public sealed class EgressOptions
{
    public const string Section = "Egress";

    public EgressRule Default { get; set; } = new();
    public EgressRule Mcp { get; set; } = new();
    public EgressRule Identity { get; set; } = new();
    public EgressRule Webhook { get; set; } = new();
    public EgressRule Knowledge { get; set; } = new();
}

public enum EgressPurpose { Mcp, Identity, Webhook, Knowledge }

public sealed class ShellEgress(IOptions<EgressOptions> options)
{
    /// <summary>For tests and tools without configuration: any host, private networks allowed, link-local blocked.</summary>
    public static readonly ShellEgress Permissive = new(Options.Create(new EgressOptions()));

    public EgressPolicy Policy(EgressPurpose purpose)
    {
        var o = options.Value;
        var rule = purpose switch
        {
            EgressPurpose.Mcp => o.Mcp,
            EgressPurpose.Identity => o.Identity,
            EgressPurpose.Webhook => o.Webhook,
            _ => o.Knowledge,
        };
        return new EgressPolicy(
            rule.AllowedHosts ?? o.Default.AllowedHosts ?? ["*"],
            rule.AllowPrivateNetworks ?? o.Default.AllowPrivateNetworks ?? true,
            rule.AllowLinkLocal ?? o.Default.AllowLinkLocal ?? false);
    }

    public SocketsHttpHandler Handler(EgressPurpose purpose, bool followRedirects = false) => Policy(purpose).Handler(followRedirects);

    /// <summary>Checks a configured URL up front, so a profile or webhook pointing somewhere forbidden fails with a clear message.</summary>
    public void Check(EgressPurpose purpose, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new EgressDeniedException($"'{url}' is not an absolute URL");
        Policy(purpose).CheckUri(uri);
    }
}
