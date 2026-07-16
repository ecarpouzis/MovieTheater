using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// Expands the identity-keyed <see cref="ArcadeGameProfile"/> rows into the worker's per-ROM override
    /// manifest (<c>game-overrides.json</c>): each profile (System, TitleKey) is joined to every matching
    /// <see cref="ArcadeGame"/> row and emitted keyed by that ROM's <see cref="ArcadeGame.CloudRetroGameKey"/>
    /// — the key the emulator matches at load. So one profile ("dc","sonic adventure" → 30fps) fans out to
    /// all its region/revision ROMs. Re-run after editing profiles (like arcade-romcache-export).
    /// See docs/arcade-per-game-config.md.
    ///
    /// <para>Manifest shape (consumed by nanoarch's gameOverrides()):
    /// <c>{ "&lt;CloudRetroGameKey&gt;": { "fps": 30, "options": { "k": "v" } }, ... }</c>.</para>
    /// </summary>
    [Command("arcade-gameconfig-export", Description = "Write the worker's game-overrides.json from ArcadeGameProfile rows.")]
    public class ArcadeGameConfigExportCommand : BasicDICommand, ICommand
    {
        [CommandOption("out", 'o', Description = "Output path for game-overrides.json (the worker's ConfDir).", IsRequired = true)]
        public string Out { get; set; } = default!;

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeGameConfigExportCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        // Matches the Go nanoarch.GameOverride shape (lowercase json tags). Fps/Options/HwContext omitted
        // when empty (DefaultIgnoreCondition.WhenWritingNull below).
        private sealed class Entry
        {
            public double? fps { get; set; }
            public Dictionary<string, string>? options { get; set; }
            public string? hwContext { get; set; }
        }

        // Only "gl"/"vulkan" are valid hw-context values; anything else (typo, stale value) is dropped so a
        // bad row can't pin a game onto a nonexistent context — mirrors nanoarch.GameHwContext's guard.
        private static string? NormalizeHwContext(string? v)
        {
            v = v?.Trim().ToLowerInvariant();
            return v is "gl" or "vulkan" ? v : null;
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            var profiles = await db.ArcadeGameProfiles.AsNoTracking().ToListAsync();
            if (profiles.Count == 0)
            {
                w.WriteLine("No ArcadeGameProfile rows — writing an empty manifest.");
            }

            var manifest = new Dictionary<string, Entry>(StringComparer.Ordinal);
            int profilesApplied = 0, romsCovered = 0, emptyProfiles = 0;

            foreach (var p in profiles)
            {
                Dictionary<string, string>? opts = null;
                if (!string.IsNullOrWhiteSpace(p.CoreOptionsJson))
                {
                    try { opts = JsonSerializer.Deserialize<Dictionary<string, string>>(p.CoreOptionsJson!); }
                    catch (Exception ex) { w.WriteLine($"  WARN [{p.System}] {p.TitleKey}: bad CoreOptionsJson ({ex.Message}) — skipping its options."); }
                }

                var hwContext = NormalizeHwContext(p.HwContext);
                bool hasFps = p.ForcedFps is > 0;
                bool hasOpts = opts is { Count: > 0 };
                bool hasHw = hwContext is not null;
                if (!hasFps && !hasOpts && !hasHw) { emptyProfiles++; continue; }

                // Fan the identity out to every ROM whose normalized Title matches. TitleKey is the
                // lowercased Title; compare case-insensitively so the manifest covers all variants.
                var key = p.TitleKey.Trim();
                var roms = await db.ArcadeGames.AsNoTracking()
                    .Where(g => g.System == p.System && g.Title.ToLower() == key)
                    .Select(g => g.CloudRetroGameKey)
                    .ToListAsync();

                if (roms.Count == 0) { w.WriteLine($"  note [{p.System}] \"{p.TitleKey}\": profile matches no ROM rows."); continue; }

                profilesApplied++;
                foreach (var romKey in roms)
                {
                    if (string.IsNullOrEmpty(romKey)) continue;
                    manifest[romKey] = new Entry
                    {
                        fps = hasFps ? p.ForcedFps : null,
                        options = hasOpts ? opts : null,
                        hwContext = hwContext,
                    };
                    romsCovered++;
                }
            }

            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

            var outPath = Path.GetFullPath(Out);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            // Atomic-ish write so the worker never reads a half-written manifest.
            var tmp = outPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, outPath, overwrite: true);

            w.WriteLine($"Wrote {outPath}: {manifest.Count} ROM entries from {profilesApplied} profile(s) " +
                        $"({romsCovered} rom matches, {emptyProfiles} empty profile(s) skipped).");
        }
    }
}
