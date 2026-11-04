using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace BlueDrive.Api.Infrastructure;

/// <summary>
/// Which hops may set X-Forwarded-For/Proto/Host. The deployed chain is client → [outer proxy] → Caddy → nginx → api:
/// every internal hop lives on the Docker network (private ranges), and an outer TLS proxy is declared with
/// <c>TRUSTED_PROXIES</c> (same value Caddy uses). The middleware walks X-Forwarded-For from the right and stops at the
/// first address not listed here, so the audit log and the login rate limit see the real client instead of Caddy's
/// container address. Spoofing is not possible: Caddy drops X-Forwarded-* sent by clients it does not trust.
/// </summary>
public static class TrustedProxyNetworks
{
    private static readonly string[] PrivateRanges =
        ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7"];

    /// <param name="extra">Space/comma separated CIDRs or addresses; Caddy's <c>private_ranges</c> keyword is accepted.</param>
    public static IReadOnlyList<System.Net.IPNetwork> Parse(string? extra)
    {
        var tokens = (extra ?? "").Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<System.Net.IPNetwork>();
        foreach (var token in PrivateRanges.Concat(tokens))
        {
            if (token.Equals("private_ranges", StringComparison.OrdinalIgnoreCase)) continue;
            if (System.Net.IPNetwork.TryParse(token, out var network)) list.Add(network);
            else if (IPAddress.TryParse(token, out var ip)) list.Add(new System.Net.IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            else throw new InvalidOperationException($"TRUSTED_PROXIES không hợp lệ: \"{token}\" (cần dạng CIDR, ví dụ 172.16.0.0/12).");
        }
        return list.Distinct().ToList();
    }

    public static void Apply(ForwardedHeadersOptions o, string? extra)
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        // No fixed hop count: the chain is 2 hops locally and 3 behind an outer proxy; trust decides where to stop.
        o.ForwardLimit = null;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var network in Parse(extra)) o.KnownIPNetworks.Add(network);
    }
}
