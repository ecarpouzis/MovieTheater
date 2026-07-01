using System.Collections.Generic;
using MovieTheater.Db;

namespace MovieTheater.Services.Recommendations
{
    /// <summary>
    /// The feature vector for one title (a Movie or a Series), assembled from the DB by the caller so
    /// the engine stays pure and unit-testable. Categorical signals live in <see cref="Features"/> as
    /// <c>key → presence(0..1)</c>; the six AI sliders and the critical scores are separate because they
    /// are modeled as continuous axes, not membership.
    ///
    /// <para>Feature-key grammar: <c>genre:Horror</c>, <c>dir:{personId}</c>, <c>act:{personId}</c>,
    /// <c>wri:{personId}</c>, <c>tag:{Category}:{value}</c>, <c>decade:1990</c>, <c>lang:ja</c>.</para>
    /// </summary>
    public sealed class TitleFeatures
    {
        public int SubjectId { get; init; }
        public InsightSubjectKind Kind { get; init; }
        public string? Title { get; init; }

        /// <summary>Categorical features → presence/salience in [0,1] (tag Weight/100, primary genre 1,
        /// secondary genre 0.7, credited person 1, etc.).</summary>
        public Dictionary<string, double> Features { get; init; } = new();

        // AI sliders (0..100), null when the model didn't judge that axis.
        public double? Surrealism { get; init; }
        public double? CultClassic { get; init; }
        public double? Intensity { get; init; }
        public double? Novelty { get; init; }
        public double? Rewatchability { get; init; }
        public double? Energy { get; init; }

        // Critical reception (raw; the engine standardizes against the library).
        public double? ImdbRating { get; init; }   // 0..10
        public double? RtTomato { get; init; }      // 0..100 (critics)
        public double? RtPopcorn { get; init; }     // 0..100 (audience)
        public double? Popularity { get; init; }    // TMDB popularity
        public int Viewers { get; init; }           // distinct users who marked it Seen (friends-popularity)

        /// <summary>Normalized "watch if you liked …" comp values (<see cref="TagCategory.CompTitle"/>).</summary>
        public List<string> CompTitles { get; init; } = new();
    }

    /// <summary>A title the user has rated, with a 0..100 score and a recency ordinal (0 = most recent,
    /// derived from the monotonic <see cref="Viewing.ViewingID"/> since Viewing has no timestamp).</summary>
    public sealed class RatedTitle
    {
        public TitleFeatures Features { get; init; } = default!;
        public double Score { get; init; }
        public int RecencyRank { get; init; }
    }

    /// <summary>Library-wide aggregates the engine needs to weight distinctiveness — computed once per
    /// run over the whole streamable set of a kind, then reused for every user.</summary>
    public sealed class LibraryStats
    {
        public int TitleCount { get; init; }
        /// <summary>df_f — how many library titles carry each feature (for IDF).</summary>
        public Dictionary<string, int> DocFreq { get; init; } = new();
        /// <summary>base_f — mean critical z-score of library titles carrying f (acclaim decorrelation).</summary>
        public Dictionary<string, double> FeatureCriticalZ { get; init; } = new();
        public double CriticalMean { get; init; }
        public double CriticalStd { get; init; }
    }

    /// <summary>A learned preference on one continuous slider axis.</summary>
    public sealed class SliderPref
    {
        public string Name { get; init; } = "";
        public double Center { get; init; }     // preferred value (0..100)
        public double Sigma { get; init; }      // tolerance
        public double Importance { get; init; } // 0..1 — how much this axis explains their liking
    }

    /// <summary>The learned weight for one categorical feature.</summary>
    public sealed class FeatureWeight
    {
        public double Weight { get; init; }    // W_f (signed) — drives candidate scoring
        public double Distinct { get; init; }  // a_f' − λ·base_f — idiosyncrasy vs. universal acclaim
        public double Signature { get; init; } // Distinct·idf — distinctive AND specific
        public double Support { get; init; }   // effective (recency-weighted) count n_f
        public double Affinity { get; init; }  // a_f' — shrunk mean deviation (z units)
    }

    /// <summary>Everything the engine learned about one user — reused across candidate scoring, exposed
    /// for the dry-run dossier, the stored <see cref="UserTasteProfile"/>, and reason rendering.</summary>
    public sealed class TasteProfile
    {
        public double MeanRating { get; init; }
        public double StdRating { get; init; }
        public int RatingCount { get; init; }
        public double PersonalizationWeight { get; init; } // ρ = n/(n+K)
        public double AcclaimAffinity { get; init; }       // μ_crit ∈ [−1,1]
        public Dictionary<string, FeatureWeight> Features { get; init; } = new();
        public List<SliderPref> Sliders { get; init; } = new();
        public HashSet<string> LovedTitleNames { get; init; } = new();
        public List<KeyValuePair<string, double>> TopSignature { get; init; } = new();
    }

    /// <summary>One produced recommendation. <see cref="ReasonKeys"/> are the drivers (feature keys +
    /// flags like <c>comp:heat</c>); the caller renders them to <c>ReasonText</c> with its id→name map.</summary>
    public sealed class Recommendation
    {
        public int SubjectId { get; init; }
        public InsightSubjectKind Kind { get; init; }
        public string? Title { get; init; }
        public double Score { get; init; } // 0..100
        public int Rank { get; init; }
        public List<string> ReasonKeys { get; init; } = new();

        /// <summary>How many distinct signals contributed positively to this pick (features + slider
        /// preferences + comp match). The fit is an aggregate of all of them; <see cref="ReasonKeys"/>
        /// are only the strongest few, so the UI can honestly say "…and N more signals".</summary>
        public int SignalCount { get; init; }
    }

    /// <summary>Tunable constants for the scoring model (see the plan / docs for the meaning of each).</summary>
    public sealed class RecommendationOptions
    {
        public double RecencyHalfLife { get; init; } = 30;    // in number of ratings
        public double Shrinkage { get; init; } = 3;           // K
        public double IdfAlpha { get; init; } = 0.5;          // W_f = a'·(α + β·idf)
        public double IdfBeta { get; init; } = 1.0;
        public double AcclaimLambda { get; init; } = 0.5;     // Distinct = a' − λ·base_f
        public double AcclaimWeight { get; init; } = 0.4;     // scale of the acclaim term in Fit
        public double CompWeight { get; init; } = 1.5;        // comp-title graph bonus
        public double MmrLambda { get; init; } = 0.75;        // relevance vs. diversity in MMR
        public double ColdStartK { get; init; } = 8;          // ρ = n/(n+K)
        public double MinFeatureSupport { get; init; } = 0.5; // ignore features below this effective support
        public int MinSliderSupport { get; init; } = 3;       // min rated titles carrying a slider to use it
        public double SigmaFloor { get; init; } = 12;
        public double SigmaCap { get; init; } = 40;
        public double StdFloor { get; init; } = 5;            // floor on a user's rating spread
        public double LovedZThreshold { get; init; } = 0.5;   // z ≥ this ⇒ "loved" (comp graph)
        public int TopSignatureCount { get; init; } = 12;
        public int MmrPoolFactor { get; init; } = 8;          // MMR runs over top (factor·topN) by blended score
        public int AlgoVersion { get; init; } = 1;
    }
}
