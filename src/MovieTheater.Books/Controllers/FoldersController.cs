using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Access;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;
using MovieTheater.Books.Projections;

namespace MovieTheater.Books.Controllers
{
    public sealed record FolderNode(int Id, string? Name, string? Path, int Depth, int? ParentId,
        int DirectChildCount, int DescendantItemCount, bool HasIcon, string? IconUrl);

    /// <summary>
    /// The Directory drill: the physical folder tree as it exists on the library share.
    ///
    /// <para><b>This view is the ONE place a shadow duplicate stays visible.</b> Everywhere else a deduplicated
    /// file is hidden (<c>IsExcluded</c>); here the file genuinely lives in that folder, so hiding it would make
    /// the drill disagree with the disk — the client dims it instead. That is the whole difference between
    /// <see cref="ItemAccess.ExcludeHidden"/> and <see cref="ItemAccess.ExcludeHiddenForDirectory"/>.</para>
    ///
    /// <para>Folder rows carry no per-folder ACL in v2 — the old per-folder authorized-users list is gone. What
    /// a caller may see is decided entirely by the maturity gate on the ITEMS, so a folder listing is public
    /// within the vertical while its contents are not.</para>
    /// </summary>
    [ApiController]
    [Route("")]
    public sealed class FoldersController : ControllerBase
    {
        private readonly BooksDb db;
        private readonly BooksOptions options;

        public FoldersController(BooksDb db, BooksOptions options)
        {
            this.db = db;
            this.options = options;
        }

        /// <summary>
        /// GET /library/{kind}/folders — the library roots and the top folders under them (the "collections").
        ///
        /// <para><c>?parentId=</c> drills one level; without it the answer is the depth-0 roots. Counts come from
        /// the folder row's own aggregates, which a scan maintains — counting a subtree per request would walk
        /// the tree on every keystroke of a drill.</para>
        ///
        /// <para>Sorting puts underscore-prefixed folders LAST rather than first, where an ordinal sort would put
        /// them: a leading underscore is the share's own convention for a staging or holding folder, and those
        /// belong at the bottom of a browse list, not above "A".</para>
        /// </summary>
        [HttpGet("library/{kind}/folders")]
        public async Task<IActionResult> GetFolders(string kind, [FromQuery] int? parentId = null,
            [FromQuery] string? countMode = null, CancellationToken ct = default)
        {
            var itemKind = CatalogController.ParseKind(kind);
            var query = db.Folders.AsNoTracking().Where(f => f.Kind == itemKind);
            query = parentId is int pid ? query.Where(f => f.ParentId == pid) : query.Where(f => f.ParentId == null);

            var rows = await query.Select(f => new
            {
                f.Id, f.Name, f.Path, f.Depth, f.ParentId, f.NormalizedName,
                f.DirectChildCount, f.DescendantItemCount, f.HasIcon,
            }).ToListAsync(ct);

            var useSubtreeCount = string.Equals(countMode, "subtreeItems", StringComparison.OrdinalIgnoreCase);
            var token = MediaToken();

            var folders = rows
                .OrderBy(f => SortKey(f.NormalizedName ?? f.Name), StringComparer.Ordinal)
                .Select(f => new FolderNode(f.Id, f.Name, f.Path, f.Depth, f.ParentId,
                    useSubtreeCount ? f.DescendantItemCount : f.DirectChildCount,
                    f.DescendantItemCount, f.HasIcon, IconUrl(token, f.Id, f.HasIcon)))
                .ToList();

            return Ok(folders);
        }

        /// <summary>
        /// GET /folders/{id} — one folder: its child folders and the items physically inside it.
        ///
        /// <para>The items come from <see cref="ItemAccess.DirectoryItems"/>, so shadow duplicates are present
        /// and the maturity ceiling still applies. <c>skip</c>/<c>top</c> page the items; the child folders are
        /// returned whole, because a folder with thousands of subfolders is not a shape this library has.</para>
        /// </summary>
        [HttpGet("folders/{id:int}")]
        public async Task<IActionResult> GetFolder(int id, [FromQuery] string? kind = null, [FromQuery] int skip = 0,
            [FromQuery] int top = 120, [FromQuery] string? orderby = null, CancellationToken ct = default)
        {
            skip = Math.Max(0, skip);
            top = Math.Clamp(top, 1, 500);

            var folder = await db.Folders.AsNoTracking().Where(f => f.Id == id)
                .Select(f => new { f.Id, f.Name, f.Path, f.Depth, f.ParentId, f.Kind, f.HasIcon, f.DescendantItemCount })
                .FirstOrDefaultAsync(ct);
            if (folder == null) return NotFound();

            // The folder's OWN kind wins over the query string: a caller cannot re-label a comics folder as books
            // to slip past a kind-scoped gate.
            var itemKind = folder.Kind;

            var childRows = await db.Folders.AsNoTracking().Where(f => f.ParentId == id)
                .Select(f => new { f.Id, f.Name, f.Path, f.Depth, f.ParentId, f.NormalizedName,
                    f.DirectChildCount, f.DescendantItemCount, f.HasIcon })
                .ToListAsync(ct);

            var token = MediaToken();
            var children = childRows
                .OrderBy(f => SortKey(f.NormalizedName ?? f.Name), StringComparer.Ordinal)
                .Select(f => new FolderNode(f.Id, f.Name, f.Path, f.Depth, f.ParentId, f.DirectChildCount,
                    f.DescendantItemCount, f.HasIcon, IconUrl(token, f.Id, f.HasIcon)))
                .ToList();

            var itemQuery = ItemAccess.DirectoryItems(db, User, itemKind, id);
            var total = await itemQuery.CountAsync(ct);
            var items = await Sort(itemQuery.Select(ItemSummary.Project), orderby).Skip(skip).Take(top).ToListAsync(ct);

            return Ok(new
            {
                folder = new FolderNode(folder.Id, folder.Name, folder.Path, folder.Depth, folder.ParentId,
                    childRows.Count, folder.DescendantItemCount, folder.HasIcon, IconUrl(token, folder.Id, folder.HasIcon)),
                kind = itemKind == ItemKind.Book ? "book" : "comic",
                children,
                totalItems = total,
                skip,
                top,
                items,
            });
        }

