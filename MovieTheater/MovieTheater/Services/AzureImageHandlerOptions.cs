using System;
using System.Threading.Tasks;
using MovieTheater.Db;

namespace MovieTheater.Services
{
    public class AzureImageHandlerOptions
    {
		public string BlobStorageConnectionString {get; set;}
    }
}