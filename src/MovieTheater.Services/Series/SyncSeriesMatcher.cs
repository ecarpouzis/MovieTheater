using System.Collections.Generic;
using System.Linq;
using MovieTheater.Db;

namespace MovieTheater.Services.Series
{
    /// <summary>
    /// The two numbering decisions in mapping a folder of episode files to a catalogued episode list.
    /// Pure functions over the two lists, held apart from the controller because they are the part
    /// that can be silently, invisibly wrong — and therefore the part that has to be tested.
    /// </summary>
    public static class SyncSeriesMatcher
    {
        /// <summary>
        /// Whether the seasons on disk have the same SHAPE as the catalogued list — for every season
        /// the files touch, the highest episode number on disk must exist in that season. Returns the
        /// disagreement in words, or null when the shapes agree.
        ///
        /// <para>This is the guard against the quietest possible corruption. Nick Arcade ships as
        /// 43 + 41 files while TMDB splits the same 84 episodes 42 + 42, so every season-2 file would
        /// map one episode early: each lookup "succeeds", nothing errors, and 41 episodes are silently
        /// wrong. Numbers alone cannot say which numbering is right, so a disagreement stops the
        /// mapping instead of resolving it.</para>
        /// </summary>
        public static string? SeasonShapeMismatch(
            IEnumerable<SyncCandidate> members, IReadOnlyCollection<Episode> episodes)
        {
            foreach (var g in members
                         .Where(c => c.SeasonNumber != null && c.EpisodeNumber != null)
                         .GroupBy(c => c.SeasonNumber!.Value)
                         .OrderBy(g => g.Key))
            {
                var diskMax = g.Max(c => c.EpisodeNumber!.Value);
                var inSeason = episodes.Where(e => e.SeasonNumber == g.Key).ToList();
                if (inSeason.Count == 0)
                    return $"season {g.Key} is not in the catalogued episode list at all";
                var catMax = inSeason.Max(e => e.EpisodeNumber);
                if (diskMax > catMax)
                    return $"season {g.Key} runs to E{diskMax} on disk but the catalogue stops at E{catMax}";
            }
            return null;
        }

        /// <summary>
        /// Pairs the nth file with the nth catalogued episode — the reviewer's explicit answer when
        /// the disk and the catalogue hold the same episodes but split them into seasons differently.
        /// Returns null unless the counts are exactly 1:1, because a partial zip has no defined
        /// meaning: one missing file would shift every pair after it.
        /// </summary>
        public static Dictionary<int, Episode>? AbsolutePairing(
            IEnumerable<SyncCandidate> members, IReadOnlyCollection<Episode> episodes)
        {
            var orderedFiles = members
                .Where(c => c.SeasonNumber != null && c.EpisodeNumber != null)
                .OrderBy(c => c.SeasonNumber!.Value).ThenBy(c => c.EpisodeNumber!.Value)
                .ToList();
            var ordered = episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
            if (orderedFiles.Count == 0 || orderedFiles.Count != ordered.Count) return null;

            var map = new Dictionary<int, Episode>(orderedFiles.Count);
            for (int i = 0; i < orderedFiles.Count; i++) map[orderedFiles[i].Id] = ordered[i];
            return map;
        }
    }
}
