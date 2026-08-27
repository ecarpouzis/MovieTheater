using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MovieTheater.Db;
using MovieTheater.Web;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The server half of the catalog's abort law (docs/catalog.md → "The instruments"): the engine
    /// already aborts a band fetch it has scrolled past, but until the request's own
    /// <c>RequestAborted</c> reached the EF calls the pod finished the query anyway — the desktop Wall's
    /// landing left ~41 swept-past queries still executing.
    ///
    /// <para>Two claims, and they only mean something together. FIRST: a browse read handed an
    /// already-cancelled token issues NO command at all — proved by counting commands at the
    /// <see cref="DbCommandInterceptor"/>, because "it threw" would also be true of a query that ran and
    /// then noticed. SECOND: the throw that costs is not paid for in logged faults —
    /// <see cref="ClientAbortedFilter"/> closes an abandoned request as 499 while leaving a cancellation
    /// from any OTHER token (a server-side timeout, a bug) to propagate as the failure it is.</para>
    /// </summary>
    public class BrowseCancellationTests : IDisposable
    {
        private readonly string workDir = Path.Combine(Path.GetTempPath(), "mt-browse-cancel-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<MovieDb> options;
        private readonly CommandCounter counter = new();

        public BrowseCancellationTests()
        {
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>()
                .UseSqlite("Data Source=" + Path.Combine(workDir, "cancel.db"))
                .AddInterceptors(counter)
                .Options;
            using var db = new MovieDb(options);
            db.Database.EnsureCreated();
            db.Genres.Add(new Genre { Id = 1, Name = "Action" });
            db.Movies.Add(new Movie { id = 1, Title = "Alpha", SimpleTitle = "Alpha", ReleaseDate = new DateTime(1994, 5, 1) });
            db.Series.Add(new Series { Id = 100, Title = "Foxtrot", SimpleTitle = "Foxtrot", StartYear = 1998 });
            db.MovieGenres.Add(new MovieGenre { MovieID = 1, GenreId = 1 });
            db.SaveChanges();
            counter.Reset();
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        /// <summary>Counts the commands EF actually sends — the only honest reading of "it never touched the DB".</summary>
        private sealed class CommandCounter : DbCommandInterceptor
        {
            private int executed;
            public int Executed => Volatile.Read(ref executed);
            public void Reset() => Volatile.Write(ref executed, 0);

            public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
            {
                Interlocked.Increment(ref executed);
                return base.ReaderExecuting(command, eventData, result);
            }

            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref executed);
                return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
            }

            public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
            {
                Interlocked.Increment(ref executed);
                return base.ScalarExecuting(command, eventData, result);
            }

            public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref executed);
                return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
            }
        }

        [Fact]
        public async Task A_facet_count_on_an_aborted_request_issues_no_command()
        {
            using var db = new MovieDb(options);
            var mq = CatalogQueries.BaseMovies(db, 100);
            var sq = CatalogQueries.BaseSeries(db, 100);

            // Sanity: the same call with a live token DOES read, so the assertion below is not vacuous.
            await BrowseFilter.CountAsync(db, mq, sq, 0, CancellationToken.None);
            Assert.True(counter.Executed > 0);

            counter.Reset();
            using var aborted = new CancellationTokenSource();
            aborted.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BrowseFilter.CountAsync(db, mq, sq, 0, aborted.Token));
            Assert.Equal(0, counter.Executed);
        }

        [Fact]
        public async Task A_group_index_on_an_aborted_request_issues_no_command()
        {
            using var db = new MovieDb(options);
            var mq = CatalogQueries.BaseMovies(db, 100);
            var sq = CatalogQueries.BaseSeries(db, 100);
            var misc = Array.Empty<BrowseGroups.MiscLight>();

            await BrowseGroups.BuildIndexAsync(db, mq, sq, misc, "genre", null, CancellationToken.None);
            Assert.True(counter.Executed > 0);

            counter.Reset();
            using var aborted = new CancellationTokenSource();
            aborted.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => BrowseGroups.BuildIndexAsync(db, mq, sq, misc, "genre", null, aborted.Token));
            Assert.Equal(0, counter.Executed);
        }

        // ── The endpoint boundary ────────────────────────────────────────────────────────────────

        private static ExceptionContext Context(Exception ex, CancellationToken requestAborted)
        {
            var http = new DefaultHttpContext { RequestAborted = requestAborted };
            var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
            return new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = ex };
        }

        [Fact]
        public void An_abandoned_request_closes_quietly_as_499()
        {
            using var aborted = new CancellationTokenSource();
            aborted.Cancel();
            var ctx = Context(new OperationCanceledException(aborted.Token), aborted.Token);
            new ClientAbortedFilter().OnException(ctx);
            Assert.True(ctx.ExceptionHandled);
            Assert.Equal(ClientAbortedFilter.ClientClosedRequest, Assert.IsType<StatusCodeResult>(ctx.Result).StatusCode);
        }

        [Fact]
        public void A_cancellation_from_any_other_token_is_still_a_failure()
        {
            // A server-side timeout, or a bug: the caller is still there, so swallowing it would hide a
            // real fault behind a status nobody reads.
            using var elsewhere = new CancellationTokenSource();
            elsewhere.Cancel();
            var ctx = Context(new OperationCanceledException(elsewhere.Token), CancellationToken.None);
            new ClientAbortedFilter().OnException(ctx);
            Assert.False(ctx.ExceptionHandled);
            Assert.Null(ctx.Result);
        }

        [Fact]
        public void An_ordinary_exception_on_an_abandoned_request_is_still_a_failure()
        {
            using var aborted = new CancellationTokenSource();
            aborted.Cancel();
            var ctx = Context(new InvalidOperationException("the query is broken"), aborted.Token);
            new ClientAbortedFilter().OnException(ctx);
            Assert.False(ctx.ExceptionHandled);
            Assert.Null(ctx.Result);
        }

        [Fact]
        public void The_decision_is_the_pure_helper()
        {
            using var aborted = new CancellationTokenSource();
            aborted.Cancel();
            Assert.True(ClientAbortedFilter.ShouldSwallow(new OperationCanceledException(), aborted.Token));
            Assert.True(ClientAbortedFilter.ShouldSwallow(new TaskCanceledException(), aborted.Token));
            Assert.False(ClientAbortedFilter.ShouldSwallow(new OperationCanceledException(), CancellationToken.None));
            Assert.False(ClientAbortedFilter.ShouldSwallow(new Exception(), aborted.Token));
            Assert.False(ClientAbortedFilter.ShouldSwallow(null, aborted.Token));
        }
    }
}
