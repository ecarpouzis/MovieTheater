using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MovieTheater.Db;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using System.IO;

namespace MovieTheater.Services
{
    public class LocalImageHandler : IImageHandler
    {
        private readonly string localFileDirectory;
        private readonly AzureImageHandler azureImageHandler;
        public LocalImageHandler(IOptions<LocalImageHandlerOptions> option, AzureImageHandler injectedImageHandler)
        {
            localFileDirectory = option.Value.LocalStorageFileDirectory;
            azureImageHandler = injectedImageHandler;
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
                var azureDownloadedPoster = await azureImageHandler.GetPosterImageFromID(movieID);
                if (azureDownloadedPoster == null)
                {
                    return null;
                }

                await File.WriteAllBytesAsync(poster.FullName, azureDownloadedPoster);

                return azureDownloadedPoster;
            }
        }
    }
}