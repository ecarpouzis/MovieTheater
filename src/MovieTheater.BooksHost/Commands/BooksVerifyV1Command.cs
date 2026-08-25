using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Verify;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-verify-v1</c> — the independent audit of a finished migration (id sets, counts, edges, the owner's
    /// activity, integrity, the series-resolution recompute diff) and, with <c>--replay</c>, the hot-set query
    /// replay with plans. Writes a Markdown report; exits non-zero on any failed check or flagged plan.
    /// </summary>
    [Command("books-verify-v1", Description = "Audit books.db/books-legs.db against the frozen v1 file; optionally replay the hot query set.")]
    public class BooksVerifyV1Command : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksVerifyV1Command(BooksHostConfiguration config) => this.config = config;

        [CommandOption("source", Description = "Frozen v1 file (default Books:V1SourcePath).")] public string? Source { get; set; }
        [CommandOption("target", Description = "books.db (default Books:DbPath).")] public string? Target { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? Legs { get; set; }
        [CommandOption("report", Description = "Report path (default <report-dir>/v2-migration-verify.md).")] public string? Report { get; set; }
        [CommandOption("owner", Description = "The standalone site's owner username (default Books:V1OwnerUsername).")] public string? Owner { get; set; }
        [CommandOption("owner-user-id", Description = "The site user id that owner became (default Books:OwnerUserId, else 1).")] public int? OwnerUserId { get; set; }
        [CommandOption("replay", Description = "Also run the hot-set query replay.")] public bool Replay { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var options = new MigrationOptions
            {
                SourcePath = Source ?? config.V1SourcePath ?? throw new CommandException("--source or Books:V1SourcePath is required."),
                TargetPath = Target ?? config.DbPath ?? throw new CommandException("--target or Books:DbPath is required."),
                LegsPath = Legs ?? config.LegsDbPath ?? throw new CommandException("--legs or Books:LegsDbPath is required."),
                ReportDir = config.ReportDir,
                OwnerUsername = Owner ?? config.V1OwnerUsername ?? throw new CommandException("--owner or Books:V1OwnerUsername is required."),
                UserIdForOwner = OwnerUserId ?? config.OwnerUserId,
            };
            var mapping = MappingContract.Load();
            using var source = new V1Source(options.SourcePath);
            using var hot = new TargetWriter(options.TargetPath, mapping, dryRun: true);
            using var legs = new TargetWriter(options.LegsPath, mapping, dryRun: true);
            var checks = new V1Verifier(source, hot, legs, options).Run();
            foreach (var c in checks) await console.Output.WriteLineAsync($"{(c.Passed ? "PASS" : "FAIL")} {c.Name}: {c.Detail}");
            var report = V1Verifier.Render(checks, $"books-verify-v1 — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            var flagged = 0;
            if (Replay)
            {
                var rows = new HotSetReplay(options.TargetPath).Run(l => console.Output.WriteLine(l));
                flagged = rows.Count(r => r.Flags.Count > 0);
                report += "\n" + HotSetReplay.Render(rows, "Hot-set replay") + $"\n- flagged: {flagged} of {rows.Count}\n";
            }
            var reportPath = Report ?? Path.Combine(config.ReportDir ?? Path.GetDirectoryName(Path.GetFullPath(options.TargetPath))!, "v2-migration-verify.md");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, report);
            await console.Output.WriteLineAsync($"report: {reportPath}");
            var failed = checks.Count(c => !c.Passed);
            if (failed > 0 || flagged > 0) throw new CommandException($"{failed} check(s) failed, {flagged} query plan(s) flagged", 3);
        }
    }
}
