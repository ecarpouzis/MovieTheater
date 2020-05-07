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
using SixLabors.ImageSharp.Processing;
using System.Diagnostics;

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
                try
                {
                    ImageMagicResizeImage(poster.FullName, shrunkPoster.FullName);
                    return null;
                }
                catch
                {
                    run_cmd("python", "PILResaveImage.py", poster.FullName);
                    ImageMagicResizeImage(poster.FullName, shrunkPoster.FullName);
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        public void run_cmd(string cmd, string scriptName, string args)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = cmd;
            start.Arguments = string.Format("{0} \"{1}\"", scriptName, args);
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