using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Core;
using System.Net;
using System.Security.Authentication;

namespace MovieTheater.Services.Poster
{
    public static class PosterServiceExtensions
    {
        public static IServiceCollection AddPosterImageServices(this IServiceCollection services, string? postersDirectoryPath, HostedEnvironment environment)
        {
            DirectoryInfo postersDir = new DirectoryInfo(postersDirectoryPath ?? "posters");

            if (!postersDir.Exists)
            {
                if (environment == HostedEnvironment.Production)
                    throw new DirectoryNotFoundException("Movie posters directory is invalid. Should be set via environment variable `MOVIE_POSTERSDIR`");
                else
                    postersDir.Create();
            }

            services.AddTransient<ImageShrinkService>();
            services.Configure<LocalPosterImageOptions>(options =>
            {
                options.Directory = postersDir;
            });

            if (environment == HostedEnvironment.Production)
            {
                services.AddTransient<IPosterImageRepository, LocalPosterImageRepository>();
            }
            else
            {
                // We don't have valid ssl cert on theater.carpouzis.com so need to allow unsigned
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

                // DevPosterImageRepository gets an HttpClient
                services.AddTransient<IPosterImageRepository, DevPosterImageRepository>();
                services.AddHttpClient<IPosterImageRepository, DevPosterImageRepository>()
                    .ConfigurePrimaryHttpMessageHandler(_ =>
                    {
                        var handler = new HttpClientHandler
                        {
                            AllowAutoRedirect = false
                        };

                        handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                        handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; };
                        handler.SslProtocols = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12;

                        return handler;
                    });
            }

            return services;
        }
    }
}
