using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace MovieTheater.Services.BoardgameImage
{
    public class DevBoardgameImageRepository : IBoardgameImageRepository
    {
        private readonly HttpClient httpClient;
        private readonly LocalBoardgameImageOptions options;

        public DevBoardgameImageRepository(HttpClient httpClient, IOptions<LocalBoardgameImageOptions> options)
        {
            this.httpClient = httpClient;
            this.options = options.Value;
        }

        public async Task<bool> HasImage(int boardgameId, BoardgameImageVariant variant)
        {
            var file = GetFile(boardgameId, variant);

            if (file.Exists)
                return true;

            // If file doesn't exist, try to get it by downloading
            var imageBytes = await GetImage(boardgameId, variant);
            return imageBytes != null;
        }

        public async Task<byte[]?> GetImage(int boardgameId, BoardgameImageVariant variant)
        {
            var file = GetFile(boardgameId, variant);

            if (!file.Exists)
            {
                string url;

                if (variant == BoardgameImageVariant.Main)
                {
                    url = $"https://theater.carpouzis.com/BoardgameImage/{boardgameId}";
                }
                else if (variant == BoardgameImageVariant.Thumbnail)
                {
                    url = $"https://theater.carpouzis.com/BoardgameImageThumb/{boardgameId}";
                }
                else
                {
                    throw new InvalidOperationException($"Unrecognized BoardgameImageVariant: \"{variant}\" ({(int)variant})");
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

        public Task SaveImage(int boardgameId, BoardgameImageVariant variant, byte[] imageContent)
        {
            throw new InvalidOperationException("You cannot save images in dev mode.");
        }

        public Task<DateTimeOffset?> GetImageModifiedDate(int boardgameId, BoardgameImageVariant variant)
        {
            var file = GetFile(boardgameId, variant);
            DateTimeOffset? result = file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc) : null;
            return Task.FromResult(result);
        }

        private FileInfo GetFile(int boardgameId, BoardgameImageVariant variant)
        {
            string path;

            if (variant == BoardgameImageVariant.Main)
            {
                path = Path.Combine(options.Directory.FullName, boardgameId + ".png");
            }
            else if (variant == BoardgameImageVariant.Thumbnail)
            {
                path = Path.Combine(options.Directory.FullName, boardgameId + "_s.png");
            }
            else
            {
                throw new InvalidOperationException($"Unrecognized BoardgameImageVariant: \"{variant}\" ({(int)variant})");
            }

            return new FileInfo(path);
        }
    }
}
