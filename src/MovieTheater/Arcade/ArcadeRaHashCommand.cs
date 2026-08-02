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
            var systems = ArcadeRaHasher.Console.Keys.ToList();
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
            var requests = new List<ArcadeRaHasher.Item>();
            foreach (var row in batch)
            {
                var path = row.SourceArchivePath ?? "";
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    missing.Add(row.Id);
                    continue;
                }
                requests.Add(new ArcadeRaHasher.Item(row.Id, path, ArcadeRaHasher.ConsoleId(row.System)));
            }

            var hashes = await ArcadeRaHasher.HashAsync(Hasher, requests, m => writer.WriteLine(m));

            var hashedIds = requests.Where(r => hashes.ContainsKey(r.Id)).Select(r => r.Id).ToList();
            var unhashableIds = requests.Where(r => !hashes.ContainsKey(r.Id)).Select(r => r.Id).ToList();

            if (Apply)
            {
                var now = DateTime.UtcNow;
                var ids = requests.Select(r => r.Id).ToList();
                var rows = await db.ArcadeGames.Where(g => ids.Contains(g.Id)).ToListAsync();
                foreach (var g in rows)
                {
                    // Stamp even a failure: it is what tells the next run "already tried, do not
                    // re-read this file", which is the difference between a sweep that converges and
                    // one that re-hashes the same broken dumps forever.
                    g.RaHash = hashes.TryGetValue(g.Id, out var h) ? h : null;
                    g.RaHashedUtc = now;
                }
                await db.SaveChangesAsync();
            }

            var nextAfterId = batch[^1].Id;
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                apply = Apply,
                processed = batch.Count,
                hashed = hashedIds.Count,
                unhashable = unhashableIds.Count,
                missing = missing.Count,
                remaining = Math.Max(0, remainingBefore - batch.Count),
                nextAfterId,
                done = remainingBefore - batch.Count <= 0,
            }));

            // A few concrete examples make a dry run reviewable; the counts alone never show WHICH dump
            // could not be read.
            foreach (var id in unhashableIds.Take(10))
            {
                var row = batch.First(b => b.Id == id);
                writer.WriteLine($"  unhashable  [{row.System}] {row.Title} :: {row.SourceArchivePath}");
            }
            foreach (var id in missing.Take(10))
            {
                var row = batch.First(b => b.Id == id);
                writer.WriteLine($"  file gone   [{row.System}] {row.Title} :: {row.SourceArchivePath}");
            }
        }

    }
}
