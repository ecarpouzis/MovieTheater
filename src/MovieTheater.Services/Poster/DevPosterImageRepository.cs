using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieTheater.Db;
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
        private readonly MovieDb movieDb;

        public DevPosterImageRepository(HttpClient httpClient, IOptions<LocalPosterImageOptions> options, MovieDb movieDb)
        {
            this.httpClient = httpClient;
            this.options = options.Value;
            this.movieDb = movieDb;
        }

        public async Task<bool> HasImage(int movieId, PosterImageVariant variant)
        {
            var file = GetFile(movieId, variant);

            if (file.Exists)
            {
                if (!await IsLocalCacheStale(movieId, variant))
                    return true;
            }

            // If file doesn't exist (or cache is stale), try to get it by downloading
            var imageBytes = await GetImage(movieId, variant);
            return imageBytes != null;
        }

        public async Task<byte[]?> GetImage(int movieId, PosterImageVariant variant)
        {
            var file = GetFile(movieId, variant);

            var needsRefresh = file.Exists && await IsLocalCacheStale(movieId, variant);

            if (!file.Exists || needsRefresh)
            {
                var fetched = await FetchAndCacheImage(movieId, variant, file);
                if (fetched != null)
                    return fetched;

                if (file.Exists)
                    return await File.ReadAllBytesAsync(file.FullName);

                return null;
            }

            return await File.ReadAllBytesAsync(file.FullName);
        }

        public Task SaveImage(int movieId, PosterImageVariant variant, byte[] imageContent)
        {
            throw new InvalidOperationException("You cannot save images in dev mode.");
        }

        public Task<DateTimeOffset?> GetImageModifiedDate(int movieId, PosterImageVariant variant)
        {
            var file = GetFile(movieId, variant);
            DateTimeOffset? result = file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc) : null;
            return Task.FromResult(result);
        }

        private async Task<byte[]?> FetchAndCacheImage(int movieId, PosterImageVariant variant, FileInfo file)
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
            await WriteLocalVersion(movieId, variant);
            return responseBytes;
        }

        private async Task<bool> IsLocalCacheStale(int movieId, PosterImageVariant variant)
        {
            var currentVersion = await movieDb.MoviePosterDetails
                .AsNoTracking()
                .Where(x => x.MovieId == movieId)
                .Select(x => (int?)x.PosterVersion)
                .SingleOrDefaultAsync() ?? 0;

            if (currentVersion <= 0)
                return false;

            var localVersion = await ReadLocalVersion(movieId, variant);
            return localVersion < currentVersion;
        }

        private async Task<int> ReadLocalVersion(int movieId, PosterImageVariant variant)
        {
            var versionFile = GetVersionFile(movieId, variant);
            if (!versionFile.Exists)
                return 0;

            var text = await File.ReadAllTextAsync(versionFile.FullName);
            return int.TryParse(text, out var parsed) ? parsed : 0;
        }

        private async Task WriteLocalVersion(int movieId, PosterImageVariant variant)
        {
            var currentVersion = await movieDb.MoviePosterDetails
                .AsNoTracking()
                .Where(x => x.MovieId == movieId)
                .Select(x => (int?)x.PosterVersion)
                .SingleOrDefaultAsync() ?? 0;

            var versionFile = GetVersionFile(movieId, variant);
            await File.WriteAllTextAsync(versionFile.FullName, currentVersion.ToString());
        }

        private FileInfo GetVersionFile(int movieId, PosterImageVariant variant)
        {
            var baseFile = GetFile(movieId, variant);
            return new FileInfo(baseFile.FullName + ".version");
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
                path = Path.Combine(options.Directory.FullName, movieId + "_s.png");
            }
            else
            {
                throw new InvalidOperationException($"Unrecognized PosterImageVariant: \"{variant}\" ({(int)variant})");
            }

            return new FileInfo(path);
        }
    }
}
