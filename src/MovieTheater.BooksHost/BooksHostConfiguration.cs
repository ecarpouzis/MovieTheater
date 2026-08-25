using Microsoft.Extensions.Configuration;
using MovieTheater.Core;

namespace MovieTheater.BooksHost
{
    /// <summary>
    /// The host's settings, bound once from the <c>Books</c> section. Every path goes through
    /// <see cref="ConfiguredRoot.FullPathOrNull"/>: a JSON null or blank means "not configured on this host",
    /// never a startup crash. appsettings.json ships placeholders; appsettings.Production.json lives only on
    /// the host and is never copied by the deploy script (the StreamGateway convention).
    /// </summary>
    public sealed class BooksHostConfiguration
    {
        public BooksHostConfiguration(IConfiguration raw)
        {
            var b = raw.GetSection("Books");
            DbPath = ConfiguredRoot.FullPathOrNull(b["DbPath"]);
            LegsDbPath = ConfiguredRoot.FullPathOrNull(b["LegsDbPath"]);
            V1SourcePath = ConfiguredRoot.FullPathOrNull(b["V1SourcePath"]);
            CalibreLinkPath = ConfiguredRoot.FullPathOrNull(b["CalibreLinkPath"]);
            CacheDir = ConfiguredRoot.FullPathOrNull(b["CacheDir"]);
            ReportDir = ConfiguredRoot.FullPathOrNull(b["ReportDir"]);
            V1OwnerUsername = string.IsNullOrWhiteSpace(b["V1OwnerUsername"]) ? null : b["V1OwnerUsername"]!.Trim();
            OwnerUserId = int.TryParse(b["OwnerUserId"], out var uid) ? uid : 1;
        }

        /// <summary>The standalone site's owner account — the ONLY user whose activity migrates (decision 5).</summary>
        public string? V1OwnerUsername { get; }

        /// <summary>The site user id that owner becomes (default 1).</summary>
        public int OwnerUserId { get; }

        /// <summary>books.db — the hot catalog the runtime opens.</summary>
        public string? DbPath { get; }

        /// <summary>books-legs.db — the offline warehouse the CLI verbs open.</summary>
        public string? LegsDbPath { get; }

        /// <summary>The frozen v1 SQLite file (books-migrate-v1 / books-verify-v1 input; opened read-only).</summary>
        public string? V1SourcePath { get; }

        /// <summary>The Calibre link JSON (comicId ↔ calibreId) the items stage reads.</summary>
        public string? CalibreLinkPath { get; }

        /// <summary>Thumbnail / collection-icon cache root (the folders stage checks f_{id}.jpg presence here).</summary>
        public string? CacheDir { get; }

        /// <summary>Where verbs write their reports (orphan insights, verify results, replay tables).</summary>
        public string? ReportDir { get; }
    }
}
