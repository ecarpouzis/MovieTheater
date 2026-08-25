using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MovieTheater.Books.Db;
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
