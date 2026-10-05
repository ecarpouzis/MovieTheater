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
    /// Fills <see cref="ArcadeGame.Controls"/> — the cabinet's control profile the touch pad's arcade presets
    /// read (a 4-way stick for Pac-Man, punches-over-kicks for Street Fighter, a trackball zone for
    /// Centipede) — from a MAME <c>-listxml</c> dump, for the MAME-shortname systems.
    ///
    /// <para>Generate the dump with any MAME build (control panels barely change between versions, so an
    /// older binary serves the current FBNeo set; re-run with a matching dump when a newer set is ingested):
    /// <c>mame -listxml &gt; data/arcade/mame-&lt;ver&gt;-listxml.xml</c> (~235 MB for 0.215; read streaming).</para>
    ///
    /// <para>Matches on <see cref="ArcadeGame.CloudRetroGameKey"/> = the MAME shortname. A shortname the
    /// listxml lacks is left untouched (the pad falls back to its standard layout). Only writes when the
    /// value changes, so a re-run is a no-op.</para>
    ///
    /// <para><b>Bulk-job rules</b>: dry-run unless <c>--apply</c>; bounded by <c>--limit</c>, ordered by Id,
    /// resumable via <c>--after &lt;id&gt;</c>; prints <c>{ processed, remaining, nextCursor }</c> per run.</para>
    /// </summary>
    [Command("arcade-controls", Description = "Set each arcade game's touch-pad control profile from a MAME -listxml; dry-run unless --apply.")]
    public class ArcadeControlsCommand : BasicDICommand, ICommand
    {
        [CommandOption("xml", Description = "Path to the MAME -listxml dump. Default data/arcade/mame-0215-listxml.xml.")]
        public string XmlPath { get; set; } = "data/arcade/mame-0215-listxml.xml";

        [CommandOption("systems", Description = "Comma-separated system codes. Default arcade,naomi,atomiswave.")]
        public string Systems { get; set; } = "arcade,naomi,atomiswave";

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("overwrite", Description = "Also rewrite rows that already have a profile (default: fill blanks only, so a hand-set profile survives).")]
        public bool Overwrite { get; set; }

        [CommandOption("limit", Description = "Max rows to process this run (default 10000).")]
        public int Limit { get; set; } = 10000;

        [CommandOption("after", Description = "Resume cursor: skip rows whose Id ≤ this (from a prior nextCursor).")]
        public int After { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeControlsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            XmlPath = RepoDataPath.Resolve(XmlPath);

            var profiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var m in ArcadeControlProfile.Read(XmlPath))
                    if (m.Profile != null && !m.IsBios && !m.IsDevice) profiles[m.Name] = m.Profile;
            }
            catch (Exception ex) { w.WriteLine($"Could not read listxml: {ex.Message}"); return; }

            var systems = Systems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant()).ToArray();
            w.WriteLine($"listxml: {profiles.Count} machines with a control panel — {XmlPath}");
            w.WriteLine($"Systems: {string.Join(", ", systems)}");
            w.WriteLine();

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);

            int set = 0, unchanged = 0, keptExisting = 0, notFound = 0;
            var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var examples = new List<string>();
            var missing = new List<string>();

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
                    if (!profiles.TryGetValue(g.CloudRetroGameKey, out var profile))
                    {
                        notFound++;
                        if (missing.Count < 8) missing.Add($"[{g.System}] {g.CloudRetroGameKey}");
                        continue;
                    }
                    var kind = profile.Split('/')[0] + (profile.EndsWith("/sf") ? " (sf)" : "");
                    byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
                    if (string.Equals(g.Controls, profile, StringComparison.Ordinal)) { unchanged++; continue; }
                    if (g.Controls != null && !Overwrite) { keptExisting++; continue; }
                    if (Apply) g.Controls = profile;
                    set++;
                    if (examples.Count < 12) examples.Add($"[{g.System}] {g.CloudRetroGameKey,-12} {profile,-12} {g.Title}");
                }

                if (Apply) await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                processed += rows.Count;
                cursor = rows[^1].Id;
                w.WriteLine($"  … {processed} row(s) through Id {cursor}");
            }

            var nextCursor = cursor;
            var remaining = await db.ArcadeGames.CountAsync(g => systems.Contains(g.System) && g.Id > nextCursor);

            w.WriteLine();
            w.WriteLine($"processed {processed} row(s):");
            w.WriteLine($"  profile set{(Apply ? "" : " (would set)"),-14}: {set,6}");
            w.WriteLine($"  already correct           : {unchanged,6}");
            w.WriteLine($"  kept (has a profile)      : {keptExisting,6}   (--overwrite to replace)");
            w.WriteLine($"  not in listxml            : {notFound,6}");
            w.WriteLine("  by kind:");
            foreach (var (k, n) in byKind.OrderByDescending(kv => kv.Value)) w.WriteLine($"    {k,-16} {n,6}");
            if (examples.Count > 0) { w.WriteLine("  e.g.:"); foreach (var s in examples) w.WriteLine("    " + s); }
            if (missing.Count > 0) { w.WriteLine("  e.g. not in listxml:"); foreach (var s in missing) w.WriteLine("    " + s); }
            w.WriteLine();
            w.WriteLine($"{{ processed: {processed}, remaining: {remaining}, nextCursor: {nextCursor} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}.");
        }
    }
}
