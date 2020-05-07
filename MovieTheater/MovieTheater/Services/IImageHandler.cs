using System;
using System.Threading.Tasks;
using MovieTheater.Db;

namespace MovieTheater.Services
{
    public interface IImageHandler
    {
        /// <summary>
        /// Pass in a movie ID and get the contents of the poster's image.
        /// 
        /// In the future, handle memory streaming, potentially multiple posters?
        /// </summary>
        /// <param name="movieID"></param>
        /// <returns></returns>
        Task<byte[]> GetPosterImageFromID(int movieID, bool getThumb = false);

        void CreateShrunkImageFile(int movieID);

        Task<string> SavePosterImageFromLink(int movieID, string url);
    }
}