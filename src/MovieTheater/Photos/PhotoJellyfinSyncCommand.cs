using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.Jellyfin;

namespace MovieTheater.Photos
{
    /// <summary>
    /// <c>photos-sync-jellyfin</c> — stamps <see cref="PhotoAsset.JellyfinItemId"/> from the DEDICATED
    /// family Jellyfin library, clears it for items that vanished, and reports the reserved-folder-name
    /// collisions §2.3 warns about (docs/photos-plan.md §2.3).
    ///
    /// <para><b>It never triggers a scan.</b> Jellyfin's scheduled scans are disabled and running one is
    /// the owner's call; this command READS a scoped item listing and writes database columns. Nothing
    /// under the collection root is touched in any way (§6).</para>
    ///
    /// <para><b>It never sees the movie library.</b> The sweep is scoped by
    /// <c>PhotosJellyfinLibraryId</c>, the exact mirror of the movie sync's path-prefix exclusion of
    /// this collection — the two halves of §2.3, neither of which depends on the other having run.</para>
    ///
    /// <para><b>Bulk-job rules</b>, as every pass here: bounded work per batch,
    /// <c>{processed, remaining, nextCursor}</c> after each, <c>--after</c> to resume, <c>--max-batches</c>
    /// to bound one invocation, and idempotent re-runs.</para>
    ///
    /// <para><b>--items-json</b> feeds the lanes from a local file instead of a server. It exists for the
    /// same reason <c>--sqlite</c> does: the configured Jellyfin endpoint is the LIVE media server, and
    /// exercising this end to end has to be possible without calling it.</para>
    /// </summary>
    [Command("photos-sync-jellyfin", Description = "Map the family Jellyfin library onto photo assets, clear vanished ids, and audit reserved folder names.")]
    public class PhotoJellyfinSyncCommand : BasicDICommand, ICommand
    {
        [CommandOption("pass", 'p', Description = "items | clear | audit | all (default all).")]
        public string Pass { get; set; } = "all";

        [CommandOption("batch-size", Description = "Items (or rows) per batch (default 200).")]
        public int BatchSize { get; set; } = 200;

        [CommandOption("max-batches", Description = "Batches this invocation runs per pass; 0 drains (default 0).")]
        public int MaxBatches { get; set; }

        [CommandOption("after", Description = "Resume cursor from a prior run's nextCursor (applies to the FIRST pass of a chained run).")]
        public string? After { get; set; }

        [CommandOption("root", 'r', Description = "Collection root. Default: PhotosLibraryDir from config.")]
        public string? Root { get; set; }

        /// <summary>
        /// Additional absolute forms of the SAME collection, for a server that reports it under a mount
        /// no <c>JellyfinPathMappings</c> entry describes — a family library added by UNC while this
        /// host has it on a drive letter, say. Every form maps to the same root-relative key, so this
        /// only ever widens what can be matched; it never changes what a match means.
        /// </summary>
        [CommandOption("extra-root", Description = "Another absolute form of the same collection root (repeatable).")]
        public IReadOnlyList<string> ExtraRoots { get; set; } = new List<string>();

        [CommandOption("library-id", Description = "Family Jellyfin library id. Default: PhotosJellyfinLibraryId from config.")]
        public string? LibraryId { get; set; }

        [CommandOption("dry-run", Description = "Report what would be written and write nothing.")]
        public bool DryRun { get; set; }

        [CommandOption("items-json", Description = "Read the library listing from this JSON file ([{ id, path }]) instead of Jellyfin (local exercise only).")]
        public string? ItemsJson { get; set; }

        [CommandOption("samples", Description = "How many example paths to print per unmatched section (default 15).")]
        public int Samples { get; set; } = 15;

        [CommandOption("sqlite", Description = "Run against this SQLite file instead of the configured database (local exercise only).")]
        public string? Sqlite { get; set; }

        private readonly MovieTheaterConfiguration config;
        private readonly IDbContextFactory<MovieDb> dbFactory;

        public PhotoJellyfinSyncCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var cancel = console.RegisterCancellationHandler();

