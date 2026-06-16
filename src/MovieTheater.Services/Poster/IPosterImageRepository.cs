using System;
using System.Threading.Tasks;

namespace MovieTheater.Services.Poster
{
    public interface IPosterImageRepository
    {
        // The Movie/Series poster id space is shared (a given id is a Movie OR a Series), so their
        // posters live in one flat namespace keyed by id. Titles with a DISJOINT id space — MiscVideo,
        // whose ids overlap Movie/Series — pass a non-null <paramref name="bucket"/> ("misc") so their
        // files are stored/served under a prefixed name and never collide. Null = the default namespace.
        Task<bool> HasImage(int movieId, PosterImageVariant variant, string? bucket = null);
        Task<byte[]> GetImage(int movieId, PosterImageVariant variant, string? bucket = null);
        Task SaveImage(int movieId, PosterImageVariant variant, byte[] imageContent, string? bucket = null);
        Task<DateTimeOffset?> GetImageModifiedDate(int movieId, PosterImageVariant variant, string? bucket = null);
    }
}
