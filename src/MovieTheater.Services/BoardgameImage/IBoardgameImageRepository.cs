using System;
using System.Threading.Tasks;

namespace MovieTheater.Services.BoardgameImage
{
    public interface IBoardgameImageRepository
    {
        Task<bool> HasImage(int boardgameId, BoardgameImageVariant variant);
        Task<byte[]> GetImage(int boardgameId, BoardgameImageVariant variant);
        Task SaveImage(int boardgameId, BoardgameImageVariant variant, byte[] imageContent);
        Task DeleteImage(int boardgameId, BoardgameImageVariant variant);
        Task<DateTimeOffset?> GetImageModifiedDate(int boardgameId, BoardgameImageVariant variant);
    }
}