            var passes = ParsePasses(Pass);
            if (passes.Count == 0)
            {
                w.WriteLine($"Unknown --pass '{Pass}'. Use items, clear, audit or all.");
                return;
            }

            var rootSetting = !string.IsNullOrWhiteSpace(Root) ? Root : config.PhotosLibraryDir;
            if (string.IsNullOrWhiteSpace(rootSetting))
            {
                w.WriteLine("No photo root: pass --root or set PhotosLibraryDir in config.");
                w.WriteLine("Without it a Jellyfin path cannot be turned into the root-relative key the table stores (§2.3).");
                return;
            }
            var paths = PhotoJellyfinPaths.Build(rootSetting, config.JellyfinPathMappings, ExtraRoots);

            var source = BuildSource(w);
            if (source == null) return;

            w.WriteLine($"root: {rootSetting}");
            w.WriteLine($"source: {await source.DescribeAsync(cancel)}");
            w.WriteLine("Nothing on disk is touched by this command — every outcome is a database column (§6).");
            if (DryRun) w.WriteLine("--dry-run: reporting only, nothing will be written.");

            var factory = BuildDbFactory(w);
            var options = new PhotoJellyfinSyncOptions
            {
                BatchSize = Math.Max(1, BatchSize),
                DryRun = DryRun,
                AuditBatchId = "jellyfin-reserved-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),
            };

            var engine = new PhotoJellyfinSync(factory, source, paths, options, line => w.WriteLine(line));
            var first = passes[0];
            foreach (var pass in passes)
            {
                w.WriteLine();
                w.WriteLine($"── {pass} ──");
                // A cursor belongs to the FIRST pass of a chained run: an item index means nothing to a
                // lane that pages our own row ids.
                var cursor = pass == first ? After : null;
                var total = await engine.RunAsync(pass, cursor, MaxBatches, cancel);

                var counts = total.CountsText();
                w.WriteLine($"{pass}: {total.Processed} examined, {total.Remaining} remaining"
                            + (counts.Length > 0 ? $"  [{counts}]" : ""));
                if (total.Remaining > 0)
                    w.WriteLine($"More to do: re-run --pass {pass.ToString().ToLowerInvariant()} --after \"{total.NextCursor}\"");
            }

            // The two-sided unmatched report §2.3 asks for. Both directions matter and mean different
            // things: one says the media server indexed a file the album has not ingested, the other
            // says the album holds a video the media server cannot play.
            PrintSection(w, $"Jellyfin paths the album does not hold ({engine.UnmatchedJellyfinPaths.Count})", engine.UnmatchedJellyfinPaths);
            PrintSection(w, $"Album videos no Jellyfin item covers — they will show \"not yet synced\" ({engine.UnmatchedAssetPaths.Count})", engine.UnmatchedAssetPaths);

            await ReportAsync(factory, w);
        }

