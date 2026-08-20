using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Takes back a box cover the art cascade got wrong.
    ///
    /// <para><b>Why this needs a command at all.</b> The posters mount is shared and the app cannot delete
    /// from it, and <c>/ArcadeImage</c> serves a cached <c>{cardId}.png</c> at step 1 — before it ever
    /// re-searches. So nulling <see cref="ArcadeGame.BoxArtPath"/> does nothing: the stale file is still
    /// sitting at the path the route probes next, and it wins forever. The only lever that works without
    /// mount access is to change the filename the route ASKS for, which is what
    /// <see cref="ArcadeGame.BoxArtGeneration"/> does — the bad file stays on disk, orphaned and unreachable,
    /// and the cascade runs again into <c>{cardId}-g{n}.png</c>.</para>
    ///
    /// <para><b>Evict vs block.</b> Eviction alone says "try again". That is right when a better source
    /// exists — a corrected <see cref="ArcadeGame.BoxArtSourceUrl"/>, or a cascade fix that will now match
    /// properly. It is WRONG when the sources simply have nothing for this card (most obscure demo discs and
    /// magazine sampler discs), because the cascade will re-fetch the same wrong cover and you evict forever.
    /// <c>--block</c> is the terminal state for those: placeholder, no network, until someone clears it with
    /// <c>--unblock</c>.</para>
    ///
    /// <para>Dry-run unless <c>--apply</c>; bounded by <c>--limit</c>; idempotent per generation. Every
    /// evicted row keeps a <c>boxart-evict</c> breadcrumb in <see cref="ArcadeGame.Notes"/> naming the file
    /// that was retired and why, so a cover that was judged wrong can never quietly come back as evidence
    /// that it was fine.</para>
    /// </summary>
    [Command("arcade-boxart-evict", Description = "Retire a card's wrong box cover (renames what the route looks for; the mount is append-only). Dry-run unless --apply.")]
    public class ArcadeBoxArtEvictCommand : BasicDICommand, ICommand
    {
        [CommandOption("ids", Description = "Card ids (any row of the card) to evict, comma-separated.")]
        public string Ids { get; set; } = "";

        [CommandOption("from", Description = "Read ids from this file instead (one per line; blank lines and # comments ignored; anything after whitespace on a line is treated as a comment).")]
        public string From { get; set; } = "";

        [CommandOption("reason", Description = "Why — recorded in Notes (e.g. \"wrong game: matched SGDB 'Super'\").")]
        public string Reason { get; set; } = "";

        [CommandOption("block", Description = "Also mark the card unsourceable: placeholder, and the cascade never runs for it again.")]
        public bool Block { get; set; }

        [CommandOption("unblock", Description = "Clear the blocked flag instead (re-opens the search). Does not change the generation.")]
        public bool Unblock { get; set; }

        [CommandOption("apply", Description = "Write. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max cards to act on this run (default 500).")]
        public int Limit { get; set; } = 500;

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeBoxArtEvictCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (Block && Unblock) throw new CommandException("--block and --unblock are opposites; pass one.");

            var ids = ParseIds(Ids).ToHashSet();
            if (From.Length > 0)
                foreach (var raw in File.ReadLines(RepoDataPath.Resolve(From)))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    var head = line.Split(new[] { ' ', '\t' }, 2)[0];
                    if (int.TryParse(head, out var n)) ids.Add(n);
                }
            if (ids.Count == 0) throw new CommandException("Nothing to do — pass --ids or --from.");

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(300);

            // An id may name any row of a card; eviction is a CARD-level act, so resolve to the whole group.
            var named = await db.ArcadeGames.Where(g => ids.Contains(g.Id))
                                .Select(g => new { g.System, g.CollapseKey }).Distinct().ToListAsync();
            var systems = named.Select(k => k.System).Distinct().ToList();
            var keys = named.Select(k => k.CollapseKey).Distinct().ToList();
            var rows = await db.ArcadeGames.Where(g => systems.Contains(g.System) && keys.Contains(g.CollapseKey))
                                           .ToListAsync();
            var wanted = named.Select(k => (k.System, k.CollapseKey)).ToHashSet();
            var cards = rows.Where(g => wanted.Contains((g.System, g.CollapseKey)))
                            .GroupBy(g => (g.System, g.CollapseKey))
                            .OrderBy(grp => grp.Min(x => x.Id))
                            .Take(Math.Max(1, Limit))
                            .ToList();

            var missing = ids.Count - rows.Count(g => ids.Contains(g.Id));
            if (missing > 0) w.WriteLine($"{missing} id(s) matched no enabled row and were skipped.");

            var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd");
            int changed = 0, blocked = 0, unblocked = 0, cleared = 0;
            foreach (var grp in cards)
            {
                var group = grp.OrderBy(g => g.Id).ToList();
                var cardId = group[0].Id;
                var gen = group.Max(g => g.BoxArtGeneration);
                var retired = group.Select(g => g.BoxArtPath).FirstOrDefault(p => p != null)
                              ?? (gen > 0 ? $"arcade/{grp.Key.System}/{cardId}-g{gen}.png"
                                          : $"arcade/{grp.Key.System}/{cardId}.png");

                if (Unblock)
                {
                    w.WriteLine($"  unblock [{grp.Key.System}] {cardId} \"{group[0].Title}\"");
                    if (Apply) foreach (var g in group) g.BoxArtBlocked = false;
                    unblocked++;
                    continue;
                }

                w.WriteLine($"  evict [{grp.Key.System}] {cardId} \"{group[0].Title}\"  g{gen}→g{gen + 1}"
                          + $"  retires {retired}{(Block ? "  [BLOCK]" : "")}");
                if (Apply)
                {
                    var note = $"boxart-evict {stamp}: retired {retired} (g{gen}→g{gen + 1})"
                             + (Block ? ", blocked" : "") + (Reason.Length > 0 ? $" — {Reason}" : "");
                    foreach (var g in group)
                    {
                        g.BoxArtGeneration = gen + 1;
                        if (g.BoxArtPath != null) { g.BoxArtPath = null; cleared++; }
                        // A source URL that produced a rejected cover must go too, or step 0 re-fetches it
                        // and the eviction achieves nothing.
                        g.BoxArtSourceUrl = null;
                        if (Block) g.BoxArtBlocked = true;
                        g.Notes = string.IsNullOrWhiteSpace(g.Notes) ? note : g.Notes.TrimEnd() + "\n" + note;
                    }
                }
                changed++;
                if (Block) blocked++;
            }

            if (Apply) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"{{ cards: {cards.Count}, evicted: {changed}, blocked: {blocked}, unblocked: {unblocked}, "
                      + $"pathsCleared: {cleared} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else w.WriteLine("The retired files stay on the mount, orphaned — the route no longer asks for them.");
        }

        private static IEnumerable<int> ParseIds(string csv) =>
            csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(s => int.TryParse(s, out var n) ? n : -1)
               .Where(n => n > 0);
    }
}
