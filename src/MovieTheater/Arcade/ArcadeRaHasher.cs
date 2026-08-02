using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Computes RetroAchievements ROM hashes by driving the fork's <c>rahash</c> tool, which calls
    /// rcheevos' own <c>rc_hash</c> — byte-for-byte the identification rc_client performs when it loads a
    /// game. Shared by <c>arcade-ra-hash</c> (the bulk sweep) and <c>arcade-ingest</c> (new rows), so a
    /// freshly ingested game is identified the same way a swept one is.
    ///
    /// <para>Never reimplement the hashing in C#: the rules are per-console (N64 byte-order normalisation,
    /// header skipping, disc consoles hashing an executable rather than the image, zip member selection),
    /// and any divergence yields hashes that look perfectly fine and match nothing at all.</para>
    /// </summary>
    public static class ArcadeRaHasher
    {
        /// <summary>Our system code → RetroAchievements console id (== rcheevos rc_console ids). A system
        /// that is absent hashes with id 0 (RC_CONSOLE_UNKNOWN), where rcheevos picks its rules from the
        /// file itself — still useful, just less certain than naming the console outright.</summary>
        public static readonly Dictionary<string, uint> Console = new(StringComparer.OrdinalIgnoreCase)
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

        public static uint ConsoleId(string? system) =>
            system != null && Console.TryGetValue(system, out var id) ? id : 0;

        /// <summary>One dump to hash. <paramref name="Id"/> is opaque and echoed back so results correlate
        /// without depending on ordering.</summary>
        public sealed class Item
        {
            public Item(int id, string path, uint consoleId) { Id = id; Path = path; ConsoleId = consoleId; }
            public int Id { get; }
            public string Path { get; }
            public uint ConsoleId { get; }
        }

        private sealed class Request
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("path")] public string Path { get; set; } = "";
            [JsonPropertyName("consoleId")] public uint ConsoleId { get; set; }
        }

        private sealed class Response
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("hash")] public string? Hash { get; set; }
            [JsonPropertyName("ok")] public bool Ok { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }

        /// <summary>
        /// Hash a batch. Returns id → hash for the ones that worked; ids absent from the result could not be
        /// hashed (the caller decides whether that is worth stamping or reporting).
        ///
        /// <para>The child process is spawned once per batch rather than per file — a process per ROM would
        /// spend more time starting than hashing — but it keeps no state between lines, so a batch that dies
        /// halfway costs only the items after the failure.</para>
        /// </summary>
        public static async Task<Dictionary<int, string>> HashAsync(
            string hasherPath, IReadOnlyList<Item> items, Action<string>? log = null)
        {
            var results = new Dictionary<int, string>();
            if (items.Count == 0) return results;
            if (!File.Exists(hasherPath))
            {
                log?.Invoke($"hasher not found: {hasherPath}");
                return results;
            }

            var psi = new ProcessStartInfo
            {
                FileName = hasherPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                log?.Invoke("could not start the hasher");
                return results;
            }

            var readErr = Task.Run(async () =>
            {
                var text = await proc.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(text)) log?.Invoke(text.Trim());
            });

            var readOut = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var r = JsonSerializer.Deserialize<Response>(line);
                        if (r != null && r.Ok && !string.IsNullOrWhiteSpace(r.Hash)) results[r.Id] = r.Hash!.Trim();
                    }
                    catch (JsonException)
                    {
                        // A line we cannot parse is the hasher misbehaving, not an item failing; keep going
                        // so one bad line does not discard the whole batch's work.
                        log?.Invoke($"  (unparseable hasher output: {line})");
                    }
                }
            });

            foreach (var it in items)
                await proc.StandardInput.WriteLineAsync(
                    JsonSerializer.Serialize(new Request { Id = it.Id, Path = it.Path, ConsoleId = it.ConsoleId }));
            proc.StandardInput.Close();

            await readOut;
            await readErr;
            await proc.WaitForExitAsync();
            return results;
        }
    }
}
