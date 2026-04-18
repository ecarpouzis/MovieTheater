using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieTheater.Db;
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
        private readonly MovieDb movieDb;

        public DevBoardgameImageRepository(HttpClient httpClient, IOptions<LocalBoardgameImageOptions> options, MovieDb movieDb)
        {
            this.httpClient = httpClient;
            this.options = options.Value;
            this.movieDb = movieDb;
        }

        public async Task<bool> HasImage(int boardgameId, BoardgameImageVariant variant)
        {
            var file = GetFile(boardgameId, variant);

            if (file.Exists)
            {
                if (!await IsLocalCacheStale(boardgameId, variant))
                    return true;
            }

            // If file doesn't exist (or cache is stale), try to get it by downloading
            var imageBytes = await GetImage(boardgameId, variant);
            return imageBytes != null;
        }

        public async Task<byte[]?> GetImage(int boardgameId, BoardgameImageVariant variant)
        {
            var file = GetFile(boardgameId, variant);

            var needsRefresh = file.Exists && await IsLocalCacheStale(boardgameId, variant);

            if (!file.Exists || needsRefresh)
            {
                var fetched = await FetchAndCacheImage(boardgameId, variant, file);
                if (fetched != null)
                    return fetched;

                if (file.Exists)
                    return await File.ReadAllBytesAsync(file.FullName);

                return null;
            }

            return await File.ReadAllBytesAsync(file.FullName);
        }

        public Task SaveImage(int boardgameId, BoardgameImageVariant variant, byte[] imageContent)
        {
            throw new InvalidOperationException("You cannot save images in dev mode.");
        }

        public Task DeleteImage(int boardgameId, BoardgameImageVariant variant)
        {
            var file = GetFile(boardgameId, variant);
            if (file.Exists) file.Delete();

            var versionFile = GetVersionFile(boardgameId, variant);
            if (versionFile.Exists) versionFile.Delete();

            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetImageModifiedDate(int boardgameId, BoardgameImageVariant variant)
        {
            var file = GetFile(boardgameId, variant);
            DateTimeOffset? result = file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc) : null;
            return Task.FromResult(result);
        }

        private async Task<byte[]?> FetchAndCacheImage(int boardgameId, BoardgameImageVariant variant, FileInfo file)
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
            {
                var sourceUrl = await GetSourceImageUrl(boardgameId, variant);
                if (string.IsNullOrWhiteSpace(sourceUrl))
                    return null;

                var sourceResponse = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, sourceUrl));
                if (sourceResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                sourceResponse.EnsureSuccessStatusCode();
                var sourceBytes = await sourceResponse.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(file.FullName, sourceBytes);
                await WriteLocalVersion(boardgameId, variant);
                return sourceBytes;
            }

            response.EnsureSuccessStatusCode();

            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(file.FullName, responseBytes);
            await WriteLocalVersion(boardgameId, variant);
            return responseBytes;
        }

        private async Task<string?> GetSourceImageUrl(int boardgameId, BoardgameImageVariant variant)
        {
            var boardgame = await movieDb.Boardgames
                .AsNoTracking()
                .Include(x => x.ImageDetails)
                .SingleOrDefaultAsync(x => x.id == boardgameId);

            if (boardgame == null)
                return null;

            return variant == BoardgameImageVariant.Main ? boardgame.ImageDetails?.ImageUrl : boardgame.ImageDetails?.ThumbnailUrl;
        }

        private async Task<bool> IsLocalCacheStale(int boardgameId, BoardgameImageVariant variant)
        {
            var currentVersion = await movieDb.BoardgameImageDetails
                .AsNoTracking()
                .Where(x => x.BoardgameId == boardgameId)
                .Select(x => (int?)x.ImageVersion)
                .SingleOrDefaultAsync() ?? 0;

            if (currentVersion <= 0)
                return false;

            var localVersion = await ReadLocalVersion(boardgameId, variant);
            return localVersion < currentVersion;
        }

        private async Task<int> ReadLocalVersion(int boardgameId, BoardgameImageVariant variant)
        {
            var versionFile = GetVersionFile(boardgameId, variant);
            if (!versionFile.Exists)
                return 0;

            var text = await File.ReadAllTextAsync(versionFile.FullName);
            return int.TryParse(text, out var parsed) ? parsed : 0;
        }

        private async Task WriteLocalVersion(int boardgameId, BoardgameImageVariant variant)
        {
            var currentVersion = await movieDb.BoardgameImageDetails
                .AsNoTracking()
                .Where(x => x.BoardgameId == boardgameId)
                .Select(x => (int?)x.ImageVersion)
                .SingleOrDefaultAsync() ?? 0;

            var versionFile = GetVersionFile(boardgameId, variant);
            await File.WriteAllTextAsync(versionFile.FullName, currentVersion.ToString());
        }

        private FileInfo GetVersionFile(int boardgameId, BoardgameImageVariant variant)
        {
            var baseFile = GetFile(boardgameId, variant);
            return new FileInfo(baseFile.FullName + ".version");
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