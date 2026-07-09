using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.LaunchBox;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Corrects arcade player counts from the LaunchBox dump — the PRIMARY seat source.
    ///
    /// <para><c>ArcadeGame.MaxPlayers</c> was never a per-game fact: <c>ArcadeIngestCommand</c> sets it to a
    /// per-SYSTEM blanket (the core's controller-port ceiling — PS2 2, N64 4, SNES 5 for the multitap). So
    /// Shadow of the Colossus advertised "2P" purely for being a PS2 game, and 13,968 of 17,291 cards claimed
    /// 2P+, which made the lobby's Players filter meaningless.</para>
    ///
    /// <para>This sets each card to <c>clamp(launchbox, 1, currentMaxPlayers)</c> — only ever LOWERING toward
    /// the real count, never above what the core's controller ports support. A card LaunchBox doesn't know
    /// keeps its blanket: <b>absent data means "leave it alone", never "single player"</b>. That rule is what
    /// saves GoldenEye 007, which has no LaunchBox player count and would be wrongly demoted to 1P by IGDB's
    /// <c>game_modes</c> (it records that four-player split-screen landmark as "Single player").</para>
    ///
    /// <para>This supersedes <see cref="ArcadeReconcileSeatsCommand"/>, whose IGDB source is NULL for 90% of
    /// cards. Run that afterwards to catch the handful LaunchBox misses but IGDB knows.</para>
    ///
    /// <para>Bulk-job rules: dry-run unless <c>--apply</c>; bounded by <c>--limit</c>, resumable via
    /// <c>--after-id</c>; idempotent (a second run finds nothing left to change).</para>
    /// </summary>
    [Command("arcade-launchbox-seats", Description = "Lower MaxPlayers to LaunchBox's per-game player count where the per-system blanket over-states it (dry-run unless --apply).")]
    public class ArcadeLaunchBoxSeatsCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max cards to process this run (default 2000).")]
        public int Limit { get; set; } = 2000;

        [CommandOption("after-id", Description = "Resume cursor: only cards whose min version id is greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("system", Description = "Restrict to one system code (e.g. ps2, n64).")]
        public string System { get; set; } = "";

        [CommandOption("zip", Description = "Path to a Metadata.zip (default data/launchbox/Metadata.zip; downloaded if absent).")]
        public string Zip { get; set; } = "data/launchbox/Metadata.zip";

        [CommandOption("refresh", Description = "Re-download the dump even if cached.")]
        public bool Refresh { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeLaunchBoxSeatsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-launchbox-seats/1.0");

            var zipPath = await LaunchBoxMetadata.EnsureDumpAsync(http, Zip, Refresh, w.WriteLine);
            var seats = LaunchBoxMetadata.BuildSeatIndex(zipPath, w.WriteLine);

            var sys = System.Trim().ToLowerInvariant();
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);

            var rows = await db.ArcadeGames.Where(g => g.IsEnabled && (sys == "" || g.System == sys)).ToListAsync();
            var cards = rows.GroupBy(g => new { g.System, g.Title })
                .Select(grp => grp.OrderBy(x => x.Id).ToList())
                .Where(c => c[0].Id > AfterId).OrderBy(c => c[0].Id)
                .Take(Math.Max(1, Limit)).ToList();

            int matched = 0, unmatched = 0, changedCards = 0, changedRows = 0, lastId = AfterId;
            var moves = new Dictionary<string, int>();
            var samples = new List<string>();

            foreach (var card in cards)
            {
                var anchor = card[0];
                lastId = anchor.Id;

                // Our romset titles carry dual names ("Red Earth / War-Zard"); try each side.
                LaunchBoxMetadata.Seats? hit = null;
                foreach (var key in LaunchBoxMetadata.TitleKeys(anchor.Title))
                    if (seats.TryGetValue((anchor.System, key), out var s)) { hit = s; break; }

                if (hit is not LaunchBoxMetadata.Seats lb) { unmatched++; continue; }
                matched++;

                var cur = card.Max(r => r.MaxPlayers);
                // Never raise: the core's controller ports are a hard ceiling, whatever LaunchBox says a
                // game supported on original hardware (multitaps, link cables, four-score adapters).
                var target = (byte)Math.Clamp(lb.MaxPlayers, 1, cur);
                if (target == cur) continue;

                changedCards++;
                var move = $"{cur}→{target}";
                moves[move] = moves.GetValueOrDefault(move) + 1;
                foreach (var r in card.Where(r => r.MaxPlayers != target))
                {
                    if (Apply) r.MaxPlayers = target;
                    changedRows++;
                }
                if (samples.Count < 15)
                    samples.Add($"  {cur}→{target}P [{anchor.System}] {anchor.Title}{(lb.Cooperative ? " (co-op)" : "")}");
            }

            if (Apply) await db.SaveChangesAsync();

            var remaining = await db.ArcadeGames.CountAsync(g => g.IsEnabled && (sys == "" || g.System == sys) && g.Id > lastId);

            foreach (var s in samples) w.WriteLine(s);
            w.WriteLine();
            w.WriteLine($"matched {matched:N0} cards in LaunchBox, {unmatched:N0} with no player count (blanket kept).");
            w.WriteLine("changes: " + (moves.Count == 0 ? "none" : string.Join(", ", moves.OrderByDescending(m => m.Value).Select(m => $"{m.Key}:{m.Value}"))));
            w.WriteLine($"{changedCards:N0} cards adjusted ({changedRows:N0} rows).");
            w.WriteLine($"{{ processed: {cards.Count}, remaining: {remaining}, nextAfterId: {lastId} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
        }
    }
}
