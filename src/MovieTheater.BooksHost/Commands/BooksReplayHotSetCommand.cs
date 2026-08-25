using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using MovieTheater.Books.Verify;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-replay-hot-set</c> — time the standalone site's hot query set over books.db and read each plan
    /// back; the v2 model's performance proof, runnable on its own after any index change.
    /// </summary>
    [Command("books-replay-hot-set", Description = "Time the hot query set over books.db and flag full scans / temp sorts.")]
    public class BooksReplayHotSetCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksReplayHotSetCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("report", Description = "Report path (default <report-dir>/v2-replay.md).")] public string? Report { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var path = DbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required.");
            var rows = new HotSetReplay(path).Run(l => console.Output.WriteLine(l));
            var reportPath = Report ?? Path.Combine(config.ReportDir ?? Path.GetDirectoryName(Path.GetFullPath(path))!, "v2-replay.md");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, HotSetReplay.Render(rows, $"Hot-set replay — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"));
            var flagged = rows.Count(r => r.Flags.Count > 0);
            await console.Output.WriteLineAsync($"report: {reportPath}; flagged {flagged} of {rows.Count}");
            if (flagged > 0) throw new CommandException("flagged plans", 3);
        }
    }
}
