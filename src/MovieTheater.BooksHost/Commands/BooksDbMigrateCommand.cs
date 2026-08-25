using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-db-migrate</c> — applies the EF migrations of both contexts to their files (creating them when
    /// absent), switches each file to WAL, and seeds the <see cref="DerivedTable"/> registry. The ONLY way the
    /// Books schema ever changes: the host never mutates schema at boot.
    /// </summary>
    [Command("books-db-migrate", Description = "Create/upgrade books.db and books-legs.db to the current EF model.")]
    public class BooksDbMigrateCommand : ICommand
    {
        private readonly BooksHostConfiguration config;

        public BooksDbMigrateCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db path (default Books:DbPath).")]
        public string? DbPath { get; set; }

        [CommandOption("legs", Description = "books-legs.db path (default Books:LegsDbPath).")]
        public string? LegsPath { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var hot = DbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required.");
            var legs = LegsPath ?? config.LegsDbPath ?? throw new CommandException("--legs or Books:LegsDbPath is required.");

            await console.Output.WriteLineAsync(await MigrateAsync(new BooksDb(BooksDbOptions.Hot(hot)), "books.db", hot));
            await console.Output.WriteLineAsync(await MigrateAsync(new BooksLegsDb(BooksDbOptions.Legs(legs)), "books-legs.db", legs));

            await using var db = new BooksDb(BooksDbOptions.Hot(hot));
            var seeded = await SeedDerivedTablesAsync(db);
            await console.Output.WriteLineAsync($"DerivedTable registry: {seeded} new entries, {DerivedTables.All.Count} total.");
        }

        public static async Task<string> MigrateAsync(DbContext db, string label, string path)
        {
            await using (db)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
                await db.Database.MigrateAsync();
                // WAL persists in the file; it cannot be set inside a migration's transaction, so it is set here.
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                var applied = (await db.Database.GetAppliedMigrationsAsync()).Count();
                return $"{label}: {path} — applied {pending.Count} pending migration(s), {applied} total.";
            }
        }

        public static async Task<int> SeedDerivedTablesAsync(BooksDb db)
        {
            var existing = await db.DerivedTables.Select(d => d.Name).ToHashSetAsync();
            var added = 0;
            foreach (var e in DerivedTables.All)
            {
                if (existing.Contains(e.Name)) continue;
                db.DerivedTables.Add(new DerivedTable { Name = e.Name, RebuildJob = e.RebuildJob });
                added++;
            }
            await db.SaveChangesAsync();
            return added;
        }
    }
}
