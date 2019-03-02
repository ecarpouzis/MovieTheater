using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MovieTheater.Db;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;

namespace MovieTheater.Services
{
    public class AzureImageHandler : IImageHandler
    {
        private readonly string AzureConnectionString;
        private const string posterBlobContainer = "movie-posters/";

        public AzureImageHandler(IOptions<AzureImageHandlerOptions> option)
        {
            AzureConnectionString = option.Value.BlobStorageConnectionString;
        }

        public async Task<byte[]> GetPosterImageFromID(int movieID)
        {
            if (CloudStorageAccount.TryParse(AzureConnectionString, out var storageAccount))
            {
                var blobClient = storageAccount.CreateCloudBlobClient();
                
                var blobReference = await blobClient.GetBlobReferenceFromServerAsync(new Uri(storageAccount.BlobStorageUri.PrimaryUri, posterBlobContainer + movieID + ".png"));
                if (blobReference != null)
                {
                    var bytes = new byte[blobReference.StreamWriteSizeInBytes];
                    await blobReference.DownloadToByteArrayAsync(bytes, 0);
                    return bytes;
                }
                else { return null; }
            }
            else
            {
                throw new Exception("Storage Account could not be parsed from Connection String");
            }
        }
    }
}