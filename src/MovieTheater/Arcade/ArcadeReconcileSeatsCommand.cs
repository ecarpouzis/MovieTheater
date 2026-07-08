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
    /// Corrects arcade seat counts from IGDB. <c>MaxPlayers</c> was set at ingest to a per-SYSTEM blanket
    /// (SNES 5 = the multitap ceiling, N64 4, …), which over-states most games — most SNES titles are really
    /// 2-player. The IGDB enrichment stored each game's true offline-max (<c>OfflineMaxPlayers</c>); this sets
    /// every row of a card to <c>clamp(OfflineMaxPlayers, 1, currentMaxPlayers)</c> — only ever LOWERING toward
    /// the real count, never above what the core's controllers support. Cards IGDB didn't rate keep the blanket.
    ///
    /// <para>Bulk-job rules: dry-run unless <c>--apply</c>; bounded by <c>--limit</c>, resumable via
    /// <c>--after-id</c>; idempotent.</para>
    /// </summary>
    [Command("arcade-reconcile-seats", Description = "Lower MaxPlayers to IGDB's per-game offline-max where the blanket over-states it (dry-run unless --apply).")]
    public class ArcadeReconcileSeatsCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max cards this run (default 20000).")]
        public int Limit { get; set; } = 20000;

        [CommandOption("after-id", Description = "Resume cursor: cards whose min version id is greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("system", Description = "Restrict to one system code.")]
        public string System { get; set; } = "";

        [CommandOption("floor", Description = "Never lower below this many seats (default 2) — IGDB offline-max=1 is often just missing co-op data, and dropping a real 2-player game to 1P would block a partner.")]
        public int Floor { get; set; } = 2;

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeReconcileSeatsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var sys = System.Trim().ToLowerInvariant();

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);
            var rows = await db.ArcadeGames.Where(g => g.IsEnabled && (sys == "" || g.System == sys)).ToListAsync();
            var cards = rows.GroupBy(g => new { g.System, g.Title })
                .Select(grp => grp.OrderBy(x => x.Id).ToList())
                .Where(c => c[0].Id > AfterId).OrderBy(c => c[0].Id)
                .Take(Math.Max(1, Limit)).ToList();

            int changedCards = 0, changedRows = 0, lastId = AfterId;
            var moves = new Dictionary<string, int>();  // "5→2" → count
            foreach (var card in cards)
            {
                var anchor = card[0]; lastId = anchor.Id;
                if (anchor.OfflineMaxPlayers is not int off) continue;
                var cur = card.Max(r => r.MaxPlayers);
                var target = (byte)Math.Clamp(off, Math.Min(Floor, cur), cur);  // floor at 2, but never above the core ceiling
                if (target == cur) continue;

                changedCards++;
                moves[$"{cur}→{target}"] = moves.GetValueOrDefault($"{cur}→{target}") + 1;
                foreach (var r in card.Where(r => r.MaxPlayers != target))
                { if (Apply) r.MaxPlayers = target; changedRows++; }
                if (changedCards <= 12)
                    w.WriteLine($"  {cur}→{target}P [{anchor.System}] {anchor.Title}");
            }

            if (Apply) await db.SaveChangesAsync();
            var remaining = await db.ArcadeGames.CountAsync(g => g.IsEnabled && (sys == "" || g.System == sys) && g.Id > lastId);

            w.WriteLine();
            w.WriteLine("changes: " + string.Join(", ", moves.OrderByDescending(m => m.Value).Select(m => $"{m.Key}:{m.Value}")));
            w.WriteLine($"{changedCards} cards adjusted ({changedRows} rows).");
            w.WriteLine($"{{ processed: {cards.Count}, remaining: {remaining}, nextAfterId: {lastId} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
        }
    }
}
