using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Photos
{
    /// <summary>
    /// <c>photos-sync-immich</c> — pulls suggestions from the headless Immich sidecar
    /// (docs/photos-plan.md §2.4): path mapping, face clusters, reverse-geocode labels and duplicate
    /// candidates.
    ///
    /// <para><b>It writes suggestions, never truth, and never a file.</b> Faces become
    /// <see cref="PhotoTagSource.Suggested"/> tags a human promotes or refuses; location labels fill only
    /// where null; duplicate candidates are Pending Near groups. Immich itself sees the collection
    /// through a READ-ONLY CIFS mount, so it is physically incapable of touching an original (§2.4) —
    /// and this command only ever GETs from it.</para>
    ///
    /// <para><b>Nothing here is required.</b> With <c>ImmichBaseUrl</c>/<c>ImmichApiKey</c> unset the
    /// command says so and exits; hand-tagging is unaffected, which is the acceptance criterion for this
    /// phase ("pulling the Immich container leaves tagging fully functional").</para>
    ///
    /// <para>Chunked and resumable like every pass here: bounded work per batch,
    /// <c>{processed, remaining, nextCursor}</c> per chunk, <c>--after</c> to resume, <c>--max-batches</c>
    /// to bound one invocation. The version is read and PRINTED first, and an untested major refuses the
    /// run rather than mis-parsing the API.</para>
    /// </summary>
    [Command("photos-sync-immich", Description = "Pull face, location and duplicate SUGGESTIONS from the Immich sidecar — rows only, never a file.")]
    public class PhotoImmichSyncCommand : BasicDICommand, ICommand
    {
        [CommandOption("pass", 'p', Description = "assets | people | faces | duplicates | all (default all).")]
        public string Pass { get; set; } = "all";

        [CommandOption("batch-size", Description = "Units per batch: Immich page size for the paged lanes, rows for faces (default 200).")]
        public int BatchSize { get; set; } = 200;

        [CommandOption("max-batches", Description = "Batches this invocation runs per pass; 0 drains (default 0).")]
        public int MaxBatches { get; set; }

        [CommandOption("after", Description = "Resume cursor from a prior run's nextCursor (applies to the FIRST pass of a chained run).")]
        public string? After { get; set; }

        [CommandOption("suffix-segments", Description = "Trailing path segments a mapping match needs (default 2).")]
        public int SuffixSegments { get; set; } = ImmichClient.DefaultSuffixSegments;

        [CommandOption("dry-run", Description = "Report what would be written and write nothing.")]
        public bool DryRun { get; set; }

        [CommandOption("base-url", Description = "Override ImmichBaseUrl for this run (local exercise against a stand-in server).")]
        public string? BaseUrl { get; set; }

        [CommandOption("api-key", Description = "Override ImmichApiKey for this run.")]
        public string? ApiKey { get; set; }

        [CommandOption("sqlite", Description = "Run against this SQLite file instead of the configured database (local exercise only).")]
        public string? Sqlite { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public PhotoImmichSyncCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            var passes = ParsePasses(Pass);
            if (passes.Count == 0)
            {
                w.WriteLine($"Unknown --pass '{Pass}'. Use assets, people, faces, duplicates or all.");
                return;
            }

            // The overrides exist so this can be exercised end to end against a stand-in server without
            // pointing the configured connection — or the configured sidecar — anywhere real.
            var effective = new MovieTheaterConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
            {
                ImmichBaseUrl = BaseUrl ?? config.ImmichBaseUrl,
                ImmichApiKey = ApiKey ?? config.ImmichApiKey,
                ImmichLibraryId = config.ImmichLibraryId,
            };

            using var immich = ImmichClient.TryCreate(effective, line => w.WriteLine(line));
            if (immich == null)
            {
                w.WriteLine("Immich is not configured on this host (ImmichBaseUrl + ImmichApiKey).");
                w.WriteLine("That is a perfectly normal state: the album is fully usable without it — people,");
                w.WriteLine("tagging and the tag queue all work by hand. Nothing to do.");
                return;
            }

            Services.ImmichVersion version;
            try
            {
                version = await immich.RequireSupportedVersionAsync();
            }
            catch (ImmichVersionUnsupportedException ex)
            {
                w.WriteLine(ex.Message);
                return;
            }
            catch (HttpRequestException ex)
            {
                w.WriteLine($"Could not reach Immich: {ex.Message}");
                w.WriteLine("Nothing was written. Tagging by hand is unaffected.");
                return;
            }

            w.WriteLine($"Immich {version} (tested against {ImmichClient.TestedMajor}.{ImmichClient.TestedMinorFrom}"
                        + $"–{ImmichClient.TestedMajor}.{ImmichClient.TestedMinorTo}).");
            w.WriteLine("Nothing on disk is touched by this command — every outcome is a row (§6).");
            w.WriteLine("Faces arrive as SUGGESTIONS. Nothing is auto-confirmed; a family member decides at /photos → Tag queue.");
            if (DryRun) w.WriteLine("--dry-run: reporting only, nothing will be written.");

            var factory = BuildDbFactory(w);
            var options = new PhotoImmichSyncOptions
            {
                BatchSize = Math.Max(1, BatchSize),
                SuffixSegments = Math.Max(1, SuffixSegments),
                ThumbCacheDir = config.PhotosThumbCacheDir,
                DryRun = DryRun,
            };

            // The version is recorded WITH the run (§2.4's pin), so a surprising suggestion months later
            // has a recorded starting point instead of a guess about which Immich produced it.
            if (!DryRun) await RecordRunAsync(factory, version.ToString());

            var engine = new PhotoImmichSync(factory, immich, options, line => w.WriteLine(line));
            var pass0 = passes[0];
            foreach (var pass in passes)
            {
                w.WriteLine();
                w.WriteLine($"── {pass} ──");
                // A cursor belongs to the FIRST pass of a chained run: an Immich page number means
                // nothing to the face lane, which pages our own ids.
                var cursor = pass == pass0 ? After : null;
                var total = await engine.RunAsync(pass, cursor, MaxBatches);

                var counts = total.CountsText();
                w.WriteLine($"{pass}: {total.Processed} examined, {total.Remaining} remaining"
                            + (counts.Length > 0 ? $"  [{counts}]" : ""));
                if (total.Remaining > 0)
                    w.WriteLine($"More to do: re-run --pass {pass.ToString().ToLowerInvariant()} --after \"{total.NextCursor}\"");
            }

            await ReportAsync(factory, w);
        }

        /// <summary>
        /// Stamps the sidecar version this run talked to onto a curation-batch row.
        ///
        /// <para>Deliberately reuses <see cref="PhotoCurationBatch"/> rather than adding a table: the row
        /// is a run marker with a cursor field, which is exactly the shape needed, and this phase ships
        /// with NO migration against a database that is shared with production.</para>
        /// </summary>
        private static async Task RecordRunAsync(Func<MovieDb> factory, string version)
        {
            using var db = factory();
            var row = await db.PhotoCurationBatches
                .FirstOrDefaultAsync(b => b.Kind == PhotoCurationBatchKind.ImmichSync && b.BatchId == ImmichRunMarker);
            if (row == null)
            {
                row = new PhotoCurationBatch
                {
                    Kind = PhotoCurationBatchKind.ImmichSync,
                    BatchId = ImmichRunMarker,
                    Status = PhotoCurationBatchStatus.Accepted,
                    CreatedUtc = DateTime.UtcNow,
                };
                db.PhotoCurationBatches.Add(row);
            }
            row.Cursor = version.Length > 128 ? version.Substring(0, 128) : version;
            row.DecidedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        /// <summary>The single run-marker row's id within its kind.</summary>
        public const string ImmichRunMarker = "immich-sync";

        /// <summary>What the tag queue has waiting after the run — counted from the database rather than
        /// accumulated by the passes.</summary>
        private static async Task ReportAsync(Func<MovieDb> factory, ConsoleWriter w)
        {
            using var db = factory();
            var suggested = await db.PhotoPersonTags.CountAsync(t => t.Source == PhotoTagSource.Suggested);
            var rejected = await db.PhotoPersonTags.CountAsync(t => t.Source == PhotoTagSource.Rejected);
            var unnamed = await db.FamilyPeople.CountAsync(p => p.Name == "" && p.ImmichPersonId != null);
            var mapped = await db.PhotoAssets.CountAsync(a => a.ImmichAssetId != null);

            w.WriteLine();
            w.WriteLine($"  assets mapped to Immich: {mapped}");
            w.WriteLine($"  face suggestions waiting: {suggested}");
            w.WriteLine($"  refused suggestions remembered (never re-proposed): {rejected}");
            w.WriteLine(unnamed > 0
                ? $"  {unnamed} unnamed face group(s) waiting for a name at /photos → Tag queue."
                : "  No unnamed face groups are waiting.");
        }

        private static List<PhotoImmichPass> ParsePasses(string value)
        {
            var all = new[]
            {
                // Order is load-bearing: an asset must be mapped before its faces can be read, and a
                // cluster must have a person row before a face can become a suggestion on it.
                PhotoImmichPass.Assets, PhotoImmichPass.People,
                PhotoImmichPass.Faces, PhotoImmichPass.Duplicates,
            };
            if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) return all.ToList();

            var result = new List<PhotoImmichPass>();
            foreach (var part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.TryParse<PhotoImmichPass>(part.Trim(), ignoreCase: true, out var pass)
                    || !Enum.IsDefined(typeof(PhotoImmichPass), pass))
                    return new List<PhotoImmichPass>();
                result.Add(pass);
            }
            return result;
        }

        /// <summary>Same explicit local lane as the other photo commands: the configured connection
        /// string is the live shared database, so exercising a pass end to end has to be possible
        /// without pointing it there.</summary>
        private Func<MovieDb> BuildDbFactory(ConsoleWriter w)
        {
            if (string.IsNullOrWhiteSpace(Sqlite)) return () => dbFactory.CreateDbContext();

            var file = Path.GetFullPath(Sqlite!);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var sqliteOptions = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + file).Options;
            using (var seed = new MovieDb(sqliteOptions)) seed.Database.EnsureCreated();
            w.WriteLine($"sqlite: {file}");
            return () => new MovieDb(sqliteOptions);
        }
    }
}
