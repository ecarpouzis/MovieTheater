using System.Security.Claims;

namespace MovieTheater.Books.Identity
{
    /// <summary>
    /// What the host knows about a caller once the identity header is opened — the claim names, and the one
    /// accessor every controller uses. Replaces the standalone site's 44 <c>User.Identity.Name</c> sites: the
    /// identity is the site's <b>UserId</b>; the username is display only.
    ///
    /// <para><see cref="CeilingFor"/> is the standalone site's <c>ComicMaturityGate.CeilingFor</c> with ONE
    /// deliberate change, stated here: a missing or unparseable ceiling claim is <b>0 (most restrictive)</b>,
    /// where the old site defaulted to 3. The header always carries the ceiling, so absence is a defect, and
    /// the site's posture everywhere is to fail closed. An admin is unrestricted, as before.</para>
    /// </summary>
    public static class BooksIdentity
    {
        public const string AuthenticationScheme = "BooksIdentity";
        public const string MaturityClaim = "books_maturity";
        public const string AdminRole = "admin";

        public static int? UserId(ClaimsPrincipal user) =>
            int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

        public static string? Username(ClaimsPrincipal user) => user.FindFirst(ClaimTypes.Name)?.Value;

        public static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(AdminRole);

        public static int CeilingFor(ClaimsPrincipal user)
        {
            if (IsAdmin(user)) return 3;
            return int.TryParse(user.FindFirst(MaturityClaim)?.Value, out var m) ? Math.Clamp(m, 0, 3) : 0;
        }

        public static ClaimsPrincipal Principal(int userId, string username, bool isAdmin, int maturityCeiling)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new(ClaimTypes.Name, username),
                new(MaturityClaim, Math.Clamp(maturityCeiling, 0, 3).ToString()),
                new("amr", "pwd"),
            };
            if (isAdmin) claims.Add(new Claim(ClaimTypes.Role, AdminRole));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationScheme));
        }
    }
}
