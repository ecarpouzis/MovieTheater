using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace MovieTheater.Services.Poster
{
    public class DevPosterImageRepository : IPosterImageRepository
    {
        private readonly HttpClient httpClient;
        private readonly LocalPosterImageOptions options;

        public DevPosterImageRepository(HttpClient httpClient, IOptions<LocalPosterImageOptions> options)
        {
            this.httpClient = httpClient;
            this.options = options.Value;
        }

        public async Task<bool> HasImage(int movieId, PosterImageVariant variant)
        {
            var file = GetFile(movieId, variant);

            if (file.Exists)
                return true;

            // If file doesn't exist, try to get it by downloading
            var imageBytes = await GetImage(movieId, variant);
            return imageBytes != null;
        }

        public async Task<byte[]> GetImage(int movieId, PosterImageVariant variant)
        {
            var file = GetFile(movieId, variant);

            if (!file.Exists)
            {
                string url;

                if (variant == PosterImageVariant.Main)
                {
                    url = $"https://theater.carpouzis.com/Image/{movieId}";
                }
                else if (variant == PosterImageVariant.Thumbnail)
                {
                    url = $"https://theater.carpouzis.com/ImageThumb/{movieId}";
                }
                else
                {
                    throw new InvalidOperationException($"Unrecognized PosterImageVariant: \"{variant}\" ({(int)variant})");
                }


                var request = new HttpRequestMessage(HttpMethod.Get, url);
                var response = await httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                response.EnsureSuccessStatusCode();

                var responseBytes = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(file.FullName, responseBytes);
                return responseBytes;
            }

            return await File.ReadAllBytesAsync(file.FullName);
        }

        public Task SaveImage(int movieId, PosterImageVariant variant, byte[] imageContent)
        {
            throw new InvalidOperationException("You cannot save images in dev mode.");
        }

        private FileInfo GetFile(int movieId, PosterImageVariant variant)
        {
            string path;

            if (variant == PosterImageVariant.Main)
            {
                path = Path.Combine(options.Directory.FullName, movieId + ".png");
            }
            else if (variant == PosterImageVariant.Thumbnail)
            {
                path = Path.Combine(options.Directory.FullName, movieId + ".png");
            }
            else
            {
                throw new InvalidOperationException($"Unrecognized PosterImageVariant: \"{variant}\" ({(int)variant})");
            }

            return new FileInfo(path);
        }
    }
}
