using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using MovieTheater.Books.Resolve;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-dedup-contained</c> — produce the <c>ContainedIn</c> duplicate groups from containment, the
    /// relationship <c>books-dedup</c> declares but cannot see: a single issue that a collected edition already
    /// holds. Flag-only by design; the groups appear in the admin Duplicates tab and cannot be bulk-resolved.
    /// Dry-run by default, chunked by series, resumable with <c>--resume</c>.
    /// </summary>
    [Command("books-dedup-contained", Description = "Group single issues under the collected edition that contains them (ContainedIn, review-only).")]
    public class BooksDedupContainedCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksDedupContainedCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("batch-size", Description = "Series per batch (default 500).")] public int BatchSize { get; set; } = 500;
        [CommandOption("min-confidence", Description = "Lowest judged span confidence that may make a group (default 0.8). Gold indicia rows are exempt.")] public double MinConfidence { get; set; } = ContainedDuplicateJob.MinConfidence;
        [CommandOption("resume", Description = "Continue from books:dedup-contained:cursor.")] public bool Resume { get; set; }
        [CommandOption("reset", Description = "Delete every ContainedIn group first (use after a containment rebuild).")] public bool Reset { get; set; }
        [CommandOption("top", Description = "How many groups to print (default 25).")] public int Top { get; set; } = 25;
        [CommandOption("apply", Description = "Actually write. Without it the job walks read-only and counts.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath, dryRun: !Apply);
            if (Reset)
            {
                hot.Begin();
                var wiped = ContainedDuplicateJob.Reset(hot);
                hot.Commit();
                await console.Output.WriteLineAsync(Apply
                    ? $"reset: {wiped} ContainedIn groups cleared"
                    : $"reset: {wiped} ContainedIn groups WOULD be cleared — they still stand for this dry run, "
                      + "so the walk below counts only containers that are not already grouped");
            }

            var (groups, members, skipped) = ContainedDuplicateJob.RunAll(
                hot, BatchSize, MinConfidence, s => console.Output.WriteLine(s), Resume, Top);

            await console.Output.WriteLineAsync(
                $"contained dedup: {groups} groups, {members} members, {skipped} containers skipped"
                + (Apply ? "" : " (dry run — re-run with --apply)"));
            if (Apply)
                await console.Output.WriteLineAsync(
                    "these groups are review-only: owning both the floppies and the collection is legitimate");
        }
    }
}
