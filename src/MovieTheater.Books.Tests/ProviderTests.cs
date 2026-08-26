using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Providers;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// A fake transport. Every provider test uses one: NOTHING in this suite opens a socket, and a test that
    /// forgot to stub a URL fails loudly rather than reaching the internet.
    /// </summary>
    public sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> byFragment = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Requests = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int RateLimitsBeforeSuccess;

        public FakeHandler Reply(string urlFragment, string json)
        {
            byFragment[urlFragment] = json;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            if (RateLimitsBeforeSuccess > 0)
            {
                RateLimitsBeforeSuccess--;
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429));
            }
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));

            foreach (var (fragment, json) in byFragment)
                if (url.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    });

            throw new InvalidOperationException("unstubbed provider request: " + url);
        }
    }

    /// <summary>
    /// The provider layer: cache-first reads, the per-bucket rate gate, candidate scoring and idempotent writes.
    /// No API key is ever configured here, so the clients are OFF except where a fake handler is handed in.
    /// </summary>
    public class ProviderTests
    {
        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        private const string VolumeSearchJson = """
        { "results": [
            { "id": 796, "name": "Batman", "start_year": 1940, "count_of_issues": 715,
              "publisher": { "name": "DC Comics" }, "deck": "The Dark Knight", "description": "<p>Bats.</p>",
              "image": { "medium_url": "http://x/img.jpg" }, "site_detail_url": "http://x/vol" },
            { "id": 12345, "name": "Batman Beyond", "start_year": 1999, "count_of_issues": 24,
              "publisher": { "name": "DC Comics" } } ] }
        """;

        private const string IssuesJson = """
        { "results": [
            { "id": 5101, "volume": { "id": 796 }, "name": "Year One 1", "issue_number": "404", "cover_date": "1987-02-01" },
            { "id": 5102, "volume": { "id": 796 }, "name": "Year One 2", "issue_number": "405", "cover_date": "1987-03-01" } ] }
        """;

        // ── the cache-first store ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheResponseCacheRoundTripsThroughTheLegsFile()
        {
            using var f = Migrated();
            var cache = new ProviderCacheStore(f.LegsPath);
            Assert.True(cache.Enabled);
            Assert.Null(cache.Get(Provider.Cv, "nothing-here"));

            cache.Put(Provider.Cv, "volsearch:batman", "{\"ok\":1}");
            Assert.Equal("{\"ok\":1}", cache.Get(Provider.Cv, "volsearch:batman"));

            // A second Put for the same key REPLACES rather than duplicating (the key is the primary key).
            cache.Put(Provider.Cv, "volsearch:batman", "{\"ok\":2}");
            Assert.Equal("{\"ok\":2}", cache.Get(Provider.Cv, "volsearch:batman"));
        }

        [Fact]
        public void WithNoLegsFileTheCacheIsSimplyOffRatherThanAnError()
        {
            var cache = new ProviderCacheStore(null);
            Assert.False(cache.Enabled);
            Assert.Null(cache.Get(Provider.Cv, "anything"));
            cache.Put(Provider.Cv, "anything", "{}");   // a no-op, not a throw
        }

        // ── the ComicVine client ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task WithNoApiKeyTheClientNeverOpensASocket()
        {
            using var f = Migrated();
            var handler = new FakeHandler();
            var client = new ComicVineClient(new HttpClient(handler), new ProviderCacheStore(f.LegsPath), apiKey: null, NullLogger<ComicVineClient>.Instance, TimeSpan.Zero);

            Assert.False(client.CanFetch);
            Assert.Empty(await client.SearchVolumesAsync("Batman", "DC", 1940));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task ACachedResponseIsServedWithoutTheWire()
        {
            using var f = Migrated();
            var cache = new ProviderCacheStore(f.LegsPath);
            cache.Put(Provider.Cv, ComicVineClient.VolumeSearchKey("Batman"), VolumeSearchJson);

            var handler = new FakeHandler();
            var client = new ComicVineClient(new HttpClient(handler), cache, apiKey: "a-key", NullLogger<ComicVineClient>.Instance, TimeSpan.Zero);

            var volumes = await client.SearchVolumesAsync("Batman", "DC", 1940);
            Assert.Equal(2, volumes.Count);
            Assert.Equal("Batman", volumes[0].Name);
            Assert.Empty(handler.Requests);   // the whole point: a re-scrape spends no API budget
        }

        [Fact]
        public async Task AFetchedResponseIsWrittenBackIntoTheCache()
        {
            using var f = Migrated();
            var cache = new ProviderCacheStore(f.LegsPath);
            var handler = new FakeHandler().Reply("/search/", VolumeSearchJson);
            var client = new ComicVineClient(new HttpClient(handler), cache, apiKey: "a-key", NullLogger<ComicVineClient>.Instance, TimeSpan.Zero);

            var volumes = await client.SearchVolumesAsync("Detective Comics", null, null);
            Assert.Equal(2, volumes.Count);
            Assert.Single(handler.Requests);
            // The key it wrote is publisher/year-independent, so the next lookup of the same name is free.
            Assert.NotNull(cache.Get(Provider.Cv, ComicVineClient.VolumeSearchKey("Detective Comics")));

            await client.SearchVolumesAsync("Detective Comics", "DC", 1937);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task ARateLimitIsRetriedInClientRatherThanPersistedAsAnError()
        {
            using var f = Migrated();
            var handler = new FakeHandler().Reply("/search/", VolumeSearchJson);
            handler.RateLimitsBeforeSuccess = 1;
            // A SHORT interval, so the retry is proved without a twenty-second wall clock. The interval being
            // an instance value rather than a constant is what makes that possible.
            var client = new ComicVineClient(new HttpClient(handler), new ProviderCacheStore(f.LegsPath), "a-key",
                NullLogger<ComicVineClient>.Instance, TimeSpan.FromMilliseconds(20));

            var volumes = await client.SearchVolumesAsync("Some Series", null, null);
            Assert.Equal(2, volumes.Count);
            Assert.Equal(2, handler.Requests.Count);   // one 429, one success — and no error persisted
        }

        [Fact]
        public void EachResourceTypeGetsItsOwnRateBucket()
        {
            Assert.Equal("search", ComicVineClient.BucketFor("https://x/api/search/?q=1"));
            Assert.Equal("volume", ComicVineClient.BucketFor("https://x/api/volume/4050-796/"));
            Assert.Equal("issue", ComicVineClient.BucketFor("https://x/api/issues/?filter=volume:796"));
            Assert.Equal("other", ComicVineClient.BucketFor("https://x/api/whatever/"));
            // A bucket nobody has used owes no delay.
            Assert.Equal(TimeSpan.Zero, ComicVineClient.Delay("never-used-bucket", DateTime.UtcNow));
            Assert.Equal(TimeSpan.FromSeconds(20), ComicVineClient.DefaultBucketInterval);
        }

        [Theory]
        // An exact normalized name is 100; the year and publisher add on top and the total is capped.
        [InlineData("Batman", 1940, "DC", 100)]
        // A containment is 80, plus 10 for the exact year.
        [InlineData("Batman Beyond Forever", 1940, null, 90)]
        // Nothing in common scores nothing at all, so it can never be a match.
        [InlineData("Fantastic Four", null, null, 0)]
        public void CandidateScoringRewardsNameThenYearThenPublisher(string query, int? year, string? publisher, int expected)
        {
            var candidate = new CvVolumeDto(796, "Batman", 1940, "DC Comics", 715, null, null, null, null);
            Assert.Equal(expected, ComicVineClient.Score(query, candidate, publisher, year));
        }

        // ── the series scraper ───────────────────────────────────────────────────────────────────────────

        private static ComicVineSeriesScraper Scraper(V1Fixture f, FakeHandler handler, string? key = "a-key") =>
            new(new ComicVineClient(new HttpClient(handler), new ProviderCacheStore(f.LegsPath), key, NullLogger<ComicVineClient>.Instance, TimeSpan.Zero),
                NullLogger<ComicVineSeriesScraper>.Instance);

        [Fact]
        public async Task AClearWinnerIsMatchedAndItsVolumeIsStored()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                // Leave exactly one unresolved key so the batch is deterministic.
                await db.SeriesKeyLinks.Where(l => l.Provider == Provider.Cv).ExecuteDeleteAsync();
                await db.ComicDetails.Where(d => d.ItemId != 6).ExecuteDeleteAsync();
                (await db.ComicDetails.FirstAsync()).ParsedSeriesKey = "Batman";
                await db.SaveChangesAsync();
                await db.CvVolumes.Where(v => v.Id == 796).ExecuteDeleteAsync();
            }

            var handler = new FakeHandler().Reply("/search/", VolumeSearchJson);
            await using (var db = f.HotDb())
            {
                var r = await Scraper(f, handler).RunBatchAsync(db, 10);
                Assert.Equal(1, r.Processed);
                Assert.Equal(1, r.Matched);
            }

            await using var after = f.HotDb();
            var link = await after.SeriesKeyLinks.FirstAsync(l => l.ParsedKey == "Batman" && l.Provider == Provider.Cv);
            Assert.Equal(LinkStatus.Matched, link.Status);
            Assert.Equal(796, link.ProviderKey);
            Assert.Equal(100, link.StoredTopScore);   // the number the stale-match heuristic compares against
            var volume = await after.CvVolumes.FirstAsync(v => v.Id == 796);
            Assert.Equal("Batman", volume.Name);
            Assert.Equal("DC Comics", volume.PublisherName);
        }

        [Fact]
        public async Task ATieParksTheDecisionAsMultipleInsteadOfGuessing()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                await db.SeriesKeyLinks.Where(l => l.Provider == Provider.Cv).ExecuteDeleteAsync();
                await db.ComicDetails.Where(d => d.ItemId != 6).ExecuteDeleteAsync();
                var detail = await db.ComicDetails.FirstAsync();
                detail.ParsedSeriesKey = "Twins";
                detail.Publisher = null;
                detail.Year = null;
                await db.SaveChangesAsync();
            }

            // Two candidates whose names both merely CONTAIN the query: both score 80, so nobody wins.
            const string tie = """
            { "results": [ { "id": 1, "name": "Twins Of Evil" }, { "id": 2, "name": "Twins Reborn" } ] }
            """;
            var handler = new FakeHandler().Reply("/search/", tie);
            await using (var db = f.HotDb())
            {
                var r = await Scraper(f, handler).RunBatchAsync(db, 10);
                Assert.Equal(1, r.Multiple);
                Assert.Equal(0, r.Matched);
            }

            await using var after = f.HotDb();
            Assert.Equal(LinkStatus.Multiple, (await after.SeriesKeyLinks.FirstAsync(l => l.ParsedKey == "Twins")).Status);
        }

        [Fact]
        public async Task NoCandidateAtAllIsRecordedAsNoMatch()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                await db.SeriesKeyLinks.Where(l => l.Provider == Provider.Cv).ExecuteDeleteAsync();
                await db.ComicDetails.Where(d => d.ItemId != 6).ExecuteDeleteAsync();
                (await db.ComicDetails.FirstAsync()).ParsedSeriesKey = "Nothing Like This Exists";
                await db.SaveChangesAsync();
            }
            var handler = new FakeHandler().Reply("/search/", "{ \"results\": [] }");
            await using (var db = f.HotDb()) Assert.Equal(1, (await Scraper(f, handler).RunBatchAsync(db, 10)).NoMatch);

            await using var after = f.HotDb();
            Assert.Equal(LinkStatus.NoMatch, (await after.SeriesKeyLinks.FirstAsync(l => l.ParsedKey == "Nothing Like This Exists")).Status);
        }

        [Fact]
        public async Task TheScraperIsIdempotentBecauseASettledKeyIsNotPickedUpAgain()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                await db.SeriesKeyLinks.Where(l => l.Provider == Provider.Cv).ExecuteDeleteAsync();
                await db.ComicDetails.Where(d => d.ItemId != 6).ExecuteDeleteAsync();
                (await db.ComicDetails.FirstAsync()).ParsedSeriesKey = "Batman";
                await db.SaveChangesAsync();
            }
            var handler = new FakeHandler().Reply("/search/", VolumeSearchJson);
            await using (var db = f.HotDb()) await Scraper(f, handler).RunBatchAsync(db, 10);

            // The cursor has moved past it AND its status is settled, so a second pass has nothing to do.
            await using (var db = f.HotDb())
            {
                await db.SystemStates.Where(x => x.Key == ComicVineSeriesScraper.CursorKey).ExecuteDeleteAsync();
                Assert.Equal(0, (await Scraper(f, handler).RunBatchAsync(db, 10)).Processed);
            }
        }

        // ── the issue scraper ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task IssuesAreMatchedToItemsByNormalizedIssueNumber()
        {
            using var f = Migrated();
            var handler = new FakeHandler().Reply("/issues/", IssuesJson);
            var scraper = new ComicVineIssueScraper(
                new ComicVineClient(new HttpClient(handler), new ProviderCacheStore(f.LegsPath), "a-key", NullLogger<ComicVineClient>.Instance, TimeSpan.Zero),
                NullLogger<ComicVineIssueScraper>.Instance);

            await using (var db = f.HotDb())
            {
                // Only series 2 (Batman, cv:796) carries a volume; skip past series 1.
                await db.SystemStates.Where(x => x.Key == ComicVineIssueScraper.CursorKey).ExecuteDeleteAsync();
                await db.Series.Where(s => s.Id != 2).ExecuteUpdateAsync(u => u.SetProperty(s => s.CvVolumeId, (int?)null));
                var r = await scraper.RunBatchAsync(db, 10);
                Assert.Equal(1, r.Processed);
                Assert.Equal(2, r.Matched);   // items 4 (#404) and 5 (#405)
            }

            await using var after = f.HotDb();
            Assert.Equal(2, await after.CvIssues.CountAsync(i => i.VolumeId == 796 && i.Id >= 5101));
            var link = await after.ItemProviderLinks.FirstAsync(l => l.ItemId == 4 && l.Provider == Provider.Cv);
            Assert.Equal(LinkStatus.Matched, link.Status);
            Assert.Equal("5101", link.ProviderKey);
            Assert.Equal("796", link.SecondaryKey);
            Assert.Equal(LinkQuality.High, link.Quality);
        }

        // ── the external fallback ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void OpenLibraryOnlyAnswersOnAnUnambiguousTitleMatch()
        {
            const string json = """
            { "docs": [ { "key": "/works/OL1W", "title": "Dune", "author_name": ["Frank Herbert"],
                          "publisher": ["Chilton"], "first_publish_year": 1965, "isbn": ["0441013597"] } ] }
            """;
            var hit = ExternalWorkScraper.ParseOpenLibrary(json, "Dune");
            Assert.NotNull(hit);
            Assert.Equal("openlibrary", hit!.Provider);
            Assert.Equal("/works/OL1W", hit.ProviderKey);
            Assert.Equal(1965, hit.FirstPublishYear);
            // A title with nothing in common is NOT taken — the fallback leg fills gaps, it does not guess.
            Assert.Null(ExternalWorkScraper.ParseOpenLibrary(json, "Something Else Entirely"));
        }

        [Fact]
        public void GoogleBooksIsTheSecondOpinionAndReadsTheSameWay()
        {
            const string json = """
            { "items": [ { "id": "gb1", "volumeInfo": { "title": "Dune", "authors": ["Frank Herbert"],
                           "publisher": "Chilton", "publishedDate": "1965-08-01", "description": "Spice." } } ] }
            """;
            var hit = ExternalWorkScraper.ParseGoogleBooks(json, "Dune");
            Assert.NotNull(hit);
            Assert.Equal("googlebooks", hit!.Provider);
            Assert.Equal(1965, hit.FirstPublishYear);
            Assert.Equal("Spice.", hit.Description);
        }

        [Fact]
        public async Task TheExternalScraperTriesOpenLibraryFirstAndFallsBackToGoogleBooks()
        {
            using var f = Migrated();
            var handler = new FakeHandler()
                .Reply("openlibrary.org", "{ \"docs\": [] }")
                .Reply("googleapis.com", """
                { "items": [ { "id": "gb1", "volumeInfo": { "title": "Doppelganger", "authors": ["A"] } } ] }
                """);
            var scraper = new ExternalWorkScraper(new HttpClient(handler), new ProviderCacheStore(f.LegsPath), NullLogger<ExternalWorkScraper>.Instance);

            var hit = await scraper.SearchAsync("Doppelganger");
            Assert.NotNull(hit);
            Assert.Equal("googlebooks", hit!.Provider);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains(handler.Requests, r => r.Contains("openlibrary.org"));
        }

        // ── the leg importers ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheLocgMapImportSkipsARowNamingAnUnknownItem()
        {
            using var f = Migrated();
            var csv = Path.Combine(f.WorkDir, "map.csv");
            File.WriteAllLines(csv, new[] { "1,4686349", "999999,4686350", "garbage" });

            using var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false);
            var r = LegImporters.ImportLocgMap(hot, csv, 0, 100);
            Assert.Equal(3, r.Processed);
            Assert.Equal(1, r.Written);
            Assert.Equal(2, r.Skipped);   // an unknown item id and an unparseable line are REPORTED, not guessed

            Assert.Equal((long)LinkStatus.Manual,
                hot.Scalar<long>($"SELECT Status FROM ItemProviderLink WHERE ItemId = 1 AND Provider = {(int)Provider.Locg}"));
        }

        [Fact]
        public void TheMangaUpdatesImportLandsTheHotRowAndTheRawLists()
        {
            using var f = Migrated();
            var json = Path.Combine(f.WorkDir, "mu.json");
            File.WriteAllText(json, """
            [ { "muSeriesId": 4242, "title": "Akira", "year": 1982, "type": "Manga", "status": "Complete",
                "completed": true, "description": "Neo-Tokyo.", "bayesianRating": 9.1,
                "genres": ["Action", "Sci-fi"], "categories": ["Post-Apocalyptic"] } ]
            """);

            using (var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false))
            using (var legs = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = f.LegsPath, Pooling = false }.ToString()))
            {
                legs.Open();
                var r = LegImporters.ImportMangaUpdates(hot, legs, json, 0, 100);
                Assert.Equal(1, r.Written);
            }

            using var w = f.Hot();
            Assert.Equal("Akira", w.Scalar<string>("SELECT Title FROM MuSeries WHERE Id = 4242"));
            Assert.Equal(1, f.LegsCount("MuSeriesRaw") > 0 ? 1 : 0);
        }
    }
}
