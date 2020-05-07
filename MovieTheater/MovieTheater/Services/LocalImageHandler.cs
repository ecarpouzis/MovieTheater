using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp.Processing;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace MovieTheater.Services
{
    public class LocalImageHandler : IImageHandler
    {
        private readonly string pyPath;

        private readonly string localFileDirectory;

        private readonly ILogger<LocalImageHandler> logger;

        public LocalImageHandler(IOptions<LocalImageHandlerOptions> options, ILogger<LocalImageHandler> logger)
        {
            pyPath = options.Value.PyPath;
            localFileDirectory = options.Value.LocalStorageFileDirectory;

            this.logger = logger;
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

        public void CreateShrunkImageFile(int movieID)
        {
            DirectoryInfo posterDir = new DirectoryInfo(localFileDirectory);

            if (!posterDir.Exists)
            {
                return;
            }

            FileInfo poster = new FileInfo(Path.Combine(posterDir.FullName, movieID + ".png"));
            FileInfo shrunkPoster = new FileInfo(Path.Combine(posterDir.FullName, movieID + "_s.png"));

            if (!poster.Exists)
            {
                logger.LogWarning("No poster exists for movieID={movieId}", movieID);
                return;
            }

            logger.LogInformation("Resizing poster for movieId={movieId}", movieID);

            try
            {
                ImageMagicResizeImage(poster.FullName, shrunkPoster.FullName);
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error attempting to resize image. Running python image shrink...");

                try
                {
                    PythonShrinkImage(poster.FullName);
                    ImageMagicResizeImage(poster.FullName, shrunkPoster.FullName);
                }
                catch (Exception e2)
                {
                    logger.LogError(e2, "Error attempting to resize image after python image shrink.");
                }
            }
        }

        private void PythonShrinkImage(string filePath)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = pyPath;
            start.Arguments = $"PILResaveImage.py \"{filePath}\"";
            start.UseShellExecute = true;// Do not use OS shell
            start.CreateNoWindow = true; // We don't need new window
            Process.Start(start).WaitForExit();
        }


        public void ImageMagicResizeImage(string imgSrc, string imgDest)
        {
            using (SixLabors.ImageSharp.Image image = SixLabors.ImageSharp.Image.Load(imgSrc))
            {
                float originalHeight = image.Height;
                float originalWidth = image.Width;
                //I want my final image to be 200px high
                float calcHeight = 200f;
                //I want my final image to be no more than 150px wide
                int maxWidth = 150;
                //200f/400f = .5f (or 50%)
                float changedPerc = calcHeight / originalHeight;
                //Final width is .5 * 250 = 125px
                float calcWidth = changedPerc * originalWidth;
                int finalWidth = (int)Math.Round(calcWidth);
                int finalHeight = (int)Math.Round(calcHeight);
                if (finalWidth > maxWidth)
                {
                    finalWidth = maxWidth;
                }

                image.Mutate(x => x.Resize(finalWidth, finalHeight, KnownResamplers.Lanczos2));
                image.Mutate(x => x.GaussianSharpen(.5f));
                image.Mutate(x => x.GaussianSharpen(.5f));
                image.Mutate(x => x.GaussianSharpen(.4f));
                image.Mutate(x => x.GaussianSharpen(.3f));
                image.Mutate(x => x.GaussianSharpen(.2f));


                SixLabors.ImageSharp.Formats.Png.PngEncoder png = new SixLabors.ImageSharp.Formats.Png.PngEncoder();
                png.CompressionLevel = 0;
                png.FilterMethod = SixLabors.ImageSharp.Formats.Png.PngFilterMethod.None;

                using (var os = new FileStream(imgDest, FileMode.Create))
                {
                    image.Save(os, png);//Replace Png encoder with the file format of choice
                }
            }
        }

        //public void MagicScalerResizeImage(string imgSrc, string imgDest)
        //{
        //    var settings = new ProcessImageSettings
        //    {
        //        Height = 200,
        //        SaveFormat = FileFormat.Png,
        //        BlendingMode = GammaMode.Linear,
        //        Interpolation = InterpolationSettings.Lanczos,
        //        ResizeMode = CropScaleMode.Stretch,
        //        HybridMode = HybridScaleMode.FavorQuality
        //    };

        //    var settingsSetWidth = new ProcessImageSettings
        //    {
        //        Height = 200,
        //        Width = 150,
        //        SaveFormat = FileFormat.Png,
        //        BlendingMode = GammaMode.Linear,
        //        Interpolation = InterpolationSettings.Lanczos,
        //        ResizeMode = CropScaleMode.Stretch,
        //        HybridMode = HybridScaleMode.FavorQuality
        //    };

        //    ProcessImageResult st;

        //    using (var os = new FileStream(imgDest, FileMode.Create)) { st = MagicImageProcessor.ProcessImage(imgSrc, os, settings); }

        //    if (st.Settings.Width > 150)
        //    {
        //        using (var os = new FileStream(imgDest, FileMode.Create)) { st = MagicImageProcessor.ProcessImage(imgSrc, os, settingsSetWidth); }
        //    }
        //}


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