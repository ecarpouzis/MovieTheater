using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieTheater.Books.Identity;
using MovieTheater.Core;

namespace MovieTheater.BooksHost.Web
{
    /// <summary>
    /// Opens the site's <see cref="BooksIdentityToken.HeaderName"/> on every request and turns it into the
    /// principal the host's controllers read through <see cref="BooksIdentity"/>. Missing or invalid → an
    /// authentication failure, which the fallback policy turns into a plain 401 (never a redirect: the only
    /// clients are the site's proxy and, later, the SPA through it). The 30 s grace covers two machines' clocks.
    /// </summary>
    public sealed class BooksIdentityAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly BooksHostConfiguration config;
        private readonly KnownIdentityRecorder recorder;

        public BooksIdentityAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
            BooksHostConfiguration config, KnownIdentityRecorder recorder) : base(options, logger, encoder)
        {
            this.config = config;
            this.recorder = recorder;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var secret = config.IdentityTokenSecret;
            if (string.IsNullOrEmpty(secret)) return AuthenticateResult.Fail("Books:IdentityTokenSecret is not configured");
            var header = Request.Headers[BooksIdentityToken.HeaderName].ToString();
            if (string.IsNullOrEmpty(header)) return AuthenticateResult.NoResult();
            if (!BooksIdentityToken.TryValidate(secret, header, out var payload) || payload == null)
                return AuthenticateResult.Fail("invalid identity header");

            await recorder.RecordAsync(Context.RequestServices, payload);
            var principal = BooksIdentity.Principal(payload.UserId, payload.Username, payload.IsAdmin, payload.MaturityCeiling);
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    }
}
