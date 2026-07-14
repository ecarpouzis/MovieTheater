using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Admin view of arcade rooms from the durable log (arcade-plan.md §6). A CLI runs in its own process,
    /// so it can't see the web app's in-memory <c>ArcadeRoomService</c> — it reports from
    /// <c>ArcadeSession</c> instead (rows with no <c>EndedUtc</c> are the live records). <c>--kill</c>
    /// stamps a room ended in the log; the ACTUAL emulator teardown is the C4/v2 server-kill channel —
    /// until then a wedged room dies when its players disconnect or via a <c>docker restart</c> of the
    /// worker (see the docker/arcade README).
    /// </summary>
    [Command("arcade-rooms", Description = "List arcade rooms from the durable log; --kill <code> ends a room record.")]
    public class ArcadeRoomsCommand : BasicDICommand, ICommand
    {
        [CommandOption("all", Description = "Include ended rooms, not just live ones.")]
        public bool All { get; set; }

        [CommandOption("kill", Description = "Mark the room with this code ended (stamps EndedUtc).")]
        public string? Kill { get; set; }

        [CommandOption("close-stale", Description = "Close ghost rows: rooms still marked live whose last heartbeat is older than --stale-minutes. DRY RUN unless --apply.")]
        public bool CloseStale { get; set; }

        [CommandOption("apply", Description = "With --close-stale: actually stamp EndedUtc (default is a dry run that only reports).")]
        public bool Apply { get; set; }

        [CommandOption("stale-minutes", Description = "With --close-stale: how quiet a room must be to count as dead. Default 60.")]
        public int StaleMinutes { get; set; } = 60;

        [CommandOption("limit", Description = "With --close-stale: max rows per run (chunked; re-run to continue). Default 200.")]
        public int Limit { get; set; } = 200;

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeRoomsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            if (!string.IsNullOrWhiteSpace(Kill))
            {
                var room = await db.ArcadeSessions.FirstOrDefaultAsync(s => s.RoomCode == Kill && s.EndedUtc == null);
                if (room == null) { w.WriteLine($"No live room with code {Kill}."); return; }
                room.EndedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
                w.WriteLine($"Room {Kill} marked ended. (The emulator frees when its players disconnect or the worker is restarted.)");
                return;
            }

            if (CloseStale)
            {
                // Ghost rows: "live" forever because the pod that knew about them restarted (the reaper's
                // old prompt-only pass could not see them). This CLI is a separate process with NO view of
                // the web app's in-memory registry, so it must not judge liveness aggressively — the stale
                // window defaults to a full hour, far beyond the reaper's own 5 min, so a room someone is
                // ACTUALLY playing right now (heartbeating every ~30 s into LastSeenUtc) can never be hit.
                // Chunked + idempotent per the bulk-jobs rule: bounded --limit per run, re-run to continue.
                var now = DateTime.UtcNow;
                var cutoff = now.AddMinutes(-Math.Max(1, StaleMinutes));
                var q0 = db.ArcadeSessions
                    .Where(s => s.EndedUtc == null && (s.LastSeenUtc ?? s.CreatedUtc) < cutoff);

                var total = await q0.CountAsync();
                var batch = await q0.OrderBy(s => s.CreatedUtc).Take(Math.Max(1, Limit)).ToListAsync();
                if (total == 0) { w.WriteLine("No stale room rows. Nothing to close."); return; }

                var ids = batch.Select(r => r.ArcadeGameId).Distinct().ToList();
                var gameTitles = await db.ArcadeGames.Where(g => ids.Contains(g.Id))
                    .ToDictionaryAsync(g => g.Id, g => g.Title);

                w.WriteLine($"{(Apply ? "CLOSING" : "DRY RUN — would close")} {batch.Count} of {total} stale row(s) " +
                            $"(no heartbeat since {cutoff:yyyy-MM-dd HH:mm} UTC):\n");
                w.WriteLine($"{"CODE",-8} {"GAME",-28} {"CREATED (UTC)",-18} LAST SEEN (UTC)");
                foreach (var r in batch)
                {
                    var title = Trunc(gameTitles.GetValueOrDefault(r.ArcadeGameId) ?? $"#{r.ArcadeGameId}", 28);
                    var seen = r.LastSeenUtc?.ToString("yyyy-MM-dd HH:mm") ?? "never";
                    w.WriteLine($"{r.RoomCode,-8} {title,-28} {r.CreatedUtc,-18:yyyy-MM-dd HH:mm} {seen}");
                }

                if (!Apply)
                {
                    w.WriteLine($"\nDry run — nothing written. Re-run with --apply to close these {batch.Count}.");
                    return;
                }

                foreach (var r in batch)
                    r.EndedUtc = now;
                await db.SaveChangesAsync();
                var remaining = total - batch.Count;
                w.WriteLine($"\nClosed {batch.Count}. {remaining} stale row(s) remain" +
                            (remaining > 0 ? " — re-run to continue." : "."));
                return;
            }

            var q = db.ArcadeSessions.AsQueryable();
            if (!All) q = q.Where(s => s.EndedUtc == null);
            var rooms = await q.OrderByDescending(s => s.CreatedUtc).Take(200).ToListAsync();
            if (rooms.Count == 0) { w.WriteLine(All ? "No rooms on record." : "No live rooms."); return; }

            var gameIds = rooms.Select(r => r.ArcadeGameId).Distinct().ToList();
            var titles = await db.ArcadeGames.Where(g => gameIds.Contains(g.Id))
                .ToDictionaryAsync(g => g.Id, g => g.Title);
            var creatorIds = rooms.Select(r => r.CreatedByUserId).Distinct().ToList();
            var users = await db.Users.Where(u => creatorIds.Contains(u.UserID))
                .ToDictionaryAsync(u => u.UserID, u => u.Username);

            w.WriteLine($"{"CODE",-8} {"GAME",-28} {"BY",-14} {"BOUND",-6} {"CREATED (UTC)",-20} STATE");
            foreach (var r in rooms)
            {
                var title = titles.GetValueOrDefault(r.ArcadeGameId) ?? $"#{r.ArcadeGameId}";
                var by = users.GetValueOrDefault(r.CreatedByUserId) ?? $"#{r.CreatedByUserId}";
                var bound = r.CloudRetroRoomId != null ? "yes" : "no";
                var state = r.EndedUtc == null ? "live" : $"ended {r.EndedUtc:HH:mm}";
                w.WriteLine($"{r.RoomCode,-8} {Trunc(title, 28),-28} {Trunc(by, 14),-14} {bound,-6} {r.CreatedUtc,-20:yyyy-MM-dd HH:mm} {state}");
            }
            w.WriteLine($"\n{rooms.Count} room(s){(All ? "" : " live")}.");
        }

        private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    }
}
