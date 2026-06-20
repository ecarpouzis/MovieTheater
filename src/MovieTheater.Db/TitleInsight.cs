using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Db
{
    /// <summary>
    /// Model-inferred metadata about a title (a <see cref="Movie"/> or <see cref="Series"/>), generated
    /// from the language model's own knowledge rather than fetched from IMDb/TMDB/OMDB. It powers
    /// "find me something that feels like X" discovery (themes, moods, surrealism, cult status,
    /// comp titles) and never overwrites the frozen authoritative columns on the subject — it is a
    /// purely additive side table.
    ///
    /// <para><b>Provenance is first-class.</b> A model won't confidently recognize every obscure
    /// video, so each row records <em>which</em> model produced it (<see cref="ModelId"/>), when
    /// (<see cref="GeneratedUtc"/>), under which field-set (<see cref="SpecVersion"/>), and how much to
    /// trust it (<see cref="Recognized"/> / <see cref="Confidence"/>). Re-generating a subject with a
    /// newer model <b>inserts a new row</b>; older rows are kept for comparison. "Current" = the newest
    /// <see cref="GeneratedUtc"/> for a given (<see cref="SubjectKind"/>, <see cref="SubjectId"/>).</para>
    /// </summary>
    [Table("TitleInsight")]
    [Index(nameof(SubjectKind), nameof(SubjectId))] // current-row lookup + the work-queue "do I have one?" probe
    public class TitleInsight
    {
        [Key]
        public int Id { get; set; }

        // ── Subject (shared id space, disambiguated by kind — same pattern as Movie/Series today) ──
        public InsightSubjectKind SubjectKind { get; set; }

        /// <summary><see cref="Movie.id"/> or <see cref="Series.Id"/>, per <see cref="SubjectKind"/>.</summary>
        public int SubjectId { get; set; }

        // ── Provenance (the reason this table exists) ──

        /// <summary>The model that produced this row, e.g. "claude-opus-4-8".</summary>
        [MaxLength(40)]
        public string ModelId { get; set; } = default!;

        public DateTime GeneratedUtc { get; set; }

        /// <summary>The field-set / prompt spec version this row was generated under; bump when the
        /// shape of an insight changes so stale rows can be identified and re-generated.</summary>
        public int SpecVersion { get; set; }

        /// <summary>Did the model actually recognize this specific title (vs. guessing from the
        /// filename/genre)? False ⇒ everything below is low-trust and a candidate for re-generation
        /// by a more-knowledgeable model.</summary>
        public bool Recognized { get; set; }

        public InsightConfidence Confidence { get; set; }

        // ── Narrative (free text, for the modal / "why interesting") ──

        /// <summary>One-line feel, e.g. "cozy 90s slacker noir".</summary>
        public string? Vibe { get; set; }

        /// <summary>Short pitch / hook — why this might be worth pulling off the shelf.</summary>
        public string? WhyInteresting { get; set; }

        /// <summary>Comp titles + occasions in prose ("watch if you liked …"). The structured,
        /// filterable comparisons live as <see cref="TagCategory.CompTitle"/> tags.</summary>
        public string? WatchIfYouLiked { get; set; }

        /// <summary>Notable people note — "the one where &lt;actor&gt; …", recurring collaborators.</summary>
        public string? PeopleNote { get; set; }

        // ── Scalar sliders (0–100; null = not judged). The "find something that feels like X" dials. ──
        public int? Surrealism { get; set; }
        public int? CultClassic { get; set; }

        /// <summary>Content intensity / darkness.</summary>
        public int? Intensity { get; set; }

        /// <summary>Obscurity / how off-the-beaten-path the title is.</summary>
        public int? Novelty { get; set; }

        public int? Rewatchability { get; set; }

        /// <summary>Pacing / kineticism.</summary>
        public int? Energy { get; set; }

        public ICollection<TitleTag> Tags { get; set; } = [];
    }
}
