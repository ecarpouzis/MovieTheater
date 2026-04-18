using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MovieTheater.Services.BoardgameImage
{
    public class LocalBoardgameImageRepository : IBoardgameImageRepository
    {
        private readonly LocalBoardgameImageOptions options;

        public LocalBoardgameImageRepository(IOptions<LocalBoardgameImageOptions> options)
        {
            this.options = options.Value;
        }

        public Task<bool> HasImage(int boardgameId, BoardgameImageVariant variant)
        {
            var file = GetFile(boardgameId, variant);
            return Task.FromResult(file.Exists);
        }

        public async Task<byte[]> GetImage(int boardgameId, BoardgameImageVariant variant)
        {
            var file = GetFile(boardgameId, variant);

            if (file.Exists)
            {
                return await File.ReadAllBytesAsync(file.FullName);
            }
            else
            {
                return null;
            }
        }

        public async Task SaveImage(int boardgameId, BoardgameImageVariant variant, byte[] imageContent)
        {
            var file = GetFile(boardgameId, variant);

            await File.WriteAllBytesAsync(file.FullName, imageContent);
        }

        public Task DeleteImage(int boardgameId, BoardgameImageVariant variant)
        {
            var file = GetFile(boardgameId, variant);
            if (file.Exists) file.Delete();
            return Task.CompletedTask;
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
