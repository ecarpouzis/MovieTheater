using System.Security.Claims;
using MovieTheater.Books.Access;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;

namespace MovieTheater.Books.Media
{
    /// <summary>
    /// The bridge between a media TOKEN and the same authorization every JSON endpoint runs.
    ///
    /// <para>A token carries the identity facts the header established (<c>userId</c>, ceiling, admin) but no
    /// principal, and <see cref="ItemAccess"/> speaks principals. Rebuilding one here — with
    /// <see cref="BooksIdentity.Principal"/>, the same constructor the cache warmer uses — is what lets the byte
    /// routes reuse <see cref="ItemAccess.GetAuthorizedItemAsync"/> verbatim instead of growing a second,
    /// drifting copy of the maturity rules. There is ONE authorization in this vertical, and this is how the
    /// media plane reaches it.</para>
    ///
    /// <para><b>Thumbnails deliberately do not come through here.</b> They are the zero-database fast path: a
    /// valid token and a file name, no query at all, because a leaked id reveals at most a cover the caller was
    /// already shown in a list. Pages, EPUB resources and downloads are the actual content and always pay the
    /// one indexed read.</para>
    /// </summary>
    public sealed class MediaAccess
    {
        private readonly BooksOptions options;
        public MediaAccess(BooksOptions options) => this.options = options;

        /// <summary>True when this host can mint and validate media tokens at all.</summary>
        public bool Configured => !string.IsNullOrEmpty(options.MediaTokenSecret);

        /// <summary>Open a token. False ⇒ the caller answers 403 (a bad capability, not a missing thing).</summary>
        public bool TryOpen(string token, out BooksMediaToken.Payload? payload)
        {
            payload = null;
            return Configured && BooksMediaToken.TryValidate(options.MediaTokenSecret!, token, out payload);
        }

        /// <summary>The principal a token stands for — the same shape the identity header would have produced.</summary>
        public static ClaimsPrincipal PrincipalFor(BooksMediaToken.Payload payload) =>
            BooksIdentity.Principal(payload.UserId, "", payload.IsAdmin, payload.MaturityCeiling);

        /// <summary>
        /// Open the token AND authorize the item behind it, in one call. Null item ⇒ 404 (see
        /// <see cref="ItemAccess.GetAuthorizedItemAsync"/> on why never 403); <c>authorized == false</c> ⇒ the
        /// token itself was bad ⇒ 403.
        /// </summary>
        public async Task<(bool TokenValid, Item? Item)> ResolveAsync(
            BooksDb db, string token, int itemId, CancellationToken ct = default)
        {
            if (!TryOpen(token, out var payload) || payload == null) return (false, null);
            var user = PrincipalFor(payload);
            var item = await ItemAccess.GetAuthorizedItemAsync(db, user, itemId, allowExcluded: true, ct);
            return (true, item);
        }
    }
}
