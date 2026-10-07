using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Requests;
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// The request queue (<c>/requests</c>, 2026-10-06): "please add this to the site." Any signed-in user
    /// may file a request, vote on another's, and withdraw their own; everyone signed in sees the whole
    /// queue. Closing a request as added or declined, and confirming or dismissing the matcher's
    /// proposals, is admin work (config admin + password-verified session — the AdminController gate).
    ///
    /// <para>Reads are per-viewer (they carry "did I vote") and never cached. The list is bounded and
    /// cursor-paged by id (newest first) per the house rule, though the queue is expected to stay small.</para>
    /// </summary>
    [Authorize]
    [Route("API/Requests")]
    public class RequestsController : Controller
    {
        private const int MaxOpenPerUser = 50;

        private readonly MovieDb db;
        private readonly MovieTheaterConfiguration config;
        private readonly ContentRequestMatcher matcher;

        public RequestsController(MovieDb db, MovieTheaterConfiguration config, ContentRequestMatcher matcher)
        {
            this.db = db;
            this.config = config;
            this.matcher = matcher;
        }

        // ── Identity ─────────────────────────────────────────────────────────────────────────────

        private int? CurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
        }

        private bool IsAdmin()
        {
            var name = User.FindFirst(ClaimTypes.Name)?.Value;
            return User.FindFirst("amr")?.Value == "pwd"
                && !string.IsNullOrEmpty(name)
                && config.AdminUsernames.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }

        // ── DTOs ─────────────────────────────────────────────────────────────────────────────────

        public sealed record RequesterDto(int UserId, string? Username);
        public sealed record MatchDto(string Kind, int Id, string? Title, DateTime? FoundUtc);
        public sealed record FulfilledDto(string Kind, int Id);

        public sealed record RequestDto(
            int Id, DateTime CreatedUtc, string Section, string Title, int? Year, string? Detail, string? Link, string? Notes,
            string Status, DateTime? ResolvedUtc, string? ResolvedBy, string? ResolutionNote,
            RequesterDto RequestedBy, int Votes, bool VotedByMe, MatchDto? Match, FulfilledDto? Fulfilled);

        public sealed class CreateRequestBody
        {
            public string? Section { get; set; }
            public string? Title { get; set; }
            public int? Year { get; set; }
            public string? Detail { get; set; }
            public string? Link { get; set; }
            public string? Notes { get; set; }
        }

        public sealed class StatusBody
        {
            public string? Status { get; set; }
            public string? Note { get; set; }
        }

        private async Task<List<RequestDto>> ToDtosAsync(List<ContentRequest> rows, int? me, CancellationToken ct)
        {
            if (rows.Count == 0) return new List<RequestDto>();
            var ids = rows.Select(r => r.Id).ToList();
            var votes = await db.ContentRequestVotes.AsNoTracking()
                .Where(v => ids.Contains(v.RequestId))
                .Select(v => new { v.RequestId, v.UserId })
                .ToListAsync(ct);
            var voteCount = votes.GroupBy(v => v.RequestId).ToDictionary(g => g.Key, g => g.Count());
            var mine = me == null ? new HashSet<int>() : votes.Where(v => v.UserId == me).Select(v => v.RequestId).ToHashSet();

            var userIds = rows.Select(r => r.RequestedByUserId).Concat(rows.Where(r => r.ResolvedByUserId != null).Select(r => r.ResolvedByUserId!.Value)).Distinct().ToList();
            var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserID)).Select(u => new { u.UserID, u.Username }).ToDictionaryAsync(u => u.UserID, u => u.Username, ct);

            return rows.Select(r => new RequestDto(
                r.Id, r.CreatedUtc, r.Section, r.Title, r.Year, r.Detail, r.Link, r.Notes,
                r.Status, r.ResolvedUtc, r.ResolvedByUserId != null && names.TryGetValue(r.ResolvedByUserId.Value, out var rb) ? rb : null, r.ResolutionNote,
                new RequesterDto(r.RequestedByUserId, names.TryGetValue(r.RequestedByUserId, out var n) ? n : null),
                voteCount.TryGetValue(r.Id, out var c) ? c : 0,
                mine.Contains(r.Id),
                r.MatchKind != null && r.MatchId != null ? new MatchDto(r.MatchKind, r.MatchId.Value, r.MatchTitle, r.MatchFoundUtc) : null,
                r.FulfilledKind != null && r.FulfilledId != null ? new FulfilledDto(r.FulfilledKind, r.FulfilledId.Value) : null
            )).ToList();
        }

        private async Task<RequestDto> OneDtoAsync(ContentRequest r, CancellationToken ct) =>
            (await ToDtosAsync(new List<ContentRequest> { r }, CurrentUserId(), ct))[0];

        // ── Reads ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The queue, newest first. <c>status</c> = <c>open</c> (default), <c>closed</c> (fulfilled +
        /// declined + withdrawn) or <c>all</c>; <c>section</c> narrows; <c>beforeId</c> + <c>limit</c> page.
        /// Envelope: <c>{ requests, totalCount, nextBeforeId }</c> — <c>nextBeforeId</c> null when done.
        /// </summary>
        [HttpGet("")]
        public async Task<IActionResult> List([FromQuery] string? status = "open", [FromQuery] string? section = null,
            [FromQuery] int? beforeId = null, [FromQuery] int limit = 200, CancellationToken ct = default)
        {
            limit = Math.Clamp(limit, 1, 500);
            var q = db.ContentRequests.AsNoTracking().AsQueryable();
            switch ((status ?? "open").ToLowerInvariant())
            {
                case "open": q = q.Where(r => r.Status == ContentRequestStatuses.Open); break;
                case "closed": q = q.Where(r => r.Status != ContentRequestStatuses.Open); break;
                case "all": break;
                default:
                    if (!ContentRequestStatuses.IsValid(status)) return BadRequest(new { message = "Unknown status filter." });
                    q = q.Where(r => r.Status == status);
                    break;
            }
            if (!string.IsNullOrEmpty(section))
            {
                if (!ContentRequestSections.IsValid(section)) return BadRequest(new { message = "Unknown section." });
                q = q.Where(r => r.Section == section);
            }
            var totalCount = await q.CountAsync(ct);
            if (beforeId != null) q = q.Where(r => r.Id < beforeId.Value);
            var rows = await q.OrderByDescending(r => r.Id).Take(limit + 1).ToListAsync(ct);
            var hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            var dtos = await ToDtosAsync(rows, CurrentUserId(), ct);
            return Ok(new { requests = dtos, totalCount, nextBeforeId = hasMore ? rows[^1].Id : (int?)null });
        }

        /// <summary>Counts for the rail and the admin's attention banner: open requests, how many of those
        /// carry an unconfirmed proposal, how many are the caller's own, and whether the caller may resolve.</summary>
        [HttpGet("Summary")]
        public async Task<IActionResult> Summary(CancellationToken ct)
        {
            var me = CurrentUserId();
            var open = await db.ContentRequests.CountAsync(r => r.Status == ContentRequestStatuses.Open, ct);
            var needsConfirmation = await db.ContentRequests.CountAsync(r => r.Status == ContentRequestStatuses.Open && r.MatchKind != null, ct);
            var mine = me == null ? 0 : await db.ContentRequests.CountAsync(r => r.RequestedByUserId == me && r.Status == ContentRequestStatuses.Open, ct);
            var bySection = await db.ContentRequests.Where(r => r.Status == ContentRequestStatuses.Open)
                .GroupBy(r => r.Section).Select(g => new { Section = g.Key, Count = g.Count() }).ToListAsync(ct);
            return Ok(new { open, needsConfirmation, mine, bySection = bySection.ToDictionary(x => x.Section, x => x.Count), canResolve = IsAdmin() });
        }

        // ── Writes: anyone signed in ─────────────────────────────────────────────────────────────

        /// <summary>
        /// File a request. A duplicate (an OPEN request in the same section whose normalized title agrees)
        /// comes back as <c>409 { duplicate }</c> so the page can offer a vote instead. The matcher runs
        /// once on the new row so "we may already have this" shows immediately.
        /// </summary>
        [HttpPost("")]
        public async Task<IActionResult> Create([FromBody] CreateRequestBody? body, CancellationToken ct)
        {
            var me = CurrentUserId();
            if (me == null) return Unauthorized();
            if (body == null) return BadRequest(new { message = "Nothing to file." });
            var section = body.Section?.Trim().ToLowerInvariant();
            if (!ContentRequestSections.IsValid(section)) return BadRequest(new { message = "Pick which part of the site this is for." });
            var title = body.Title?.Trim();
            if (string.IsNullOrEmpty(title)) return BadRequest(new { message = "A title is required." });
            if (title.Length > 200) return BadRequest(new { message = "That title is too long (200 characters)." });
            if (body.Year != null && (body.Year < 1850 || body.Year > DateTime.UtcNow.Year + 5)) return BadRequest(new { message = "That year doesn't look right." });
            var link = Clean(body.Link, 500);
            if (link != null && !(Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
                return BadRequest(new { message = "The link must be a full http(s) URL." });

            // A runaway account must not be able to fill the queue; fifty open asks is more than any
            // real person has in flight.
            var openByMe = await db.ContentRequests.CountAsync(r => r.RequestedByUserId == me && r.Status == ContentRequestStatuses.Open, ct);
            if (openByMe >= MaxOpenPerUser) return BadRequest(new { message = $"You already have {openByMe} open requests — close some first." });

            // Duplicate check: an OPEN request in the same section for the SAME title — the normalized forms
            // equal (not the matcher's loose containment, which would make "Dune" block "Dune Part Two"),
            // the years within a year when both are given, and the detail agreeing when both are given
            // ("Greatest Hits" by Queen is not "Greatest Hits" by ABBA).
            var titleNorm = ContentRequestMatcher.Normalize(title);
            var detailNorm = ContentRequestMatcher.Normalize(body.Detail);
            var openSame = await db.ContentRequests.AsNoTracking()
                .Where(r => r.Status == ContentRequestStatuses.Open && r.Section == section)
                .Select(r => new { r.Id, r.Title, r.Year, r.Detail }).ToListAsync(ct);
            var dupe = openSame.FirstOrDefault(r =>
                ContentRequestMatcher.Normalize(r.Title) == titleNorm
                && ContentRequestMatcher.YearsAgree(body.Year, r.Year)
                && (detailNorm.Length == 0 || string.IsNullOrEmpty(r.Detail) || ContentRequestMatcher.TitlesAgree(detailNorm, ContentRequestMatcher.Normalize(r.Detail))));
            if (dupe != null)
            {
                var existing = await db.ContentRequests.AsNoTracking().FirstAsync(r => r.Id == dupe.Id, ct);
                return Conflict(new { message = "Somebody already asked for that.", duplicate = await OneDtoAsync(existing, ct) });
            }

            var row = new ContentRequest
            {
                CreatedUtc = DateTime.UtcNow,
                RequestedByUserId = me.Value,
                Section = section!,
                Title = title,
                Year = body.Year,
                Detail = Clean(body.Detail, 200),
                Link = link,
                Notes = Clean(body.Notes, 1000),
                Status = ContentRequestStatuses.Open,
            };

            // Already in the library? Propose it right away — the requester sees it on their own row.
            var match = await matcher.FindAsync(row, null, ct);
            if (match != null)
            {
                row.MatchKind = match.Kind;
                row.MatchId = match.Id;
                row.MatchTitle = ContentRequestMatcher.Truncate(await matcher.DisplayTitleAsync(match, ct) + (match.Year != null ? $" ({match.Year})" : ""), 300);
                row.MatchFoundUtc = row.CreatedUtc;
            }

            db.ContentRequests.Add(row);
            await db.SaveChangesAsync(ct);
            return Ok(await OneDtoAsync(row, ct));
        }

        /// <summary>Toggle the caller's "me too" — on somebody ELSE's OPEN request. Returns the new tally.</summary>
        [HttpPost("{id:int}/Vote")]
        public async Task<IActionResult> Vote(int id, CancellationToken ct)
        {
            var me = CurrentUserId();
            if (me == null) return Unauthorized();
            var row = await db.ContentRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row == null) return NotFound();
            if (row.RequestedByUserId == me.Value) return BadRequest(new { message = "That one's yours — others can vote for it." });
            if (row.Status != ContentRequestStatuses.Open) return BadRequest(new { message = "That request is closed." });
            var existing = await db.ContentRequestVotes.FirstOrDefaultAsync(v => v.RequestId == id && v.UserId == me, ct);
            var votedByMe = existing == null;
            if (existing != null) db.ContentRequestVotes.Remove(existing);
            else db.ContentRequestVotes.Add(new ContentRequestVote { RequestId = id, UserId = me.Value, CreatedUtc = DateTime.UtcNow });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two tabs toggled at once: the unique index refused the second insert, or the row was already
                // gone. Report what is true now rather than a 500.
                db.ChangeTracker.Clear();
                votedByMe = await db.ContentRequestVotes.AnyAsync(v => v.RequestId == id && v.UserId == me, ct);
            }
            var votes = await db.ContentRequestVotes.CountAsync(v => v.RequestId == id, ct);
            return Ok(new { id, votes, votedByMe });
        }

        /// <summary>
        /// Change a request's status. Admin: <c>fulfilled</c> / <c>declined</c> / <c>open</c> (reopen).
        /// Requester: <c>withdrawn</c> on their own open request, and <c>open</c> to take a withdrawal back.
        /// </summary>
        [HttpPost("{id:int}/Status")]
        public async Task<IActionResult> SetStatus(int id, [FromBody] StatusBody? body, CancellationToken ct)
        {
            var me = CurrentUserId();
            if (me == null) return Unauthorized();
            var status = body?.Status?.Trim().ToLowerInvariant();
            if (!ContentRequestStatuses.IsValid(status)) return BadRequest(new { message = "Unknown status." });
            var row = await db.ContentRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row == null) return NotFound();

            var admin = IsAdmin();
            var owner = row.RequestedByUserId == me.Value;
            var allowed = admin
                || (owner && status == ContentRequestStatuses.Withdrawn && row.Status == ContentRequestStatuses.Open)
                || (owner && status == ContentRequestStatuses.Open && row.Status == ContentRequestStatuses.Withdrawn);
            if (!allowed) return Forbid();

            Apply(row, status!, me.Value, Clean(body!.Note, 400));
            // "Added" by hand while a proposal stands: the proposal IS the row it was added as.
            if (status == ContentRequestStatuses.Fulfilled && row.MatchKind != null && row.MatchId != null) ConsumeProposal(row);
            await db.SaveChangesAsync(ct);
            return Ok(await OneDtoAsync(row, ct));
        }

        /// <summary>Move a standing proposal into the fulfilment columns; a closed row carries no proposal.</summary>
        private static void ConsumeProposal(ContentRequest row)
        {
            row.FulfilledKind = row.MatchKind;
            row.FulfilledId = row.MatchId;
            row.MatchKind = null;
            row.MatchId = null;
            row.MatchTitle = null;
            row.MatchFoundUtc = null;
        }

        private static void Apply(ContentRequest row, string status, int by, string? note)
        {
            row.Status = status;
            if (status == ContentRequestStatuses.Open)
            {
                row.ResolvedUtc = null;
                row.ResolvedByUserId = null;
                row.ResolutionNote = null;
                row.FulfilledKind = null;
                row.FulfilledId = null;
            }
            else
            {
                row.ResolvedUtc = DateTime.UtcNow;
                row.ResolvedByUserId = by;
                row.ResolutionNote = note;
            }
        }

        // ── Writes: admin ────────────────────────────────────────────────────────────────────────

        /// <summary>The matcher was right: close the request as fulfilled WITH the proposed row.</summary>
        [HttpPost("{id:int}/Match/Confirm")]
        public async Task<IActionResult> ConfirmMatch(int id, CancellationToken ct)
        {
            if (!IsAdmin()) return Forbid();
            var row = await db.ContentRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row == null) return NotFound();
            if (row.MatchKind == null || row.MatchId == null) return BadRequest(new { message = "This request has no proposed match." });
            Apply(row, ContentRequestStatuses.Fulfilled, CurrentUserId()!.Value, "Matched: " + row.MatchTitle);
            ConsumeProposal(row);
            await db.SaveChangesAsync(ct);
            return Ok(await OneDtoAsync(row, ct));
        }

        /// <summary>The matcher was wrong: clear the proposal and remember the key so it is not re-proposed.</summary>
        [HttpPost("{id:int}/Match/Dismiss")]
        public async Task<IActionResult> DismissMatch(int id, CancellationToken ct)
        {
            if (!IsAdmin()) return Forbid();
            var row = await db.ContentRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row == null) return NotFound();
            if (row.MatchKind != null && row.MatchId != null) row.DismissedMatchKey = row.MatchKind + ":" + row.MatchId;
            row.MatchKind = null;
            row.MatchId = null;
            row.MatchTitle = null;
            row.MatchFoundUtc = null;
            await db.SaveChangesAsync(ct);
            return Ok(await OneDtoAsync(row, ct));
        }

        /// <summary>A hand pass of the matcher over the open queue — one bounded chunk per call
        /// (<c>from</c> / <c>limit</c>), the client drives it to <c>nextFrom == null</c>.</summary>
        [HttpPost("Sweep")]
        public async Task<IActionResult> Sweep([FromQuery] int from = 0, [FromQuery] int limit = 50, CancellationToken ct = default)
        {
            if (!IsAdmin()) return Forbid();
            var result = await matcher.SweepAsync(from, limit, ct);
            return Ok(new { @checked = result.Checked, proposed = result.Proposed, nextFrom = result.NextFrom, remaining = result.Remaining });
        }

        /// <summary>Remove a request outright (its votes cascade). Admin only — a requester withdraws instead,
        /// so the history keeps what was asked.</summary>
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id, CancellationToken ct)
        {
            if (!IsAdmin()) return Forbid();
            var row = await db.ContentRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row == null) return NotFound();
            db.ContentRequests.Remove(row);
            await db.SaveChangesAsync(ct);
            return Ok(new { id, deleted = true });
        }

        private static string? Clean(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
