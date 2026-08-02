using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Compute the RetroAchievements hash of each arcade dump and store it on
    /// <see cref="ArcadeGame.RaHash"/>, so RA games can be matched by CONTENT instead of by title.
    ///
    /// <para><b>Why this exists.</b> <c>arcade-ra-enrich</c> matches our cards to RA games by normalized
    /// title. That is structurally unable to be right every time — RA tags non-retail entries inside the
    /// title (<c>~Hack~</c>, <c>~Demo~</c>, <c>[Subset - …]</c>), a translation patch resolves to a
    /// different region's entry, and names simply diverge. It is also unable to say anything about the
    /// DUMP: a card can carry a perfectly correct RaGameId while the file we actually boot is a revision
    /// RA does not carry, in which case the room shows achievement and leaderboard badges and then
    /// silently scores nothing. RA identifies a ROM by hash; this makes us do the same.</para>
    ///
    /// <para><b>How.</b> The hashing itself is delegated to the fork's <c>rahash</c> tool, which calls
    /// rcheevos' own <c>rc_hash</c> — byte-for-byte the algorithm rc_client runs when it identifies a
    /// loaded game. Reimplementing it here is not an option: the rules are per-console (N64 byte-order
    /// normalisation, header skipping, disc consoles hashing an executable rather than the image, zip
    /// member selection) and any divergence would produce hashes that look fine and match nothing. Pass
    /// its path with <c>--hasher</c>.</para>
    ///
    /// <para>Bulk-job rules (global): dry-run unless <c>--apply</c>; bounded by <c>--limit</c> rows;
    /// resumable via <c>--after-id</c>; skips rows already stamped (non-null RaHashedUtc) unless
    /// <c>--overwrite</c>; emits {processed, hashed, unhashable, missing, remaining, nextAfterId} so the
    /// caller can drive it to completion and see it advancing.</para>
    /// </summary>
    [Command("arcade-ra-hash", Description = "Compute each arcade dump's RetroAchievements hash (via the fork's rahash tool) into ArcadeGame.RaHash. Dry-run unless --apply.")]
    public class ArcadeRaHashCommand : BasicDICommand, ICommand
    {
        [CommandOption("hasher", IsRequired = true, Description = "Path to the fork's rahash executable (built from cmd/rahash).")]
        public string Hasher { get; set; } = "";

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max rows to process this run (default 300).")]
        public int Limit { get; set; } = 300;

        [CommandOption("after-id", Description = "Resume cursor: only rows with Id greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("system", Description = "Restrict to one system code (e.g. nes, snes, ps1).")]
        public string System { get; set; } = "";

        [CommandOption("overwrite", Description = "Re-hash rows already stamped (non-null RaHashedUtc).")]
        public bool Overwrite { get; set; }

        [CommandOption("include-disabled", Description = "Also hash rows that are not enabled in the lobby.")]
        public bool IncludeDisabled { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeRaHashCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        // Systems whose dumps RA can identify AND whose console id rcheevos knows. Deliberately the same
        // set arcade-ra-enrich maps, minus nothing: a console rcheevos cannot place still hashes fine
        // with RC_CONSOLE_UNKNOWN (0), because rc_hash falls back to the file's own shape. Passing the
        // real id when we know it is still better — it is what the room itself will use.
        private static readonly Dictionary<string, uint> RaConsole = new(StringComparer.OrdinalIgnoreCase)
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
        };

        private sealed class HashRequest
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("path")] public string Path { get; set; } = "";
            [JsonPropertyName("consoleId")] public uint ConsoleId { get; set; }
        }

        private sealed class HashResponse
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("hash")] public string? Hash { get; set; }
            [JsonPropertyName("ok")] public bool Ok { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var writer = console.Output;
            if (!File.Exists(Hasher))
            {
                writer.WriteLine($"hasher not found: {Hasher}");
                return;
            }

            using var db = await dbFactory.CreateDbContextAsync();

            var q = db.ArcadeGames.AsNoTracking().Where(g => g.Id > AfterId);
            if (!IncludeDisabled) q = q.Where(g => g.IsEnabled);
            if (!string.IsNullOrWhiteSpace(System)) q = q.Where(g => g.System == System);
            if (!Overwrite) q = q.Where(g => g.RaHashedUtc == null);

            // Only rows we could actually hash: RA has no console for the rest, and a row with no source
            // file on disk has nothing to read.
            var systems = RaConsole.Keys.ToList();
            q = q.Where(g => g.System != null && systems.Contains(g.System) && g.SourceArchivePath != null);

            var remainingBefore = await q.CountAsync();
            var batch = await q.OrderBy(g => g.Id)
                .Take(Math.Max(1, Limit))
                .Select(g => new { g.Id, g.System, g.Title, g.SourceArchivePath })
                .ToListAsync();

            if (batch.Count == 0)
            {
                writer.WriteLine(JsonSerializer.Serialize(new { processed = 0, hashed = 0, unhashable = 0, missing = 0, remaining = 0, nextAfterId = AfterId, done = true }));
                return;
            }

            // Split the file-missing rows out BEFORE spawning the hasher: they are a data problem (a
            // dump moved or was pruned), not a hashing failure, and lumping them together would hide a
            // library that is quietly losing files behind a plausible "unhashable" count.
            var missing = new List<int>();
            var requests = new List<HashRequest>();
            foreach (var row in batch)
            {
                var path = row.SourceArchivePath ?? "";
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    missing.Add(row.Id);
                    continue;
                }
                RaConsole.TryGetValue(row.System ?? "", out var consoleId);
                requests.Add(new HashRequest { Id = row.Id, Path = path, ConsoleId = consoleId });
            }

            var results = await RunHasherAsync(requests, writer);

            var hashed = results.Where(r => r.Ok && !string.IsNullOrWhiteSpace(r.Hash)).ToList();
            var unhashable = results.Where(r => !r.Ok || string.IsNullOrWhiteSpace(r.Hash)).ToList();

            if (Apply)
            {
                var now = DateTime.UtcNow;
                var byId = results.ToDictionary(r => r.Id);
                var ids = byId.Keys.ToList();
                var rows = await db.ArcadeGames.Where(g => ids.Contains(g.Id)).ToListAsync();
                foreach (var g in rows)
                {
                    var r = byId[g.Id];
                    // Stamp even a failure: it is what tells the next run "already tried, do not
                    // re-read this file", which is the difference between a sweep that converges and
                    // one that re-hashes the same broken dumps forever.
                    g.RaHash = r.Ok ? r.Hash : null;
                    g.RaHashedUtc = now;
                }
                await db.SaveChangesAsync();
            }

            var nextAfterId = batch[^1].Id;
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                apply = Apply,
                processed = batch.Count,
                hashed = hashed.Count,
                unhashable = unhashable.Count,
                missing = missing.Count,
                remaining = Math.Max(0, remainingBefore - batch.Count),
                nextAfterId,
                done = remainingBefore - batch.Count <= 0,
            }));

            // A few concrete examples make a dry run reviewable; the counts alone never show WHICH dump
            // could not be read.
            foreach (var r in unhashable.Take(10))
            {
                var row = batch.First(b => b.Id == r.Id);
                writer.WriteLine($"  unhashable  [{row.System}] {row.Title} :: {row.SourceArchivePath}");
            }
            foreach (var id in missing.Take(10))
            {
                var row = batch.First(b => b.Id == id);
                writer.WriteLine($"  file gone   [{row.System}] {row.Title} :: {row.SourceArchivePath}");
            }
        }

        /// <summary>
        /// Stream a batch through the hasher process: one JSON request per line in, one result per line
        /// out. The child is spawned once per batch rather than per file — a per-file process would spend
        /// more time starting than hashing — but it holds no state between lines, so a batch that dies
        /// halfway costs only the rows after the failure and the caller simply resumes at nextAfterId.
        /// </summary>
        private async Task<List<HashResponse>> RunHasherAsync(List<HashRequest> requests, ConsoleWriter writer)
        {
            var results = new List<HashResponse>();
            if (requests.Count == 0) return results;

            var psi = new ProcessStartInfo
            {
                FileName = Hasher,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                writer.WriteLine("could not start the hasher");
                return results;
            }

            var readErr = Task.Run(async () =>
            {
                var text = await proc.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(text)) writer.WriteLine(text.Trim());
            });

            var readOut = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var r = JsonSerializer.Deserialize<HashResponse>(line);
                        if (r != null) results.Add(r);
                    }
                    catch (JsonException)
                    {
                        // A line we cannot parse is the hasher misbehaving, not a row failing; keep going
                        // so one bad line does not discard a whole batch's work.
                        writer.WriteLine($"  (unparseable hasher output: {line})");
                    }
                }
            });

            foreach (var r in requests)
            {
                await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(r));
            }
            proc.StandardInput.Close();

            await readOut;
            await readErr;
            await proc.WaitForExitAsync();
            return results;
        }
    }
}
