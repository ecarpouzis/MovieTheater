using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MovieTheater.Books;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Db;
using MovieTheater.Books.Services;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// The minimum service graph a CLI verb needs: one <see cref="BooksDb"/>, the archive readers, and the job
    /// classes. Built here rather than by <c>AddBooks</c> so a batch run brings up no web stack, no controllers
    /// and no cache warmer — a verb that opens a library file should not also start a server.
    /// </summary>
    internal static class CommandServices
    {
        public static ServiceProvider Build(BooksHostConfiguration config, string dbPath, string? cacheDir = null, LogLevel level = LogLevel.Warning)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(level));
            services.AddSingleton(new BooksOptions
            {
                DbPath = dbPath,
                CacheDir = cacheDir ?? config.CacheDir,
                ThumbnailQuality = config.ThumbnailQuality,
                SevenZipPath = config.SevenZipPath,
                ArchiveCacheGb = 0,   // a one-shot batch job gains nothing from warming the whole-archive cache
                EnableCacheWarmer = false,
            });
            services.AddDbContext<BooksDb>(o => BooksDbOptions.Configure(o, dbPath));
            services.AddMemoryCache(o => o.SizeLimit = 512);
            services.AddSingleton<SevenZipCliExtractor>();
            services.AddSingleton<IArchiveReader, CbzArchiveReader>();
            services.AddSingleton<IArchiveReader, CbrArchiveReader>();
            services.AddSingleton<IArchiveReader, PdfArchiveReader>();
            services.AddSingleton<IArchiveReader, MobiArchiveReader>();
            services.AddSingleton<IArchiveReader, EpubArchiveReader>();
            services.AddSingleton<ThumbnailService>();
            services.AddSingleton<ThumbnailJob>();
            services.AddSingleton<LibraryScanner>();
            services.AddSingleton<CalibreImportService>();
            services.AddSingleton<DuplicateDetectionService>();
            return services.BuildServiceProvider();
        }
    }
}
