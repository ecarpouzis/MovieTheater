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

        public async Task SavePdfAsync(int boardgameId, int slot, byte[] content)
            => await File.WriteAllBytesAsync(GetFile(boardgameId, slot).FullName, content);

        public async Task<byte[]?> GetPdfAsync(int boardgameId, int slot)
        {
            var file = GetFile(boardgameId, slot);
            return file.Exists ? await File.ReadAllBytesAsync(file.FullName) : null;
        }

        public void DeleteFromSlot(int boardgameId, int fromSlot, int totalSlots)
        {
            for (int i = fromSlot; i < totalSlots; i++)
            {
                var file = GetFile(boardgameId, i);
                if (file.Exists) file.Delete();
            }
        }

        public void DeleteAndCompact(int boardgameId, int slotToRemove, int totalSlots)
        {
            var target = GetFile(boardgameId, slotToRemove);
            if (target.Exists) target.Delete();

            for (int i = slotToRemove + 1; i < totalSlots; i++)
            {
                var src = GetFile(boardgameId, i);
                var dst = GetFile(boardgameId, i - 1);
                if (src.Exists) src.MoveTo(dst.FullName, overwrite: true);
            }
        }

        private FileInfo GetFile(int boardgameId, int slot)
            => new FileInfo(Path.Combine(rulesDir.FullName, $"{boardgameId}_{slot}.pdf"));
    }
}