        /// <summary>Where the item listing comes from. A local JSON file when <c>--items-json</c> is
        /// given, otherwise the configured family library — and nothing at all when that is
        /// unconfigured, which is the normal state on every host but the operator's.</summary>
        private IPhotoJellyfinSource? BuildSource(ConsoleWriter w)
        {
            if (!string.IsNullOrWhiteSpace(ItemsJson))
            {
                var file = Path.GetFullPath(ItemsJson!);
                if (!File.Exists(file)) { w.WriteLine($"--items-json not found: {file}"); return null; }
                return new JsonFileSource(file);
            }

            var libraryId = !string.IsNullOrWhiteSpace(LibraryId) ? LibraryId : config.PhotosJellyfinLibraryId;
            if (string.IsNullOrWhiteSpace(libraryId))
            {
                w.WriteLine("No family Jellyfin library configured (PhotosJellyfinLibraryId, or --library-id).");
                w.WriteLine("That is the normal state until the dedicated homevideos library exists (§2.3);");
                w.WriteLine("the album is fully usable without it — videos simply show \"not yet synced\".");
                w.WriteLine("Use --items-json to exercise the lanes against a local listing.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(config.JellyfinBaseUrl) || string.IsNullOrWhiteSpace(config.JellyfinApiKey))
            {
                w.WriteLine("JellyfinBaseUrl / JellyfinApiKey are not configured on this host.");
                return null;
            }
            return new JellyfinPhotoSource(GetRequiredService<JellyfinApi>(), libraryId!);
        }

        /// <summary>
        /// A library listing read from a file: <c>[{ "id": "...", "path": "..." }]</c>. The local
        /// exercise lane — the configured Jellyfin endpoint is the live media server, and a smoke test
        /// must never be the thing that calls it.
        /// </summary>
        private sealed class JsonFileSource : IPhotoJellyfinSource
        {
            private readonly string file;

            public JsonFileSource(string file) => this.file = file;

            public Task<IReadOnlyList<PhotoJellyfinItem>> ItemsAsync(CancellationToken cancel = default)
            {
                List<PhotoJellyfinItem>? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<List<PhotoJellyfinItem>>(File.ReadAllText(file),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException e)
                {
                    // A clean refusal, never an empty list: the Clear lane treats "no items" as a
                    // reason to change nothing, and a malformed file quietly reading as empty would
                    // turn a typo into a lane that silently did nothing while reporting success.
                    throw new CommandException($"--items-json is not a JSON array of {{ id, path }}: {e.Message}");
                }
                if (parsed == null)
                    throw new CommandException("--items-json parsed to null; expected a JSON array of { id, path }.");
                return Task.FromResult<IReadOnlyList<PhotoJellyfinItem>>(parsed);
            }

            public Task<string> DescribeAsync(CancellationToken cancel = default) =>
                Task.FromResult($"local listing {file} (no server was contacted)");
        }

        /// <summary>What the album can actually play after the run — counted from the database rather
        /// than accumulated by the lanes.</summary>
        private static async Task ReportAsync(Func<MovieDb> factory, ConsoleWriter w)
        {
            using var db = factory();
            var videos = await db.PhotoAssets.CountAsync(a => a.Kind == PhotoAssetKind.Video && a.MissingSinceUtc == null);
            var playable = await db.PhotoAssets.CountAsync(a => a.Kind == PhotoAssetKind.Video
                                                               && a.MissingSinceUtc == null && a.JellyfinItemId != null);
            var reserved = await db.PhotoCurationBatchItems
                .CountAsync(i => i.PhotoCurationBatch.Kind == PhotoCurationBatchKind.JellyfinReserved);

            w.WriteLine();
            w.WriteLine($"  videos in the album: {videos}");
            w.WriteLine($"  playable (a Jellyfin item id is stamped): {playable}");
            w.WriteLine($"  not yet synced: {videos - playable}");
            w.WriteLine(reserved > 0
                ? $"  ⚠ {reserved} video(s) sit in folders whose names Jellyfin RESERVES for extras — their contents are dropped by its folder walk, so they can never be stamped or played. Nothing will be renamed (§6); see /photos → Review."
                : "  No reserved-folder-name collisions found.");
        }

        private static void PrintSection(ConsoleWriter w, string heading, IReadOnlyList<string> lines)
        {
            w.WriteLine();
            w.WriteLine(heading);
            if (lines.Count == 0) { w.WriteLine("  (none)"); return; }
            foreach (var line in lines.Take(15)) w.WriteLine($"  {line}");
            if (lines.Count > 15) w.WriteLine("  … (more omitted)");
        }

        private static List<PhotoJellyfinPass> ParsePasses(string value)
        {
            // Order is load-bearing: Clear must not run before the item list is known to be real, and
            // the audit reads paths the item lane has just been over.
            var all = new[] { PhotoJellyfinPass.Items, PhotoJellyfinPass.Clear, PhotoJellyfinPass.Audit };
            if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) return all.ToList();

            var result = new List<PhotoJellyfinPass>();
            foreach (var part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.TryParse<PhotoJellyfinPass>(part.Trim(), ignoreCase: true, out var pass)
                    || !Enum.IsDefined(typeof(PhotoJellyfinPass), pass))
                    return new List<PhotoJellyfinPass>();
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
