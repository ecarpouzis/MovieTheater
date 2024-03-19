using Microsoft.Extensions.Logging;
using MovieTheater.Core;
using MovieTheater.Services.Python;
using SixLabors.ImageSharp.Processing;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MovieTheater.Services.Poster
{
    public class ImageShrinkService
    {
        private readonly IPosterImageRepository posterRepository;
        private readonly PythonClient pythonClient;
        private readonly ILogger<ImageShrinkService> logger;

        public ImageShrinkService(
            IPosterImageRepository posterRepository,
            PythonClient pythonClient,
            ILogger<ImageShrinkService> logger)
        {
            this.posterRepository = posterRepository;
            this.pythonClient = pythonClient;
            this.logger = logger;
        }

        public async Task EnsurePosterThumnailExists(int movieId)
        {
            var alreadyExists = await posterRepository.HasImage(movieId, PosterImageVariant.Thumbnail);
            if (alreadyExists)
            {
                logger.LogInformation("Thumbnail poster already exists for {movieId}. Skipping regen...", movieId);
                return;
            }

            var mainPosterExists = await posterRepository.HasImage(movieId, PosterImageVariant.Main);
            if (!mainPosterExists)
            {
                logger.LogWarning("Main poster doesn't exist for movie {movieId}, so we cannot generate the thumbnail.", movieId);
            }

            var mainPosterBytes = await posterRepository.GetImage(movieId, PosterImageVariant.Main);

            logger.LogInformation("Resizing poster for movieId={movieId}", movieId);

            byte[] thumbnailPosterBytes;

            try
            {
                thumbnailPosterBytes = ImageMagicResizeImage(mainPosterBytes);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error attempting to resize image. Running python image shrink...");

                try
                {
                    var pythonPosterBytes = await PythonShrinkImage(mainPosterBytes);
                    thumbnailPosterBytes = ImageMagicResizeImage(pythonPosterBytes);
                }
                catch (Exception e2)
                {
                    logger.LogError(e2, "Error attempting to resize image after python image shrink.");
                    throw;
                }
            }

            await posterRepository.SaveImage(movieId, PosterImageVariant.Thumbnail, thumbnailPosterBytes);
        }

        private async Task<byte[]> PythonShrinkImage(byte[] sourceImage)
        {
            using var mainPosterFile = CorePath.DisposableTempFile();
            await File.WriteAllBytesAsync(mainPosterFile.FileInfo.FullName, sourceImage);

            pythonClient.Exec("PILResaveImage.py", mainPosterFile.FileInfo.FullName);
            
            sourceImage = await File.ReadAllBytesAsync(mainPosterFile.FileInfo.FullName);

            return sourceImage;
        }

        private byte[] ImageMagicResizeImage(byte[] sourceImage)
        {
            using (SixLabors.ImageSharp.Image image = SixLabors.ImageSharp.Image.Load(sourceImage))
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


                SixLabors.ImageSharp.Formats.Png.PngEncoder png = new SixLabors.ImageSharp.Formats.Png.PngEncoder
                {
                    CompressionLevel = 0,
                    FilterMethod = SixLabors.ImageSharp.Formats.Png.PngFilterMethod.None
                };

                using (var ms = new MemoryStream())
                {
                    image.Save(ms, png);//Replace Png encoder with the file format of choice
                    return ms.ToArray();
                }
            }
        }
    }
}