        /// <summary>
        /// GET /folders/{id}/parent — the folder one level up, for a breadcrumb. 200 with a null parent at a
        /// root, because "you are at the top" is an answer, not an error.
        /// </summary>
        [HttpGet("folders/{id:int}/parent")]
        public async Task<IActionResult> GetParent(int id, CancellationToken ct = default)
        {
            var folder = await db.Folders.AsNoTracking().Where(f => f.Id == id)
                .Select(f => new { f.Id, f.ParentId }).FirstOrDefaultAsync(ct);
            if (folder == null) return NotFound();
            if (folder.ParentId is not int parentId) return Ok(new { parentId = (int?)null, parent = (FolderNode?)null });

            var parent = await db.Folders.AsNoTracking().Where(f => f.Id == parentId)
                .Select(f => new { f.Id, f.Name, f.Path, f.Depth, f.ParentId, f.DirectChildCount, f.DescendantItemCount, f.HasIcon })
                .FirstOrDefaultAsync(ct);
            if (parent == null) return Ok(new { parentId = (int?)parentId, parent = (FolderNode?)null });

            var token = MediaToken();
            return Ok(new
            {
                parentId = (int?)parent.Id,
                parent = new FolderNode(parent.Id, parent.Name, parent.Path, parent.Depth, parent.ParentId,
                    parent.DirectChildCount, parent.DescendantItemCount, parent.HasIcon, IconUrl(token, parent.Id, parent.HasIcon)),
            });
        }

        /// <summary>
        /// GET /folders/{id}/icon-info — whether a folder has a hand-set icon and where its bytes live.
        ///
        /// <para>The BYTES are on the media plane; UPLOADING one is an admin action and belongs to slice 5. This
        /// endpoint exists so a client can decide between the icon and a fallback cover without a failed image
        /// request per folder.</para>
        /// </summary>
        [HttpGet("folders/{id:int}/icon-info")]
        public async Task<IActionResult> GetIconInfo(int id, CancellationToken ct = default)
        {
            var folder = await db.Folders.AsNoTracking().Where(f => f.Id == id)
                .Select(f => new { f.Id, f.HasIcon }).FirstOrDefaultAsync(ct);
            if (folder == null) return NotFound();

            // HasIcon is the catalog's claim; the file is the truth. They disagree after a cache wipe, and the
            // client should believe the file.
            var present = folder.HasIcon
                && options.CacheDir != null
                && BooksMediaRoutes.ResolveFolderIcon(options.CacheDir, id.ToString()) is { } p
                && System.IO.File.Exists(p);

            return Ok(new { id, hasIcon = present, iconUrl = IconUrl(MediaToken(), id, present) });
        }

        // ── shared ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Underscore-prefixed names sort LAST. See <see cref="GetFolders"/>.</summary>
        private static string SortKey(string? name)
        {
            var value = name ?? "";
            return value.Length > 0 && value[0] == '_' ? "￿" + value : value;
        }

        private string? IconUrl(string? token, int folderId, bool hasIcon) =>
            hasIcon && token != null && options.PublicBaseUrl != null
                ? BooksMediaRoutes.FolderIconUrl(options.PublicBaseUrl, token, folderId)
                : null;

        private string? MediaToken()
        {
            if (string.IsNullOrEmpty(options.MediaTokenSecret) || string.IsNullOrEmpty(options.PublicBaseUrl)) return null;
            if (BooksIdentity.UserId(User) is not int userId) return null;
            return BooksMediaToken.MintNow(options.MediaTokenSecret, userId,
                BooksIdentity.CeilingFor(User), BooksIdentity.IsAdmin(User), out _);
        }

        /// <summary>The drill's sorts. Every one ends with the item id, so a page boundary never drops a row.</summary>
        private static IQueryable<ItemSummary> Sort(IQueryable<ItemSummary> q, string? orderby) => orderby switch
        {
            "newest" => q.OrderByDescending(s => s.Year).ThenByDescending(s => s.IndexedAt).ThenBy(s => s.Id),
            "oldest" => q.OrderBy(s => s.Year).ThenBy(s => s.IndexedAt).ThenBy(s => s.Id),
            "rating" => q.OrderByDescending(s => s.Rating).ThenBy(s => s.Id),
            "series" => q.OrderBy(s => s.Series).ThenBy(s => s.Year).ThenBy(s => s.Id),
            // A file explorer sorts by NAME by default — that is what makes it match the folder on disk.
            _ => q.OrderBy(s => s.FileName).ThenBy(s => s.Id),
        };
    }
}
