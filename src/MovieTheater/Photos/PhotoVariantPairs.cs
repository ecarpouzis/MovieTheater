using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The <see cref="PhotoDupeGroupKind.Variant"/> rules (docs/photos-plan.md §2.6): "one capture,
    /// several files BY DESIGN — RAW+JPEG, Samsung motion photos, a Live Photo's .heic+.mov, an edited
    /// export. Auto-paired by basename + capture-time; the display half is master automatically and
    /// variants never appear as timeline clutter."
    ///
    /// <para><b>Same folder, same stem.</b> A camera writes the halves of one capture side by side, so
    /// the pairing key is the DIRECTORY plus the filename stem. Matching stems across folders is how
    /// <c>IMG_0001.jpg</c> from a 2007 camera gets welded to <c>IMG_0001.mp4</c> from a 2019 phone —
    /// two unrelated captures, one silently swallowed. Capture time is the second guard, applied
    /// whenever both halves have a date; a Phase 1 video has none, which is why a missing date permits
    /// rather than forbids.</para>
    ///
    /// <para><b>Only recognized shapes pair.</b> A JPEG beside a PNG of the same name is not made a
    /// Variant here even though §2.6 mentions edited exports: two ordinary stills are exactly what the
    /// Exact and Near lanes are for, and a rule that swallowed them would auto-master one copy of a
    /// human's pair with no review at all. Variant groups get no human review, so they may only be
    /// minted where the file types themselves prove the intent.</para>
    /// </summary>
    public static class PhotoVariantPairs
    {
        /// <summary>A RAW negative beside its camera-produced JPEG.</summary>
        public const string RuleRawJpeg = "raw+jpeg";

        /// <summary>An iPhone Live Photo: the <c>.heic</c> still and its <c>.mov</c> half.</summary>
        public const string RuleLivePhoto = "live-photo";

        /// <summary>A Samsung-style motion photo delivered as two files: the still and a short video.</summary>
        public const string RuleMotionPhoto = "motion-photo";

        public static readonly IReadOnlyList<string> AllRules = new[] { RuleRawJpeg, RuleLivePhoto, RuleMotionPhoto };

        private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".srw", ".pef",
        };

        private static readonly HashSet<string> HeifExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".heic", ".heif",
        };

        public sealed class Options
        {
            /// <summary>How far apart the two halves' capture times may be before they are treated as
            /// separate captures. Generous: a phone stamps the still and the video from one shutter
            /// press, but a RAW converter can re-derive a JPEG minutes later.</summary>
            public TimeSpan TimeTolerance = TimeSpan.FromMinutes(5);

            /// <summary>Longest a motion-photo/Live-Photo video half may be. Videos carry no duration
            /// until Phase 5 runs ffprobe, so null passes — the folder+stem+time agreement is doing the
            /// work here, and this bound only ever REJECTS a full-length video that happens to share a
            /// stem with a photo.</summary>
            public double MaxMotionSeconds = 10.0;

            public HashSet<string> Rules = new HashSet<string>(AllRules, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The pairing key: the folder the file sits in, plus its stem, lower-cased.</summary>
        public static (string Folder, string Stem) Key(string relativePath)
        {
            var slash = relativePath.LastIndexOf('/');
            var folder = slash < 0 ? "" : relativePath.Substring(0, slash);
            var name = slash < 0 ? relativePath : relativePath.Substring(slash + 1);
            var dot = name.LastIndexOf('.');
            var stem = dot <= 0 ? name : name.Substring(0, dot);
            return (folder, stem.ToLowerInvariant());
        }

        public static string Extension(string relativePath)
        {
            var dot = relativePath.LastIndexOf('.');
            var slash = relativePath.LastIndexOf('/');
            return dot > slash && dot >= 0 ? relativePath.Substring(dot) : "";
        }

        /// <summary>
        /// Which rule (if any) makes these same-folder, same-stem files one capture. Returns null when
        /// they are simply two files that happen to share a name — the answer that keeps this pass from
        /// inventing pairs.
        /// </summary>
        public static string? Classify(IReadOnlyList<PhotoAsset> candidates, Options options)
        {
            if (candidates.Count < 2) return null;
            if (!TimesAgree(candidates, options.TimeTolerance)) return null;

            var stills = candidates.Where(a => a.Kind == PhotoAssetKind.Photo).ToList();
            var videos = candidates.Where(a => a.Kind == PhotoAssetKind.Video).ToList();
            if (stills.Count == 0) return null;

            if (videos.Count > 0)
            {
                // A video half must be short: the point of a motion photo is a second and a half of
                // context, and a 40-minute recording sharing a stem is a coincidence, not a capture.
                if (videos.Any(v => v.DurationSec != null && v.DurationSec > options.MaxMotionSeconds)) return null;

                var live = stills.Any(s => HeifExtensions.Contains(Extension(s.Path)));
                var rule = live ? RuleLivePhoto : RuleMotionPhoto;
                return options.Rules.Contains(rule) ? rule : null;
            }

            var raws = stills.Where(s => RawExtensions.Contains(Extension(s.Path))).ToList();
            var displays = stills.Where(s => s.OriginalRenderable).ToList();
            if (raws.Count > 0 && displays.Count > 0)
                return options.Rules.Contains(RuleRawJpeg) ? RuleRawJpeg : null;

            // Two ordinary stills. Deliberately NOT a Variant — see the class remarks.
            return null;
        }

        /// <summary>
        /// Capture times agree when every pair that HAS two dates is within tolerance. A half with no
        /// date abstains rather than vetoing: Phase 1 gives videos no timestamp at all, and a rule that
        /// demanded one would mean no motion photo is ever paired until Phase 5 ships.
        /// </summary>
        private static bool TimesAgree(IReadOnlyList<PhotoAsset> candidates, TimeSpan tolerance)
        {
            var dated = candidates.Where(a => a.TakenAt != null).Select(a => a.TakenAt!.Value).ToList();
            if (dated.Count < 2) return true;
            return (dated.Max() - dated.Min()) <= tolerance;
        }

        /// <summary>
        /// The embedded single-file case (§2.6's "the paired/embedded video half"), detected only where
        /// the metadata pass ALREADY captured the evidence — Google's <c>GCamera:MotionPhoto</c> /
        /// <c>MicroVideo</c> XMP keys, which MetadataExtractor surfaces into the persisted raw readout.
        ///
        /// <para>It cannot become a group: a group needs two rows and this is one file. It is COUNTED so
        /// the pass reports how much of the collection is motion photos that no pairing rule will ever
        /// see, and so the decision to extract them (a Phase 5 concern — it needs a video demuxer) is
        /// made against a number rather than a guess. Anything less obvious than a name match in the
        /// stored JSON is left alone: reopening files to sniff for trailers would be a NAS read per
        /// photo for a cosmetic label.</para>
        /// </summary>
        public static bool LooksLikeEmbeddedMotionPhoto(PhotoAsset asset)
        {
            var json = asset.RawMetadataJson;
            if (string.IsNullOrEmpty(json)) return false;
            return json!.IndexOf("MotionPhoto", StringComparison.OrdinalIgnoreCase) >= 0
                   || json.IndexOf("MicroVideo", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
