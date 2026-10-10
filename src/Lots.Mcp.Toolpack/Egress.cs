using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Lots.Mcp.Toolpack;

/// <summary>
/// Egress control (#86): outbound requests go only to allowlisted hosts, and never to loopback, private, link-local (cloud metadata),
/// carrier-grade NAT or multicast addresses unless explicitly allowed. The address check runs on the address actually connected to,
/// so a host that resolves to a public address for the check and a private one for the request (DNS rebinding) is still blocked.
/// </summary>
public sealed class EgressPolicy(IReadOnlyList<string> allowedHosts, bool allowPrivateNetworks = false)
{
    private readonly Regex[] _hosts = allowedHosts.Select(h => new Regex("^" + Regex.Escape(h.Trim().ToLowerInvariant()).Replace(@"\*", "[a-z0-9.-]*") + "$")).ToArray();

    public bool HostAllowed(string host) => _hosts.Any(r => r.IsMatch(host.ToLowerInvariant()));

    public void CheckUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) throw new EgressDeniedException($"only http(s) is allowed, not {uri.Scheme}");
        if (!uri.IsDefaultPort && uri.Port is not (80 or 443 or 8080 or 8443)) throw new EgressDeniedException($"port {uri.Port} is not allowed");
        if (!HostAllowed(uri.IdnHost)) throw new EgressDeniedException($"{uri.Host} is not on the allowlist");
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal)) CheckAddress(literal);
    }

    public void CheckAddress(IPAddress address)
    {
        if (allowPrivateNetworks) return;
        if (IsInternal(address)) throw new EgressDeniedException($"{address} is an internal address");
    }

    public static bool IsInternal(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] switch
        {
            10 or 127 or 0 => true,
            169 when b[1] == 254 => true,                        // link-local, cloud metadata (169.254.169.254)
            172 when b[1] is >= 16 and <= 31 => true,
            192 when b[1] == 168 => true,
            100 when b[1] is >= 64 and <= 127 => true,           // carrier-grade NAT
            >= 224 => true,                                      // multicast and reserved
            _ => false,
        };
    }

    /// <summary>An HttpClient handler that enforces the policy on every connection, including after redirects.</summary>
    public SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false, // redirects are followed by the caller, each hop checked again
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var target = addresses.FirstOrDefault() ?? throw new EgressDeniedException($"{context.DnsEndPoint.Host} does not resolve");
            foreach (var a in addresses) CheckAddress(a);
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

public sealed class EgressDeniedException(string message) : Exception("egress denied: " + message);
