using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    /// title share the flags) — full coverage including JIT/disc systems without hashing 448 GB of ROMs.
    /// RA's non-retail entries additionally match through a de-tagged alias (see
    /// <see cref="NormalizeRaTitleUntagged"/>), which is what lets a romhack card find its set.</para>
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

        [CommandOption("from-unlocks", Description = "Resolve RaGameId from achievements ALREADY UNLOCKED on the card (RA's own achievement→game map) instead of by title. Hash-grade: rcheevos identified the ROM to award them. Only touches cards that have unlocks and no RaGameId.")]
        public bool FromUnlocks { get; set; }

        [CommandOption("clear-unmatched", Description = "Also ERASE the RA mapping of cards whose title no longer matches. Off by default — a title miss is not evidence an existing mapping is wrong, and it would undo --from-unlocks.")]
        public bool ClearUnmatched { get; set; }

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
        // API_GetAchievementUnlocks: we want only its Game block — RA's authoritative "which game is this
        // achievement from". c=1 keeps the unlock list (which we ignore) to a single row.
        private sealed class RaAchGame { [JsonPropertyName("ID")] public int Id { get; set; } }
        private sealed class RaAchUnlocksResponse { [JsonPropertyName("Game")] public RaAchGame? Game { get; set; } }
        private sealed class RaGameExtended
        {
            [JsonPropertyName("Title")] public string? Title { get; set; }
            [JsonPropertyName("NumAchievements")] public int NumAchievements { get; set; }
        }
        private sealed class RaHash { [JsonPropertyName("Name")] public string? Name { get; set; } }
        private sealed class RaHashesResponse { [JsonPropertyName("Results")] public List<RaHash>? Results { get; set; } }

        // RA prefixes non-retail entries with ~Tag~ markers — "~Hack~ Super Mario 64: Last Impact",
        // "~Hack~ ~Demo~ The Legend of Peach", also ~Prototype~ / ~Unlicensed~ / ~Homebrew~ / ~Test Kit~.
        // Our catalog titles carry no such marker, so NormalizeTitle put the tag words INTO the key
        // ("hacksupermario64lastimpact") and every romhack card missed its set — while rcheevos, which
        // identifies the ROM by hash, awarded that set at play time regardless. The result was cards with
        // real unlocks and RaGameId null. Strip only LEADING tag groups; a "~" inside a title (dual names
        // like "Dodge 'Em ~ Dodger Cars") is a different thing and must survive untouched.
        private static readonly Regex RaLeadingTags = new(@"^(?:\s*~[^~]*~)+\s*", RegexOptions.Compiled);

        /// <summary>The normalized key for an RA title with its leading ~Tag~ markers removed, or "" when the
        /// title carries none (so callers can tell "no alias" from "same as the plain key").</summary>
        private static string NormalizeRaTitleUntagged(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            var stripped = RaLeadingTags.Replace(title, "");
            if (stripped.Length == 0 || stripped.Length == title.Length) return "";
            return LaunchBoxMetadata.NormalizeTitle(stripped);
        }

        // Reduce a dump name to a comparable key: drop the extension, lowercase, keep only alphanumerics.
        // "Sonic The Hedgehog (USA, Europe).md" and our "Sonic the Hedgehog (USA, Europe)" collapse to the
        // same key; a hack like "… (GameCube Edition)" collapses to a different one — that's the discriminator.
        private static string NormalizeDump(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var dot = name.LastIndexOf('.');
            if (dot > 0 && name.Length - dot <= 5) name = name[..dot]; // strip a short file extension
            return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

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

            // --from-unlocks: the achievement ids we already mirrored ARE a hash-grade fingerprint — rcheevos
            // had to identify the ROM by content hash before RA would hand it that set — so one of them names
            // the RA game exactly, with no title guessing. Any achievement on the card will do; take the
            // lowest for a deterministic, resumable choice.
            var achForVersion = new Dictionary<int, long>();
            if (FromUnlocks)
            {
                var unlocked = await db.ArcadeAchievementUnlocks.AsNoTracking()
                    .Where(u => u.ArcadeGameId != null)
                    .GroupBy(u => u.ArcadeGameId!.Value)
                    .Select(grp => new { VersionId = grp.Key, Ach = grp.Min(u => u.RaAchievementId) })
                    .ToListAsync();
                foreach (var u in unlocked) achForVersion[u.VersionId] = u.Ach;
            }

            // One card = (System, CollapseKey); process card-at-a-time so all versions of a title share flags.
            var cards = rows.GroupBy(g => new { g.System, g.CollapseKey })
                .Select(grp => grp.OrderBy(x => x.Id).ToList())
                .Where(c => c[0].Id > AfterId)
                .Where(c => FromUnlocks
                    // Cards this mode exists for: someone earned something here, yet the card names no RA
                    // game. A card that already has one is left alone — including by --overwrite, which in
                    // this mode would only re-ask a question we already have the right answer to.
                    ? c.All(x => x.RaGameId == null) && c.Any(x => achForVersion.ContainsKey(x.Id))
                    : Overwrite || c.Any(x => x.RaCheckedUtc == null)) // resume: skip fully-checked cards
                .OrderBy(c => c[0].Id)
                .ToList();
            var batch = cards.Take(Math.Max(1, Limit)).ToList();
            var remaining = cards.Count - batch.Count;

            // Per-console RA game map (normalized title -> game), fetched once. Per-game leaderboard formats cached.
            var consoleMaps = new Dictionary<int, Dictionary<string, RaListGame>>();
            var lbCache = new Dictionary<int, (bool score, bool time)>();
            var hashCache = new Dictionary<int, HashSet<string>>(); // gameId -> normalized supported dump names
            int withAch = 0, withScore = 0, withTime = 0, noMatch = 0, skippedSys = 0, lastId = AfterId, sinceSave = 0;
            int keptMapping = 0; // no-match cards whose existing mapping was preserved rather than erased

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
                    // TWO PASSES, and the order between them is the whole point: every RA title claims its own
                    // normalized key first, and only then may a de-tagged alias fill a key still empty. Doing it
                    // in one pass would let "~Hack~ Sonic the Hedgehog" reach `sonicthehedgehog` ahead of the
                    // real game purely on list order, and badge the base card with the hack's set.
                    foreach (var g in list)
                    {
                        var n = LaunchBoxMetadata.NormalizeTitle(g.Title);
                        if (!string.IsNullOrEmpty(n) && !map.ContainsKey(n)) map[n] = g; // first wins
                    }
                    int aliases = 0;
                    foreach (var g in list)
                    {
                        var n = NormalizeRaTitleUntagged(g.Title);
                        if (!string.IsNullOrEmpty(n) && !map.ContainsKey(n)) { map[n] = g; aliases++; }
                    }
                    consoleMaps[consoleId] = map;
                    w.WriteLine($"  RA console {consoleId}: {map.Count} games with achievements ({aliases} via de-tagged alias)");
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

            // The set of ROM-dump names RA actually supports for a game (normalized), so we can flag WHICH of
            // our versions is the recognized dump vs a hack/unmatched region in the same card.
            async Task<HashSet<string>> SupportedDumps(int gameId)
            {
                if (hashCache.TryGetValue(gameId, out var cached)) return cached;
                var set = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    var json = await http.GetStringAsync(
                        $"https://retroachievements.org/API/API_GetGameHashes.php?i={gameId}&z={Uri.EscapeDataString(ApiUser)}&y={Uri.EscapeDataString(key)}");
                    var resp = JsonSerializer.Deserialize<RaHashesResponse>(json);
                    foreach (var h in resp?.Results ?? new())
                    {
                        var n = NormalizeDump(h.Name);
                        if (n.Length > 0) set.Add(n);
                    }
                    await Task.Delay(300); // gentle on the RA API
                }
                catch (Exception ex) { w.WriteLine($"  ! hashes for RA game {gameId} failed: {ex.Message}"); }
                hashCache[gameId] = set;
                return set;
            }

            // --from-unlocks resolution: RA's own achievement→game map. Returns the RA game and how many
            // achievements its set holds, or null if RA can't place the achievement (deleted/demoted).
            async Task<RaListGame?> GameForAchievement(long achievementId)
            {
                try
                {
                    var json = await http.GetStringAsync(
                        $"https://retroachievements.org/API/API_GetAchievementUnlocks.php?a={achievementId}&c=1&z={Uri.EscapeDataString(ApiUser)}&y={Uri.EscapeDataString(key)}");
                    var id = JsonSerializer.Deserialize<RaAchUnlocksResponse>(json)?.Game?.Id ?? 0;
                    await Task.Delay(300); // gentle on the RA API
                    if (id <= 0) return null;

                    // Second call for the set size — the achievement→game response carries no achievement count,
                    // and RaAchievementCount drives the card's 🏆 badge.
                    var gjson = await http.GetStringAsync(
                        $"https://retroachievements.org/API/API_GetGameExtended.php?i={id}&z={Uri.EscapeDataString(ApiUser)}&y={Uri.EscapeDataString(key)}");
                    var g = JsonSerializer.Deserialize<RaGameExtended>(gjson);
                    await Task.Delay(300);
                    return new RaListGame { Id = id, Title = g?.Title, NumAchievements = g?.NumAchievements ?? 0, NumLeaderboards = 1 };
                }
                catch (Exception ex) { w.WriteLine($"  ! achievement {achievementId} → game failed: {ex.Message}"); return null; }
            }

            var now = DateTime.UtcNow;
            foreach (var card in batch)
            {
                lastId = card[0].Id;
                var system = card[0].System;
                if (!RaConsole.TryGetValue(system, out var consoleId)) { skippedSys++; continue; }

                RaListGame? raGame;
                if (FromUnlocks)
                {
                    // No console game list needed and no title involved — RA is asked directly which game
                    // this card's own unlocked achievement belongs to.
                    var achId = card.Where(v => achForVersion.ContainsKey(v.Id)).Select(v => achForVersion[v.Id]).Min();
                    raGame = await GameForAchievement(achId);
                    if (raGame == null)
                    {
                        noMatch++;
                        w.WriteLine($"  ? {system}/{card[0].Title}: RA couldn't place achievement {achId} — left unmapped");
                        continue; // no RaCheckedUtc stamp: this is a failed lookup, not a confirmed answer
                    }
                    w.WriteLine($"  ✔ {system}/{card[0].Title} → RA {raGame.Id} \"{raGame.Title}\" ({raGame.NumAchievements} achievements) via achievement {achId}");
                }
                else
                {
                    var map = await ConsoleMap(consoleId);
                    if (map == null || map.Count == 0) continue;

                    var norm = card[0].CollapseKey; // == NormalizeTitle(title) — same normalizer RA titles get
                    if (string.IsNullOrEmpty(norm)) raGame = null;
                    else raGame = map.TryGetValue(norm, out var hit) ? hit : null;
                }

                if (raGame == null)
                {
                    noMatch++;
                    // A title miss says our title doesn't look like RA's — NOT that an existing mapping is
                    // wrong. This used to null the mapping unconditionally, which made every better source
                    // of truth temporary: a --from-unlocks id (resolved from an achievement RA itself
                    // awarded, so hash-grade) survived only until the next --overwrite title pass erased it.
                    // Preserve by default; --clear-unmatched opts back into erasing.
                    var pinned = card.Any(r => r.RaGameId != null) && !ClearUnmatched;
                    if (pinned) keptMapping++;
                    foreach (var r in card)
                    {
                        if (!pinned) { r.RaGameId = null; r.RaAchievementCount = 0; r.RaHasScoreLeaderboard = false; r.RaHasTimeLeaderboard = false; }
                        // Mark checked so a resume doesn't reprocess a confirmed no-match (unless --overwrite).
                        r.RaCheckedUtc = now;
                    }
                    if (Apply && ++sinceSave >= 50) { await db.SaveChangesAsync(); sinceSave = 0; }
                    continue;
                }

                bool score = false, time = false;
                if (raGame.NumLeaderboards > 0) (score, time) = await LbFormats(raGame.Id);
                var dumps = await SupportedDumps(raGame.Id); // the exact ROM dumps RA recognizes
                foreach (var r in card)
                {
                    r.RaGameId = raGame.Id;
                    r.RaAchievementCount = raGame.NumAchievements;
                    r.RaHasScoreLeaderboard = score;
                    r.RaHasTimeLeaderboard = time;
                    // Per-version: is THIS dump one RA supports? Floats it to the top of the dropdown (Rank).
                    r.RaSupported = dumps.Contains(NormalizeDump(r.CloudRetroGameKey));
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

            w.WriteLine($"{{ processed: {batch.Count}, withAchievements: {withAch}, highScores: {withScore}, speedruns: {withTime}, noMatch: {noMatch}, keptExistingMapping: {keptMapping}, skippedUnmappedSystem: {skippedSys}, remaining: {remaining}, nextAfterId: {lastId} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after-id {lastId} --apply.");
        }
    }
}
