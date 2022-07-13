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

        public Task<bool> HasImage(int movieId, PosterImageVariant variant)
        {
            var file = GetFile(movieId, variant);
            return Task.FromResult(file.Exists);
        }

        public async Task<byte[]> GetImage(int movieId, PosterImageVariant variant)
        {
            var file = GetFile(movieId, variant);

            if (file.Exists)
            {
                return await File.ReadAllBytesAsync(file.FullName);
            }
            else
            {
                return null;
            }
        }

        public async Task SaveImage(int movieId, PosterImageVariant variant, byte[] imageContent)
        {
            var file = GetFile(movieId, variant);

            await File.WriteAllBytesAsync(file.FullName, imageContent);
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
