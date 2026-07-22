using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Resolves the arcade catalog against the FinalBurn Neo DAT (<see cref="FbneoDat"/>). Our ROM source
    /// is a full MAME romset but the core is FBNeo, so most ingested rows can't actually run. For each
    /// arcade/neogeo <c>ArcadeGame</c> this:
    /// <list type="bullet">
    /// <item>sets <b>Title</b> to the real game name from the DAT (shortname <c>1942a</c> → "1942"), with
    /// every clone/revision sharing the parent's title so the query-time (System,Title) grouping collapses
    /// them into a single lobby card (docs/arcade-dedupe-multidisc-plan.md);</item>
    /// <item><b>disables</b> (IsEnabled=false, reversible, Notes-marked) rows whose shortname is not in the
    /// FBNeo DAT — they cannot run on this core — and BIOS sets, which are not games.</item>
    /// </list>
    /// The ROM dependency-closure fix (staging the <c>romof</c> parent+BIOS zips) lives in the romcache
    /// export/gateway, driven by the same DAT.
    ///
    /// <para><b>Bulk-job rules</b>: dry-run unless <c>--apply</c>; bounded by <c>--limit</c>, ordered by Id,
    /// resumable via <c>--after &lt;id&gt;</c>; idempotent. Re-run after any <c>arcade-jit-ingest</c> (which
    /// re-enables rows on upsert).</para>
    /// </summary>
    [Command("arcade-fbneo-resolve", Description = "Resolve arcade rows against the FBNeo DAT: real titles + dedupe, disable non-runnable (dry-run unless --apply).")]
    public class ArcadeFbneoResolveCommand : BasicDICommand, ICommand
    {
        [CommandOption("dat", Description = "Path to the FBNeo Arcade ClrMame XML DAT. Default data/arcade/fbneo-arcade.dat.")]
        public string DatPath { get; set; } = "data/arcade/fbneo-arcade.dat";

        [CommandOption("systems", Description = "Comma-separated system codes to resolve. Default arcade,neogeo.")]
        public string Systems { get; set; } = "arcade,neogeo";

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max rows to process this run (default 5000).")]
        public int Limit { get; set; } = 5000;

        [CommandOption("after", Description = "Resume cursor: skip rows whose Id ≤ this (from a prior nextCursor).")]
        public int After { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeFbneoResolveCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        private const string DisabledNotInDat = "fbneo-resolve: not in FBNeo DAT (unrunnable on fbneo core)";
        private const string DisabledBios = "fbneo-resolve: BIOS set (not a game)";

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            FbneoDat dat;
            try { dat = FbneoDat.Load(DatPath); }
            catch (Exception ex) { w.WriteLine($"Could not load DAT: {ex.Message}"); return; }

            var systems = Systems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant()).ToArray();

            w.WriteLine($"FBNeo DAT v{dat.Version} ({dat.Count} games) — {DatPath}");
            w.WriteLine($"Systems: {string.Join(", ", systems)}");
            w.WriteLine();

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);   // remote DB, large ArcadeGame table

            int runnable = 0, titleChanged = 0, disabledUnrunnable = 0, disabledBios = 0, reenabled = 0;
            var cards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // distinct (system|title) after dedupe
            var egRunnable = new List<string>();
            var egUnrunnable = new List<string>();

            // Bounded by --limit per invocation, but fetched in internal pages so a single query over a
            // remote 36k-row table can't blow the command timeout. On --apply we save + clear per page.
            int processed = 0, cursor = After;
            const int Page = 2000;
            while (processed < Limit)
            {
                int take = Math.Min(Page, Limit - processed);
                var rows = await db.ArcadeGames
                    .Where(g => systems.Contains(g.System) && g.Id > cursor)
                    .OrderBy(g => g.Id).Take(take).ToListAsync();
                if (rows.Count == 0) break;

                foreach (var g in rows)
                {
                    var key = g.CloudRetroGameKey;
                    if (!dat.TryGet(key, out var entry))
                    {
                        // Not an FBNeo set → cannot run on this core. Disable (reversible) + mark.
                        if (g.IsEnabled) { if (Apply) g.IsEnabled = false; }
                        if (Apply) g.Notes = DisabledNotInDat;
                        disabledUnrunnable++;
                        if (egUnrunnable.Count < 6) egUnrunnable.Add($"[{g.System}] {key}");
                        continue;
                    }

                    if (entry.IsBios)
                    {
                        if (g.IsEnabled) { if (Apply) g.IsEnabled = false; }
                        if (Apply) g.Notes = DisabledBios;
                        disabledBios++;
                        continue;
                    }

                    // Runnable FBNeo game — set the real title (parent's, so clones collapse) and fill year.
                    runnable++;
                    var newTitle = dat.TitleFor(key);
                    var newSort = ArcadeNaming.ArticleInvert(newTitle);
                    if (g.Title != newTitle || g.SortTitle != newSort)
                    {
                        if (Apply) { g.Title = newTitle; g.SortTitle = newSort; g.CollapseKey = ArcadeNaming.CollapseKey(newTitle); }
                        titleChanged++;
                        if (egRunnable.Count < 6) egRunnable.Add($"[{g.System}] {key,-14} → \"{newTitle}\"");
                    }
                    if (g.Year == null && entry.Year is int y) { if (Apply) g.Year = y; }
                    if (!g.IsEnabled) { if (Apply) g.IsEnabled = true; reenabled++; }
                    cards.Add($"{g.System}|{newTitle}");
                }

                if (Apply) await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                processed += rows.Count;
                cursor = rows[^1].Id;
            }

            var nextCursor = cursor;
            var remaining = await db.ArcadeGames.CountAsync(g => systems.Contains(g.System) && g.Id > nextCursor);

            w.WriteLine($"processed {processed} row(s):");
            w.WriteLine($"  runnable FBNeo games        : {runnable,6}  → {cards.Count} distinct cards after dedupe");
            w.WriteLine($"    of which title (re)written : {titleChanged,6}");
            w.WriteLine($"  disabled — not in FBNeo DAT : {disabledUnrunnable,6}");
            w.WriteLine($"  disabled — BIOS set         : {disabledBios,6}");
            if (reenabled > 0) w.WriteLine($"  re-enabled (were disabled)  : {reenabled,6}");
            w.WriteLine();
            if (egRunnable.Count > 0) { w.WriteLine("  e.g. runnable:"); foreach (var s in egRunnable) w.WriteLine("    " + s); }
            if (egUnrunnable.Count > 0) { w.WriteLine("  e.g. unrunnable (disabled):"); foreach (var s in egUnrunnable) w.WriteLine("    " + s); }
            w.WriteLine();
            w.WriteLine($"{{ processed: {processed}, remaining: {remaining}, nextCursor: {nextCursor} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}.");
        }
    }
}
