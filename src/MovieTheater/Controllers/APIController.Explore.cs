using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// The two reads the Movies Explore tab (R9 S7) needs and the site did not have. Everything else
    /// on that landing is composed IN THE BROWSER out of endpoints that already existed
    /// (`/API/Browse`, `/API/BrowseGroups`, `/API/GetFranchiseRail`, the channel lineup); these two
    /// are here because the data — a resume position, a personal recommendation — has never had a
    /// read route at all.
    ///
    /// Both are read-only, per-viewer (so never cached and never warmed), capped, and age-gated
    /// through the same base queries the browse uses.
    /// </summary>
    public partial class APIController
    {
        private const int ExploreMaxTake = 24;
        /// <summary>Past this much of a title, "continue watching" is really "watch again" — drop it.</summary>
        private const double ResumeCeiling = 0.95;
        /// <summary>Below this, the viewer barely started; a stray 30 seconds is not a resume.</summary>
        private const double ResumeFloor = 0.01;

        /// <summary>
        /// GET /API/ContinueWatching — the viewer's own unfinished titles, most recently played first.
        /// A progress row hangs off a <see cref="Playable"/>, which is either a movie or an EPISODE, so
        /// an episode resolves to the card of the series it belongs to (the row you would actually
        /// click to keep going).
        /// </summary>
        [HttpGet("/API/ContinueWatching")]
        public async Task<IActionResult> ContinueWatchingAsync(int take = 12, CancellationToken ct = default)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Ok(new { items = Array.Empty<object>() });
            take = Math.Clamp(take, 1, ExploreMaxTake);

            // Over-read: some rows will be gated out or resolve to nothing, and a short rail is better
            // than a second round trip. Bounded at 4x the ask, never the whole table.
            var rows = await movieDb.MoviePlaybackProgresses
                .Where(p => p.UserID == userId.Value && !p.Completed && p.DurationTicks > 0 && p.PositionTicks > 0)
                .OrderByDescending(p => p.UpdatedUtc)
                .Take(take * 4)
                .Select(p => new { p.PlayableId, p.PositionTicks, p.DurationTicks, p.UpdatedUtc })
                .ToListAsync(ct);
            var live = rows
                .Select(r => new { r.PlayableId, r.UpdatedUtc, Fraction = (double)r.PositionTicks / r.DurationTicks })
                .Where(r => r.Fraction is > ResumeFloor and < ResumeCeiling)
                .ToList();
            if (live.Count == 0) return Ok(new { items = Array.Empty<object>() });

            var playableIds = live.Select(r => r.PlayableId).Distinct().ToList();
            var movieByPlayable = await movieDb.Movies
                .Where(m => m.PlayableId != null && playableIds.Contains(m.PlayableId.Value))
                .Select(m => new { Playable = m.PlayableId!.Value, m.id })
                .ToListAsync(ct);
            var seriesByPlayable = await movieDb.Episodes
                .Where(e => e.PlayableId != null && e.SeriesId != null && playableIds.Contains(e.PlayableId.Value))
                .Select(e => new { Playable = e.PlayableId!.Value, SeriesId = e.SeriesId!.Value, e.SeasonNumber, e.EpisodeNumber, e.Title })
                .ToListAsync(ct);

            // Cards come out of the GATED base queries, so an age-restricted viewer never gets a
            // resume tile for something they cannot open.
            var movieIds = movieByPlayable.Select(x => x.id).Distinct().ToList();
            var seriesIds = seriesByPlayable.Select(x => x.SeriesId).Distinct().ToList();
            var movieCards = (await (await GetBaseMovieQuery()).Where(m => movieIds.Contains(m.id)).Select(ToCardDto).ToListAsync(ct))
                .ToDictionary(c => c.id);
            var seriesCards = (await (await GetBaseSeriesQuery()).Where(s => seriesIds.Contains(s.Id)).Select(ToSeriesCardDto).ToListAsync(ct))
                .ToDictionary(c => c.id);
            var movieOf = movieByPlayable.ToDictionary(x => x.Playable, x => x.id);
            var episodeOf = seriesByPlayable.GroupBy(x => x.Playable).ToDictionary(g => g.Key, g => g.First());

            var items = new List<object>();
            var seen = new HashSet<string>();
            foreach (var r in live)
            {
                MovieCardDto? card = null;
                string? note = null;
                if (movieOf.TryGetValue(r.PlayableId, out var mid)) movieCards.TryGetValue(mid, out card);
                else if (episodeOf.TryGetValue(r.PlayableId, out var ep) && seriesCards.TryGetValue(ep.SeriesId, out var sc))
                {
                    card = sc;
                    note = $"S{ep.SeasonNumber}E{ep.EpisodeNumber}" + (string.IsNullOrWhiteSpace(ep.Title) ? "" : $" · {ep.Title}");
                }
                if (card == null) continue;
                // One tile per TITLE: three episodes of the same series is one "keep watching" card.
                if (!seen.Add($"{card.Kind}:{card.id}")) continue;
                items.Add(new { card, percent = (int)Math.Round(r.Fraction * 100), lastPlayedUtc = r.UpdatedUtc, note });
                if (items.Count >= take) break;
            }
            return Ok(new { items });
        }

        /// <summary>
        /// GET /API/Recommendations — the viewer's own "For You" ranking (the rows
        /// <c>RecommendationMaintenanceService</c> keeps fresh), best pick first, age-gated. Empty for
        /// a signed-out viewer and for anyone the engine has not scored yet, which the rail draws as
        /// "no rail" rather than an empty shelf.
        /// </summary>
        [HttpGet("/API/Recommendations")]
        public async Task<IActionResult> RecommendationsAsync(int take = 18, CancellationToken ct = default)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Ok(new { items = Array.Empty<object>() });
            take = Math.Clamp(take, 1, ExploreMaxTake * 2);

            var rows = await movieDb.TitleRecommendations
                .Where(r => r.UserId == userId.Value)
                .OrderBy(r => r.Rank)
                .Take(take * 2)
                .Select(r => new { r.SubjectKind, r.SubjectId, r.Score, r.Rank, r.ReasonText })
                .ToListAsync(ct);
            if (rows.Count == 0) return Ok(new { items = Array.Empty<object>() });

            var movieIds = rows.Where(r => r.SubjectKind == InsightSubjectKind.Movie).Select(r => r.SubjectId).Distinct().ToList();
            var seriesIds = rows.Where(r => r.SubjectKind == InsightSubjectKind.Series).Select(r => r.SubjectId).Distinct().ToList();
            var movieCards = (await (await GetBaseMovieQuery()).Where(m => movieIds.Contains(m.id)).Select(ToCardDto).ToListAsync(ct))
                .ToDictionary(c => c.id);
            var seriesCards = (await (await GetBaseSeriesQuery()).Where(s => seriesIds.Contains(s.Id)).Select(ToSeriesCardDto).ToListAsync(ct))
                .ToDictionary(c => c.id);

            var items = new List<object>();
            foreach (var r in rows)
            {
                MovieCardDto? card = null;
                if (r.SubjectKind == InsightSubjectKind.Movie) movieCards.TryGetValue(r.SubjectId, out card);
                else seriesCards.TryGetValue(r.SubjectId, out card);
                if (card == null) continue;
                items.Add(new { card, score = (int)Math.Round(r.Score), reason = r.ReasonText });
                if (items.Count >= take) break;
            }
            return Ok(new { items });
        }
    }
}
