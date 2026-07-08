using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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
            ["gc"] = "Nintendo - GameCube",
            ["ps1"] = "Sony - PlayStation",
            // Added 2026-07. naomi/atomiswave/neogeo are arcade-named (like fbneo) → omitted, they keep
            // the placeholder. A repo-name miss is a cosmetic non-error (placeholder card), never fatal.
            ["psp"] = "Sony - PlayStation Portable",
            ["dc"] = "Sega - Dreamcast",
            ["sms"] = "Sega - Master System - Mark III",
            ["gg"] = "Sega - Game Gear",
            ["sg1000"] = "Sega - SG-1000",
            ["segacd"] = "Sega - Mega-CD - Sega CD",
            ["sega32x"] = "Sega - 32X",
            ["pce"] = "NEC - PC Engine - TurboGrafx 16",
            ["ngpc"] = "SNK - Neo Geo Pocket Color",
            ["wsc"] = "Bandai - WonderSwan Color",
            ["a2600"] = "Atari - 2600",
            ["a7800"] = "Atari - 7800",
            ["lynx"] = "Atari - Lynx",
            ["vb"] = "Nintendo - Virtual Boy",
            ["fds"] = "Nintendo - Family Computer Disk System",
        };

        public static bool HasRepo(string system) => ThumbRepo.ContainsKey(system);

        /// <summary>Fetch + downscale box art for a single ROM key (legacy shape — exact-name only). Prefer
        /// <see cref="TryFetchThumbnailForCardAsync"/>, which matches by title across a card's versions.</summary>
        public static async Task<byte[]?> TryFetchThumbnailAsync(HttpClient http, string system, string cloudRetroGameKey, int thumbPx)
        {
            if (!ThumbRepo.ContainsKey(system)) return null;
            var png = await TryDownloadFirst(http, system, new[] { LibretroName(cloudRetroGameKey) });
            return png == null ? null : Thumbnail(png, thumbPx);
        }

        /// <summary>Fetch + downscale box art for a whole CARD (one game, its several ROM versions). Tries, in
        /// order: each version's exact ROM name (precise for cleanly-named systems), the index match if the
        /// system's filename index is present (catches word-order / region-tag / TOSEC drift), then title +
        /// common region-tag guesses. Returns the first valid PNG, downscaled; null on a clean miss. Never
        /// throws.</summary>
        /// <param name="title">The card's display title (tags already stripped).</param>
        /// <param name="regions">The card's version regions (used to prefer a regional box).</param>
        /// <param name="cloudRetroGameKeys">The raw ROM launch keys of the card's versions.</param>
        /// <param name="index">Optional per-system filename index (from <see cref="ArcadeBoxArtIndex"/>).</param>
        public static async Task<byte[]?> TryFetchThumbnailForCardAsync(
            HttpClient http, string system, string title, IEnumerable<string?> regions,
            IEnumerable<string> cloudRetroGameKeys, int thumbPx, ArcadeBoxArtIndex? index)
        {
            if (!ThumbRepo.ContainsKey(system)) return null;
            var candidates = BuildCandidates(system, title, regions, cloudRetroGameKeys, index);
            var png = await TryDownloadFirst(http, system, candidates);
            return png == null ? null : Thumbnail(png, thumbPx);
        }

        /// <summary>Ordered, de-duplicated list of libretro filename candidates (without .png) for a card.</summary>
        public static List<string> BuildCandidates(
            string system, string title, IEnumerable<string?> regions, IEnumerable<string> keys, ArcadeBoxArtIndex? index)
        {
            var regionList = (regions ?? Enumerable.Empty<string?>()).ToList();
            var keyList = (keys ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            var ordered = new List<string>();

            // 0. Hand-curated alias — the authoritative libretro name for a card the auto-matcher can't reach.
            var alias = ArcadeBoxArtAliases.For(system, title);
            if (alias != null) ordered.Add(alias);

            // 1. Exact ROM names — a precise hit for No-Intro/Redump-named systems (snes, nes, gba, …).
            foreach (var k in keyList) ordered.Add(LibretroName(k));

            // 2. Index match — resolves drift the exact name can't (SMS/GG region+language tags, Dreamcast
            //    TOSEC names, "007 - GoldenEye" ⇄ "GoldenEye 007").
            var hit = index?.Match(title, regionList);
            if (hit != null) ordered.Add(hit);

            // 3. Title + region-tag guesses — a network fallback when no index is present. Try the card's own
            //    regions first, then the usual English-first tags, then a "NNN - X" → "X NNN" swap and bare.
            foreach (var tag in RegionTags(regionList))
                ordered.Add(LibretroName(title + " " + tag));
            var swap = Regex.Match(title, @"^(\d+)\s*-\s*(.+)$");
            if (swap.Success)
            {
                var swapped = swap.Groups[2].Value.Trim() + " " + swap.Groups[1].Value;
                foreach (var tag in RegionTags(regionList)) ordered.Add(LibretroName(swapped + " " + tag));
            }
            ordered.Add(LibretroName(title));

            // De-dupe, preserve order, keep it bounded so a miss is cheap. Case-SENSITIVE: GitHub raw URLs
            // are case-sensitive, so a candidate that differs from another only in case (e.g. the index's
            // "…Featuring…" vs a ROM key's "…featuring…") is a DIFFERENT file and must not be collapsed away.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return ordered.Where(c => c.Length > 0 && seen.Add(c)).Take(12).ToList();
        }

        // Region parentheticals to try, card's own regions first (mapped to libretro tags), then defaults.
        private static IEnumerable<string> RegionTags(IEnumerable<string?> regions)
        {
            var tags = new List<string>();
            foreach (var r in regions)
            {
                var t = (r ?? "").ToLowerInvariant() switch
                {
                    "usa" => "(USA)", "europe" => "(Europe)", "japan" => "(Japan)", "world" => "(World)", _ => null,
                };
                if (t != null) tags.Add(t);
            }
            tags.AddRange(new[] { "(USA)", "(World)", "(Europe)", "(USA, Europe)", "(Japan)" });
            return tags.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static async Task<byte[]?> TryDownloadFirst(HttpClient http, string system, IEnumerable<string> names)
        {
            var repo = ThumbRepo[system].Replace(' ', '_');
            foreach (var name in names)
            {
                // Repos use underscores for spaces (Nintendo_-_Nintendo_64); the FILENAME keeps spaces (encoded).
                var url = $"https://raw.githubusercontent.com/libretro-thumbnails/{repo}/master/Named_Boxarts/{Uri.EscapeDataString(name)}.png";
                var png = await TryDownloadPng(http, url);
                if (png != null) return png;
            }
            return null;
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
