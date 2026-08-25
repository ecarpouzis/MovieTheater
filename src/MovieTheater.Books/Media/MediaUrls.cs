using System.Security.Claims;
using MovieTheater.Books.Identity;

namespace MovieTheater.Books.Media
{
    /// <summary>
    /// The media URLs one response hands out, minted ONCE for the caller.
    ///
    /// <para>Slice 2's <c>ItemsController</c> established the pattern: a response that carries covers mints a
    /// media token for the caller's own identity (never wider — the token carries the SAME ceiling and admin flag
    /// the identity header established) and builds every URL from it, so a card arrives ready to render in one
    /// round trip instead of after a <c>/media-token</c> hop. This type is that pattern named, because the rails,
    /// the kids shelves and the novels list all need it and none of them should re-derive it.</para>
    ///
    /// <para><b>Not configured is not an error.</b> A host without <c>Books:PublicBaseUrl</c> /
    /// <c>Books:MediaTokenSecret</c> (a test, a CLI verb) answers <see cref="Configured"/> false and every URL is
    /// null — the JSON is still correct, it simply has no pictures in it.</para>
    ///
    /// <para><b>A URL is minted whether or not the thumbnail file exists.</b> A rail of 80 cards would otherwise
    /// cost 80 file stats to decide, and a missing thumbnail answers 404 on the media plane, which is exactly what
    /// the client's fallback art is for. The batch manifest (<c>POST /thumbs/batch</c>) is the surface that reports
    /// existence, because a grid asks it for cache validators too.</para>
    /// </summary>
    public sealed record MediaUrls(string? Token, string? BaseUrl)
    {
        public bool Configured => Token != null && BaseUrl != null;

        public string? Thumb(long itemId) => Configured ? BooksMediaRoutes.ThumbUrl(BaseUrl!, Token!, itemId) : null;
        public string? Download(long itemId) => Configured ? BooksMediaRoutes.DownloadUrl(BaseUrl!, Token!, itemId) : null;
        public string? PagesTemplate(long itemId) => Configured ? BooksMediaRoutes.PageUrlTemplate(BaseUrl!, Token!, itemId) : null;

        /// <summary>
        /// The set for this caller: their own token when they sent one, otherwise a freshly minted one. Null
        /// everywhere when the host has no media configuration or the principal carries no user id.
        /// </summary>
        public static MediaUrls For(BooksOptions options, ClaimsPrincipal user, string? suppliedToken = null)
        {
            if (string.IsNullOrEmpty(options.PublicBaseUrl)) return new MediaUrls(null, null);
            if (!string.IsNullOrWhiteSpace(suppliedToken)) return new MediaUrls(suppliedToken, options.PublicBaseUrl);
            if (string.IsNullOrEmpty(options.MediaTokenSecret)) return new MediaUrls(null, null);
            if (BooksIdentity.UserId(user) is not int userId) return new MediaUrls(null, null);
            var token = BooksMediaToken.MintNow(options.MediaTokenSecret, userId,
                BooksIdentity.CeilingFor(user), BooksIdentity.IsAdmin(user), out _);
            return new MediaUrls(token, options.PublicBaseUrl);
        }
    }
}
