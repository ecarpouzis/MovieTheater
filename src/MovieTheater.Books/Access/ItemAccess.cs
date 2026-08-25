using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;

namespace MovieTheater.Books.Access
{
    /// <summary>
    /// The one place a request decides which items a caller may see. Two filters compose:
    /// the dedup EXCLUSION filter here and the <see cref="MaturityFilter"/> gate.
    ///
    /// <para><b>Exclusion.</b> An item marked <c>IsExcluded</c> is a duplicate the dedup pass shadowed. The file is
    /// never deleted and the row is never removed — it is simply hidden from every browse/group/search/facet
    /// surface. The ONE exception is the Directory drill, which mirrors the physical folder tree: the file genuinely
    /// lives in that folder, so a shadow duplicate (<c>IsExcluded &amp;&amp; KeepInDirectory</c>) stays visible there
    /// and the tile dims it. That is the whole difference between <see cref="ExcludeHidden"/> and
    /// <see cref="ExcludeHiddenForDirectory"/>.</para>
    ///
    /// <para>Written as <c>!i.IsExcluded</c> deliberately: the R4 replay proved that comparing the flag any other
    /// way (or leaving it in an index prefix) costs more than it saves — it is 0.4 % selective.</para>
    /// </summary>
    public static class ItemAccess
    {
        public static IQueryable<Item> ExcludeHidden(this IQueryable<Item> items, bool includeExcluded = false) =>
            includeExcluded ? items : items.Where(i => !i.IsExcluded);

        /// <summary>
        /// Directory (file-explorer) variant: keeps shadow duplicates visible because the drill mirrors the folder
        /// tree. Only truly-hidden items (<c>IsExcluded &amp;&amp; !KeepInDirectory</c>) are dropped.
        /// </summary>
        public static IQueryable<Item> ExcludeHiddenForDirectory(this IQueryable<Item> items) =>
            items.Where(i => !i.IsExcluded || i.KeepInDirectory);

        /// <summary>
        /// The base browse set for a caller: one kind, no shadow duplicates, gated by the caller's ceiling.
        /// Every list endpoint starts here so a restricted account can never see a title, a facet value or a count
        /// it is not allowed to see.
        /// </summary>
        public static IQueryable<Item> VisibleItems(BooksDb db, ClaimsPrincipal user, ItemKind kind) =>
            db.Items.AsNoTracking().Where(i => i.Kind == kind).ExcludeHidden().ApplyMaturity(db, BooksIdentity.CeilingFor(user));

        /// <summary>
        /// The Directory drill's set for a caller: the items physically inside one folder, shadow duplicates
        /// included (dimmed by the client), still gated by the ceiling.
        /// </summary>
        public static IQueryable<Item> DirectoryItems(BooksDb db, ClaimsPrincipal user, ItemKind kind, int folderId) =>
            db.Items.AsNoTracking().Where(i => i.Kind == kind && i.FolderId == folderId)
                .ExcludeHiddenForDirectory().ApplyMaturity(db, BooksIdentity.CeilingFor(user));

        /// <summary>
        /// <b>The one authorization for anything addressed by id</b> — every detail endpoint, every next/prev
        /// hop, and every byte the media plane serves goes through here. It is a single indexed read on the
        /// primary key with the exclusion and maturity predicates folded in, so authorizing costs the same as
        /// fetching.
        ///
        /// <para>Null means "not visible to this caller", and callers answer <b>404</b> — never 403. A 403 would
        /// tell a restricted account that an item it may not see EXISTS at that id, and the ids are sequential:
        /// a caller could enumerate the shape of the library it is gated out of. Absent and forbidden must be
        /// indistinguishable from outside.</para>
        ///
        /// <para><b>An excluded item is not readable by id either.</b> A shadow duplicate stays visible in the
        /// Directory drill (see <see cref="ExcludeHiddenForDirectory"/>) because that view mirrors the folder
        /// tree, so a by-id read of one is allowed ONLY when <c>allowExcluded</c> says the caller came from
        /// there — that is what makes clicking a dimmed tile work without opening a back door to every
        /// deduplicated file.</para>
        /// </summary>
        public static Task<Item?> GetAuthorizedItemAsync(
            BooksDb db, ClaimsPrincipal user, int id, bool allowExcluded = true, CancellationToken ct = default) =>
            db.Items.AsNoTracking()
                .Where(i => i.Id == id)
                .ExcludeHiddenForDirectoryOrHidden(allowExcluded)
                .ApplyMaturity(db, BooksIdentity.CeilingFor(user))
                .FirstOrDefaultAsync(ct);

        /// <summary>
        /// The by-id exclusion rule: shadow duplicates that are kept in the Directory drill remain reachable by
        /// id (the tile is clickable), everything else excluded is gone. With <paramref name="allowExcluded"/>
        /// false it is the strict browse rule.
        /// </summary>
        private static IQueryable<Item> ExcludeHiddenForDirectoryOrHidden(this IQueryable<Item> items, bool allowExcluded) =>
            allowExcluded ? items.ExcludeHiddenForDirectory() : items.ExcludeHidden();
    }
}
