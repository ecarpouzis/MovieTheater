using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Best-effort box art for the arcade catalog from the community libretro-thumbnails repos
    /// (arcade-plan.md §5). For each enabled game with no <c>BoxArtPath</c>, it tries
    /// <c>Named_Boxarts/&lt;name&gt;.png</c> in that system's thumbnail repo (matched on the raw filename,
    /// which follows No-Intro naming), saves a hit under the posters mount, and records the path. Misses
    /// are listed for hand-fixing — box art is cosmetic, so a miss is not an error.
    ///
    /// <para>Bulk-job rules: bounded by <c>--limit</c>, resumable via an <c>--after-id</c> cursor, reports
    /// <c>{fetched, missed, remaining, nextAfterId}</c>, idempotent (skips games that already have art
    /// unless <c>--overwrite</c>), and writes nothing without <c>--apply</c>. Runs where the posters mount
    /// is present (prod / the box), and needs outbound network to raw.githubusercontent.com.</para>
    /// </summary>
    [Command("arcade-boxart", Description = "Fetch arcade box art from libretro-thumbnails (dry-run unless --apply).")]
    public class ArcadeBoxArtCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write files + rows. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max games to attempt this run (default 100).")]
        public int Limit { get; set; } = 100;

        [CommandOption("after-id", Description = "Resume cursor: only games with Id greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("overwrite", Description = "Re-fetch games that already have box art.")]
        public bool Overwrite { get; set; }

        // System code → libretro-thumbnails repo name. Arcade (fbneo) box art rarely name-matches, so it's
        // deliberately omitted (its cards keep the placeholder).
        private static readonly Dictionary<string, string> ThumbRepo = new()
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

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public ArcadeBoxArtCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (string.IsNullOrEmpty(config.MoviePostersDir))
            { w.WriteLine("MoviePostersDir is not configured — run this where the posters mount is present."); return; }
            var postersRoot = Path.GetFullPath(config.MoviePostersDir);

            await using var db = await dbFactory.CreateDbContextAsync();

            var query = db.ArcadeGames.Where(g => g.IsEnabled && g.Id > AfterId);
            if (!Overwrite) query = query.Where(g => g.BoxArtPath == null);
            var batch = await query.OrderBy(g => g.Id).Take(Math.Max(1, Limit)).ToListAsync();
            var remaining = await query.CountAsync() - batch.Count;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-boxart/1.0");

            int fetched = 0, missed = 0;
            int lastId = AfterId;
            foreach (var game in batch)
            {
                lastId = game.Id;
                if (!ThumbRepo.TryGetValue(game.System, out var repo))
                { missed++; w.WriteLine($"  ? [{game.System}] {game.Title} (no thumbnail repo for system)"); continue; }

                var url = $"https://raw.githubusercontent.com/libretro-thumbnails/{Uri.EscapeDataString(repo)}/master/Named_Boxarts/{Uri.EscapeDataString(LibretroName(game.CloudRetroGameKey))}.png";
                byte[]? bytes = await TryDownloadPng(http, url);
                if (bytes == null) { missed++; w.WriteLine($"  ? [{game.System}] {game.Title} (no match)"); continue; }

                var rel = $"arcade/{game.System}/{SafeFileName(game.CloudRetroGameKey)}.png";
                if (Apply)
                {
                    var dest = Path.Combine(postersRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await File.WriteAllBytesAsync(dest, bytes);
                    game.BoxArtPath = rel;
                }
                fetched++;
                w.WriteLine($"  + [{game.System}] {game.Title}");
            }

            if (Apply && fetched > 0) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"this run: {fetched} fetched, {missed} missed.");
            w.WriteLine($"{{ fetched: {fetched}, missed: {missed}, remaining: {remaining}, nextAfterId: {lastId} }}");
            if (!Apply) w.WriteLine("DRY RUN — no files or rows written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after-id {lastId}.");
        }

        // libretro-thumbnails replaces these characters in the ROM name with '_': & * / : ` < > ? \ | "
        private static string LibretroName(string name)
        {
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if ("&*/:`<>?\\|\"".IndexOf(chars[i]) >= 0) chars[i] = '_';
            return new string(chars);
        }

        private static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // A hit is a real PNG (200 + PNG magic), so a repo's 404 HTML page is never saved as "box art".
        private static async Task<byte[]?> TryDownloadPng(HttpClient http, string url)
        {
            try
            {
                using var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
                    return null; // not a PNG
                return bytes;
            }
            catch { return null; }
        }
    }
}
