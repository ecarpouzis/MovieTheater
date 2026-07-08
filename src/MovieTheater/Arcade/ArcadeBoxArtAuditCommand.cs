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
using SixLabors.ImageSharp;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Box-art quality audit: for each card, resolve the libretro box it WOULD use, read its pixel
    /// dimensions (header only — cheap), and flag per-console <b>aspect-ratio outliers</b> — the tall,
    /// mis-formatted, or wrong-shaped boxes (e.g. PS1 "1Xtreme") that stand out from that console's norm.
    /// Flagged cards are the ones to source from IGDB instead. Read-only report by default; with
    /// <c>--flag --apply</c> it tags the outlier's anchor row <c>Notes</c> with "boxart-prefer-igdb" so the
    /// art population routes them to an IGDB cover.
    ///
    /// <para>Run per system for a clean median (each console has its own box shape). Bounded by <c>--limit</c>,
    /// resumable via <c>--after-id</c>. Network-bounded (one small header fetch per card).</para>
    /// </summary>
    [Command("arcade-boxart-audit", Description = "Flag per-console box-art aspect outliers (malformed/wrong boxes) to route to IGDB. Report-only unless --flag --apply.")]
    public class ArcadeBoxArtAuditCommand : BasicDICommand, ICommand
    {
        [CommandOption("system", Description = "System code to audit (one at a time for a clean per-console median). Required.", IsRequired = true)]
        public string System { get; set; } = default!;

        [CommandOption("limit", Description = "Max cards this run (default 4000).")]
        public int Limit { get; set; } = 4000;

        [CommandOption("after-id", Description = "Resume cursor: cards whose min version id is greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("dev", Description = "Outlier threshold: relative aspect deviation from the median (default 0.20 = 20%).")]
        public double DevThreshold { get; set; } = 0.20;

        [CommandOption("out", Description = "Write a TSV report to this path.")]
        public string Out { get; set; } = "";

        [CommandOption("flag", Description = "Tag outlier anchor rows Notes='boxart-prefer-igdb' (needs --apply).")]
        public bool Flag { get; set; }

        [CommandOption("apply", Description = "Persist the --flag tags. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public ArcadeBoxArtAuditCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var sys = System.Trim().ToLowerInvariant();
            if (!ArcadeBoxArt.HasRepo(sys)) { w.WriteLine($"System '{sys}' has no libretro repo to audit."); return; }
            if (string.IsNullOrEmpty(config.MoviePostersDir)) { w.WriteLine("MoviePostersDir not configured."); return; }
            var postersRoot = Path.GetFullPath(config.MoviePostersDir);
            var index = ArcadeBoxArtIndex.Load(postersRoot, sys);
            if (index == null) { w.WriteLine($"No filename index for '{sys}' — run arcade-boxart --index-only first."); return; }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-boxart-audit/1.0");

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);
            var rows = await db.ArcadeGames.Where(g => g.IsEnabled && g.System == sys).ToListAsync();
            var cards = rows.GroupBy(g => g.Title)
                .Select(grp => grp.OrderBy(x => x.Id).ToList())
                .Where(c => c[0].Id > AfterId).OrderBy(c => c[0].Id)
                .Take(Math.Max(1, Limit)).ToList();

            var measured = new List<(List<ArcadeGame> Card, string File, int W, int H, double Aspect)>();
            int lastId = AfterId, noMatch = 0, fetchFail = 0;
            foreach (var card in cards)
            {
                var anchor = card[0]; lastId = anchor.Id;
                var file = index.Match(anchor.Title, card.Select(r => r.Region))
                         ?? index.InferBest(anchor.Title, card.Select(r => r.Region));
                if (file == null) { noMatch++; continue; }
                try
                {
                    var url = $"https://raw.githubusercontent.com/libretro-thumbnails/{ArcadeBoxArt.ThumbRepo[sys].Replace(' ', '_')}/master/Named_Boxarts/{Uri.EscapeDataString(file)}.png";
                    var bytes = await http.GetByteArrayAsync(url);
                    var info = Image.Identify(bytes);
                    if (info == null || info.Width == 0) { fetchFail++; continue; }
                    measured.Add((card, file, info.Width, info.Height, (double)info.Height / info.Width));
                }
                catch { fetchFail++; }
            }

            if (measured.Count == 0) { w.WriteLine("Nothing measured (no matches / fetches failed)."); return; }

            var aspects = measured.Select(m => m.Aspect).OrderBy(x => x).ToList();
            double median = aspects[aspects.Count / 2];

            var outliers = measured
                .Select(m => (m, dev: Math.Abs(m.Aspect - median) / median))
                .Where(x => x.dev > DevThreshold)
                .OrderByDescending(x => x.dev).ToList();

            if (!string.IsNullOrEmpty(Out))
            {
                var lines = new List<string> { "system\ttitle\tfile\tw\th\taspect\tmedian\tdev\toutlier" };
                foreach (var m in measured.OrderByDescending(m => Math.Abs(m.Aspect - median) / median))
                {
                    var dev = Math.Abs(m.Aspect - median) / median;
                    lines.Add($"{sys}\t{m.Card[0].Title}\t{m.File}\t{m.W}\t{m.H}\t{m.Aspect:0.000}\t{median:0.000}\t{dev:0.00}\t{(dev > DevThreshold ? "YES" : "")}");
                }
                await File.WriteAllLinesAsync(Out, lines);
            }

            if (Flag && Apply)
                foreach (var (m, _) in outliers)
                    m.Card[0].Notes = "boxart-prefer-igdb";
            if (Apply) await db.SaveChangesAsync();

            var nextRemaining = await db.ArcadeGames.CountAsync(g => g.IsEnabled && g.System == sys && g.Id > lastId);
            w.WriteLine($"[{sys}] measured {measured.Count} boxes; median aspect (h/w) = {median:0.000}.");
            w.WriteLine($"  {outliers.Count} outliers beyond ±{DevThreshold:P0} (candidates for IGDB replacement); {noMatch} no-match, {fetchFail} fetch-fail.");
            foreach (var (m, dev) in outliers.Take(15))
                w.WriteLine($"    {m.Aspect:0.00} (dev {dev:P0}) [{m.W}x{m.H}] {m.Card[0].Title}  ({m.File})");
            w.WriteLine($"{{ processed: {cards.Count}, remaining: {nextRemaining}, nextAfterId: {lastId} }}");
            if (Flag && !Apply) w.WriteLine("DRY RUN — outliers not tagged. Add --apply to tag Notes='boxart-prefer-igdb'.");
        }
    }
}
