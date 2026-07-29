using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using MovieTheater.Db;

namespace MovieTheater.Channels
{
    /// <summary>Which kinds of library content a channel may air. Default <see cref="Movies"/> keeps
    /// pre-existing channels movies-only (their stored FilterJson has no <c>Kinds</c> field).</summary>
    [Flags]
    public enum ContentKinds
    {
        Movies = 1,
        Series = 2,
        Misc = 4,
    }

    /// <summary>An inclusive numeric range; either bound may be null ("no floor"/"no ceiling").
    /// Named to avoid clashing with <see cref="System.Range"/>.</summary>
    public sealed class FilterRange
    {
        public double? Min { get; set; }
        public double? Max { get; set; }

        public FilterRange() { }
        public FilterRange(double? min, double? max) { Min = min; Max = max; }
    }

    /// <summary>A predicate over the AI discovery tags (<see cref="TitleTag"/>). Multiple rules on a
    /// filter AND together (each becomes its own EXISTS); within a rule the values OR (Mode "any") or
    /// AND (Mode "all"); <see cref="Negate"/> flips it to "must NOT have".</summary>
    public sealed class TagRule
    {
        public TagCategory Category { get; set; }
        public List<string> Values { get; set; } = new();

        /// <summary>"any" (default) or "all".</summary>
        public string Mode { get; set; } = "any";

        public bool Negate { get; set; }
    }

    /// <summary>A credit predicate ("directed by / starring"). People in <see cref="PersonIds"/> match
    /// with OR (an ensemble — e.g. Lee or Cushing); multiple <see cref="CreditRule"/>s on a filter AND
    /// together (a pairing — e.g. Scorsese and De Niro). A null <see cref="Role"/> matches any role.</summary>
    public sealed class CreditRule
    {
        public List<int> PersonIds { get; set; } = new();
        public CreditRole? Role { get; set; }
    }

    /// <summary>
    /// The eligibility rule for a channel, serialized into <see cref="Db.Channel.FilterJson"/>
    /// (streaming-plan.md §8). All fields optional; an empty filter means "every Movie that has a
    /// playable file and isn't excluded from random". Every field added beyond the original
    /// genre/year/MPAA/unwatched set is nullable/empty by default, so old FilterJson deserializes
    /// unchanged (back-compat) and <see cref="Parse"/> tolerates unknown keys.
    /// </summary>
    public class ChannelFilter
    {
        // ── Original facets (unchanged; still honored) ──
        public List<int> GenreIds { get; set; } = new();

        /// <summary>"any" (default) or "all" — whether a title must match one or every listed genre.</summary>
        public string GenreMode { get; set; } = "any";

        public int? YearMin { get; set; }
        public int? YearMax { get; set; }

        /// <summary>Inclusive MPA rating-id ceiling (1=G … 7=Unknown). Null = no ceiling.</summary>
        public int? MaxMpaRatingId { get; set; }

        /// <summary>Exclude adult (NC-17 / X) titles regardless of any ceiling. Default true — most
        /// channels avoid them; an explicitly adult channel sets this false.</summary>
        public bool ExcludeAdult { get; set; } = true;

        /// <summary>When set, exclude titles this user has already marked Seen.</summary>
        public int? UnwatchedByUserId { get; set; }

        public bool ExcludeRemoveFromRandom { get; set; } = true;

        // ── Content kinds (default Movies => back-compat) ──
        public ContentKinds Kinds { get; set; } = ContentKinds.Movies;

        // ── Numeric ranges (each compiles to x >= Min && x <= Max in SQL) ──
        public FilterRange? ImdbRating { get; set; }       // ImdbRatingScraped (decimal)
        public FilterRange? Tomatometer { get; set; }      // RtTomatometer (0–100)
        public FilterRange? Popcornmeter { get; set; }     // RtPopcornmeter (0–100)
        public FilterRange? Popularity { get; set; }       // TmdbPopularity
        public FilterRange? VoteCount { get; set; }        // TmdbVoteCount
        public FilterRange? Runtime { get; set; }          // RuntimeMinutes

        // ── AI sliders (live on the BEST TitleInsight per subject; names match the DB columns) ──
        public FilterRange? CultClassic { get; set; }
        public FilterRange? Surrealism { get; set; }
        public FilterRange? Intensity { get; set; }
        public FilterRange? Novelty { get; set; }
        public FilterRange? Rewatchability { get; set; }
        public FilterRange? Energy { get; set; }

        /// <summary>When true, only titles whose current insight is <see cref="TitleInsight.Recognized"/>
        /// qualify for AI predicates — drops low-trust model guesses.</summary>
        public bool RequireRecognized { get; set; }

        // ── Provenance (origin language / country) ──
        public List<string> Languages { get; set; } = new();         // OriginalLanguage include (OR)
        public List<string> ExcludeLanguages { get; set; } = new();  // e.g. World Cinema = NOT "en"
        public List<string> Countries { get; set; } = new();         // Country contains (OR)

        /// <summary>Broadcast networks to match (Series.Network contains any). Movies can't satisfy this.</summary>
        public List<string> Networks { get; set; } = new();

        /// <summary>Match titles whose on-disk file path contains any of these substrings (OR) — the
        /// escape hatch for on-disk collections the DB doesn't tag as a franchise (e.g. "Looney Tunes",
        /// "Criterion", a set of Nickelodeon show folders). Applies to a title's playable file path.</summary>
        public List<string> PathContains { get; set; } = new();

        // ── AI tag rules (each ANDs; see TagRule) ──
        public List<TagRule> Tags { get; set; } = new();

        // ── Credits ("directed by / starring X") ──
        public List<CreditRule> Credits { get; set; } = new();

        // ── Personalization + freshness ──
        /// <summary>When set, restrict to titles this user has on their Want-to-Watch list.</summary>
        public int? WantedByUserId { get; set; }

        /// <summary>When set, restrict to titles the recommendation engine picked for this user
        /// (rows in <c>TitleRecommendation</c>) — the pool of a personalized "For You" channel. The
        /// per-title <c>Score</c> also drives how often each recurs (see the RecommendationWeighted
        /// schedule strategy).</summary>
        public int? RecommendedForUserId { get; set; }

        /// <summary>Require the title to have been marked Seen by at least this many distinct users
        /// (community popularity). Null = no requirement.</summary>
        public int? MinViewers { get; set; }

        /// <summary>When set, restrict to titles added to the library within the last N days.</summary>
        public int? AddedWithinDays { get; set; }

        /// <summary>When set, restrict to titles <em>released</em> within the last N years — a rolling
        /// window measured from "now" at query time, not a baked-in year range. Deliberately rolling
        /// rather than <see cref="YearMin"/>/<see cref="YearMax"/>: the schedule is materialized ~48h
        /// ahead, so a "New Releases" channel re-evaluates this on every extension and stays current
        /// without anyone editing the catalog each January. A calendar-year equivalent
        /// (<c>year >= now.Year - 1</c>) would instead collapse the pool on Jan 1.</summary>
        public int? ReleasedWithinYears { get; set; }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static ChannelFilter Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new ChannelFilter();
            try
            {
                return JsonSerializer.Deserialize<ChannelFilter>(json, JsonOptions) ?? new ChannelFilter();
            }
            catch (JsonException)
            {
                return new ChannelFilter();
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    }
}
