using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Db;
using MovieTheater.Books.Media;
using MovieTheater.Books.Providers;
using MovieTheater.Books.Services;

namespace MovieTheater.Books
{
    /// <summary>
    /// What the host has to know about the Books vertical: two calls. Everything else — the controllers, the
    /// projection, the caches, the warmer — lives in this library, so the host stays a thin CliFx shell and the
    /// same surface can be hosted by a test or a future process without copying wiring.
    /// </summary>
    public sealed class BooksOptions
    {
        /// <summary>books.db. When null the catalog endpoints are still mapped but no <see cref="BooksDb"/> is registered.</summary>
        public string? DbPath { get; init; }

        /// <summary>The hard page ceiling for <c>/odata/catalog</c> — the same number the [EnableQuery] attribute carries.</summary>
        public int MaxTop { get; init; } = 500;

        /// <summary>
        /// How many cached payloads (facet sets, head lists) may live at once. Entries each declare Size = 1, so
        /// this is a count, not a byte budget: it bounds the working set without pretending to measure heap.
        /// </summary>
        public long CacheEntryLimit { get; init; } = 2_000;

        /// <summary>Run the change-driven cache warmer. Off in tests and in any process that is not the service.</summary>
        public bool EnableCacheWarmer { get; init; } = true;

        // ── slice 2: media plane, readers, thumbnails ────────────────────────────────────────────────────
        // These are the host's own settings (Books:…) handed to the library, because the pieces that need them
        // — the readers, the thumbnail service, the URL builders in the item payloads — live HERE, not in the
        // host. A null path means "not configured on this host" and the feature degrades; it is never a crash.

        /// <summary>This host's public base URL — the origin of every media URL its JSON hands out.</summary>
        public string? PublicBaseUrl { get; init; }

        /// <summary>The HMAC secret that mints and validates media-plane tokens. Host-only; the site never mints.</summary>
        public string? MediaTokenSecret { get; init; }

        /// <summary>Thumbnail + folder-icon cache root. Thumbnails are <c>{itemId}.webp</c>, icons <c>f_{id}.jpg</c>.</summary>
        public string? CacheDir { get; init; }

        /// <summary>Where whole archives are copied off the share. Null ⇒ <c>{CacheDir}/archives</c>.</summary>
        public string? ArchiveCacheDir { get; init; }

        /// <summary>Budget for that copy cache, in GB. 0 disables it (every read then goes to the share).</summary>
        public int ArchiveCacheGb { get; init; }

        /// <summary>JPEG quality for a scaled page on the wire.</summary>
        public int PageJpegQuality { get; init; } = 82;

        /// <summary>Byte budget (MB) of the extracted-page cache. Its own MemoryCache, not the shared one.</summary>
        public int PageCacheLimitMb { get; init; } = 384;

        /// <summary>WebP quality for a generated thumbnail. 75 is the measured setting — see ThumbnailService.</summary>
        public int ThumbnailQuality { get; init; } = 75;

        /// <summary>Optional 7z.exe for the archive fallback. Null ⇒ probe the usual install paths.</summary>
        public string? SevenZipPath { get; init; }

        /// <summary>Run the Bubble Zoom detector. Off ⇒ the text-region endpoint answers an empty list.</summary>
        public bool EnableTextRegions { get; init; } = true;

        // ── slice 5: admin & providers ───────────────────────────────────────────────────────────────────

        /// <summary>books-legs.db — the offline warehouse. The tag folds, the LOCG containment reduction and the
        /// provider response cache read it; null means those jobs are unavailable, never that startup fails.</summary>
        public string? LegsDbPath { get; init; }

        /// <summary>
        /// The ComicVine API key — PLAIN CONFIGURATION (`Books:ComicVineApiKey`), overridable through the admin
        /// settings overlay. The standalone's per-user DPAPI key vault is deleted: one shared scraper key
        /// belongs to the host, not to an account. Null ⇒ the scrapers run cache-only and never open a socket.
        /// </summary>
        public string? ComicVineApiKey { get; init; }

        /// <summary>The admin-writable settings overlay (default <c>{DataDir}/books.settings.json</c>).</summary>
        public string? SettingsOverlayPath { get; init; }

        /// <summary>The Calibre <c>calibre_link.json</c> the book importer matches by.</summary>
        public string? CalibreLinkPath { get; init; }
    }

