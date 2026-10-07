using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MovieTheater.Controllers;
using MovieTheater.Db;
using MovieTheater.Requests;
using MovieTheater.Services;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The request queue's API (2026-10-06), driven as the controller with a fake session: who may do
    /// what (member vs requester vs admin), the duplicate 409, the vote toggle, the status rules, the
    /// proposal confirm/dismiss, and the paged list envelope.
    /// </summary>
    public class RequestsControllerTests : IDisposable
    {
        private readonly string workDir = Path.Combine(Path.GetTempPath(), "mt-reqctl-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<MovieDb> options;
        private readonly MovieTheaterConfiguration config;

        public RequestsControllerTests()
        {
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + Path.Combine(workDir, "reqctl.db") + ";Pooling=False").Options;
            using (var db = new MovieDb(options))
            {
                db.Database.EnsureCreated();
                db.Users.AddRange(new User { UserID = 1, Username = "eric", PasswordHash = "x" }, new User { UserID = 2, Username = "sam" }, new User { UserID = 3, Username = "kim" });
                db.Playables.Add(new Playable { Id = 110, Kind = PlayableKind.Movie });
                db.MediaFiles.Add(new MediaFile { PlayableId = 110, Path = "dune.mkv", Role = MovieFileRole.Primary });
                db.Movies.Add(new Movie { id = 10, Title = "Dune", SimpleTitle = "Dune", ReleaseDate = new DateTime(1984, 12, 14), PlayableId = 110 });
                db.SaveChanges();
            }
            var raw = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AdminUsernames:0"] = "eric" }).Build();
            config = new MovieTheaterConfiguration(raw);
        }

        public void Dispose()
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        private (RequestsController ctl, MovieDb db) As(int userId, string name, bool pwd = false)
        {
            var db = new MovieDb(options);
            var ctl = new RequestsController(db, config, new ContentRequestMatcher(db));
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, name) };
            if (pwd) claims.Add(new Claim("amr", "pwd"));
            ctl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
            return (ctl, db);
        }

        private static T Body<T>(IActionResult r)
        {
            var ok = Assert.IsAssignableFrom<ObjectResult>(r);
            return Assert.IsType<T>(ok.Value);
        }

        private static RequestsController.CreateRequestBody Req(string section, string title, int? year = null, string? detail = null) =>
            new() { Section = section, Title = title, Year = year, Detail = detail };

        [Fact]
        public async Task A_member_files_a_request_and_the_matcher_proposes_right_away()
        {
            var (ctl, db) = As(2, "sam");
            using (db)
            {
                var dto = Body<RequestsController.RequestDto>(await ctl.Create(Req("movies", "dune", 1984), CancellationToken.None));
                Assert.Equal("open", dto.Status);
                Assert.Equal("sam", dto.RequestedBy.Username);
                Assert.NotNull(dto.Match);
                Assert.Equal(("movie", 10), (dto.Match!.Kind, dto.Match.Id));
                Assert.Equal("Dune (1984)", dto.Match.Title);
            }
        }

        [Fact]
        public async Task Filing_the_same_title_again_is_a_409_carrying_the_existing_row()
        {
            var (ctl, db) = As(2, "sam");
            using (db)
            {
                await ctl.Create(Req("boardgames", "Catan: Seafarers"), CancellationToken.None);
                var again = await ctl.Create(Req("boardgames", "catan seafarers"), CancellationToken.None);
                var conflict = Assert.IsType<ConflictObjectResult>(again);
                Assert.Contains("duplicate", conflict.Value!.ToString());
                // A different section is a different ask; so is a LONGER title that merely contains this one,
                // a different year when both are given, and a different artist/detail when both are given.
                Assert.IsType<OkObjectResult>(await ctl.Create(Req("arcade", "Catan: Seafarers"), CancellationToken.None));
                Assert.IsType<OkObjectResult>(await ctl.Create(Req("boardgames", "Catan"), CancellationToken.None));
                Assert.IsType<OkObjectResult>(await ctl.Create(Req("movies", "Dune", 1984), CancellationToken.None));
                Assert.IsType<OkObjectResult>(await ctl.Create(Req("movies", "Dune", 2021), CancellationToken.None));
                Assert.IsType<ConflictObjectResult>(await ctl.Create(Req("movies", "Dune"), CancellationToken.None));
                Assert.IsType<OkObjectResult>(await ctl.Create(Req("music", "Greatest Hits", null, "Queen"), CancellationToken.None));
                Assert.IsType<OkObjectResult>(await ctl.Create(Req("music", "Greatest Hits", null, "ABBA"), CancellationToken.None));
                Assert.IsType<ConflictObjectResult>(await ctl.Create(Req("music", "greatest hits", null, "queen"), CancellationToken.None));
                Assert.IsType<ConflictObjectResult>(await ctl.Create(Req("music", "Greatest Hits"), CancellationToken.None));
            }
        }

        [Fact]
        public async Task Create_validates_section_title_year_and_link()
        {
            var (ctl, db) = As(2, "sam");
            using (db)
            {
                Assert.IsType<BadRequestObjectResult>(await ctl.Create(Req("tv", "x"), CancellationToken.None));
                Assert.IsType<BadRequestObjectResult>(await ctl.Create(Req("movies", "   "), CancellationToken.None));
                Assert.IsType<BadRequestObjectResult>(await ctl.Create(Req("movies", "x", 1700), CancellationToken.None));
                Assert.IsType<BadRequestObjectResult>(await ctl.Create(new RequestsController.CreateRequestBody { Section = "movies", Title = "x", Link = "imdb.com/tt1" }, CancellationToken.None));
                Assert.IsType<OkObjectResult>(await ctl.Create(new RequestsController.CreateRequestBody { Section = "movies", Title = "x", Link = "https://imdb.com/tt1" }, CancellationToken.None));
            }
        }

        [Fact]
        public async Task Votes_toggle_and_count_per_user()
        {
            int id;
            var (sam, db1) = As(2, "sam");
            using (db1) id = Body<RequestsController.RequestDto>(await sam.Create(Req("music", "Sixteen Stone"), CancellationToken.None)).Id;

            // Not on your own, and a null body on the write endpoints is a 400, not a 500.
            var (samAgain, db1b) = As(2, "sam");
            using (db1b)
            {
                Assert.IsType<BadRequestObjectResult>(await samAgain.Vote(id, CancellationToken.None));
                Assert.IsType<BadRequestObjectResult>(await samAgain.Create(null!, CancellationToken.None));
                Assert.IsType<BadRequestObjectResult>(await samAgain.SetStatus(id, null!, CancellationToken.None));
            }

            var (kim, db2) = As(3, "kim");
            using (db2)
            {
                var v1 = Assert.IsType<OkObjectResult>(await kim.Vote(id, CancellationToken.None)).Value!;
                Assert.Equal(1, (int)v1.GetType().GetProperty("votes")!.GetValue(v1)!);
                Assert.True((bool)v1.GetType().GetProperty("votedByMe")!.GetValue(v1)!);
                var v2 = Assert.IsType<OkObjectResult>(await kim.Vote(id, CancellationToken.None)).Value!;
                Assert.Equal(0, (int)v2.GetType().GetProperty("votes")!.GetValue(v2)!);
            }
        }

        [Fact]
        public async Task Status_rules_member_withdraws_own_only_admin_closes_anything()
        {
            int samId, kimId;
            var (sam, db1) = As(2, "sam");
            using (db1) samId = Body<RequestsController.RequestDto>(await sam.Create(Req("movies", "Heat"), CancellationToken.None)).Id;
            var (kim, db2) = As(3, "kim");
            using (db2) kimId = Body<RequestsController.RequestDto>(await kim.Create(Req("movies", "Ronin"), CancellationToken.None)).Id;

            // Sam may withdraw his own, not Kim's, and may not mark anything fulfilled.
            var (sam2, db3) = As(2, "sam");
            using (db3)
            {
                Assert.IsType<ForbidResult>(await sam2.SetStatus(kimId, new RequestsController.StatusBody { Status = "withdrawn" }, CancellationToken.None));
                Assert.IsType<ForbidResult>(await sam2.SetStatus(samId, new RequestsController.StatusBody { Status = "fulfilled" }, CancellationToken.None));
                var w = Body<RequestsController.RequestDto>(await sam2.SetStatus(samId, new RequestsController.StatusBody { Status = "withdrawn" }, CancellationToken.None));
                Assert.Equal("withdrawn", w.Status);
                Assert.Equal("sam", w.ResolvedBy);
                var back = Body<RequestsController.RequestDto>(await sam2.SetStatus(samId, new RequestsController.StatusBody { Status = "open" }, CancellationToken.None));
                Assert.Equal("open", back.Status);
                Assert.Null(back.ResolvedUtc);
            }

            // Eric is a config admin, but only with a password-verified session.
            var (ericNoPwd, db4) = As(1, "eric");
            using (db4) Assert.IsType<ForbidResult>(await ericNoPwd.SetStatus(kimId, new RequestsController.StatusBody { Status = "declined" }, CancellationToken.None));
            var (eric, db5) = As(1, "eric", pwd: true);
            using (db5)
            {
                var d = Body<RequestsController.RequestDto>(await eric.SetStatus(kimId, new RequestsController.StatusBody { Status = "declined", Note = "Already covered." }, CancellationToken.None));
                Assert.Equal(("declined", "eric", "Already covered."), (d.Status, d.ResolvedBy, d.ResolutionNote));
            }
        }

        [Fact]
        public async Task Confirm_closes_as_fulfilled_with_the_row_and_dismiss_remembers_the_key()
        {
            int id;
            var (sam, db1) = As(2, "sam");
            using (db1) id = Body<RequestsController.RequestDto>(await sam.Create(Req("movies", "Dune"), CancellationToken.None)).Id;

            var (eric, db2) = As(1, "eric", pwd: true);
            using (db2)
            {
                var dismissed = Body<RequestsController.RequestDto>(await eric.DismissMatch(id, CancellationToken.None));
                Assert.Null(dismissed.Match);
                Assert.Equal("movie:10", db2.ContentRequests.Single(r => r.Id == id).DismissedMatchKey);
                // No proposal left to confirm.
                Assert.IsType<BadRequestObjectResult>(await eric.ConfirmMatch(id, CancellationToken.None));
                // A sweep does not re-propose the dismissed row.
                var sweep = Assert.IsType<OkObjectResult>(await eric.Sweep(0, 50, CancellationToken.None)).Value!;
                Assert.Equal(0, (int)sweep.GetType().GetProperty("proposed")!.GetValue(sweep)!);
                // Close it so the title is free to be asked for again (an open twin would be a 409).
                await eric.SetStatus(id, new RequestsController.StatusBody { Status = "declined" }, CancellationToken.None);
            }

            // A fresh request for the same film is proposed and confirmed.
            int id2;
            var (kim, db3) = As(3, "kim");
            using (db3) id2 = Body<RequestsController.RequestDto>(await kim.Create(Req("movies", "Dune", 1984), CancellationToken.None)).Id;
            var (eric2, db4) = As(1, "eric", pwd: true);
            using (db4)
            {
                var done = Body<RequestsController.RequestDto>(await eric2.ConfirmMatch(id2, CancellationToken.None));
                Assert.Equal("fulfilled", done.Status);
                Assert.Equal(("movie", 10), (done.Fulfilled!.Kind, done.Fulfilled.Id));
                Assert.Null(done.Match);
            }

            // A member may not confirm, dismiss, sweep or delete.
            var (sam2, db5) = As(2, "sam");
            using (db5)
            {
                Assert.IsType<ForbidResult>(await sam2.ConfirmMatch(id2, CancellationToken.None));
                Assert.IsType<ForbidResult>(await sam2.DismissMatch(id2, CancellationToken.None));
                Assert.IsType<ForbidResult>(await sam2.Sweep(0, 50, CancellationToken.None));
                Assert.IsType<ForbidResult>(await sam2.Delete(id2, CancellationToken.None));
            }
        }

        [Fact]
        public async Task List_filters_by_status_and_section_and_pages_newest_first()
        {
            var (sam, db1) = As(2, "sam");
            using (db1)
            {
                for (var i = 0; i < 5; i++) await sam.Create(Req("movies", "Film " + i), CancellationToken.None);
                await sam.Create(Req("music", "Record"), CancellationToken.None);
            }
            var (eric, db2) = As(1, "eric", pwd: true);
            using (db2)
            {
                var first = db2.ContentRequests.OrderBy(r => r.Id).First().Id;
                await eric.SetStatus(first, new RequestsController.StatusBody { Status = "fulfilled" }, CancellationToken.None);

                var open = Assert.IsType<OkObjectResult>(await eric.List("open", null, null, 3, CancellationToken.None)).Value!;
                var t = open.GetType();
                Assert.Equal(5, (int)t.GetProperty("totalCount")!.GetValue(open)!);
                var page = (List<RequestsController.RequestDto>)t.GetProperty("requests")!.GetValue(open)!;
                Assert.Equal(3, page.Count);
                Assert.True(page[0].Id > page[1].Id && page[1].Id > page[2].Id);
                var next = (int?)t.GetProperty("nextBeforeId")!.GetValue(open);
                Assert.Equal(page[2].Id, next);

                var rest = Assert.IsType<OkObjectResult>(await eric.List("open", null, next, 3, CancellationToken.None)).Value!;
                var rest2 = (List<RequestsController.RequestDto>)rest.GetType().GetProperty("requests")!.GetValue(rest)!;
                Assert.Equal(2, rest2.Count);
                Assert.Null((int?)rest.GetType().GetProperty("nextBeforeId")!.GetValue(rest));

                var closed = Assert.IsType<OkObjectResult>(await eric.List("closed", null, null, 200, CancellationToken.None)).Value!;
                Assert.Equal(1, (int)closed.GetType().GetProperty("totalCount")!.GetValue(closed)!);
                var music = Assert.IsType<OkObjectResult>(await eric.List("all", "music", null, 200, CancellationToken.None)).Value!;
                Assert.Equal(1, (int)music.GetType().GetProperty("totalCount")!.GetValue(music)!);
                Assert.IsType<BadRequestObjectResult>(await eric.List("open", "tv", null, 200, CancellationToken.None));

                var summary = Assert.IsType<OkObjectResult>(await eric.Summary(CancellationToken.None)).Value!;
                Assert.Equal(5, (int)summary.GetType().GetProperty("open")!.GetValue(summary)!);
                Assert.True((bool)summary.GetType().GetProperty("canResolve")!.GetValue(summary)!);
            }
        }
    }
}
