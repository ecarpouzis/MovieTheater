using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Models;
using MovieTheater.Web;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The lists' write rules (2026-09-04, friends' marks): who may mark whose list, a Want placed for a
    /// friend is the suggestion, provenance stamped, every change journalled.
    /// </summary>
    public class ViewingWritesTests : IDisposable
    {
        private readonly string workDir = Path.Combine(Path.GetTempPath(), "mt-viewing-writes-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<MovieDb> options;
        private static readonly DateTime Now = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

        private const int Eric = 1, Alex = 2, Jamie = 3;

        public ViewingWritesTests()
        {
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + Path.Combine(workDir, "writes.db") + ";Pooling=False").Options;
            using var db = new MovieDb(options);
            db.Database.EnsureCreated();
            db.Users.AddRange(new User { UserID = Eric, Username = "Eric" }, new User { UserID = Alex, Username = "Alex" }, new User { UserID = Jamie, Username = "Jamie" });
            db.Movies.AddRange(new Movie { id = 10, Title = "Heat", SimpleTitle = "Heat" }, new Movie { id = 11, Title = "Paddington 2", SimpleTitle = "Paddington 2" });
            db.Series.Add(new Series { Id = 100, Title = "Columbo", SimpleTitle = "Columbo" });
            db.SaveChanges();
        }

        public void Dispose()
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        private Task<ViewingWrites.Result> Apply(MovieDb db, int actor, bool pwd, int? forUser, ViewingType action, bool on, string kind = "movie", int id = 10) =>
            ViewingWrites.ApplyAsync(db, actor, pwd, forUser, kind, id, action, on, Now);

        [Fact]
        public async Task OwnMark_Creates_Stamps_Journals_AndDeletesOnUntoggle()
        {
            using var db = new MovieDb(options);
            var r = await Apply(db, Alex, pwd: false, forUser: null, ViewingType.SetWatched, on: true);
            Assert.True(r.Success);
            var row = Assert.Single(db.Viewings.Where(v => v.UserID == Alex));
            Assert.Equal(ViewingTypes.Seen, row.ViewingType);
            Assert.Equal(10, row.MovieID);
            Assert.Equal(Now, row.CreatedUtc);
            Assert.Equal(Alex, row.CreatedByUserId);

            // Idempotent: marking again adds nothing.
            await Apply(db, Alex, false, null, ViewingType.SetWatched, true);
            Assert.Single(db.Viewings);

            await Apply(db, Alex, false, null, ViewingType.SetWatched, false);
            Assert.Empty(db.Viewings);

            var events = db.ViewingEvents.OrderBy(e => e.Id).ToList();
            Assert.Equal(new[] { ViewingEvent.ActionAdded, ViewingEvent.ActionRemoved }, events.Select(e => e.Action));
            Assert.All(events, e => { Assert.Equal(Alex, e.UserId); Assert.Equal(Alex, e.ActorUserId); Assert.Equal(ViewingTypes.Seen, e.ViewingType); Assert.Equal(10, e.MovieID); Assert.Equal(ViewingEvent.SourceWeb, e.Source); });
        }

        [Fact]
        public async Task SeenOnBehalf_NeedsAPasswordVerifiedSession_AndStampsTheActor()
        {
            using var db = new MovieDb(options);
            var refused = await Apply(db, Eric, pwd: false, forUser: Alex, ViewingType.SetWatched, on: true);
            Assert.Equal(403, refused.Status);
            Assert.Empty(db.Viewings);

            var ok = await Apply(db, Eric, pwd: true, forUser: Alex, ViewingType.SetWatched, on: true, kind: "series", id: 100);
            Assert.True(ok.Success);
            var row = Assert.Single(db.Viewings);
            Assert.Equal(Alex, row.UserID);
            Assert.Equal(Eric, row.CreatedByUserId);
            Assert.Equal(100, row.SeriesId);
            Assert.Null(row.MovieID);
            var ev = Assert.Single(db.ViewingEvents);
            Assert.Equal(Alex, ev.UserId);
            Assert.Equal(Eric, ev.ActorUserId);
        }

        [Fact]
        public async Task WantOnBehalf_IsTheSuggestion_NoPasswordNeeded_OneRowWhoeverPlacesIt()
        {
            using var db = new MovieDb(options);
            Assert.Equal(400, (await Apply(db, Eric, false, 999, ViewingType.SetWantToWatch, true)).Status);

            var r1 = await Apply(db, Eric, pwd: false, forUser: Alex, ViewingType.SetWantToWatch, on: true);
            Assert.True(r1.Success);
            var row = Assert.Single(db.Viewings);
            Assert.Equal(Alex, row.UserID);
            Assert.Equal(ViewingTypes.WantToWatch, row.ViewingType);
            Assert.Equal(Eric, row.CreatedByUserId); // the placer = the suggester

            // Jamie "suggesting" the same title changes nothing: it is already on Alex's list.
            await Apply(db, Jamie, false, Alex, ViewingType.SetWantToWatch, true);
            Assert.Single(db.Viewings);
            Assert.Equal(Eric, db.Viewings.Single().CreatedByUserId);

            // The owner un-wanting it ("not interested") removes the row; the journal keeps both halves.
            await Apply(db, Alex, false, null, ViewingType.SetWantToWatch, false);
            Assert.Empty(db.Viewings);
            var events = db.ViewingEvents.OrderBy(e => e.Id).ToList();
            Assert.Equal(new[] { (ViewingEvent.ActionAdded, (int?)Eric), (ViewingEvent.ActionRemoved, (int?)Alex) }, events.Select(e => (e.Action, e.ActorUserId)));
        }

        [Fact]
        public async Task UnknownTitle_IsRefused()
        {
            using var db = new MovieDb(options);
            Assert.Equal(400, (await Apply(db, Alex, false, null, ViewingType.SetWatched, true, id: 999)).Status);
            Assert.Equal(400, (await Apply(db, Alex, false, null, ViewingType.SetWatched, true, kind: "misc", id: 1)).Status);
            Assert.Empty(db.Viewings);
        }
    }
}
