using Microsoft.Extensions.Options;
using System.IO;

namespace MovieTheater.Services.BoardgameImage
{
    public class BoardgamePdfRepository
    {
        private readonly DirectoryInfo rulesDir;

        public BoardgamePdfRepository(IOptions<LocalBoardgameImageOptions> options)
        {
            rulesDir = new DirectoryInfo(Path.Combine(options.Value.Directory.FullName, "rules"));
            if (!rulesDir.Exists)
                rulesDir.Create();
        }

        public bool HasPdf(int boardgameId) => GetFile(boardgameId).Exists;

        public async Task SavePdfAsync(int boardgameId, byte[] content)
            => await File.WriteAllBytesAsync(GetFile(boardgameId).FullName, content);

        public async Task<byte[]?> GetPdfAsync(int boardgameId)
        {
            var file = GetFile(boardgameId);
            return file.Exists ? await File.ReadAllBytesAsync(file.FullName) : null;
        }

        public void DeletePdf(int boardgameId)
        {
            var file = GetFile(boardgameId);
            if (file.Exists) file.Delete();
        }

        private FileInfo GetFile(int boardgameId)
            => new FileInfo(Path.Combine(rulesDir.FullName, $"{boardgameId}.pdf"));
    }
}
