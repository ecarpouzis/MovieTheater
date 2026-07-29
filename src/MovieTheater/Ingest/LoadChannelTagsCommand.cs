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
using MovieTheater.Channels;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Loads curated channel-membership judgments — "which stations would program this title" —
    /// into <see cref="TagCategory.Channel"/> tags on each subject's current insight. This is the
    /// sink for the judged curation sweep (the model judges in-session and emits JSON batches);
    /// like <c>load-ai-metadata --franchises-only</c> it surgically replaces ONE tag category and
    /// touches nothing else, but with two deliberate differences:
    ///
    /// <para>1. "Newest insight" here uses the CHANNEL ENGINE's ordering (SpecVersion →
    /// GeneratedUtc → Id, see <see cref="ChannelScheduleService.CurrentInsights"/>) so the tags
    /// land on the row channel filters actually read — not the GeneratedUtc-only pick the
    /// franchise mode uses.</para>
    ///
    /// <para>2. Channel keys are validated strictly against <see cref="ChannelCatalog"/> — an
    /// unknown key is a typo or an unapproved rubric, warned and skipped, never written.</para>
    ///
    /// <para>Subjects with no insight row are reported and skipped — never given a stub insight,
    /// because a stub with a fresh GeneratedUtc would win the modal's GeneratedUtc-only ordering
    /// and blank out the title's prose. Idempotent: re-running an applied batch reports 0 to
    /// change. Tag writes don't change FilterJson, so the catalog apply's future-schedule prune
    /// never fires for them — <c>--prune-schedules</c> drops the affected channels' future lineup
    /// so the maintainer regenerates under the new membership within minutes.</para>
    /// </summary>
    [Command("load-channel-tags", Description = "Load curated channel-membership judgments (JSON) into Channel tags on each subject's current insight.")]
    public class LoadChannelTagsCommand : BasicDICommand, ICommand
    {
        [CommandOption("file", 'f', Description = "Path to the JSON batch file to load.", IsRequired = true)]
        public string File { get; set; } = default!;

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max batch entries to process this run.")]
        public int? Limit { get; set; }

        [CommandOption("mode", Description = "replace (default: the batch IS the title's full membership) | add (union with what's there) | remove (drop just the listed keys).")]
        public string Mode { get; set; } = "replace";

        [CommandOption("prune-schedules", Description = "After an --apply, drop future ChannelScheduleItems for every channel whose membership changed, so lineups regenerate promptly.")]
        public bool PruneSchedules { get; set; }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public LoadChannelTagsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            if (!System.IO.File.Exists(File)) { w.WriteLine($"File not found: {File}"); return; }
            List<EntryDto>? batch;
            try
            {
                batch = JsonSerializer.Deserialize<List<EntryDto>>(await System.IO.File.ReadAllTextAsync(File), JsonOpts);
            }
            catch (JsonException ex) { w.WriteLine($"Bad JSON: {ex.Message}"); return; }
            if (batch is null || batch.Count == 0) { w.WriteLine("Batch is empty."); return; }
            if (Limit.HasValue) batch = batch.Take(Limit.Value).ToList();

            bool add = string.Equals(Mode, "add", StringComparison.OrdinalIgnoreCase);
            bool remove = string.Equals(Mode, "remove", StringComparison.OrdinalIgnoreCase);
            if (!add && !remove && !string.Equals(Mode, "replace", StringComparison.OrdinalIgnoreCase))
            { w.WriteLine($"Unknown --mode '{Mode}' — use replace | add | remove."); return; }
            w.WriteLine($"mode: {Mode.ToLowerInvariant()}");

            // Fold repeats of the same subject into ONE entry before touching the change tracker. A batch
            // that names a title twice (easy to author — the Romance and Crime merges both listed every
            // film that carries both genres) used to be processed twice: the second pass tried to delete
            // the tag rows the first pass had just added, which still hold temporary keys, and EF threw
            // "The property 'TitleTag.Id' has a temporary value while attempting to change the entity's
            // state to 'Deleted'" partway through the write.
            int dupes = batch.Count;
            batch = batch
                .GroupBy(b => (b.Kind, b.SubjectId))
                .Select(g => new EntryDto
                {
                    SubjectKind = g.First().SubjectKind,
                    SubjectId = g.Key.SubjectId,
                    Channels = g.SelectMany(e => e.Channels ?? new List<ChannelDto>()).ToList(),
                })
                .ToList();
            dupes -= batch.Count;
            if (dupes > 0) w.WriteLine($"folded {dupes} repeated subject entr(ies) into their first occurrence");

            // Catalog keys plus the reserved holiday-lock markers, which ride the same Channel tag category
            // but name a holiday rather than a station (see ChannelCatalog.HolidayLockKeys).
            var validKeys = new HashSet<string>(
                ChannelCatalog.All.Select(d => d.Key).Concat(ChannelCatalog.HolidayLockKeys),
                StringComparer.OrdinalIgnoreCase);

            await using var db = await dbFactory.CreateDbContextAsync();

            var movieIds = batch.Where(b => b.Kind == InsightSubjectKind.Movie).Select(b => b.SubjectId).Distinct().ToList();
            var seriesIds = batch.Where(b => b.Kind == InsightSubjectKind.Series).Select(b => b.SubjectId).Distinct().ToList();
            var miscIds = batch.Where(b => b.Kind == InsightSubjectKind.MiscVideo).Select(b => b.SubjectId).Distinct().ToList();
            var liveMovieIds = (await db.Movies.Where(m => movieIds.Contains(m.id)).Select(m => m.id).ToListAsync()).ToHashSet();
            var liveSeriesIds = (await db.Series.Where(s => seriesIds.Contains(s.Id)).Select(s => s.Id).ToListAsync()).ToHashSet();
            var liveMiscIds = (await db.MiscVideos.Where(mv => miscIds.Contains(mv.Id)).Select(mv => mv.Id).ToListAsync()).ToHashSet();

            var insights = await db.TitleInsights
                .Where(ti => (ti.SubjectKind == InsightSubjectKind.Movie && movieIds.Contains(ti.SubjectId))
                          || (ti.SubjectKind == InsightSubjectKind.Series && seriesIds.Contains(ti.SubjectId))
                          || (ti.SubjectKind == InsightSubjectKind.MiscVideo && miscIds.Contains(ti.SubjectId)))
                .Include(ti => ti.Tags)
                .ToListAsync();
            // The channel engine's "current insight" ordering — NOT the franchise mode's
            // GeneratedUtc-only pick (see class doc).
            var current = insights
                .GroupBy(ti => (ti.SubjectKind, ti.SubjectId))
                .ToDictionary(g => g.Key, g => g
                    .OrderByDescending(ti => ti.SpecVersion)
                    .ThenByDescending(ti => ti.GeneratedUtc)
                    .ThenByDescending(ti => ti.Id)
                    .First());

            int changed = 0, unchanged = 0, noInsight = 0, invalid = 0, unknownKeys = 0;
            var touchedChannelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in batch)
            {
                if (d.Kind is not (InsightSubjectKind.Movie or InsightSubjectKind.Series or InsightSubjectKind.MiscVideo))
                { w.WriteLine($"  ! invalid subjectKind '{d.SubjectKind}' (id {d.SubjectId}) — Movie|Series|MiscVideo only"); invalid++; continue; }
                bool exists = d.Kind switch
                {
                    InsightSubjectKind.Movie => liveMovieIds.Contains(d.SubjectId),
                    InsightSubjectKind.Series => liveSeriesIds.Contains(d.SubjectId),
                    _ => liveMiscIds.Contains(d.SubjectId),
                };
                if (!exists) { w.WriteLine($"  ! no such {d.Kind} id {d.SubjectId} — skipping"); invalid++; continue; }

                // Desired membership set. An explicit empty list means "member of nothing" — a
                // deliberate, diffable removal, not a skip.
                var desired = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in d.Channels ?? new List<ChannelDto>())
                {
                    var key = (c.Key ?? "").Trim();
                    if (key.Length == 0) continue;
                    if (!validKeys.Contains(key))
                    { w.WriteLine($"  ! {d.Kind} {d.SubjectId}: unknown channel key '{key}' — skipping"); unknownKeys++; continue; }
                    var weight = c.Weight is null ? (int?)null : Math.Clamp(c.Weight.Value, 0, 100);
                    if (!desired.TryGetValue(key, out var prior) || (weight ?? -1) > (prior ?? -1)) desired[key] = weight;
                }

                if (!current.TryGetValue((d.Kind, d.SubjectId), out var insight))
                { w.WriteLine($"  ! {d.Kind} {d.SubjectId}: no insight to attach channel tags to — skipping (run load-ai-metadata for it first)"); noInsight++; continue; }

                var have = insight.Tags.Where(t => t.Category == TagCategory.Channel && t.Value != null)
                    .GroupBy(t => t.Value!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Weight ?? -1).First().Weight, StringComparer.OrdinalIgnoreCase);

                // add/remove fold the batch into the existing set, so a sweep that only cares about one
                // station doesn't have to restate (and risk dropping) every other membership a title holds.
                if (add)
                {
                    var merged = new Dictionary<string, int?>(have, StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in desired) merged[kv.Key] = kv.Value;
                    desired = merged;
                }
                else if (remove)
                {
                    var kept = new Dictionary<string, int?>(have, StringComparer.OrdinalIgnoreCase);
                    foreach (var k in desired.Keys) kept.Remove(k);
                    desired = kept;
                }

                bool same = have.Count == desired.Count
                    && have.All(kv => desired.TryGetValue(kv.Key, out var dw) && dw == kv.Value);
                if (same) { unchanged++; continue; }

                w.WriteLine($"  ~ {d.Kind} {d.SubjectId}: [{string.Join(", ", have.Select(Fmt))}] -> [{string.Join(", ", desired.Select(Fmt))}]");
                changed++;
                foreach (var k in have.Keys) touchedChannelKeys.Add(k);
                foreach (var k in desired.Keys) touchedChannelKeys.Add(k);

                if (Apply)
                {
                    var obsolete = insight.Tags.Where(t => t.Category == TagCategory.Channel).ToList();
                    foreach (var t in obsolete) insight.Tags.Remove(t);
                    db.RemoveRange(obsolete);
                    foreach (var kv in desired)
                        insight.Tags.Add(new TitleTag { Category = TagCategory.Channel, Value = kv.Key, Weight = kv.Value });
                }
            }

            // Like franchises-only: "changed" is the work left on a dry run, the write count on an
            // --apply, and 0 on a re-run — the resumability signal for the chunked sweep.
            w.WriteLine($"\nchannel-tags {Path.GetFileName(File)}: {changed} to change, {unchanged} already-correct, " +
                        $"{noInsight} no-insight, {invalid} invalid, {unknownKeys} unknown key(s).");
            if (!Apply) { w.WriteLine("\nDRY RUN — nothing written. Re-run with --apply."); return; }
            if (changed == 0) { w.WriteLine("Nothing to write."); return; }
            await db.SaveChangesAsync();
            w.WriteLine($"\nDONE. updated channel tags on {changed} insight(s).");

            if (PruneSchedules && touchedChannelKeys.Count > 0)
            {
                // Tag writes leave FilterJson untouched, so channel-catalog --apply's own prune
                // won't fire; drop the affected channels' future lineup so the maintainer rebuilds
                // it under the new membership instead of airing pre-curation picks for 48h.
                var keys = touchedChannelKeys.ToList();
                var channelIds = await db.Channels
                    .Where(c => c.CatalogKey != null && keys.Contains(c.CatalogKey))
                    .Select(c => c.Id).ToListAsync();
                if (channelIds.Count > 0)
                {
                    var now = DateTime.UtcNow;
                    int pruned = await db.ChannelScheduleItems
                        .Where(i => channelIds.Contains(i.ChannelId) && i.StartUtc > now)
                        .ExecuteDeleteAsync();
                    w.WriteLine($"pruned {pruned} future schedule item(s) across {channelIds.Count} channel(s); the maintainer regenerates within minutes.");
                }
            }
        }

        private static string Fmt(KeyValuePair<string, int?> kv) => kv.Value is null ? kv.Key : $"{kv.Key}:{kv.Value}";

        // ── JSON shape ──
        private sealed class EntryDto
        {
            public string? SubjectKind { get; set; }
            public int SubjectId { get; set; }
            public List<ChannelDto>? Channels { get; set; }

            public InsightSubjectKind Kind =>
                Enum.TryParse<InsightSubjectKind>(SubjectKind, ignoreCase: true, out var k) ? k : (InsightSubjectKind)(-1);
        }

        private sealed class ChannelDto
        {
            public string? Key { get; set; }
            public int? Weight { get; set; }
        }
    }
}
