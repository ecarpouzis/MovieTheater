using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.LaunchBox;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Folds lobby cards that are the SAME GAME under a different NAME into one card — the localized
    /// releases the <c>(System, CollapseKey)</c> grouping structurally cannot see. "007 - Ein Quantum Trost"
    /// (nds, German) and "007 - Quantum of Solace" are one game, but their titles fold to different keys, so
    /// the lobby shows two cards; the same holds for every EU-localized DS release, every Japanese title
    /// whose Western name differs (Akumajou Dracula ⇄ Castlevania), and every regional rebrand
    /// (DTM Race Driver 3 ⇄ V8 Supercars Australia 3).
    ///
    /// <para><b>Why it isn't just a rename.</b> <c>arcade-launchbox-rename</c> deliberately SKIPS a card whose
    /// title LaunchBox already knows — and a localized title IS known: it is a
    /// <c>&lt;GameAlternateName&gt;</c>. That is exactly the population this command targets. A merge here is
    /// a rename whose destination already exists.</para>
    ///
    /// <para><b>How a candidate is found</b> — three independent signals, unioned:
    /// <list type="bullet">
    /// <item><c>lb-alias</c> — the card's key is a LaunchBox alias of a game whose canonical name folds to a
    /// DIFFERENT key that also exists as a card in the same system. Several localized cards pointing at a
    /// canonical name we don't carry are paired with each other instead.</item>
    /// <item><c>igdb</c> — two cards in one system carry the same <see cref="ArcadeGame.IgdbId"/>.</item>
    /// <item><c>ra</c> — two cards in one system carry the same <see cref="ArcadeGame.RaGameId"/>.</item>
    /// <item><c>art</c> (<c>--art-discover</c>) — the two covers are near-identical. The other three all
    /// resolve by TITLE somewhere upstream, so they are structurally blind to a pair no title index links:
    /// a coded Saturn dump (<c>0691-atlantis-fre-cd1</c>) has no name to look up at all. This one never
    /// reads the name.</item>
    /// </list>
    /// The first three resolve by TITLE upstream, so all three are individually fallible; the report prints
    /// which fired, and the box-art check is the independent corroboration.</para>
    ///
    /// <para><b>Box art is the tell.</b> Two dumps of one game usually carry the same cover artwork even when
    /// the text on it is in another language. Each candidate's two covers are compared as normalized 12x12
    /// luma grids of the inner 76%×80% crop — the crop drops the platform frame, which otherwise correlates
    /// every DS box with every other DS box at ~0.32 and every GBA box at ~0.56. The score is the mean
    /// product of the two normalized grids: 1.0 = identical artwork, ~0 = unrelated. Absence of similarity is
    /// NOT disqualifying (a Japanese box and its US box are often completely different paintings), which is
    /// why art RANKS candidates rather than gating them.</para>
    ///
    /// <para><b>Guards — a rejected candidate is never merged, in either mode.</b> Numeric tokens must agree
    /// (roman numerals folded), so "Clock Tower 2" can never fold into "Clock Tower"; demo/trial/prototype
    /// markers must agree; the two cards must not each carry a DIFFERENT IgdbId or RaGameId (one signal
    /// contradicting another); neither side may be a romhack (<see cref="ArcadeGame.SourceArchivePath"/> under
    /// the Romhacks tree, or an all-modified card facing a plain release) — a hack that collapses into its
    /// stock card goes invisible AND cross-wires its metadata; and both cards must share a delivery
    /// <see cref="ArcadeGame.Lane"/>.</para>
    ///
    /// <para><b>Applying.</b> <c>--apply</c> alone does nothing: the last step is a judgement call no
    /// title-match heuristic can finish, because one string shape covers both a translated subtitle and a
    /// series sibling ("Yamagata Digital Museum - Autumn" ⇄ "- Spring" is NOT a merge). So an apply run needs
    /// either <c>--pairs</c>, a reviewed TSV of <c>system/srcKey/dstKey</c> (guards still enforced), or an
    /// explicit <c>--auto</c> opt-in to the heuristic tier. Bulk-job rules throughout: bounded by
    /// <c>--limit</c>, resumable via <c>--after-id</c>, idempotent (a merged card stops generating an edge),
    /// and non-destructive — rows are re-titled, never deleted, and each keeps a <c>merged-card</c>
    /// breadcrumb in <see cref="ArcadeGame.Notes"/> naming the title (and cover) it came from.</para>
    ///
    /// <para>After an apply run, re-run <c>arcade-launchbox</c>, <c>arcade-boxart</c>, <c>arcade-igdb</c> and
    /// <c>arcade-rating-weights</c> so the merged cards re-enrich under their canonical name.</para>
    /// </summary>
    [Command("arcade-merge-cards", Description = "Fold same-game/different-name arcade cards (localized titles, regional rebrands) into one card. Dry-run unless --apply with --pairs or --auto.")]
    public class ArcadeMergeCardsCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write merges. Needs --pairs or --auto. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("pairs", Description = "TSV of reviewed merges to apply: system<TAB>srcCollapseKey<TAB>dstCollapseKey (# comments ignored). Guards still apply.")]
        public string Pairs { get; set; } = "";

        [CommandOption("auto", Description = "Apply the heuristic auto tier (art >= --art-strong, or 2+ signals with art >= --art-min) instead of a reviewed list.")]
        public bool Auto { get; set; }

        [CommandOption("limit", Description = "Max merge edges to act on this run (default 200).")]
        public int Limit { get; set; } = 200;

        [CommandOption("after-id", Description = "Resume cursor: only edges whose SOURCE card anchor id is greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("system", Description = "Restrict to these system codes (comma-separated). Omit for all.")]
        public string System { get; set; } = "";

        [CommandOption("zip", Description = "Path to a LaunchBox Metadata.zip (default data/launchbox/Metadata.zip; downloaded if absent).")]
        public string Zip { get; set; } = "data/launchbox/Metadata.zip";

        [CommandOption("refresh", Description = "Re-download the LaunchBox dump even if cached.")]
        public bool Refresh { get; set; }

        [CommandOption("art-base-url", Description = "Fetch box art for the similarity check from this site (e.g. https://theater.carpouzis.com). Omit to read the local posters mount only.")]
        public string ArtBaseUrl { get; set; } = "";

        [CommandOption("art-cache", Description = "Directory to cache fetched box art in (default data/arcade-merge-art).")]
        public string ArtCache { get; set; } = "data/arcade-merge-art";

        [CommandOption("art-min", Description = "Auto tier: minimum art similarity when 2+ signals agree (default 0.55).")]
        public double ArtMin { get; set; } = 0.55;

        [CommandOption("art-strong", Description = "Auto tier: art similarity that qualifies on its own (default 0.90).")]
        public double ArtStrong { get; set; } = 0.90;

        [CommandOption("art-discover", Description = "ALSO generate candidates by comparing every in-scope card's cover with every other's (needs --art-base-url or a posters mount). Finds twins no title index links.")]
        public bool ArtDiscover { get; set; }

        [CommandOption("art-discover-min", Description = "art-discover: minimum cover similarity for a pair to become a candidate (default 0.93).")]
        public double ArtDiscoverMin { get; set; } = 0.93;

        [CommandOption("art-hub-max", Description = "art-discover: drop a cover that matches more than this many other cards — it is stock/series art, not this game's (default 5).")]
        public int ArtHubMax { get; set; } = 5;

        [CommandOption("out", Description = "Write a TSV report of every candidate edge (signals, art score, guard verdict) to this path.")]
        public string Out { get; set; } = "";

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration cfg;

        public ArcadeMergeCardsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            cfg = config;
        }

        /// <summary>One lobby card: every enabled row sharing a (System, CollapseKey), lowest id first.</summary>
        private sealed class Card
        {
            public string System = "";
            public string Key = "";
            public List<ArcadeGame> Rows = new();
            public ArcadeGame Anchor => Rows[0];                  // lowest id — the enrichment/art anchor
            public string Title => Rows[0].Title;
            public HashSet<string> Tokens = new(StringComparer.Ordinal);
            public HashSet<int> Igdb = new();
            public HashSet<int> Ra = new();
            public bool IsRomhack;
            public bool AllModified;                              // every row is a Hack/Pirate/Unlicensed dump
            public string Lane = "cloudretro";
        }

        private sealed class Edge
        {
            public Card Src = default!, Dst = default!;
            public SortedSet<string> Signals = new(StringComparer.Ordinal);
            public double? Art;
            public string Reject = "";
            public string Tier = "";
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (Apply && !Auto && string.IsNullOrWhiteSpace(Pairs))
                throw new CommandException("--apply needs a reviewed list (--pairs <tsv>) or an explicit --auto opt-in. "
                                         + "A merge is a judgement call — see this command's summary for why.");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-merge-cards/1.0");

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(600);

            var requested = System.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Select(s => s.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

            var rows = await db.ArcadeGames.Where(g => g.IsEnabled).ToListAsync();
            if (requested.Count > 0) rows = rows.Where(g => requested.Contains(g.System)).ToList();

            var cards = rows.GroupBy(g => (g.System, g.CollapseKey))
                .ToDictionary(grp => grp.Key, grp =>
                {
                    var ordered = grp.OrderBy(x => x.Id).ToList();
                    return new Card
                    {
                        System = grp.Key.System,
                        Key = grp.Key.CollapseKey,
                        Rows = ordered,
                        Tokens = LaunchBoxNameIndex.Tokenize(ordered[0].Title),
                        Igdb = ordered.Where(x => x.IgdbId.HasValue).Select(x => (int)x.IgdbId!.Value).ToHashSet(),
                        Ra = ordered.Where(x => x.RaGameId.HasValue).Select(x => x.RaGameId!.Value).ToHashSet(),
                        IsRomhack = ordered.Any(IsRomhackRow),
                        AllModified = ordered.All(x => x.Variant is "Hack" or "Pirate" or "Unlicensed"),
                        Lane = ordered[0].Lane ?? "cloudretro",
                    };
                });
            w.WriteLine($"{rows.Count:N0} enabled rows -> {cards.Count:N0} cards.");

            var art = new BoxArtSimilarity(http, ArtBaseUrl, RepoDataPath.Resolve(ArtCache), cfg.MoviePostersDir);

            var edges = Pairs.Length > 0
                ? LoadPairs(RepoDataPath.Resolve(Pairs), cards, w)
                : await GenerateAsync(cards, http, art, w);

            foreach (var e in edges) e.Reject = Guard(e);
            var live = edges.Where(e => e.Reject.Length == 0).ToList();
            w.WriteLine($"{edges.Count:N0} candidate edge(s); {edges.Count - live.Count:N0} rejected by guards.");
            foreach (var grp in edges.Where(e => e.Reject.Length > 0).GroupBy(e => e.Reject).OrderByDescending(g => g.Count()))
                w.WriteLine($"    rejected {grp.Count(),5}  {grp.Key}");

            // Order + bound BEFORE scoring art, so one run costs a bounded number of image fetches.
            live = live.Where(e => e.Src.Anchor.Id > AfterId).OrderBy(e => e.Src.Anchor.Id).ToList();
            var remaining = Math.Max(0, live.Count - Limit);
            var batch = live.Take(Math.Max(1, Limit)).ToList();

            foreach (var e in batch) e.Art ??= await art.ScoreAsync(e.Src.Anchor.Id, e.Dst.Anchor.Id);
            w.WriteLine($"art: {art.Hits} cover(s) resolved, {art.Misses} unavailable "
                      + $"({batch.Count(e => e.Art.HasValue)}/{batch.Count} edges scored).");

            foreach (var e in batch) e.Tier = TierOf(e);

            if (!string.IsNullOrEmpty(Out))
            {
                var report = new List<string> { "system\ttier\tsignals\tart\tsrcKey\tsrcTitle\tsrcAnchor\tsrcRows\tdstKey\tdstTitle\tdstAnchor\tdstRows\treject" };
                report.AddRange(edges.OrderBy(e => e.Src.System, StringComparer.Ordinal).ThenBy(e => e.Src.Key, StringComparer.Ordinal).Select(e =>
                    $"{e.Src.System}\t{e.Tier}\t{string.Join(",", e.Signals)}\t{(e.Art.HasValue ? e.Art.Value.ToString("0.000", CultureInfo.InvariantCulture) : "")}\t"
                  + $"{e.Src.Key}\t{e.Src.Title}\t{e.Src.Anchor.Id}\t{e.Src.Rows.Count}\t"
                  + $"{e.Dst.Key}\t{e.Dst.Title}\t{e.Dst.Anchor.Id}\t{e.Dst.Rows.Count}\t{e.Reject}"));
                await File.WriteAllLinesAsync(Out, report);
                w.WriteLine($"Wrote {Out} ({report.Count - 1} rows).");
            }

            var toMerge = Pairs.Length > 0 ? batch : batch.Where(e => e.Tier == "auto").ToList();
            w.WriteLine();
            w.WriteLine($"{toMerge.Count} merge(s) selected this run"
                      + (Pairs.Length > 0 ? " (reviewed list)" : $" (auto tier; {batch.Count - toMerge.Count} left for review)") + ":");
            int shown = 0;
            foreach (var e in toMerge)
            {
                if (shown++ >= 40) { w.WriteLine($"    ... and {toMerge.Count - 40} more"); break; }
                w.WriteLine($"  [{e.Src.System}] \"{e.Src.Title}\" ({e.Src.Rows.Count} row(s)) -> \"{e.Dst.Title}\""
                          + $"   {string.Join(",", e.Signals)}{(e.Art.HasValue ? $" art={e.Art.Value:0.00}" : " art=n/a")}");
            }

            int mergedRows = 0, artCleared = 0, applied = 0;
            if (Apply)
            {
                var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd");
                // Follow chains: if this run also merges the destination away, land on the final card.
                var redirect = new Dictionary<(string, string), (string, string)>();
                foreach (var e in toMerge) redirect[(e.Src.System, e.Src.Key)] = (e.Dst.System, e.Dst.Key);
                foreach (var e in toMerge)
                {
                    var target = Resolve(redirect, (e.Dst.System, e.Dst.Key));
                    if (target == (e.Src.System, e.Src.Key)) continue;          // a cycle — leave both alone
                    var dst = cards[target];
                    var newTitle = dst.Title;
                    var newSort = ArcadeNaming.ArticleInvert(newTitle);
                    var newKey = ArcadeNaming.CollapseKey(newTitle);
                    bool dstHasArt = dst.Rows.Any(r => r.BoxArtPath != null);
                    applied++;
                    foreach (var g in e.Src.Rows)
                    {
                        var was = g.Title;
                        g.Title = newTitle;
                        g.SortTitle = newSort;
                        g.CollapseKey = newKey;
                        // The image route serves whichever sibling's cached file it finds FIRST, so a merged
                        // card holding two covers would answer nondeterministically. Drop the losing
                        // (localized) cover's POINTER — the file on the shared mount is never touched, and the
                        // breadcrumb below records it — so the canonical card's box wins. If the destination
                        // has no art at all, keep this one: some cover beats none.
                        string wasArt = null;
                        if (dstHasArt && g.BoxArtPath != null) { wasArt = g.BoxArtPath; g.BoxArtPath = null; artCleared++; }
                        var note = $"merged-card {stamp}: was \"{was}\" ({e.Src.Key})" + (wasArt != null ? $", art {wasArt}" : "");
                        g.Notes = string.IsNullOrWhiteSpace(g.Notes) ? note : g.Notes.TrimEnd() + "\n" + note;
                        mergedRows++;
                    }
                }
                await db.SaveChangesAsync();
            }

            w.WriteLine();
            w.WriteLine($"{{ processed: {batch.Count}, merged: {applied}, rowsRetitled: {mergedRows}, artPointersCleared: {artCleared}, "
                      + $"remaining: {remaining}, nextAfterId: {(batch.Count > 0 ? batch[^1].Src.Anchor.Id : AfterId)} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply plus --pairs <tsv> or --auto.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after-id {batch[^1].Src.Anchor.Id} --apply.");
            else w.WriteLine("Done. Now re-run: arcade-launchbox --apply, arcade-boxart --apply, arcade-igdb, arcade-rating-weights --apply.");
        }

        // ── candidate generation ─────────────────────────────────────────────────────────────────────────

        private async Task<List<Edge>> GenerateAsync(Dictionary<(string, string), Card> cards, HttpClient http,
                                                     BoxArtSimilarity art, ConsoleWriter w)
        {
            var zipPath = await LaunchBoxMetadata.EnsureDumpAsync(http, RepoDataPath.Resolve(Zip), Refresh, w.WriteLine);
            var index = LaunchBoxMetadata.BuildNameIndex(zipPath, w.WriteLine);

            var edges = new Dictionary<(string, string, string), Edge>();
            void Add(Card src, Card dst, string signal)
            {
                if (src.Key == dst.Key || src.System != dst.System) return;
                // The dictionary key is order-independent so two signals for one pair land on one edge; the
                // FIRST signal to see the pair fixes the merge direction.
                var k = string.CompareOrdinal(src.Key, dst.Key) < 0
                    ? (src.System, src.Key, dst.Key) : (src.System, dst.Key, src.Key);
                if (!edges.TryGetValue(k, out var e)) edges[k] = e = new Edge { Src = src, Dst = dst };
                e.Signals.Add(signal);
            }

            // 1. LaunchBox alias -> canonical name. The DESTINATION is always the canonically-named card, so a
            //    whole family of localized titles converges on the one name LaunchBox (and therefore its box
            //    art, rating and alias set) actually indexes.
            var orphanedCanonical = new Dictionary<(string, string), List<Card>>();
            foreach (var card in cards.Values.OrderBy(c => c.Anchor.Id))
            {
                var hit = index.ExactLookup(card.System, card.Key);
                if (hit == null) continue;
                var canonKey = ArcadeNaming.CollapseKey(hit.Name);
                if (canonKey.Length == 0 || canonKey == card.Key) continue;   // already the canonical name
                if (cards.TryGetValue((card.System, canonKey), out var dst)) { Add(card, dst, "lb-alias"); continue; }
                // No card carries the canonical name — but several localized cards may point AT it, and those
                // are still one game. Pair them with each other.
                var slot = (card.System, canonKey);
                if (!orphanedCanonical.TryGetValue(slot, out var peers)) orphanedCanonical[slot] = peers = new List<Card>();
                peers.Add(card);
            }
            foreach (var peers in orphanedCanonical.Values.Where(p => p.Count > 1))
            {
                var dst = peers.OrderBy(p => p.Anchor.Id).First();
                foreach (var src in peers.Where(p => p != dst)) Add(src, dst, "lb-alias");
            }

            // 2/3. Shared IgdbId / RaGameId inside one system. Destination = the card with the most rows (then
            //      the lowest anchor) — the better-populated card is the one worth keeping the name of.
            void BySharedId(Func<Card, IEnumerable<int>> ids, string signal)
            {
                var groups = new Dictionary<(string, int), List<Card>>();
                foreach (var card in cards.Values)
                    foreach (var id in ids(card))
                    {
                        var k = (card.System, id);
                        if (!groups.TryGetValue(k, out var l)) groups[k] = l = new List<Card>();
                        if (!l.Contains(card)) l.Add(card);
                    }
                foreach (var g in groups.Values.Where(g => g.Count > 1))
                {
                    var dst = g.OrderByDescending(c => c.Rows.Count).ThenBy(c => c.Anchor.Id).First();
                    foreach (var src in g.Where(c => c != dst)) Add(src, dst, signal);
                }
            }
            BySharedId(c => c.Igdb, "igdb");
            BySharedId(c => c.Ra, "ra");

            // 4. Cover-first discovery. The three signals above all resolve by TITLE somewhere upstream, so
            //    they are blind to a pair no title index links — a coded Saturn dump ("0691-atlantis-fre-cd1")
            //    has no name to look up, and a Japanese title whose Western name shares not one token can only
            //    be found by something that never reads the name. Two dumps of one game usually carry the same
            //    cover art, so compare every in-scope card's cover with every other's in the same system.
            if (ArtDiscover) await DiscoverByArtAsync(cards, art, Add, w);

            return edges.Values.ToList();
        }

        /// <summary>All-pairs cover comparison inside each system. Only cards whose cover is ALREADY cached
        /// are probed — a card with no <see cref="ArcadeGame.BoxArtPath"/> would send /ArcadeImage off through
        /// the libretro → IGDB → SteamGridDB → web-search cascade, and a sweep of tens of thousands of those
        /// is not something a catalog-hygiene pass gets to spend.
        ///
        /// <para>A cover that matches MORE than <c>--art-hub-max</c> other cards is stock or series art (one
        /// publisher template across a shovelware line, a compilation's shared sleeve), not this game's own —
        /// every pair it forms is dropped and the hub is logged, because a hub that slips through pulls a
        /// dozen unrelated games onto one card in a single apply.</para></summary>
        private async Task DiscoverByArtAsync(Dictionary<(string, string), Card> cards, BoxArtSimilarity art,
                                              Action<Card, Card, string> add, ConsoleWriter w)
        {
            if (ArtBaseUrl.Length == 0 && string.IsNullOrEmpty(cfg.MoviePostersDir))
            { w.WriteLine("--art-discover needs --art-base-url or a posters mount; skipping."); return; }

            var pool = cards.Values.Where(c => c.Rows.Any(r => r.BoxArtPath != null))
                                   .GroupBy(c => c.System)
                                   .OrderByDescending(g => g.Count()).ToList();
            w.WriteLine($"art-discover: {pool.Sum(g => g.Count()):N0} card(s) with a cached cover across "
                      + $"{pool.Count} system(s) — reading covers…");

            int pairs = 0, hubs = 0, done = 0;
            foreach (var grp in pool)
            {
                var list = new List<(Card Card, float[] F)>();
                foreach (var c in grp)
                {
                    var f = await art.FeatureAsync(c.Anchor.Id);
                    if (f != null) list.Add((c, f));
                    if (++done % 2000 == 0) w.WriteLine($"    … {done:N0} cover(s) read");
                }
                if (list.Count < 2) continue;

                // Collect every over-threshold pair, then drop the hubs before emitting anything.
                var hits = new List<(int A, int B, double S)>();
                var degree = new int[list.Count];
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var sc = BoxArtSimilarity.Score(list[i].F, list[j].F);
                        if (sc < ArtDiscoverMin) continue;
                        hits.Add((i, j, sc));
                        degree[i]++; degree[j]++;
                    }
                foreach (var (i, j, _) in hits)
                {
                    if (degree[i] > ArtHubMax || degree[j] > ArtHubMax) continue;
                    add(list[i].Card, list[j].Card, "art");
                    pairs++;
                }
                int hubCount = degree.Count(d => d > ArtHubMax);
                hubs += hubCount;
                if (hubCount > 0)
                    w.WriteLine($"    [{grp.Key}] {hubCount} hub cover(s) ignored (matched > {ArtHubMax} cards each) — "
                              + string.Join(", ", Enumerable.Range(0, list.Count).Where(k => degree[k] > ArtHubMax)
                                    .Take(5).Select(k => $"\"{list[k].Card.Title}\" x{degree[k]}")));
            }
            w.WriteLine($"art-discover: {pairs:N0} candidate pair(s) from cover similarity ≥ {ArtDiscoverMin:0.00} "
                      + $"({hubs} hub cover(s) ignored).");
        }

        private static List<Edge> LoadPairs(string path, Dictionary<(string, string), Card> cards, ConsoleWriter w)
        {
            var edges = new List<Edge>();
            var seen = new HashSet<(string, string)>();
            int bad = 0, dup = 0;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var f = line.Split('\t');
                if (f.Length < 3) { bad++; continue; }
                var sys = f[0].Trim().ToLowerInvariant();
                if (!cards.TryGetValue((sys, f[1].Trim()), out var src) || !cards.TryGetValue((sys, f[2].Trim()), out var dst))
                { bad++; continue; }
                if (!seen.Add((sys, f[1].Trim()))) { dup++; continue; }
                var e = new Edge { Src = src, Dst = dst };
                e.Signals.Add("reviewed");
                if (f.Length > 3 && f[3].Trim().Length > 0)
                    foreach (var s in f[3].Split(',', StringSplitOptions.RemoveEmptyEntries)) e.Signals.Add(s.Trim());
                edges.Add(e);
            }
            w.WriteLine($"Loaded {edges.Count:N0} reviewed pair(s) from {path}"
                      + (bad > 0 ? $"; {bad} line(s) skipped (malformed, or naming a card that no longer exists — e.g. already merged)" : "")
                      + (dup > 0 ? $"; {dup} duplicate source(s) skipped" : "") + ".");
            return edges;
        }

        // ── guards ───────────────────────────────────────────────────────────────────────────────────────

        private static readonly HashSet<string> DemoMarkers = new(StringComparer.Ordinal)
        {
            "demo", "trial", "sample", "sampler", "preview", "taikenban", "taikenhan", "hibaihin", "otameshi",
            "proto", "prototype", "beta", "kiosk",
        };

        private static bool IsRomhackRow(ArcadeGame g) =>
            g.SourceArchivePath != null &&
            g.SourceArchivePath.Contains(@"\Romhacks\", StringComparison.OrdinalIgnoreCase);

        private static string Guard(Edge e)
        {
            var a = e.Src; var b = e.Dst;
            if (a.Key == b.Key) return "same-card";
            if (a.System != b.System) return "cross-system";
            if (a.Lane != b.Lane) return "lane-mismatch";
            // Sequel / volume numbers must agree — "Clock Tower 2" is not "Clock Tower". Tokenize folds roman
            // numerals to digits, so "Streets of Rage II" ⇄ "... 2" still passes.
            var na = a.Tokens.Where(t => t.All(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
            var nb = b.Tokens.Where(t => t.All(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
            if (!na.SetEquals(nb)) return "number-mismatch";
            if (!a.Tokens.Where(DemoMarkers.Contains).ToHashSet(StringComparer.Ordinal)
                  .SetEquals(b.Tokens.Where(DemoMarkers.Contains))) return "demo-marker-mismatch";
            // One signal contradicting another: both cards resolved upstream, and to different games.
            if (a.Igdb.Count > 0 && b.Igdb.Count > 0 && !a.Igdb.Overlaps(b.Igdb)) return "igdb-conflict";
            if (a.Ra.Count > 0 && b.Ra.Count > 0 && !a.Ra.Overlaps(b.Ra)) return "ra-conflict";
            // Romhacks must never collapse into a stock card: the hack goes invisible and its rating, summary
            // and box art cross-wire with the stock rows (arcade-add-system references/romhacks.md).
            if (a.IsRomhack || b.IsRomhack) return "romhack";
            if (a.AllModified != b.AllModified) return "hack-vs-release";
            return "";
        }

        private string TierOf(Edge e)
        {
            if (e.Art is double s && s >= ArtStrong) return "auto";
            if (e.Signals.Count >= 2 && e.Art is double t && t >= ArtMin) return "auto";
            return "review";
        }

        private static (string, string) Resolve(Dictionary<(string, string), (string, string)> redirect, (string, string) start)
        {
            var seen = new HashSet<(string, string)> { start };
            var cur = start;
            while (redirect.TryGetValue(cur, out var next) && seen.Add(next)) cur = next;
            return cur;
        }
    }
}
