using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Services
{
    /// <summary>What one normalization pass changed.</summary>
    public sealed record TagNormResult(int AliasesApplied, int EraRangesRemoved, int CrossCategoryRemoved, int ToneMatureMigrated)
    {
        public int Total => AliasesApplied + EraRangesRemoved + CrossCategoryRemoved + ToneMatureMigrated;
        public override string ToString() =>
            $"{{ aliasesApplied: {AliasesApplied}, eraRangesRemoved: {EraRangesRemoved}, crossCategoryRemoved: {CrossCategoryRemoved}, toneMatureMigrated: {ToneMatureMigrated} }}";
    }

    /// <summary>
    /// Tag hygiene, applied to the INPUT rows (`InsightTag`) rather than to the derived facets.
    ///
    /// <para>That placement is the whole design. `SeriesTag(Source=AI)` and `ItemTag(Source=AI)` are DERIVED by
    /// the fold in <see cref="Resolve.TagFolds.RebuildAiFold"/>; cleaning them directly would be undone by the
    /// next resolve. Cleaning the insight's own tags and re-running the fold is the golden rule made
    /// operational — which is why every method here says which job to re-run.</para>
    ///
    /// <para><b>Four passes, in order:</b> apply the `TagAlias` map (spellings collapse onto one canonical
    /// value); drop era tags that are actually DATE RANGES ("1986-1992" is not an era); drop audience values
    /// that leaked into `genre` or `tone`; and move `tone:mature`, which is an audience descriptor wearing the
    /// wrong category, to `audience:mature`.</para>
    ///
    /// <para>The alias TABLE is not seeded here: the migration carried the standalone's 174 rows, and the
    /// admin's normalization panel is where new ones are added. A pass with no aliases is a legal no-op.</para>
    /// </summary>
    public sealed class DataNormalizationService
    {
        /// <summary>Audience values that leaked into the wrong category. The right tag already exists on the
        /// same subject from another pass, so these are dropped rather than moved.</summary>
        public static readonly (string Category, string[] Tags)[] CrossCategoryPollutants =
        {
            ("genre", new[] { "all-ages", "adult", "teen", "children", "children's", "mature" }),
            ("tone", new[] { "all-ages", "adult", "family-friendly", "child-friendly" }),
        };

        /// <summary>"1986-1992" in the era category is a date range, not an era name.</summary>
        private static readonly Regex EraDateRange = new(@"^\d{4}[–\-]", RegexOptions.Compiled);

        private readonly ILogger<DataNormalizationService> logger;
        public DataNormalizationService(ILogger<DataNormalizationService> logger) => this.logger = logger;

        /// <summary>
        /// Run every pass. The caller must then re-run <c>books-resolve</c> so the folds pick the cleaned tags
        /// up — this method deliberately does NOT trigger it, because an operator normalizing a batch of
        /// aliases should pay for one re-fold, not one per alias.
        /// </summary>
        public async Task<TagNormResult> NormalizeTagsAsync(BooksDb db, bool apply = true, CancellationToken ct = default)
        {
            var aliases = await ApplyTagAliasesAsync(db, apply, ct);
            var era = await RemoveEraDateRangesAsync(db, apply, ct);
            var cross = await RemoveCrossCategoryPollutionAsync(db, apply, ct);
            var tone = await MigrateToneMatureToAudienceAsync(db, apply, ct);
            var result = new TagNormResult(aliases, era, cross, tone);
            if (result.Total > 0)
                logger.LogInformation("tag normalization: {Result} — re-run books-resolve to re-fold", result);
            return result;
        }

        /// <summary>
        /// Rename every aliased tag onto its canonical spelling. The composite key means a rename can COLLIDE
        /// with a row that already carries the canonical value, so the colliding row is dropped first — the two
        /// spellings were always the same tag.
        /// </summary>
        public async Task<int> ApplyTagAliasesAsync(BooksDb db, bool apply = true, CancellationToken ct = default)
        {
            var aliases = await db.TagAlias.AsNoTracking().Where(a => a.CanonicalTag != null).ToListAsync(ct);
            if (aliases.Count == 0) return 0;

            var changed = 0;
            foreach (var alias in aliases)
            {
                if (!apply)
                {
                    changed += await db.InsightTags.CountAsync(t => t.Category == alias.Category && t.Value == alias.AliasTag, ct);
                    continue;
                }
                await db.Database.ExecuteSqlRawAsync(@"
DELETE FROM InsightTag
WHERE Category = {0} AND Value = {1}
  AND EXISTS (SELECT 1 FROM InsightTag t2 WHERE t2.InsightId = InsightTag.InsightId AND t2.Category = {0} AND t2.Value = {2})",
                    alias.Category, alias.AliasTag, alias.CanonicalTag!);
                changed += await db.Database.ExecuteSqlRawAsync(
                    "UPDATE InsightTag SET Value = {0} WHERE Category = {1} AND Value = {2}",
                    alias.CanonicalTag!, alias.Category, alias.AliasTag);
            }
            return changed;
        }

        public async Task<int> RemoveEraDateRangesAsync(BooksDb db, bool apply = true, CancellationToken ct = default)
        {
            var era = await db.InsightTags.Where(t => t.Category == "era").ToListAsync(ct);
            var doomed = era.Where(t => EraDateRange.IsMatch(t.Value)).ToList();
            if (doomed.Count == 0 || !apply) return doomed.Count;
            db.InsightTags.RemoveRange(doomed);
            await db.SaveChangesAsync(ct);
            return doomed.Count;
        }

        public async Task<int> RemoveCrossCategoryPollutionAsync(BooksDb db, bool apply = true, CancellationToken ct = default)
        {
            var removed = 0;
            foreach (var (category, tags) in CrossCategoryPollutants)
                foreach (var tag in tags)
                    removed += apply
                        ? await db.InsightTags.Where(t => t.Category == category && t.Value == tag).ExecuteDeleteAsync(ct)
                        : await db.InsightTags.CountAsync(t => t.Category == category && t.Value == tag, ct);
            return removed;
        }

        /// <summary>`tone:mature` is an audience descriptor in the wrong category: copy it across where it is
        /// missing, then drop every tone row.</summary>
        public async Task<int> MigrateToneMatureToAudienceAsync(BooksDb db, bool apply = true, CancellationToken ct = default)
        {
            if (!apply) return await db.InsightTags.CountAsync(t => t.Category == "tone" && t.Value == "mature", ct);
            await db.Database.ExecuteSqlRawAsync(@"
INSERT OR IGNORE INTO InsightTag (InsightId, Category, Value)
SELECT InsightId, 'audience', 'mature' FROM InsightTag WHERE Category = 'tone' AND Value = 'mature'");
            return await db.InsightTags.Where(t => t.Category == "tone" && t.Value == "mature").ExecuteDeleteAsync(ct);
        }

        /// <summary>Add or update one alias. An alias is an INPUT — the fold re-reads it on the next resolve.</summary>
        public async Task<TagAlias> UpsertAliasAsync(BooksDb db, string category, string aliasTag, string canonicalTag, CancellationToken ct = default)
        {
            category = category.Trim().ToLowerInvariant();
            aliasTag = aliasTag.Trim().ToLowerInvariant();
            canonicalTag = canonicalTag.Trim().ToLowerInvariant();
            if (category.Length == 0 || aliasTag.Length == 0 || canonicalTag.Length == 0)
                throw new ArgumentException("category, alias and canonical are all required.");
            if (aliasTag == canonicalTag) throw new ArgumentException("An alias cannot point at itself.");

            var row = await db.TagAlias.FirstOrDefaultAsync(a => a.Category == category && a.AliasTag == aliasTag, ct);
            if (row == null) { row = new TagAlias { Category = category, AliasTag = aliasTag }; db.TagAlias.Add(row); }
            row.CanonicalTag = canonicalTag;
            row.Source = "Admin";
            await db.SaveChangesAsync(ct);
            return row;
        }

        public async Task<bool> DeleteAliasAsync(BooksDb db, string category, string aliasTag, CancellationToken ct = default)
        {
            var row = await db.TagAlias.FirstOrDefaultAsync(a => a.Category == category && a.AliasTag == aliasTag, ct);
            if (row == null) return false;
            db.TagAlias.Remove(row);
            await db.SaveChangesAsync(ct);
            return true;
        }
    }
}
