using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Shared box-art fetching for the arcade: pulls a game's box art from the community
    /// libretro-thumbnails repos and downscales it to a small thumbnail. Used by both the bulk
    /// <c>arcade-boxart</c> CLI and the on-demand <c>/ArcadeImage/{id}</c> route, so the source-of-truth for
    /// the repo mapping + URL shape + thumbnail size lives in one place.
    /// </summary>
    public static class ArcadeBoxArt
    {
        // System code → libretro-thumbnails repo (display name; the GitHub repo swaps spaces for '_').
        // Arcade (fbneo) box art is named by MAME full title, not the shortname, so it's omitted here — those
        // cards keep the placeholder until a DAT-based mapping is added.
        public static readonly Dictionary<string, string> ThumbRepo = new()
        {
            ["nes"] = "Nintendo - Nintendo Entertainment System",
            ["snes"] = "Nintendo - Super Nintendo Entertainment System",
            ["genesis"] = "Sega - Mega Drive - Genesis",
            ["gb"] = "Nintendo - Game Boy",
            ["gbc"] = "Nintendo - Game Boy Color",
            ["gba"] = "Nintendo - Game Boy Advance",
            ["n64"] = "Nintendo - Nintendo 64",
            ["ps1"] = "Sony - PlayStation",
        };

        public static bool HasRepo(string system) => ThumbRepo.ContainsKey(system);

        /// <summary>Fetch + downscale box art for a game; null if the system has no repo, the art doesn't
        /// exist, or the download isn't a valid PNG. Never throws.</summary>
        public static async Task<byte[]?> TryFetchThumbnailAsync(HttpClient http, string system, string cloudRetroGameKey, int thumbPx)
        {
            if (!ThumbRepo.TryGetValue(system, out var repo)) return null;
            // Repos use underscores for spaces (Nintendo_-_Nintendo_64); the FILENAME keeps spaces (encoded).
            var url = $"https://raw.githubusercontent.com/libretro-thumbnails/{repo.Replace(' ', '_')}/master/Named_Boxarts/{Uri.EscapeDataString(LibretroName(cloudRetroGameKey))}.png";
            var png = await TryDownloadPng(http, url);
            return png == null ? null : Thumbnail(png, thumbPx);
        }

        // libretro-thumbnails replaces these characters in the ROM name with '_': & * / : ` < > ? \ | "
        public static string LibretroName(string name)
        {
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if ("&*/:`<>?\\|\"".IndexOf(chars[i]) >= 0) chars[i] = '_';
            return new string(chars);
        }

        // A hit is a real PNG (200 + PNG magic), so a repo's 404 HTML page is never mistaken for art.
        private static async Task<byte[]?> TryDownloadPng(HttpClient http, string url)
        {
            try
            {
                using var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
                    return null;
                return bytes;
            }
            catch { return null; }
        }

        // Downscale so ~49k games stay small: full art (~300-500 KB) → a ~220px thumbnail (~12-20 KB).
        public static byte[] Thumbnail(byte[] source, int maxDim)
        {
            using var img = Image.Load(source);
            int max = Math.Max(img.Width, img.Height);
            if (max > maxDim)
            {
                double s = (double)maxDim / max;
                img.Mutate(x => x.Resize(Math.Max(1, (int)Math.Round(img.Width * s)), Math.Max(1, (int)Math.Round(img.Height * s))));
            }
            using var ms = new MemoryStream();
            img.Save(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
            return ms.ToArray();
        }
    }
}
