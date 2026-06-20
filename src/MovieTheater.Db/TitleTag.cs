using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Db
{
    /// <summary>
    /// One categorized, model-inferred tag on a <see cref="TitleInsight"/> — the multi-valued,
    /// searchable discovery layer (themes, moods, subgenres, franchises, art styles, comp titles…).
    /// Tags hang off the insight, so they inherit its provenance and are replaced wholesale when a
    /// subject is re-generated. The <see cref="Category"/> + <see cref="Value"/> index is what makes
    /// "give me all heist / all surreal / all &lt;franchise&gt;" cheap.
    /// </summary>
    [Table("TitleTag")]
    [Index(nameof(Category), nameof(Value))]
    public class TitleTag
    {
        [Key]
        public int Id { get; set; }

        public int TitleInsightId { get; set; }

        [ForeignKey(nameof(TitleInsightId))]
        public TitleInsight Insight { get; set; } = default!;

        public TagCategory Category { get; set; }

        /// <summary>Normalized (lower-case, trimmed) tag value, e.g. "heist". Normalized on write by
        /// the loader against the controlled vocabulary so values don't fragment.</summary>
        [MaxLength(80)]
        public string Value { get; set; } = default!;

        /// <summary>Optional salience 0–100 — how central this tag is to the title.</summary>
        public int? Weight { get; set; }
    }
}
