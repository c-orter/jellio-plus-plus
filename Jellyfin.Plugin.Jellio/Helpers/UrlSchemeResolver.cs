using System;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Jellio.Helpers;

/// <summary>
/// Resolves the public-facing scheme of an inbound HTTP request.
///
/// When Jellyfin sits behind a reverse proxy (Cloudflare Tunnel, nginx, Caddy, etc.),
/// the origin sees the proxy's scheme — almost always <c>http</c> — even though the
/// real client connected over <c>https</c>. Returning that origin scheme in
/// addon/stream URLs causes Stremio-compatible clients to fetch the manifest and
/// HLS endpoints over cleartext HTTP, which the proxy then either rejects with a
/// 404 (Cloudflare's default) or refuses to route. The result is the
/// <c>source error: none of the available extractors could read the stream [3003]</c>
/// error reported by ExoPlayer-based clients like Nuvio on Android TV.
///
/// This resolver honors the standard proxy headers so the original scheme is
/// preserved end-to-end.
/// </summary>
internal static class UrlSchemeResolver
{
    private static readonly Regex ForwardedProtoRegex = new(
        @"proto=(?<proto>[^;,\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the scheme the original client used, preferring
    /// <c>X-Forwarded-Proto</c> then the RFC 7239 <c>Forwarded</c> header, and
    /// finally falling back to the immediate connection's scheme.
    /// </summary>
    public static string Resolve(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var forwardedProto = request.Headers["X-Forwarded-Proto"].ToString();
        if (!string.IsNullOrWhiteSpace(forwardedProto))
        {
            // Multi-value header: take the first non-empty entry.
            foreach (var segment in forwardedProto.Split(','))
            {
                var trimmed = segment.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
        }

        var forwarded = request.Headers["Forwarded"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            // The Forwarded header can appear multiple times; search them all.
            foreach (var entry in forwarded.Split(','))
            {
                var match = ForwardedProtoRegex.Match(entry);
                if (match.Success)
                {
                    return match.Groups["proto"].Value.Trim('"');
                }
            }
        }

        return request.Scheme;
    }

    /// <summary>
    /// Returns <c>true</c> if the given host is a loopback or site-local address
    /// (IPv4 or IPv6) where a cleartext HTTP URL is appropriate for testing.
    /// </summary>
    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // Strip optional :port suffix and IPv6 brackets before matching.
        var stripped = host.Trim();
        if (stripped.StartsWith("[", StringComparison.Ordinal))
        {
            var closing = stripped.IndexOf(']');
            if (closing > 0)
            {
                stripped = stripped[1..closing];
            }
        }
        else
        {
            var colon = stripped.IndexOf(':');
            if (colon > 0)
            {
                stripped = stripped[..colon];
            }
        }

        if (stripped.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (System.Net.IPAddress.TryParse(stripped, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip))
            {
                return true;
            }

            // RFC 1918 private ranges and link-local are also treated as "local".
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4)
            {
                if (bytes[0] == 10) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                if (bytes[0] == 169 && bytes[1] == 254) return true;
            }
            else if (bytes.Length == 16)
            {
                if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true; // fe80::/10
                if (bytes[0] == 0xfc) return true; // fc00::/7 (unique local)
            }
        }

        return false;
    }
}
