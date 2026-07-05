using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Imports a curated per-game fixes dataset (from online / community sources) into
    /// <see cref="ArcadeGameProfile"/>. The dataset is a reviewed, source-cited JSON file kept in the
    /// repo (data/arcade/game-fixes.json); this command matches each entry to the catalog by normalized
    /// identity and upserts the profile. See docs/arcade-per-game-config.md.
    ///
    /// <para><b>Safety.</b> Dry-run-first: prints exactly which games would be created/updated/skipped and
    /// writes nothing unless <c>--apply</c>. Guarded: a fix is <b>skipped</b> when the title matches no
    /// catalog row (so a typo can't create an orphan profile), and an existing hand-set profile is
    /// preserved unless <c>--overwrite</c>. Idempotent: re-running with the same dataset is a no-op. This
    /// matters because a wrong <c>forcedFps</c> would mis-pace a game (e.g. 30 on a genuine 60fps title
    /// halves it) — the dataset is the reviewed gate, this command just applies it.</para>
    /// </summary>
    [Command("arcade-gameconfig-import", Description = "Import a curated per-game fixes JSON into ArcadeGameProfile (dry-run unless --apply).")]
    public class ArcadeGameConfigImportCommand : BasicDICommand, ICommand
    {
        [CommandOption("file", 'f', Description = "Path to the fixes dataset JSON (default data/arcade/game-fixes.json).")]
        public string File { get; set; } = "data/arcade/game-fixes.json";

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("overwrite", Description = "Also update profiles that already exist (default: preserve them).")]
        public bool Overwrite { get; set; }

        [CommandOption("system", Description = "Only import fixes for this system code (e.g. dc).")]
        public string System { get; set; } = "";

        [CommandOption("min-confidence", Description = "Only apply fixes at or above this confidence: verified|high|medium (default verified).")]
        public string MinConfidence { get; set; } = "verified";

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeGameConfigImportCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        // Dataset shape: { "fixes": [ { system, title, forcedFps?, coreOptions?{}, notes?, source? }, ... ] }
        private sealed class Dataset { public List<Fix> fixes { get; set; } = new(); }
        private sealed class Fix
        {
            public string system { get; set; } = default!;
            public string title { get; set; } = default!;
            public double? forcedFps { get; set; }
            public Dictionary<string, string>? coreOptions { get; set; }
            public string? notes { get; set; }
            public string? source { get; set; }
            /// <summary>verified | high | medium — how sure we are the fix is correct for OUR stack.
            /// forcedFps entries below "verified" have NOT been confirmed to actually double-speed here
            /// (a self-limiting 30fps game would be HALVED), so they require --min-confidence to apply.</summary>
            public string? confidence { get; set; }
        }

        private static int Rank(string? c) => (c ?? "medium").Trim().ToLowerInvariant() switch
        { "verified" => 3, "high" => 2, "medium" => 1, _ => 0 };

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var path = Path.GetFullPath(File);
            if (!global::System.IO.File.Exists(path)) { w.WriteLine($"Dataset not found: {path}"); return; }

            Dataset? ds;
            try { ds = JsonSerializer.Deserialize<Dataset>(await global::System.IO.File.ReadAllTextAsync(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (Exception ex) { w.WriteLine($"Bad dataset JSON: {ex.Message}"); return; }
            if (ds?.fixes is null || ds.fixes.Count == 0) { w.WriteLine("No fixes in dataset."); return; }

            await using var db = await dbFactory.CreateDbContextAsync();

            int minRank = Rank(MinConfidence);
            int created = 0, updated = 0, skippedExisting = 0, unmatched = 0, filtered = 0, belowConfidence = 0;
            foreach (var fx in ds.fixes)
            {
                if (string.IsNullOrWhiteSpace(fx.system) || string.IsNullOrWhiteSpace(fx.title)) continue;
                var sys = fx.system.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(System) && sys != System.Trim().ToLowerInvariant()) { filtered++; continue; }

                // Below the confidence bar → show it but don't touch the DB (halving risk on a wrong fps).
                if (Rank(fx.confidence) < minRank)
                {
                    w.WriteLine($"  hold [{sys}] \"{fx.title}\" (confidence={fx.confidence ?? "medium"} < {MinConfidence}) — {fx.notes}");
                    belowConfidence++;
                    continue;
                }

                var titleKey = fx.title.Trim().ToLowerInvariant();

                // Match by normalized identity so one fix covers all ROM variants. Skip when nothing matches.
                var matchCount = await db.ArcadeGames.CountAsync(g => g.System == sys && g.Title.ToLower() == titleKey);
                if (matchCount == 0)
                {
                    w.WriteLine($"  MISS [{sys}] \"{fx.title}\" — no catalog match, skipped.");
                    unmatched++;
                    continue;
                }

                var coreJson = fx.coreOptions is { Count: > 0 } ? JsonSerializer.Serialize(fx.coreOptions) : null;
                var existing = await db.ArcadeGameProfiles.FirstOrDefaultAsync(p => p.System == sys && p.TitleKey == titleKey);

                if (existing != null)
                {
                    if (!Overwrite) { w.WriteLine($"  keep [{sys}] \"{fx.title}\" — profile exists (use --overwrite)."); skippedExisting++; continue; }
                    w.WriteLine($"  UPDATE [{sys}] \"{fx.title}\" → fps={fx.forcedFps?.ToString() ?? "-"} opts={(coreJson != null ? "yes" : "-")} ({matchCount} ROM(s))");
                    if (Apply) { existing.ForcedFps = fx.forcedFps; existing.CoreOptionsJson = coreJson; existing.Notes = fx.notes; }
                    updated++;
                }
                else
                {
                    w.WriteLine($"  CREATE [{sys}] \"{fx.title}\" → fps={fx.forcedFps?.ToString() ?? "-"} opts={(coreJson != null ? "yes" : "-")} ({matchCount} ROM(s))");
                    if (Apply) db.ArcadeGameProfiles.Add(new ArcadeGameProfile
                    {
                        System = sys, TitleKey = titleKey, ForcedFps = fx.forcedFps, CoreOptionsJson = coreJson, Notes = fx.notes,
                    });
                    created++;
                }
            }

            if (Apply) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"{(Apply ? "APPLIED" : "DRY RUN")}: created={created} updated={updated} kept-existing={skippedExisting} unmatched={unmatched} held-below-confidence={belowConfidence}{(filtered > 0 ? $" filtered-by-system={filtered}" : "")}");
            if (created + updated > 0)
                w.WriteLine(Apply
                    ? "Next: run arcade-gameconfig-export to regenerate the worker manifest."
                    : "Re-run with --apply to write these, then arcade-gameconfig-export.");
        }
    }
}
