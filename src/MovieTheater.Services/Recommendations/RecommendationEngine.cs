using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MovieTheater.Services.Recommendations
{
    /// <summary>
    /// A content-based, distinctiveness-weighted taste model. It learns what is <em>unique</em> about a
    /// user's likes and dislikes from their 0–100 ratings and ranks the whole library by personal fit —
    /// deliberately <b>not</b> "popular with similar people → generic popular picks".
    ///
    /// <para>The core ideas (see the plan for the full write-up): center ratings on the user's own mean
    /// (handles harsh/generous graders and the ambiguous stored <c>0</c>); recency-weight by rating order;
    /// per-feature affinity with Bayesian shrinkage; multiply by <b>IDF</b> (library rarity) and subtract an
    /// <b>acclaim baseline</b> so universally-loved traits don't masquerade as personal taste; model the six
    /// AI sliders as Gaussian preferences; reward the CompTitle graph; and diversify the final lineup with
    /// MMR. Cold-start blends toward acclaim/popularity until enough ratings exist.</para>
    ///
    /// <para>Pure and deterministic — it operates only on data the caller supplies, so it is trivially
    /// unit-testable and has no DB/EF dependency.</para>
    /// </summary>
    public sealed class RecommendationEngine
    {
        public RecommendationOptions Opt { get; }

        public RecommendationEngine(RecommendationOptions? opt = null) => Opt = opt ?? new RecommendationOptions();

        private static readonly string[] SliderNames =
            { "Surrealism", "CultClassic", "Intensity", "Novelty", "Rewatchability", "Energy" };

        // ── Library aggregates (computed once per run, reused for every user) ──────────────────────

        /// <summary>Document frequency per feature (for IDF) and the mean critical z-score of titles
        /// carrying each feature (for acclaim decorrelation), over the whole streamable set of a kind.</summary>
        public static LibraryStats BuildLibraryStats(IReadOnlyList<TitleFeatures> all)
        {
            var df = new Dictionary<string, int>();
            foreach (var t in all)
                foreach (var kv in t.Features)
                    if (kv.Value > 0) df[kv.Key] = df.GetValueOrDefault(kv.Key) + 1;

            var comps = new List<double>();
            foreach (var t in all)
                if (CriticalComposite(t) is double c) comps.Add(c);
            double mean = comps.Count > 0 ? comps.Average() : 50.0;
            double std = comps.Count > 1 ? Std(comps, mean) : 1.0;
            if (std < 1e-6) std = 1.0;

            var sum = new Dictionary<string, double>();
            var cnt = new Dictionary<string, int>();
            foreach (var t in all)
            {
                if (CriticalComposite(t) is not double c) continue;
                double z = (c - mean) / std;
                foreach (var kv in t.Features)
                    if (kv.Value > 0)
                    {
                        sum[kv.Key] = sum.GetValueOrDefault(kv.Key) + z;
                        cnt[kv.Key] = cnt.GetValueOrDefault(kv.Key) + 1;
                    }
            }
            var baseF = new Dictionary<string, double>();
            foreach (var k in sum.Keys) baseF[k] = sum[k] / cnt[k];

            return new LibraryStats
            {
                TitleCount = all.Count,
                DocFreq = df,
                FeatureCriticalZ = baseF,
                CriticalMean = mean,
                CriticalStd = std,
            };
        }

        // ── Taste profile ─────────────────────────────────────────────────────────────────────────

        public TasteProfile BuildProfile(IReadOnlyList<RatedTitle> ratings, LibraryStats lib)
        {
            int n = ratings.Count;
            if (n == 0)
                return new TasteProfile { MeanRating = 0, StdRating = Opt.StdFloor, RatingCount = 0, PersonalizationWeight = 0, AcclaimAffinity = 0 };

            double mean = ratings.Average(r => r.Score);
            double std = n > 1 ? Std(ratings.Select(r => r.Score).ToList(), mean) : Opt.StdFloor;
            if (std < Opt.StdFloor) std = Opt.StdFloor;
            double rho = n / (n + Opt.ColdStartK);

            var w = new double[n];
            var z = new double[n];
            for (int i = 0; i < n; i++)
            {
                w[i] = Math.Pow(0.5, ratings[i].RecencyRank / Opt.RecencyHalfLife);
                z[i] = (ratings[i].Score - mean) / std;
            }

            // Per-feature recency+salience-weighted mean deviation.
            var num = new Dictionary<string, double>();
            var den = new Dictionary<string, double>();
            for (int i = 0; i < n; i++)
                foreach (var kv in ratings[i].Features.Features)
                {
                    if (kv.Value <= 0) continue;
                    double c = w[i] * kv.Value;
                    num[kv.Key] = num.GetValueOrDefault(kv.Key) + c * z[i];
                    den[kv.Key] = den.GetValueOrDefault(kv.Key) + c;
                }

            var feats = new Dictionary<string, FeatureWeight>();
            foreach (var k in den.Keys)
            {
                double support = den[k];
                if (support < Opt.MinFeatureSupport) continue;
                double a = num[k] / support;
                double aShrunk = a * support / (support + Opt.Shrinkage);
                int df = lib.DocFreq.GetValueOrDefault(k, 0);
                double idf = Math.Log(1.0 + (double)lib.TitleCount / (1.0 + df));
                double baseF = lib.FeatureCriticalZ.GetValueOrDefault(k, 0.0);
                double distinct = aShrunk - Opt.AcclaimLambda * baseF;
                double weight = aShrunk * (Opt.IdfAlpha + Opt.IdfBeta * idf);
                feats[k] = new FeatureWeight
                {
                    Weight = weight,
                    Distinct = distinct,
                    Signature = distinct * idf,
                    Support = support,
                    Affinity = aShrunk,
                };
            }

            // Slider preferences: preferred center (weighted toward liked titles) + how much the axis matters.
            var sliders = new List<SliderPref>();
            foreach (var name in SliderNames)
            {
                var xs = new List<double>();
                var zs = new List<double>();
                var ws = new List<double>();
                for (int i = 0; i < n; i++)
                    if (Slider(ratings[i].Features, name) is double v) { xs.Add(v); zs.Add(z[i]); ws.Add(w[i]); }
                if (xs.Count < Opt.MinSliderSupport) continue;

                var likedW = new double[xs.Count];
                double likedSum = 0;
                for (int i = 0; i < xs.Count; i++) { likedW[i] = ws[i] * Math.Max(0, zs[i]); likedSum += likedW[i]; }

                double center, sigma;
                if (likedSum < 1e-9) { center = WeightedMean(xs, ws); sigma = WeightedStd(xs, ws, center); }
                else { var lw = likedW.ToList(); center = WeightedMean(xs, lw); sigma = WeightedStd(xs, lw, center); }
                sigma = Math.Clamp(sigma, Opt.SigmaFloor, Opt.SigmaCap);

                double imp = Math.Clamp(Math.Abs(WeightedCorr(xs, zs, ws)), 0, 1);
                sliders.Add(new SliderPref { Name = name, Center = center, Sigma = sigma, Importance = imp });
            }

            // Acclaim affinity: do their high ratings track critical acclaim? (Contrarians go negative.)
            var cx = new List<double>();
            var cz = new List<double>();
            var cw = new List<double>();
            for (int i = 0; i < n; i++)
                if (CriticalComposite(ratings[i].Features) is double comp)
                { cx.Add((comp - lib.CriticalMean) / lib.CriticalStd); cz.Add(z[i]); cw.Add(w[i]); }
            double muCrit = cx.Count >= Opt.MinSliderSupport ? Math.Clamp(WeightedCorr(cx, cz, cw), -1, 1) : 0;

            // Loved titles (for the comp graph).
            var loved = new HashSet<string>();
            for (int i = 0; i < n; i++)
                if (z[i] >= Opt.LovedZThreshold)
                {
                    var nm = Normalize(ratings[i].Features.Title);
                    if (nm.Length > 0) loved.Add(nm);
                }

            var topSig = feats.Where(kv => kv.Value.Signature > 0)
                .OrderByDescending(kv => kv.Value.Signature)
                .Take(Opt.TopSignatureCount)
                .Select(kv => new KeyValuePair<string, double>(kv.Key, kv.Value.Signature))
                .ToList();

            return new TasteProfile
            {
                MeanRating = mean,
                StdRating = std,
                RatingCount = n,
                PersonalizationWeight = rho,
                AcclaimAffinity = muCrit,
                Features = feats,
                Sliders = sliders,
                LovedTitleNames = loved,
                TopSignature = topSig,
            };
        }

        // ── Candidate ranking ───────────────────────────────────────────────────────────────────────

        public IReadOnlyList<Recommendation> Recommend(
            IReadOnlyList<RatedTitle> ratings, IReadOnlyList<TitleFeatures> candidates, LibraryStats lib, int topN)
        {
            var profile = BuildProfile(ratings, lib);
            return Rank(profile, candidates, lib, topN);
        }

        /// <summary>Rank candidates for an already-built profile (kept separate so the CLI can print the
        /// profile and reuse it).</summary>
        public IReadOnlyList<Recommendation> Rank(
            TasteProfile profile, IReadOnlyList<TitleFeatures> candidates, LibraryStats lib, int topN)
        {
            int m = candidates.Count;
            if (m == 0) return new List<Recommendation>();

            var fit = new double[m];
            var prior = new double[m];
            for (int i = 0; i < m; i++)
            {
                var c = candidates[i];
                double f = 0;
                foreach (var kv in c.Features)
                    if (profile.Features.TryGetValue(kv.Key, out var fw)) f += fw.Weight * kv.Value;

                foreach (var sp in profile.Sliders)
                {
                    if (sp.Importance <= 0) continue;
                    if (Slider(c, sp.Name) is double v)
                    {
                        double d = v - sp.Center;
                        f += sp.Importance * Math.Exp(-(d * d) / (2 * sp.Sigma * sp.Sigma));
                    }
                }

                double? comp = CriticalComposite(c);
                if (comp is double cc)
                    f += Opt.AcclaimWeight * profile.AcclaimAffinity * ((cc - lib.CriticalMean) / lib.CriticalStd);

                if (CompMatch(c, profile.LovedTitleNames, out _)) f += Opt.CompWeight;

                fit[i] = f;
                prior[i] = (comp ?? lib.CriticalMean) + 5.0 * Math.Log(1 + Math.Max(0, c.Viewers));
            }

            var fitN = MinMax(fit);
            var priorN = MinMax(prior);
            double rho = profile.PersonalizationWeight;
            var blended = new double[m];
            for (int i = 0; i < m; i++) blended[i] = rho * fitN[i] + (1 - rho) * priorN[i];

            // MMR diversification over the top pool (bounded so it stays fast).
            int poolSize = Math.Min(m, Math.Max(topN, topN * Opt.MmrPoolFactor));
            var order = Enumerable.Range(0, m).OrderByDescending(i => blended[i]).Take(poolSize).ToList();
            var remaining = new HashSet<int>(order);
            var maxSim = new Dictionary<int, double>();
            foreach (var i in order) maxSim[i] = 0;
            var norms = order.ToDictionary(i => i, i => Norm(candidates[i].Features));

            var selected = new List<int>();
            int take = Math.Min(topN, order.Count);
            while (selected.Count < take && remaining.Count > 0)
            {
                int best = -1;
                double bestScore = double.NegativeInfinity;
                foreach (var i in remaining)
                {
                    double mmr = Opt.MmrLambda * blended[i] - (1 - Opt.MmrLambda) * maxSim[i];
                    if (mmr > bestScore) { bestScore = mmr; best = i; }
                }
                selected.Add(best);
                remaining.Remove(best);
                foreach (var i in remaining)
                {
                    double s = Cosine(candidates[i].Features, candidates[best].Features, norms[i], norms[best]);
                    if (s > maxSim[i]) maxSim[i] = s;
                }
            }

            var recs = new List<Recommendation>(selected.Count);
            for (int rank = 0; rank < selected.Count; rank++)
            {
                int i = selected[rank];
                var c = candidates[i];
                var (reasonKeys, signals) = BuildReasons(c, profile);
                recs.Add(new Recommendation
                {
                    SubjectId = c.SubjectId,
                    Kind = c.Kind,
                    Title = c.Title,
                    Score = Math.Round(100 * blended[i], 2),
                    Rank = rank,
                    ReasonKeys = reasonKeys,
                    SignalCount = signals,
                });
            }
            return recs;
        }

        // The strongest few reason keys PLUS a count of ALL positive signals — so the fit is never
        // misrepresented as being explained by just the few we can name.
        private (List<string> Keys, int Signals) BuildReasons(TitleFeatures c, TasteProfile profile)
        {
            var contrib = new List<(string key, double val)>();
            foreach (var kv in c.Features)
                if (profile.Features.TryGetValue(kv.Key, out var fw))
                {
                    double v = fw.Weight * kv.Value;
                    if (v > 0) contrib.Add((kv.Key, v));
                }

            // Slider preferences that meaningfully match also count toward the aggregate.
            int sliderSignals = 0;
            foreach (var sp in profile.Sliders)
            {
                if (sp.Importance <= 0.05) continue;
                if (Slider(c, sp.Name) is double v)
                {
                    double d = v - sp.Center;
                    if (sp.Importance * Math.Exp(-(d * d) / (2 * sp.Sigma * sp.Sigma)) > 0.25) sliderSignals++;
                }
            }

            bool comp = CompMatch(c, profile.LovedTitleNames, out var matched);
            int signals = contrib.Count + sliderSignals + (comp ? 1 : 0);

            var top = contrib.OrderByDescending(t => t.val).Take(3).Select(t => t.key).ToList();
            if (comp) top.Insert(0, "comp:" + matched);
            return (top.Take(3).ToList(), signals);
        }

        // ── Reason rendering (shared by the CLI and, later, the web layer) ─────────────────────────

        /// <summary>
        /// Render the strongest reason threads into an honest one-liner. The fit is an aggregate of many
        /// weighted signals, so this names only the top few and appends "…plus N more signals" rather than
        /// implying those few ARE the reason. <paramref name="personName"/> resolves a person id → name;
        /// <paramref name="totalSignals"/> is the count of all positive contributors (Recommendation.SignalCount).
        /// </summary>
        public static string RenderReason(IReadOnlyList<string> keys, Func<int, string?> personName, int totalSignals = 0)
        {
            var parts = new List<string>();
            foreach (var k in keys)
            {
                if (DescribeFeature(k, personName) is string d && !parts.Contains(d)) parts.Add(d);
                if (parts.Count >= 3) break;
            }
            if (parts.Count == 0)
                return totalSignals > 0 ? $"Matches your taste across {totalSignals} signals." : "Picked for your taste.";

            string joined = parts.Count == 1
                ? parts[0]
                : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
            int more = Math.Max(0, totalSignals - parts.Count);
            string tail = more > 0 ? $", plus {more} more signal{(more == 1 ? "" : "s")}" : "";
            return $"Among your strongest matches: {joined}{tail}.";
        }

        public static string? DescribeFeature(string key, Func<int, string?> personName)
        {
            if (key.StartsWith("comp:", StringComparison.Ordinal)) return $"films like {key[5..]}";
            if (key.StartsWith("genre:", StringComparison.Ordinal)) return key[6..];
            if (key.StartsWith("decade:", StringComparison.Ordinal)) return $"{key[7..]}s films";
            if (key.StartsWith("lang:", StringComparison.Ordinal))
                return LangName(key[5..]) is string ln ? $"{ln}-language films" : null;
            if (key.StartsWith("dir:", StringComparison.Ordinal))
                return int.TryParse(key[4..], out var d) && personName(d) is string n ? $"films by {n}" : null;
            if (key.StartsWith("act:", StringComparison.Ordinal))
                return int.TryParse(key[4..], out var a) && personName(a) is string n ? n : null;
            if (key.StartsWith("wri:", StringComparison.Ordinal))
                return int.TryParse(key[4..], out var wr) && personName(wr) is string n ? $"writing by {n}" : null;
            if (key.StartsWith("tag:", StringComparison.Ordinal))
            {
                var parts = key.Split(':', 3);
                if (parts.Length == 3) return DescribeTag(parts[1], parts[2]);
            }
            return null;
        }

        private static string DescribeTag(string category, string value) => category switch
        {
            "Mood" => $"a {value} mood",
            "Tone" => $"a {value} tone",
            "Theme" => $"stories about {value}",
            "Setting" => $"a {value} setting",
            "Era" => $"the {value}",
            "Franchise" => $"the {value} universe",
            _ => value, // Subgenre, VisualStyle, ContentDescriptor, Occasion, Keyword
        };

        private static string? LangName(string iso) => iso switch
        {
            "ja" => "Japanese", "fr" => "French", "es" => "Spanish", "de" => "German", "it" => "Italian",
            "ko" => "Korean", "zh" => "Chinese", "ru" => "Russian", "hi" => "Hindi", "sv" => "Swedish",
            "da" => "Danish", "pt" => "Portuguese", "en" => null, _ => null,
        };

        // ── Small numeric / string helpers ─────────────────────────────────────────────────────────

        private static double? Slider(TitleFeatures t, string name) => name switch
        {
            "Surrealism" => t.Surrealism,
            "CultClassic" => t.CultClassic,
            "Intensity" => t.Intensity,
            "Novelty" => t.Novelty,
            "Rewatchability" => t.Rewatchability,
            "Energy" => t.Energy,
            _ => null,
        };

        private static double? CriticalComposite(TitleFeatures t)
        {
            double sum = 0; int n = 0;
            if (t.ImdbRating is double ir) { sum += ir * 10; n++; }
            if (t.RtTomato is double rt) { sum += rt; n++; }
            if (t.RtPopcorn is double rp) { sum += rp; n++; }
            return n == 0 ? (double?)null : sum / n;
        }

        private static bool CompMatch(TitleFeatures c, HashSet<string> loved, out string matched)
        {
            matched = "";
            if (loved.Count == 0) return false;
            foreach (var comp in c.CompTitles)
            {
                var nm = Normalize(comp);
                if (nm.Length > 0 && loved.Contains(nm)) { matched = comp; return true; }
            }
            return false;
        }

        /// <summary>Normalize a title for comp matching: lower-case, drop punctuation and a leading article.</summary>
        public static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            var parts = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int start = parts.Length > 1 && parts[0] is "the" or "a" or "an" ? 1 : 0;
            return string.Join(' ', parts.Skip(start));
        }

        private static double Std(IReadOnlyList<double> xs, double mean)
        {
            if (xs.Count < 2) return 0;
            double s = 0;
            foreach (var x in xs) s += (x - mean) * (x - mean);
            return Math.Sqrt(s / xs.Count);
        }

        private static double WeightedMean(IReadOnlyList<double> x, IReadOnlyList<double> w)
        {
            double sw = 0, s = 0;
            for (int i = 0; i < x.Count; i++) { s += w[i] * x[i]; sw += w[i]; }
            return sw <= 0 ? 0 : s / sw;
        }

        private static double WeightedStd(IReadOnlyList<double> x, IReadOnlyList<double> w, double mean)
        {
            double sw = 0, s = 0;
            for (int i = 0; i < x.Count; i++) { s += w[i] * (x[i] - mean) * (x[i] - mean); sw += w[i]; }
            return sw <= 0 ? 0 : Math.Sqrt(s / sw);
        }

        private static double WeightedCorr(IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double> w)
        {
            double sw = 0;
            for (int i = 0; i < w.Count; i++) sw += w[i];
            if (sw <= 0) return 0;
            double mx = 0, my = 0;
            for (int i = 0; i < x.Count; i++) { mx += w[i] * x[i]; my += w[i] * y[i]; }
            mx /= sw; my /= sw;
            double cov = 0, vx = 0, vy = 0;
            for (int i = 0; i < x.Count; i++)
            {
                double dx = x[i] - mx, dy = y[i] - my;
                cov += w[i] * dx * dy; vx += w[i] * dx * dx; vy += w[i] * dy * dy;
            }
            return vx <= 0 || vy <= 0 ? 0 : cov / Math.Sqrt(vx * vy);
        }

        private static double[] MinMax(double[] v)
        {
            int n = v.Length;
            var r = new double[n];
            if (n == 0) return r;
            double mn = v.Min(), mx = v.Max(), range = mx - mn;
            if (range < 1e-9) { for (int i = 0; i < n; i++) r[i] = 0.5; return r; }
            for (int i = 0; i < n; i++) r[i] = (v[i] - mn) / range;
            return r;
        }

        private static double Norm(Dictionary<string, double> a)
        {
            double s = 0;
            foreach (var v in a.Values) s += v * v;
            return Math.Sqrt(s);
        }

        private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b, double na, double nb)
        {
            if (na <= 0 || nb <= 0) return 0;
            var (small, big) = a.Count <= b.Count ? (a, b) : (b, a);
            double dot = 0;
            foreach (var kv in small)
                if (big.TryGetValue(kv.Key, out var bv)) dot += kv.Value * bv;
            return dot / (na * nb);
        }
    }
}
