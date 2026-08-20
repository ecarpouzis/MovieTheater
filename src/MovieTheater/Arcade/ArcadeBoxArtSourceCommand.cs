using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Points cards at a specific cover image — the other half of <c>arcade-boxart-evict</c>.
    ///
    /// <para>Eviction answers "this cover is wrong and I have nothing better". This answers "this cover is
    /// wrong AND I know the right one", which is the commoner outcome once you go looking: the cascade
    /// matches by title, and a title that fails is usually a title that is spelled differently somewhere,
    /// not a game nobody has art for. Our "San Goku Shi DS" is libretro's "Rekishi Simulation Game -
    /// Sangokushi DS (Japan)" — same box, unrecognisable to an exact-title lookup.</para>
    ///
    /// <para>Writing <see cref="ArcadeGame.BoxArtSourceUrl"/> is enough on its own: it is step 0 of the image
    /// route and outranks even a cached file, and its cache key is <c>{cardId}-{sha1_8(url)}.png</c>, so the
    /// wrong cover is retired by the same rename trick eviction uses. No generation bump, no eviction first.</para>
    ///
    /// <para><b>Every URL is fetched and checked before it is written</b> (unless <c>--no-verify</c>). A URL
    /// that 404s or serves HTML would silently fall through to the cascade and re-cache the very cover you
    /// were replacing — a fix that looks applied and changes nothing. Dry-run unless <c>--apply</c>; bounded
    /// by <c>--limit</c>; every row keeps a <c>boxart-source</c> breadcrumb in <see cref="ArcadeGame.Notes"/>.</para>
    /// </summary>
    [Command("arcade-boxart-source", Description = "Point cards at a known-good cover URL (step 0, outranks the cache). Verifies each URL first. Dry-run unless --apply.")]
    public class ArcadeBoxArtSourceCommand : BasicDICommand, ICommand
    {
        [CommandOption("from", Description = "TSV of cardId<TAB>url (blank lines and # comments ignored). Anything after a third tab is treated as a note.")]
        public string From { get; set; } = "";

        [CommandOption("ids", Description = "Card ids for a one-off, comma-separated; needs --url.")]
        public string Ids { get; set; } = "";

        [CommandOption("url", Description = "The cover URL for --ids.")]
        public string Url { get; set; } = "";

        [CommandOption("reason", Description = "Why — recorded in Notes alongside the URL.")]
        public string Reason { get; set; } = "";

        [CommandOption("no-verify", Description = "Skip the fetch check. Only for a source you have already proven; an unreachable URL silently falls back to the cascade.")]
        public bool NoVerify { get; set; }

        [CommandOption("apply", Description = "Write. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max cards to act on this run (default 500).")]
        public int Limit { get; set; } = 500;

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeBoxArtSourceCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var want = new List<(int Id, string Url, string Note)>();

            if (From.Length > 0)
                foreach (var raw in File.ReadLines(RepoDataPath.Resolve(From)))
                {
                    var line = raw.TrimEnd();
                    if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                    var f = line.Split('\t');
                    if (f.Length < 2 || !int.TryParse(f[0].Trim(), out var cid)) continue;
                    want.Add((cid, f[1].Trim(), f.Length > 2 ? f[2].Trim() : ""));
                }
            foreach (var cid in Ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                   .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n > 0))
            {
                if (Url.Length == 0) throw new CommandException("--ids needs --url.");
                want.Add((cid, Url, ""));
            }
            if (want.Count == 0) throw new CommandException("Nothing to do — pass --from or --ids/--url.");
            want = want.Take(Math.Max(1, Limit)).ToList();

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(300);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-boxart-source/1.0");

            var ids = want.Select(x => x.Id).ToHashSet();
            var named = await db.ArcadeGames.Where(g => ids.Contains(g.Id))
                                .Select(g => new { g.Id, g.System, g.CollapseKey }).ToListAsync();
            var systems = named.Select(k => k.System).Distinct().ToList();
            var keys = named.Select(k => k.CollapseKey).Distinct().ToList();
            var rows = await db.ArcadeGames.Where(g => systems.Contains(g.System) && keys.Contains(g.CollapseKey))
                                           .ToListAsync();
            var cardOf = named.ToDictionary(k => k.Id, k => (k.System, k.CollapseKey));

            var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd");
            int set = 0, badUrl = 0, unknown = 0, rowsTouched = 0;
            var seen = new HashSet<(string, string)>();

            foreach (var (cid, url, note) in want)
            {
                if (!cardOf.TryGetValue(cid, out var key)) { unknown++; continue; }
                if (!seen.Add(key)) continue;                                  // one source per card

                if (!NoVerify && !await LooksLikeAnImageAsync(http, url))
                { w.WriteLine($"  SKIP  {cid}  unreachable or not an image: {url}"); badUrl++; continue; }

                var group = rows.Where(g => g.System == key.Item1 && g.CollapseKey == key.Item2)
                                .OrderBy(g => g.Id).ToList();
                w.WriteLine($"  set   [{key.Item1}] {group[0].Id} \"{group[0].Title}\" -> {url}");
                if (Apply)
                {
                    var breadcrumb = $"boxart-source {stamp}: {url}"
                                   + (Reason.Length > 0 ? $" — {Reason}" : "")
                                   + (note.Length > 0 ? $" — {note}" : "");
                    foreach (var g in group)
                    {
                        g.BoxArtSourceUrl = url;
                        // A blocked card is being given art on purpose — un-block it, or step 0 never runs.
                        g.BoxArtBlocked = false;
                        g.Notes = string.IsNullOrWhiteSpace(g.Notes) ? breadcrumb : g.Notes.TrimEnd() + "\n" + breadcrumb;
                        rowsTouched++;
                    }
                }
                set++;
            }

            if (Apply) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"{{ requested: {want.Count}, set: {set}, rowsTouched: {rowsTouched}, "
                      + $"badUrl: {badUrl}, unknownId: {unknown} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
        }

        /// <summary>HEAD-then-GET probe: the URL must answer 2xx with image bytes. Some hosts (GitHub raw
        /// among them) answer HEAD oddly or serve a symlink's TARGET FILENAME as plain text instead of a
        /// PNG, so a status check alone is not enough — sniff the magic bytes.</summary>
        private static async Task<bool> LooksLikeAnImageAsync(HttpClient http, string url)
        {
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return false;
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                return bytes.Length > 64 &&
                       ((bytes[0] == 0x89 && bytes[1] == 0x50) ||        // PNG
                        (bytes[0] == 0xFF && bytes[1] == 0xD8) ||        // JPEG
                        (bytes[0] == 0x47 && bytes[1] == 0x49) ||        // GIF
                        (bytes[0] == 0x42 && bytes[1] == 0x4D) ||        // BMP
                        (bytes[0] == 0x52 && bytes[1] == 0x49));         // RIFF/WEBP
            }
            catch { return false; }
        }
    }
}
