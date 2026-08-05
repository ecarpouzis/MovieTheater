using System;
using System.IO;
using MovieTheater.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Music
{
    /// <summary>
    /// Where album art lives on disk and how it's shaped (music-plan.md §2.5). Shared by the
    /// <c>music-art</c> CLI (writes) and <c>MusicImageController</c> (reads) so the two can't drift
    /// on a filename.
    ///
    /// <para>Naming follows the poster-bucket convention (<c>PosterBucket</c>): a prefixed filename
    /// keeps a disjoint id space out of the movie/series namespace — <c>music_{albumId}.png</c> and
    /// <c>music_{albumId}_s.png</c>. The directory is <c>MusicImagesDir</c> when configured, else the
    /// posters mount (already persistent in prod), which is why the prefix matters.</para>
    ///
    /// <para>Resizing is aspect-preserving here rather than going through
    /// <c>ImageShrinkService</c>: that service is hard-coded to the movie-poster shape (200px tall,
    /// ≤150px wide) and would squash square album art into a portrait rectangle.</para>
    /// </summary>
    public static class MusicArtStore
    {
        /// <summary>Longest edge of the stored full-size art — plenty for the modal hero and the
        /// Now Playing page without keeping 3000px scans on the mount.</summary>
        public const int MainMaxPx = 600;

        /// <summary>Longest edge of the thumbnail — fills the ~150px grid tile crisply on 2× screens.</summary>
        public const int ThumbMaxPx = 300;

        /// <summary>Image extensions the folder-art scan will consider.</summary>
        public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

        /// <summary>Filename stems that are conventionally THE cover of an album folder, best first.</summary>
        public static readonly string[] PreferredStems = { "cover", "folder", "front", "album", "albumart", "albumartsmall" };

        public static string? ResolveDir(MovieTheaterConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(config.MusicImagesDir))
                return Path.GetFullPath(config.MusicImagesDir);
            if (!string.IsNullOrWhiteSpace(config.MoviePostersDir))
                return Path.GetFullPath(config.MoviePostersDir);
            return null;
        }

        /// <summary>The bare filename for one album's art. Pure + deterministic — unit-tested.</summary>
        public static string FileName(int albumId, bool thumbnail) =>
            thumbnail ? $"music_{albumId}_s.png" : $"music_{albumId}.png";

        public static string? PathFor(MovieTheaterConfiguration config, int albumId, bool thumbnail)
        {
            var dir = ResolveDir(config);
            return dir == null ? null : Path.Combine(dir, FileName(albumId, thumbnail));
        }

        /// <summary>Decode → downscale (never upscale) → PNG. Returns null when the bytes aren't a
        /// decodable image, which is the common case for a stray "art" file in a folder.</summary>
        public static byte[]? Downscale(byte[] source, int maxDim)
        {
            try
            {
                using var img = Image.Load(source);
                int max = Math.Max(img.Width, img.Height);
                if (max > maxDim)
                {
                    double s = (double)maxDim / max;
                    img.Mutate(x => x.Resize(
                        Math.Max(1, (int)Math.Round(img.Width * s)),
                        Math.Max(1, (int)Math.Round(img.Height * s))));
                }
                using var ms = new MemoryStream();
                img.Save(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Per-pixel mean of the opaque pixels, "#RRGGBB" — the same computation the poster
        /// pipeline uses for its dominant color, replicated here because that one is private.</summary>
        public static string? ComputeAverageColor(byte[] imageBytes)
        {
            try
            {
                using var image = Image.Load<Rgba32>(imageBytes);
                long r = 0, g = 0, b = 0, n = 0;
                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            var p = row[x];
                            if (p.A < 128) continue;
                            r += p.R; g += p.G; b += p.B; n++;
                        }
                    }
                });
                return n == 0 ? null : $"#{r / n:X2}{g / n:X2}{b / n:X2}";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Picks the cover file out of an album folder: a conventional stem
        /// (cover/folder/front/…) first, else the largest image by byte size. Returns null when the
        /// folder holds no image at all. Does not recurse — CD1/CD2 subfolders rarely carry the cover
        /// and a recursive walk over a network share is the expensive thing here.</summary>
        public static string? FindFolderImage(string albumDir)
        {
            if (!Directory.Exists(albumDir)) return null;

            FileInfo? largest = null;
            var best = new FileInfo[PreferredStems.Length];
            foreach (var path in Directory.EnumerateFiles(albumDir))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (Array.IndexOf(ImageExtensions, ext) < 0) continue;

                var fi = new FileInfo(path);
                if (largest == null || fi.Length > largest.Length) largest = fi;

                var stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                for (int i = 0; i < PreferredStems.Length; i++)
                {
                    if (stem == PreferredStems[i] && best[i] == null) best[i] = fi;
                }
            }

            foreach (var candidate in best)
                if (candidate != null) return candidate.FullName;
            return largest?.FullName;
        }
    }
}
