using System.Text.Json;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Migration.Units
{
    /// <summary>
    /// Insight ids are deterministic so every unit (and a re-run) agrees without an id map: a series insight keeps
    /// its v1 ClaudeSeriesMetadata.Id; a book insight is <c>BookBase + Comics.Id</c> (v1 keyed them by ComicId).
    /// </summary>
    public static class InsightIds
    {
        public const int BookBase = 10_000_000;
        public static int ForBook(long itemId) => checked((int)(BookBase + itemId));
    }

    /// <summary>ClaudeSeriesMetadata → Insight(SubjectKind=Series); name-keyed rows that resolve to no series are exported, not carried.</summary>
    public sealed class SeriesInsightsUnit : StageUnit
    {
        public override string Stage => "insights";
        public override string? SourceTable => "ClaudeSeriesMetadata";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var name = r.S("SeriesName");
            var seriesId = ctx.SeriesForInsight(r.L("Id")!.Value, name);
            if (seriesId == null) { c.Unmapped++; c.Bump("orphan"); return; }
            hot.Upsert("Insight", new
            {
                Id = r.Int("Id"), SubjectKind = SubjectKind.Series, SubjectId = (int)seriesId.Value, ModelId = r.S("ModelId") ?? "unknown", Rank = Transforms.ModelRank(r.S("ModelId")),
                Confidence = Transforms.ConfidenceOf(r.S("Confidence")), Recognized = r.B("KnownSeries"), Rating = r.I("Rating"), Synopsis = r.S("Synopsis"), Author = r.T("Author"), Artist = r.T("Artist"),
                YearBegin = r.I("YearBegin"), YearEnd = r.I("YearEnd"), Maturity = (int?)null, ReviewFlag = r.T("ReviewFlag"), SourceKey = name, GeneratedAt = r.At("GeneratedAt"), IsCurrent = false,
            });
            foreach (var (otherSeries, cloneId) in ctx.ClonesForInsight(r.L("Id")!.Value))
            {
                if (!ctx.SeriesExists(otherSeries)) continue;
                hot.Upsert("Insight", new
                {
                    Id = cloneId, SubjectKind = SubjectKind.Series, SubjectId = (int)otherSeries, ModelId = r.S("ModelId") ?? "unknown", Rank = Transforms.ModelRank(r.S("ModelId")),
                    Confidence = Transforms.ConfidenceOf(r.S("Confidence")), Recognized = r.B("KnownSeries"), Rating = r.I("Rating"), Synopsis = r.S("Synopsis"), Author = r.T("Author"), Artist = r.T("Artist"),
                    YearBegin = r.I("YearBegin"), YearEnd = r.I("YearEnd"), Maturity = (int?)null, ReviewFlag = r.T("ReviewFlag"), SourceKey = name + "|clone-of:" + r.L("Id"), GeneratedAt = r.At("GeneratedAt"), IsCurrent = false,
                });
                c.Bump("clones");
            }
            c.Inserted++;
        }

        /// <summary>The orphan rows, exported whole (they carry real synopses; a later pass may map them by hand).</summary>
        public override void Finalize(MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var orphans = ctx.Source.Rows("SELECT * FROM ClaudeSeriesMetadata ORDER BY Id").Where(r => ctx.SeriesForInsight(r.L("Id")!.Value, r.S("SeriesName")) == null).ToList();
            var path = ctx.ReportPath("orphan-insights.json");
            if (!ctx.Options.DryRun)
            {
                using var fs = File.Create(path);
                using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
                w.WriteStartArray();
                foreach (var r in orphans)
                {
                    w.WriteStartObject();
                    foreach (var col in new[] { "Id", "SeriesName", "Rating", "Synopsis", "Confidence", "KnownSeries", "GeneratedAt", "ModelId", "Author", "Artist", "YearBegin", "YearEnd", "ReviewFlag", "TagsCsv" })
                    {
                        var v = r.Raw(col);
                        switch (v)
                        {
                            case null: w.WriteNull(col); break;
                            case long l: w.WriteNumber(col, l); break;
                            case double d: w.WriteNumber(col, d); break;
                            default: w.WriteString(col, Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); break;
                        }
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            c.Bump("orphans-exported", orphans.Count);
            ctx.Log($"orphan series insights: {orphans.Count} → {path}");
        }
    }

    public sealed class SeriesInsightTagsUnit : StageUnit
    {
        public override string Stage => "insights";
        public override string? SourceTable => "ClaudeSeriesTags";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var mid = r.L("MetadataId");
            if (mid == null || !ctx.SeriesInsightCarried(mid.Value)) { c.Unmapped++; c.Bump("insight-not-carried"); return; }
            var tag = r.T("Tag"); var cat = r.T("Category");
            if (tag == null || cat == null) { c.Skipped++; return; }
            hot.Upsert("InsightTag", new { InsightId = (int)mid.Value, Category = cat, Value = tag });
            foreach (var (_, cloneId) in ctx.ClonesForInsight(mid.Value))
                hot.Upsert("InsightTag", new { InsightId = cloneId, Category = cat, Value = tag });
            c.Inserted++;
        }
    }

    /// <summary>ClaudeBookMetadata → Insight(SubjectKind=Item); one row per book in v1, so it is current already.</summary>
    public sealed class BookInsightsUnit : StageUnit
    {
        public override string Stage => "insights";
        public override string? SourceTable => "ClaudeBookMetadata";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.L("ComicId");
            if (!ctx.ItemExists(id)) { c.Unmapped++; c.Bump("item-missing"); return; }
            hot.Upsert("Insight", new
            {
                Id = InsightIds.ForBook(id!.Value), SubjectKind = SubjectKind.Item, SubjectId = (int)id.Value, ModelId = r.S("ModelId") ?? "unknown", Rank = Transforms.ModelRank(r.S("ModelId")),
                Confidence = Transforms.ConfidenceOf(r.S("Confidence")), Recognized = r.B("KnownBook"), Rating = r.I("Rating"), Synopsis = r.S("Synopsis"), Author = r.T("Author"), Artist = (string?)null,
                YearBegin = r.I("YearPublished"), YearEnd = (int?)null, Maturity = r.I("Maturity"), ReviewFlag = (string?)null, SourceKey = (string?)null, GeneratedAt = r.At("GeneratedAt"), IsCurrent = true,
            });
            c.Inserted++;
        }
    }

    public sealed class BookInsightTagsUnit : StageUnit
    {
        public override string Stage => "insights";
        public override string? SourceTable => "ClaudeBookTags";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.L("ComicId");
            if (id == null || !ctx.ItemExists(id) || !ctx.BookInsightExists(id.Value)) { c.Unmapped++; c.Bump("insight-missing"); return; }
            var tag = r.T("Tag"); var cat = r.T("Category");
            if (tag == null || cat == null) { c.Skipped++; return; }
            hot.Upsert("InsightTag", new { InsightId = InsightIds.ForBook(id.Value), Category = cat, Value = tag });
            c.Inserted++;
        }
    }

    /// <summary>LibraryComicRatings / LibrarySeriesRatings → Rating(Source=Library); LibraryRatingOverrides → Rating(Source=Override, IsOverride).</summary>
    public sealed class RatingsUnit : StageUnit
    {
        private readonly string table;
        public RatingsUnit(string table) { this.table = table; }
        public override string Stage => "ratings";
        public override string? SourceTable => table;
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            switch (table)
            {
                case "LibraryComicRatings":
                    if (!ctx.ItemExists(r.L("ComicId"))) { c.Unmapped++; c.Bump("item-missing"); return; }
                    hot.Upsert("Rating", new
                    {
                        TargetKind = SubjectKind.Item, TargetId = r.Int("ComicId"), Source = RatingSource.Library, Value = r.I("Rating"), RawValue = (double?)r.I("Rating"), RawScale = "0-100", Count = (int?)null,
                        Note = Note(r), IsOverride = false, ModelId = r.S("ModelId") ?? "library", GeneratedAt = r.At("GeneratedAt"),
                    });
                    break;
                case "LibrarySeriesRatings":
                    if (!ctx.SeriesExists(r.L("SeriesId"))) { c.Unmapped++; c.Bump("series-missing"); return; }
                    hot.Upsert("Rating", new
                    {
                        TargetKind = SubjectKind.Series, TargetId = r.Int("SeriesId"), Source = RatingSource.Library, Value = r.I("Rating"), RawValue = (double?)r.I("Rating"), RawScale = "0-100", Count = (int?)null,
                        Note = Note(r), IsOverride = false, ModelId = r.S("ModelId") ?? "library", GeneratedAt = r.At("GeneratedAt"),
                    });
                    break;
                case "LibraryRatingOverrides":
                {
                    var kind = Transforms.SubjectOf(r.S("TargetType"));
                    var ok = kind == SubjectKind.Series ? ctx.SeriesExists(r.L("TargetId")) : ctx.ItemExists(r.L("TargetId"));
                    if (!ok) { c.Unmapped++; c.Bump("target-missing"); return; }
                    hot.Upsert("Rating", new
                    {
                        TargetKind = kind, TargetId = r.Int("TargetId"), Source = RatingSource.Override, Value = r.I("Rating"), RawValue = (double?)r.I("Rating"), RawScale = "0-100", Count = (int?)null,
                        Note = r.T("Note"), IsOverride = true, ModelId = "override", GeneratedAt = r.At("CreatedAt"),
                    });
                    break;
                }
            }
            c.Inserted++;
        }
        private static string? Note(V1Row r)
        {
            var note = r.T("Note"); var sources = r.T("Sources");
            if (sources == null) return note;
            return note == null ? "sources: " + sources : note + " [sources: " + sources + "]";
        }
    }

    public sealed class KidSafeTagsUnit : StageUnit
    {
        public override string Stage => "tags";
        public override string? SourceTable => "KidSafeTags";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("KidSafeTag", new { Category = r.S("Category") ?? "", Tag = r.S("Tag") ?? "", AppliesTo = r.T("AppliesTo"), UpdatedAt = r.At("UpdatedAt") });
            c.Inserted++;
        }
    }

    public sealed class TagAliasesUnit : StageUnit
    {
        public override string Stage => "tags";
        public override string? SourceTable => "TagAliases";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("TagAlias", new { Category = r.S("Category") ?? "", AliasTag = r.S("AliasTag") ?? "", CanonicalTag = r.S("CanonicalTag"), Source = r.T("Source") });
            c.Inserted++;
        }
    }

    public sealed class CvdbResolutionsUnit : StageUnit
    {
        public override string Stage => "tags";
        public override string? SourceTable => "CvdbResolutions";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("CvdbResolution", new { CvdbTag = r.S("CvdbTag") ?? "", ComicvineId = r.I("ComicvineId"), ResolvedName = r.T("ResolvedName"), EntityType = r.T("EntityType"), Status = r.T("Status"), ResolvedAt = r.At("ResolvedAt") });
            c.Inserted++;
        }
    }

    public sealed class InferenceDecisionsUnit : StageUnit
    {
        public override string Stage => "reconciliation";
        public override string? SourceTable => "SeriesInferenceDecisions";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("SeriesInferenceDecision", new
            {
                Id = r.Int("Id"), SeriesKey = r.S("SeriesKey"), Class = r.S("Class"), Action = r.S("Action"), Target = r.S("Target"), Confidence = r.S("Confidence"), EvidenceJson = r.S("EvidenceJson"),
                State = r.S("State"), UndoJson = r.S("UndoJson"), DecidedBy = r.S("DecidedBy"), DecidedAt = r.At("DecidedAt"),
            });
            c.Inserted++;
        }
    }

    public sealed class MatchReviewsUnit : StageUnit
    {
        public override string Stage => "reconciliation";
        public override string? SourceTable => "SeriesMatchReviews";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("SeriesMatchReview", new { Id = r.Int("Id"), Scope = r.S("Scope"), Key = r.S("Key"), State = r.S("State"), Note = r.S("Note"), DecidedBy = r.S("DecidedBy"), DecidedAt = r.At("DecidedAt") });
            c.Inserted++;
        }
    }

    public sealed class DuplicateGroupsUnit : StageUnit
    {
        public override string Stage => "dedup-groups";
        public override string? SourceTable => "DuplicateGroups";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("DuplicateGroup", new
            {
                Id = r.Int("Id"), Relationship = r.I("Relationship"), Confidence = r.S("Confidence"), Evidence = r.S("Evidence"),
                SuggestedKeeperItemId = ctx.ItemExists(r.L("SuggestedKeeperComicId")) ? r.I("SuggestedKeeperComicId") : null, ReviewState = r.S("ReviewState"), DetectedAt = r.At("DetectedAt"),
            });
            c.Inserted++;
        }
    }

    public sealed class DuplicateMembersUnit : StageUnit
    {
        public override string Stage => "dedup-members";
        public override string? SourceTable => "DuplicateMembers";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            if (!ctx.DuplicateGroupExists(r.L("DuplicateGroupId")) || !ctx.ItemExists(r.L("ComicId"))) { c.Unmapped++; c.Bump("parent-missing"); return; }
            hot.Upsert("DuplicateMember", new { Id = r.Int("Id"), DuplicateGroupId = r.Int("DuplicateGroupId"), ItemId = r.Int("ComicId"), Role = r.S("Role"), SoleFileInFolder = r.B("SoleFileInFolder") });
            c.Inserted++;
        }
    }
}
