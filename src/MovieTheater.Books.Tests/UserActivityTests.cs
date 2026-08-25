using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The migrated synthetic v1 file — migrated ONCE, then handed to each test as a fresh COPY. These tests
    /// write, and a shared file would make them order-dependent; copying a finished SQLite file is far cheaper
    /// than re-running the migration per test. Everything is a throwaway file under the temp directory.
    /// </summary>
    public sealed class UserActivityFixture : IDisposable
    {
        public readonly V1Fixture V1 = new();
        private int copies;

        public UserActivityFixture()
        {
            var summary = V1.Engine(V1.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            using (var db = V1.HotDb()) db.Database.CloseConnection();   // flush the WAL before the first copy
        }

        /// <summary>A private copy of the migrated hot file, opened. Writes in one test cannot reach another.</summary>
        public BooksDb Fresh()
        {
            var path = Path.Combine(V1.WorkDir, $"hot-{Interlocked.Increment(ref copies)}.db");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(V1.HotPath + suffix)) File.Copy(V1.HotPath + suffix, path + suffix, true);
            return new BooksDb(BooksDbOptions.Hot(path));
        }

        public void Dispose() => V1.Dispose();
    }

    /// <summary>
    /// Slice 3's contract: the ONE reading-position API, the item/group marks, the shelf's progress arithmetic and
    /// the suggestions port — all against a real migrated SQLite file with the controllers instantiated directly
    /// under a fabricated principal.
    ///
    /// <para>The fixture's owner is user 1 with 5 <c>UserItemState</c> rows and 2 <c>GroupMark</c> rows: item 1
    /// finished, item 2 in progress + wanted, item 101 (a book) in progress but HIDDEN from history, item 4 wanted
    /// + favourite with a user rating of 30, item 5 wanted; series 1 read+favourite (rating 80) and series 2 read.
    /// Every assertion below is against that shape.</para>
    /// </summary>
    public class UserActivityTests : IClassFixture<UserActivityFixture>
    {
        private readonly UserActivityFixture fixture;
        public UserActivityTests(UserActivityFixture fixture) => this.fixture = fixture;

        private static ClaimsPrincipal Owner(int ceiling = 3, bool isAdmin = false) =>
            BooksIdentity.Principal(1, "owner", isAdmin, ceiling);

        private static T Bind<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
            return controller;
        }

        private static ReadingController Reading(BooksDb db, ClaimsPrincipal? user = null) =>
            Bind(new ReadingController(db), user ?? Owner());

        private static MarksController Marks(BooksDb db, ClaimsPrincipal? user = null) =>
            Bind(new MarksController(db), user ?? Owner());

        private static ShelfController Shelf(BooksDb db, ClaimsPrincipal? user = null) =>
            Bind(new ShelfController(db), user ?? Owner());

        private static SuggestionsController Suggestions(BooksDb db, ClaimsPrincipal? user = null) =>
            Bind(new SuggestionsController(db, new MemoryCache(new MemoryCacheOptions { SizeLimit = 50 })), user ?? Owner());

        private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

        /// <summary>A detached JSON value, so a tri-state field can carry "absent", "null" and a number.</summary>
        private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

        // ── the reading position ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_position_defaults_to_the_start_of_the_book_and_404s_only_when_the_gate_refuses()
        {
            using var db = fixture.Fresh();

            // never opened ⇒ the start-of-book default, NOT a 404: the readers have one response shape
            var unstarted = Body<ReadingPositionResult>(await Reading(db).Get(6));
            Assert.Equal(6, unstarted.ItemId);
            Assert.Equal(0, unstarted.LastPage);
            Assert.Equal("unread", unstarted.Status);
            Assert.Null(unstarted.UpdatedAt);

            var finished = Body<ReadingPositionResult>(await Reading(db).Get(1));
            Assert.Equal("finished", finished.Status);
            Assert.Equal(-1, finished.LastPage);   // as v1 stored it; new finishes store the last page

            // 404 is reserved for "the gate refuses": a non-existent item, and a book above a kid's ceiling
            Assert.IsType<NotFoundResult>(await Reading(db).Get(999999));
            Assert.IsType<NotFoundResult>(await Reading(db, Owner(ceiling: 0)).Get(101));
        }

        [Fact]
        public async Task Minus_one_is_the_only_signal_that_finishes_a_book()
        {
            using var db = fixture.Fresh();

            var reading = Body<ReadingPositionResult>(await Reading(db).Upsert(6, new UpsertPositionRequest(5, null, null)));
            Assert.Equal("inprogress", reading.Status);
            Assert.Equal(5, reading.LastPage);

            // page 0 with nothing else is "opened the cover", not progress
            Assert.Equal("unread", Body<ReadingPositionResult>(await Reading(db).Upsert(6, new UpsertPositionRequest(0, null, null))).Status);

            // reaching (or passing) the last page NEVER auto-finishes
            Assert.Equal("inprogress", Body<ReadingPositionResult>(await Reading(db).Upsert(6, new UpsertPositionRequest(31, null, null))).Status);

            var done = Body<ReadingPositionResult>(await Reading(db).Upsert(6, new UpsertPositionRequest(-1, null, null)));
            Assert.Equal("finished", done.Status);
            Assert.Equal(31, done.LastPage);   // the fixture's comics are 32 pages: -1 lands on the last one

            // an EPUB write is progress, and carries the spine position
            var epub = Body<ReadingPositionResult>(await Reading(db).Upsert(102, new UpsertPositionRequest(null, 7, 0.5)));
            Assert.Equal("inprogress", epub.Status);
            Assert.Equal(7, epub.LastSpineItemIndex);
            Assert.Equal(0.5, epub.LastScrollPercent);
        }

        [Fact]
        public async Task Any_write_clears_hidden_from_history()
        {
            using var db = fixture.Fresh();

            Assert.True(await db.UserItemStates.AsNoTracking().AnyAsync(s => s.ItemId == 101 && s.HiddenFromHistory));
            Assert.DoesNotContain(Body<HistoryPage>(await Reading(db).History()).Entries, e => e.ItemId == 101);

            var written = Body<ReadingPositionResult>(await Reading(db).Upsert(101, new UpsertPositionRequest(null, 6, 0.4)));
            Assert.False(written.HiddenFromHistory);
            Assert.Contains(Body<HistoryPage>(await Reading(db).History()).Entries, e => e.ItemId == 101);
        }

        [Fact]
        public async Task A_touch_re_surfaces_an_existing_row_and_starts_nothing_new()
        {
            using var db = fixture.Fresh();

            // a bodyless write on a dismissed row is the "I opened it again" signal
            Assert.IsType<NoContentResult>(await Reading(db).Hide(1));
            Assert.False(Body<ReadingPositionResult>(await Reading(db).Upsert(1, new UpsertPositionRequest(null, null, null))).HiddenFromHistory);
            Assert.Equal("finished", Body<ReadingPositionResult>(await Reading(db).Get(1)).Status);   // and it stays finished

            // on a book with no row it records nothing: opening is not activity until a position is reported
            Assert.Equal("unread", Body<ReadingPositionResult>(await Reading(db).Upsert(6, new UpsertPositionRequest(null, null, null))).Status);
            Assert.False(await db.UserItemStates.AsNoTracking().AnyAsync(s => s.ItemId == 6));
        }

        [Fact]
        public async Task History_is_newest_first_carries_the_projection_and_hides_what_was_dismissed()
        {
            using var db = fixture.Fresh();

            var page = Body<HistoryPage>(await Reading(db).History());
            // opened = in progress OR finished, minus the dismissed book 101; item 2 was touched after item 1
            Assert.Equal(2, page.TotalCount);
            Assert.Equal(new[] { 2, 1 }, page.Entries.Select(e => e.ItemId).ToArray());
            Assert.All(page.Entries, e => Assert.Equal(e.ItemId, e.Item?.Id));
            Assert.Equal("2000 AD #1", page.Entries.Single(e => e.ItemId == 1).Item!.Title);

            Assert.Equal(new[] { 1 }, Body<HistoryPage>(await Reading(db).History(status: "finished")).Entries.Select(e => e.ItemId).ToArray());

            // the shelf's ✕ is non-destructive: the row (and its Finished status) survives, it just leaves history
            Assert.IsType<NoContentResult>(await Reading(db).Hide(1));
            Assert.Equal(new[] { 2 }, Body<HistoryPage>(await Reading(db).History()).Entries.Select(e => e.ItemId).ToArray());
            Assert.Equal(ReadStatus.Finished, (await db.UserItemStates.AsNoTracking().FirstAsync(s => s.ItemId == 1)).Status);

            // paging is bounded and stable
            var first = Body<HistoryPage>(await Reading(db).History(top: 1));
            Assert.Single(first.Entries);
            Assert.Equal(1, first.TotalCount);
        }

        [Fact]
        public async Task A_position_reset_keeps_the_marks()
        {
            using var db = fixture.Fresh();

            // item 2 is wanted AND in progress: the position goes, the mark stays
            Assert.IsType<NoContentResult>(await Reading(db).Reset(2));
            var kept = await db.UserItemStates.AsNoTracking().FirstAsync(s => s.ItemId == 2);
            Assert.True(kept.WantToRead);
            Assert.Equal(0, kept.LastPage);
            Assert.Equal(ReadStatus.Unread, kept.Status);

            // item 1 carries nothing but its position, so the row goes with it
            Assert.IsType<NoContentResult>(await Reading(db).Reset(1));
            Assert.False(await db.UserItemStates.AsNoTracking().AnyAsync(s => s.ItemId == 1));
        }

        // ── marks ─────────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Item_marks_upsert_delete_and_write_a_user_rating_row()
        {
            using var db = fixture.Fresh();

            var wanted = Body<ItemMarksPage>(await Marks(db).GetItems(kind: "want"));
            Assert.Equal(new[] { 2, 4, 5 }, wanted.Entries.Select(e => e.ItemId).OrderBy(id => id).ToArray());
            Assert.Equal(30, wanted.Entries.Single(e => e.ItemId == 4).Rating);   // migrated Rating(Source=User)
            Assert.Null(wanted.Entries.Single(e => e.ItemId == 2).Rating);        // unrated is null, never 0
            Assert.All(wanted.Entries, e => Assert.Equal(e.ItemId, e.Item?.Id));

            Assert.Equal(new[] { 4 }, Body<ItemMarksPage>(await Marks(db).GetItems(kind: "favorite")).Entries.Select(e => e.ItemId).ToArray());
            Assert.Equal(new[] { 1 }, Body<ItemMarksPage>(await Marks(db).GetItems(kind: "read")).Entries.Select(e => e.ItemId).ToArray());

            // a number sets the rating; the row lands in Rating(Item, Source=User)
            var set = Body<ItemMarkResult>(await Marks(db).UpsertItem(6,
                new UpsertItemMarkRequest { WantToRead = true, Rating = Json("77") }));
            Assert.True(set.WantToRead);
            Assert.Equal(77, set.Rating);
            var row = await db.Ratings.AsNoTracking().SingleAsync(r => r.TargetKind == SubjectKind.Item && r.TargetId == 6);
            Assert.Equal(RatingSource.User, row.Source);
            Assert.Equal("user:1", row.ModelId);

            // an ABSENT rating leaves it alone; an explicit null removes the row (the whole reason it is tri-state)
            Assert.Equal(77, Body<ItemMarkResult>(await Marks(db).UpsertItem(6, new UpsertItemMarkRequest { Favorite = true })).Rating);
            Assert.Null(Body<ItemMarkResult>(await Marks(db).UpsertItem(6, new UpsertItemMarkRequest { Rating = Json("null") })).Rating);
            Assert.False(await db.Ratings.AsNoTracking().AnyAsync(r => r.TargetKind == SubjectKind.Item && r.TargetId == 6));

            // clearing every mark removes the (now empty) row; clearing "read" is refused — that is a position reset
            Assert.IsType<NoContentResult>(await Marks(db).DeleteItemMark(6, "want"));
            Assert.IsType<NoContentResult>(await Marks(db).DeleteItemMark(6, "favorite"));
            Assert.False(await db.UserItemStates.AsNoTracking().AnyAsync(s => s.ItemId == 6));
            Assert.IsType<BadRequestObjectResult>(await Marks(db).DeleteItemMark(6, "read"));
        }

        [Fact]
        public async Task Group_marks_validate_the_series_and_answer_in_batch()
        {
            using var db = fixture.Fresh();

            var groups = Body<List<GroupMarkResult>>(await Marks(db).GetGroups("series"));
            Assert.Equal(2, groups.Count);
            var dredd = groups.Single(g => g.GroupKey == "1");
            Assert.True(dredd.IsRead);
            Assert.True(dredd.IsFavorite);
            Assert.Equal(80, dredd.Rating);
            Assert.Equal("2000 AD", dredd.Label);

            // the batch shape: many keys, one round trip, keyed "{groupType}::{groupKey}" — unmarked keys are absent
            var batch = Body<Dictionary<string, GroupMarkResult>>(await Marks(db).Batch(new GroupMarkBatchRequest(
            [
                new GroupKeyRef("series", "1"), new GroupKeyRef("series", "2"),
                new GroupKeyRef("series", "3"), new GroupKeyRef("decade", "1970s"),
            ])));
            Assert.Equal(new[] { "series::1", "series::2" }, batch.Keys.OrderBy(k => k).ToArray());

            // a series key is a SeriesId and is validated: a name, or an id that is not a series, is refused
            Assert.IsType<NotFoundResult>(await Marks(db).UpsertGroup("series", "999", new UpsertGroupMarkRequest { WantToRead = true }));
            Assert.IsType<BadRequestObjectResult>(await Marks(db).UpsertGroup("series", "Batman", new UpsertGroupMarkRequest { WantToRead = true }));
            Assert.IsType<BadRequestObjectResult>(await Marks(db).UpsertGroup("nonsense", "1", new UpsertGroupMarkRequest { WantToRead = true }));

            // other group types are free-form keys
            Assert.IsType<OkObjectResult>(await Marks(db).UpsertGroup("decade", "1970s",
                new UpsertGroupMarkRequest { IsFavorite = true, Notes = Json("\"the good stuff\"") }));
            var decade = Body<List<GroupMarkResult>>(await Marks(db).GetGroups("decade")).Single();
            Assert.Equal("the good stuff", decade.Notes);
            // an empty string clears the note rather than storing one made of nothing
            Assert.IsType<OkObjectResult>(await Marks(db).UpsertGroup("decade", "1970s", new UpsertGroupMarkRequest { Notes = Json("\"\"") }));
            Assert.Null(Body<List<GroupMarkResult>>(await Marks(db).GetGroups("decade")).Single().Notes);

            Assert.IsType<NoContentResult>(await Marks(db).DeleteGroup("decade", "1970s"));
            Assert.IsType<NotFoundResult>(await Marks(db).DeleteGroup("decade", "1970s"));
        }

        [Fact]
        public async Task Marking_a_series_read_finishes_its_issues_and_says_what_is_left()
        {
            using var db = fixture.Fresh();

            var result = Assert.IsType<OkObjectResult>(await Marks(db).UpsertGroup("series", "2",
                new UpsertGroupMarkRequest { IsRead = true })).Value!;
            var marked = (int)result.GetType().GetProperty("issuesMarked")!.GetValue(result)!;
            var remaining = (int)result.GetType().GetProperty("issuesRemaining")!.GetValue(result)!;
            Assert.Equal(2, marked);       // Batman #404 and #405
            Assert.Equal(0, remaining);    // the caller drives it until this is 0

            var rows = await db.UserItemStates.AsNoTracking().Where(s => s.ItemId == 4 || s.ItemId == 5).ToListAsync();
            Assert.All(rows, r => Assert.Equal(ReadStatus.Finished, r.Status));
            Assert.All(rows, r => Assert.Equal(31, r.LastPage));
            Assert.True(rows.Single(r => r.ItemId == 4).WantToRead);   // the fan-out finishes; it does not unmark

            // re-running is idempotent: nothing is left to do
            var again = Assert.IsType<OkObjectResult>(await Marks(db).UpsertGroup("series", "2",
                new UpsertGroupMarkRequest { IsRead = true })).Value!;
            Assert.Equal(0, (int)again.GetType().GetProperty("issuesMarked")!.GetValue(again)!);
        }

        // ── the shelf ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_shelf_computes_progress_from_the_users_own_rows()
        {
            using var db = fixture.Fresh();

            var body = Assert.IsType<OkObjectResult>(await Shelf(db).GetSeries(kind: "read")).Value!;
            var cards = (List<ShelfSeriesCard>)body.GetType().GetProperty("series")!.GetValue(body)!;
            Assert.Equal(2, cards.Count);

            var dredd = cards.Single(c => c.SeriesId == 1);
            Assert.Equal("2000 AD", dredd.SeriesName);
            Assert.Equal(2, dredd.IssueCount);        // three issues exist; one is an excluded shadow duplicate
            Assert.Equal(3, dredd.SeriesIssueCount);  // the run's own published total is a different number
            Assert.Equal(1, dredd.FinishedCount);     // only #1 is finished
            Assert.Equal(1, dredd.CoverItemId);       // the first issue in reading order represents the run
            Assert.True(dredd.IsRead);
            Assert.True(dredd.IsFavorite);
            Assert.Equal(80, dredd.Rating);

            var batman = cards.Single(c => c.SeriesId == 2);
            Assert.Equal(2, batman.IssueCount);
            Assert.Equal(0, batman.FinishedCount);

            // nothing is shelved as want-to-read at the SERIES level in the fixture
            var wantBody = Assert.IsType<OkObjectResult>(await Shelf(db).GetSeries(kind: "want")).Value!;
            Assert.Empty((List<ShelfSeriesCard>)wantBody.GetType().GetProperty("series")!.GetValue(wantBody)!);

            Assert.Equal(new[] { 2 }, Body<HistoryPage>(await Shelf(db).Continue()).Entries.Select(e => e.ItemId).ToArray());
            Assert.Equal(new[] { 2, 1 }, Body<HistoryPage>(await Shelf(db).LastOpened()).Entries.Select(e => e.ItemId).ToArray());

            Assert.IsType<BadRequestObjectResult>(await Shelf(db).GetSeries(kind: "nonsense"));
        }

        // ── suggestions ───────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Suggestions_exclude_everything_the_user_has_touched()
        {
            using var db = fixture.Fresh();

            var ids = SuggestedIds(await Suggestions(db).Get(count: 10, seed: 7));
            Assert.NotEmpty(ids);
            // read / wanted / favourite / dismissed items are never suggested, and neither is a shadow duplicate
            Assert.Empty(ids.Intersect(new[] { 1, 2, 3, 4, 5, 101 }));
            // the user has engaged with series 1 and 2, so only the untouched runs can win
            var series = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id))
                .Select(i => i.SeriesId).ToListAsync();
            Assert.All(series, s => Assert.DoesNotContain(s, new int?[] { 1, 2 }));

            // the same seed replays the same shelf — the noise is for variety, not for irreproducibility
            Assert.Equal(ids, SuggestedIds(await Suggestions(db).Get(count: 10, seed: 7)));
        }

        [Fact]
        public async Task Suggestions_respect_the_maturity_ceiling()
        {
            using var db = fixture.Fresh();

            // unrestricted, the untouched runs are suggestable
            Assert.NotEmpty(SuggestedIds(await Suggestions(db, Owner(ceiling: 3)).Get(count: 10, seed: 7)));

            // Restricted accounts see nothing here, and the reason is the FAIL-SAFE half of the gate rather than
            // an age judgement: a series is visible below ceiling 3 only if it carries a KNOWN audience
            // classification at or under the ceiling. The fixture's untouched runs have no insight, so no audience
            // tag, so they are hidden — the same as they are in browse. An unclassified series is never suggested.
            Assert.Empty(SuggestedIds(await Suggestions(db, Owner(ceiling: 0)).Get(count: 10, seed: 7)));
            Assert.Empty(SuggestedIds(await Suggestions(db, Owner(ceiling: 1)).Get(count: 10, seed: 7)));
        }

        private static List<int> SuggestedIds(IActionResult result)
        {
            var body = Assert.IsType<OkObjectResult>(result).Value!;
            var items = body.GetType().GetProperty("items")!.GetValue(body)!;
            return items switch
            {
                List<Projections.ItemSummary> list => list.Select(i => i.Id).ToList(),
                _ => [],
            };
        }

        // ── the helper the browse layer will call ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_browse_helpers_answer_with_the_marked_ids_and_the_read_series()
        {
            using var db = fixture.Fresh();

            Assert.Equal(new[] { 2, 4, 5 },
                (await UserActivityQueries.MarkedItemIds(db, 1, MarkKind.WantToRead).ToListAsync()).OrderBy(id => id).ToArray());
            Assert.Equal(new[] { 4 }, await UserActivityQueries.MarkedItemIds(db, 1, MarkKind.Favorite).ToListAsync());
            Assert.Equal(new[] { 1 }, await UserActivityQueries.MarkedItemIds(db, 1, MarkKind.Read).ToListAsync());

            Assert.Equal(new[] { 1, 2 }, (await UserActivityQueries.ReadSeriesIds(db, 1)).OrderBy(id => id).ToArray());
            Assert.Empty(await UserActivityQueries.WantedSeriesIds(db, 1));

            var progress = await UserActivityQueries.SeriesProgress(db, 1, new[] { 1, 2 });
            Assert.Equal(new SeriesProgressRow(1, 2, 1, 1), progress[1]);
            Assert.Equal(0, progress[2].FinishedCount);

            // a different user shares the file and must see none of it
            Assert.Empty(await UserActivityQueries.MarkedItemIds(db, 2, MarkKind.WantToRead).ToListAsync());
        }
    }
}
