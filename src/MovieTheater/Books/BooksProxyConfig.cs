using System;
using System.Collections.Generic;
using MovieTheater.Core;
using MovieTheater.Services;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace MovieTheater.Books
{
    /// <summary>
    /// The two Books routes and their cluster, built in code and loaded from memory beside the config-file
    /// catch-all. In code, not appsettings, for one reason: an unconfigured host (dev, or prod before the
    /// secret lands) must start exactly as today — a config-file cluster with an empty destination would
    /// fail Yarp's validation at startup and take the whole API down with it.
    /// </summary>
    public static class BooksProxyConfig
    {
        public static bool IsConfigured(MovieTheaterConfiguration config) =>
            !string.IsNullOrWhiteSpace(config.BooksHostBaseUrl) && !string.IsNullOrWhiteSpace(config.BooksTokenSecret);

        public static IReadOnlyList<RouteConfig> Routes() => new[]
        {
            new RouteConfig
            {
                RouteId = BooksRoutes.ApiRouteId,
                ClusterId = BooksRoutes.ClusterId,
                Order = 0, // ahead of the SPA catch-all (Order 1)
                AuthorizationPolicy = BooksAccessGate.PolicyName,
                Match = new RouteMatch { Path = BooksRoutes.ApiPrefix + "/{**rest}" },
                Transforms = new[]
                {
                    new Dictionary<string, string> { ["PathRemovePrefix"] = BooksRoutes.ApiPrefix },
                    new Dictionary<string, string> { ["RequestHeaderRemove"] = "Cookie" },
                },
            },
            new RouteConfig
            {
                RouteId = BooksRoutes.OpdsRouteId,
                ClusterId = BooksRoutes.ClusterId,
                Order = 0,
                AuthorizationPolicy = BooksAccessGate.BasicPolicyName,
                Match = new RouteMatch { Path = BooksRoutes.OpdsPrefix + "/{**rest}" },
                Transforms = new[]
                {
                    // the e-reader's Basic credential must not reach the host: the pod verified it and the host trusts the identity header
                    new Dictionary<string, string> { ["RequestHeaderRemove"] = "Authorization" },
                    new Dictionary<string, string> { ["RequestHeaderRemove"] = "Cookie" },
                },
            },
        };

        public static IReadOnlyList<ClusterConfig> Clusters(MovieTheaterConfiguration config) => new[]
        {
            new ClusterConfig
            {
                ClusterId = BooksRoutes.ClusterId,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["host"] = new DestinationConfig { Address = config.BooksHostBaseUrl!.TrimEnd('/') + "/" },
                },
                // Scrape event streams can be silent for minutes; Yarp's 100 s idle default would sever them.
                // One hour rather than infinite (Yarp validates the timeout); the ported SSE loops also send a
                // ": keepalive" comment every 20 s for the proxies in between.
                HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromHours(1) },
            },
        };
    }
}
