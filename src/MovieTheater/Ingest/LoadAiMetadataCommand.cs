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
            var liveMovieIds = (await db.Movies.Where(m => movieIds.Contains(m.id)).Select(m => m.id).ToListAsync()).ToHashSet();
            var liveSeriesIds = (await db.Series.Where(s => seriesIds.Contains(s.Id)).Select(s => s.Id).ToListAsync()).ToHashSet();

            var subjectKeys = batch.Select(b => new { b.Kind, b.SubjectId }).ToList();
            var existing = await db.TitleInsights
                .Where(ti => ti.ModelId == ModelId)
                .Where(ti => (ti.SubjectKind == InsightSubjectKind.Movie && movieIds.Contains(ti.SubjectId))
                          || (ti.SubjectKind == InsightSubjectKind.Series && seriesIds.Contains(ti.SubjectId)))
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
                if (d.Kind is not (InsightSubjectKind.Movie or InsightSubjectKind.Series))
                { w.WriteLine($"  ! invalid subjectKind '{d.SubjectKind}' (id {d.SubjectId}) — Movie|Series only"); invalid++; continue; }
                bool exists = d.Kind == InsightSubjectKind.Movie ? liveMovieIds.Contains(d.SubjectId) : liveSeriesIds.Contains(d.SubjectId);
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
            w.WriteLine($"work queue remaining (no {ModelId} insight yet): {remaining.movies} movies + {remaining.series} series.");

            if (!Apply) { w.WriteLine("\nDRY RUN — nothing written. Re-run with --apply."); return; }
            if (planned == 0) { w.WriteLine("Nothing to write."); return; }

            if (toRemove.Count > 0) db.TitleInsights.RemoveRange(toRemove); // tags cascade
            db.TitleInsights.AddRange(toAdd);
            await db.SaveChangesAsync();
            w.WriteLine($"\nDONE. wrote {planned} insight(s) ({replaced} replaced).");
        }

        private async Task<(int movies, int series)> RemainingQueueAsync(MovieDb db)
        {
            var movies = await db.Movies.CountAsync(m => m.ReviewBatch == null && !db.TitleInsights.Any(
                ti => ti.ModelId == ModelId && ti.SubjectKind == InsightSubjectKind.Movie && ti.SubjectId == m.id));
            var series = await db.Series.CountAsync(s => s.ReviewBatch == null && !db.TitleInsights.Any(
                ti => ti.ModelId == ModelId && ti.SubjectKind == InsightSubjectKind.Series && ti.SubjectId == s.Id));
            return (movies, series);
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
