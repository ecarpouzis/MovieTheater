using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.LaunchBox;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Correct oddly-named / misspelled arcade cards by snapping their Title onto the canonical LaunchBox
    /// name, so the exact-title enrichment (LaunchBox ratings, libretro box art) can then match them. The
    /// de-dupe ingest leaves a tail of cards whose Title normalizes to a key LaunchBox doesn't carry —
    /// dump-code / catalog-serial noise leaking in (<c>Ys IV - The Dawn of Ys {hcd3051-4-1108}</c>,
    /// <c>Xmen-Cota ... T-8108h</c>, <c>Virtuoso 960514</c>), or a real spelling slip (<c>Aldynes - The
    /// Misson Code</c>). This finds a canonical name two ways:
    /// <list type="number">
    /// <item><b>clean-exact</b> — strip the noise (<c>{...}</c>, trailing catalog serials, 6-digit date
    /// codes, doubled spaces), and if the cleaned key is now a real LaunchBox title, take it. Zero-risk.</item>
    /// <item><b>fuzzy</b> — the closest LaunchBox primary name by combined token + edit-distance score,
    /// accepted only on a clear, high-confidence single winner (all four gates below).</item>
    /// </list>
    ///
    /// <para><b>Guards (this WRITES to the shared prod DB).</b> Dry-run unless <c>--apply</c>; a card whose
    /// name ALREADY matches LaunchBox is skipped (it's blank because LaunchBox has no rating, not because
    /// the name is wrong — never re-touch a good name); fuzzy accepts nothing ambiguous; a rename rewrites
    /// the WHOLE <c>(System, CollapseKey)</c> group (Title + SortTitle + CollapseKey) so version siblings
    /// stay collapsed; a rename whose new key lands on a DIFFERENT existing card is flagged
    /// <c>merges-into-existing</c> in the report (usually a welcome dedupe, but visible). Bounded by
    /// <c>--limit</c>, resumable via <c>--after-id</c>, idempotent (a corrected card matches on the next
    /// pass and is skipped). After an apply pass, re-run <c>arcade-launchbox</c> + <c>arcade-boxart</c> +
    /// <c>arcade-igdb</c> + <c>arcade-rating-weights</c> to fill the now-matchable cards.</para>
    /// </summary>
    [Command("arcade-launchbox-rename", Description = "Snap mis-named blank cards onto their canonical LaunchBox name (clean-exact + conservative fuzzy). Dry-run unless --apply.")]
    public class ArcadeLaunchBoxRenameCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write renames. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max candidate cards to examine this run (default 500).")]
        public int Limit { get; set; } = 500;

        [CommandOption("after-id", Description = "Resume cursor: only cards whose anchor (min version) id is greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("system", Description = "Restrict to these system codes (comma-separated, e.g. pce,saturn,ps1). Omit for all LaunchBox-mapped systems.")]
        public string System { get; set; } = "";

        [CommandOption("zip", Description = "Path to a Metadata.zip (default data/launchbox/Metadata.zip; downloaded if absent).")]
        public string Zip { get; set; } = "data/launchbox/Metadata.zip";

        [CommandOption("refresh", Description = "Re-download the LaunchBox dump even if cached.")]
        public bool Refresh { get; set; }

        [CommandOption("exclude-ids", Description = "Anchor ids to skip entirely (comma-separated) — for rows a dry-run review rejected.")]
        public string ExcludeIds { get; set; } = "";

        [CommandOption("out", Description = "Write a full TSV report (every candidate + proposal + metrics) to this path.")]
        public string Out { get; set; } = "";

        // Conservative fuzzy gates. A fuzzy rename fires only when ALL hold — see BestFuzzy for the metrics.
        [CommandOption("min-score", Description = "Fuzzy: minimum combined score (½·token-F1 + ½·char-sim). Default 0.85.")]
        public double MinScore { get; set; } = 0.85;

        [CommandOption("min-charsim", Description = "Fuzzy: minimum character similarity (1 - editDist/len). Default 0.72.")]
        public double MinCharSim { get; set; } = 0.72;

        [CommandOption("min-gap", Description = "Fuzzy: minimum score lead of the winner over the runner-up. Default 0.10.")]
        public double MinGap { get; set; } = 0.10;

        [CommandOption("min-f1", Description = "Fuzzy: minimum token-F1 of the winner. Default 0.60.")]
        public double MinF1 { get; set; } = 0.60;

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeLaunchBoxRenameCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        // Trailing catalog/serial (needs a letter-then-digits shape or a "X-1234" hyphen — never a bare
        // number, so "Area 51" / "F1 2000" survive) and a trailing 6-digit date code ("960514").
        private static readonly Regex Braces = new(@"\{[^}]*\}", RegexOptions.Compiled);
        private static readonly Regex TrailingSerialHyphen = new(@"\s+[A-Za-z]{1,4}-\d{2,}[A-Za-z0-9\-]*\s*$", RegexOptions.Compiled);
        private static readonly Regex TrailingSerialGlued = new(@"\s+[A-Za-z]{2,}\d{3,}[A-Za-z0-9\-]*\s*$", RegexOptions.Compiled);
        private static readonly Regex TrailingDate = new(@"\s+\d{6}\s*$", RegexOptions.Compiled);
        private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

        private static string StripNoise(string title)
        {
            var t = Braces.Replace(title, " ");
            t = TrailingSerialHyphen.Replace(t, " ");
            t = TrailingSerialGlued.Replace(t, " ");
            t = TrailingDate.Replace(t, " ");
            t = MultiSpace.Replace(t, " ").Trim();
            return t;
        }

        private enum Kind { AlreadyOk, Clean, Fuzzy, NoMatch }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-launchbox-rename/1.0");

            var zipPath = await LaunchBoxMetadata.EnsureDumpAsync(http, Zip, Refresh, w.WriteLine);
            var index = LaunchBoxMetadata.BuildNameIndex(zipPath, w.WriteLine);

            var mapped = new HashSet<string>(LaunchBoxMetadata.PlatformToSystem.Values, StringComparer.Ordinal);
            var requested = System.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Select(s => s.ToLowerInvariant()).ToList();
            var systems = requested.Count > 0
                ? requested.Where(mapped.Contains).ToHashSet(StringComparer.Ordinal)
                : mapped;
            var skippedReq = requested.Where(s => !mapped.Contains(s)).ToList();
            if (skippedReq.Count > 0)
                w.WriteLine($"Ignoring unmapped system(s) LaunchBox can't help: {string.Join(", ", skippedReq)}");

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(300);

            // All enabled rows for the in-scope systems, grouped into cards; anchor = lowest-id row.
            var rows = await db.ArcadeGames
                .Where(g => g.IsEnabled && systems.Contains(g.System))
                .ToListAsync();
            var groups = rows.GroupBy(g => new { g.System, g.CollapseKey })
                             .ToDictionary(grp => (grp.Key.System, grp.Key.CollapseKey),
                                           grp => grp.OrderBy(x => x.Id).ToList());
            // Existing keys per system, to detect a rename that merges into a different card.
            var keysBySystem = rows.GroupBy(g => g.System)
                                   .ToDictionary(g => g.Key,
                                                 g => g.Select(x => x.CollapseKey).ToHashSet(StringComparer.Ordinal));

            var excluded = ExcludeIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                     .Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n > 0).ToHashSet();

            var candidates = groups.Values
                .Select(g => g[0])
                .Where(a => a.LaunchBoxRating == null && a.Id > AfterId && !excluded.Contains(a.Id))
                .OrderBy(a => a.Id)
                .ToList();
            var batch = candidates.Take(Math.Max(1, Limit)).ToList();
            var remaining = candidates.Count - batch.Count;

            var report = new List<string> { "system\tanchorId\tcurrentTitle\tkind\tproposedName\tscore\tcharSim\tf1\tsecondScore\tlbRated\tmergesInto\thadArt" };
            int alreadyOk = 0, clean = 0, fuzzy = 0, noMatch = 0, merges = 0, tooLong = 0, samples = 0, lastId = AfterId;

            foreach (var anchor in batch)
            {
                lastId = anchor.Id;
                var sys = anchor.System;
                var rawKey = LaunchBoxMetadata.NormalizeTitle(anchor.Title);

                // Name already IS a LaunchBox title → not a naming problem; leave it alone.
                if (index.ExactLookup(sys, rawKey) != null) { alreadyOk++; continue; }

                Kind kind; string proposed = ""; double score = 0, sim = 0, f1 = 0, secondScore = 0; bool lbRated = false;

                var cleaned = StripNoise(anchor.Title);
                var cleanKey = LaunchBoxMetadata.NormalizeTitle(cleaned);
                var cleanHit = cleanKey != rawKey ? index.ExactLookup(sys, cleanKey) : null;

                if (cleanHit != null)
                {
                    kind = Kind.Clean; proposed = cleanHit.Name; score = 1.0; sim = 1.0; f1 = 1.0; lbRated = cleanHit.Rated;
                }
                else
                {
                    var hit = index.BestFuzzy(sys, LaunchBoxNameIndex.Tokenize(cleaned), cleanKey);
                    bool accept = hit is { } h
                                  && h.F1 >= MinF1 && h.CharSim >= MinCharSim
                                  && h.Score >= MinScore && (h.Score - h.SecondScore) >= MinGap;
                    if (hit is { } hh) { score = hh.Score; sim = hh.CharSim; f1 = hh.F1; secondScore = hh.SecondScore; proposed = hh.Best.Name; lbRated = hh.Best.Rated; }
                    kind = accept ? Kind.Fuzzy : Kind.NoMatch;
                    if (!accept) proposed = hit is { } sug ? sug.Best.Name : ""; // report the near-miss as a hint only
                }

                bool willRename = kind is Kind.Clean or Kind.Fuzzy;
                string mergesInto = "";
                if (willRename)
                {
                    if (proposed.Length > 200) { tooLong++; kind = Kind.NoMatch; willRename = false; }
                    else
                    {
                        var newKey = ArcadeNaming.CollapseKey(proposed);
                        if (newKey != anchor.CollapseKey
                            && keysBySystem.TryGetValue(sys, out var ks) && ks.Contains(newKey))
                        { mergesInto = newKey; merges++; }
                    }
                }

                if (kind == Kind.Clean) clean++;
                else if (kind == Kind.Fuzzy) fuzzy++;
                else if (kind == Kind.NoMatch) noMatch++;

                if (!string.IsNullOrEmpty(Out))
                    report.Add($"{sys}\t{anchor.Id}\t{anchor.Title}\t{kind}\t{proposed}\t{score:0.000}\t{sim:0.000}\t{f1:0.000}\t{secondScore:0.000}\t{(lbRated ? "yes" : "no")}\t{mergesInto}\t{(anchor.BoxArtPath == null ? "no" : "yes")}");

                if (willRename && samples < 25)
                {
                    w.WriteLine($"  {(kind == Kind.Clean ? "≈" : "~")} [{sys}] {anchor.Id}: \"{anchor.Title}\" → \"{proposed}\""
                              + (kind == Kind.Fuzzy ? $"  (score {score:0.00}, sim {sim:0.00}, gap {score - secondScore:0.00})" : "")
                              + (lbRated ? "" : "  [LB unrated]")
                              + (mergesInto.Length > 0 ? "  [merges-into-existing]" : ""));
                    samples++;
                }

                if (Apply && willRename)
                {
                    var newTitle = proposed;
                    var newSort = ArcadeNaming.ArticleInvert(newTitle);
                    var newKey = ArcadeNaming.CollapseKey(newTitle);
                    // Rewrite the whole (System, CollapseKey) group so version siblings stay collapsed and the
                    // CollapseKey = Normalize(Title) invariant holds group-wide.
                    if (groups.TryGetValue((sys, anchor.CollapseKey), out var group))
                        foreach (var g in group) { g.Title = newTitle; g.SortTitle = newSort; g.CollapseKey = newKey; }
                }
            }

            if (Apply) await db.SaveChangesAsync();

            if (!string.IsNullOrEmpty(Out)) { await File.WriteAllLinesAsync(Out, report); w.WriteLine($"Wrote {Out} ({report.Count - 1} rows)."); }

            w.WriteLine();
            w.WriteLine($"this run: {clean} clean-exact + {fuzzy} fuzzy = {clean + fuzzy} rename(s); "
                      + $"{alreadyOk} already-match (name fine), {noMatch} no-match (left as-is)"
                      + (tooLong > 0 ? $", {tooLong} skipped (name >200)" : "")
                      + (merges > 0 ? $"; {merges} would merge into an existing card" : "") + ".");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextAfterId: {lastId} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after-id {lastId} --apply.");
            else w.WriteLine("Done. Now re-run: arcade-launchbox --apply, arcade-boxart --apply, arcade-igdb, arcade-rating-weights --apply.");
        }
    }
}
