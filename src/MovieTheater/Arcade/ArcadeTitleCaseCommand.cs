using System;
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
    /// Backfills proper title case onto existing <c>ArcadeGame</c> rows whose <c>Title</c> is entirely
    /// lowercase — the R: drive's reclassified PSX/Saturn/CD-i/Sega CD/Naomi/Atomiswave/PC Engine-CD
    /// collections carry lowercase filenames, so rows ingested from them (via <see cref="ArcadeJitIngestCommand"/>
    /// before <see cref="ArcadeNaming.CleanTitle"/> learned to title-case) got Titles like
    /// <c>"70's robot anime - geppy-x - the super boosted armor"</c> instead of a proper card name.
    ///
    /// <para>Recomputes from <c>CloudRetroGameKey</c> through the current (now case-fixing)
    /// <see cref="ArcadeNaming.CleanTitle"/> — the same "recompute from the key" approach as
    /// <see cref="ArcadeRenormalizeCommand"/>. Only rewrites a row when its CURRENT Title is entirely
    /// lowercase (so a hand-curated or already-properly-cased title, which by definition isn't all-lower,
    /// is never touched) AND the recomputed title is case-insensitively identical to the old one (a sanity
    /// check that this is purely a case fix, not a semantic title change from a since-updated key).</para>
    ///
    /// <para><b>Bulk-job rules.</b> Dry-run-first: prints a sample + <c>{examined, updated, skipped,
    /// remaining, nextCursor}</c>, writing nothing unless <c>--apply</c>. Bounded + resumable: at most
    /// <c>--limit</c> rows per run ordered by Id; loop passing <c>--after &lt;nextCursor&gt;</c>. Idempotent:
    /// once a row is properly cased it's no longer all-lower and is skipped on re-run.</para>
    /// </summary>
    [Command("arcade-titlecase", Description = "Title-case ArcadeGame rows whose Title is entirely lowercase (dry-run unless --apply).")]
    public class ArcadeTitleCaseCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max rows to examine this run (default 2000).")]
        public int Limit { get; set; } = 2000;

        [CommandOption("after", Description = "Resume cursor: skip rows whose Id ≤ this (from a prior run's nextCursor).")]
        public int After { get; set; }

        [CommandOption("system", Description = "Restrict to one system code (e.g. ps1). Omit for all.")]
        public string System { get; set; } = "";

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeTitleCaseCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            var q = db.ArcadeGames.AsQueryable().Where(g => g.Id > After);
            if (!string.IsNullOrWhiteSpace(System))
            {
                var sys = System.Trim().ToLowerInvariant();
                q = q.Where(g => g.System == sys);
            }

            var batch = await q.OrderBy(g => g.Id).Take(Math.Max(1, Limit)).ToListAsync();
            var remaining = await q.OrderBy(g => g.Id).Skip(batch.Count).CountAsync();

            int examined = 0, updated = 0, skipped = 0, samplesShown = 0;
            int nextCursor = After;

            foreach (var g in batch)
            {
                examined++;
                nextCursor = g.Id;

                if (!ArcadeNaming.IsAllLower(g.Title)) { skipped++; continue; }

                var newTitle = ArcadeNaming.CleanTitle(g.CloudRetroGameKey);
                if (!string.Equals(newTitle, g.Title, StringComparison.OrdinalIgnoreCase))
                {
                    // The key changed the title's WORDS, not just its case (stale key vs. a hand-tweak
                    // elsewhere) — don't silently overwrite something CleanTitle no longer agrees with.
                    skipped++;
                    continue;
                }
                if (newTitle == g.Title) { skipped++; continue; } // already properly cased somehow

                var newSort = ArcadeNaming.ArticleInvert(newTitle);

                if (samplesShown < 25)
                {
                    w.WriteLine($"  [{g.System}] {g.Id}: \"{g.Title}\" → \"{newTitle}\"");
                    samplesShown++;
                }

                if (Apply)
                {
                    g.Title = newTitle;
                    g.SortTitle = newSort;
                }
                updated++;
            }

            if (Apply) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"{(Apply ? "APPLIED" : "DRY RUN")}: examined={examined} updated={updated} skipped={skipped} remaining={remaining}");
            if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}{(string.IsNullOrEmpty(System) ? "" : $" --system {System}")}{(Apply ? " --apply" : "")}.");
            else w.WriteLine("Done — no rows remaining.");
        }
    }
}
