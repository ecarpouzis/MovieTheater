using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Services.Recommendations;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The staleness contract behind the recommendation maintenance loop: a re-score edits the
    /// Viewing row in place (same ViewingID, same count), so it is invisible to the stamp — the
    /// blank-the-stamp convention and the sentinel's "blanked" component exist to cover exactly
    /// that hole. These tests pin both halves.
    /// </summary>
    public class RecommendationStalenessTests : IDisposable
    {
        private readonly string workDir = Path.Combine(Path.GetTempPath(), "mt-reco-stale-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<MovieDb> options;
        private readonly RecommendationStaleness refresher = new(algoVersion: 1);

        public RecommendationStalenessTests()
        {
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>()
                .UseSqlite("Data Source=" + Path.Combine(workDir, "reco.db"))
                .Options;
            using var db = new MovieDb(options);
            db.Database.EnsureCreated();
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { /* the OS still has it */ }
            GC.SuppressFinalize(this);
        }

        private static Viewing Rated(int userId, int movieId, string score) => new()
        {
            UserID = userId,
            MovieID = movieId,
            ViewingType = "Rated",
            ViewingData = score,
        };

        [Fact]
        public async Task BlankedStamp_MakesUserStale_WithoutAnyRowChange()
        {
            using (var db = new MovieDb(options))
            {
                db.Users.Add(new User { UserID = 1 });
                db.Movies.Add(new Movie { id = 10 });
                db.Viewings.Add(Rated(1, 10, "80"));
                await db.SaveChangesAsync();
                var maxLib = await refresher.MaxLibIdAsync(db);
                var stamp = await refresher.StampAsync(db, 1, maxLib);
                db.UserTasteProfiles.Add(new UserTasteProfile { UserId = 1, RatingsStamp = stamp, GeneratedUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            using (var db = new MovieDb(options))
            {
                Assert.Empty(await refresher.StaleUsersAsync(db));

                // The SetRatings re-score path: the row edits in place, only the stamp is blanked.
                (await db.Viewings.SingleAsync()).ViewingData = "40";
                (await db.UserTasteProfiles.SingleAsync()).RatingsStamp = "";
                await db.SaveChangesAsync();

                var stale = await refresher.StaleUsersAsync(db);
                Assert.Equal(1, Assert.Single(stale).UserId);
            }
        }

        [Fact]
        public async Task Sentinel_IsStableWhenQuiet_AndMovesOnEveryStalenessSource()
        {
            using var db = new MovieDb(options);
            db.Users.Add(new User { UserID = 1 });
            db.Movies.AddRange(new Movie { id = 10 }, new Movie { id = 11 });
            db.Viewings.Add(Rated(1, 10, "80"));
            db.UserTasteProfiles.Add(new UserTasteProfile { UserId = 1, RatingsStamp = "anything", GeneratedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var quiet1 = await refresher.SentinelAsync(db);
            var quiet2 = await refresher.SentinelAsync(db);
            Assert.Equal(quiet1, quiet2); // identical world ⇒ identical sentinel — this is what makes idle ticks skippable

            db.Viewings.Add(Rated(1, 11, "70")); // a NEW rating moves max-id and count
            await db.SaveChangesAsync();
            var afterNewRating = await refresher.SentinelAsync(db);
            Assert.NotEqual(quiet1, afterNewRating);

            (await db.UserTasteProfiles.SingleAsync()).RatingsStamp = ""; // a RE-SCORE moves neither — only the blank marks it
            await db.SaveChangesAsync();
            Assert.NotEqual(afterNewRating, await refresher.SentinelAsync(db));

            // A DELETE moves the sentinel too — remove the OLDER row (removing the newest would
            // restore the exact initial world, where an equal sentinel is the CORRECT answer).
            var firstRow = await db.Viewings.OrderBy(v => v.ViewingID).FirstAsync();
            db.Viewings.Remove(firstRow);
            (await db.UserTasteProfiles.SingleAsync()).RatingsStamp = "anything";
            await db.SaveChangesAsync();
            Assert.NotEqual(quiet1, await refresher.SentinelAsync(db));
        }
    }
}
