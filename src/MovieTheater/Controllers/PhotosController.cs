using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Core;
using MovieTheater.Db;
using MovieTheater.Photos;
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Family photo album control plane (docs/photos-plan.md §4). Phase 1 added the browse surfaces —
    /// timeline, folder tree, asset detail, capability minting and the admin ingest readout; Phase 2
    /// adds curation (hide flags, suggested-hide review, ingest-batch quarantine) and albums; people,
    /// dupes and video playback land in later phases behind this same class-level policy.
    ///
    /// <para>The policy is declared ONCE, here, rather than per action — an endpoint added later
    /// inherits the gate instead of having to remember it, which is the failure mode that makes
    /// UI-only gating dangerous (§2.1). Nothing in this controller may ever carry <c>[EnableQuery]</c>:
    /// §6's privacy invariant keeps photo tables out of OData entirely.</para>
    ///
    /// <para>Photo BYTES are not served from here. They flow through short-lived HMAC capability tokens
    /// to the StreamGateway's PhotoThumb/PhotoOriginal routes (§2.2), minted only for a session that
    /// passes this policy — which is why the list endpoints hand back finished URLs rather than making
    /// the browser ask for a token per card.</para>
    /// </summary>
    [Authorize(Policy = FamilyAlbumGate.PolicyName)]
    public class PhotosController : Controller
    {
        private readonly MovieDb movieDb;
        private readonly MovieTheaterConfiguration config;

        /// <summary>Capability lifetime. Long enough to browse a timeline page and open the lightbox on
        /// it without re-minting, short enough that a leaked URL is not a standing grant.</summary>
        private static readonly TimeSpan TokenTtl = TimeSpan.FromHours(6);

        /// <summary>Timeline/folder page size. The justified grid asks for one screenful plus reach.</summary>
        private const int DefaultTake = 120;
        private const int MaxTake = 400;

        /// <summary>How many assets one curation call may flip. Selection mode is a human picking cards,
        /// so this is generous rather than tight — but it is a bound, because an unbounded id list is an
        /// unbounded IN clause and a request that can never be reasoned about.</summary>
        private const int MaxBatchIds = 2000;

        /// <summary>
        /// Above this many UNREVIEWED ingest batches the timeline stops filtering by them and says so
        /// (§2.5's quarantine). A chunked walk mints a marker per invocation, so an un-reviewed backlog
        /// can grow large; turning that into a thousand-term IN clause on the hottest query on the page
        /// would be a worse failure than showing photos a moment before they were approved.
        /// </summary>
        private const int MaxQuarantineBatches = 200;

        /// <summary>
        /// Mints Jellyfin playback for a family video (§2.3). Optional so the controller can be built
        /// without it — a host with no media server still serves every browse surface, and the tests
        /// that are about rows rather than streaming construct it absent on purpose.
        /// </summary>
        private readonly IPhotoVideoPlayback? videoPlayback;

        public PhotosController(MovieDb movieDb, MovieTheaterConfiguration config,
            IPhotoVideoPlayback? videoPlayback = null)
        {
            this.movieDb = movieDb;
            this.config = config;
            this.videoPlayback = videoPlayback;
        }

        /// <summary>
        /// The review state (§2.5 quarantine / §2.9 hide proposals), held in
        /// <see cref="PhotoCurationBatch"/> rows since Phase 3.
        /// <para>Phase 2 kept it as JSON under <c>PhotosReportDir</c>, which required the CLI host and
        /// this process to resolve that setting to the same directory — in prod they cannot, so every
        /// review surface here rendered empty while looking healthy. <c>PhotosReportDir</c> now holds
        /// only what never crosses that boundary: ambiguous-pairing reports and exports.</para>
        /// </summary>
        private PhotoCurationStore CurationStore => new PhotoCurationStore(movieDb);

        private int? GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
        }

        /// <summary>Whether this host can mint capability URLs at all. Unconfigured means the browse
        /// surfaces still work — they just carry no image URLs, which the UI renders as an honest
        /// "data plane not configured" instead of a page of broken images.</summary>
        private bool DataPlaneConfigured =>
            !string.IsNullOrEmpty(config.StreamGatewayBaseUrl) && !string.IsNullOrEmpty(config.StreamTokenSecret);

        /// <summary>
        /// Whether this request may SEE hidden assets (Phase 4 addendum, superseding Phase 2).
        ///
        /// <para><b>Hiding is member work; seeing what was hidden is admin work.</b> Any family member
        /// may hide or unhide a photo — that is ordinary curation and it stays where it was. But the
        /// hidden pile is the screenshots, the junk and whatever somebody decided the family should not
        /// have to scroll past, so it is revealed only to an admin who is ALSO a family member: the
        /// class-level policy has already established membership before this is ever consulted, and this
        /// adds the operator half on top.</para>
        ///
        /// <para><b>A non-admin asking is IGNORED, not refused.</b> A 403 would tell a stale tab —
        /// and its user — that there is something there to be forbidden; silently answering the curated
        /// view is both the honest answer to "show me the album" and the one that cannot be probed.</para>
        /// </summary>
        private bool ShowHidden(bool requested) => requested && IsCurrentUserAdmin();

        /// <summary>
        /// The same rule for the BY-ID endpoints, where there is no <c>includeHidden</c> to opt into.
        ///
        /// <para><see cref="ShowHidden"/> guards the list surfaces, which is where the rule is easy to
        /// see and easy to believe is complete. It is not: a list that filters hidden rows still hands
        /// the browser real asset ids for everything ELSE, and every by-id endpoint beside it —
        /// <c>Asset/{id}</c>, <c>Tokens</c>, the tag and album readouts, <c>Video/Start</c>, the dupe
        /// group's member data — answered on any id it was given. "Hidden is visible only to an admin on
        /// EVERY surface" then meant "on every surface that happens to be a list", and the hidden pile
        /// was one guessable integer away for any family member with the network tab open.</para>
        ///
        /// <para>An admin needs no opt-in here: the browse lists ask because their DEFAULT must stay the
        /// curated view, but a direct fetch of one asset is already a deliberate act. The refusal is a
        /// 404 rather than a 403, for the reason <see cref="ShowHidden"/> ignores rather than refuses —
        /// a 403 would confirm there is something there.</para>
        /// </summary>
        private bool HiddenFromCaller(PhotoAsset? asset) =>
            asset != null && asset.Hidden && !IsCurrentUserAdmin();

        /// <summary>The database-side half of the same rule, for filtering a set by id.</summary>
        private IQueryable<PhotoAsset> VisibleAssets(IQueryable<PhotoAsset> query) =>
            IsCurrentUserAdmin() ? query : query.Where(a => !a.Hidden);

        /// <summary>
        /// The FAMILY RECORD: everything on the timeline shelf (§2.12). Written once and applied by the
        /// timeline, the undated shelf and person pages, because those three are the surfaces that
        /// answer "what happened in this family" — and art is not an answer to that question.
        ///
        /// <para><b>Deliberately NOT applied to the folder view or to album pages.</b> The folder view
        /// is the "what is actually on disk" surface, so it shows every shelf and marks the archived
        /// ones with a badge, exactly as it already shows collapsed duplicates and marks them. An album
        /// renders whatever it contains regardless of shelf, which is the entire point of the Gallery:
        /// a collection of an artist's work must show its artwork to a family member who opens it.</para>
        ///
        /// <para><b>There is no <c>includeArchive</c> opt-in, and that is the difference from
        /// hidden.</b> Hidden is a privacy boundary with an admin override; a shelf is a filing
        /// decision, and the way to see the other shelf is to go to it — the Gallery is a section, not
        /// a checkbox.</para>
        /// </summary>
        private static IQueryable<PhotoAsset> TimelineShelf(IQueryable<PhotoAsset> query) =>
            query.Where(a => a.Shelf == PhotoShelf.Timeline);

        // ── Status ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What the album currently holds. Reaching this at all is the proof that the caller is a
        /// family member; before any ingest it answers all zeros, which is what <c>empty = true</c> says
        /// so the page does not have to infer it from counts.
        /// </summary>
        [HttpGet("/API/Photos/Status")]
        public async Task<IActionResult> Status()
        {
            // Counted server-side: these are cheap aggregates, and returning rows to count them would
            // put photo data on the wire for a page that only needs to know whether ingest has run.
            //
            // ONE query per TABLE, not one per number. The UI re-fetches this after every curation write,
            // and the obvious spelling — a CountAsync per field — was twenty sequential round trips to
            // answer one question, several of them buried inside the anonymous object below where they
            // did not look like queries at all. Conditional sums over a single scan cost the database no
            // more than the plain count did.
            var asset = await CountsAsync(movieDb.PhotoAssets, g => new AssetCounts
            {
                Total = g.Count(),
                Photos = g.Sum(a => a.Kind == PhotoAssetKind.Photo ? 1 : 0),
                Videos = g.Sum(a => a.Kind == PhotoAssetKind.Video ? 1 : 0),
                // Files the walk stopped finding — flagged, never deleted (§2.5). Surfaced from the
                // first day so drift is visible rather than discovered.
                Missing = g.Sum(a => a.MissingSinceUtc != null ? 1 : 0),
                Hidden = g.Sum(a => a.Hidden ? 1 : 0),
                // §2.12: what the Gallery holds. Counted over the same single scan as everything else
                // here rather than as its own CountAsync — the whole reason this is a conditional sum.
                Archived = g.Sum(a => a.Shelf == PhotoShelf.Archive ? 1 : 0),
                // Both remaining counts now say TIMELINE SHELF, because both are read as "how much of
                // the family record is X". Undated in particular drives whether the navbar offers the
                // undated shelf at all, and offering a shelf that the timeline-shelf query then renders
                // empty would be a rail entry leading nowhere.
                Undated = g.Sum(a => !a.Hidden && a.MissingSinceUtc == null && a.TakenAt == null
                                     && a.Shelf == PhotoShelf.Timeline ? 1 : 0),
                // §2.3: a video with no Jellyfin item id is on disk, browsable and taggable, and simply
                // cannot play — which is what the ⚠ on the Review tab counts against.
                VideosSynced = g.Sum(a => a.Kind == PhotoAssetKind.Video && a.MissingSinceUtc == null
                                          && a.JellyfinItemId != null ? 1 : 0),
                // What the timeline VIEW can actually show a member, before dupe collapse: the navbar's
                // Timeline entry used the raw table total, which quietly included the Gallery, the
                // hidden pile and the missing — a rail entry promising ~2,900 photographs the page then
                // never shows reads as data loss, the exact misreading every count here is written
                // against.
                TimelineVisible = g.Sum(a => a.Shelf == PhotoShelf.Timeline && !a.Hidden
                                             && a.MissingSinceUtc == null ? 1 : 0),
            });

            var person = await CountsAsync(movieDb.FamilyPeople, g => new PersonCounts
            {
                Total = g.Count(),
                // §2.8: named rows only are people; an unnamed row is an imported face cluster waiting
                // to be named, which is a queue item rather than a person.
                Named = g.Sum(p => p.Name != "" ? 1 : 0),
                Unnamed = g.Sum(p => p.Name == "" ? 1 : 0),
            });

            // What the Dupes surface has waiting (§2.6). Variant groups are settled by the pass and are
            // never offered for "pick the better copy", so they cannot be Pending and cannot inflate
            // this number.
            var dupe = await CountsAsync(movieDb.PhotoDupeGroups, g => new DupeCounts
            {
                Pending = g.Sum(x => x.Status == PhotoDupeGroupStatus.Pending
                                     && x.Kind != PhotoDupeGroupKind.Variant ? 1 : 0),
                PendingNear = g.Sum(x => x.Status == PhotoDupeGroupStatus.Pending
                                         && x.Kind == PhotoDupeGroupKind.Near ? 1 : 0),
            });

            // §2.10: `googleOnly` is what the Review tab counts against — items the archive holds and
            // the library does not, waiting for somebody to say keep or ignore.
            var google = await CountsAsync(movieDb.PhotoGoogleItems, g => new GoogleCounts
            {
                Total = g.Count(),
                Unmatched = g.Sum(i => i.Status == PhotoGoogleItemStatus.Unmatched ? 1 : 0),
            });

            var assets = asset.Total;
            // §2.12 splits the album index in two. One grouped query, same reasoning as the asset
            // counts: the navbar draws both rail entries from this response and a CountAsync each would
            // be two round trips to answer one question.
            var album = await CountsAsync(movieDb.PhotoAlbums, g => new AlbumCounts
            {
                Total = g.Count(),
                Archive = g.Sum(a => a.Shelf == PhotoShelf.Archive ? 1 : 0),
                // An archive album with an artist is an ARTIST COLLECTION; the Gallery index leads with
                // them, so the count is what lets the rail say whether there is a gallery worth the name.
                Artists = g.Sum(a => a.Shelf == PhotoShelf.Archive && a.ArtistName != null ? 1 : 0),
            });
            var albums = album.Total - album.Archive;
            var collapsedIds = PhotoDupeMasters.CollapsedAssetIds(movieDb);
            var collapsed = await collapsedIds.CountAsync();
            // The collapse's share of the timeline shelf, subtracted so `timelineCount` is what the
            // timeline page actually renders. Counted against the shelf rather than reusing the global
            // figure: a collapsed copy sitting in the Gallery or the hidden pile was never going to be
            // on the timeline, and subtracting it twice would under-promise the same way the raw total
            // over-promised.
            var collapsedOnTimeline = await TimelineShelf(movieDb.PhotoAssets)
                .CountAsync(a => !a.Hidden && a.MissingSinceUtc == null && collapsedIds.Contains(a.Id));
            var pendingTagSuggestions = await movieDb.PhotoPersonTags
                .CountAsync(t => t.Source == PhotoTagSource.Suggested);
            var untaggedPhotos = await UntaggedQueue().CountAsync();

            // What the Review surface has waiting. Counted here so the page can show (or hide) the tab
            // without a second round-trip on every visit.
            var store = CurationStore;
            var quarantine = await QuarantineAsync(store);
            var pendingProposals = (await store.ListProposalsAsync()).Count(p => p.Status == PhotoHideProposal.StatusPending);

            return Json(new
            {
                assets,
                photos = asset.Photos,
                videos = asset.Videos,
                missing = asset.Missing,
                hidden = asset.Hidden,
                undated = asset.Undated,
                people = person.Total,
                // The FAMILY album index only (§2.12) — Gallery collections are counted beside it, not
                // inside it, so the two rail entries add up to the album table rather than double-count
                // part of it.
                albums,
                archived = asset.Archived,
                archiveAlbums = album.Archive,
                artistCollections = album.Artists,
                pendingDupeGroups = dupe.Pending,
                pendingNearGroups = dupe.PendingNear,
                // What the Timeline rail entry promises — and therefore what the page shows (§2.12's
                // shelf split plus every member-facing exclusion, minus the collapsed copies).
                timelineCount = asset.TimelineVisible - collapsedOnTimeline,
                // Non-masters a settled group keeps out of the timeline (§2.6). Not a deletion and not
                // a hide — the folder view still shows every one of them.
                collapsed,
                empty = assets == 0,
                dataPlane = DataPlaneConfigured,
                // Curation review state (§2.5/§2.9).
                curationStore = store.Configured,
                pendingHideProposals = pendingProposals,
                quarantinedBatches = quarantine.PendingCount,
                quarantineActive = quarantine.Active,
                // Being in the album is not being an operator: the ingest-batch surfaces are admin-only
                // on top of the family gate, and the UI needs to know which of the two it is drawing.
                admin = IsCurrentUserAdmin(),
                namedPeople = person.Named,
                unnamedFaceGroups = person.Unnamed,
                pendingTagSuggestions,
                untaggedPhotos,
                // Whether the navbar's show-hidden checkbox does anything for this session.
                canShowHidden = IsCurrentUserAdmin(),
                // The sidecar is optional and absent by default; the UI hides its affordances rather
                // than offering buttons that cannot work (§2.4).
                immich = !string.IsNullOrWhiteSpace(config.ImmichBaseUrl) && !string.IsNullOrWhiteSpace(config.ImmichApiKey),
                videosSynced = asset.VideosSynced,
                videoPlayback = videoPlayback?.Configured ?? false,
                googleItems = google.Total,
                googleOnly = google.Unmatched,
            });
        }

        /// <summary>
        /// Runs one grouped-aggregate query over a table and hands back the shape, or an all-zero shape
        /// when the table is empty.
        ///
        /// <para>The empty case is why this is a helper rather than an inline <c>GroupBy</c>: grouping by
        /// a constant produces NO ROWS over an empty table, so the natural spelling throws or nulls on
        /// exactly the state <c>/API/Photos/Status</c> exists to report — a host before its first
        /// ingest.</para>
        /// </summary>
        private static async Task<TCounts> CountsAsync<TEntity, TCounts>(
            IQueryable<TEntity> source, System.Linq.Expressions.Expression<Func<IGrouping<int, TEntity>, TCounts>> shape)
            where TCounts : new() =>
            await source.GroupBy(_ => 1).Select(shape).FirstOrDefaultAsync() ?? new TCounts();

        private sealed class AssetCounts
        {
            public int Total { get; set; }
            public int Photos { get; set; }
            public int Videos { get; set; }
            public int Missing { get; set; }
            public int Hidden { get; set; }
            public int Archived { get; set; }
            public int Undated { get; set; }
            public int VideosSynced { get; set; }
            public int TimelineVisible { get; set; }
        }

        private sealed class AlbumCounts
        {
            public int Total { get; set; }
            public int Archive { get; set; }
            public int Artists { get; set; }
        }

        private sealed class PersonCounts
        {
            public int Total { get; set; }
            public int Named { get; set; }
            public int Unnamed { get; set; }
        }

        private sealed class DupeCounts
        {
            public int Pending { get; set; }
            public int PendingNear { get; set; }
        }

        private sealed class GoogleCounts
        {
            public int Total { get; set; }
            public int Unmatched { get; set; }
        }

        /// <summary>
        /// The cheapest possible confirmation that this session is inside the gate — used by the SPA to
        /// re-check membership on a page it was navigated to directly, without paying for the counts.
        /// A non-member never sees this body; the policy answers 403 first.
        /// </summary>
        [HttpGet("/API/Photos/Access")]
        public IActionResult Access()
        {
            return Json(new { familyAlbum = true, userId = GetCurrentUserId() });
        }

        // ── Timeline (§2.7) ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The primary browse surface (§1), cursor-paged by <c>(TakenAt DESC, Id DESC)</c> — the shape
        /// the covering index is keyed for, so a page SEEKS rather than re-sorting the table.
        ///
        /// <para><b>Date-unknown items are NOT interleaved</b> (§2.7). They live on their own shelf,
        /// reached with <c>undated=true</c>, because scattering them at epoch 0 — or at any invented
        /// date — is exactly the dishonesty the plan's date rules exist to avoid. The two modes are
        /// separate queries with separate cursors and never mix in one page.</para>
        ///
        /// <para>Keyset, not OFFSET: an ingest running while someone browses shifts every offset, and
        /// the resulting skipped/repeated photos would look like data loss.</para>
        ///
        /// <para><b>Four exclusions, all curation and none a deletion.</b> Hidden assets are out unless
        /// an ADMIN asks for them (<c>includeHidden</c>, gated by <see cref="ShowHidden"/> since Phase 4);
        /// assets from an ingest batch nobody has approved are quarantined until someone does (§2.5); the
        /// non-master copies of a settled duplicate group are COLLAPSED to their master (§2.6), so one
        /// photograph that exists three times on disk is one card here; and since Phase 7 the Gallery
        /// shelf is out entirely (§2.12) — art and memes are not the family record, and unlike the other
        /// three this one has NO opt-in here, because the way to see the Gallery is to open the Gallery.
        /// All four are WHERE clauses over rows that stay exactly where they are.</para>
        /// </summary>
        [HttpGet("/API/Photos/Timeline")]
        public async Task<IActionResult> Timeline(
            string? beforeTakenAt = null, int? beforeId = null, int take = DefaultTake, bool undated = false,
            bool includeHidden = false, bool includeCollapsed = false)
        {
            take = Math.Clamp(take, 1, MaxTake);
            includeHidden = ShowHidden(includeHidden);
            // §2.12: the shelf filter goes on FIRST so the filtered covering index
            // (IX_PhotoAsset_TimelineShelf) is the obvious candidate for the whole predicate.
            var query = TimelineShelf(movieDb.PhotoAssets).Where(a => a.MissingSinceUtc == null);
            if (!includeHidden) query = query.Where(a => !a.Hidden);
            if (!includeCollapsed)
            {
                // Composed as a subquery, never materialized: the duplicate ids are a set the database
                // can subtract, and pulling them into memory would cost the whole collection to draw
                // one screenful.
                var collapsed = PhotoDupeMasters.CollapsedAssetIds(movieDb);
                query = query.Where(a => !collapsed.Contains(a.Id));
            }

            var quarantine = await QuarantineAsync(CurationStore);
            if (quarantine.Applied.Count > 0)
            {
                var pending = quarantine.Applied;
                query = query.Where(a => a.IngestBatch == null || !pending.Contains(a.IngestBatch));
            }

            List<PhotoAsset> rows;
            if (undated)
            {
                query = query.Where(a => a.TakenAt == null);
                if (beforeId != null) query = query.Where(a => a.Id < beforeId.Value);
                rows = await query.OrderByDescending(a => a.Id).Take(take).ToListAsync();
            }
            else
            {
                query = query.Where(a => a.TakenAt != null);
                if (beforeId != null
                    && DateTime.TryParse(beforeTakenAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var cursorTakenAt))
                {
                    // The tie-break on Id is what makes the cursor total: many photos share a
                    // to-the-second capture time, and without it a page boundary inside such a run
                    // would drop or repeat the rest of that second.
                    query = query.Where(a => a.TakenAt < cursorTakenAt
                        || (a.TakenAt == cursorTakenAt && a.Id < beforeId.Value));
                }
                rows = await query.OrderByDescending(a => a.TakenAt).ThenByDescending(a => a.Id)
                    .Take(take).ToListAsync();
            }

            var userId = GetCurrentUserId() ?? 0;
            var badges = await BadgesAsync(rows);
            var last = rows.Count > 0 ? rows[rows.Count - 1] : null;
            return Json(new
            {
                items = rows.Select(a => Card(a, userId, badges)).ToList(),
                nextCursor = last == null ? null : new
                {
                    takenAt = last.TakenAt,
                    id = last.Id,
                },
                // "Fewer than asked for" is the only honest end-of-list signal for a keyset page.
                hasMore = rows.Count == take,
                undated,
                includeHidden,
                includeCollapsed,
                quarantinedBatches = quarantine.PendingCount,
                dataPlane = DataPlaneConfigured,
            });
        }

        /// <summary>
        /// The timeline's year index — what a scrubber needs to make 75 years navigable: which years
        /// hold photographs, how many, and how big the date-unknown shelf is.
        ///
        /// <para>Counts honor the SAME four exclusions as <see cref="Timeline"/> (shelf, hidden,
        /// quarantine, dupe collapse), because a rail that promises 300 photos in 2010 and lands on a
        /// page showing 240 reads as data loss — the exact misreading the timeline's own filters are
        /// documented against. The jump itself needs no endpoint at all: the browser seeds the existing
        /// keyset cursor at Jan 1 of the following year with <c>beforeId=0</c>, which the tie-break
        /// predicate turns into a clean strictly-before seek.</para>
        /// </summary>
        [HttpGet("/API/Photos/TimelineYears")]
        public async Task<IActionResult> TimelineYears(bool includeHidden = false)
        {
            includeHidden = ShowHidden(includeHidden);
            var query = TimelineShelf(movieDb.PhotoAssets).Where(a => a.MissingSinceUtc == null);
            if (!includeHidden) query = query.Where(a => !a.Hidden);
            var collapsed = PhotoDupeMasters.CollapsedAssetIds(movieDb);
            query = query.Where(a => !collapsed.Contains(a.Id));

            var quarantine = await QuarantineAsync(CurationStore);
            if (quarantine.Applied.Count > 0)
            {
                var pending = quarantine.Applied;
                query = query.Where(a => a.IngestBatch == null || !pending.Contains(a.IngestBatch));
            }

            var years = await query.Where(a => a.TakenAt != null)
                .GroupBy(a => a.TakenAt!.Value.Year)
                .Select(g => new { year = g.Key, count = g.Count() })
                .OrderByDescending(g => g.year)
                .ToListAsync();
            var undated = await query.CountAsync(a => a.TakenAt == null);

            return Json(new { years, undated });
        }

        // ── Folder view (§2.9) ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// The folder tree, derived from <c>Path</c> prefixes — zero extra modeling (§2.9), and the
        /// folder is a browse VIEW, never an album's identity.
        ///
        /// <para>One query per level, and neither of them materializes the table: the child-folder list
        /// is a server-side GROUP BY over the first segment below the prefix, and the file list is the
        /// paged set of rows with no further separator. Loading every descendant path to slice it in
        /// memory would cost the whole collection to draw the root's thirty-odd folders.</para>
        ///
        /// <para>Unlike the timeline, the folder view shows the non-master copies a duplicate group
        /// collapses (§2.6): it is the "what is actually on disk" surface, and a collapse is a browse
        /// decision rather than a claim about the disk. Each card carries its group badge so a copy reads
        /// as a copy rather than as a mystery the timeline is missing.</para>
        ///
        /// <para><b>Hidden items are the Phase 4 exception</b> (addendum, superseding §2.9's "folder view
        /// shows all"). "Hidden is visible only to an admin" has to hold on EVERY surface or it holds on
        /// none — a rule the folder tab quietly opted out of would not be a rule, it would be a longer
        /// route to the same pictures. An admin still sees the whole tree by turning the navbar's
        /// show-hidden checkbox on.</para>
        /// </summary>
        [HttpGet("/API/Photos/Folders")]
        public async Task<IActionResult> Folders(string? path = null, int skip = 0, int take = DefaultTake,
            bool includeHidden = false)
        {
            take = Math.Clamp(take, 1, MaxTake);
            skip = Math.Max(0, skip);
            includeHidden = ShowHidden(includeHidden);
            var prefix = NormalizeFolder(path);

            IQueryable<PhotoAsset> inTree = movieDb.PhotoAssets.Where(a => a.MissingSinceUtc == null);
            if (!includeHidden) inTree = inTree.Where(a => !a.Hidden);
            inTree = prefix.Length == 0 ? inTree : inTree.Where(a => a.Path.StartsWith(prefix));
            var prefixLength = prefix.Length;

            var folders = await inTree
                .Select(a => a.Path.Substring(prefixLength))
                .Where(rest => rest.Contains("/"))
                .Select(rest => rest.Substring(0, rest.IndexOf("/")))
                .GroupBy(name => name)
                .Select(g => new { name = g.Key, count = g.Count() })
                .OrderBy(f => f.name)
                .ToListAsync();

            var files = inTree.Where(a => !a.Path.Substring(prefixLength).Contains("/"));
            var total = await files.CountAsync();
            var rows = await files.OrderBy(a => a.Path).Skip(skip).Take(take).ToListAsync();

            var userId = GetCurrentUserId() ?? 0;
            var badges = await BadgesAsync(rows);
            return Json(new
            {
                path = prefix.TrimEnd('/'),
                folders,
                items = rows.Select(a => Card(a, userId, badges)).ToList(),
                total,
                skip,
                hasMore = skip + rows.Count < total,
                includeHidden,
                dataPlane = DataPlaneConfigured,
            });
        }

        /// <summary>Folder paths arrive from the UI as the same root-relative, forward-slash strings the
        /// rows carry. Normalized to a prefix ending in '/' (empty for the root) so the two queries
        /// above can be written once; leading slashes and <c>..</c> segments are stripped rather than
        /// interpreted — this is a string prefix over a column, not a filesystem path, and nothing here
        /// ever touches the disk.</summary>
        private static string NormalizeFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var parts = path!.Replace('\\', '/').Split('/')
                .Where(s => s.Length > 0 && s != "." && s != "..")
                .ToList();
            return parts.Count == 0 ? "" : string.Join("/", parts) + "/";
        }

        // ── Asset detail ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Everything the lightbox needs for one asset: the view/zoom/original capabilities honouring
        /// <c>OriginalRenderable</c> (§2.2), and the EXIF panel — the raw readout the metadata pass
        /// persisted, which is why re-reading the file off the NAS is never necessary to show it.
        /// </summary>
        [HttpGet("/API/Photos/Asset/{id}")]
        public async Task<IActionResult> Asset(int id)
        {
            var a = await movieDb.PhotoAssets.FirstOrDefaultAsync(x => x.Id == id);
            // A hidden asset does not exist for a non-admin, here as everywhere else: the EXIF panel,
            // the folder, the camera and the file name are exactly what "hidden" was supposed to take
            // off the table, and the id to ask for is a small integer.
            if (a == null || HiddenFromCaller(a)) return NotFound();

            var userId = GetCurrentUserId() ?? 0;
            var badges = await BadgesAsync(new List<PhotoAsset> { a });
            var slash = a.Path.LastIndexOf('/');
            return Json(new
            {
                card = Card(a, userId, badges),
                fileName = slash < 0 ? a.Path : a.Path.Substring(slash + 1),
                folder = slash < 0 ? "" : a.Path.Substring(0, slash),
                sizeBytes = a.SizeBytes,
                fileModifiedUtc = a.FileModifiedUtc,
                takenAtUtcRaw = a.TakenAtUtcRaw,
                cameraMake = a.CameraMake,
                cameraModel = a.CameraModel,
                gpsLat = a.GpsLat,
                gpsLon = a.GpsLon,
                locationLabel = a.LocationLabel,
                sha256 = a.Sha256,
                hidden = a.Hidden,
                ingestBatch = a.IngestBatch,
                ingestError = a.IngestError,
                // The lightbox's zoom target (§2.2): the untouched original when a browser can render
                // it, otherwise the 3200px derivative. One column decides, at mint time.
                viewUrl = ThumbUrl(a, userId, PhotoStreamRoutes.SizeView),
                zoomUrl = a.OriginalRenderable ? OriginalUrl(a, userId) : ThumbUrl(a, userId, PhotoStreamRoutes.SizeZoom),
                // Always the real file, always an explicit action — never the <img> src.
                downloadUrl = OriginalUrl(a, userId),
                exif = ParseRawMetadata(a.RawMetadataJson),
                // §2.3: everything the lightbox needs to decide between a play button, a "not yet
                // synced" note and nothing at all. Null for a photo, so the component branches on
                // presence rather than on a kind string.
                video = a.Kind != PhotoAssetKind.Video ? null : new
                {
                    synced = a.JellyfinItemId != null,
                    playbackConfigured = videoPlayback?.Configured ?? false,
                    durationSec = a.DurationSec,
                },
                // "Other copies" (§2.6): the lightbox jumps between the members of this photo's group —
                // the master, the second scan, the RAW half — without leaving the picture.
                group = await GroupDetailAsync(a, userId),
                // §2.10: what Google's own record says about this photograph. Null for the vast
                // majority — only a meshed archive item produces it.
                google = await GoogleDetailAsync(a.Id),
            });
        }

        /// <summary>
        /// The Takeout sidecar's view of one asset (§2.10), read back out of the verbatim JSON on the
        /// <see cref="PhotoGoogleItem"/> row.
        ///
        /// <para><b>This is where a Google DESCRIPTION lives.</b> <c>PhotoAsset</c> has no caption
        /// column, and Phase 6 declined to add one: a caption is human curation and would need an
        /// editor, a redirect to the group master and a place in the export — a feature, not a
        /// backfill. Until that exists the sidecar's own text is shown as what it is, Google's, beside
        /// the photograph rather than pretending to be ours.</para>
        /// </summary>
        private async Task<object?> GoogleDetailAsync(int assetId)
        {
            var item = await movieDb.PhotoGoogleItems
                .Where(i => i.MatchedPhotoAssetId == assetId)
                .OrderByDescending(i => i.LastSeenUtc)
                .FirstOrDefaultAsync();
            if (item == null) return null;

            var sidecar = item.SidecarJson == null ? null : PhotoGoogleSidecar.ParseJson(item.SidecarJson);
            return new
            {
                takeoutFileName = item.TakeoutFileName,
                takenAtUtc = item.TakenAtUtc,
                matchMethod = item.MatchMethod,
                // Present only for a pHash match — and its presence IS the lower-confidence marker.
                matchDistance = item.MatchDistance,
                description = sidecar?.Description,
                disagreements = SplitFlags(item.Disagreements),
            };
        }

        private static List<string> SplitFlags(string? value) =>
            string.IsNullOrEmpty(value)
                ? new List<string>()
                : value!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        /// <summary>
        /// Bulk capability minting for callers that already hold the rows (a re-mint after a token
        /// aged out mid-session, rather than re-fetching the page). Refuses anything but a derivative
        /// size, and refuses <c>zoom</c> for a renderable original — that derivative is never emitted,
        /// so a token for it could only ever 404.
        /// </summary>
        [HttpGet("/API/Photos/Tokens")]
        public async Task<IActionResult> Tokens(string ids, string size = PhotoStreamRoutes.SizeGrid)
        {
            if (size != PhotoStreamRoutes.SizeGrid && size != PhotoStreamRoutes.SizeView
                && size != PhotoStreamRoutes.SizeZoom && size != PhotoStreamRoutes.SizeOriginal)
                return BadRequest();

            var wanted = (ids ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var v) ? v : -1)
                .Where(v => v > 0)
                .Distinct()
                .Take(MaxTake)
                .ToList();
            if (wanted.Count == 0) return Json(new { urls = new Dictionary<string, string?>() });

            // Hidden ids are SKIPPED rather than refused, and the caller simply gets no entry for them:
            // this endpoint's whole job is re-minting for cards the caller already holds, and a list
            // surface never handed a non-admin a hidden id in the first place. Without this, one call
            // with "1,2,3,…" minted an original-download URL for every hidden photo in the collection.
            var rows = await VisibleAssets(movieDb.PhotoAssets.Where(a => wanted.Contains(a.Id))).ToListAsync();
            var userId = GetCurrentUserId() ?? 0;
            var urls = new Dictionary<string, string?>();
            foreach (var a in rows)
            {
                urls[a.Id.ToString(CultureInfo.InvariantCulture)] = size == PhotoStreamRoutes.SizeOriginal
                    ? OriginalUrl(a, userId)
                    : ThumbUrl(a, userId, size);
            }
            return Json(new { urls, dataPlane = DataPlaneConfigured });
        }

        // ── Curation flags (§2.9) ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Hide or unhide assets — one card or a whole selection, one round-trip either way.
        ///
        /// <para><b>This is a flag, and only ever a flag.</b> Nothing under the collection root is
        /// written, renamed, moved or deleted (§6): a hidden photo is out of the timeline and out of
        /// albums, still in the folder view, and still exactly where it always was on disk. That is why
        /// there is no confirmation on it — hiding is reversible by definition, which is the whole
        /// reason §1 asked for a flag "not deletion".</para>
        /// </summary>
        [HttpPost("/API/Photos/Hide")]
        public async Task<IActionResult> Hide([FromBody] PhotoHideRequest request)
        {
            if (request?.Ids == null || request.Ids.Count == 0) return BadRequest(new { message = "No assets selected." });

            var ids = request.Ids.Distinct().Take(MaxBatchIds).ToList();
            var rows = await movieDb.PhotoAssets.Where(a => ids.Contains(a.Id)).ToListAsync();
            var changed = 0;
            foreach (var row in rows)
            {
                if (row.Hidden == request.Hidden) continue;
                row.Hidden = request.Hidden;
                changed++;
            }
            if (changed > 0) await movieDb.SaveChangesAsync();

            return Json(new { requested = ids.Count, matched = rows.Count, changed, hidden = request.Hidden });
        }

        /// <summary>
        /// Move photographs between the family timeline and the Gallery (§2.12) — one card or a whole
        /// selection, one round-trip either way.
        ///
        /// <para><b>Member work, at exactly hide's permission level.</b> Deciding that a picture is art
        /// rather than family record is ordinary curation, and the class-level family gate is the whole
        /// of the check — the same stance Phase 2 took for hiding and for the same reason: a shared
        /// family album whose filing only one person may change is one person's album. (Hide's ADMIN
        /// half is about SEEING the hidden pile, which is a different question and is unaffected here.)</para>
        ///
        /// <para><b>The move is GROUP-COHERENT</b> (see
        /// <see cref="PhotoDupeMasters.GroupCoherentIdsAsync"/>): a settled duplicate group is one
        /// photograph on the browse surfaces, so it changes shelf as a unit or the collapse breaks. The
        /// number of extra rows that came along is REPORTED, because a member who moved six cards and
        /// changed nine is owed the reason — the same courtesy the album and tag routes already pay for
        /// master redirects.</para>
        ///
        /// <para>A flag, and only ever a flag (§6). Nothing is written, renamed, moved or deleted under
        /// the collection root; the pictures are in the other section and can be sent back from it.</para>
        /// </summary>
        [HttpPost("/API/Photos/Shelf")]
        public async Task<IActionResult> Shelf([FromBody] PhotoShelfRequest request)
        {
            if (request?.Ids == null || request.Ids.Count == 0) return BadRequest(new { message = "No assets selected." });
            if (!Enum.TryParse<PhotoShelf>(request.Shelf, ignoreCase: true, out var shelf))
                return BadRequest(new { message = "Unknown shelf." });

            var ids = request.Ids.Distinct().Take(MaxBatchIds).ToList();
            var expanded = await PhotoDupeMasters.GroupCoherentIdsAsync(movieDb, ids);
            var alsoMoved = expanded.Count - ids.Count;

            var rows = await movieDb.PhotoAssets.Where(a => expanded.Contains(a.Id)).ToListAsync();
            var changed = 0;
            foreach (var row in rows)
            {
                if (row.Shelf == shelf) continue;
                row.Shelf = shelf;
                changed++;
            }
            if (changed > 0) await movieDb.SaveChangesAsync();

            return Json(new
            {
                requested = ids.Count,
                matched = rows.Count,
                changed,
                // Group members dragged along by the coherence rule — never silent.
                groupMembersIncluded = alsoMoved,
                shelf = shelf.ToString(),
            });
        }

        /// <summary>
        /// The suggested-hide proposals waiting for a verdict (§2.9), newest first, each with its rule
        /// breakdown and a handful of example paths.
        ///
        /// <para>The full item list is deliberately NOT returned: a screenshots pile is thousands of
        /// rows, and a reviewer decides on the RULE and the count, not by scrolling ten thousand file
        /// names. <c>/API/Photos/HideProposal/{batchId}</c> pages the cards for anyone who wants to
        /// look before accepting.</para>
        /// </summary>
        [HttpGet("/API/Photos/HideProposals")]
        public async Task<IActionResult> HideProposals(bool includeDecided = false)
        {
            var store = CurationStore;
            var batches = (await store.ListProposalsAsync())
                .Where(p => includeDecided || p.Status == PhotoHideProposal.StatusPending)
                .ToList();

            var proposals = new List<object>();
            foreach (var p in batches)
            {
                // Counts and samples as aggregates, one proposal at a time: the item rows are never
                // loaded to draw this list.
                var (rules, count, samples) = await store.RuleCountsAsync(p.BatchId);
                proposals.Add(new
                {
                    batchId = p.BatchId,
                    createdUtc = p.CreatedUtc,
                    status = p.Status,
                    decidedUtc = p.DecidedUtc,
                    appliedCount = p.AppliedCount,
                    complete = p.Complete,
                    count,
                    rules,
                    samplePaths = samples,
                });
            }

            return Json(new { configured = store.Configured, proposals });
        }

        /// <summary>One proposal's assets as cards, paged — the "let me look first" surface.</summary>
        [HttpGet("/API/Photos/HideProposal/{batchId}")]
        public async Task<IActionResult> HideProposal(string batchId, int skip = 0, int take = DefaultTake)
        {
            take = Math.Clamp(take, 1, MaxTake);
            skip = Math.Max(0, skip);

            var store = CurationStore;
            var proposal = await store.LoadProposalAsync(batchId);
            if (proposal == null) return NotFound();

            var total = await store.ProposalItemCountAsync(batchId);
            var page = await store.ProposalItemsAsync(batchId, skip, take);
            var ids = page.Select(i => i.AssetId).ToList();
            var rows = await movieDb.PhotoAssets.Where(a => ids.Contains(a.Id)).ToListAsync();
            var byId = rows.ToDictionary(a => a.Id);
            var badges = await BadgesAsync(rows);
            var userId = GetCurrentUserId() ?? 0;

            return Json(new
            {
                batchId = proposal.BatchId,
                status = proposal.Status,
                total,
                skip,
                hasMore = skip + page.Count < total,
                items = page.Where(i => byId.ContainsKey(i.AssetId))
                    .Select(i => new { rule = i.Rule, card = Card(byId[i.AssetId], userId, badges) })
                    .ToList(),
                dataPlane = DataPlaneConfigured,
            });
        }

        /// <summary>
        /// Accept or reject a whole proposal in one action (§2.9: "human-confirmed batch-wise").
        ///
        /// <para>Accepting re-reads the rows rather than trusting the artifact: an asset that has since
        /// been hidden by hand, or that the walk has since lost, is skipped and counted, so the applied
        /// number is what actually happened and not what the file hoped for. Rejecting writes nothing at
        /// all — the proposal is stamped and stops appearing.</para>
        /// </summary>
        [HttpPost("/API/Photos/HideProposal/{batchId}/{decision}")]
        public async Task<IActionResult> DecideHideProposal(string batchId, string decision)
        {
            var accept = string.Equals(decision, "accept", StringComparison.OrdinalIgnoreCase);
            if (!accept && !string.Equals(decision, "reject", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Decision must be accept or reject." });

            var store = CurationStore;
            var proposal = await store.LoadProposalAsync(batchId);
            if (proposal == null) return NotFound();
            if (proposal.Status != PhotoHideProposal.StatusPending)
                return BadRequest(new { message = "That batch has already been decided." });

            var proposed = await store.ProposalItemCountAsync(batchId);
            var applied = 0;
            if (accept)
            {
                // Chunked over the proposal's items by their own id: a screenshots pile is thousands of
                // rows, and one IN clause per thousand keeps the statement a shape the database can
                // plan. The page orders by the same column the cursor advances through.
                var afterItemId = 0;
                while (true)
                {
                    var page = await store.ProposalAssetPageAsync(batchId, afterItemId, 1000);
                    if (page.Count == 0) break;

                    var chunk = page.Select(i => i.AssetId).Distinct().ToList();
                    var rows = await movieDb.PhotoAssets.Where(a => chunk.Contains(a.Id) && !a.Hidden).ToListAsync();
                    foreach (var row in rows) { row.Hidden = true; applied++; }
                    if (rows.Count > 0) await movieDb.SaveChangesAsync();

                    afterItemId = page[page.Count - 1].ItemId;
                }
            }

            var stamped = await store.DecideAsync(batchId,
                accept ? PhotoHideProposal.StatusAccepted : PhotoHideProposal.StatusRejected,
                GetCurrentUserId(), applied);

            return Json(new
            {
                batchId,
                status = stamped?.Status,
                applied,
                proposed,
            });
        }

        // ── Ingest-batch quarantine (§2.5) ───────────────────────────────────────────────────────

        /// <summary>
        /// The ingest batches and their review state, GROUPED so a night's chunked walk is one row to
        /// approve instead of forty.
        ///
        /// <para>Grouping is by the marker's date: the default marker is <c>photos-yyyyMMdd-HHmmss</c>
        /// and a driver loop mints one PER INVOCATION, so everything from the same day and prefix is one
        /// review item. A hand-passed <c>--batch-id</c> that does not carry that shape stands alone,
        /// which is what naming a batch by hand should mean.</para>
        ///
        /// <para>Admin-only on top of the family gate: this describes the pipeline, not the photos.</para>
        /// </summary>
        [HttpGet("/API/Photos/IngestBatches")]
        public async Task<IActionResult> IngestBatches()
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            var store = CurationStore;
            var batches = await movieDb.PhotoAssets
                .Where(a => a.IngestBatch != null)
                .GroupBy(a => a.IngestBatch!)
                .Select(g => new { batch = g.Key, count = g.Count(), firstSeenUtc = g.Min(a => a.FirstSeenUtc) })
                .ToListAsync();

            var review = await store.LoadIngestReviewAsync(batches.Select(b => b.batch));
            var groups = batches
                .GroupBy(b => PhotoCurationStore.GroupKey(b.batch))
                .Select(g => new
                {
                    groupKey = g.Key,
                    batchIds = g.Select(b => b.batch).OrderBy(b => b, StringComparer.Ordinal).ToList(),
                    count = g.Sum(b => b.count),
                    firstSeenUtc = g.Min(b => b.firstSeenUtc),
                    lastSeenUtc = g.Max(b => b.firstSeenUtc),
                    approved = g.All(b => review.IsApproved(b.batch)),
                    pendingBatchIds = g.Where(b => !review.IsApproved(b.batch))
                        .Select(b => b.batch).OrderBy(b => b, StringComparer.Ordinal).ToList(),
                })
                .OrderByDescending(g => g.firstSeenUtc)
                .ToList();

            var quarantine = await QuarantineAsync(store);
            return Json(new
            {
                configured = store.Configured,
                baselineUtc = review.BaselineUtc,
                groups,
                quarantinedBatches = quarantine.PendingCount,
                quarantineActive = quarantine.Active,
                quarantineCap = MaxQuarantineBatches,
            });
        }

        /// <summary>Approves batches into the timeline — a group's ids in one action.</summary>
        [HttpPost("/API/Photos/IngestBatches/Approve")]
        public async Task<IActionResult> ApproveIngestBatches([FromBody] PhotoApproveBatchesRequest request)
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            var store = CurationStore;
            if (!store.Configured) return BadRequest(new { message = "Batch review is unavailable on this host." });

            var existing = await movieDb.PhotoAssets.Where(a => a.IngestBatch != null)
                .Select(a => a.IngestBatch!).Distinct().ToListAsync();

            var wanted = new List<string>(request?.BatchIds ?? new List<string>());
            if (!string.IsNullOrWhiteSpace(request?.GroupKey))
                wanted.AddRange(existing.Where(b => PhotoCurationStore.GroupKey(b) == request!.GroupKey));
            if (wanted.Count == 0) return BadRequest(new { message = "Nothing to approve." });

            var review = await store.ApproveIngestBatchesAsync(
                existing, wanted.Distinct(StringComparer.OrdinalIgnoreCase), GetCurrentUserId());
            return Json(new { approved = review.Approved.Count, batches = wanted.Distinct(StringComparer.OrdinalIgnoreCase).Count() });
        }

        /// <summary>Which batches the timeline is currently keeping out, and whether the filter is on at
        /// all — see <see cref="MaxQuarantineBatches"/> for the one case where it deliberately is not.</summary>
        private async Task<QuarantineState> QuarantineAsync(PhotoCurationStore store)
        {
            if (!store.Configured) return new QuarantineState();

            // One indexed DISTINCT over IX_PhotoAsset_IngestBatch. The batch count is bounded by the
            // number of ingest invocations, not by the collection size.
            var existing = await movieDb.PhotoAssets.Where(a => a.IngestBatch != null)
                .Select(a => a.IngestBatch!).Distinct().ToListAsync();
            if (existing.Count == 0) return new QuarantineState();

            var review = await store.LoadIngestReviewAsync(existing);
            var pending = existing.Where(b => !review.IsApproved(b)).ToList();
            return new QuarantineState
            {
                PendingCount = pending.Count,
                Active = pending.Count <= MaxQuarantineBatches,
                Applied = pending.Count <= MaxQuarantineBatches ? pending : new List<string>(),
            };
        }

        private sealed class QuarantineState
        {
            public int PendingCount;
            /// <summary>False only when the unreviewed backlog exceeded the cap — reported rather than
            /// silently dropped, so "why is this showing" has an answer.</summary>
            public bool Active = true;
            public List<string> Applied = new List<string>();
        }

        // ── Albums (§2.9) ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The FAMILY album index. Albums are DB rows, never folders: the tree holds device dumps and
        /// misc piles that are not albums, so the folder view is a browse surface and a SEED, and the
        /// folder is never an album's identity (§2.9).
        ///
        /// <para>Since Phase 7 this is one of TWO indexes over the same table (§2.12): Gallery
        /// collections live at <c>/API/Photos/Gallery</c> and are excluded here, so the family album
        /// shelf stays a shelf of family albums. The DETAIL page is shared — an archive album is still
        /// <c>/API/Photos/Album/{slug}</c> — so every link ever sent keeps working.</para>
        /// </summary>
        [HttpGet("/API/Photos/Albums")]
        public Task<IActionResult> Albums() => AlbumIndexAsync(PhotoShelf.Timeline);

        /// <summary>
        /// The GALLERY index (§2.12): the art, meme and reference collections the timeline does not
        /// carry, browsable by every family member — the section the owner asked for when they said
        /// this material "isn't the timeline, put [it] in another section".
        ///
        /// <para><b>Artist collections lead.</b> An archive album carrying an
        /// <see cref="PhotoAlbum.ArtistName"/> is a body of one person's work rather than a pile, and
        /// the ordering says so before any card is drawn. Within each half the album's own sort order
        /// still applies, so the shelf remains hand-arrangeable.</para>
        /// </summary>
        [HttpGet("/API/Photos/Gallery")]
        public Task<IActionResult> Gallery() => AlbumIndexAsync(PhotoShelf.Archive);

        private async Task<IActionResult> AlbumIndexAsync(PhotoShelf shelf)
        {
            var userId = GetCurrentUserId() ?? 0;
            var albums = await movieDb.PhotoAlbums
                .Where(a => a.Shelf == shelf)
                // Artist collections first on both shelves — a no-op on the family shelf, where nothing
                // carries an artist, and the Gallery's whole ordering rule on the other. One expression
                // rather than two query shapes that could drift apart.
                .OrderByDescending(a => a.ArtistName != null ? 1 : 0)
                .ThenBy(a => a.SortOrder).ThenByDescending(a => a.CreatedUtc)
                .Select(a => new
                {
                    a.Id, a.Title, a.Slug, a.Description, a.RangeStart, a.RangeEnd, a.SortOrder, a.CreatedUtc,
                    a.Shelf, a.ArtistName,
                    count = a.Entries.Count,
                    cover = a.CoverAsset,
                    // Falls back to the first entry so a fresh album is not a grey box: a cover is a
                    // nicety, and an album with members always has something to show.
                    firstEntry = a.Entries.OrderBy(e => e.SortOrder).ThenBy(e => e.Id)
                        .Select(e => e.PhotoAsset).FirstOrDefault(),
                })
                .ToListAsync();

            return Json(new
            {
                albums = albums.Select(a => new
                {
                    id = a.Id,
                    title = a.Title,
                    slug = a.Slug,
                    description = a.Description,
                    rangeStart = a.RangeStart,
                    rangeEnd = a.RangeEnd,
                    sortOrder = a.SortOrder,
                    createdUtc = a.CreatedUtc,
                    shelf = a.Shelf.ToString(),
                    artistName = a.ArtistName,
                    count = a.count,
                    coverUrl = ThumbUrl(a.cover ?? a.firstEntry, userId, PhotoStreamRoutes.SizeGrid),
                }).ToList(),
                shelf = shelf.ToString(),
                dataPlane = DataPlaneConfigured,
            });
        }

        /// <summary>
        /// One album and a page of its entries, in the album's own order.
        ///
        /// <para>Collapsed duplicates are excluded here exactly as they are from the timeline (§2.6).
        /// In practice the exclusion is nearly empty: adding a non-master to an album adds the MASTER
        /// instead (see <c>AddAssetsAsync</c>), so only entries created before a group was settled can
        /// hit it — and showing a photo twice in one album is precisely what settling that group
        /// decided against.</para>
        ///
        /// <para><b>The SHELF is not filtered here at all</b> (§2.12), on either the album's shelf or
        /// its members'. An album shows what it contains: a Gallery collection full of archive assets
        /// renders every one of them to any family member, which is the whole reason the Gallery is a
        /// section rather than a longer hide list. One URL serves both shelves, so a deep link minted
        /// before Phase 7 resolves exactly as it did.</para>
        /// </summary>
        [HttpGet("/API/Photos/Album/{slug}")]
        public async Task<IActionResult> Album(string slug, int skip = 0, int take = DefaultTake,
            bool includeCollapsed = false, bool includeHidden = false)
        {
            take = Math.Clamp(take, 1, MaxTake);
            skip = Math.Max(0, skip);
            includeHidden = ShowHidden(includeHidden);

            var album = await movieDb.PhotoAlbums.FirstOrDefaultAsync(a => a.Slug == slug);
            if (album == null) return NotFound();

            var entries = movieDb.PhotoAlbumEntries.Where(e => e.PhotoAlbumId == album.Id);
            // §2.9 has always said albums exclude hidden assets; before Phase 4 only the SEEDING path
            // honoured it, so a photo hidden after it joined an album stayed visible there. One rule,
            // every surface — otherwise "hidden" means "hidden from the timeline", which is not what a
            // family member is told when they press the button.
            if (!includeHidden) entries = entries.Where(e => !e.PhotoAsset.Hidden);
            if (!includeCollapsed)
            {
                var collapsed = PhotoDupeMasters.CollapsedAssetIds(movieDb);
                entries = entries.Where(e => !collapsed.Contains(e.PhotoAssetId));
            }
            var total = await entries.CountAsync();
            var page = await entries
                // Manual order first; taken-date breaks ties, so an album nobody has reordered still
                // reads chronologically instead of by insert order.
                .OrderBy(e => e.SortOrder).ThenBy(e => e.PhotoAsset.TakenAt).ThenBy(e => e.Id)
                .Skip(skip).Take(take)
                .Select(e => new { e.Id, e.SortOrder, e.Caption, Asset = e.PhotoAsset })
                .ToListAsync();

            var userId = GetCurrentUserId() ?? 0;
            var badges = await BadgesAsync(page.Select(e => e.Asset).ToList());
            return Json(new
            {
                album = AlbumSummary(album),
                items = page.Select(e => new
                {
                    entryId = e.Id,
                    sortOrder = e.SortOrder,
                    caption = e.Caption,
                    card = Card(e.Asset, userId, badges),
                }).ToList(),
                total,
                skip,
                hasMore = skip + page.Count < total,
                dataPlane = DataPlaneConfigured,
            });
        }

        /// <summary>
        /// Creates an album — empty, from a selection, or seeded from a folder (§2.9's "make an album
        /// from this folder", which COPIES membership into rows so the disk layout stays free to be
        /// ugly and the album survives the folder being reorganized).
        ///
        /// <para>Any family member may create and edit albums; the plan says so, and a shared family
        /// album with an owner-only curation model would be one person's album.</para>
        /// </summary>
        [HttpPost("/API/Photos/Albums")]
        public async Task<IActionResult> CreateAlbum([FromBody] PhotoAlbumCreateRequest request)
        {
            var title = (request?.Title ?? "").Trim();
            if (title.Length == 0) return BadRequest(new { message = "An album needs a title." });
            if (title.Length > 300) title = title.Substring(0, 300);

            // Slugs are minted server-side and never taken from the client: they are the album's URL,
            // and a client-chosen one is a uniqueness race plus a path-injection question nobody needs.
            var existingSlugs = await movieDb.PhotoAlbums.Select(a => a.Slug).ToListAsync();
            var album = new PhotoAlbum
            {
                Title = title,
                Slug = PhotoAlbumSlug.Unique(title, existingSlugs),
                Description = string.IsNullOrWhiteSpace(request?.Description) ? null : request!.Description!.Trim(),
                CreatedByUserId = GetCurrentUserId(),
                CreatedUtc = DateTime.UtcNow,
                SortOrder = 0,
            };
            movieDb.PhotoAlbums.Add(album);
            await movieDb.SaveChangesAsync();

            var seeded = (Added: 0, Redirected: 0);
            if (!string.IsNullOrWhiteSpace(request?.FromFolder))
                seeded = await AddAssetsAsync(album, await FolderAssetIdsAsync(request!.FromFolder!));
            else if (request?.AssetIds != null && request.AssetIds.Count > 0)
                seeded = await AddAssetsAsync(album, request.AssetIds.Distinct().Take(MaxBatchIds).ToList());

            return Json(new { album = AlbumSummary(album), added = seeded.Added, redirectedToMasters = seeded.Redirected });
        }

        /// <summary>Rename / describe / date-range / cover / album order. Every field is optional; only
        /// what is sent is touched, so two people editing different fields do not overwrite each other's
        /// work through a full-object PUT.</summary>
        [HttpPost("/API/Photos/Album/{id}/Update")]
        public async Task<IActionResult> UpdateAlbum(int id, [FromBody] PhotoAlbumUpdateRequest request)
        {
            var album = await movieDb.PhotoAlbums.FirstOrDefaultAsync(a => a.Id == id);
            if (album == null) return NotFound();
            if (request == null) return BadRequest(new { message = "Nothing to update." });

            if (request.Title != null)
            {
                var title = request.Title.Trim();
                if (title.Length == 0) return BadRequest(new { message = "An album needs a title." });
                // The slug is deliberately NOT re-minted: it is a link a family member may have sent to
                // another one, and retitling must not break it (§2.9).
                album.Title = title.Length > 300 ? title.Substring(0, 300) : title;
            }
            if (request.Description != null)
                album.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            if (request.RangeStartSet) album.RangeStart = request.RangeStart;
            if (request.RangeEndSet) album.RangeEnd = request.RangeEnd;
            if (request.SortOrder != null) album.SortOrder = request.SortOrder.Value;

            // §2.12: the album's shelf, and the artist that turns a Gallery collection into a body of
            // work. Moving an album between shelves moves the ALBUM only — its assets keep whatever
            // shelf they were put on, because "which index this collection appears on" and "is this
            // photograph part of the family record" are two different questions. The selection-bar
            // action is the one that moves assets.
            if (request.Shelf != null)
            {
                if (!Enum.TryParse<PhotoShelf>(request.Shelf, ignoreCase: true, out var shelf))
                    return BadRequest(new { message = "Unknown shelf." });
                album.Shelf = shelf;
            }
            if (request.ArtistNameSet)
            {
                var artist = request.ArtistName?.Trim();
                if (artist != null && artist.Length > 256) artist = artist.Substring(0, 256);
                album.ArtistName = string.IsNullOrWhiteSpace(artist) ? null : artist;
            }

            if (request.CoverAssetId != null)
            {
                // A cover has to be IN the album: a cover picked from outside it is a picture the album
                // does not contain, which is a puzzle for whoever opens it next.
                var member = await movieDb.PhotoAlbumEntries
                    .AnyAsync(e => e.PhotoAlbumId == album.Id && e.PhotoAssetId == request.CoverAssetId.Value);
                if (!member) return BadRequest(new { message = "The cover must be one of the album's photos." });
                album.CoverAssetId = request.CoverAssetId.Value;
            }

            await movieDb.SaveChangesAsync();
            return Json(new { album = AlbumSummary(album) });
        }

        /// <summary>Adds assets to an album — from a selection or a whole folder. Membership is a set:
        /// adding a photo that is already in is a no-op, not a second row.</summary>
        [HttpPost("/API/Photos/Album/{id}/Add")]
        public async Task<IActionResult> AddToAlbum(int id, [FromBody] PhotoAlbumMembershipRequest request)
        {
            var album = await movieDb.PhotoAlbums.FirstOrDefaultAsync(a => a.Id == id);
            if (album == null) return NotFound();

            var ids = request?.AssetIds?.Distinct().Take(MaxBatchIds).ToList() ?? new List<int>();
            if (!string.IsNullOrWhiteSpace(request?.FromFolder))
                ids = await FolderAssetIdsAsync(request!.FromFolder!);
            if (ids.Count == 0) return BadRequest(new { message = "No photos to add." });

            var result = await AddAssetsAsync(album, ids);
            var total = await movieDb.PhotoAlbumEntries.CountAsync(e => e.PhotoAlbumId == album.Id);
            // The redirect count is REPORTED, never silent: a member who selected six cards and got four
            // entries is owed the reason, and "two of those were duplicates of photos you already added"
            // is the reason (§2.6).
            return Json(new { added = result.Added, redirectedToMasters = result.Redirected, total });
        }

        /// <summary>Removes assets from an album. Rows only — the files are untouched, as always (§6).</summary>
        [HttpPost("/API/Photos/Album/{id}/Remove")]
        public async Task<IActionResult> RemoveFromAlbum(int id, [FromBody] PhotoAlbumMembershipRequest request)
        {
            var album = await movieDb.PhotoAlbums.FirstOrDefaultAsync(a => a.Id == id);
            if (album == null) return NotFound();

            var ids = request?.AssetIds?.Distinct().Take(MaxBatchIds).ToList() ?? new List<int>();
            if (ids.Count == 0) return BadRequest(new { message = "No photos to remove." });

            var rows = await movieDb.PhotoAlbumEntries
                .Where(e => e.PhotoAlbumId == album.Id && ids.Contains(e.PhotoAssetId))
                .ToListAsync();
            movieDb.PhotoAlbumEntries.RemoveRange(rows);

            // A cover that just left the album would render a photo the album no longer contains.
            if (album.CoverAssetId != null && ids.Contains(album.CoverAssetId.Value)) album.CoverAssetId = null;
            await movieDb.SaveChangesAsync();

            var total = await movieDb.PhotoAlbumEntries.CountAsync(e => e.PhotoAlbumId == album.Id);
            return Json(new { removed = rows.Count, total });
        }

        /// <summary>
        /// Sets the album's manual order from a list of asset ids.
        ///
        /// <para>The list may be PARTIAL: the ids given take the front in the order given, and everything
        /// else keeps its existing relative order behind them. That is what a drag of one card into
        /// place means, and it is why the endpoint does not demand the whole album be sent to move one
        /// photo. Unknown ids and duplicates are dropped and counted rather than rejected — a stale tab
        /// re-sending a photo someone else removed should not fail the reorder.</para>
        /// </summary>
        [HttpPost("/API/Photos/Album/{id}/Reorder")]
        public async Task<IActionResult> ReorderAlbum(int id, [FromBody] PhotoAlbumMembershipRequest request)
        {
            var album = await movieDb.PhotoAlbums.FirstOrDefaultAsync(a => a.Id == id);
            if (album == null) return NotFound();

            var entries = await movieDb.PhotoAlbumEntries
                .Where(e => e.PhotoAlbumId == album.Id)
                .OrderBy(e => e.SortOrder).ThenBy(e => e.Id)
                .ToListAsync();
            if (entries.Count == 0) return Json(new { ordered = 0, ignored = 0, total = 0 });

            var byAsset = entries.ToDictionary(e => e.PhotoAssetId);
            var wanted = request?.AssetIds ?? new List<int>();

            var ordered = new List<PhotoAlbumEntry>();
            var seen = new HashSet<int>();
            var ignored = 0;
            foreach (var assetId in wanted)
            {
                if (!seen.Add(assetId)) { ignored++; continue; }
                if (!byAsset.TryGetValue(assetId, out var entry)) { ignored++; continue; }
                ordered.Add(entry);
            }
            foreach (var entry in entries)
                if (!seen.Contains(entry.PhotoAssetId)) ordered.Add(entry);

            for (var i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i;
            await movieDb.SaveChangesAsync();

            return Json(new { ordered = ordered.Count, ignored, total = entries.Count });
        }

        /// <summary>
        /// Deletes an album.
        ///
        /// <para><c>confirm</c> is required — not because anything on disk is at risk (nothing here has
        /// ever been), but because an album is hand-built curation (§2.11) and a mis-click should not
        /// discard an afternoon of it. The entries cascade; the ASSETS do not, and neither do the
        /// files.</para>
        /// </summary>
        [HttpPost("/API/Photos/Album/{id}/Delete")]
        public async Task<IActionResult> DeleteAlbum(int id, [FromBody] PhotoAlbumDeleteRequest request)
        {
            if (request?.Confirm != true) return BadRequest(new { message = "Deleting an album needs an explicit confirmation." });

            var album = await movieDb.PhotoAlbums.FirstOrDefaultAsync(a => a.Id == id);
            if (album == null) return NotFound();

            var entries = await movieDb.PhotoAlbumEntries.Where(e => e.PhotoAlbumId == album.Id).ToListAsync();
            movieDb.PhotoAlbumEntries.RemoveRange(entries);
            movieDb.PhotoAlbums.Remove(album);
            await movieDb.SaveChangesAsync();

            return Json(new { deleted = true, entriesRemoved = entries.Count });
        }

        /// <summary>Which albums an asset is in — the lightbox asks it for every photo opened.</summary>
        [HttpGet("/API/Photos/Asset/{id}/Albums")]
        public async Task<IActionResult> AssetAlbums(int id)
        {
            // Which albums a photograph is in is a fact ABOUT that photograph, so it follows the same
            // rule the photograph does.
            if (HiddenFromCaller(await movieDb.PhotoAssets.FirstOrDefaultAsync(a => a.Id == id)))
                return NotFound();

            var albums = await movieDb.PhotoAlbumEntries
                .Where(e => e.PhotoAssetId == id)
                .Select(e => new { id = e.PhotoAlbum.Id, title = e.PhotoAlbum.Title, slug = e.PhotoAlbum.Slug })
                .ToListAsync();
            return Json(new { albums });
        }

        /// <summary>
        /// The assets under a folder prefix, for the folder-seeded album action.
        ///
        /// <para>Recursive on purpose — "make an album from this folder" means the event, and event
        /// folders in this tree carry subfolders (a videos folder, a per-day split). Hidden assets are
        /// left out: they were curated out of browse, and seeding would quietly bring them back.</para>
        /// </summary>
        private async Task<List<int>> FolderAssetIdsAsync(string folder)
        {
            var prefix = NormalizeFolder(folder);
            var query = movieDb.PhotoAssets.Where(a => a.MissingSinceUtc == null && !a.Hidden);
            if (prefix.Length > 0) query = query.Where(a => a.Path.StartsWith(prefix));
            return await query
                .OrderBy(a => a.TakenAt).ThenBy(a => a.Path)
                .Select(a => a.Id)
                .Take(MaxBatchIds)
                .ToListAsync();
        }

        /// <summary>
        /// Appends assets to an album, skipping the ones already in it and keeping the caller's order
        /// behind whatever is already there.
        ///
        /// <para><b>Every id goes through the master-redirect first</b> (§2.6: "tags, dates and captions
        /// attach to the group master … browse surfaces collapse to masters"). Adding a non-master adds
        /// the copy the album will actually show, so a selection that happened to include two copies of
        /// one photograph makes one entry rather than one entry plus one invisible row. The number
        /// redirected is returned so the UI can say so.</para>
        /// </summary>
        private async Task<(int Added, int Redirected)> AddAssetsAsync(PhotoAlbum album, List<int> assetIds)
        {
            if (assetIds.Count == 0) return (0, 0);

            var masters = await PhotoDupeMasters.MasterMapAsync(movieDb, assetIds);
            var redirected = assetIds.Count(id => masters.TryGetValue(id, out var m) && m != id);
            assetIds = assetIds.Select(id => masters.TryGetValue(id, out var m) ? m : id).Distinct().ToList();

            var already = await movieDb.PhotoAlbumEntries
                .Where(e => e.PhotoAlbumId == album.Id)
                .Select(e => e.PhotoAssetId)
                .ToListAsync();
            var existing = new HashSet<int>(already);

            // Only real assets: an id from a stale tab must not create an entry that points at nothing.
            var real = await movieDb.PhotoAssets.Where(a => assetIds.Contains(a.Id)).Select(a => a.Id).ToListAsync();
            var realSet = new HashSet<int>(real);

            var nextSort = already.Count == 0
                ? 0
                : await movieDb.PhotoAlbumEntries.Where(e => e.PhotoAlbumId == album.Id).MaxAsync(e => e.SortOrder) + 1;

            var added = 0;
            foreach (var assetId in assetIds)
            {
                if (!realSet.Contains(assetId) || !existing.Add(assetId)) continue;
                movieDb.PhotoAlbumEntries.Add(new PhotoAlbumEntry
                {
                    PhotoAlbumId = album.Id,
                    PhotoAssetId = assetId,
                    SortOrder = nextSort++,
                });
                added++;
            }
            if (added > 0) await movieDb.SaveChangesAsync();
            return (added, redirected);
        }

        private object AlbumSummary(PhotoAlbum album) => new
        {
            id = album.Id,
            title = album.Title,
            slug = album.Slug,
            description = album.Description,
            rangeStart = album.RangeStart,
            rangeEnd = album.RangeEnd,
            sortOrder = album.SortOrder,
            coverAssetId = album.CoverAssetId,
            createdUtc = album.CreatedUtc,
            // §2.12: which index this album sits on, and — when it is a Gallery collection of one
            // person's work — whose. The page draws its eyebrow and its museum treatment from these two,
            // so they travel with every album readout rather than being re-derived per surface.
            shelf = album.Shelf.ToString(),
            artistName = album.ArtistName,
        };

        // ── People (§2.8) ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The family's people, plus the face clusters nobody has named yet.
        ///
        /// <para><b>Two lists, because they are two different things.</b> A named row is a person: it has
        /// a page, a tag count and a birth year that hints at dates (§2.7). A row with an EMPTY name is an
        /// imported Immich cluster (§2.4) — "an unnamed group of N faces" — which is a queue item, not a
        /// person, and must never appear in a picker as a nameless choice. Naming one is what links it,
        /// and that single act fans its suggestions across the library: §2.8 calls it the
        /// highest-leverage flow in the feature, and this split is what makes it a one-click flow rather
        /// than a hunt.</para>
        ///
        /// <para>Member-visible and member-editable. A shared family album whose people list only one
        /// person could edit would be one person's album (the §2.9 stance, restated for people).</para>
        /// </summary>
        [HttpGet("/API/Photos/People")]
        public async Task<IActionResult> People()
        {
            var userId = GetCurrentUserId() ?? 0;

            var rows = await movieDb.FamilyPeople
                .Select(p => new PersonRow
                {
                    Id = p.Id,
                    Name = p.Name,
                    BirthYear = p.BirthYear,
                    UserId = p.UserId,
                    ImmichPersonId = p.ImmichPersonId,
                    CreatedUtc = p.CreatedUtc,
                    Cover = p.CoverAsset,
                    // Only Manual/Confirmed count as "photos of X" (§2.8); a suggestion is a question.
                    Tagged = movieDb.PhotoPersonTags.Count(t => t.FamilyPersonId == p.Id
                        && (t.Source == PhotoTagSource.Manual || t.Source == PhotoTagSource.Confirmed)),
                    Suggested = movieDb.PhotoPersonTags.Count(t => t.FamilyPersonId == p.Id
                        && t.Source == PhotoTagSource.Suggested),
                })
                .ToListAsync();

            object Shape(PersonRow p) => new
            {
                id = p.Id,
                name = p.Name,
                birthYear = p.BirthYear,
                userId = p.UserId,
                immichLinked = p.ImmichPersonId != null,
                createdUtc = p.CreatedUtc,
                tagCount = p.Tagged,
                suggestionCount = p.Suggested,
                coverUrl = ThumbUrl(p.Cover, userId, PhotoStreamRoutes.SizeGrid),
                faceCropUrl = FaceCropUrl(p.ImmichPersonId, userId),
            };

            return Json(new
            {
                people = rows.Where(p => p.Name.Length > 0)
                    .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(Shape).ToList(),
                // Biggest cluster first: naming the group that appears in three hundred photographs is
                // worth more than naming the one that appears in two.
                unnamed = rows.Where(p => p.Name.Length == 0)
                    .OrderByDescending(p => p.Suggested).ThenBy(p => p.Id)
                    .Select(Shape).ToList(),
                immich = !string.IsNullOrWhiteSpace(config.ImmichBaseUrl),
                dataPlane = DataPlaneConfigured,
            });
        }

        /// <summary>Creates a person. Names live in rows and nowhere else (§6) — never in code, a
        /// comment, or seed data.</summary>
        [HttpPost("/API/Photos/People")]
        public async Task<IActionResult> CreatePerson([FromBody] PhotoPersonRequest request)
        {
            var name = (request?.Name ?? "").Trim();
            if (name.Length == 0) return BadRequest(new { message = "A person needs a name." });
            if (name.Length > 200) name = name.Substring(0, 200);

            var existing = await movieDb.FamilyPeople.FirstOrDefaultAsync(p => p.Name == name);
            if (existing != null) return Json(new { person = PersonSummary(existing), created = false });

            var person = new FamilyPerson
            {
                Name = name,
                BirthYear = NormalizeBirthYear(request?.BirthYear),
                CreatedUtc = DateTime.UtcNow,
            };
            movieDb.FamilyPeople.Add(person);
            await movieDb.SaveChangesAsync();
            return Json(new { person = PersonSummary(person), created = true });
        }

        /// <summary>
        /// Renames a person, sets a birth year, or picks a cover. Every field optional; only what is sent
        /// is touched.
        ///
        /// <para>This is also how an unnamed Immich cluster becomes a person: sending a name to a row
        /// whose name is empty NAMES the cluster, which is the whole point of importing it unnamed
        /// (§2.8). The suggestions already hanging off it become suggestions for that person the moment
        /// the row has a name — no rewrite of the tag rows, because they were always pointed here.</para>
        /// </summary>
        [HttpPost("/API/Photos/Person/{id}/Update")]
        public async Task<IActionResult> UpdatePerson(int id, [FromBody] PhotoPersonRequest request)
        {
            var person = await movieDb.FamilyPeople.FirstOrDefaultAsync(p => p.Id == id);
            if (person == null) return NotFound();
            if (request == null) return BadRequest(new { message = "Nothing to update." });

            var named = false;
            if (request.Name != null)
            {
                var name = request.Name.Trim();
                if (name.Length == 0) return BadRequest(new { message = "A person needs a name." });
                named = person.Name.Length == 0;
                person.Name = name.Length > 200 ? name.Substring(0, 200) : name;
            }
            if (request.BirthYearSet) person.BirthYear = NormalizeBirthYear(request.BirthYear);
            if (request.CoverAssetId != null)
            {
                var exists = await movieDb.PhotoAssets.AnyAsync(a => a.Id == request.CoverAssetId.Value);
                if (!exists) return BadRequest(new { message = "That photo does not exist." });
                person.CoverAssetId = request.CoverAssetId.Value;
            }

            await movieDb.SaveChangesAsync();
            return Json(new { person = PersonSummary(person), named });
        }

        /// <summary>
        /// Folds one person into another — how an unnamed cluster is MAPPED onto somebody who already
        /// exists, rather than named a second time (§2.8).
        ///
        /// <para>Collisions resolve in favour of the stronger claim, so merging can never weaken a
        /// human's answer into a machine's guess or revive a refusal. The emptied row is then deleted:
        /// keeping it would leave a cluster that re-imports its own suggestions on the next sync.</para>
        /// </summary>
        [HttpPost("/API/Photos/Person/{id}/MergeInto")]
        public async Task<IActionResult> MergePerson(int id, [FromBody] PhotoPersonMergeRequest request)
        {
            if (request == null || request.IntoPersonId == id)
                return BadRequest(new { message = "Pick a different person to merge into." });

            var from = await movieDb.FamilyPeople.FirstOrDefaultAsync(p => p.Id == id);
            var into = await movieDb.FamilyPeople.FirstOrDefaultAsync(p => p.Id == request.IntoPersonId);
            if (from == null || into == null) return NotFound();

            var (moved, dropped) = await PhotoPersonTags.MergePersonAsync(movieDb, from.Id, into.Id);
            // The cluster link travels with the tags: the sidecar's next run must find its cluster
            // already answered, or it would import it again as a fresh unnamed group.
            if (from.ImmichPersonId != null && into.ImmichPersonId == null)
                into.ImmichPersonId = from.ImmichPersonId;
            movieDb.FamilyPeople.Remove(from);
            await movieDb.SaveChangesAsync();

            return Json(new { merged = true, moved, dropped, into = PersonSummary(into) });
        }

        /// <summary>
        /// Deletes a person and their tags.
        ///
        /// <para><c>confirm</c> is required for the §2.11 reason albums need it: nothing on disk is at
        /// risk — nothing in this vertical ever is — but tags are irreplaceable human labor, and a
        /// mis-click should not discard an afternoon of them.</para>
        /// </summary>
        [HttpPost("/API/Photos/Person/{id}/Delete")]
        public async Task<IActionResult> DeletePerson(int id, [FromBody] PhotoAlbumDeleteRequest request)
        {
            if (request?.Confirm != true)
                return BadRequest(new { message = "Deleting a person needs an explicit confirmation." });

            var person = await movieDb.FamilyPeople.FirstOrDefaultAsync(p => p.Id == id);
            if (person == null) return NotFound();

            var tags = await movieDb.PhotoPersonTags.Where(t => t.FamilyPersonId == id).ToListAsync();
            movieDb.PhotoPersonTags.RemoveRange(tags);
            movieDb.FamilyPeople.Remove(person);
            await movieDb.SaveChangesAsync();
            return Json(new { deleted = true, tagsRemoved = tags.Count });
        }

        /// <summary>
        /// One person's page: their counts, their date range, and the "also with…" chips — the other
        /// people who appear in the same photographs (§2.8's co-occurrence).
        /// </summary>
        [HttpGet("/API/Photos/Person/{id}")]
        public async Task<IActionResult> Person(int id)
        {
            var person = await movieDb.FamilyPeople.FirstOrDefaultAsync(p => p.Id == id);
            if (person == null) return NotFound();

            var mine = PhotoPersonTags.Affirmed(movieDb.PhotoPersonTags.Where(t => t.FamilyPersonId == id))
                .Select(t => t.PhotoAssetId);

            // Composed as a subquery on both sides: the co-occurrence answer is a GROUP BY the database
            // can do, and pulling a person's asset ids into memory to intersect them would cost the whole
            // set to draw a row of chips.
            var alsoWith = await PhotoPersonTags.Affirmed(movieDb.PhotoPersonTags)
                .Where(t => t.FamilyPersonId != id && mine.Contains(t.PhotoAssetId))
                .Where(t => t.FamilyPerson.Name != "")
                .GroupBy(t => new { t.FamilyPersonId, t.FamilyPerson.Name })
                .Select(g => new { id = g.Key.FamilyPersonId, name = g.Key.Name, count = g.Count() })
                .OrderByDescending(x => x.count).ThenBy(x => x.name)
                .Take(12)
                .ToListAsync();

            var dated = movieDb.PhotoAssets.Where(a => mine.Contains(a.Id) && a.TakenAt != null);
            var userId = GetCurrentUserId() ?? 0;
            return Json(new
            {
                person = PersonSummary(person),
                tagCount = await mine.CountAsync(),
                suggestionCount = await movieDb.PhotoPersonTags
                    .CountAsync(t => t.FamilyPersonId == id && t.Source == PhotoTagSource.Suggested),
                firstTakenAt = await dated.MinAsync(a => (DateTime?)a.TakenAt),
                lastTakenAt = await dated.MaxAsync(a => (DateTime?)a.TakenAt),
                alsoWith,
                coverUrl = ThumbUrl(person.CoverAsset, userId, PhotoStreamRoutes.SizeGrid),
                faceCropUrl = FaceCropUrl(person.ImmichPersonId, userId),
                dataPlane = DataPlaneConfigured,
            });
        }

        /// <summary>
        /// Photos of one person, newest first — the same browse rules as everywhere else: collapsed
        /// duplicates out (§2.6, and the tag is on the master anyway) and hidden out unless an admin
        /// asked (the Phase 4 rule).
        /// </summary>
        [HttpGet("/API/Photos/Person/{id}/Timeline")]
        public async Task<IActionResult> PersonTimeline(int id, int skip = 0, int take = DefaultTake,
            bool includeHidden = false)
        {
            take = Math.Clamp(take, 1, MaxTake);
            skip = Math.Max(0, skip);
            includeHidden = ShowHidden(includeHidden);

            if (!await movieDb.FamilyPeople.AnyAsync(p => p.Id == id)) return NotFound();

            var tagged = PhotoPersonTags.Affirmed(movieDb.PhotoPersonTags.Where(t => t.FamilyPersonId == id))
                .Select(t => t.PhotoAssetId);
            var all = movieDb.PhotoAssets.Where(a => a.MissingSinceUtc == null && tagged.Contains(a.Id));
            // §2.12: a person page is a page of the family record, so the Gallery shelf is out of it —
            // somebody tagged in a meme is not thereby part of that afternoon.
            var query = TimelineShelf(all);
            if (!includeHidden) { query = query.Where(a => !a.Hidden); all = all.Where(a => !a.Hidden); }
            var collapsed = PhotoDupeMasters.CollapsedAssetIds(movieDb);
            query = query.Where(a => !collapsed.Contains(a.Id));

            // …but it is REPORTED rather than silent. An exclusion nobody can see is indistinguishable
            // from data loss, and this one has no checkbox to reveal it (see TimelineShelf). The chip
            // says how many of this person's photographs are on the other shelf, so the page is honest
            // about being a filtered view of what it knows. Counted only when there is something to say.
            var archived = await all.Where(a => a.Shelf == PhotoShelf.Archive)
                .Where(a => !collapsed.Contains(a.Id))
                .CountAsync();

            var total = await query.CountAsync();
            // Undated photographs of a person belong on the page, at the end, rather than on a separate
            // shelf: a person page is small enough to read whole, which the timeline is not.
            var rows = await query
                .OrderByDescending(a => a.TakenAt == null ? 0 : 1)
                .ThenByDescending(a => a.TakenAt).ThenByDescending(a => a.Id)
                .Skip(skip).Take(take).ToListAsync();

            var userId = GetCurrentUserId() ?? 0;
            var badges = await BadgesAsync(rows);
            return Json(new
            {
                items = rows.Select(a => Card(a, userId, badges)).ToList(),
                total,
                skip,
                hasMore = skip + rows.Count < total,
                includeHidden,
                // §2.12: "N archived" — the count-chip that keeps the shelf exclusion from being a
                // silent one. Zero for almost every person, which is why the UI draws it only when set.
                archived,
                dataPlane = DataPlaneConfigured,
            });
        }

        // ── Tagging (§2.8) ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Who is in this photo — read from the MASTER, because that is where the tags are (§2.6).
        ///
        /// <para>Each row carries the birth-year hint the date editor shows (§2.7): a tagged subject born
        /// in year N means the photograph is not older than N. A HINT, surfaced to the human, never an
        /// automatic write — the editor prints it beside the field and lets a person decide.</para>
        /// </summary>
        [HttpGet("/API/Photos/Asset/{id}/Tags")]
        public async Task<IActionResult> AssetTags(int id)
        {
            var master = await PhotoDupeMasters.MasterForAsync(movieDb, id);
            var userId = GetCurrentUserId() ?? 0;

            // WHO is in a hidden photograph is the most sensitive thing about it, and the tag rows live
            // on the master — so both ends are checked, not just the id that was asked for.
            if (HiddenFromCaller(await movieDb.PhotoAssets.FirstOrDefaultAsync(a => a.Id == id))
                || HiddenFromCaller(await movieDb.PhotoAssets.FirstOrDefaultAsync(a => a.Id == master)))
                return NotFound();

            var tags = await movieDb.PhotoPersonTags
                .Where(t => t.PhotoAssetId == master && t.Source != PhotoTagSource.Rejected)
                .Select(t => new
                {
                    t.Id, t.FamilyPersonId, t.FamilyPerson.Name, t.FamilyPerson.BirthYear,
                    t.FamilyPerson.ImmichPersonId, t.Source, t.Confidence,
                    t.BoxX, t.BoxY, t.BoxW, t.BoxH,
                })
                .ToListAsync();

            return Json(new
            {
                assetId = id,
                // Said out loud whenever it happened: a member who tagged the copy in front of them is
                // owed the reason their tag appears on a different row (§2.6).
                masterAssetId = master,
                redirected = master != id,
                tags = tags
                    .OrderByDescending(t => PhotoPersonTags.IsAffirmed(t.Source) ? 1 : 0)
                    .ThenByDescending(t => t.Confidence ?? 0)
                    .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(t => TagView(t.Id, t.FamilyPersonId, t.Name, t.BirthYear, t.ImmichPersonId,
                        t.Source, t.Confidence, t.BoxX, t.BoxY, t.BoxW, t.BoxH, userId))
                    .ToList(),
                // The §2.7 hint, computed once for the whole photo: the LATEST birth year among the
                // people in it is the earliest the picture can be.
                earliestYearHint = tags
                    .Where(t => PhotoPersonTags.IsAffirmed(t.Source) && t.BirthYear != null)
                    .Select(t => t.BirthYear!.Value)
                    .DefaultIfEmpty(0)
                    .Max(),
            });
        }

        /// <summary>
        /// Tags one photo or a whole selection with one person (§2.8) — the same endpoint for the
        /// lightbox picker and the batch action, because they are the same write.
        ///
        /// <para><b>Every id goes through the master-redirect</b> (<see cref="PhotoPersonTags"/>), so a
        /// selection that happened to include two copies of one photograph makes one tag rather than one
        /// tag plus one invisible row. A name may be sent instead of an id, which creates the person —
        /// the type-ahead's "add …" is one round trip, not two.</para>
        /// </summary>
        [HttpPost("/API/Photos/Tags/Add")]
        public async Task<IActionResult> AddTags([FromBody] PhotoTagRequest request)
        {
            var ids = request?.AssetIds?.Distinct().Take(MaxBatchIds).ToList() ?? new List<int>();
            if (ids.Count == 0) return BadRequest(new { message = "No photos selected." });

            var person = await ResolvePersonAsync(request!);
            if (person == null) return BadRequest(new { message = "Pick a person, or type a name to add one." });

            var result = await PhotoPersonTags.AddAsync(movieDb, ids, person.Id);
            return Json(new
            {
                person = PersonSummary(person),
                added = result.Added,
                promoted = result.Promoted,
                unchanged = result.Unchanged,
                redirectedToMasters = result.RedirectedToMasters,
                missing = result.Missing,
            });
        }

        /// <summary>Removes a person's tag — an untag, which DELETES the row rather than leaving a
        /// tombstone: "I picked the wrong person" must not permanently bar the right one.</summary>
        [HttpPost("/API/Photos/Tags/Remove")]
        public async Task<IActionResult> RemoveTags([FromBody] PhotoTagRequest request)
        {
            var ids = request?.AssetIds?.Distinct().Take(MaxBatchIds).ToList() ?? new List<int>();
            if (ids.Count == 0 || request?.FamilyPersonId == null)
                return BadRequest(new { message = "Nothing to remove." });

            var removed = await PhotoPersonTags.RemoveAsync(movieDb, ids, request.FamilyPersonId.Value);
            return Json(new { removed });
        }

        /// <summary>Accepts a suggestion (§2.8) — one keystroke in the queue, one row transitioning.</summary>
        [HttpPost("/API/Photos/Tag/{tagId}/Confirm")]
        public async Task<IActionResult> ConfirmTag(int tagId)
        {
            var tag = await PhotoPersonTags.ConfirmAsync(movieDb, tagId);
            if (tag == null) return NotFound();
            return Json(new { id = tag.Id, source = tag.Source.ToString(), assetId = tag.PhotoAssetId });
        }

        /// <summary>
        /// Refuses a suggestion. The row SURVIVES as a tombstone so the next <c>photos-sync-immich</c>
        /// does not propose the identical face again (§2.4) — the same stance as a rejected duplicate
        /// group, for the same reason.
        /// </summary>
        [HttpPost("/API/Photos/Tag/{tagId}/Reject")]
        public async Task<IActionResult> RejectTag(int tagId)
        {
            var tag = await PhotoPersonTags.RejectAsync(movieDb, tagId);
            if (tag == null) return NotFound();
            return Json(new { id = tag.Id, source = tag.Source.ToString(), assetId = tag.PhotoAssetId });
        }

        // ── Dates (§2.7) ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets a photograph's date by hand — an exact wall-clock <see cref="PhotoAsset.TakenAt"/>
        /// (source <see cref="TakenAtSource.Manual"/>) or a circa range (<c>YearMin</c>/<c>YearMax</c>,
        /// source <see cref="TakenAtSource.Estimated"/>), which is what a box of undated scans actually
        /// supports.
        ///
        /// <para><b>Wall-clock, no offset</b> (§2.7): the string arrives and is stored as typed. Handing
        /// it through anything timezone-aware is how "Christmas morning" lands on December 24th.</para>
        ///
        /// <para><b>A year is never written as a date.</b> Setting a range leaves <c>TakenAt</c> null —
        /// writing January 1st would pile a decade onto one day, wearing a more convincing date than the
        /// undated shelf it escaped (the Phase 1 addendum's rule, restated where a human can trigger
        /// it).</para>
        ///
        /// <para>The write redirects to the group master, exactly as tags do (§2.6).</para>
        /// </summary>
        [HttpPost("/API/Photos/Asset/{id}/Date")]
        public async Task<IActionResult> SetAssetDate(int id, [FromBody] PhotoDateRequest request)
        {
            if (request == null) return BadRequest(new { message = "Nothing to set." });

            var masterId = await PhotoDupeMasters.MasterForAsync(movieDb, id);
            var asset = await movieDb.PhotoAssets.FirstOrDefaultAsync(a => a.Id == masterId);
            // Dating a photograph you are not allowed to look at is not curation. Hiding and unhiding
            // stay open to every member (that is deliberately member work) — everything else about a
            // hidden asset does not.
            if (asset == null || HiddenFromCaller(asset)) return NotFound();

            if (request.TakenAtSet)
            {
                if (string.IsNullOrWhiteSpace(request.TakenAt))
                {
                    asset.TakenAt = null;
                    asset.TakenAtUtcRaw = null;
                    asset.TakenAtSource = TakenAtSource.Unknown;
                }
                else if (DateTime.TryParse(request.TakenAt, CultureInfo.InvariantCulture,
                             DateTimeStyles.None, out var wallClock))
                {
                    asset.TakenAt = wallClock;
                    // A hand-typed date has no UTC original; keeping a stale one would make the
                    // conversion look revisitable when there is nothing left to revisit (§2.7).
                    asset.TakenAtUtcRaw = null;
                    asset.TakenAtSource = TakenAtSource.Manual;
                }
                else
                {
                    return BadRequest(new { message = "That date could not be read." });
                }
            }

            if (request.YearsSet)
            {
                var min = NormalizeYear(request.YearMin);
                var max = NormalizeYear(request.YearMax) ?? min;
                if (min != null && max != null && max < min) (min, max) = (max, min);
                asset.YearMin = min;
                asset.YearMax = max;
                if (min != null && asset.TakenAt == null) asset.TakenAtSource = TakenAtSource.Estimated;
            }

            await movieDb.SaveChangesAsync();
            return Json(new
            {
                assetId = masterId,
                redirected = masterId != id,
                takenAt = asset.TakenAt,
                takenAtSource = asset.TakenAtSource.ToString(),
                yearMin = asset.YearMin,
                yearMax = asset.YearMax,
            });
        }

        // ── Tag queue (§2.8) ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The keyboard-first review queue (§2.8): photographs carrying a machine SUGGESTION, and
        /// photographs carrying no tag at all.
        ///
        /// <para><b>Shaped so the sidecar slots in without reshaping it.</b> The <c>suggested</c> mode
        /// reads <see cref="PhotoTagSource.Suggested"/> rows and the <c>untagged</c> mode reads assets
        /// with none — which means the queue is a working surface the day it ships, with Immich absent,
        /// and gains a second mode the day a sync runs. That is the §2.4 posture as a UI shape: manual
        /// first, suggestions as an accelerator.</para>
        ///
        /// <para>Keyset-paged by id so accepting and rejecting under the reviewer does not shift a page
        /// out from under them, and <c>remaining</c> is counted from the database so the number they are
        /// working down is real.</para>
        /// </summary>
        [HttpGet("/API/Photos/TagQueue")]
        public async Task<IActionResult> TagQueue(string mode = "suggested", int afterId = 0, int take = 24)
        {
            take = Math.Clamp(take, 1, 100);
            afterId = Math.Max(0, afterId);
            var suggested = !string.Equals(mode, "untagged", StringComparison.OrdinalIgnoreCase);

            var queue = suggested ? SuggestedQueue() : UntaggedQueue();
            var rows = await queue.Where(a => a.Id > afterId).OrderBy(a => a.Id).Take(take).ToListAsync();

            var userId = GetCurrentUserId() ?? 0;
            var badges = await BadgesAsync(rows);
            var ids = rows.Select(a => a.Id).ToList();

            // One query for the whole page's tags, never one per card.
            var tags = await movieDb.PhotoPersonTags
                .Where(t => ids.Contains(t.PhotoAssetId) && t.Source != PhotoTagSource.Rejected)
                .Select(t => new
                {
                    t.Id, t.PhotoAssetId, t.FamilyPersonId, t.FamilyPerson.Name, t.FamilyPerson.BirthYear,
                    t.FamilyPerson.ImmichPersonId, t.Source, t.Confidence, t.BoxX, t.BoxY, t.BoxW, t.BoxH,
                })
                .ToListAsync();
            var byAsset = tags.GroupBy(t => t.PhotoAssetId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var lastId = rows.Count > 0 ? rows[rows.Count - 1].Id : afterId;
            return Json(new
            {
                mode = suggested ? "suggested" : "untagged",
                items = rows.Select(a => new
                {
                    card = Card(a, userId, badges),
                    // The lightbox-sized derivative: a face box is unreadable on a 400px grid thumb, and
                    // the queue is the one surface whose whole job is looking at faces.
                    viewUrl = ThumbUrl(a, userId, PhotoStreamRoutes.SizeView),
                    tags = (byAsset.TryGetValue(a.Id, out var list) ? list : new())
                        .OrderByDescending(t => t.Source == PhotoTagSource.Suggested ? 1 : 0)
                        .ThenByDescending(t => t.Confidence ?? 0)
                        .Select(t => TagView(t.Id, t.FamilyPersonId, t.Name, t.BirthYear, t.ImmichPersonId,
                            t.Source, t.Confidence, t.BoxX, t.BoxY, t.BoxW, t.BoxH, userId))
                        .ToList(),
                }).ToList(),
                nextCursor = lastId,
                hasMore = rows.Count == take,
                remaining = await queue.CountAsync(a => a.Id > lastId),
                total = await queue.CountAsync(),
            });
        }

        /// <summary>Assets carrying at least one pending suggestion. Hidden and collapsed copies are out:
        /// the queue is about photographs a family will look at, and a suggestion on a copy nobody sees
        /// is a decision with no consequence.</summary>
        private IQueryable<PhotoAsset> SuggestedQueue()
        {
            var collapsed = PhotoDupeMasters.CollapsedAssetIds(movieDb);
            var pending = movieDb.PhotoPersonTags
                .Where(t => t.Source == PhotoTagSource.Suggested)
                .Select(t => t.PhotoAssetId);
            return movieDb.PhotoAssets
                .Where(a => a.MissingSinceUtc == null && !a.Hidden && !collapsed.Contains(a.Id))
                .Where(a => pending.Contains(a.Id));
        }

        /// <summary>Assets with no tag row at all — the manual lane, which works with no sidecar ever
        /// deployed. A Rejected tombstone does NOT count as a tag: refusing a machine's guess must not
        /// take a photograph out of the queue a human is working through.</summary>
        private IQueryable<PhotoAsset> UntaggedQueue()
        {
            var collapsed = PhotoDupeMasters.CollapsedAssetIds(movieDb);
            var anyTag = movieDb.PhotoPersonTags
                .Where(t => t.Source != PhotoTagSource.Rejected)
                .Select(t => t.PhotoAssetId);
            return movieDb.PhotoAssets
                .Where(a => a.MissingSinceUtc == null && !a.Hidden && a.Kind == PhotoAssetKind.Photo
                            && !collapsed.Contains(a.Id))
                .Where(a => !anyTag.Contains(a.Id));
        }

        // ── People/tag shaping ───────────────────────────────────────────────────────────────────

        /// <summary>The people list's projection, as a named shape rather than an anonymous one — the
        /// cover asset and the two counts are read in the same query, and the list is drawn twice (named
        /// people, unnamed clusters) from one pass over the rows.</summary>
        private sealed class PersonRow
        {
            public int Id;
            public string Name = "";
            public int? BirthYear;
            public int? UserId;
            public string? ImmichPersonId;
            public DateTime CreatedUtc;
            public PhotoAsset? Cover;
            public int Tagged;
            public int Suggested;
        }

        private object PersonSummary(FamilyPerson person) => new
        {
            id = person.Id,
            name = person.Name,
            birthYear = person.BirthYear,
            userId = person.UserId,
            coverAssetId = person.CoverAssetId,
            immichLinked = person.ImmichPersonId != null,
            createdUtc = person.CreatedUtc,
        };

        private object TagView(int tagId, int personId, string name, int? birthYear, string? immichPersonId,
            PhotoTagSource source, double? confidence, double? boxX, double? boxY, double? boxW, double? boxH,
            int userId) => new
        {
            id = tagId,
            personId,
            name,
            // An empty name is an imported cluster, not a person (§2.4). The UI says "unnamed group"
            // rather than rendering a blank chip.
            unnamed = name.Length == 0,
            birthYear,
            source = source.ToString(),
            confidence,
            // Fractions of the image, so one box is correct on the grid thumb, the view derivative, the
            // zoom copy and the original alike.
            box = boxX == null || boxY == null || boxW == null || boxH == null
                ? null
                : new { x = boxX, y = boxY, w = boxW, h = boxH },
            faceCropUrl = FaceCropUrl(immichPersonId, userId),
        };

        /// <summary>
        /// The cached face crop for a cluster, as an ordinary capability URL into the derivative cache
        /// (§2.4). Null whenever no crop has been cached — the overwhelmingly common case with no
        /// sidecar deployed — and the UI then draws the stored box over our own derivative instead.
        ///
        /// <para>Immich is never named, never proxied live, and never reachable from a browser: the
        /// bytes were fetched server-side by the sync on the host that can see it, and what crosses the
        /// wire here is a signed path into a cache the gateway already serves.</para>
        /// </summary>
        private string? FaceCropUrl(string? immichPersonId, int userId)
        {
            if (string.IsNullOrEmpty(immichPersonId) || !DataPlaneConfigured) return null;
            if (!PhotoFaceCrops.Exists(config.PhotosThumbCacheDir, immichPersonId!)) return null;

            var relative = PhotoFaceCrops.RelativePath(immichPersonId!);
            // Asset id 0: this derivative belongs to a cluster, not to one photograph. The gateway
            // confines by PATH, not by that field, so nothing rests on the number.
            return PhotoStreamRoutes.ThumbUrl(config.StreamGatewayBaseUrl!,
                Mint(userId, 0, relative, PhotoStreamRoutes.SizeGrid));
        }

        /// <summary>Finds the person a tag write is about, creating one when the caller typed a name the
        /// list did not have — the type-ahead's "add …" in one round trip.</summary>
        private async Task<FamilyPerson?> ResolvePersonAsync(PhotoTagRequest request)
        {
            if (request.FamilyPersonId != null)
                return await movieDb.FamilyPeople.FirstOrDefaultAsync(p => p.Id == request.FamilyPersonId.Value);

            var name = (request.Name ?? "").Trim();
            if (name.Length == 0) return null;
            if (name.Length > 200) name = name.Substring(0, 200);

            var existing = await movieDb.FamilyPeople.FirstOrDefaultAsync(p => p.Name == name);
            if (existing != null) return existing;

            var person = new FamilyPerson { Name = name, CreatedUtc = DateTime.UtcNow };
            movieDb.FamilyPeople.Add(person);
            await movieDb.SaveChangesAsync();
            return person;
        }

        /// <summary>A birth year outside living memory is a typo, not a fact (§2.7's hint is only worth
        /// anything if the bound is real). Out-of-range values are dropped rather than stored.</summary>
        private static int? NormalizeBirthYear(int? year) => NormalizeYear(year);

        private static int? NormalizeYear(int? year) =>
            year == null || year < 1800 || year > DateTime.UtcNow.Year + 1 ? null : year;

        // ── Dupe review (§2.6) ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// The groups waiting for a human (docs/photos-plan.md §2.6's review surface): Near groups a
        /// perceptual hash proposed, and Exact groups that are already auto-mastered but still listed for
        /// confirmation.
        ///
        /// <para><b>Variant groups are never offered here.</b> "One capture, several files by design …
        /// these need no human review and must never be offered for 'pick the better copy'" — asking
        /// which of a RAW and its JPEG is the better copy is a question with no answer, and answering it
        /// wrongly would collapse the half a browser can show.</para>
        ///
        /// <para>Member-visible, like the rest of curation: any family member may settle a duplicate,
        /// the same policy that lets any of them accept a hide batch. Nothing here is admin-only, and
        /// nothing here touches a file.</para>
        /// </summary>
        [HttpGet("/API/Photos/DupeGroups")]
        public async Task<IActionResult> DupeGroups(string status = "pending", string? kind = null, int skip = 0, int take = 20)
        {
            take = Math.Clamp(take, 1, 100);
            skip = Math.Max(0, skip);

            var wantedStatus = ParseGroupStatus(status);
            var groups = movieDb.PhotoDupeGroups.Where(g => g.Kind != PhotoDupeGroupKind.Variant);
            if (wantedStatus != null) groups = groups.Where(g => g.Status == wantedStatus.Value);
            if (Enum.TryParse<PhotoDupeGroupKind>(kind, ignoreCase: true, out var wantedKind)
                && Enum.IsDefined(typeof(PhotoDupeGroupKind), wantedKind) && wantedKind != PhotoDupeGroupKind.Variant)
                groups = groups.Where(g => g.Kind == wantedKind);

            var total = await groups.CountAsync();
            // Oldest first: a review queue is worked from the front, and a stable order is what lets the
            // keyboard next/prev walk mean anything between two page loads.
            var page = await groups.OrderBy(g => g.Id).Skip(skip).Take(take)
                .Select(g => new { g.Id, g.Kind, g.Status, g.CreatedUtc, g.ResolvedUtc })
                .ToListAsync();

            var ids = page.Select(g => g.Id).ToList();
            var members = await movieDb.PhotoDupeMembers
                .Where(m => ids.Contains(m.PhotoDupeGroupId))
                .Select(m => new { m.PhotoDupeGroupId, m.IsMaster, m.Similarity, Asset = m.PhotoAsset })
                .ToListAsync();
            var byGroup = members.GroupBy(m => m.PhotoDupeGroupId).ToDictionary(g => g.Key, g => g.ToList());

            var userId = GetCurrentUserId() ?? 0;
            return Json(new
            {
                total,
                skip,
                hasMore = skip + page.Count < total,
                groups = page.Select(g => new
                {
                    id = g.Id,
                    kind = g.Kind.ToString(),
                    status = g.Status.ToString(),
                    createdUtc = g.CreatedUtc,
                    resolvedUtc = g.ResolvedUtc,
                    members = (byGroup.TryGetValue(g.Id, out var rows) ? rows : new())
                        // Hidden copies are not listed for a non-admin (the Phase 4 rule): a member view
                        // is the photograph's path, camera and a view-sized capability.
                        .Where(m => !HiddenFromCaller(m.Asset))
                        // Biggest first: the copy most likely to win the master pick leads, so the common
                        // case is one glance and one key press.
                        .OrderByDescending(m => (long)(m.Asset.Width ?? 0) * (m.Asset.Height ?? 0))
                        .ThenByDescending(m => m.Asset.SizeBytes)
                        .ThenBy(m => m.Asset.Id)
                        .Select(m => MemberView(m.Asset, m.IsMaster, m.Similarity, userId))
                        .ToList(),
                }).ToList(),
                dataPlane = DataPlaneConfigured,
            });
        }

        /// <summary>
        /// Settles a group: the chosen member becomes the master and the group is Resolved, which
        /// collapses every other copy out of the timeline and albums immediately (§2.6).
        ///
        /// <para>Rows and flags, as always — no file is copied, moved, renamed or deleted, and the
        /// copies that just left the timeline are all still in the folder view, on disk, untouched.</para>
        /// </summary>
        [HttpPost("/API/Photos/DupeGroup/{id}/Resolve")]
        public async Task<IActionResult> ResolveDupeGroup(int id, [FromBody] PhotoDupeResolveRequest request)
        {
            var group = await movieDb.PhotoDupeGroups
                .Include(g => g.Members)
                .FirstOrDefaultAsync(g => g.Id == id);
            if (group == null) return NotFound();
            if (group.Kind == PhotoDupeGroupKind.Variant)
                return BadRequest(new { message = "A variant pair is settled by the pass; there is no better copy to pick." });

            var master = group.Members.FirstOrDefault(m => m.PhotoAssetId == request?.MasterAssetId);
            if (master == null) return BadRequest(new { message = "The master must be one of the group's copies." });

            // The old flag is cleared in its own round trip: IX_PhotoDupeMember_Master is a filtered
            // UNIQUE index, and two masters for an instant is not a state it permits.
            foreach (var member in group.Members) member.IsMaster = false;
            await movieDb.SaveChangesAsync();

            master.IsMaster = true;
            group.Status = PhotoDupeGroupStatus.Resolved;
            group.ResolvedUtc = DateTime.UtcNow;
            group.ResolvedByUserId = GetCurrentUserId();
            await movieDb.SaveChangesAsync();

            return Json(new
            {
                id = group.Id,
                status = group.Status.ToString(),
                masterAssetId = master.PhotoAssetId,
                collapsed = group.Members.Count - 1,
            });
        }

        /// <summary>
        /// Records "these are not the same photo".
        ///
        /// <para>The group survives as a TOMBSTONE rather than being deleted, because the grouping pass
        /// checks it before proposing: without the row, the next run would re-propose the same pair, and
        /// a review queue that re-asks a question you already answered is a review queue nobody
        /// opens.</para>
        /// </summary>
        [HttpPost("/API/Photos/DupeGroup/{id}/Reject")]
        public async Task<IActionResult> RejectDupeGroup(int id)
        {
            var group = await movieDb.PhotoDupeGroups
                .Include(g => g.Members)
                .FirstOrDefaultAsync(g => g.Id == id);
            if (group == null) return NotFound();
            if (group.Kind == PhotoDupeGroupKind.Variant)
                return BadRequest(new { message = "A variant pair is settled by the pass and is not offered for review." });

            group.Status = PhotoDupeGroupStatus.Rejected;
            group.ResolvedUtc = DateTime.UtcNow;
            group.ResolvedByUserId = GetCurrentUserId();
            await movieDb.SaveChangesAsync();

            return Json(new { id = group.Id, status = group.Status.ToString(), members = group.Members.Count });
        }

        private static PhotoDupeGroupStatus? ParseGroupStatus(string? status)
        {
            if (string.Equals(status, "all", StringComparison.OrdinalIgnoreCase)) return null;
            return Enum.TryParse<PhotoDupeGroupStatus>(status, ignoreCase: true, out var parsed)
                   && Enum.IsDefined(typeof(PhotoDupeGroupStatus), parsed)
                ? parsed
                : PhotoDupeGroupStatus.Pending;
        }

        /// <summary>One copy as the compare view needs it: the card, plus the facts a human actually
        /// decides on — resolution, file size, format, date, and WHICH FOLDER this copy lives in, which
        /// is the whole story of the merge-needed phone-backup folders (§2.6).</summary>
        private object MemberView(PhotoAsset a, bool isMaster, double? similarity, int userId)
        {
            var slash = a.Path.LastIndexOf('/');
            var dot = a.Path.LastIndexOf('.');
            return new
            {
                card = Card(a, userId),
                isMaster,
                similarity,
                fileName = slash < 0 ? a.Path : a.Path.Substring(slash + 1),
                folder = slash < 0 ? "" : a.Path.Substring(0, slash),
                format = dot > slash ? a.Path.Substring(dot + 1).ToUpperInvariant() : "",
                sizeBytes = a.SizeBytes,
                width = a.Width,
                height = a.Height,
                takenAt = a.TakenAt,
                takenAtSource = a.TakenAtSource.ToString(),
                cameraMake = a.CameraMake,
                cameraModel = a.CameraModel,
                hidden = a.Hidden,
                // The compare pane zooms; it needs the view derivative, not the grid thumbnail.
                viewUrl = ThumbUrl(a, userId, PhotoStreamRoutes.SizeView),
            };
        }

        /// <summary>
        /// The asset's group, for the lightbox's "other copies" (§2.6). One group — the one that
        /// explains this asset's browse behaviour — chosen the same way its badge is, so the two can
        /// never tell a viewer different stories.
        /// </summary>
        private async Task<object?> GroupDetailAsync(PhotoAsset asset, int userId)
        {
            var memberships = await movieDb.PhotoDupeMembers
                .Where(m => m.PhotoAssetId == asset.Id)
                .Select(m => new { m.PhotoDupeGroupId, m.IsMaster, m.PhotoDupeGroup.Kind, m.PhotoDupeGroup.Status })
                .ToListAsync();
            if (memberships.Count == 0) return null;

            var primary = memberships
                .OrderByDescending(m => IsCollapsing(m.Kind, m.Status) && !m.IsMaster ? 1 : 0)
                .ThenBy(m => m.PhotoDupeGroupId)
                .First();

            var members = await movieDb.PhotoDupeMembers
                .Where(m => m.PhotoDupeGroupId == primary.PhotoDupeGroupId)
                .Select(m => new { m.IsMaster, m.Similarity, Asset = m.PhotoAsset })
                .ToListAsync();

            return new
            {
                id = primary.PhotoDupeGroupId,
                kind = primary.Kind.ToString(),
                status = primary.Status.ToString(),
                isMaster = primary.IsMaster,
                // A hidden copy is not shown as an "other copy": a member view carries the path, the
                // camera and a view-sized capability, which is the whole picture by another route.
                members = members
                    .Where(m => !HiddenFromCaller(m.Asset))
                    .OrderByDescending(m => m.IsMaster ? 1 : 0)
                    .ThenBy(m => m.Asset.Id)
                    .Select(m => MemberView(m.Asset, m.IsMaster, m.Similarity, userId))
                    .ToList(),
            };
        }

        /// <summary>
        /// The dupe badge for a page of cards: which group each asset is in, how big it is, whether this
        /// copy is the master, and whether the group is what keeps it out of the timeline.
        ///
        /// <para>Two bounded queries for the whole page, never one per card. An asset can sit in groups
        /// of several kinds at once (an exact copy that is also half of a RAW pair), so the badge shown
        /// is the one that EXPLAINS the card — the group collapsing it, if any — with the lowest group id
        /// breaking ties so two loads of the same page agree.</para>
        /// </summary>
        private async Task<Dictionary<int, DupeBadge>> BadgesAsync(List<PhotoAsset> rows)
        {
            var badges = new Dictionary<int, DupeBadge>();
            if (rows.Count == 0) return badges;

            var ids = rows.Select(a => a.Id).ToList();
            var memberships = await movieDb.PhotoDupeMembers
                .Where(m => ids.Contains(m.PhotoAssetId))
                .Select(m => new { m.PhotoAssetId, m.PhotoDupeGroupId, m.IsMaster, m.PhotoDupeGroup.Kind, m.PhotoDupeGroup.Status })
                .ToListAsync();
            if (memberships.Count == 0) return badges;

            var groupIds = memberships.Select(m => m.PhotoDupeGroupId).Distinct().ToList();
            var sizes = await movieDb.PhotoDupeMembers
                .Where(m => groupIds.Contains(m.PhotoDupeGroupId))
                .GroupBy(m => m.PhotoDupeGroupId)
                .Select(g => new { id = g.Key, count = g.Count() })
                .ToListAsync();
            var sizeById = sizes.ToDictionary(x => x.id, x => x.count);

            foreach (var byAsset in memberships.GroupBy(m => m.PhotoAssetId))
            {
                var pick = byAsset
                    .OrderByDescending(m => IsCollapsing(m.Kind, m.Status) && !m.IsMaster ? 1 : 0)
                    .ThenBy(m => m.PhotoDupeGroupId)
                    .First();
                badges[byAsset.Key] = new DupeBadge
                {
                    GroupId = pick.PhotoDupeGroupId,
                    Kind = pick.Kind.ToString(),
                    Status = pick.Status.ToString(),
                    Size = sizeById.TryGetValue(pick.PhotoDupeGroupId, out var n) ? n : 0,
                    IsMaster = pick.IsMaster,
                    Collapsed = IsCollapsing(pick.Kind, pick.Status) && !pick.IsMaster,
                };
            }
            return badges;
        }

        /// <summary>Mirrors <see cref="PhotoDupeMasters.Collapsed"/> for rows already in memory. The
        /// SQL side is the authority; this is the same rule stated for the badge, and the two are
        /// deliberately adjacent so a change to one is a visible change to the other.</summary>
        private static bool IsCollapsing(PhotoDupeGroupKind kind, PhotoDupeGroupStatus status) =>
            status == PhotoDupeGroupStatus.Resolved
            || (status == PhotoDupeGroupStatus.Pending && kind == PhotoDupeGroupKind.Exact);

        private sealed class DupeBadge
        {
            public int GroupId;
            public string Kind = "";
            public string Status = "";
            public int Size;
            public bool IsMaster;
            public bool Collapsed;
        }

        // ── Admin: ingest progress (§5 Phase 1 acceptance) ───────────────────────────────────────

        /// <summary>
        /// Per-queue outstanding counts, so "drive the ingest to completion in chunks with progress
        /// visible" is answerable from the site rather than only from the console that happens to be
        /// running the CLI.
        ///
        /// <para>Admin-only ON TOP of the family gate: being in the album is not being an operator, and
        /// this readout describes the pipeline rather than the photos. Same two-part test the rest of
        /// the admin surface uses — a config-designated admin AND a password-verified session.</para>
        /// </summary>
        [HttpGet("/API/Photos/IngestStatus")]
        public async Task<IActionResult> IngestStatus()
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            var live = movieDb.PhotoAssets.Where(a => a.MissingSinceUtc == null);
            var metadataQueue = await live.CountAsync(a => a.MetadataUpdatedUtc == null && a.IngestError == null);
            var hashQueue = await live.CountAsync(a => a.HashUpdatedUtc == null && a.IngestError == null);
            var thumbQueue = await live.CountAsync(a => a.ThumbsUpdatedUtc == null && a.IngestError == null);
            var errored = await live.CountAsync(a => a.IngestError != null);
            var missing = await movieDb.PhotoAssets.CountAsync(a => a.MissingSinceUtc != null);

            var batches = await movieDb.PhotoAssets
                .Where(a => a.IngestBatch != null)
                .GroupBy(a => a.IngestBatch!)
                .Select(g => new { batch = g.Key, count = g.Count(), firstSeenUtc = g.Min(a => a.FirstSeenUtc) })
                .OrderByDescending(b => b.firstSeenUtc)
                .Take(10)
                .ToListAsync();

            var byThumbState = await live
                .GroupBy(a => a.ThumbState)
                .Select(g => new { state = g.Key, count = g.Count() })
                .ToListAsync();

            return Json(new
            {
                total = await movieDb.PhotoAssets.CountAsync(),
                queues = new { metadata = metadataQueue, hash = hashQueue, thumbs = thumbQueue },
                errored,
                missing,
                thumbStates = byThumbState.ToDictionary(x => x.state.ToString(), x => x.count),
                recentBatches = batches,
                dataPlane = DataPlaneConfigured,
                thumbCacheConfigured = !string.IsNullOrWhiteSpace(config.PhotosThumbCacheDir),
                libraryConfigured = !string.IsNullOrWhiteSpace(config.PhotosLibraryDir),
            });
        }

        // ── Video playback (§2.3) ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Starts playback of ONE family video and hands back a signed gateway URL.
        ///
        /// <para><b>The gate is the class-level policy and nothing else is needed</b> (§2.1): family
        /// membership plus a password-verified session, re-checked by ASP.NET on this request. There is
        /// deliberately no age gate — §2.1 says the album has "no age/rating logic", it is a hard
        /// member/non-member test — and no admin requirement, because watching the family's own home
        /// videos is what being a member IS.</para>
        ///
        /// <para><b>The Jellyfin item id comes from the ROW, never from the caller.</b> The browser
        /// names a <see cref="PhotoAsset"/>; this looks up what may be played for it. Accepting an item
        /// id from the body would turn a family-gated endpoint into a general-purpose media-server
        /// proxy for anyone who is in the album.</para>
        ///
        /// <para><b>An unsynced video is a 409, with a sentence explaining it</b>, not a 404 or a
        /// silent failure: the file exists, the album can see it, and the missing piece is a pipeline
        /// step the owner runs (§2.3's <c>photos-sync-jellyfin</c>). The UI shows that state on the tile
        /// too, so reaching this is the stale-tab case rather than the normal one.</para>
        /// </summary>
        [HttpPost("/API/Photos/Video/Start")]
        public async Task<IActionResult> StartVideo([FromBody] PhotoVideoStartRequest request)
        {
            if (request == null || request.AssetId <= 0) return BadRequest(new { message = "No video selected." });

            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            if (videoPlayback == null || !videoPlayback.Configured)
                return StatusCode(501, new { message = "Video playback is not configured on this server." });

            var asset = await movieDb.PhotoAssets.FirstOrDefaultAsync(a => a.Id == request.AssetId);
            // The same 404 a missing row gets: a hidden video is not playable by a non-admin, and the
            // answer must not distinguish "hidden" from "not there".
            if (asset == null || HiddenFromCaller(asset)) return NotFound(new { message = "No such item." });
            if (asset.Kind != PhotoAssetKind.Video)
                return BadRequest(new { message = "That item is not a video." });
            if (asset.MissingSinceUtc != null)
                return StatusCode(409, new { message = "This video is not on disk right now.", notSynced = false });
            if (string.IsNullOrEmpty(asset.JellyfinItemId))
                return StatusCode(409, new
                {
                    message = "This video has not been synced to the media server yet, so it cannot play. "
                              + "It is safe on disk and everything else about it works.",
                    notSynced = true,
                });

            var result = await videoPlayback.StartAsync(
                userId.Value, User.Identity?.Name, asset.JellyfinItemId!, request, HttpContext.RequestAborted);
            if (result.StatusCode != 200)
                return StatusCode(result.StatusCode, new { message = result.Message });

            return Json(new
            {
                playSessionId = result.PlaySessionId,
                url = result.Url,
                isHls = result.IsHls,
                durationTicks = result.DurationTicks,
                directPlay = result.DirectPlay,
                videoCodec = result.VideoCodec,
                // The row's own duration, from ffprobe (§2.3) — the player can show a scrubber before
                // the media server has said anything.
                durationSec = asset.DurationSec,
            });
        }

        // ── Reserved-folder-name audit (§2.3's ⚠ trap) ───────────────────────────────────────────

        /// <summary>
        /// The most recent <c>photos-sync-jellyfin</c> reserved-folder-name report: which family videos
        /// sit inside folders Jellyfin's core folder walk reserves for extras and therefore DROPS, so
        /// they can never be indexed, stamped or played.
        ///
        /// <para><b>There is no action attached to it, by design.</b> The only two remedies are a rename
        /// under the collection root — forbidden absolutely (§6) — or giving the folder its own library
        /// entry path on the Jellyfin side, which is an operator's decision about another system. This
        /// endpoint exists so the answer to "which videos will never play, and why" is a query rather
        /// than a discovery months later.</para>
        ///
        /// <para>Admin-only on top of the family gate: it describes the pipeline, not the photos.</para>
        /// </summary>
        [HttpGet("/API/Photos/JellyfinAudit")]
        public async Task<IActionResult> JellyfinAudit()
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            var batch = await movieDb.PhotoCurationBatches
                .Where(b => b.Kind == PhotoCurationBatchKind.JellyfinReserved)
                .OrderByDescending(b => b.CreatedUtc)
                .FirstOrDefaultAsync();

            var videos = await movieDb.PhotoAssets
                .CountAsync(a => a.Kind == PhotoAssetKind.Video && a.MissingSinceUtc == null);
            var synced = await movieDb.PhotoAssets
                .CountAsync(a => a.Kind == PhotoAssetKind.Video && a.MissingSinceUtc == null && a.JellyfinItemId != null);

            if (batch == null)
                return Json(new
                {
                    ran = false,
                    videos,
                    synced,
                    notSynced = videos - synced,
                    folders = Array.Empty<object>(),
                    reservedNames = PhotoJellyfinReservedFolders.Names,
                });

            var items = await movieDb.PhotoCurationBatchItems
                .Where(i => i.PhotoCurationBatchId == batch.Id)
                .Select(i => new { i.Path, i.Rule })
                .ToListAsync();

            // Grouped by the FOLDER, because one collision is one thing a human decides about however
            // many videos are inside it.
            var folders = items
                .GroupBy(i => PhotoJellyfinReservedFolders.ReservedFolder(i.Path) ?? "", StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => new
                {
                    folder = g.Key,
                    reservedName = g.First().Rule.StartsWith("jellyfin-reserved:", StringComparison.Ordinal)
                        ? g.First().Rule.Substring("jellyfin-reserved:".Length)
                        : g.First().Rule,
                    count = g.Count(),
                    samples = g.Take(5).Select(i => i.Path).ToList(),
                })
                .ToList();

            return Json(new
            {
                ran = true,
                batchId = batch.BatchId,
                createdUtc = batch.CreatedUtc,
                complete = batch.Complete,
                videos,
                synced,
                notSynced = videos - synced,
                affected = items.Count,
                folders,
                reservedNames = PhotoJellyfinReservedFolders.Names,
            });
        }

        // ── Google mesh (§2.10) ──────────────────────────────────────────────────────────────────

        /// <summary>How many Google-only items one page of the review list carries. Smaller than a
        /// timeline page: each row shows a date, a description and a decision, so it is read rather
        /// than scrolled.</summary>
        private const int GoogleTake = 60;

        /// <summary>
        /// Where the last <c>photos-google-mesh</c> run left things (§2.10): how the archive's items
        /// landed against the library, by which rung, and what Google disagrees with us about.
        ///
        /// <para><b>Member-visible, like the rest of curation.</b> Deciding that a Google-only photo is
        /// not worth keeping is ordinary family curation, not an operator action — the same stance the
        /// hide proposals and the dupe review take.</para>
        ///
        /// <para><b>The disagreement counts are the interesting half.</b> A sidecar that WON over a
        /// weaker local source was written and flagged (§2.10's flag-but-write convention); one that
        /// LOST to a camera's own stamp wrote nothing and is recorded here instead. Both are counted, by
        /// the field they were about, so a systematic problem shows up as a cluster rather than as
        /// scattered surprises months later.</para>
        /// </summary>
        [HttpGet("/API/Photos/GoogleMesh")]
        public async Task<IActionResult> GoogleMesh()
        {
            var byStatus = await movieDb.PhotoGoogleItems
                .GroupBy(i => i.Status)
                .Select(g => new { status = g.Key, count = g.Count() })
                .ToListAsync();

            var byMethod = await movieDb.PhotoGoogleItems
                .Where(i => i.MatchMethod != null)
                .GroupBy(i => i.MatchMethod!)
                .Select(g => new { method = g.Key, count = g.Count() })
                .ToListAsync();

            // Flags are a short token list per row, so they are counted in memory over the rows that
            // have any — a tiny population by construction, and the alternative is string surgery in
            // SQL that two database engines would disagree about.
            var flagRows = await movieDb.PhotoGoogleItems
                .Where(i => i.Disagreements != null)
                .Select(i => i.Disagreements!)
                .ToListAsync();
            var disagreements = flagRows
                .SelectMany(SplitFlags)
                // "takenAt:Exif" and "takenAt:Manual" are the same KIND of disagreement about different
                // local sources; the field is what a reviewer groups by, and the source is the detail.
                .GroupBy(flag => flag.Split(':')[0], StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => new { field = g.Key, count = g.Count() })
                .ToList();

            int CountOf(PhotoGoogleItemStatus status) =>
                byStatus.FirstOrDefault(s => s.status == status)?.count ?? 0;

            var pending = CountOf(PhotoGoogleItemStatus.Pending);
            return Json(new
            {
                // No rows at all means the mesh has never run here — the UI says so rather than
                // drawing an empty list that looks like "Google has nothing".
                ran = byStatus.Sum(s => s.count) > 0,
                total = byStatus.Sum(s => s.count),
                pending,
                matched = CountOf(PhotoGoogleItemStatus.Matched),
                googleOnly = CountOf(PhotoGoogleItemStatus.Unmatched),
                ignored = CountOf(PhotoGoogleItemStatus.Ignored),
                downloaded = CountOf(PhotoGoogleItemStatus.Downloaded),
                byMethod = byMethod.OrderByDescending(m => m.count).ToList(),
                disagreements,
                disagreeingItems = flagRows.Count,
                // §2.10's drain guard, surfaced: while anything is Pending the download lane refuses,
                // and a human staring at a Google-only list deserves to know the list is incomplete.
                drained = pending == 0,
            });
        }

        /// <summary>
        /// The Google-only review list (§2.10 step 4): what the archive holds and the library does not,
        /// with thumbnails generated FROM THE ARCHIVE by the mesh's thumb pass.
        ///
        /// <para>Ignored items are excluded unless asked for, because "no" is an answer and a queue that
        /// re-asks an answered question is a queue nobody opens (the Phase 4 tombstone stance).</para>
        /// </summary>
        [HttpGet("/API/Photos/GoogleOnly")]
        public async Task<IActionResult> GoogleOnly(int skip = 0, int take = GoogleTake, bool includeIgnored = false)
        {
            take = Math.Clamp(take, 1, MaxTake);
            skip = Math.Max(0, skip);
            var userId = GetCurrentUserId() ?? 0;

            var query = movieDb.PhotoGoogleItems.Where(i => includeIgnored
                ? (i.Status == PhotoGoogleItemStatus.Unmatched || i.Status == PhotoGoogleItemStatus.Ignored)
                : i.Status == PhotoGoogleItemStatus.Unmatched);

            var total = await query.CountAsync();
            // Newest first, undated last: the same shape the timeline uses, for the same reason — a
            // family reviews what it recognizes before it reviews what it cannot place.
            var page = await query
                // Undated LAST — the comment above always said so, but OrderByDescending on the
                // is-null flag sorts true (1) ahead of false (0), which put every undated Takeout item
                // at the FRONT of the queue. Written the way PersonTimeline writes it, so the two read
                // as the same rule rather than as two spellings that happen to disagree.
                .OrderBy(i => i.TakenAtUtc == null ? 1 : 0)
                .ThenByDescending(i => i.TakenAtUtc)
                .ThenBy(i => i.Id)
                .Skip(skip)
                .Take(take)
                .ToListAsync();

            var items = page.Select(i =>
            {
                var sidecar = i.SidecarJson == null ? null : PhotoGoogleSidecar.ParseJson(i.SidecarJson);
                return new
                {
                    id = i.Id,
                    fileName = i.TakeoutFileName,
                    archivePath = i.TakeoutRelativePath,
                    takenAtUtc = i.TakenAtUtc,
                    sizeBytes = i.SizeBytes,
                    description = sidecar?.Description,
                    gpsLat = sidecar?.Latitude,
                    gpsLon = sidecar?.Longitude,
                    ignored = i.Status == PhotoGoogleItemStatus.Ignored,
                    gridUrl = GoogleThumbUrl(i, userId, PhotoStreamRoutes.SizeGrid),
                    viewUrl = GoogleThumbUrl(i, userId, PhotoStreamRoutes.SizeView),
                };
            }).ToList();

            return Json(new { total, skip, take, items });
        }

        /// <summary>
        /// Mark Google-only items as ignored, or take that back. A member action: it is a decision about
        /// the family's photographs, not about the pipeline.
        ///
        /// <para>An ignored item is excluded from the download lane — which is the whole point of the
        /// flag — and keeps its row and its thumbnail so the decision is visible and reversible. Nothing
        /// here can touch a matched item: "ignore" is an answer to "shall we bring this one down", and a
        /// photo we already own was never asked.</para>
        /// </summary>
        [HttpPost("/API/Photos/GoogleItems/Ignore")]
        public async Task<IActionResult> IgnoreGoogleItems([FromBody] PhotoGoogleIgnoreRequest request)
        {
            var ids = (request?.Ids ?? new List<int>()).Distinct().Take(MaxBatchIds).ToList();
            if (ids.Count == 0) return Json(new { updated = 0 });

            var ignored = request!.Ignored;
            var rows = await movieDb.PhotoGoogleItems
                .Where(i => ids.Contains(i.Id)
                            && (i.Status == PhotoGoogleItemStatus.Unmatched || i.Status == PhotoGoogleItemStatus.Ignored))
                .ToListAsync();

            var target = ignored ? PhotoGoogleItemStatus.Ignored : PhotoGoogleItemStatus.Unmatched;
            var updated = 0;
            foreach (var row in rows.Where(r => r.Status != target))
            {
                row.Status = target;
                updated++;
            }
            if (updated > 0) await movieDb.SaveChangesAsync();

            return Json(new { updated, ignored, requested = ids.Count });
        }

        /// <summary>
        /// A capability for a Google-only item's derivative (§2.2 + §2.10). The token's asset field
        /// carries <b>0</b> — a Takeout item has no <c>PhotoAsset</c>, which is the entire reason it is
        /// on this list, and a borrowed asset id would mislead anyone who ever inspected a token (the
        /// Phase 5 video-token stance). The gateway resolves the path, not the id.
        /// </summary>
        private string? GoogleThumbUrl(PhotoGoogleItem item, int userId, string size)
        {
            if (!DataPlaneConfigured) return null;
            var relative = PhotoThumbCache.GoogleRelativePath(item.Id, PhotoGoogleMesh.GoogleThumbKey(item), size);
            return PhotoStreamRoutes.ThumbUrl(config.StreamGatewayBaseUrl!, Mint(userId, 0, relative, size));
        }

        /// <summary>Config-designated admin AND a password-verified session — the AdminController rule,
        /// restated because passwordless login makes a username alone worthless as proof.
        ///
        /// <para>Memoized for the life of the request: since <see cref="HiddenFromCaller"/> consults it
        /// per minted URL, the un-cached form would re-scan the configured admin list once per card on
        /// every page of the timeline. Claims do not change mid-request, so the answer cannot.</para>
        /// </summary>
        private bool IsCurrentUserAdmin() => isAdmin ??= ComputeIsCurrentUserAdmin();

        private bool? isAdmin;

        private bool ComputeIsCurrentUserAdmin()
        {
            if (User.FindFirst("amr")?.Value != "pwd") return false;
            var username = User.FindFirst(ClaimTypes.Name)?.Value;
            return !string.IsNullOrEmpty(username)
                && config.AdminUsernames.Any(a => string.Equals(a, username, StringComparison.OrdinalIgnoreCase));
        }

        // ── Cards + capability minting (§2.2) ────────────────────────────────────────────────────

        /// <summary>
        /// The shape every browse surface returns per asset. Dimensions are DISPLAY dimensions (the
        /// EXIF orientation is already applied at ingest), which is what lets the justified grid lay a
        /// row out before a single image has loaded.
        /// </summary>
        private object Card(PhotoAsset a, int userId, IReadOnlyDictionary<int, DupeBadge>? badges = null)
        {
            DupeBadge? badge = null;
            badges?.TryGetValue(a.Id, out badge);
            return new
            {
            id = a.Id,
            path = a.Path,
            kind = a.Kind.ToString(),
            width = a.Width,
            height = a.Height,
            takenAt = a.TakenAt,
            takenAtSource = a.TakenAtSource.ToString(),
            yearMin = a.YearMin,
            yearMax = a.YearMax,
            durationSec = a.DurationSec,
            hidden = a.Hidden,
            // §2.12: which shelf this is on. Carried on every card because the FOLDER view shows both
            // shelves and marks the archived ones — the same way it already shows collapsed duplicates
            // and marks them, and for the same reason: on the "what is actually on disk" surface an
            // absence is a mystery, whereas a badge is an explanation.
            shelf = a.Shelf.ToString(),
            originalRenderable = a.OriginalRenderable,
            // The UI renders a deterministic placeholder from this rather than an <img> that will
            // 404: videos are Phase 5, and HEIC/RAW have no derivative in this build (§2.2).
            thumbState = a.ThumbState.ToString(),
            // §2.3: a video is playable only once photos-sync-jellyfin has stamped its item id. The
            // card says which it is so the tile can draw a clear "not yet synced" state instead of a
            // play button that would fail — a dead button is the one outcome worse than no button.
            videoSynced = a.Kind == PhotoAssetKind.Video ? (bool?)(a.JellyfinItemId != null) : null,
            gridUrl = ThumbUrl(a, userId, PhotoStreamRoutes.SizeGrid),
            // The §2.6 group badge. Null for the overwhelming majority of cards, which is why it is a
            // page-wide lookup rather than a column: the folder view shows every copy and marks it,
            // and the timeline shows one and marks it as the one that stands for the rest.
            group = badge == null ? null : new
            {
                id = badge.GroupId,
                kind = badge.Kind,
                status = badge.Status,
                size = badge.Size,
                isMaster = badge.IsMaster,
                collapsed = badge.Collapsed,
            },
            };
        }

        private string? ThumbUrl(PhotoAsset? a, int userId, string size)
        {
            if (a == null || !DataPlaneConfigured) return null;
            // The last line of the hidden rule, and the one that cannot be forgotten by a surface added
            // later: a hidden asset mints NO capability for a non-admin, wherever the row came from —
            // a person's cover photo, a duplicate group's other copy, a bulk re-mint by id.
            if (HiddenFromCaller(a)) return null;
            // Only advertise a derivative the ingest actually wrote. The gateway 404s a missing thumb
            // by design, so minting for one we know is absent would turn an ingest gap into a broken
            // image instead of a placeholder.
            if (a.ThumbState != PhotoThumbState.Ready || a.ThumbKey == null) return null;
            if (!PhotoThumbCache.Has(a.ThumbVariants, size)) return null;

            var relative = PhotoThumbCache.RelativePath(a.Id, a.ThumbKey, size);
            return PhotoStreamRoutes.ThumbUrl(config.StreamGatewayBaseUrl!, Mint(userId, a.Id, relative, size));
        }

        private string? OriginalUrl(PhotoAsset a, int userId)
        {
            if (!DataPlaneConfigured || HiddenFromCaller(a)) return null;
            return PhotoStreamRoutes.OriginalUrl(config.StreamGatewayBaseUrl!,
                Mint(userId, a.Id, a.Path, PhotoStreamRoutes.SizeOriginal));
        }

        private string Mint(int userId, int assetId, string relativePath, string size) =>
            PhotoCapabilityToken.Mint(config.StreamTokenSecret!, new PhotoCapabilityToken.Payload(
                userId, assetId, relativePath, size,
                DateTimeOffset.UtcNow.Add(TokenTtl).ToUnixTimeSeconds()));

        /// <summary>The persisted raw EXIF readout, handed through as an object so the panel can render
        /// it without the browser having to parse a string embedded in JSON. Bad JSON (an older row, a
        /// truncated write) degrades to no panel rather than to a 500.</summary>
        private static object? ParseRawMetadata(string? rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return null;
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(rawJson!);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    // ── Request bodies (docs/photos-plan.md §4) ─────────────────────────────────────────────────
    // Explicit DTOs rather than loose parameters: these are POSTs from selection mode, where the
    // difference between "the field was absent" and "the field was cleared" decides whether a date
    // range is left alone or deleted.

    public class PhotoHideRequest
    {
        public List<int> Ids { get; set; } = new List<int>();

        /// <summary>True hides, false unhides. One endpoint for both, because they are the same edit.</summary>
        public bool Hidden { get; set; } = true;
    }

    public class PhotoShelfRequest
    {
        public List<int> Ids { get; set; } = new List<int>();

        /// <summary>"Timeline" or "Archive" (§2.12). One endpoint for both directions, because sending
        /// a picture to the Gallery and bringing it back are the same edit. A STRING rather than the
        /// enum so an unrecognised value is a 400 with a message instead of a silent bind to 0 — which
        /// would be the shelf that means "put it back on the family timeline".</summary>
        public string Shelf { get; set; } = "";
    }

    public class PhotoApproveBatchesRequest
    {
        /// <summary>Approve a whole display group at once — a chunked walk's markers reviewed as the one
        /// ingest they actually were.</summary>
        public string? GroupKey { get; set; }

        public List<string>? BatchIds { get; set; }
    }

    public class PhotoAlbumCreateRequest
    {
        public string? Title { get; set; }

        public string? Description { get; set; }

        /// <summary>Create-from-selection.</summary>
        public List<int>? AssetIds { get; set; }

        /// <summary>Folder-seeded album (§2.9): the folder's membership is COPIED into rows; the folder
        /// itself is never the album's identity.</summary>
        public string? FromFolder { get; set; }
    }

    public class PhotoAlbumUpdateRequest
    {
        public string? Title { get; set; }

        public string? Description { get; set; }

        public DateTime? RangeStart { get; set; }

        public DateTime? RangeEnd { get; set; }

        /// <summary>Whether <see cref="RangeStart"/> was sent at all. A null date means "clear it", which
        /// is a different instruction from "do not touch it" — and a nullable field alone cannot tell
        /// the two apart.</summary>
        public bool RangeStartSet { get; set; }

        public bool RangeEndSet { get; set; }

        public int? SortOrder { get; set; }

        public int? CoverAssetId { get; set; }

        /// <summary>§2.12: "Timeline" (the family album index) or "Archive" (the Gallery). Absent leaves
        /// it alone — a string rather than the enum so an unknown value is a 400 with a message instead
        /// of a silent bind to 0, which would quietly pull a collection back out of the Gallery.</summary>
        public string? Shelf { get; set; }

        /// <summary>§2.12: the artist, when this is an artist collection. Empty clears it.</summary>
        public string? ArtistName { get; set; }

        /// <summary>Whether <see cref="ArtistName"/> was sent at all — clearing an artist and leaving it
        /// alone are different instructions, and a nullable string cannot tell them apart.</summary>
        public bool ArtistNameSet { get; set; }
    }

    public class PhotoAlbumMembershipRequest
    {
        public List<int>? AssetIds { get; set; }

        /// <summary>Add-a-whole-folder, for the "make an album from this folder" action.</summary>
        public string? FromFolder { get; set; }
    }

    public class PhotoDupeResolveRequest
    {
        /// <summary>The copy that will represent the group everywhere (§2.6). Must be a member — a
        /// master picked from outside the group is a photo the group does not contain.</summary>
        public int MasterAssetId { get; set; }
    }

    public class PhotoAlbumDeleteRequest
    {
        /// <summary>Required. Nothing on disk is at risk — an album is rows — but it is hand-built
        /// curation (§2.11), and a mis-click should not discard an afternoon of it.</summary>
        public bool Confirm { get; set; }
    }

    // ── People + tagging + dates (docs/photos-plan.md §2.7, §2.8) ───────────────────────────────

    public class PhotoPersonRequest
    {
        /// <summary>Sent to create or rename. Sending it to a row whose name is EMPTY is what names an
        /// imported face cluster, which is the highest-leverage act in the whole feature (§2.8).</summary>
        public string? Name { get; set; }

        public int? BirthYear { get; set; }

        /// <summary>Whether <see cref="BirthYear"/> was sent at all. Null means "clear it", which is a
        /// different instruction from "do not touch it" — and a nullable field alone cannot tell the two
        /// apart.</summary>
        public bool BirthYearSet { get; set; }

        /// <summary>Optional cover: which photograph this person's face crop comes from in pickers and
        /// on their page (§2.8).</summary>
        public int? CoverAssetId { get; set; }
    }

    public class PhotoPersonMergeRequest
    {
        /// <summary>The person who survives. Used to MAP an unnamed cluster onto somebody who already
        /// exists, rather than creating a second row for the same face (§2.8).</summary>
        public int IntoPersonId { get; set; }
    }

    public class PhotoTagRequest
    {
        public List<int>? AssetIds { get; set; }

        public int? FamilyPersonId { get; set; }

        /// <summary>Alternative to the id when the type-ahead's "add …" was chosen: names the person to
        /// create. Ignored when an id is present.</summary>
        public string? Name { get; set; }
    }

    public class PhotoGoogleIgnoreRequest
    {
        /// <summary><see cref="PhotoGoogleItem"/> ids, not asset ids — these are pictures we do not
        /// hold, so there is no asset to name.</summary>
        public List<int> Ids { get; set; } = new List<int>();

        /// <summary>True ignores, false takes it back. One endpoint for both, because they are the same
        /// edit (§2.10).</summary>
        public bool Ignored { get; set; } = true;
    }

    public class PhotoDateRequest
    {
        /// <summary>Naive local wall-clock, as typed (§2.7) — <c>2011-07-04T10:30</c>. Never parsed
        /// through anything timezone-aware: EXIF has no offset, and neither does a family's memory of
        /// which morning it was.</summary>
        public string? TakenAt { get; set; }

        public bool TakenAtSet { get; set; }

        public int? YearMin { get; set; }

        public int? YearMax { get; set; }

        /// <summary>Whether the circa range was sent. A range NEVER writes <see cref="TakenAt"/>: a year
        /// is not a wall clock, and January 1st would pile a decade onto one day.</summary>
        public bool YearsSet { get; set; }
    }
}
