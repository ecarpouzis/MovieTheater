using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MovieTheater.Db;
using System.IO;
using System.Net;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PhotoSauce.MagicScaler;

namespace MovieTheater.Services
{
    public class LocalImageHandler : IImageHandler
    {
        private readonly string localFileDirectory;

        public LocalImageHandler(IOptions<LocalImageHandlerOptions> option)
        {
            localFileDirectory = option.Value.LocalStorageFileDirectory;
        }

        public async Task<byte[]> GetPosterImageFromID(int movieID, bool getThumb = false)
        {
            DirectoryInfo posterDir = new DirectoryInfo(localFileDirectory);

            if (!posterDir.Exists)
            {
                return null;
            }

            FileInfo poster;

            if (!getThumb)
            {
                poster = new FileInfo(Path.Combine(posterDir.FullName, movieID + ".png"));
            }
            else
            {
                poster = new FileInfo(Path.Combine(posterDir.FullName, movieID + "_s.png"));
            }

            if (poster.Exists)
            {
                return await File.ReadAllBytesAsync(poster.FullName);
            }
            else
            {
                return null;
            }
        }

        public async Task<byte[]> ShrinkPosterFromID(int movieID)
        {
            DirectoryInfo posterDir = new DirectoryInfo(localFileDirectory);

            if (!posterDir.Exists)
            {
                return null;
            }

            FileInfo poster = new FileInfo(Path.Combine(posterDir.FullName, movieID + ".png"));
            FileInfo shrunkPoster = new FileInfo(Path.Combine(posterDir.FullName, movieID + "_s.png"));
            if (poster.Exists)
            {
                MagicScalerResizeImage(poster.FullName, shrunkPoster.FullName);
                return null;
            }
            else
            {
                return null;
            }
        }

        public void MagicScalerResizeImage(string imgSrc, string imgDest)
        {
            var settings = new ProcessImageSettings
            {
                Height = 200,
                SaveFormat = FileFormat.Png,
                BlendingMode = GammaMode.Linear,
                Interpolation = InterpolationSettings.Lanczos,
                ResizeMode = CropScaleMode.Stretch,
                HybridMode = HybridScaleMode.FavorQuality
            };

            var settingsSetWidth = new ProcessImageSettings
            {
                Height = 200,
                Width = 150,
                SaveFormat = FileFormat.Png,
                BlendingMode = GammaMode.Linear,
                Interpolation = InterpolationSettings.Lanczos,
                ResizeMode = CropScaleMode.Stretch,
                HybridMode = HybridScaleMode.FavorQuality
            };

            ProcessImageResult st;

            using (var os = new FileStream(imgDest, FileMode.Create)) { st = MagicImageProcessor.ProcessImage(imgSrc, os, settings); }

            if (st.Settings.Width > 150)
            {
                using (var os = new FileStream(imgDest, FileMode.Create)) { st = MagicImageProcessor.ProcessImage(imgSrc, os, settingsSetWidth); }
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
                await ShrinkPosterFromID(movieID);
            }
            catch
            {
                return null;
            }
                
            return posterLoc.FullName;
        }

    }
}