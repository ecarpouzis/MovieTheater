using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Core;
using System.IO;
using System.Net.Http;

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
                // DevBoardgameImageRepository reads through from prod over normally-validated HTTPS.
                services.AddTransient<IBoardgameImageRepository, DevBoardgameImageRepository>();
                services.AddHttpClient<IBoardgameImageRepository, DevBoardgameImageRepository>()
                    .ConfigurePrimaryHttpMessageHandler(_ => new HttpClientHandler
                    {
                        AllowAutoRedirect = false
                    });
            }

            return services;
        }
    }
}
