using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MovieTheater.Services.Poster
{
    public class LocalPosterImageRepository : IPosterImageRepository
    {
        private readonly LocalPosterImageOptions options;

        public LocalPosterImageRepository(IOptions<LocalPosterImageOptions> options)
        {
            this.options = options.Value;
        }

        public Task<bool> HasImage(int movieId, PosterImageVariant variant, string? bucket = null)
        {
            var file = GetFile(movieId, variant, bucket);
            return Task.FromResult(file.Exists);
        }

        public async Task<byte[]> GetImage(int movieId, PosterImageVariant variant, string? bucket = null)
        {
            var file = GetFile(movieId, variant, bucket);

            if (file.Exists)
            {
                return await File.ReadAllBytesAsync(file.FullName);
            }
            else
            {
                return null;
            }
        }

        public async Task SaveImage(int movieId, PosterImageVariant variant, byte[] imageContent, string? bucket = null)
        {
            var file = GetFile(movieId, variant, bucket);

            await File.WriteAllBytesAsync(file.FullName, imageContent);
        }

        public Task<DateTimeOffset?> GetImageModifiedDate(int movieId, PosterImageVariant variant, string? bucket = null)
        {
            var file = GetFile(movieId, variant, bucket);
            DateTimeOffset? result = file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc) : null;
            return Task.FromResult(result);
        }

        // bucket (e.g. "misc") prefixes the filename so a disjoint id space can't collide with the
        // shared Movie/Series posters: "{bucket}_{id}.png". Null = the default Movie/Series namespace.
        private FileInfo GetFile(int movieId, PosterImageVariant variant, string? bucket = null)
        {
            var prefix = string.IsNullOrEmpty(bucket) ? "" : bucket + "_";
            string path;

            if (variant == PosterImageVariant.Main)
            {
                path = Path.Combine(options.Directory.FullName, prefix + movieId + ".png");
            }
            else if (variant == PosterImageVariant.Thumbnail)
            {
                path = Path.Combine(options.Directory.FullName, prefix + movieId + "_s.png");
            }
            else
            {
                throw new InvalidOperationException($"Unrecognized PosterImageVariant: \"{variant}\" ({(int)variant})");
            }

            return new FileInfo(path);
        }
    }
}
