using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Db
{
    /// <summary>
    /// One precomputed personalized recommendation: title <see cref="SubjectId"/> is a good fit for
    /// user <see cref="UserId"/>, with a normalized <see cref="Score"/> (0–100) and an MMR-diversified
    /// <see cref="Rank"/>. Produced by the <c>compute-recommendations</c> job from the user's ratings
    /// and refreshed as they rate / as the library grows; the per-user "For You" channels read this
    /// table via <c>ChannelFilter.RecommendedForUserId</c>.
    ///
    /// <para>Keyed to a title through the shared id space (<see cref="SubjectKind"/> + <see cref="SubjectId"/>),
    /// the same no-FK pattern as <see cref="TitleInsight"/> / <see cref="Viewing"/>. A user has at most one
    /// row per title (the unique index), so a refresh is an idempotent upsert.</para>
    /// </summary>
    [Table("TitleRecommendation")]
    [Index(nameof(UserId), nameof(SubjectKind), nameof(SubjectId), IsUnique = true)]
    public class TitleRecommendation
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        /// <summary><see cref="InsightSubjectKind.Movie"/> or <see cref="InsightSubjectKind.Series"/>.</summary>
        public InsightSubjectKind SubjectKind { get; set; }

        /// <summary><see cref="Movie.id"/> or <see cref="Series.Id"/>, per <see cref="SubjectKind"/>.</summary>
        public int SubjectId { get; set; }

        /// <summary>Normalized personal-fit score, 0–100 (higher = better fit). Drives how often the
        /// title recurs on the reco channel (see the RecommendationWeighted schedule strategy).</summary>
        public double Score { get; set; }

        /// <summary>Position in the diversified (MMR) ranking; 0 = the single best pick.</summary>
        public int Rank { get; set; }

        /// <summary>Short, templated "why you'll like this" line surfaced in the TV UI.</summary>
        public string? ReasonText { get; set; }

        /// <summary>The engine/spec version that produced this row, so a rubric change can invalidate
        /// old rows deterministically (mirrors <see cref="TitleInsight.SpecVersion"/>).</summary>
        public int AlgoVersion { get; set; }

        public DateTime GeneratedUtc { get; set; }
    }
}
