using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MovieTheater.Db;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using System.IO;
using System.Net;

namespace MovieTheater.Services
{
    public class LocalImageHandler : IImageHandler
    {
        private readonly string localFileDirectory;

        public LocalImageHandler(IOptions<LocalImageHandlerOptions> option)
        {
            localFileDirectory = option.Value.LocalStorageFileDirectory;
        }

        public async Task<byte[]> GetPosterImageFromID(int movieID)
        {
            DirectoryInfo posterDir = new DirectoryInfo(localFileDirectory);

            if (!posterDir.Exists)
            {
                return null;
            }

            FileInfo poster = new FileInfo(Path.Combine(posterDir.FullName, movieID + ".png"));

            if (poster.Exists)
            {
                return await File.ReadAllBytesAsync(poster.FullName);
            }
            else
            {
                return null;
            }
        }

        public async Task<string> SavePosterImageFromLink(int movieID, string url)
        {
            DirectoryInfo posterDir = new DirectoryInfo(localFileDirectory);

            if (!posterDir.Exists)
            {
                return null;
            }


            WebClient client = new WebClient();
            byte[] potentialPoster = new byte[0];

            potentialPoster = client.DownloadData(url);
            FileInfo posterLoc = new FileInfo(Path.Combine(posterDir.FullName, movieID + ".png"));

            try
            {
                await File.WriteAllBytesAsync(posterLoc.FullName, potentialPoster);
            }
            catch
            {
                return null;
            }
            return posterLoc.FullName;

            //Create a MoviePoster object for MoviePoster table insertion
            //MoviePoster posterInsert = movieDb.MoviePosters.FirstOrDefault(m => m.MovieID == givenID);
            //if (posterInsert == null)
            //{
            //    posterInsert = new MoviePoster();
            //    posterInsert.MovieID = givenID;
            //    movieDb.MoviePosters.Add(posterInsert);
            //    movieDb.SaveChanges();
            //}

            //posterInsert.Poster = potentialPoster;
            //posterInsert.MovieID = givenID;
            //FileInfo poster = new FileInfo(Path.Combine(posterDir.FullName, movieID + ".png"));
            //if (poster.Exists)
            //{
            //    return await File.ReadAllBytesAsync(poster.FullName);
            //}
            //else
            //{
            //    return null;
            //}

        }

    }
}