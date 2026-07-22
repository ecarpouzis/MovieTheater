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
    /// Gives the flycast-arcade systems (naomi, atomiswave) real display names + region/year from a MAME
    /// <c>-listxml</c> dump, so their lobby cards stop showing raw shortnames (<c>18wheels</c> →
    /// "18 Wheeler") and clones/regions collapse into one card via the query-time (System, Title) grouping
    /// (docs/arcade-multidisc-and-metadata-plan.md, docs/arcade-dedupe-multidisc-plan.md).
    ///
    /// <para>Unlike <see cref="ArcadeFbneoResolveCommand"/>, a shortname MISSING from the listxml is NOT
    /// disabled — flycast runs every staged naomi/atomiswave ROM regardless of the DAT, so a miss just means
    /// "no better name available"; the row is left untouched. Only real BIOS/device sets (<c>awbios</c>) are
    /// disabled. Generate the listxml with the local MAME:
    /// <c>mame64 -listxml &lt;shortnames…&gt; &gt; data/arcade/mame-naomi-atomiswave.xml</c>.</para>
    ///
    /// <para>Title comes from the cloneof PARENT's description (so every clone shares it and they dedupe);
    /// Region/Variant are parsed from the description's parenthetical tags via <see cref="ArcadeRomTags"/>
    /// ("Airline Pilots (World, Rev B)" → Region World). <b>Preserves hand-edits</b>: a row is only rewritten
    /// when its current Title still equals its raw shortname (i.e. never curated). Idempotent.</para>
    ///
    /// <para><b>Bulk-job rules</b>: dry-run unless <c>--apply</c>; bounded by <c>--limit</c>, ordered by Id,
    /// resumable via <c>--after &lt;id&gt;</c>.</para>
    /// </summary>
    [Command("arcade-mame-resolve", Description = "Name naomi/atomiswave cards from a MAME -listxml (real titles + region/year, dedupe); dry-run unless --apply.")]
    public class ArcadeMameResolveCommand : BasicDICommand, ICommand
    {
        [CommandOption("xml", Description = "Path to the MAME -listxml dump. Default data/arcade/mame-naomi-atomiswave.xml.")]
        public string XmlPath { get; set; } = "data/arcade/mame-naomi-atomiswave.xml";

        [CommandOption("systems", Description = "Comma-separated system codes to resolve. Default naomi,atomiswave.")]
        public string Systems { get; set; } = "naomi,atomiswave";

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max rows to process this run (default 5000).")]
        public int Limit { get; set; } = 5000;

        [CommandOption("after", Description = "Resume cursor: skip rows whose Id ≤ this (from a prior nextCursor).")]
        public int After { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeMameResolveCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        private const string DisabledBios = "mame-resolve: BIOS/device set (not a game)";

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            FbneoDat dat;
            try { dat = FbneoDat.Load(XmlPath); }
            catch (Exception ex) { w.WriteLine($"Could not load listxml: {ex.Message}"); return; }

            var systems = Systems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant()).ToArray();

            w.WriteLine($"MAME listxml v{dat.Version} ({dat.Count} machines) — {XmlPath}");
            w.WriteLine($"Systems: {string.Join(", ", systems)}");
            w.WriteLine();

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);

            int resolved = 0, titleChanged = 0, regionSet = 0, yearSet = 0, disabledBios = 0,
                notFound = 0, handEdited = 0;
            var cards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var egResolved = new List<string>();
            var egNotFound = new List<string>();

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
                        notFound++;
                        if (egNotFound.Count < 8) egNotFound.Add($"[{g.System}] {key}");
                        continue;   // flycast still runs it — just no better name; leave untouched.
                    }

                    if (entry.IsBios)
                    {
                        if (g.IsEnabled && Apply) g.IsEnabled = false;
                        if (Apply) g.Notes = DisabledBios;
                        disabledBios++;
                        continue;
                    }

                    // Only (re)name a row that was never hand-curated — its Title still equals the shortname.
                    if (!string.Equals(g.Title, key, StringComparison.Ordinal))
                    {
                        handEdited++;
                        cards.Add($"{g.System}|{g.Title}");
                        continue;
                    }

                    resolved++;
                    var newTitle = dat.TitleFor(key);                    // parent's cleaned description → clones share it
                    var newSort = ArcadeNaming.ArticleInvert(newTitle);
                    var (region, variant) = ArcadeRomTags.Parse(entry.Description);

                    if (Apply)
                    {
                        g.Title = newTitle;
                        g.SortTitle = newSort;
                        if (region != ArcadeRomTags.Unknown) g.Region = region;
                        if (variant != ArcadeRomTags.Release) g.Variant = variant;
                        if (g.Year == null && entry.Year is int) g.Year = entry.Year;
                    }
                    titleChanged++;
                    if (region != ArcadeRomTags.Unknown) regionSet++;
                    if (g.Year == null && entry.Year is int) yearSet++;
                    cards.Add($"{g.System}|{newTitle}");
                    if (egResolved.Count < 10) egResolved.Add($"[{g.System}] {key,-12} → \"{newTitle}\"  ({region}, {entry.Year?.ToString() ?? "?"})");
                }

                if (Apply) await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                processed += rows.Count;
                cursor = rows[^1].Id;
            }

            var nextCursor = cursor;
            var remaining = await db.ArcadeGames.CountAsync(g => systems.Contains(g.System) && g.Id > nextCursor);

            w.WriteLine($"processed {processed} row(s):");
            w.WriteLine($"  named from listxml          : {resolved,6}  → {cards.Count} distinct cards after dedupe");
            w.WriteLine($"    region filled             : {regionSet,6}");
            w.WriteLine($"    year filled               : {yearSet,6}");
            w.WriteLine($"  disabled — BIOS/device set  : {disabledBios,6}");
            w.WriteLine($"  left (already hand-named)   : {handEdited,6}");
            w.WriteLine($"  left (not in listxml)       : {notFound,6}");
            w.WriteLine();
            if (egResolved.Count > 0) { w.WriteLine("  e.g. named:"); foreach (var s in egResolved) w.WriteLine("    " + s); }
            if (egNotFound.Count > 0) { w.WriteLine("  e.g. not in listxml (left as-is):"); foreach (var s in egNotFound) w.WriteLine("    " + s); }
            w.WriteLine();
            w.WriteLine($"{{ processed: {processed}, remaining: {remaining}, nextCursor: {nextCursor} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}.");
        }
    }
}
