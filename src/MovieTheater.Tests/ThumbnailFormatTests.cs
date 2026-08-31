using System;
using MovieTheater.Web;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The 2026-08-31 thumbnail format move, and the one thing that makes it safe: the serve path types an
    /// image by its CONTENT, so the PNGs already on the images mount and the WebP everything writes now can
    /// share a name and a URL.
    /// </summary>
    public class ThumbnailFormatTests
    {
        /// <summary>A photograph-ish source: smooth gradients + noise, which is what cover art looks like
        /// to an encoder and the reason PNG was the wrong container for it.</summary>
        private static byte[] PhotoLike(int w = 600, int h = 800)
        {
            using var img = new Image<Rgba32>(w, h);
            var rng = new Random(7);
            img.ProcessPixelRows(a =>
            {
                for (var y = 0; y < a.Height; y++)
                {
                    var row = a.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                        row[x] = new Rgba32(
                            (byte)((x * 255 / a.Width + rng.Next(12)) & 0xFF),
                            (byte)((y * 255 / a.Height + rng.Next(12)) & 0xFF),
                            (byte)(((x + y) * 255 / (a.Width + a.Height) + rng.Next(12)) & 0xFF));
                }
            });
            using var ms = new System.IO.MemoryStream();
            img.SaveAsPng(ms);
            return ms.ToArray();
        }

        [Fact]
        public void The_thumbnail_recipe_emits_webp_and_is_far_smaller_than_the_png_it_replaced()
        {
            var source = PhotoLike();
            var thumb = MovieTheater.Services.Poster.ImageShrinkService.ShrinkToThumbnail(source, new[] { .5f });

            Assert.Equal(MovieTheater.Web.ImageBytes.Webp, MovieTheater.Web.ImageBytes.ContentTypeOf(thumb));

            // The old recipe, byte for byte, so the comparison is the real one rather than a remembered
            // number: same resize, same sharpen, PNG at CompressionLevel 0 + FilterMethod None.
            using var image = Image.Load(source);
            image.Mutate(x => x.Resize(150, 200, KnownResamplers.Lanczos2));
            image.Mutate(x => x.GaussianSharpen(.5f));
            using var ms = new System.IO.MemoryStream();
            image.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder
            {
                CompressionLevel = 0,
                FilterMethod = SixLabors.ImageSharp.Formats.Png.PngFilterMethod.None,
            });
            var oldPng = ms.ToArray();

            // Measured on real cover art at 10x; a synthetic gradient compresses differently, so the test
            // asserts the ORDER OF MAGNITUDE rather than a number that would be brittle.
            Assert.True(thumb.Length * 3 < oldPng.Length,
                $"webp {thumb.Length} B should be far under png {oldPng.Length} B");

            // And it still decodes to the size the recipe promises.
            using var decoded = Image.Load(thumb);
            Assert.Equal(200, decoded.Height);
            Assert.True(decoded.Width <= 150);
        }

        [Fact]
        public void An_image_is_typed_by_its_magic_number_so_both_generations_serve_under_one_name()
        {
            var webp = MovieTheater.Services.Poster.ImageShrinkService.ShrinkToThumbnail(PhotoLike(), new[] { .5f });
            Assert.Equal(MovieTheater.Web.ImageBytes.Webp, MovieTheater.Web.ImageBytes.ContentTypeOf(webp));

            using var ms = new System.IO.MemoryStream();
            using (var img = Image.Load(PhotoLike())) img.SaveAsPng(ms);
            Assert.Equal(MovieTheater.Web.ImageBytes.Png, MovieTheater.Web.ImageBytes.ContentTypeOf(ms.ToArray()));

            using var jm = new System.IO.MemoryStream();
            using (var img = Image.Load(PhotoLike())) img.SaveAsJpeg(jm);
            Assert.Equal(MovieTheater.Web.ImageBytes.Jpeg, MovieTheater.Web.ImageBytes.ContentTypeOf(jm.ToArray()));

            Assert.Equal(MovieTheater.Web.ImageBytes.Gif, MovieTheater.Web.ImageBytes.ContentTypeOf(new byte[] { 0x47, 0x49, 0x46, 0x38 }));

            // Anything unrecognised or truncated keeps the site's historic behaviour rather than becoming a
            // download prompt: PNG is what these routes served for years.
            Assert.Equal(MovieTheater.Web.ImageBytes.Png, MovieTheater.Web.ImageBytes.ContentTypeOf(Array.Empty<byte>()));
            Assert.Equal(MovieTheater.Web.ImageBytes.Png, MovieTheater.Web.ImageBytes.ContentTypeOf(new byte[] { 1, 2, 3 }));
            // A RIFF container that is NOT WebP (a .wav) must not be called an image/webp.
            Assert.Equal(MovieTheater.Web.ImageBytes.Png, MovieTheater.Web.ImageBytes.ContentTypeOf(new byte[]
            {
                0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45,
            }));
        }
    }
}
