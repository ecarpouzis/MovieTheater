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

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Bulk box art for the arcade catalog, from the community libretro-thumbnails repos, downscaled to
    /// thumbnails (via <see cref="ArcadeBoxArt"/> — the same fetch the on-demand <c>/ArcadeImage</c> route
    /// uses). Works <b>per card</b> (one game across its ROM versions), not per row: it matches by TITLE via
    /// the per-system filename index (so word-order / region-tag / TOSEC drift still resolves), and caches
    /// exactly ONE PNG per card (keyed by the card's lowest version id) to conserve space.
    ///
    /// <para>Bulk-job rules: bounded by <c>--limit</c>, resumable via <c>--after-id</c> (a card's min id),
    /// reports <c>{fetched, linked, missed, remaining, nextAfterId}</c>, idempotent (skips cards that already
    /// have art unless <c>--overwrite</c>), writes nothing without <c>--apply</c>. Run where the posters
    /// mount is present. Pass <c>--refresh-index</c> (once, or when the DATs move) to (re)download each
    /// system's filename list first — that's what lifts match rates on the drifted systems.</para>
    /// </summary>
    [Command("arcade-boxart", Description = "Fetch arcade box art thumbnails from libretro-thumbnails (dry-run unless --apply).")]
    public class ArcadeBoxArtCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write files + rows. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max cards to attempt this run (default 200).")]
        public int Limit { get; set; } = 200;

        [CommandOption("after-id", Description = "Resume cursor: only cards whose min version id is greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("system", Description = "Restrict to one system code (e.g. dc, sms, n64).")]
        public string System { get; set; } = "";

        [CommandOption("overwrite", Description = "Re-fetch cards that already have box art.")]
        public bool Overwrite { get; set; }

        [CommandOption("refresh-index", Description = "(Re)download each system's libretro filename index before matching.")]
        public bool RefreshIndex { get; set; }

        [CommandOption("index-only", Description = "Only (re)download the filename indexes, then stop.")]
        public bool IndexOnly { get; set; }

        [CommandOption("report-misses", Description = "Write a TSV of cards the index can't match, with fuzzy proposals, then stop (offline; needs indexes built).")]
        public string ReportMisses { get; set; } = "";

        [CommandOption("thumb-px", Description = "Max thumbnail dimension in px (default 300).")]
        public int ThumbPx { get; set; } = 300;

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public ArcadeBoxArtCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (string.IsNullOrEmpty(config.MoviePostersDir))
            { w.WriteLine("MoviePostersDir is not configured — run this where the posters mount is present."); return; }
            var postersRoot = Path.GetFullPath(config.MoviePostersDir);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-boxart/1.0");

            var sysFilter = System.Trim().ToLowerInvariant();
            var repoSystems = ArcadeBoxArt.ThumbRepo.Keys
                .Where(s => sysFilter.Length == 0 || s == sysFilter).ToList();

            // (Re)download the per-system filename indexes. Always writes (a plain cache file), so it's fine
            // ahead of a dry run — it makes no catalog changes.
            if (RefreshIndex || IndexOnly)
            {
                w.WriteLine("Refreshing libretro filename indexes…");
                foreach (var sys in repoSystems)
                {
                    try
                    {
                        var n = await ArcadeBoxArtIndex.RefreshAsync(http, postersRoot, sys);
                        w.WriteLine($"  {sys}: {n} filenames");
                    }
                    catch (Exception ex) { w.WriteLine($"  {sys}: index refresh failed ({ex.Message})"); }
                }
                if (IndexOnly) { w.WriteLine("Index refresh done."); return; }
            }

            await using var db = await dbFactory.CreateDbContextAsync();

            // Load the candidate rows once and group into cards (System+Title). One PNG per card.
            var rows = await db.ArcadeGames
                .Where(g => g.IsEnabled && repoSystems.Contains(g.System))
                .ToListAsync();

            var cards = rows.GroupBy(g => new { g.System, g.Title })
                .Select(grp => new Card(grp.Key.System, grp.Key.Title, grp.OrderBy(x => x.Id).ToList()))
                .Where(c => c.MinId > AfterId)
                .OrderBy(c => c.MinId)
                .ToList();

            var batch = cards.Take(Math.Max(1, Limit)).ToList();
            var remaining = cards.Count - batch.Count;

            var indexes = new Dictionary<string, ArcadeBoxArtIndex?>();
            ArcadeBoxArtIndex? IndexFor(string sys) =>
                indexes.TryGetValue(sys, out var i) ? i : (indexes[sys] = ArcadeBoxArtIndex.Load(postersRoot, sys));

            // Offline miss report: for every card, does the index match? Emit the misses + fuzzy proposals so
            // the genuinely-renamed ones can be curated into the alias map. No network, no writes.
            if (!string.IsNullOrWhiteSpace(ReportMisses))
            {
                var lines = new List<string> { "system\ttitle\tregions\tproposal1\tcov1\tproposal2\tproposal3\talias" };
                int rMatched = 0, rInferred = 0, rMissed = 0, rNoIndex = 0;
                var perSystem = new SortedDictionary<string, (int matched, int inferred, int missed)>();
                foreach (var card in rows.GroupBy(g => new { g.System, g.Title })
                             .Select(grp => new Card(grp.Key.System, grp.Key.Title, grp.OrderBy(x => x.Id).ToList()))
                             .OrderBy(c => c.System).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase))
                {
                    var idx = IndexFor(card.System);
                    if (idx == null) { rNoIndex++; continue; }
                    var regions = card.Rows.Select(r => r.Region).ToList();
                    perSystem.TryGetValue(card.System, out var pc);
                    if (idx.Match(card.Title, regions) != null) { rMatched++; perSystem[card.System] = (pc.matched + 1, pc.inferred, pc.missed); continue; }
                    if (idx.InferBest(card.Title, regions) != null) { rInferred++; perSystem[card.System] = (pc.matched, pc.inferred + 1, pc.missed); continue; }
                    rMissed++; perSystem[card.System] = (pc.matched, pc.inferred, pc.missed + 1);
                    var f = idx.Fuzzy(card.Title, regions, 3);
                    string p1 = f.ElementAtOrDefault(0).File ?? "", p2 = f.ElementAtOrDefault(1).File ?? "", p3 = f.ElementAtOrDefault(2).File ?? "";
                    string cov1 = f.Count > 0 ? f[0].Coverage.ToString("0.00") : "";
                    var reg = string.Join(",", regions.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct());
                    string alias = idx.ContiguousProposal(card.Title, regions) ?? "";
                    lines.Add($"{card.System}\t{card.Title}\t{reg}\t{p1}\t{cov1}\t{p2}\t{p3}\t{alias}");
                }
                await File.WriteAllLinesAsync(ReportMisses, lines);
                int rCovered = rMatched + rInferred, rTotal = rCovered + rMissed;
                w.WriteLine($"Wrote {ReportMisses}: {rMatched} matched + {rInferred} inferred = {rCovered}/{rTotal} covered ({rMissed} still missing); {rNoIndex} cards with no index.");
                foreach (var kv in perSystem)
                {
                    var v = kv.Value; int tot = v.matched + v.inferred + v.missed;
                    w.WriteLine($"  {kv.Key,-8}: {v.matched + v.inferred,5}/{tot,-5} covered ({v.inferred} inferred, {v.missed} missing)");
                }
                return;
            }

            int fetched = 0, linked = 0, missed = 0, lastId = AfterId;
            foreach (var card in batch)
            {
                lastId = card.MinId;
                var rel = $"arcade/{card.System}/{card.MinId}.png";
                var dest = Path.Combine(postersRoot, rel.Replace('/', Path.DirectorySeparatorChar));

                // Already covered? A stored BoxArtPath, or the card file already on disk → just heal the
                // pointer if needed. Skip the network entirely unless --overwrite.
                bool hasArt = card.Rows.Any(r => !string.IsNullOrWhiteSpace(r.BoxArtPath)) || File.Exists(dest);
                if (hasArt && !Overwrite)
                {
                    if (card.Anchor.BoxArtPath == null && File.Exists(dest))
                    { if (Apply) card.Anchor.BoxArtPath = rel; linked++; }
                    continue;
                }

                var thumb = await ArcadeBoxArt.TryFetchThumbnailForCardAsync(
                    http, card.System, card.Title,
                    card.Rows.Select(r => r.Region), card.Rows.Select(r => r.CloudRetroGameKey), ThumbPx, IndexFor(card.System));
                if (thumb == null) { missed++; w.WriteLine($"  ? [{card.System}] {card.Title} (no match)"); continue; }

                if (Apply)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await File.WriteAllBytesAsync(dest, thumb);
                    card.Anchor.BoxArtPath = rel;
                }
                fetched++;
                w.WriteLine($"  + [{card.System}] {card.Title}");
            }

            if (Apply && (fetched > 0 || linked > 0)) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"this run: {fetched} fetched, {linked} linked (existing file), {missed} missed.");
            w.WriteLine($"{{ fetched: {fetched}, linked: {linked}, missed: {missed}, remaining: {remaining}, nextAfterId: {lastId} }}");
            if (!Apply) w.WriteLine("DRY RUN — no files or rows written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after-id {lastId}.");
        }

        // One lobby card: a game and its ROM versions (same System+Title). The lowest-id row is the anchor
        // that owns the single shared PNG (arcade/{system}/{MinId}.png).
        private sealed class Card
        {
            public string System { get; }
            public string Title { get; }
            public List<ArcadeGame> Rows { get; }
            public ArcadeGame Anchor => Rows[0];
            public int MinId => Rows[0].Id;
            public Card(string system, string title, List<ArcadeGame> rows) { System = system; Title = title; Rows = rows; }
        }
    }
}
