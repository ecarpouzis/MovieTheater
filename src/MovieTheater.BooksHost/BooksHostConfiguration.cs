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
            Urls = Text(b["Urls"]);
            SiteOrigin = Text(b["SiteOrigin"]);
            PublicBaseUrl = Text(b["PublicBaseUrl"]);
            IdentityTokenSecret = Text(b["IdentityTokenSecret"]);
            MediaTokenSecret = Text(b["MediaTokenSecret"]);
            ArchiveCacheDir = ConfiguredRoot.FullPathOrNull(b["ArchiveCacheDir"]);
            ArchiveCacheGb = Int(b["ArchiveCacheGb"], 0);
            PageJpegQuality = Clamp(Int(b["PageJpegQuality"], 82), 40, 100);
            PageCacheLimitMb = Clamp(Int(b["PageCacheLimitMb"], 384), 16, 8192);
            ThumbnailQuality = Clamp(Int(b["ThumbnailQuality"], 75), 40, 100);
            SevenZipPath = Text(b["SevenZipPath"]);
            EnableTextRegions = !bool.TryParse(b["EnableTextRegions"], out var tr) || tr;
        }

        private static string? Text(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        private static int Int(string? v, int fallback) => int.TryParse(v, out var n) ? n : fallback;
        private static int Clamp(int v, int lo, int hi) => Math.Clamp(v, lo, hi);

        /// <summary>Kestrel listen URL(s) for the <c>web</c> verb (default http://localhost:2204; Caddy fronts it).</summary>
        public string? Urls { get; }

        /// <summary>The site origin — the only origin allowed to fetch media here (one ACAO, Vary: Origin).</summary>
        public string? SiteOrigin { get; }

        /// <summary>This host's public base (https://books-host.…), the origin of every media URL it mints.</summary>
        public string? PublicBaseUrl { get; }

        /// <summary>HMAC secret shared with the site pods; opens the X-MT-Identity header.</summary>
        public string? IdentityTokenSecret { get; }

        /// <summary>HMAC secret known to this host only; mints and validates the media-plane tokens.</summary>
        public string? MediaTokenSecret { get; }

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

        // ── R6 slice 2: readers, pages, thumbnails ───────────────────────────────────────────────────────

        /// <summary>Where whole archives are copied off the library share. Null ⇒ <c>{CacheDir}/archives</c>.</summary>
        public string? ArchiveCacheDir { get; }

        /// <summary>Budget for that copy cache in GB. 0 (the default) disables it entirely.</summary>
        public int ArchiveCacheGb { get; }

        /// <summary>JPEG quality for a scaled page on the wire.</summary>
        public int PageJpegQuality { get; }

        /// <summary>Byte budget (MB) of the extracted-page cache — its own MemoryCache, not the shared one.</summary>
        public int PageCacheLimitMb { get; }

        /// <summary>WebP quality for a generated thumbnail.</summary>
        public int ThumbnailQuality { get; }

        /// <summary>Optional 7z.exe for the archive fallback. Null ⇒ probe the usual install paths.</summary>
        public string? SevenZipPath { get; }

        /// <summary>Run the Bubble Zoom detector (default on). Off ⇒ the text-region route answers an empty list.</summary>
        public bool EnableTextRegions { get; }
    }
}