    public static class BooksServiceExtensions
    {
        /// <summary>
        /// Register the Books runtime: the catalog DbContext, the controllers (as an application part of THIS
        /// assembly), OData in query-options-only mode — the site's own mode: no EDM route components, no
        /// metadata document, just <c>[EnableQuery]</c> on attribute-routed actions — the bounded memory cache
        /// the browse heads/facets live in, and the warmer.
        /// </summary>
        public static IServiceCollection AddBooks(this IServiceCollection services, BooksOptions options)
        {
            services.AddSingleton(options);

            if (options.DbPath != null && services.All(d => d.ServiceType != typeof(BooksDb)))
                services.AddDbContext<BooksDb>(o => BooksDbOptions.Configure(o, options.DbPath));

            services.AddMemoryCache(o => o.SizeLimit = options.CacheEntryLimit);

            services.AddControllers()
                .AddApplicationPart(typeof(BooksServiceExtensions).Assembly)
                .AddJsonOptions(json =>
                {
                    // Enums ride as names (DatePrecision, SynopsisSource, …) exactly as the site serializes them,
                    // so the client never has to keep a copy of the integer vocabulary.
                    json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                })
                .AddOData(opts => opts.Select().Filter().OrderBy().Expand().SetMaxTop(options.MaxTop));

            if (options.EnableCacheWarmer)
                services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, CacheWarmupService>());

            // ── slice 2 (items, folders, thumbnails, pages, readers) ─────────────────────────────────────
            // All singletons: every one of them owns a cache or a lock table whose whole value is that it
            // outlives a request. The controllers are found by the application part above and need no
            // registration of their own.
            services.AddSingleton<SevenZipCliExtractor>();
            services.AddSingleton<IArchiveReader, CbzArchiveReader>();
            services.AddSingleton<IArchiveReader, CbrArchiveReader>();
            services.AddSingleton<IArchiveReader, PdfArchiveReader>();
            services.AddSingleton<IArchiveReader, EpubArchiveReader>();
            services.AddSingleton<IArchiveReader, MobiArchiveReader>();
            services.AddSingleton<EpubReaderService>();
            services.AddSingleton<LocalArchiveCache>();
            services.AddSingleton<PageByteCache>();
            services.AddSingleton<ImageScalingService>();
            services.AddSingleton<TextRegionService>();
            services.AddSingleton<ThumbnailService>();
            services.AddSingleton<ThumbnailJob>();
            services.AddSingleton<MediaAccess>();
            // ── end slice 2 ──────────────────────────────────────────────────────────────────────────────

            // ── slice 5 (admin & providers) ──────────────────────────────────────────────────────────────
            // The job classes are singletons because each owns a cursor protocol, not per-request state; the
            // JobRunner is a singleton for the same reason it exists at all (a job outlives the request that
            // started it). The two HTTP clients are named so a test can replace their handler and prove the
            // scrapers never open a socket.
            services.AddSingleton<JobRunner>();
            services.AddSingleton<InMemoryLogStore>();
            services.AddSingleton(sp => new BooksSettingsOverlay(options.SettingsOverlayPath));
            services.AddSingleton<LibraryScanner>();
            services.AddSingleton<CalibreImportService>();
            services.AddSingleton<DuplicateDetectionService>();
            services.AddSingleton<DataNormalizationService>();
            services.AddSingleton<SeriesMismatchService>();
            services.AddSingleton<SeriesNamesService>();

            services.AddSingleton(sp => new ProviderCacheStore(options.LegsDbPath));
            services.AddHttpClient<ComicVineClient>();
            services.AddHttpClient<ExternalWorkScraper>();
            services.AddSingleton(sp => new ComicVineClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ComicVineClient)),
                sp.GetRequiredService<ProviderCacheStore>(),
                sp.GetRequiredService<BooksSettingsOverlay>().Value("ComicVineApiKey") ?? options.ComicVineApiKey,
                sp.GetRequiredService<ILogger<ComicVineClient>>()));
            services.AddSingleton(sp => new ExternalWorkScraper(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ExternalWorkScraper)),
                sp.GetRequiredService<ProviderCacheStore>(),
                sp.GetRequiredService<ILogger<ExternalWorkScraper>>()));
            services.AddSingleton<ComicVineSeriesScraper>();
            services.AddSingleton<ComicVineIssueScraper>();
            // ── end slice 5 ──────────────────────────────────────────────────────────────────────────────

            return services;
        }

        /// <summary>Map the Books routes. They inherit the host's fallback authorization policy — identity required.</summary>
        public static WebApplication MapBooks(this WebApplication app)
        {
            app.MapControllers();
            return app;
        }
    }
}
