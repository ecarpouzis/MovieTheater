using System.Threading.Tasks;

namespace MovieTheater.Services.Poster
{
    public interface IPosterImageRepository
    {
        Task<bool> HasImage(int movieId, PosterImageVariant variant);
        Task<byte[]> GetImage(int movieId, PosterImageVariant variant);
        Task SaveImage(int movieId, PosterImageVariant variant, byte[] imageContent);
    }
}
