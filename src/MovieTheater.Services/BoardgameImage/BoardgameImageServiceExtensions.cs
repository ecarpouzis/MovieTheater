using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Core;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;

namespace MovieTheater.Services.BoardgameImage
{
    public static class BoardgameImageServiceExtensions
    {
        public static IServiceCollection AddBoardgameImageServices(this IServiceCollection services, string? boardgameImagesDirectoryPath, HostedEnvironment environment)
        {
            DirectoryInfo boardgameImagesDir = new DirectoryInfo(boardgameImagesDirectoryPath ?? "BoardgameImages");

            if (!boardgameImagesDir.Exists)
                boardgameImagesDir.Create();

            services.Configure<LocalBoardgameImageOptions>(options =>
            {
                options.Directory = boardgameImagesDir;
            });

            services.AddTransient<BoardgamePdfRepository>();

            if (environment == HostedEnvironment.Production)
            {
                services.AddTransient<IBoardgameImageRepository, LocalBoardgameImageRepository>();
            }
            else
            {
                // We don't have valid ssl cert on theater.carpouzis.com so need to allow unsigned
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

                // DevBoardgameImageRepository gets an HttpClient
                services.AddTransient<IBoardgameImageRepository, DevBoardgameImageRepository>();
                services.AddHttpClient<IBoardgameImageRepository, DevBoardgameImageRepository>()
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
