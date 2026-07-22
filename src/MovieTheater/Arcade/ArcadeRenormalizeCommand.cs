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
    /// Recomputes <c>ArcadeGame.Title</c> / <c>SortTitle</c> from the <c>CloudRetroGameKey</c> using the
    /// current naming rules, to backfill catalog rows ingested before a naming fix. The concrete case:
    /// the TOSEC "bare version" token (e.g. <c>"Sonic Adventure v1.005"</c>) used to leak into the Title,
    /// so revisions of one game each became their OWN lobby card instead of collapsing into one card with
    /// a version dropdown. <see cref="ArcadeNaming.CleanTitle"/> now peels that token; this command applies
    /// the new title to the existing rows.
    ///
    /// <para><b>Bulk-job rules.</b> Dry-run-first: prints <c>{examined, updated, skipped, remaining,
    /// nextCursor}</c> and a sample of changes, writing nothing unless <c>--apply</c>. Bounded + resumable:
    /// at most <c>--limit</c> rows per run ordered by Id; the caller loops passing <c>--after
    /// &lt;nextCursor&gt;</c>. <b>Preserves hand-edits</b>: a row is only rewritten when its current Title
    /// exactly equals what the OLD algorithm produced from the key — i.e. it was never curated by hand.
    /// Idempotent: once a row's Title is the new normalized form it matches neither branch and is skipped.</para>
    /// </summary>
    [Command("arcade-renormalize-titles", Description = "Recompute ArcadeGame Title/SortTitle from the key (peels leaked version tokens); dry-run unless --apply.")]
    public class ArcadeRenormalizeCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max rows to examine this run (default 1000).")]
        public int Limit { get; set; } = 1000;

        [CommandOption("after", Description = "Resume cursor: skip rows whose Id ≤ this (from a prior run's nextCursor).")]
        public int After { get; set; }

        [CommandOption("system", Description = "Restrict to one system code (e.g. dc). Omit for all.")]
        public string System { get; set; } = "";

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeRenormalizeCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        // The PRE-FIX title algorithm = the CURRENT CleanTitle with its newest peel switched OFF. A row whose
        // stored Title equals this was produced by the previous code (not hand-edited), so it's safe to
        // rewrite to the new form. Using the real cleaner (minus the one new peel) instead of a hand-frozen
        // reimplementation keeps the guard faithful to what actually named the rows — the old frozen copy
        // dropped title-casing and the disc peels, so it stopped recognizing its own output and would have
        // refused to touch exactly the rows this fix targets. peelBareDisc:false is the sole difference
        // between the previous algorithm and the new one.
        private static string OldCleanTitle(string name) => ArcadeNaming.CleanTitle(name, collapseFixes: false);

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

            var batch = await q.OrderBy(g => g.Id).Take(Limit).ToListAsync();
            var remaining = await q.OrderBy(g => g.Id).Skip(batch.Count).CountAsync();

            int examined = 0, titleUpdated = 0, collapseUpdated = 0, skipped = 0, handEdited = 0, samplesShown = 0;
            int nextCursor = After;

            foreach (var g in batch)
            {
                examined++;
                nextCursor = g.Id;

                var newTitle = ArcadeNaming.CleanTitle(g.CloudRetroGameKey);
                var newSort = ArcadeNaming.ArticleInvert(newTitle);

                // Rewrite Title/SortTitle only when they differ from the current algorithm AND the row was
                // never hand-curated (its stored Title still equals the PREVIOUS algorithm's output).
                bool wantsTitle = g.Title != newTitle || g.SortTitle != newSort;
                bool rewriteTitle = wantsTitle && g.Title == OldCleanTitle(g.CloudRetroGameKey);
                if (wantsTitle && !rewriteTitle) handEdited++;

                // CollapseKey is ALWAYS derived from the EFFECTIVE display Title — the rewritten one if we're
                // rewriting, otherwise whatever the Title is now (including a hand-edited title). This is what
                // backfills the new grouping column for every row, hand-edited ones included.
                var effectiveTitle = rewriteTitle ? newTitle : g.Title;
                var newCollapse = ArcadeNaming.CollapseKey(effectiveTitle);
                bool rewriteCollapse = g.CollapseKey != newCollapse;

                if (!rewriteTitle && !rewriteCollapse) { skipped++; continue; }

                if (samplesShown < 25)
                {
                    if (rewriteTitle)
                        w.WriteLine($"  [{g.System}] {g.Id}: \"{g.Title}\" → \"{newTitle}\"  [key: {newCollapse}]");
                    else
                        w.WriteLine($"  [{g.System}] {g.Id}: (title kept \"{g.Title}\")  key: \"{g.CollapseKey}\" → \"{newCollapse}\"");
                    samplesShown++;
                }

                if (Apply)
                {
                    if (rewriteTitle) { g.Title = newTitle; g.SortTitle = newSort; }
                    g.CollapseKey = newCollapse;
                }
                if (rewriteTitle) titleUpdated++;
                if (rewriteCollapse) collapseUpdated++;
            }

            if (Apply) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"{(Apply ? "APPLIED" : "DRY RUN")}: examined={examined} titleUpdated={titleUpdated} collapseKeyUpdated={collapseUpdated} skipped={skipped} (hand-edited-titles-preserved={handEdited}) remaining={remaining}");
            if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}{(string.IsNullOrEmpty(System) ? "" : $" --system {System}")}{(Apply ? " --apply" : "")}.");
            else w.WriteLine("Done — no rows remaining.");
        }
    }
}
