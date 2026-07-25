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
        // arcade/neogeo now resolve real titles from the FBNeo DAT (arcade-fbneo-resolve), so their box art
        // is matchable against the FBNeo - Arcade Games thumbnails by TITLE via the index (Normalize bridges
        // the casing/region-suffix drift, e.g. our "1944 - the loop master" ⇄ "1944 - The Loop Master (Japan)").
        public static readonly Dictionary<string, string> ThumbRepo = new()
        {
            ["arcade"] = "FBNeo - Arcade Games",
            ["neogeo"] = "FBNeo - Arcade Games",
            ["nes"] = "Nintendo - Nintendo Entertainment System",
            ["snes"] = "Nintendo - Super Nintendo Entertainment System",
            ["genesis"] = "Sega - Mega Drive - Genesis",
            ["gb"] = "Nintendo - Game Boy",
            ["gbc"] = "Nintendo - Game Boy Color",
            ["gba"] = "Nintendo - Game Boy Advance",
            ["n64"] = "Nintendo - Nintendo 64",
            ["nds"] = "Nintendo - Nintendo DS",
            ["3ds"] = "Nintendo - Nintendo 3DS",
            ["gc"] = "Nintendo - GameCube",
            ["wii"] = "Nintendo - Wii",
            ["ps1"] = "Sony - PlayStation",
            ["ps2"] = "Sony - PlayStation 2",
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
            // ScummVM has its own thumbnail repo (1,324 boxes), named from the ScummVM game DB's
            // descriptions ("Dig, The", "Freddi Fish 3_ The Case of the Stolen Conch Shell", with the
            // usual '_' substitution and a "(DOS_English)" style tag). Our ingest titles come from the
            // SAME database, so the title/token match lands ~95% of cards with no extra machinery.
            ["scummvm"] = "ScummVM",
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
            //    Skipped for ScummVM, whose "ROM" key is a ScummVM TARGET ("dig-de", "sword25-fr"), never a
            //    dump name — every such candidate is a guaranteed 404 in front of the index hit.
            if (!string.Equals(system, "scummvm", StringComparison.Ordinal))
                foreach (var k in keyList) ordered.Add(LibretroName(k));

            // 2. Index match — resolves drift the exact name can't (SMS/GG region+language tags, Dreamcast
            //    TOSEC names, "007 - GoldenEye" ⇄ "GoldenEye 007").
            var hit = index?.Match(title, regionList);
            if (hit != null) ordered.Add(hit);

            // 2b. Gated inference to finish the set — a contiguous-run or full-token-coverage guess for titles
            //     the exact/token match missed (added subtitle/region in the libretro name). Strict enough that
            //     a wrong box is unlikely; still ranked below the exact matches above.
            var inferred = index?.InferBest(title, regionList);
            if (inferred != null) ordered.Add(inferred);

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
                var png = await TryDownloadArt(http, repo, name);
                if (png != null) return png;
            }
            return null;
        }

        // Max symlink hops to follow. Targets can chain ("X (German)" -> "X (DOS)" -> "X"), but a cycle or a
        // pathological chain must not turn one card into unbounded requests.
        private const int MaxSymlinkHops = 3;

        /// <summary>One candidate filename, FOLLOWING libretro-thumbnails' symlinks. A large share of these
        /// repos' <c>Named_Boxarts</c> entries are git symlinks that dedupe every language/platform variant of
        /// a game onto one image (ScummVM: 403 of 1,324). raw.githubusercontent serves such an entry as 200
        /// with its TARGET FILENAME in plain text — not a PNG — so the old "no PNG magic = miss" check made
        /// matched cards come up blank anyway. A target must be a bare SIBLING filename (no path separators),
        /// so following it can never leave <c>Named_Boxarts</c>.</summary>
        private static async Task<byte[]?> TryDownloadArt(HttpClient http, string repo, string name)
        {
            for (int hop = 0; hop <= MaxSymlinkHops; hop++)
            {
                // Repos use underscores for spaces (Nintendo_-_Nintendo_64); the FILENAME keeps spaces (encoded).
                var url = $"https://raw.githubusercontent.com/libretro-thumbnails/{repo}/master/Named_Boxarts/{Uri.EscapeDataString(name)}.png";
                var bytes = await TryDownloadBytes(http, url);
                if (bytes == null) return null;
                if (IsPng(bytes)) return bytes;
                var target = SymlinkTarget(bytes);
                if (target == null) return null;      // not art and not a symlink (a 404 page, say)
                name = target;
            }
            return null;
        }

        // The symlink's target as a candidate name (".png" trimmed, matching BuildCandidates' convention), or
        // null if the body isn't a plain sibling-filename symlink.
        internal static string? SymlinkTarget(byte[] body)
        {
            if (body.Length == 0 || body.Length > 512) return null;
            var text = System.Text.Encoding.UTF8.GetString(body).Trim();
            if (!text.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
            if (text.Length <= 4 || text.IndexOfAny(new[] { '/', '\\' }) >= 0) return null;
            if (text.Any(char.IsControl)) return null;
            return text.Substring(0, text.Length - 4);
        }

        private static bool IsPng(byte[] b) =>
            b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

        private static async Task<byte[]?> TryDownloadBytes(HttpClient http, string url)
        {
            try
            {
                using var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsByteArrayAsync();
            }
            catch { return null; }
        }

        // libretro-thumbnails replaces these characters in the ROM name with '_': & * / : ` < > ? \ | "
        public static string LibretroName(string name)
        {
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if ("&*/:`<>?\\|\"".IndexOf(chars[i]) >= 0) chars[i] = '_';
            return new string(chars);
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
