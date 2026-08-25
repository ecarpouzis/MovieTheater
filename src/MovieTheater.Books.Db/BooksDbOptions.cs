using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MovieTheater.Books.Db
{
    /// <summary>
    /// The one place a Books SQLite file is opened. Every context (runtime, CLI verbs, design-time
    /// tooling, tests) goes through <see cref="Configure"/> so the pragmas below are never forgotten.
    /// </summary>
    public static class BooksDbOptions
    {
        public static DbContextOptionsBuilder<T> Configure<T>(DbContextOptionsBuilder<T> builder, string path, bool readOnly = false)
            where T : DbContext
        {
            Configure((DbContextOptionsBuilder)builder, path, readOnly);
            return builder;
        }

        /// <summary>The non-generic form <c>AddDbContext</c> lambdas receive.</summary>
        public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string path, bool readOnly = false)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
            }.ToString();
            builder.UseSqlite(cs);
            builder.AddInterceptors(SqliteTuningInterceptor.Instance);
            return builder;
        }

        public static DbContextOptions<BooksDb> Hot(string path, bool readOnly = false) =>
            Configure(new DbContextOptionsBuilder<BooksDb>(), path, readOnly).Options;

        public static DbContextOptions<BooksLegsDb> Legs(string path, bool readOnly = false) =>
            Configure(new DbContextOptionsBuilder<BooksLegsDb>(), path, readOnly).Options;
    }

    /// <summary>
    /// Per-connection SQLite tuning, applied on every open (connections are pooled, so it runs rarely).
    /// Ported from the standalone books site, where it was the single biggest cold-read lever: the
    /// first browse after the machine has sat idle full-scans a ~500 MB file from cold disk.
    ///  - mmap_size 1 GB: pages come straight through the OS page cache, shared across the pool.
    ///  - cache_size 32 MB private page cache (default ~2 MB) so hot B-tree interior pages survive.
    ///  - temp_store=MEMORY: GROUP BY / ORDER BY spill buffers stay off disk.
    ///  - busy_timeout 5 s: writers (scan, positions, jobs) wait instead of surfacing SQLITE_BUSY.
    ///  - foreign_keys ON: Microsoft.Data.Sqlite enables it by default; stated so a bare connection matches.
    ///  - journal_mode=WAL is set once per file by the migration verb (it persists in the file).
    /// </summary>
    public sealed class SqliteTuningInterceptor : DbConnectionInterceptor
    {
        public static readonly SqliteTuningInterceptor Instance = new();

        private const string Pragmas =
            "PRAGMA mmap_size=1073741824;" +
            "PRAGMA cache_size=-32768;" +
            "PRAGMA temp_store=MEMORY;" +
            "PRAGMA busy_timeout=5000;" +
            "PRAGMA foreign_keys=ON;";

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = Pragmas;
            cmd.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = Pragmas;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
