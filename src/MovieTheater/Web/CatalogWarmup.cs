using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Web
{
    /// <summary>
    /// What the catalog LOOKS like right now, in eight scalars — the Long Box's `PRAGMA data_version`
    /// idea against SQL Server, where no such counter exists. It is deliberately cheap (indexed COUNTs
    /// and MAXes, no joins, no scans of text) and deliberately COARSE: it does not care what changed,
    /// only that something did.
    ///
    /// A warm is gated on this CHANGING, never on a clock and never on a request arriving — a timer
    /// re-warms a library that has not moved in a week, and request-driven warming is just a slow
    /// first request with extra steps.
    /// </summary>
    public readonly record struct CatalogFingerprint(
        int Movies, int Series, int Misc, int Insights, int Viewings,
        long MovieStamp, long SeriesStamp, long InsightStamp)
    {
        public static readonly CatalogFingerprint None = default;

        public bool IsEmpty => this == default;

        /// <summary>One short line for the log — a warm's reason has to be readable at 3am.</summary>
        public override string ToString() =>
            $"m={Movies} s={Series} x={Misc} i={Insights} v={Viewings} @{MovieStamp}/{SeriesStamp}/{InsightStamp}";

        private static long Ticks(DateTime? d) => d?.Ticks ?? 0L;

        /// <summary>
        /// Reads the fingerprint. READ-ONLY by construction — counts and maxes, nothing else — because
        /// the connection this runs on is the live shared database.
        /// </summary>
        public static async Task<CatalogFingerprint> ReadAsync(MovieDb db, CancellationToken ct = default)
        {
            var movies = await db.Movies.CountAsync(m => m.ReviewBatch == null, ct);
            var series = await db.Series.CountAsync(s => s.ReviewBatch == null, ct);
            var misc = await db.MiscVideos.CountAsync(v => v.ReviewBatch == null, ct);
            var insights = await db.TitleInsights.CountAsync(ct);
            var viewings = await db.Viewings.CountAsync(ct);
            var movieStamp = await db.Movies.MaxAsync(m => (DateTime?)m.UploadedDate, ct);
            var seriesStamp = await db.Series.MaxAsync(s => (DateTime?)s.UploadedDate, ct);
            var insightStamp = await db.TitleInsights.MaxAsync(t => (DateTime?)t.GeneratedUtc, ct);
            return new CatalogFingerprint(movies, series, misc, insights, viewings,
                Ticks(movieStamp), Ticks(seriesStamp), Ticks(insightStamp));
        }
    }

    /// <summary>Why a warm pass is (or is not) happening. `Warm == false` means the loop sleeps again.</summary>
    public readonly record struct WarmDecision(bool Warm, string Reason);

    public sealed class CatalogWarmupOptions
    {
        /// <summary>How often the FINGERPRINT is read. Cheap; this is not the warm interval.</summary>
        public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(5);
        /// <summary>A warm happens at least this often even if nothing changed — the backstop for a cache that expired.</summary>
        public TimeSpan BackstopTtl { get; init; } = TimeSpan.FromHours(4);
        /// <summary>
        /// The floor between two warms. Viewing counts are part of the fingerprint (a mark changes what
        /// `my=` scopes see), so without a floor a burst of ticks on the Rate page would re-warm the
        /// whole index once per tick. A change inside the floor is not lost — it warms at the next check.
        /// </summary>
        public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(2);
        /// <summary>Pause between warm targets, so the pass never occupies the pool.</summary>
        public TimeSpan StepPause { get; init; } = TimeSpan.FromSeconds(2);
        /// <summary>Off by default in Development; the pods turn it on.</summary>
        public bool Enabled { get; init; } = true;
    }

    public static class CatalogWarmupPlan
    {
        /// <summary>
        /// The whole gating decision, pure and testable: warm on the FIRST pass, on a fingerprint
        /// change, or when the backstop has elapsed — and never inside the minimum interval.
        /// </summary>
        public static WarmDecision Decide(
            CatalogFingerprint? previous, CatalogFingerprint current,
            DateTime? lastWarmUtc, DateTime nowUtc, CatalogWarmupOptions options)
        {
            if (!options.Enabled) return new WarmDecision(false, "disabled");
            if (lastWarmUtc is DateTime last && nowUtc - last < options.MinInterval)
                return new WarmDecision(false, "inside the minimum interval");
            if (previous is not CatalogFingerprint prev || lastWarmUtc == null)
                return new WarmDecision(true, "first pass");
            if (!prev.Equals(current))
                return new WarmDecision(true, $"catalog changed ({prev} → {current})");
            if (nowUtc - lastWarmUtc.Value >= options.BackstopTtl)
                return new WarmDecision(true, "backstop TTL elapsed");
            return new WarmDecision(false, "unchanged");
        }
    }

    /// <summary>One thing to warm: a scope + what to build over it. Named so the log line is legible.</summary>
    public readonly record struct WarmTarget(string Name, IReadOnlyList<NormalizedTitleType> TypeScope, string? GroupBy)
    {
        public bool IsFacets => GroupBy == null;
    }

    public static class CatalogWarmupTargets
    {
        /// <summary>The three axes worth a warm in EVERY Type scope — the ones the pill opens on.</summary>
        public static readonly IReadOnlyList<string> CoreAxes = new[] { "genre", "decade", "franchise" };

        /// <summary>
        /// The rest of the user-independent axes the Group pill offers (R9 S8). They warm over the two
        /// scopes that carry the traffic — the landing (`f=type:Movies`) and the combined one — rather
        /// than all three, because the cache is byte-BUDGETED (200 MB, `Startup`) and an index costs
        /// roughly one row per (title, group): a per-scope copy of ten axes would spend a third of the
        /// budget on shelves nobody opened. The Series-only copies of these axes are built on first ask.
        /// `my` is absent by construction: it reads the caller's own lists, so there is no shared entry
        /// to warm (`BrowseGroups.IsUserDependent`).
        /// </summary>
        public static readonly IReadOnlyList<string> WideAxes = new[] { "type", "mpa", "director", "subgenre", "mood", "era", "setting" };

        /// <summary>
        /// What the pods warm, in the order the SPA asks for it: the landing's Type scope first
        /// (`f=type:Movies` is the seeded default), then Series, then the combined scope.
        ///
        /// Misc-inclusive scopes are deliberately absent: their index needs the misc CARD projection,
        /// which lives on the controller, and misc is a small in-memory list that costs nothing cold.
        /// </summary>
        public static IReadOnlyList<WarmTarget> Default()
        {
            var movies = new[] { NormalizedTitleType.Movies };
            var series = new[] { NormalizedTitleType.Series };
            var both = new[] { NormalizedTitleType.Movies, NormalizedTitleType.Series };
            var list = new List<WarmTarget>
            {
                new("facets:Movies", movies, null),
                new("facets:Series", series, null),
                // The combined scope carries traffic — this file already warms ten GROUP axes for it —
                // and its facet counts were the one thing left cold: 4.75 s on prod, 0.14 s once warm.
                new("facets:Movies,Series", both, null),
                // The empty scope is "all types", which is what CLEARING the Type chip leaves behind
                // (`moviesFacetSpec`: the landing seeds `f=type:Movies` once per tab session, so clearing
                // it later means all types). It is the widest count pass there is and it was never warmed:
                // 10.7 s on prod, and the 6 h TTL means the first reader after every expiry — and after
                // every deploy, which is every push — paid it again.
                new("facets:all", Array.Empty<NormalizedTitleType>(), null),
            };
            foreach (var by in CoreAxes)
            {
                list.Add(new WarmTarget($"groups:Movies:{by}", movies, by));
                list.Add(new WarmTarget($"groups:Series:{by}", series, by));
                list.Add(new WarmTarget($"groups:Movies,Series:{by}", both, by));
            }
            foreach (var by in WideAxes)
            {
                list.Add(new WarmTarget($"groups:Movies:{by}", movies, by));
                list.Add(new WarmTarget($"groups:Movies,Series:{by}", both, by));
            }
            return list;
        }
    }
}
