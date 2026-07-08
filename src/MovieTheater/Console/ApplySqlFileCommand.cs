using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Console
{
    /// <summary>
    /// Applies a SQL script to the app's database through its own runtime connection — the missing half of
    /// this repo's manual-migration workflow. Design-time <c>dotnet ef database update</c> can't reach the
    /// live DB (no design-time connection string), so the deliberate pattern is: generate the idempotent SQL
    /// with <c>dotnet ef migrations script &lt;from&gt; &lt;to&gt; --idempotent</c>, read it, then apply it here.
    /// Splits on <c>GO</c> batch separators (which EF emits but ExecuteSqlRaw can't run as one statement).
    /// Dry-run by default: prints the batches without executing unless <c>--apply</c>.
    /// </summary>
    [Command("db-apply-sql", Description = "Apply a SQL script file via the app's DB connection (GO-separated batches). Dry-run unless --apply.")]
    public class ApplySqlFileCommand : BasicDICommand, ICommand
    {
        [CommandOption("file", 'f', Description = "Path to the .sql script (e.g. an EF idempotent migration script).", IsRequired = true)]
        public string File { get; set; } = default!;

        [CommandOption("apply", Description = "Execute the batches. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ApplySqlFileCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var path = Path.GetFullPath(File);
            if (!System.IO.File.Exists(path)) { w.WriteLine($"SQL file not found: {path}"); return; }

            var text = await System.IO.File.ReadAllTextAsync(path);
            // Split on lines that are exactly GO (case-insensitive), the SQL Server batch separator.
            var batches = System.Text.RegularExpressions.Regex
                .Split(text, @"(?im)^\s*GO\s*$")
                .Select(b => b.Trim())
                .Where(b => b.Length > 0)
                .ToList();

            w.WriteLine($"{path}: {batches.Count} batch(es).");
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(300);

            int run = 0;
            // Hold ONE open connection so a script-level BEGIN TRANSACTION … COMMIT spans all batches (EF would
            // otherwise close the connection between ExecuteSqlRaw calls, silently dropping the transaction). On
            // any error, closing the still-open connection rolls the server-side transaction back.
            if (Apply) await db.Database.OpenConnectionAsync();
            try
            {
                foreach (var batch in batches)
                {
                    var preview = batch.Length <= 120 ? batch.Replace("\r", " ").Replace("\n", " ")
                        : batch[..120].Replace("\r", " ").Replace("\n", " ") + "…";
                    if (Apply)
                    {
                        await db.Database.ExecuteSqlRawAsync(batch);
                        run++;
                        w.WriteLine($"  ✓ {preview}");
                    }
                    else w.WriteLine($"  · {preview}");
                }
            }
            finally { if (Apply) await db.Database.CloseConnectionAsync(); }

            w.WriteLine();
            w.WriteLine(Apply ? $"Applied {run} batch(es)." : "DRY RUN — nothing executed. Re-run with --apply.");
        }
    }
}
