using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Core;
using MovieTheater.Services;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MovieTheater.Books
{
    /// <summary>
    /// Stamps <see cref="BooksIdentityToken.HeaderName"/> onto every request the Books routes forward. Runs
    /// AFTER the route's authorization policy, so the principal here has already passed the gate; it reads
    /// the memoized <see cref="IBooksMembership"/> (no second query) for the maturity ceiling and decides
    /// <c>isAdmin</c> exactly as <c>/API/Me</c> does: on the config admin list AND password-verified.
    ///
    /// <para>The cookie never crosses the seam — the route's <c>RequestHeaderRemove Cookie</c> transform
    /// strips it — and a request that somehow reaches this transform without an identity is forwarded
    /// WITHOUT the header, which the host answers with a 401. Never a forged default identity.</para>
    /// </summary>
    public sealed class BooksIdentityTransform : ITransformProvider
    {
        private readonly MovieTheaterConfiguration config;

        public BooksIdentityTransform(MovieTheaterConfiguration config) => this.config = config;

        public void ValidateRoute(TransformRouteValidationContext context) { }
        public void ValidateCluster(TransformClusterValidationContext context) { }

        public void Apply(TransformBuilderContext context)
        {
            if (context.Route.RouteId != BooksRoutes.ApiRouteId && context.Route.RouteId != BooksRoutes.OpdsRouteId) return;
            var secret = config.BooksTokenSecret;
            if (string.IsNullOrEmpty(secret)) return;

            context.AddRequestTransform(async transform =>
            {
                var http = transform.HttpContext;
                var user = http.User;
                var idClaim = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var username = user?.FindFirst(ClaimTypes.Name)?.Value;
                if (!int.TryParse(idClaim, out var userId) || string.IsNullOrEmpty(username)) return;

                var grant = await http.RequestServices.GetRequiredService<IBooksMembership>().GetAsync(userId);
                var passwordVerified = user!.FindFirst("amr")?.Value == "pwd";
                var admins = http.RequestServices.GetRequiredService<IConfiguration>().GetSection("AdminUsernames").Get<string[]>() ?? Array.Empty<string>();
                var isAdmin = passwordVerified && admins.Any(a => string.Equals(a, username, StringComparison.OrdinalIgnoreCase));

                transform.ProxyRequest.Headers.Remove(BooksIdentityToken.HeaderName);
                transform.ProxyRequest.Headers.TryAddWithoutValidation(BooksIdentityToken.HeaderName,
                    BooksIdentityToken.MintNow(secret, userId, username, isAdmin, grant.MaturityCeiling));
            });
        }
    }
}
