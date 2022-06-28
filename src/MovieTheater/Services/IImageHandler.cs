using System.IO;
using System.Threading.Tasks;

namespace MovieTheater.Services
{
    public interface IImageHandler
    {
        FileInfo GetPosterFile(int movieId);

        FileInfo GetShrunkPosterFile(int movieId);

        /// <summary>
        /// Pass in a movie ID and get the contents of the poster's image.
        /// 
        /// In the future, handle memory streaming, potentially multiple posters?
        /// </summary>
        /// <param name="movieID"></param>
        /// <returns></returns>
        Task<byte[]> GetPosterImageFromID(int movieID, bool getThumb = false);

        Task Initialize();

        void CreateShrunkImageFile(int movieID);
        Task<string> CheckForServerPosters();

        Task<string> SavePosterImageFromLink(int movieID, string url);
    }
}