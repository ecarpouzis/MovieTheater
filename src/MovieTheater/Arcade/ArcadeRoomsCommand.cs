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
