using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Lots.Net;

/// <summary>
/// Egress control (#86), shared by the shell and the tool pack (linked source, one set of rules). Outbound requests go only to
/// allowlisted hosts, never to link-local addresses (cloud metadata at 169.254.169.254, fe80::/10), unspecified or multicast
/// addresses unless explicitly allowed, and to loopback, private and carrier-grade NAT ranges only when private networks are allowed.
/// Hosts and addresses are checked on every connection the handler opens, including those after a redirect, on the address actually
/// connected to: a host that resolves to a public address for a check and a private one for the request (DNS rebinding) is blocked.
/// </summary>
public sealed class EgressPolicy(IReadOnlyList<string> allowedHosts, bool allowPrivateNetworks = false, bool allowLinkLocal = false,
    IReadOnlyCollection<int>? allowedPorts = null)
{
    /// <summary>The tool pack's default: web ports only. The shell passes null (backends listen on any port).</summary>
    public static readonly IReadOnlyCollection<int> WebPorts = [80, 443, 8080, 8443];

    private readonly Regex[] _hosts = allowedHosts.Select(h => new Regex("^" + Regex.Escape(h.Trim().ToLowerInvariant()).Replace(@"\*", "[a-z0-9.-]*") + "$")).ToArray();
    private readonly IReadOnlyCollection<int>? _ports = allowedPorts;

    public bool HostAllowed(string host) => _hosts.Any(r => r.IsMatch(host.Trim('[', ']').ToLowerInvariant()));

    public void CheckUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) throw new EgressDeniedException($"only http(s) is allowed, not {uri.Scheme}");
        if (_ports is not null && !uri.IsDefaultPort && !_ports.Contains(uri.Port)) throw new EgressDeniedException($"port {uri.Port} is not allowed");
        CheckHost(uri.IdnHost);
    }

    public void CheckHost(string host)
    {
        if (!HostAllowed(host)) throw new EgressDeniedException($"{host} is not on the allowlist");
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) CheckAddress(literal);
    }

    public void CheckAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (!allowLinkLocal && IsLinkLocalOrReserved(address)) throw new EgressDeniedException($"{address} is a link-local or reserved address");
        if (!allowPrivateNetworks && IsPrivate(address)) throw new EgressDeniedException($"{address} is an internal address");
    }

    /// <summary>Any address that is not public: private, loopback, link-local, reserved.</summary>
    public static bool IsInternal(IPAddress a) => IsPrivate(a) || IsLinkLocalOrReserved(a);

    /// <summary>Loopback, RFC 1918, carrier-grade NAT, IPv6 unique-local and site-local.</summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] switch
        {
            10 or 127 => true,
            172 when b[1] is >= 16 and <= 31 => true,
            192 when b[1] == 168 => true,
            100 when b[1] is >= 64 and <= 127 => true,           // carrier-grade NAT
            _ => false,
        };
    }

    /// <summary>Link-local (incl. cloud metadata), unspecified ("this network"), multicast and reserved ranges.</summary>
    public static bool IsLinkLocalOrReserved(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6Multicast;
        var b = a.GetAddressBytes();
        return b[0] switch
        {
            0 => true,
            169 when b[1] == 254 => true,                        // link-local, cloud metadata (169.254.169.254)
            >= 224 => true,                                      // multicast and reserved
            _ => false,
        };
    }

    /// <summary>An HttpClient handler that enforces the policy on every connection it opens.</summary>
    public SocketsHttpHandler Handler(bool followRedirects = false, TimeSpan? connectTimeout = null) => new()
    {
        // Redirects are followed by the handler only when asked; every hop opens a new connection, which is checked again.
        AllowAutoRedirect = followRedirects,
        MaxAutomaticRedirections = 5,
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            CheckHost(context.DnsEndPoint.Host);
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
