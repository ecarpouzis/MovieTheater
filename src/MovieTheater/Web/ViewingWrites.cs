using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Models;

namespace MovieTheater.Web
{
    /// <summary>
    /// Every write to the Seen / Want lists, in one testable place (the controller is a shell around
    /// <see cref="ApplyAsync"/>). Pure over <see cref="MovieDb"/>, like <see cref="BrowseFilter"/>, so it
    /// runs against SQLite in the tests as written.
    ///
    /// <para>The rules, in one place:</para>
    /// <list type="bullet">
    /// <item>A mark's existence IS its state: one row per (owner, title, type).</item>
    /// <item>A Want placed on somebody ELSE's list is a suggestion — anyone signed in may place one, and
    /// the row remembers who did (<see cref="Viewing.CreatedByUserId"/>). Marking Seen on somebody else's
    /// behalf needs a password-verified session (the <c>amr=pwd</c> claim): the passwordless communal
    /// login could otherwise rewrite a password-protected user's history.</item>
    /// <item>Un-marking is the same call with <c>on = false</c>, by the owner or by the placer alike — a
    /// friend may withdraw the suggestion they made; the owner may say "not interested".</item>
    /// <item>Every create and delete writes a <see cref="ViewingEvent"/>; the row stamps
    /// <see cref="Viewing.CreatedUtc"/> / <see cref="Viewing.CreatedByUserId"/> on create.</item>
    /// </list>
    /// </summary>
    public static class ViewingWrites
    {
        /// <summary>Status = the HTTP status the controller answers with.</summary>
        public sealed record Result(int Status, string? Message)
        {
            public bool Success => Status == 200;
            public static Result Ok() => new(200, null);
            public static Result Fail(int status, string message) => new(status, message);
        }

        public static string NormKind(string? k) =>
            string.Equals(k, "series", StringComparison.OrdinalIgnoreCase) ? "series"
            : string.Equals(k, "misc", StringComparison.OrdinalIgnoreCase) ? "misc"
            : "movie";

        /// <summary>The Viewing rows of one title, in whichever id space the kind names.</summary>
        public static Expression<Func<Viewing, bool>> TitleIs(string kind, int id) => kind switch
        {
            "series" => v => v.SeriesId == id,
            "misc" => v => v.MiscVideoId == id,
            _ => v => v.MovieID == id,
        };

        public static async Task<Result> ApplyAsync(MovieDb db, int actorId, bool actorIsPasswordVerified, int? forUserId,
            string? kindRaw, int id, ViewingType action, bool on, DateTime nowUtc, CancellationToken ct = default)
        {
            var kind = NormKind(kindRaw);
            var targetId = forUserId ?? actorId;
            var onBehalf = targetId != actorId;

            if (onBehalf && action == ViewingType.SetWatched && !actorIsPasswordVerified)
                return Result.Fail(403, "Marking Seen on someone else's behalf needs a password-verified session.");
            if (onBehalf && !await db.Users.AnyAsync(u => u.UserID == targetId, ct))
                return Result.Fail(400, "No such user.");

            bool titleExists = kind switch
            {
                "series" => await db.Series.AnyAsync(s => s.Id == id, ct),
                "misc" => await db.MiscVideos.AnyAsync(mv => mv.Id == id, ct),
                _ => await db.Movies.AnyAsync(m => m.id == id, ct),
            };
            if (!titleExists)
                return Result.Fail(400, kind == "series" ? "Invalid Series ID." : kind == "misc" ? "Invalid MiscVideo ID." : "Invalid Movie ID.");

            var type = action == ViewingType.SetWatched ? ViewingTypes.Seen : ViewingTypes.WantToWatch;
            var existing = await db.Viewings.Where(v => v.UserID == targetId && v.ViewingType == type).Where(TitleIs(kind, id)).FirstOrDefaultAsync(ct);
            if (existing == null && on)
            {
                db.Viewings.Add(new Viewing
                {
                    MovieID = kind == "movie" ? id : null,
                    SeriesId = kind == "series" ? id : null,
                    MiscVideoId = kind == "misc" ? id : null,
                    UserID = targetId,
                    ViewingType = type,
                    CreatedUtc = nowUtc,
                    CreatedByUserId = actorId,
                });
                db.ViewingEvents.Add(Event(targetId, actorId, kind, id, type, ViewingEvent.ActionAdded, null, nowUtc));
            }
            else if (existing != null && !on)
            {
                db.Viewings.Remove(existing);
                db.ViewingEvents.Add(Event(targetId, actorId, kind, id, type, ViewingEvent.ActionRemoved, null, nowUtc));
            }
            await db.SaveChangesAsync(ct);
            return Result.Ok();
        }

        /// <summary>One journal row. Shared with the Rate page's upserts (Added / Removed / Rescored).</summary>
        public static ViewingEvent Event(int userId, int? actorId, string kind, int id, string viewingType, string action, string? data, DateTime atUtc, string source = ViewingEvent.SourceWeb) => new()
        {
            UserId = userId,
            ActorUserId = actorId,
            MovieID = kind == "movie" ? id : null,
            SeriesId = kind == "series" ? id : null,
            MiscVideoId = kind == "misc" ? id : null,
            ViewingType = viewingType,
            Action = action,
            Data = data,
            AtUtc = atUtc,
            Source = source,
        };
    }
}
