using Microsoft.Extensions.DependencyInjection;

namespace MovieTheater.Services.Arcade
{
    public static class ArcadeServiceExtensions
    {
        /// <summary>
        /// Registers the arcade host seam. Like <c>AddJellyfinServices</c> this tolerates missing
        /// config: with no gateway URL / token secret the site still boots and runs, and the arcade
        /// endpoints report unconfigured (hidden / 503) rather than throwing. The host is a singleton —
        /// it is stateless and only reads config + mints tokens.
        /// </summary>
        public static IServiceCollection AddArcadeServices(this IServiceCollection services, MovieTheaterConfiguration config)
        {
            services.AddSingleton<IArcadeHost>(_ => new CloudRetroHost(config));
            return services;
        }
    }
}
