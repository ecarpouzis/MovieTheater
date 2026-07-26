using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.LaunchBox;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Enrich arcade cards with RetroAchievements SUPPORT flags — which games have achievements (🏆),
    /// high-score leaderboards (🥇), and speedrun/time leaderboards (⏱). Populates ArcadeGame.Ra* so the
    /// lobby can badge each card/version. This is only the SUPPORT map (does RA cover this game); the
    /// actual unlocks/scores/times are recorded at play time by the worker's rcheevos engine.
    ///
    /// <para>Matches by NORMALIZED TITLE per console: RA's game titles run through the same
    /// <see cref="LaunchBoxMetadata.NormalizeTitle"/> our <see cref="ArcadeGame.CollapseKey"/> uses, so a
    /// card matches when its CollapseKey equals a RA game's normalized title. Game-level (all versions of a
    /// title share the flags) — full coverage including JIT/disc systems without hashing 448 GB of ROMs.</para>
    ///
    /// <para>Bulk-job rules (global): dry-run unless <c>--apply</c>; bounded by <c>--limit</c> cards;
    /// resumable via <c>--after-id</c> (a card's min version id) + skips already-checked cards
    /// (non-null RaCheckedUtc) unless <c>--overwrite</c>; emits {processed, remaining, nextAfterId}.</para>
    ///
    /// <para>Needs the RA <b>Web API</b> credentials (RA → Settings → Keys → "Web API Key"): pass
    /// <c>--user</c> + <c>--key</c> (or <c>--key-file</c>).</para>
    /// </summary>
    [Command("arcade-ra-enrich", Description = "Flag arcade cards with RetroAchievements support (achievements/high-scores/speedruns) by normalized-title match. Dry-run unless --apply.")]
    public class ArcadeRaEnrichCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max cards to process this run (default 300).")]
        public int Limit { get; set; } = 300;

        [CommandOption("after-id", Description = "Resume cursor: only cards whose min version id is greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("system", Description = "Restrict to one system code (e.g. nes, snes, ps1).")]
        public string System { get; set; } = "";

        [CommandOption("overwrite", Description = "Re-check cards already resolved (non-null RaCheckedUtc).")]
        public bool Overwrite { get; set; }

        [CommandOption("user", Description = "RetroAchievements Web API username (z=).")]
        public string ApiUser { get; set; } = "";

        [CommandOption("key", Description = "RetroAchievements Web API key (y=). Or use --key-file.")]
        public string ApiKey { get; set; } = "";

        [CommandOption("key-file", Description = "File holding the RA Web API key (keeps it out of shell history).")]
        public string ApiKeyFile { get; set; } = "";

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeRaEnrichCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        // Our system code → RetroAchievements console id (RA's ids == rcheevos rc_console ids). Systems RA
        // doesn't cover (or we don't map) are skipped. Extend as systems are added.
        private static readonly Dictionary<string, int> RaConsole = new(StringComparer.OrdinalIgnoreCase)
        {
            ["megadrive"] = 1, ["genesis"] = 1, ["md"] = 1,
            ["n64"] = 2, ["snes"] = 3, ["gb"] = 4, ["gba"] = 5, ["gbc"] = 6, ["nes"] = 7, ["fds"] = 7,
            ["pce"] = 8, ["pcengine"] = 8, ["tg16"] = 8,
            ["sms"] = 11, ["mastersystem"] = 11, ["ps1"] = 12, ["psx"] = 12,
            ["lynx"] = 13, ["ngp"] = 14, ["ngpc"] = 14, ["gg"] = 15, ["gamegear"] = 15,
            ["gc"] = 16, ["gamecube"] = 16, ["nds"] = 18, ["ds"] = 18, ["ps2"] = 21,
            ["vb"] = 28, ["virtualboy"] = 28, ["sg1000"] = 33, ["saturn"] = 39,
            ["dc"] = 40, ["dreamcast"] = 40, ["psp"] = 41, ["colecovision"] = 44, ["coleco"] = 44,
            ["a2600"] = 25, ["atari2600"] = 25, ["a7800"] = 51, ["wsc"] = 53, ["ws"] = 53, ["wonderswan"] = 53,
            ["arcade"] = 27, ["fbneo"] = 27, ["mame"] = 27, ["neogeo"] = 27,
            ["3ds"] = 62,
        };

        // RA leaderboard Format tokens: time-ish (speedrun) vs score-ish (high score).
        private static bool IsTimeFormat(string f) => f?.ToUpperInvariant() switch
        {
            "TIME" or "FRAMES" or "MILLISECS" or "CENTISECS" or "TIMESECS" or "SECS" or "MINUTES" or "SECS_AS_MINS" => true,
            _ => false,
        };
        private static bool IsScoreFormat(string f) => f?.ToUpperInvariant() switch
        {
            "SCORE" or "VALUE" or "POINTS" or "UNSIGNED" or "FLOAT1" or "FLOAT2" or "FLOAT3" => true,
            _ => false,
        };

        private sealed class RaListGame
        {
            [JsonPropertyName("ID")] public int Id { get; set; }
            [JsonPropertyName("Title")] public string? Title { get; set; }
            [JsonPropertyName("NumAchievements")] public int NumAchievements { get; set; }
            [JsonPropertyName("NumLeaderboards")] public int NumLeaderboards { get; set; }
        }
        private sealed class RaLbResult { [JsonPropertyName("Format")] public string? Format { get; set; } }
        private sealed class RaLbResponse { [JsonPropertyName("Results")] public List<RaLbResult>? Results { get; set; } }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var key = !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim()
                : (!string.IsNullOrWhiteSpace(ApiKeyFile) && File.Exists(ApiKeyFile) ? File.ReadAllText(ApiKeyFile).Trim() : "");
            if (string.IsNullOrWhiteSpace(ApiUser) || string.IsNullOrWhiteSpace(key))
            { w.WriteLine("Need RA Web API creds: --user <name> and --key <key> (or --key-file <path>)."); return; }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheaterArcade/1.0");
            var sys = System.Trim().ToLowerInvariant();

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);

            var rows = await db.ArcadeGames.Where(g => g.IsEnabled && (sys == "" || g.System == sys)).ToListAsync();
            // One card = (System, CollapseKey); process card-at-a-time so all versions of a title share flags.
            var cards = rows.GroupBy(g => new { g.System, g.CollapseKey })
                .Select(grp => grp.OrderBy(x => x.Id).ToList())
                .Where(c => c[0].Id > AfterId)
                .Where(c => Overwrite || c.Any(x => x.RaCheckedUtc == null)) // resume: skip fully-checked cards
                .OrderBy(c => c[0].Id)
                .ToList();
            var batch = cards.Take(Math.Max(1, Limit)).ToList();
            var remaining = cards.Count - batch.Count;

            // Per-console RA game map (normalized title -> game), fetched once. Per-game leaderboard formats cached.
            var consoleMaps = new Dictionary<int, Dictionary<string, RaListGame>>();
            var lbCache = new Dictionary<int, (bool score, bool time)>();
            int withAch = 0, withScore = 0, withTime = 0, noMatch = 0, skippedSys = 0, lastId = AfterId, sinceSave = 0;

            async Task<Dictionary<string, RaListGame>?> ConsoleMap(int consoleId)
            {
                if (consoleMaps.TryGetValue(consoleId, out var cached)) return cached;
                try
                {
                    // f=1 = only games that HAVE achievements; that is exactly the set we want to badge.
                    var json = await http.GetStringAsync(
                        $"https://retroachievements.org/API/API_GetGameList.php?i={consoleId}&f=1&z={Uri.EscapeDataString(ApiUser)}&y={Uri.EscapeDataString(key)}");
                    var list = JsonSerializer.Deserialize<List<RaListGame>>(json) ?? new();
                    var map = new Dictionary<string, RaListGame>(StringComparer.Ordinal);
                    foreach (var g in list)
                    {
                        var n = LaunchBoxMetadata.NormalizeTitle(g.Title);
                        if (!string.IsNullOrEmpty(n) && !map.ContainsKey(n)) map[n] = g; // first wins (RA lists base before hacks)
                    }
                    consoleMaps[consoleId] = map;
                    w.WriteLine($"  RA console {consoleId}: {map.Count} games with achievements");
                    return map;
                }
                catch (Exception ex) { w.WriteLine($"  ! RA game list for console {consoleId} failed: {ex.Message}"); consoleMaps[consoleId] = new(); return consoleMaps[consoleId]; }
            }

            async Task<(bool score, bool time)> LbFormats(int gameId)
            {
                if (lbCache.TryGetValue(gameId, out var c)) return c;
                var result = (score: false, time: false);
                try
                {
                    var json = await http.GetStringAsync(
                        $"https://retroachievements.org/API/API_GetGameLeaderboards.php?i={gameId}&c=500&z={Uri.EscapeDataString(ApiUser)}&y={Uri.EscapeDataString(key)}");
                    var resp = JsonSerializer.Deserialize<RaLbResponse>(json);
                    foreach (var lb in resp?.Results ?? new())
                    {
                        if (IsTimeFormat(lb.Format ?? "")) result.time = true;
                        else if (IsScoreFormat(lb.Format ?? "")) result.score = true;
                    }
                    await Task.Delay(300); // be gentle on the RA API between per-game calls
                }
                catch (Exception ex) { w.WriteLine($"  ! leaderboards for RA game {gameId} failed: {ex.Message}"); }
                lbCache[gameId] = result;
                return result;
            }

            var now = DateTime.UtcNow;
            foreach (var card in batch)
            {
                lastId = card[0].Id;
                var system = card[0].System;
                if (!RaConsole.TryGetValue(system, out var consoleId)) { skippedSys++; continue; }

                var map = await ConsoleMap(consoleId);
                if (map == null || map.Count == 0) continue;

                var norm = card[0].CollapseKey; // == NormalizeTitle(title) — same normalizer RA titles get
                if (string.IsNullOrEmpty(norm) || !map.TryGetValue(norm, out var raGame))
                {
                    noMatch++;
                    // Mark checked so a resume doesn't reprocess a confirmed no-match (unless --overwrite).
                    foreach (var r in card) { r.RaGameId = null; r.RaAchievementCount = 0; r.RaHasScoreLeaderboard = false; r.RaHasTimeLeaderboard = false; r.RaCheckedUtc = now; }
                    if (Apply && ++sinceSave >= 50) { await db.SaveChangesAsync(); sinceSave = 0; }
                    continue;
                }

                bool score = false, time = false;
                if (raGame.NumLeaderboards > 0) (score, time) = await LbFormats(raGame.Id);
                foreach (var r in card)
                {
                    r.RaGameId = raGame.Id;
                    r.RaAchievementCount = raGame.NumAchievements;
                    r.RaHasScoreLeaderboard = score;
                    r.RaHasTimeLeaderboard = time;
                    r.RaCheckedUtc = now;
                }
                if (raGame.NumAchievements > 0) withAch++;
                if (score) withScore++;
                if (time) withTime++;
                if (Apply && ++sinceSave >= 50) { await db.SaveChangesAsync(); sinceSave = 0; }

                if ((withAch + noMatch) % 100 == 0)
                    w.WriteLine($"  … {batch.IndexOf(card) + 1}/{batch.Count} cards ({withAch} with achievements, {noMatch} no-match)");
            }

            if (Apply) await db.SaveChangesAsync();

            w.WriteLine($"{{ processed: {batch.Count}, withAchievements: {withAch}, highScores: {withScore}, speedruns: {withTime}, noMatch: {noMatch}, skippedUnmappedSystem: {skippedSys}, remaining: {remaining}, nextAfterId: {lastId} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after-id {lastId} --apply.");
        }
    }
}
