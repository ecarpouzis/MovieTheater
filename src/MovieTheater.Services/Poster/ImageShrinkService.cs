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

        public async Task EnsurePosterThumnailExists(int movieId, bool force = false, string? bucket = null)
        {
            var alreadyExists = await posterRepository.HasImage(movieId, PosterImageVariant.Thumbnail, bucket);
            if (alreadyExists && !force)
            {
                logger.LogInformation("Thumbnail poster already exists for {movieId}. Skipping regen...", movieId);
                return;
            }

            var mainPosterExists = await posterRepository.HasImage(movieId, PosterImageVariant.Main, bucket);
            if (!mainPosterExists)
            {
                logger.LogWarning("Main poster doesn't exist for movie {movieId}, so we cannot generate the thumbnail.", movieId);
            }

            var mainPosterBytes = await posterRepository.GetImage(movieId, PosterImageVariant.Main, bucket);

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

            await posterRepository.SaveImage(movieId, PosterImageVariant.Thumbnail, thumbnailPosterBytes, bucket);
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
            // The poster chain runs five progressively-lighter sharpen passes.
            return ShrinkToThumbnail(sourceImage, new[] { .5f, .5f, .4f, .3f, .2f });
        }

        /// <summary>
        /// The thumbnail encode quality. 82 is the knee: at 150x200 and 300 px it is visually
        /// indistinguishable from the lossless original on cover art, and dropping further starts to show
        /// on flat title lettering. Measured at this setting: 125 KB PNG -> 12.9 KB.
        /// </summary>
        public const int ThumbnailQuality = 82;

        /// <summary>
        /// The site's ONE thumbnail recipe: scale to 200px high (capped at 150px wide, aspect
        /// preserved), Lanczos2, the given Gaussian-sharpen chain, then <b>WebP q82</b>. Shared by the
        /// poster pipeline (five-pass chain) and the boardgame image download (single .5f pass) - the two
        /// used to carry hand-kept copies with a "change both or neither" warning; now there is only one
        /// to change.
        ///
        /// <para>It wrote PNG at CompressionLevel 0 + FilterMethod None until 2026-08-31 — essentially raw
        /// RGB — on the reasoning that files are bigger on disk but encode fastest and "the wire cost is
        /// absorbed by the immutable/RAM caching on the image routes". Caching does nothing for the FIRST
        /// paint of a grid, which is the thing that reads as slow, and the measurement was stark: a cover
        /// served at 125 KB as PNG is 12.9 KB as WebP q82. Raising PNG's compression was NOT the fix
        /// (125 KB at level 6, 122 KB at level 9) — cover art is a photograph and PNG is the wrong
        /// container for one. Old PNGs on the mount keep working: the serve path reads the magic number
        /// (<see cref="MovieTheater.Web.ImageBytes"/>) instead of trusting a name or a default.</para>
        /// </summary>
        public static byte[] ShrinkToThumbnail(byte[] sourceImage, float[] sharpenPasses)
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
                foreach (var pass in sharpenPasses)
                    image.Mutate(x => x.GaussianSharpen(pass));

                var webp = new SixLabors.ImageSharp.Formats.Webp.WebpEncoder
                {
                    Quality = ThumbnailQuality,
                    FileFormat = SixLabors.ImageSharp.Formats.Webp.WebpFileFormatType.Lossy,
                };

                using (var ms = new MemoryStream())
                {
                    image.Save(ms, webp);
                    return ms.ToArray();
                }
            }
        }
    }
}
