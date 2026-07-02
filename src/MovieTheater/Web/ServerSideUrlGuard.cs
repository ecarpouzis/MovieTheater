using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MovieTheater.Web
{
    /// <summary>
    /// Guards server-side outbound fetches of caller-supplied URLs against SSRF. Any endpoint that
    /// does <c>httpClient.GetAsync(userSuppliedUrl)</c> must run the URL through <see cref="ValidateAsync"/>
    /// first, otherwise a caller can point us at internal services (localhost:8096 Jellyfin, the cloud
    /// metadata endpoint 169.254.169.254, in-cluster k8s service IPs) and use the server as a proxy /
    /// port scanner.
    /// </summary>
    public static class ServerSideUrlGuard
    {
        /// <summary>
        /// Returns (ok, error). When ok is false the caller must refuse the fetch. Enforces http/https
        /// only and rejects any host that resolves to a loopback / private / link-local / unique-local /
        /// multicast address (checked across every resolved address to blunt DNS-rebinding).
        /// </summary>
        public static async Task<(bool ok, string error)> ValidateAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return (false, "URL is required.");

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return (false, "URL is not a valid absolute URL.");

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return (false, "Only http and https URLs are allowed.");

            IPAddress[] addresses;
            try
            {
                // If the host is already a literal IP this just wraps it; otherwise it resolves DNS.
                addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost);
            }
            catch (Exception)
            {
                return (false, "Could not resolve host.");
            }

            if (addresses.Length == 0)
                return (false, "Could not resolve host.");

            foreach (var addr in addresses)
            {
                if (IsBlockedAddress(addr))
                    return (false, "URL resolves to a disallowed (private/internal) address.");
            }

            return (true, null);
        }

        private static bool IsBlockedAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
                return true;

            // Normalize IPv4-mapped IPv6 (e.g. ::ffff:169.254.169.254) to its v4 form.
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();
                // 0.0.0.0/8, 10/8, 127/8, 169.254/16 (link-local incl. cloud metadata),
                // 172.16/12, 192.168/16, 100.64/10 (CGNAT).
                if (b[0] == 0 || b[0] == 10 || b[0] == 127) return true;
                if (b[0] == 169 && b[1] == 254) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                    return true;
                var b = address.GetAddressBytes();
                // fc00::/7 unique-local.
                if ((b[0] & 0xFE) == 0xFC) return true;
                return false;
            }

            // Unknown address family — refuse rather than risk it.
            return true;
        }
    }
}
