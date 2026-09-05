using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Web;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// The lists as READ surfaces (2026-09-04, friends' marks): anyone signed in may look at anyone's
    /// Seen / Want lists (they are communal — the card's "3 have seen it" and the people menu are built
    /// from everybody's), the title sheet's provenance lines, and "whose lists" for the switcher. All
    /// per-viewer or communal, never cached, never warmed. The WRITE side is <see cref="ViewingWrites"/>
    /// behind <c>/API/SetViewingState</c>.
    /// </summary>
    public partial class APIController
    {
        /// <summary>A password-verified session (the <c>amr=pwd</c> claim minted at login): what marking
        /// Seen on somebody ELSE's behalf requires. The passwordless communal login alone proves nothing
        /// about who is at the keyboard.</summary>
        private bool IsPasswordVerified() => User.FindFirst("amr")?.Value == "pwd";

        /// <summary>
        /// The id-array shape the SPA holds for a person's lists — <c>/API/Me</c>'s own arrays, and
        /// <c>/API/UserLists</c> for a friend's. <c>MoviesSuggested</c> is the subset of the Want list that
        /// somebody ELSE placed (a friend's Want is the suggestion), newest first, so Explore can take its head.
        /// </summary>
        public sealed record UserListsDto(int UserId, string? Username, List<int> MoviesSeen, List<int> MoviesToWatch,
            List<int> MoviesSuggested, List<int> MiscSeen, Dictionary<string, int> Ratings);

        private async Task<UserListsDto> BuildListsAsync(int ownerId, CancellationToken ct = default)
        {
            // One round-trip for all of this person's viewings; the kinds are split in memory below.
            var viewings = await movieDb.Viewings
                .Where(v => v.UserID == ownerId)
                .Select(v => new { v.ViewingID, v.ViewingType, v.MovieID, v.SeriesId, v.MiscVideoId, v.ViewingData, v.CreatedUtc, v.CreatedByUserId })
                .ToListAsync(ct);

            // Seen / Want lists carry both movie and series ids (a viewing targets one or the other; the
            // shared id space + the card's Kind disambiguate). MovieID ?? SeriesId yields the id either way.
            static IEnumerable<int> TitleIds<T>(IEnumerable<T> rows, Func<T, int?> movie, Func<T, int?> series) =>
                rows.Select(r => movie(r) ?? series(r)).Where(x => x != null).Select(x => x!.Value);

            var moviesSeen = TitleIds(viewings.Where(d => d.ViewingType == ViewingTypes.Seen), d => d.MovieID, d => d.SeriesId).ToList();
            var wantRows = viewings.Where(d => d.ViewingType == ViewingTypes.WantToWatch).ToList();
            var moviesToWatch = TitleIds(wantRows, d => d.MovieID, d => d.SeriesId).ToList();
            // Suggestions = the Want rows a friend placed, newest first (legacy rows have no CreatedUtc and
            // sort after the dated ones, by id).
            var moviesSuggested = TitleIds(
                wantRows.Where(d => d.CreatedByUserId != null && d.CreatedByUserId != ownerId)
                    .OrderByDescending(d => d.CreatedUtc ?? DateTime.MinValue).ThenByDescending(d => d.ViewingID),
                d => d.MovieID, d => d.SeriesId).ToList();

            // Watched MiscVideo ids (their own id space, so kept separate from moviesSeen). The Rate page
            // fetches their cards via GetMiscByIds.
            var miscSeen = viewings.Where(d => d.ViewingType == ViewingTypes.Seen && d.MiscVideoId != null)
                .Select(d => d.MiscVideoId!.Value).ToList();

            // 0–100 ratings, keyed by a composite "{kind}:{id}" because MiscVideo has its own id space that
            // can collide with a movie id. Non-numeric / out-of-range values are treated as unrated.
            var ratings = new Dictionary<string, int>();
            foreach (var r in viewings.Where(v => v.ViewingType == ViewingTypes.Rated && v.ViewingData != null))
            {
                if (!int.TryParse(r.ViewingData, out var score) || score < 0 || score > 100) continue;
                string? key = r.MovieID != null ? $"movie:{r.MovieID.Value}"
                            : r.SeriesId != null ? $"series:{r.SeriesId.Value}"
                            : r.MiscVideoId != null ? $"misc:{r.MiscVideoId.Value}"
                            : null;
                if (key != null) ratings[key] = score;
            }

            var username = await movieDb.Users.Where(u => u.UserID == ownerId).Select(u => u.Username).FirstOrDefaultAsync(ct);
            return new UserListsDto(ownerId, username, moviesSeen, moviesToWatch, moviesSuggested, miscSeen, ratings);
        }

        /// <summary>
        /// The list OWNER a browse acts for: the caller, or — with <c>for=&lt;username&gt;</c> — the friend
        /// whose lists the caller is browsing. Null when nobody is signed in or the name is unknown, which
        /// the `my=` leg answers with nothing. The same id goes into the cache key, so two people looking
        /// at Alex's list at the same age share one entry (communal), and nobody's own list leaks.
        /// </summary>
        private async Task<int?> ResolveListOwnerAsync(string? forUser, CancellationToken ct = default)
        {
            var caller = GetCurrentUserId();
            if (caller == null) return null;
            var name = (forUser ?? "").Trim();
            if (name.Length == 0) return caller;
            var owner = await movieDb.Users.AsNoTracking()
                .Where(u => u.Username != null && u.Username.ToLower() == name.ToLower())
                .Select(u => (int?)u.UserID).FirstOrDefaultAsync(ct);
            return owner;
        }

        /// <summary>Anyone's lists, in /API/Me's shape.</summary>
        [HttpGet("/API/UserLists")]
        public async Task<IActionResult> UserLists(string? user, CancellationToken ct = default)
        {
            var caller = GetCurrentUserId();
            if (caller == null) return Unauthorized(new { Success = false, Message = "Not logged in." });
            var owner = await ResolveListOwnerAsync(user, ct);
            if (owner == null) return NotFound(new { Success = false, Message = "No such user." });
            var lists = await BuildListsAsync(owner.Value, ct);
            return Ok(new
            {
                userId = lists.UserId, username = lists.Username,
                moviesSeen = lists.MoviesSeen, moviesToWatch = lists.MoviesToWatch,
                moviesSuggested = lists.MoviesSuggested, miscSeen = lists.MiscSeen,
            });
        }

        /// <summary>
        /// Everybody's Seen / Want lists (movie + series ids), the viewer included: what the card's "3 have
        /// seen it · 1 wants to watch" pill and the pills' people menu are built from. Communal by nature,
        /// so the SPA holds one copy for a few minutes and patches it as marks are made.
        /// </summary>
        [HttpGet("/API/PeerLists")]
        public async Task<IActionResult> PeerLists(CancellationToken ct = default)
        {
            var caller = GetCurrentUserId();
            if (caller == null) return Unauthorized(new { Success = false, Message = "Not logged in." });
            var users = await movieDb.Users.AsNoTracking()
                .Where(u => u.Username != null)
                .OrderByDescending(u => u.LastLogin.HasValue).ThenByDescending(u => u.LastLogin)
                .Select(u => new { u.UserID, u.Username, hasPassword = u.PasswordHash != null })
                .ToListAsync(ct);
            var rows = await movieDb.Viewings.AsNoTracking()
                .Where(v => (v.ViewingType == ViewingTypes.Seen || v.ViewingType == ViewingTypes.WantToWatch) && (v.MovieID != null || v.SeriesId != null))
                .Select(v => new { v.UserID, v.ViewingType, Id = v.MovieID ?? v.SeriesId })
                .ToListAsync(ct);
            var byUser = rows.GroupBy(r => r.UserID).ToDictionary(g => g.Key, g => g.ToList());
            var result = users.Select(u =>
            {
                var mine = byUser.TryGetValue(u.UserID, out var list) ? list : new();
                return new
                {
                    userId = u.UserID, username = u.Username, u.hasPassword,
                    moviesSeen = mine.Where(r => r.ViewingType == ViewingTypes.Seen).Select(r => r.Id!.Value).Distinct().ToList(),
                    moviesToWatch = mine.Where(r => r.ViewingType == ViewingTypes.WantToWatch).Select(r => r.Id!.Value).Distinct().ToList(),
                };
            }).ToList();
            return Ok(result);
        }

        /// <summary>
        /// The title sheet's provenance for one title on one person's lists: the owner's Seen / Want marks
        /// with who placed them and when (a Want placed by a friend is the suggestion), plus everyone
        /// else's marks on it. Rows older than provenance answer null dates ("before Sep 2026").
        /// </summary>
        [HttpGet("/API/ViewingDetail")]
        public async Task<IActionResult> ViewingDetail(int id, string? kind = null, string? user = null, CancellationToken ct = default)
        {
            var caller = GetCurrentUserId();
            if (caller == null) return Unauthorized(new { Success = false, Message = "Not logged in." });
            var owner = await ResolveListOwnerAsync(user, ct);
            if (owner == null) return NotFound(new { Success = false, Message = "No such user." });

            var k = ViewingWrites.NormKind(kind);
            var rows = await movieDb.Viewings.AsNoTracking()
                .Where(v => v.ViewingType == ViewingTypes.Seen || v.ViewingType == ViewingTypes.WantToWatch)
                .Where(ViewingWrites.TitleIs(k, id))
                .Select(v => new { v.UserID, v.ViewingType, v.CreatedUtc, v.CreatedByUserId })
                .ToListAsync(ct);

            var ids = rows.Select(r => r.UserID).Concat(rows.Where(r => r.CreatedByUserId != null).Select(r => r.CreatedByUserId!.Value)).Distinct().ToList();
            var names = ids.Count == 0 ? new Dictionary<int, string?>()
                : await movieDb.Users.AsNoTracking().Where(u => ids.Contains(u.UserID)).ToDictionaryAsync(u => u.UserID, u => u.Username, ct);
            string? Name(int? uid) => uid is int u && names.TryGetValue(u, out var n) ? n : null;

            object? Mark(string type)
            {
                var r = rows.FirstOrDefault(x => x.UserID == owner.Value && x.ViewingType == type);
                return r == null ? null : new { atUtc = r.CreatedUtc, byUserId = r.CreatedByUserId, byUsername = Name(r.CreatedByUserId) };
            }

            var others = rows.Where(x => x.UserID != owner.Value)
                .GroupBy(x => x.UserID)
                .Select(g => new
                {
                    userId = g.Key, username = Name(g.Key),
                    seen = g.Any(x => x.ViewingType == ViewingTypes.Seen),
                    want = g.Any(x => x.ViewingType == ViewingTypes.WantToWatch),
                })
                .OrderBy(x => x.username)
                .ToList();

            return Ok(new { userId = owner.Value, username = Name(owner.Value), seen = Mark(ViewingTypes.Seen), want = Mark(ViewingTypes.WantToWatch), others });
        }
    }
}
