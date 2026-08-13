using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The <c>photos-shelf</c> engine (docs/photos-plan.md §2.12): files a whole subtree onto a shelf by
    /// ROOT-RELATIVE PATH PREFIX, optionally gathering it into an album and optionally hiding it.
    ///
    /// <para>This exists because §1's art/meme piles are identified by WHERE THEY ARE, not by anything
    /// on the row. No heuristic finds them — they are ordinary JPEGs of ordinary sizes — so the owner
    /// names the folder and the pass files it. The selection bar does the same edit one screenful at a
    /// time; this does it for 1,608 files without anybody scrolling.</para>
    ///
    /// <para>Same bulk-job contract as every other pass here: bounded rows per batch,
    /// <c>{processed, remaining, nextCursor}</c> printed after each, resume from the cursor, and a
    /// no-progress safety break. <b>Cursor ordering IS the query ordering</b> — <c>Id</c> ascending in
    /// both — the rule a previous cursor bug on this repo was written in blood.</para>
    ///
    /// <para><b>Idempotent by construction.</b> Every write is a comparison first: a row already on the
    /// shelf is counted as <c>already</c> and not touched, an asset already in the album makes no second
    /// entry, and an album found by title is reused rather than duplicated. Re-running a completed rule
    /// changes nothing and says so, which is the property that makes it safe to drive from a script that
    /// may have died halfway.</para>
    ///
    /// <para>Reads NO files (§6). Every decision is made from the <see cref="PhotoAsset.Path"/> column,
    /// so the pass never touches the collection root and runs anywhere the database is reachable.</para>
    /// </summary>
    public sealed class PhotoShelfPass
    {
        private readonly Func<MovieDb> dbFactory;
        private readonly Options options;
        private readonly int batchSize;
        private readonly Action<string> log;

        public sealed class Options
        {
            /// <summary>Root-relative, forward-slash prefix that selects the subtree. Normalized to end
            /// in '/' unless it is empty (which would be the whole collection — allowed, because
            /// refusing it would just mean the caller types the root's name).</summary>
            public string PathPrefix = "";

            /// <summary>Subtrees carved back OUT of <see cref="PathPrefix"/>. This is what makes "the
            /// loose files at the top of this folder, but not the deep trees under it" expressible in
            /// one rule instead of one rule per file.</summary>
            public List<string> ExcludePrefixes = new List<string>();

            public PhotoShelf Shelf = PhotoShelf.Archive;

            /// <summary>Create-or-find an album by title and add every matched asset to it. The album
            /// takes <see cref="Shelf"/> too — a Gallery collection on the family album index would be
            /// the one arrangement neither section wants.</summary>
            public string? AlbumTitle;

            /// <summary>Makes the album an ARTIST COLLECTION (§2.12). Composes with
            /// <see cref="AlbumTitle"/> and means nothing without it — there is nowhere else to put it.</summary>
            public string? ArtistName;

            /// <summary>Also sets <see cref="PhotoAsset.Hidden"/> on the matches. Separate from the
            /// shelf on purpose: the shelf says "not the family record", hiding says "not for
            /// non-admins", and the corner of the collection that needs both needs them as two
            /// statements rather than one conflated flag.</summary>
            public bool Hide;

            /// <summary>Reports without writing. Every counter is still computed, so a dry run tells you
            /// the real numbers rather than a plan for them.</summary>
            public bool DryRun;
        }

        public PhotoShelfPass(Func<MovieDb> dbFactory, Options options, int batchSize, Action<string> log)
        {
            this.dbFactory = dbFactory;
            this.options = options;
            this.batchSize = Math.Max(1, batchSize);
            this.log = log;
        }

        /// <summary>Normalizes a root-relative prefix the way the folder view does: forward slashes, no
        /// leading slash, no <c>.</c>/<c>..</c> segments, one trailing slash. A string prefix over a
        /// column — nothing here touches a filesystem.</summary>
        public static string NormalizePrefix(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var parts = path!.Replace('\\', '/').Split('/')
                .Where(s => s.Length > 0 && s != "." && s != "..")
                .ToList();
            return parts.Count == 0 ? "" : string.Join("/", parts) + "/";
        }

        /// <summary>
        /// Runs up to <paramref name="maxBatches"/> bounded batches (0 drains), printing the per-chunk
        /// line the standing rule requires and stopping deterministically.
        /// </summary>
        public async Task<PhotoIngestBatchResult> RunAsync(string? cursor, int maxBatches)
        {
            var total = new PhotoIngestBatchResult { NextCursor = cursor ?? "0" };

            // The album is resolved ONCE, before the loop: creating it per batch would either race on
            // the unique slug or mint "misc-art-2" on the second chunk of the same run.
            var album = await ResolveAlbumAsync(total);

            var batches = 0;
            while (maxBatches <= 0 || batches < maxBatches)
            {
                var result = await BatchAsync(ParseCursor(batches == 0 ? cursor : total.NextCursor), album);
                batches++;
                total.Processed += result.Processed;
                total.Remaining = result.Remaining;
                total.NextCursor = result.NextCursor;
                foreach (var kv in result.Counts) total.Add(kv.Key, kv.Value);

                var counts = result.CountsText();
                log($"{{ processed: {result.Processed}, remaining: {result.Remaining}, nextCursor: \"{result.NextCursor}\" }}"
                    + (counts.Length > 0 ? $"  [{counts}]" : ""));

                if (result.Remaining <= 0) break;
                if (result.Processed <= 0)
                {
                    log("No progress in a batch while rows remained — stopping.");
                    break;
                }
            }
            return total;
        }

        /// <summary>
        /// Create-or-find the album by TITLE, case-insensitively.
        ///
        /// <para>Title rather than slug is the key because the title is what the operator types and what
        /// they will type again on the re-run; the slug is minted from it server-side (§2.9) and is an
        /// output, not an input. Shelf and artist are re-asserted on an album that already exists, so
        /// adding <c>--artist</c> to a rule that already ran is a correction rather than a duplicate.</para>
        /// </summary>
        private async Task<AlbumTarget> ResolveAlbumAsync(PhotoIngestBatchResult total)
        {
            var title = options.AlbumTitle?.Trim();
            if (string.IsNullOrWhiteSpace(title)) return AlbumTarget.None;
            if (title!.Length > 300) title = title.Substring(0, 300);
            var artist = string.IsNullOrWhiteSpace(options.ArtistName) ? null : options.ArtistName!.Trim();
            if (artist != null && artist.Length > 256) artist = artist.Substring(0, 256);

            using var db = dbFactory();
            var album = await db.PhotoAlbums.FirstOrDefaultAsync(a => a.Title.ToLower() == title.ToLower());
            if (album != null)
            {
                var changed = false;
                if (album.Shelf != options.Shelf) { album.Shelf = options.Shelf; changed = true; }
                if (artist != null && album.ArtistName != artist) { album.ArtistName = artist; changed = true; }
                if (changed && !options.DryRun) await db.SaveChangesAsync();
                total.Add(changed ? "album-updated" : "album-found");
                log($"album: \"{album.Title}\" (#{album.Id}, /{album.Slug})"
                    + (album.ArtistName != null ? $", artist \"{album.ArtistName}\"" : "")
                    + $", shelf {album.Shelf}");
                return AlbumTarget.Existing(album.Id);
            }

            if (options.DryRun)
            {
                total.Add("album-would-create");
                log($"album: would create \"{title}\"" + (artist != null ? $" (artist \"{artist}\")" : "")
                    + $" on the {options.Shelf} shelf");
                // There is no id to add entries against, but the entry COUNT is knowable and is the
                // number the operator is actually deciding on. A dry run that reported "0 entries"
                // because it had not created the album yet would be advertising a no-op.
                return AlbumTarget.Pending;
            }

            var existingSlugs = await db.PhotoAlbums.Select(a => a.Slug).ToListAsync();
            album = new PhotoAlbum
            {
                Title = title,
                Slug = PhotoAlbumSlug.Unique(title, existingSlugs),
                Shelf = options.Shelf,
                ArtistName = artist,
                CreatedUtc = DateTime.UtcNow,
                SortOrder = 0,
            };
            db.PhotoAlbums.Add(album);
            await db.SaveChangesAsync();
            total.Add("album-created");
            log($"album: created \"{album.Title}\" (#{album.Id}, /{album.Slug})"
                + (artist != null ? $", artist \"{artist}\"" : "") + $", shelf {album.Shelf}");
            return AlbumTarget.Existing(album.Id);
        }

        /// <summary>Where this run's album entries go: nowhere (no <c>--album</c>), a real album, or —
        /// in a dry run whose album does not exist yet — an album that WOULD be created, whose entry
        /// count is still worth reporting.</summary>
        public readonly struct AlbumTarget
        {
            public readonly int? Id;
            public readonly bool WouldCreate;

            private AlbumTarget(int? id, bool wouldCreate) { Id = id; WouldCreate = wouldCreate; }

            public static readonly AlbumTarget None = new AlbumTarget(null, false);
            public static readonly AlbumTarget Pending = new AlbumTarget(null, true);
            public static AlbumTarget Existing(int id) => new AlbumTarget(id, false);
        }

        /// <summary>One bounded batch: the next <c>batchSize</c> matched rows after the cursor.</summary>
        public async Task<PhotoIngestBatchResult> BatchAsync(int cursorId, AlbumTarget album)
        {
            var result = new PhotoIngestBatchResult { NextCursor = cursorId.ToString(CultureInfo.InvariantCulture) };
            using var db = dbFactory();

            var rows = await Matched(db).Where(a => a.Id > cursorId)
                .OrderBy(a => a.Id).Take(batchSize).ToListAsync();
            if (rows.Count == 0)
            {
                result.Remaining = 0;
                return result;
            }

            result.Processed = rows.Count;
            result.Add("matched", rows.Count);
            var lastId = rows[rows.Count - 1].Id;

            // §2.12's coherence rule, applied here for the same reason it is applied to the selection
            // bar: a settled duplicate group is one photograph, and a prefix can cut straight through
            // one (the same meme saved twice, in two folders). The extras are counted separately so a
            // rule that reaches outside its own prefix says so out loud.
            var ids = rows.Select(a => a.Id).ToList();
            var expanded = await PhotoDupeMasters.GroupCoherentIdsAsync(db, ids);
            var extras = expanded.Count - ids.Count;
            if (extras > 0) result.Add("group-coherent", extras);

            var targets = extras > 0
                ? await db.PhotoAssets.Where(a => expanded.Contains(a.Id)).ToListAsync()
                : rows;

            var writes = 0;
            foreach (var row in targets)
            {
                if (row.Shelf == options.Shelf) result.Add("already");
                else { row.Shelf = options.Shelf; result.Add("shelved"); writes++; }

                // --hide is one-directional on purpose: the flag exists to cover the corner of the
                // collection that must not reach a non-admin, and a pass that could silently UNHIDE
                // would be able to undo a human's curation decision from a command line.
                if (options.Hide && !row.Hidden) { row.Hidden = true; result.Add("hidden"); writes++; }
            }
            if (writes > 0 && !options.DryRun) await db.SaveChangesAsync();

            if (album.Id != null)
                result.Add("album-entries-added", await AddToAlbumAsync(db, album.Id.Value, targets.Select(a => a.Id).ToList()));
            else if (album.WouldCreate)
                // A brand-new album holds nothing, so every distinct master in the batch is an entry
                // it would gain.
                result.Add("album-entries-added",
                    (await PhotoDupeMasters.MasterMapAsync(db, targets.Select(a => a.Id).ToList()))
                        .Values.Distinct().Count());

            result.NextCursor = lastId.ToString(CultureInfo.InvariantCulture);
            // The independent count the standing rule asks for: how many MATCHED rows are still ahead of
            // the cursor, asked of the database rather than inferred from the batch size.
            result.Remaining = await Matched(db).CountAsync(a => a.Id > lastId);
            return result;
        }

        /// <summary>
        /// Album membership for a batch. Every id goes through the master redirect first (§2.6), exactly
        /// as the controller's own add does — adding a non-master adds the copy the album will actually
        /// show — and an asset already in the album makes no second row.
        /// </summary>
        private async Task<int> AddToAlbumAsync(MovieDb db, int albumId, List<int> assetIds)
        {
            if (assetIds.Count == 0) return 0;

            var masters = await PhotoDupeMasters.MasterMapAsync(db, assetIds);
            var wanted = assetIds.Select(id => masters.TryGetValue(id, out var m) ? m : id).Distinct().ToList();

            var already = await db.PhotoAlbumEntries
                .Where(e => e.PhotoAlbumId == albumId && wanted.Contains(e.PhotoAssetId))
                .Select(e => e.PhotoAssetId)
                .ToListAsync();
            var existing = new HashSet<int>(already);
            var missing = wanted.Where(id => !existing.Contains(id)).ToList();
            if (missing.Count == 0) return 0;
            if (options.DryRun) return missing.Count;

            var nextSort = await db.PhotoAlbumEntries.Where(e => e.PhotoAlbumId == albumId)
                .Select(e => (int?)e.SortOrder).MaxAsync() is int max ? max + 1 : 0;

            foreach (var id in missing)
            {
                db.PhotoAlbumEntries.Add(new PhotoAlbumEntry
                {
                    PhotoAlbumId = albumId,
                    PhotoAssetId = id,
                    SortOrder = nextSort++,
                });
            }
            await db.SaveChangesAsync();
            return missing.Count;
        }

        /// <summary>
        /// The rule's row set: inside the prefix, outside every exclusion.
        ///
        /// <para>Missing rows are INCLUDED. A file the walk can no longer find still has a shelf, and
        /// leaving it on the timeline would mean a folder reorganization that brought it back also
        /// brought a meme back onto the family record. The exclusion this pass makes is about filing,
        /// not about presence.</para>
        /// </summary>
        private IQueryable<PhotoAsset> Matched(MovieDb db)
        {
            var prefix = NormalizePrefix(options.PathPrefix);
            IQueryable<PhotoAsset> query = db.PhotoAssets;
            if (prefix.Length > 0) query = query.Where(a => a.Path.StartsWith(prefix));
            foreach (var raw in options.ExcludePrefixes)
            {
                var exclude = NormalizePrefix(raw);
                if (exclude.Length == 0) continue;
                // Captured per iteration — a closure over the loop variable would apply the LAST
                // exclusion N times, which reads as "the excludes did nothing" for every one but one.
                var e = exclude;
                query = query.Where(a => !a.Path.StartsWith(e));
            }
            return query;
        }

        private static int ParseCursor(string? cursor) =>
            int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }
}
