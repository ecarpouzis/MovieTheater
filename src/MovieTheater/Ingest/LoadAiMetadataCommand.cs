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

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Persists a batch of model-inferred title insights (a JSON file the language model produced
    /// in-session) into the <see cref="TitleInsight"/> / <see cref="TitleTag"/> tables. The model is
    /// the generator; this command is just the idempotent, validated, resumable <em>sink</em> — it
    /// calls no external API. Provenance (<see cref="TitleInsight.ModelId"/> / GeneratedUtc /
    /// SpecVersion) is stamped here so the JSON doesn't have to carry it.
    ///
    /// <para>Dry-run by default (like <see cref="EnrichTitlesCommand"/>): prints the planned inserts
    /// and the remaining work-queue size; re-run with <c>--apply</c> to write. Re-loading a subject the
    /// <em>same</em> model already covered is a no-op unless <c>--force</c> (which replaces that row);
    /// a newer <c>--model</c> always inserts a fresh row, so older models' judgements are preserved.</para>
    /// </summary>
    [Command("load-ai-metadata", Description = "Load a JSON batch of model-inferred title insights into TitleInsight/TitleTag.")]
    public class LoadAiMetadataCommand : BasicDICommand, ICommand
    {
        /// <summary>The model these insights came from; stamped on every row as provenance.</summary>
        public const string DefaultModelId = "claude-opus-4-8";

        /// <summary>Field-set / prompt spec version; bump when the shape of an insight changes.</summary>
        public const int CurrentSpecVersion = 1;

        [CommandOption("file", 'f', Description = "Path to the JSON batch file to load.", IsRequired = true)]
        public string File { get; set; } = default!;

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("model", Description = "Model id stamped as provenance (default: " + DefaultModelId + ").")]
        public string ModelId { get; set; } = DefaultModelId;

        [CommandOption("spec", Description = "Spec version stamped on each row (default: current).")]
        public int SpecVersion { get; set; } = CurrentSpecVersion;

        [CommandOption("force", Description = "Replace an existing same-model insight for a subject instead of skipping it.")]
        public bool Force { get; set; }

        [CommandOption("limit", Description = "Max insights to load this run.")]
        public int? Limit { get; set; }

        [CommandOption("franchises-only", Description = "Surgically replace ONLY the Franchise tags on each subject's newest insight (any model), leaving every other facet untouched. For franchise re-curation batches — see docs/franchise-tagging-spec.md.")]
        public bool FranchisesOnly { get; set; }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public LoadAiMetadataCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            if (!System.IO.File.Exists(File)) { w.WriteLine($"File not found: {File}"); return; }
            List<InsightDto>? batch;
            try
            {
                batch = JsonSerializer.Deserialize<List<InsightDto>>(await System.IO.File.ReadAllTextAsync(File), JsonOpts);
            }
            catch (JsonException ex) { w.WriteLine($"Bad JSON: {ex.Message}"); return; }
            if (batch is null || batch.Count == 0) { w.WriteLine("Batch is empty."); return; }
            if (Limit.HasValue) batch = batch.Take(Limit.Value).ToList();

            await using var db = await dbFactory.CreateDbContextAsync();

            // Resolve subject existence + existing same-model rows for just the ids in this batch.
            var movieIds = batch.Where(b => b.Kind == InsightSubjectKind.Movie).Select(b => b.SubjectId).Distinct().ToList();
            var seriesIds = batch.Where(b => b.Kind == InsightSubjectKind.Series).Select(b => b.SubjectId).Distinct().ToList();
            var miscIds = batch.Where(b => b.Kind == InsightSubjectKind.MiscVideo).Select(b => b.SubjectId).Distinct().ToList();
            var liveMovieIds = (await db.Movies.Where(m => movieIds.Contains(m.id)).Select(m => m.id).ToListAsync()).ToHashSet();
            var liveSeriesIds = (await db.Series.Where(s => seriesIds.Contains(s.Id)).Select(s => s.Id).ToListAsync()).ToHashSet();
            var liveMiscIds = (await db.MiscVideos.Where(mv => miscIds.Contains(mv.Id)).Select(mv => mv.Id).ToListAsync()).ToHashSet();

            // Franchise re-curation: don't insert a fresh whole insight (which would churn the good
            // narrative/slider facets) — surgically swap just the Franchise tags on the newest insight.
            if (FranchisesOnly)
            {
                await ApplyFranchisesOnlyAsync(db, batch, movieIds, seriesIds, miscIds, liveMovieIds, liveSeriesIds, liveMiscIds, w);
                return;
            }

            var subjectKeys = batch.Select(b => new { b.Kind, b.SubjectId }).ToList();
            var existing = await db.TitleInsights
                .Where(ti => ti.ModelId == ModelId)
                .Where(ti => (ti.SubjectKind == InsightSubjectKind.Movie && movieIds.Contains(ti.SubjectId))
                          || (ti.SubjectKind == InsightSubjectKind.Series && seriesIds.Contains(ti.SubjectId))
                          || (ti.SubjectKind == InsightSubjectKind.MiscVideo && miscIds.Contains(ti.SubjectId)))
                .Include(ti => ti.Tags)
                .ToListAsync();
            TitleInsight? FindExisting(InsightDto d) =>
                existing.FirstOrDefault(e => e.SubjectKind == d.Kind && e.SubjectId == d.SubjectId);

            int planned = 0, skipped = 0, replaced = 0, invalid = 0, novelTags = 0;
            var toAdd = new List<TitleInsight>();
            var toRemove = new List<TitleInsight>();

            foreach (var d in batch)
            {
                // ── Validate ──
                if (d.Kind is not (InsightSubjectKind.Movie or InsightSubjectKind.Series or InsightSubjectKind.MiscVideo))
                { w.WriteLine($"  ! invalid subjectKind '{d.SubjectKind}' (id {d.SubjectId}) — Movie|Series|MiscVideo only"); invalid++; continue; }
                bool exists = d.Kind switch
                {
                    InsightSubjectKind.Movie => liveMovieIds.Contains(d.SubjectId),
                    InsightSubjectKind.Series => liveSeriesIds.Contains(d.SubjectId),
                    _ => liveMiscIds.Contains(d.SubjectId),
                };
                if (!exists)
                { w.WriteLine($"  ! no such {d.Kind} id {d.SubjectId} — skipping"); invalid++; continue; }

                var prior = FindExisting(d);
                if (prior != null && !Force) { skipped++; continue; }

                var insight = new TitleInsight
                {
                    SubjectKind = d.Kind,
                    SubjectId = d.SubjectId,
                    ModelId = ModelId,
                    SpecVersion = SpecVersion,
                    GeneratedUtc = DateTime.UtcNow,
                    Recognized = d.Recognized,
                    Confidence = d.Recognized ? d.ParsedConfidence : InsightConfidence.Low,
                    Vibe = Trim(d.Vibe),
                    WhyInteresting = Trim(d.WhyInteresting),
                    WatchIfYouLiked = Trim(d.WatchIfYouLiked),
                    PeopleNote = Trim(d.PeopleNote),
                    Surrealism = Clamp(d.Surrealism),
                    CultClassic = Clamp(d.CultClassic),
                    Intensity = Clamp(d.Intensity),
                    Novelty = Clamp(d.Novelty),
                    Rewatchability = Clamp(d.Rewatchability),
                    Energy = Clamp(d.Energy),
                };

                foreach (var t in d.Tags ?? new List<TagDto>())
                {
                    if (!Enum.TryParse<TagCategory>(t.Category, ignoreCase: true, out var cat))
                    { w.WriteLine($"  ! {d.Kind} {d.SubjectId}: unknown tag category '{t.Category}' — skipping tag"); continue; }
                    var value = AiMetadataVocab.Normalize(t.Value ?? "");
                    if (value.Length == 0) continue;
                    if (!AiMetadataVocab.IsKnown(cat, value))
                    { w.WriteLine($"  · novel tag {cat}='{value}' ({d.Kind} {d.SubjectId})"); novelTags++; }
                    insight.Tags.Add(new TitleTag { Category = cat, Value = value, Weight = Clamp(t.Weight) });
                }

                if (prior != null) { toRemove.Add(prior); replaced++; }
                toAdd.Add(insight);
                planned++;
            }

            w.WriteLine($"\nbatch {Path.GetFileName(File)} ({ModelId}, spec {SpecVersion}): " +
                        $"{planned} to write ({replaced} replace, {planned - replaced} new), {skipped} already-loaded, {invalid} invalid; {novelTags} novel tags.");

            var remaining = await RemainingQueueAsync(db);
            w.WriteLine($"work queue remaining (no {ModelId} insight yet): {remaining.movies} movies + {remaining.series} series + {remaining.misc} misc.");

            if (!Apply) { w.WriteLine("\nDRY RUN — nothing written. Re-run with --apply."); return; }
            if (planned == 0) { w.WriteLine("Nothing to write."); return; }

            if (toRemove.Count > 0) db.TitleInsights.RemoveRange(toRemove); // tags cascade
            db.TitleInsights.AddRange(toAdd);
            await db.SaveChangesAsync();
            w.WriteLine($"\nDONE. wrote {planned} insight(s) ({replaced} replaced).");
        }

        // Replace only the Franchise-category tags on each subject's newest insight (any model), leaving
        // narrative/sliders/other tag categories intact and GeneratedUtc unchanged. Idempotent: a subject
        // whose newest insight already carries exactly the desired franchise set is skipped. Dry-run by
        // default; honors --limit (applied to the batch upstream). See docs/franchise-tagging-spec.md.
        private async Task ApplyFranchisesOnlyAsync(
            MovieDb db, List<InsightDto> batch,
            List<int> movieIds, List<int> seriesIds, List<int> miscIds,
            HashSet<int> liveMovieIds, HashSet<int> liveSeriesIds, HashSet<int> liveMiscIds,
            TextWriter w)
        {
            // Load every insight for the batch's subjects, then index the newest per subject.
            var insights = await db.TitleInsights
                .Where(ti => (ti.SubjectKind == InsightSubjectKind.Movie && movieIds.Contains(ti.SubjectId))
                          || (ti.SubjectKind == InsightSubjectKind.Series && seriesIds.Contains(ti.SubjectId))
                          || (ti.SubjectKind == InsightSubjectKind.MiscVideo && miscIds.Contains(ti.SubjectId)))
                .Include(ti => ti.Tags)
                .ToListAsync();
            var newest = insights
                .GroupBy(ti => (ti.SubjectKind, ti.SubjectId))
                .ToDictionary(g => g.Key, g => g.OrderByDescending(ti => ti.GeneratedUtc).First());

            int changed = 0, unchanged = 0, noInsight = 0, invalid = 0, novelTags = 0;

            foreach (var d in batch)
            {
                if (d.Kind is not (InsightSubjectKind.Movie or InsightSubjectKind.Series or InsightSubjectKind.MiscVideo))
                { w.WriteLine($"  ! invalid subjectKind '{d.SubjectKind}' (id {d.SubjectId})"); invalid++; continue; }
                bool exists = d.Kind switch
                {
                    InsightSubjectKind.Movie => liveMovieIds.Contains(d.SubjectId),
                    InsightSubjectKind.Series => liveSeriesIds.Contains(d.SubjectId),
                    _ => liveMiscIds.Contains(d.SubjectId),
                };
                if (!exists) { w.WriteLine($"  ! no such {d.Kind} id {d.SubjectId} — skipping"); invalid++; continue; }

                // Desired franchise set from the batch entry — Franchise tags only.
                var desired = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in d.Tags ?? new List<TagDto>())
                {
                    if (!Enum.TryParse<TagCategory>(t.Category, ignoreCase: true, out var cat))
                    { w.WriteLine($"  ! {d.Kind} {d.SubjectId}: unknown tag category '{t.Category}' — skipping tag"); continue; }
                    if (cat != TagCategory.Franchise)
                    { w.WriteLine($"  ! {d.Kind} {d.SubjectId}: --franchises-only ignores non-Franchise tag '{t.Category}={t.Value}'"); continue; }
                    var value = AiMetadataVocab.Normalize(t.Value ?? "");
                    if (value.Length == 0) continue;
                    if (!AiMetadataVocab.IsKnown(cat, value))
                    { w.WriteLine($"  · novel franchise '{value}' ({d.Kind} {d.SubjectId})"); novelTags++; }
                    var weight = Clamp(t.Weight);
                    if (!desired.TryGetValue(value, out var prior) || (weight ?? -1) > (prior ?? -1)) desired[value] = weight;
                }

                if (!newest.TryGetValue((d.Kind, d.SubjectId), out var insight))
                { w.WriteLine($"  ! {d.Kind} {d.SubjectId}: no insight to attach franchises to — skipping"); noInsight++; continue; }

                var current = insight.Tags.Where(t => t.Category == TagCategory.Franchise && t.Value != null)
                    .GroupBy(t => t.Value!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Weight ?? -1).First().Weight, StringComparer.OrdinalIgnoreCase);

                bool same = current.Count == desired.Count
                    && current.All(kv => desired.TryGetValue(kv.Key, out var dw) && dw == kv.Value);
                if (same) { unchanged++; continue; }

                w.WriteLine($"  ~ {d.Kind} {d.SubjectId}: [{string.Join(", ", current.Select(FmtTag))}] -> [{string.Join(", ", desired.Select(FmtTag))}]");
                changed++;

                if (Apply)
                {
                    var remove = insight.Tags.Where(t => t.Category == TagCategory.Franchise).ToList();
                    foreach (var t in remove) insight.Tags.Remove(t);
                    db.RemoveRange(remove);
                    foreach (var kv in desired)
                        insight.Tags.Add(new TitleTag { Category = TagCategory.Franchise, Value = kv.Key, Weight = kv.Value });
                }
            }

            // "changed" doubles as the remaining-to-apply count: on a dry run it's the work left; after an
            // --apply it's how many were written, and a re-run reports 0 (idempotent) — the resumability signal.
            w.WriteLine($"\nfranchises-only {Path.GetFileName(File)}: {changed} to change, {unchanged} already-correct, " +
                        $"{noInsight} no-insight, {invalid} invalid; {novelTags} novel franchise(s).");
            if (!Apply) { w.WriteLine("\nDRY RUN — nothing written. Re-run with --apply."); return; }
            if (changed == 0) { w.WriteLine("Nothing to write."); return; }
            await db.SaveChangesAsync();
            w.WriteLine($"\nDONE. updated franchises on {changed} insight(s).");
        }

        private static string FmtTag(KeyValuePair<string, int?> kv) => kv.Value is null ? kv.Key : $"{kv.Key}:{kv.Value}";

        private async Task<(int movies, int series, int misc)> RemainingQueueAsync(MovieDb db)
        {
            var movies = await db.Movies.CountAsync(m => m.ReviewBatch == null && !db.TitleInsights.Any(
                ti => ti.ModelId == ModelId && ti.SubjectKind == InsightSubjectKind.Movie && ti.SubjectId == m.id));
            var series = await db.Series.CountAsync(s => s.ReviewBatch == null && !db.TitleInsights.Any(
                ti => ti.ModelId == ModelId && ti.SubjectKind == InsightSubjectKind.Series && ti.SubjectId == s.Id));
            var misc = await db.MiscVideos.CountAsync(mv => mv.ReviewBatch == null && !db.TitleInsights.Any(
                ti => ti.ModelId == ModelId && ti.SubjectKind == InsightSubjectKind.MiscVideo && ti.SubjectId == mv.Id));
            return (movies, series, misc);
        }

        private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static int? Clamp(int? v) => v is null ? null : Math.Clamp(v.Value, 0, 100);

        // ── JSON shape (provenance stamped by the loader, not carried in the file) ──
        private sealed class InsightDto
        {
            public string? SubjectKind { get; set; }
            public int SubjectId { get; set; }
            public bool Recognized { get; set; } = true;
            public string? Confidence { get; set; }
            public string? Vibe { get; set; }
            public string? WhyInteresting { get; set; }
            public string? WatchIfYouLiked { get; set; }
            public string? PeopleNote { get; set; }
            public int? Surrealism { get; set; }
            public int? CultClassic { get; set; }
            public int? Intensity { get; set; }
            public int? Novelty { get; set; }
            public int? Rewatchability { get; set; }
            public int? Energy { get; set; }
            public List<TagDto>? Tags { get; set; }

            public InsightSubjectKind Kind =>
                Enum.TryParse<InsightSubjectKind>(SubjectKind, ignoreCase: true, out var k) ? k : (InsightSubjectKind)(-1);

            public InsightConfidence ParsedConfidence =>
                Enum.TryParse<InsightConfidence>(Confidence, ignoreCase: true, out var c) ? c : InsightConfidence.Medium;
        }

        private sealed class TagDto
        {
            public string? Category { get; set; }
            public string? Value { get; set; }
            public int? Weight { get; set; }
        }
    }
}
