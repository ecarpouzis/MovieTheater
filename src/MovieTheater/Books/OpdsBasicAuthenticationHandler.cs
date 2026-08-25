using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieTheater.Core;
using MovieTheater.Db;

namespace MovieTheater.Books
{
    /// <summary>
    /// HTTP Basic for OPDS e-readers, at the site — they cannot carry the site cookie, and the Books host must
    /// never see a password. The credential is the site username + the site (streaming) password, verified
    /// with the same <see cref="PasswordHasher{TUser}"/> the login uses. Honoured ONLY by the
    /// <see cref="BooksAccessGate.BasicPolicyName"/> policy on the <c>/opds</c> route; nothing else names
    /// the scheme, so a Basic header on any other path is inert.
    ///
    /// <para>Two lessons carried over from the standalone site: the <c>try</c> wraps ONLY the base64 decode
    /// (a downstream exception must never turn into a 401 with a challenge — that is how a feed's 500 once
    /// spent weeks looking like a login problem), and a SHA-256(credential) → identity memo (10 min) keeps
    /// OPDS-PSE page streams from running the hasher on every request.</para>
    /// </summary>
    public sealed class OpdsBasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "OpdsBasic";
        public const string Realm = "Books";
        public static readonly TimeSpan MemoTtl = TimeSpan.FromMinutes(10);

        private readonly MovieDb movieDb;
        private readonly IMemoryCache memoryCache;
        private readonly IPasswordVerifier passwordVerifier;

        public OpdsBasicAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
            MovieDb movieDb, IMemoryCache memoryCache, IPasswordVerifier passwordVerifier)
            : base(options, logger, encoder)
        {
            this.movieDb = movieDb;
            this.memoryCache = memoryCache;
            this.passwordVerifier = passwordVerifier;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Path.StartsWithSegments(BooksRoutes.OpdsPrefix)) return AuthenticateResult.NoResult();
            if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header)
                || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(header.Parameter))
                return AuthenticateResult.NoResult();

            var memoKey = "opds-basic:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(header.Parameter)));
            if (memoryCache.TryGetValue(memoKey, out Identity? memo) && memo != null)
                return AuthenticateResult.Success(new AuthenticationTicket(Principal(memo), Scheme.Name));

            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
            }
            catch (FormatException)
            {
                return AuthenticateResult.Fail("malformed Basic credential");
            }
            var colon = decoded.IndexOf(':');
            if (colon <= 0) return AuthenticateResult.Fail("malformed Basic credential");
            var username = decoded[..colon];
            var password = decoded[(colon + 1)..];

            var user = await movieDb.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null || user.PasswordHash == null || !passwordVerifier.Verify(user, password))
                return AuthenticateResult.Fail("invalid credentials");

            var identity = new Identity(user.UserID, user.Username);
            memoryCache.Set(memoKey, identity, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = MemoTtl, Size = 1 });
            return AuthenticateResult.Success(new AuthenticationTicket(Principal(identity), Scheme.Name));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\"";
            return Task.CompletedTask;
        }

        private ClaimsPrincipal Principal(Identity identity) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, identity.UserId.ToString()),
                new Claim(ClaimTypes.Name, identity.Username),
                new Claim("amr", "pwd"),
            }, Scheme.Name));

        public sealed record Identity(int UserId, string Username);
    }

    /// <summary>The password check behind the Basic scheme — a seam so the handler is provable without a real hash in a test.</summary>
    public interface IPasswordVerifier
    {
        bool Verify(User user, string password);
    }

    public sealed class IdentityPasswordVerifier : IPasswordVerifier
    {
        private static readonly PasswordHasher<User> hasher = new();
        public bool Verify(User user, string password) =>
            user.PasswordHash != null && hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
    }
}
