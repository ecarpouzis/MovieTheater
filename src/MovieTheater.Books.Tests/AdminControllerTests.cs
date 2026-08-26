using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The admin surface, its job runner, its settings overlay and its log ring — driven directly, under a
    /// fabricated admin principal, against a migrated throwaway file.
    /// </summary>
    public class AdminControllerTests
    {
        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        private static ClaimsPrincipal Admin() => BooksIdentity.Principal(1, "owner", isAdmin: true, maturityCeiling: 3);

        /// <summary>The controller with a real DbContext, a real JobRunner and a real overlay in the work dir.</summary>
        private static (AdminController Controller, ServiceProvider Provider, BooksDb Db, InMemoryLogStore Logs, BooksSettingsOverlay Settings)
            Build(V1Fixture f)
        {
            var options = new BooksOptions
            {
                DbPath = f.HotPath,
                LegsDbPath = f.LegsPath,
                CacheDir = f.CacheDir,
                SettingsOverlayPath = Path.Combine(f.WorkDir, "books.settings.json"),
                EnableCacheWarmer = false,
            };
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
            services.AddSingleton(options);
            services.AddDbContext<BooksDb>(o => BooksDbOptions.Configure(o, f.HotPath));
            services.AddMemoryCache(o => o.SizeLimit = 256);
            services.AddSingleton<JobRunner>();
            var logs = new InMemoryLogStore();
            services.AddSingleton(logs);
            var settings = new BooksSettingsOverlay(options.SettingsOverlayPath);
            services.AddSingleton(settings);
            services.AddSingleton<Archives.SevenZipCliExtractor>();
            services.AddSingleton<Archives.IArchiveReader, Archives.CbzArchiveReader>();
            services.AddSingleton<ThumbnailService>();
            services.AddSingleton<ThumbnailJob>();
            services.AddSingleton<LibraryScanner>();
            services.AddSingleton<CalibreImportService>();
            services.AddSingleton<DuplicateDetectionService>();
            services.AddSingleton<DataNormalizationService>();
            services.AddSingleton<SeriesMismatchService>();
            services.AddSingleton<SeriesNamesService>();

            var provider = services.BuildServiceProvider();
            var db = provider.GetRequiredService<BooksDb>();
            var controller = new AdminController(
                db, options,
                provider.GetRequiredService<JobRunner>(), logs, settings,
                provider.GetRequiredService<LibraryScanner>(),
                provider.GetRequiredService<ThumbnailJob>(),
                provider.GetRequiredService<ThumbnailService>(),
                provider.GetRequiredService<DuplicateDetectionService>(),
                provider.GetRequiredService<DataNormalizationService>(),
                provider.GetRequiredService<SeriesMismatchService>(),
                provider.GetRequiredService<SeriesNamesService>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Admin() } },
            };
            return (controller, provider, db, logs, settings);
        }

        private static T Value<T>(IActionResult result) where T : class =>
            (result as ObjectResult)?.Value as T ?? throw new InvalidOperationException("not an ObjectResult of " + typeof(T).Name);

        private static object Payload(IActionResult result) =>
            (result as ObjectResult)?.Value ?? throw new InvalidOperationException("not an ObjectResult");

        private static object? Read(object payload, string name) =>
            payload.GetType().GetProperty(name)?.GetValue(payload);

        // ── info & the derived registry ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task InfoReportsTheCountsAnOperatorChecksFirst()
        {
            using var f = Migrated();
            var (controller, provider, _, _, _) = Build(f);
            await using (provider)
            {
                var payload = Payload(await controller.Info(default));
                var catalog = Read(payload, "catalog")!;
                Assert.Equal(9, Convert.ToInt32(Read(catalog, "items")));
                Assert.Equal(2, Convert.ToInt32(Read(catalog, "books")));
                Assert.Equal(1, Convert.ToInt32(Read(catalog, "excluded")));
                Assert.True(Convert.ToInt32(Read(catalog, "series")) > 0);
                Assert.NotNull(Read(payload, "jobs"));
            }
        }

        [Fact]
        public async Task TheDerivedPanelNamesARealVerbForEveryTableAndFlagsStaleness()
        {
            using var f = Migrated();
            var (controller, provider, _, _, _) = Build(f);
            await using (provider)
            {
                var rows = ((IEnumerable<object>)Payload(controller.Derived())).ToList();
                Assert.Equal(DerivedTables.All.Count, rows.Count);

                // Every RebuildJob string must name a verb the host actually has.
                var verbs = new HashSet<string>(StringComparer.Ordinal)
                {
                    "books-resolve", "books-resolve --series", "books-resolve --tags", "books-resolve --fts",
                    "books-reading-order", "books-containment", "books-collected-editions",
                    "books-scan", "books-library-ratings",
                };
                foreach (var row in rows)
                {
                    var job = (string)Read(row, "rebuildJob")!;
                    Assert.Contains(job, verbs);
                    Assert.NotNull(Read(row, "name"));
                }

                // The migration stamped the resolve-owned rows, so at least one is NOT stale.
                Assert.Contains(rows, r => Read(r, "stale") is false);
            }
        }

        // ── job control ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ARecomputeStartsAJobAndAnswersWithAStatusUrl()
        {
            using var f = Migrated();
            var (controller, provider, _, _, _) = Build(f);
            await using (provider)
            {
                var accepted = await controller.Recompute("reading-order") as AcceptedResult;
                Assert.NotNull(accepted);
                var payload = accepted!.Value!;
                Assert.Equal("/admin/jobs/status?kind=recompute:reading-order", Read(payload, "statusUrl"));
                var job = (JobStatus)Read(payload, "job")!;
                Assert.Equal("recompute:reading-order", job.Kind);
                Assert.True(job.Processed > 0, "the first batch runs inline so the answer carries real numbers");

                await WaitForIdleAsync(provider.GetRequiredService<JobRunner>(), "recompute:reading-order");
                var status = Value<JobStatus>(controller.JobsStatus("recompute:reading-order"));
                Assert.Equal("done", status.State);
            }

            using var w = f.Hot();
            Assert.True(w.Scalar<long>("SELECT count(*) FROM ReadingOrderEntry") > 0);
        }

        [Fact]
        public async Task AnUnknownRecomputeIsRefused()
        {
            using var f = Migrated();
            var (controller, provider, _, _, _) = Build(f);
            await using (provider)
                Assert.IsType<BadRequestObjectResult>(await controller.Recompute("no-such-thing"));
        }

        [Fact]
        public async Task StoppingAJobThatIsNotRunningIsANotFoundRatherThanASilentNoOp()
        {
            using var f = Migrated();
            var (controller, provider, _, _, _) = Build(f);
            await using (provider)
                Assert.IsType<NotFoundObjectResult>(controller.StopJob("scan"));
        }

        [Fact]
        public async Task TheRunnerRefusesASecondRunOfTheSameKind()
        {
            using var f = Migrated();
            var (_, provider, _, _, _) = Build(f);
            await using (provider)
            {
                var runner = provider.GetRequiredService<JobRunner>();
                var gate = new TaskCompletionSource();
                var batches = 0;

                async Task<JobProgress> Step(IServiceProvider _, CancellationToken ct)
                {
                    if (Interlocked.Increment(ref batches) > 1) await gate.Task.WaitAsync(ct);
                    return new JobProgress(1, 1, batches.ToString(), 0, "tick");
                }

                await runner.StartAsync("slow", Step);
                Assert.True(runner.IsRunning("slow"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync("slow", Step));

                Assert.True(runner.Stop("slow"));
                gate.SetResult();
                await WaitForIdleAsync(runner, "slow");
                Assert.Equal("stopped", runner.Status("slow")!.State);
            }
        }

        [Fact]
        public async Task ABatchThatMovesNoCursorStopsTheRunInsteadOfSpinning()
        {
            using var f = Migrated();
            var (_, provider, _, _, _) = Build(f);
            await using (provider)
            {
                var runner = provider.GetRequiredService<JobRunner>();
                var calls = 0;
                await runner.StartAsync("stuck", (_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(new JobProgress(1, 5, "always-the-same", 0, null));
                });
                await WaitForIdleAsync(runner, "stuck");
                Assert.Equal("done", runner.Status("stuck")!.State);
                Assert.True(calls <= 3, $"a stalled cursor must stop the run, but it ran {calls} batches");
            }
        }

        [Fact]
        public async Task AFailingJobRecordsItsErrorRatherThanVanishing()
        {
            using var f = Migrated();
            var (_, provider, _, _, _) = Build(f);
            await using (provider)
            {
                var runner = provider.GetRequiredService<JobRunner>();
                var first = true;
                await runner.StartAsync("breaks", (_, _) =>
                {
                    if (first) { first = false; return Task.FromResult(new JobProgress(1, 1, "1", 0, null)); }
                    throw new InvalidOperationException("the share went away");
                });
                await WaitForIdleAsync(runner, "breaks");
                var status = runner.Status("breaks")!;
                Assert.Equal("failed", status.State);
                Assert.Equal("the share went away", status.Error);
            }
        }

        // ── the cache clear guard ────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheCacheClearDeletesGeneratedThumbnailsAndSparesHandMadeIcons()
        {
            using var f = Migrated();
            File.WriteAllBytes(Path.Combine(f.CacheDir, "1.webp"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(f.CacheDir, "22.webp"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(f.CacheDir, "notes.txt"), new byte[] { 1 });
            // f_2.jpg is written by the fixture: a hand-made collection icon that can never be regenerated.

            var (controller, provider, _, _, _) = Build(f);
            await using (provider)
            {
                var dry = Payload(controller.ClearCache(apply: false));
                Assert.Equal(2, Convert.ToInt32(Read(dry, "wouldDelete")));
                Assert.True(File.Exists(Path.Combine(f.CacheDir, "1.webp")), "a dry run deletes nothing");

                var applied = Payload(controller.ClearCache(apply: true));
                Assert.Equal(2, Convert.ToInt32(Read(applied, "deleted")));
            }

            Assert.False(File.Exists(Path.Combine(f.CacheDir, "1.webp")));
            Assert.False(File.Exists(Path.Combine(f.CacheDir, "22.webp")));
            Assert.True(File.Exists(Path.Combine(f.CacheDir, "f_2.jpg")), "the collection icon must survive");
            Assert.True(File.Exists(Path.Combine(f.CacheDir, "notes.txt")), "only the {id}.webp pattern is cleared");
        }

        // ── the settings overlay ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheConfigOverlayWritesOnlyItsAllowListAndNeverEchoesASecret()
        {
            using var f = Migrated();
            var (controller, provider, _, _, settings) = Build(f);
            await using (provider)
            {
                var ok = controller.PutConfig(new Dictionary<string, object?>
                {
                    ["ComicVineApiKey"] = "a-secret-value",
                    ["ThumbnailQuality"] = 82,
                }) as OkObjectResult;
                Assert.NotNull(ok);

                var values = (Dictionary<string, object?>)ok!.Value!;
                Assert.Equal("(set)", values["ComicVineApiKey"]);      // presence, never the value
                Assert.Equal(82, Convert.ToInt32(values["ThumbnailQuality"]));
                Assert.Equal("a-secret-value", settings.Value("ComicVineApiKey"));   // the runtime still reads it

                // A key outside the allow-list is REFUSED, not ignored.
                Assert.IsType<BadRequestObjectResult>(controller.PutConfig(new Dictionary<string, object?> { ["DbPath"] = "/somewhere/else" }));
                Assert.IsType<BadRequestObjectResult>(controller.PutConfig(new Dictionary<string, object?> { ["MediaTokenSecret"] = "x" }));
                // And so is an out-of-range number.
                Assert.IsType<BadRequestObjectResult>(controller.PutConfig(new Dictionary<string, object?> { ["ThumbnailQuality"] = 5 }));

                // A null clears the key back to the host's own configuration.
                controller.PutConfig(new Dictionary<string, object?> { ["ComicVineApiKey"] = null });
                Assert.Null(settings.Value("ComicVineApiKey"));

                var got = Payload(controller.GetConfig());
                Assert.Equal(BooksSettingsOverlay.AllowedKeys.Count, ((IEnumerable<object>)Read(got, "keys")!).Count());
            }
        }

        [Fact]
        public void ACorruptOverlayDegradesToNoOverlayRatherThanTakingTheHostDown()
        {
            using var f = Migrated();
            var path = Path.Combine(f.WorkDir, "bad.settings.json");
            File.WriteAllText(path, "{ this is not json");
            var settings = new BooksSettingsOverlay(path);
            Assert.Null(settings.Value("ComicVineApiKey"));
            Assert.All(settings.Read().Values, v => Assert.Null(v));
        }

        // ── logs ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheLogPanelTailsTheRingNewestFirstAndFiltersByLevel()
        {
            using var f = Migrated();
            var (controller, provider, _, logs, _) = Build(f);
            await using (provider)
            {
                logs.Add("Information", "Scan", "walking", null);
                logs.Add("Warning", "Scan", "one file unreadable", null);
                logs.Add("Error", "Scan", "the share went away", "boom");

                var all = Payload(controller.Logs(count: 10));
                var entries = ((IEnumerable<LogEntry>)Read(all, "entries")!).ToList();
                Assert.Equal(3, entries.Count);
                Assert.Equal("the share went away", entries[0].Message);   // newest first

                var warnings = ((IEnumerable<LogEntry>)Read(Payload(controller.Logs(10, "Warning")), "entries")!).ToList();
                Assert.Equal(2, warnings.Count);

                Assert.IsType<NoContentResult>(controller.ClearLogs());
                Assert.Empty((IEnumerable<LogEntry>)Read(Payload(controller.Logs()), "entries")!);
            }
        }

        [Fact]
        public void TheLogRingIsBoundedAndDropsTheOldest()
        {
            var logs = new InMemoryLogStore();
            for (var i = 0; i < InMemoryLogStore.Capacity + 50; i++) logs.Add("Information", "T", "line " + i, null);
            var tail = logs.Tail(InMemoryLogStore.Capacity);
            Assert.Equal(InMemoryLogStore.Capacity, tail.Count);
            Assert.DoesNotContain(tail, e => e.Message == "line 0");
        }

        [Fact]
        public void TheLoggerProviderFeedsTheRing()
        {
            var logs = new InMemoryLogStore();
            using var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Information);
                b.AddProvider(new InMemoryLoggerProvider(logs));
            });
            factory.CreateLogger("Books.Test").LogInformation("hello {Name}", "world");
            var entry = Assert.Single(logs.Tail());
            Assert.Equal("hello world", entry.Message);
            Assert.Equal("Books.Test", entry.Category);
        }

        // ── roots, kids tags, dedup, reconciliation through the controller ───────────────────────────────

        [Fact]
        public async Task RootCrudRefusesToDeleteARootThatStillHoldsItems()
        {
            using var f = Migrated();
            var (controller, provider, _, _, _) = Build(f);
            await using (provider)
            {
                Assert.IsType<ConflictObjectResult>(await controller.DeleteRoot(1, default));

                var added = Value<LibraryRoot>(await controller.AddRoot(new AdminController.RootBody(@"\\share\extra", ItemKind.Comic, false, true), default));
                Assert.Equal(@"\\share\extra", added.Path);
                Assert.IsType<ConflictObjectResult>(await controller.AddRoot(new AdminController.RootBody(@"\\share\extra", ItemKind.Comic, false, true), default));

                var updated = Value<LibraryRoot>(await controller.UpdateRoot(added.Id, new AdminController.RootBody(@"\\share\extra", ItemKind.Book, true, false), default));
                Assert.False(updated.Enabled);
                Assert.IsType<NoContentResult>(await controller.DeleteRoot(added.Id, default));
            }
        }

        [Fact]
        public async Task KidsTagsAreUpsertedLowerCasedAndDeletable()
        {
            using var f = Migrated();
            var (controller, provider, _, _, _) = Build(f);
            await using (provider)
            {
                var tag = Value<KidSafeTag>(await controller.UpsertKidsTag(new AdminController.KidTagBody("Audience", "All-Ages", null), default));
                Assert.Equal("audience", tag.Category);
                Assert.Equal("all-ages", tag.Tag);
                Assert.Equal("both", tag.AppliesTo);
                Assert.IsType<BadRequestObjectResult>(await controller.UpsertKidsTag(new AdminController.KidTagBody("", "", null), default));
                Assert.IsType<NoContentResult>(await controller.DeleteKidsTag("audience", "all-ages", default));
                Assert.IsType<NotFoundResult>(await controller.DeleteKidsTag("audience", "all-ages", default));
            }
        }

        [Fact]
        public async Task TheSeriesReconciliationEndpointsSayWhatMustBeRebuilt()
        {
            using var f = Migrated();
            var (controller, provider, _, _, _) = Build(f);
            await using (provider)
            {
                var cleared = Value<SeriesMismatchService.EditResult>(
                    await controller.ClearLink(new AdminController.LinkBody("2000 AD", Provider.Cv, null), default));
                Assert.True(cleared.RebuildRequired);

                Assert.IsType<BadRequestObjectResult>(await controller.SetLink(new AdminController.LinkBody("2000 AD", Provider.Cv, null), default));
                var set = Value<SeriesMismatchService.EditResult>(
                    await controller.SetLink(new AdminController.LinkBody("2000 AD", Provider.Cv, 19752), default));
                Assert.Equal(1, set.RowsChanged);

                Assert.IsType<BadRequestObjectResult>(await controller.Fold(new AdminController.FoldBody("Batman", "Batman"), default));
                Assert.NotNull(await controller.SeriesSummary(default));
                Assert.NotNull(await controller.NameFix(apply: false, default));
            }
        }

        [Fact]
        public async Task NormalizationThroughTheControllerDefaultsToADryRunAndNamesTheNextStep()
        {
            using var f = Migrated();
            var (controller, provider, db, _, _) = Build(f);
            await using (provider)
            {
                var dry = Payload(await controller.ApplyNormalization(apply: false, default));
                Assert.True((bool)Read(dry, "dryRun")!);
                Assert.Equal("POST /admin/recompute/resolve", Read(dry, "next"));
                Assert.True(await db.InsightTags.AnyAsync(t => t.Value == "science-fiction"));

                var applied = Payload(await controller.ApplyNormalization(apply: true, default));
                Assert.False((bool)Read(applied, "dryRun")!);
            }
        }

        [Fact]
        public async Task TheDedupPanelListsGroupsWithTheirMembers()
        {
            using var f = Migrated();
            var (controller, provider, _, _, _) = Build(f);
            await using (provider)
            {
                var payload = Payload(await controller.DedupList("Pending", 0, 50, default));
                Assert.Equal(1, Convert.ToInt32(Read(payload, "totalCount")));   // the fixture carries one group
                var groups = ((IEnumerable<object>)Read(payload, "groups")!).ToList();
                Assert.Single(groups);
                Assert.NotNull(Read(groups[0], "members"));
            }
        }

        /// <summary>Poll until the runner's background loop has finished, rather than sleeping a fixed time.</summary>
        private static async Task WaitForIdleAsync(JobRunner runner, string kind)
        {
            for (var i = 0; i < 400; i++)
            {
                var status = runner.Status(kind);
                if (status != null && status.State is "done" or "failed" or "stopped") return;
                await Task.Delay(25);
            }
            throw new TimeoutException($"job '{kind}' never finished");
        }
    }
}
