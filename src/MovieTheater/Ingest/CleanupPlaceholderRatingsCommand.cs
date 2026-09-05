using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Removes legacy placeholder "Rated" viewings — rows the old rating feature left with a ViewingData of
    /// "0" / empty (i.e. a non-rating). Real ratings (1–100) are never touched. Clearing the placeholders
    /// frees 0 to be a usable score and de-clutters the new Rate page. Guarded + dry-run by default; deletes
    /// in bounded, observable batches when --apply'd, and is resumable (each batch commits; the predicate
    /// naturally excludes already-deleted rows on a re-run).
    /// </summary>
    [Command("cleanup-placeholder-ratings", Description = "Delete legacy 0/empty 'Rated' viewings (placeholders); keep real 1–100 ratings. Dry-run by default.")]
    public class CleanupPlaceholderRatingsCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public CleanupPlaceholderRatingsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            // Placeholder = a "Rated" row whose score is absent or "0". Real ratings (>= 1) are never matched.
            var placeholders = db.Viewings.Where(v =>
                v.ViewingType == ViewingTypes.Rated && (v.ViewingData == null || v.ViewingData == "" || v.ViewingData == "0"));

            var total = await placeholders.CountAsync();
            var kept = await db.Viewings.CountAsync(v =>
                v.ViewingType == ViewingTypes.Rated && v.ViewingData != null && v.ViewingData != "" && v.ViewingData != "0");

            var perUser = await placeholders
                .GroupBy(v => v.UserID)
                .Select(g => new { UserID = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToListAsync();

            w.WriteLine($"Placeholder 'Rated' rows (0 / empty): {total}{(Apply ? "" : "  (dry run)")}");
            w.WriteLine($"Real ratings kept (1–100): {kept}");
            foreach (var u in perUser) w.WriteLine($"  user {u.UserID}: {u.Count}");

            if (!Apply)
            {
                w.WriteLine("\nDRY RUN — re-run with --apply to delete the placeholders above.");
                return;
            }

            // Bounded, observable deletes (project rule): batch + report progress.
            const int batchSize = 500;
            int removed = 0;
            while (true)
            {
                var batch = await placeholders.OrderBy(v => v.ViewingID).Take(batchSize).ToListAsync();
                if (batch.Count == 0) break;
                db.Viewings.RemoveRange(batch);
                await db.SaveChangesAsync();
                removed += batch.Count;
                w.WriteLine($"  deleted {removed}/{total}…");
            }
            w.WriteLine($"Done. Removed {removed} placeholder 'Rated' row(s).");
        }
    }
}
