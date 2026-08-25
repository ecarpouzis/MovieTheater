using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MovieTheater.Books.Access;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Db;
using MovieTheater.Books.Media;

namespace MovieTheater.Books.Controllers
{
    /// <summary>
    /// The EPUB reader's structure endpoints: the spine, the table of contents, and one chapter's HTML. The
    /// RESOURCE bytes an EPUB's HTML references (images, fonts, stylesheets) come from the media plane, because
    /// they are bytes and the whole point of that plane is that bytes never pass through the site pods.
    ///
    /// <para>Every route authorizes through <see cref="ItemAccess.GetAuthorizedItemAsync"/> and then refuses
    /// anything that is not actually an EPUB — a caller must not be able to aim the EPUB parser at an arbitrary
    /// library file.</para>
    ///
    /// <para>Each response carries an ETag built from the item id and the file's mtime alone, so a conditional
    /// revalidation is answered BEFORE the container is opened. Opening an EPUB parses the whole package; the
    /// reader asks for the spine, then the TOC, then a chapter at a time, and a 304 costs none of it.</para>
    /// </summary>
    [ApiController]
    [Route("epub")]
    public sealed class EpubController : ControllerBase
    {
        private readonly BooksDb db;
        private readonly BooksOptions options;
        private readonly EpubReaderService epub;

        public EpubController(BooksDb db, BooksOptions options, EpubReaderService epub)
        {
            this.db = db;
            this.options = options;
            this.epub = epub;
        }

        /// <summary>
        /// GET /epub/{id}/spine — the reading order, plus the two facts that decide how the client renders it:
        /// <c>fixedLayout</c> (a pre-paginated comic EPUB must not be re-paginated by CSS columns) and
        /// <c>direction</c> (<c>rtl</c> for manga).
        /// </summary>
        [HttpGet("{id:int}/spine")]
        public async Task<IActionResult> GetSpine(int id, CancellationToken ct = default)
        {
            var item = await Epub(id, ct);
            if (item == null) return NotFound();
            if (NotModified(id, "spine", item)) return StatusCode(StatusCodes.Status304NotModified);

            var info = await epub.GetSpineInfoAsync(item.Path);
            return Ok(new { id, count = info.Items.Count, fixedLayout = info.FixedLayout, direction = info.Direction, items = info.Items });
        }

        /// <summary>
        /// GET /epub/{id}/toc — the flattened table of contents, each entry carrying the SPINE INDEX of its
        /// target so a tap jumps straight there. An entry whose target is not in the reading order keeps
        /// <c>spineIndex = -1</c> and renders as a heading rather than being dropped.
        /// </summary>
        [HttpGet("{id:int}/toc")]
        public async Task<IActionResult> GetToc(int id, CancellationToken ct = default)
        {
            var item = await Epub(id, ct);
            if (item == null) return NotFound();
            if (NotModified(id, "toc", item)) return StatusCode(StatusCodes.Status304NotModified);

            var toc = await epub.GetTocAsync(item.Path);
            return Ok(new { id, count = toc.Count, entries = toc });
        }

        /// <summary>
        /// GET /epub/{id}/chapters/{spineIndex} — one spine document's HTML, served as HTML so the reader's
        /// iframe can load it directly. Out of range is 404, not an empty document: the reader treats an empty
        /// chapter as a blank page and would show one.
        /// </summary>
        [HttpGet("{id:int}/chapters/{spineIndex:int}")]
        public async Task<IActionResult> GetChapter(int id, int spineIndex, CancellationToken ct = default)
        {
            var item = await Epub(id, ct);
            if (item == null) return NotFound();
            if (NotModified(id, "ch" + spineIndex, item, TimeSpan.FromDays(1))) return StatusCode(StatusCodes.Status304NotModified);

            try
            {
                var html = await epub.GetChapterHtmlAsync(item.Path, spineIndex);
                return Content(html, "text/html; charset=utf-8");
            }
            catch (ArgumentOutOfRangeException) { return NotFound(); }
            catch (FileNotFoundException) { return NotFound(); }
            catch (DirectoryNotFoundException) { return NotFound(); }
        }

        /// <summary>
        /// GET /epub/{id}/chapters — the whole spine with each document's href and title, for a client that wants
        /// the chapter list without a second call for the TOC. It does NOT carry the HTML: a novel's full text in
        /// one payload is megabytes, and the reader loads a chapter at a time on purpose.
        /// </summary>
        [HttpGet("{id:int}/chapters")]
        public async Task<IActionResult> GetChapters(int id, CancellationToken ct = default)
        {
            var item = await Epub(id, ct);
            if (item == null) return NotFound();
            if (NotModified(id, "chs", item)) return StatusCode(StatusCodes.Status304NotModified);

            var info = await epub.GetSpineInfoAsync(item.Path);
            var toc = await epub.GetTocAsync(item.Path);
            // The TOC's label for a spine document beats the file name the spine gives it — "Chapter One" reads
            // better than "part0007". First TOC entry pointing at the document wins.
            var labels = new Dictionary<int, string>();
            foreach (var entry in toc)
                if (entry.SpineIndex >= 0) labels.TryAdd(entry.SpineIndex, entry.Label);

            var chapters = info.Items.Select(s => new
            {
                index = s.Index,
                href = s.Href,
                title = labels.GetValueOrDefault(s.Index) ?? s.Title,
                resourceUrl = ResourceUrl(id, s.Href),
            });
            return Ok(new { id, count = info.Items.Count, fixedLayout = info.FixedLayout, direction = info.Direction, chapters });
        }

        // ── shared ────────────────────────────────────────────────────────────────────────────────────────

        private async Task<Item?> Epub(int id, CancellationToken ct)
        {
            var item = await ItemAccess.GetAuthorizedItemAsync(db, User, id, allowExcluded: true, ct);
            if (item == null) return null;
            // Not an EPUB ⇒ 404, the same answer as "no such item": the caller learns nothing about what the id
            // actually is.
            return ".epub".Equals(item.Extension, StringComparison.OrdinalIgnoreCase) ? item : null;
        }

        private bool NotModified(int id, string what, Item item, TimeSpan? maxAge = null)
        {
            var etag = $"\"{id}_ep{what}_{item.FileModifiedAt?.Ticks ?? 0}\"";
            Response.Headers.CacheControl = $"private, max-age={(int)(maxAge ?? TimeSpan.FromHours(1)).TotalSeconds}";
            Response.Headers.ETag = etag;
            return Request.Headers.IfNoneMatch.ToString() == etag;
        }

        private string? ResourceUrl(int id, string href)
        {
            if (string.IsNullOrEmpty(options.MediaTokenSecret) || string.IsNullOrEmpty(options.PublicBaseUrl)) return null;
            if (Identity.BooksIdentity.UserId(User) is not int userId) return null;
            var token = BooksMediaToken.MintNow(options.MediaTokenSecret, userId,
                Identity.BooksIdentity.CeilingFor(User), Identity.BooksIdentity.IsAdmin(User), out _);
            return BooksMediaRoutes.EpubResourceUrl(options.PublicBaseUrl, token, id, href);
        }
    }
}
