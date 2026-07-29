using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Channels
{
    /// <summary>
    /// The channel-quality regression harness. Reads <c>docs/channel-canon.json</c> — per-channel
    /// hand-labelled canon (titles that MUST be in the eligible pool, titles that MUST NOT be) —
    /// and resolves each against the REAL engine (<see cref="ChannelScheduleService.GetEligibleAsync"/>,
    /// so file presence, rating gates, related-misc and majority-path all apply). Any violation
    /// prints loudly and exits nonzero, so "Family Movie Night lost Star Wars" or "Anchorman leaked
    /// back in" can never regress silently again. Run it after every catalog edit, tag sweep, or
    /// engine change.
    ///
    /// <para>Default mode pools each channel's STORED FilterJson (post-apply truth, by CatalogKey);
    /// <c>--from-catalog</c> builds a probe filter from <see cref="ChannelCatalog"/> instead — the
    /// pre-apply rehearsal, so tag sweeps can be validated before any FilterJson changes. Canon
    /// entries are case-insensitive title substrings; keep them distinctive ("Toy Story", never "It").</para>
    /// </summary>
    [Command("channel-canon", Description = "Verify per-channel canon (must-include / must-exclude titles) against the real eligible pools; exits nonzero on violations.")]
    public class ChannelCanonCommand : BasicDICommand, ICommand
    {
        [CommandOption("file", 'f', Description = "Canon file (default: docs/channel-canon.json).")]
        public string File { get; set; } = System.IO.Path.Combine("docs", "channel-canon.json");

        [CommandOption("from-catalog", Description = "Pool the code-defined catalog filters instead of the stored FilterJson — the pre-apply rehearsal. (Genre names are resolved; credit-name channels aren't supported here — use the default DB mode for those.)")]
        public bool FromCatalog { get; set; }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ChannelCanonCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            if (!System.IO.File.Exists(File))
                throw new CommandException($"Canon file not found: {File}", 1);
            List<CanonDto>? canon;
            try
            {
                canon = JsonSerializer.Deserialize<List<CanonDto>>(await System.IO.File.ReadAllTextAsync(File), JsonOpts);
            }
            catch (JsonException ex) { throw new CommandException($"Bad canon JSON: {ex.Message}", 1); }
            if (canon is null || canon.Count == 0) { w.WriteLine("Canon file is empty — nothing to check."); return; }

            await using var db = await dbFactory.CreateDbContextAsync();
            using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 200_000 });
            var svc = new ChannelScheduleService(db, cache, NullLogger<ChannelScheduleService>.Instance);

            // --from-catalog: resolve defs the way channel-catalog does, minus credit names (the
            // curated channels this harness guards are tag/genre/mpaa-based; credit channels are
            // checked in DB mode where their FilterJson already carries resolved person ids).
            Dictionary<string, string>? catalogJson = null;
            if (FromCatalog)
            {
                var genres = await db.Genres.ToDictionaryAsync(g => g.Name, g => g.Id, StringComparer.OrdinalIgnoreCase);
                catalogJson = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var def in ChannelCatalog.All)
                {
                    if (def.CreditNames.Count > 0) continue; // unsupported here by design
                    var f = def.Filter;
                    f.Kinds = def.Kinds;
                    f.GenreMode = def.GenreModeAll ? "all" : "any";
                    f.GenreIds = def.GenreNames
                        .Select(n => genres.TryGetValue(n, out var id) ? id : -1)
                        .Where(id => id > 0).ToList();
                    catalogJson[def.Key] = f.ToJson();
                }
            }

            var channelsByKey = (await db.Channels.Where(c => c.CatalogKey != null).ToListAsync())
                .ToDictionary(c => c.CatalogKey!, StringComparer.OrdinalIgnoreCase);

            int violations = 0, checkedChannels = 0;
            foreach (var entry in canon)
            {
                var key = (entry.Channel ?? "").Trim();
                if (key.Length == 0) continue;

                Channel probe;
                if (FromCatalog)
                {
                    if (catalogJson!.TryGetValue(key, out var json))
                        probe = new Channel { Id = 0, FilterJson = json };
                    else
                    { w.WriteLine($"FAIL [{key}]: not in the code catalog (or credit-based — use DB mode)"); violations++; continue; }
                }
                else
                {
                    if (!channelsByKey.TryGetValue(key, out var ch) || !ch.Enabled)
                    { w.WriteLine($"FAIL [{key}]: no enabled channel with this CatalogKey"); violations++; continue; }
                    probe = ch;
                }

                var (pool, _) = await svc.GetEligibleAsync(probe);
                var titles = await ResolveTitlesAsync(db, pool);
                checkedChannels++;

                foreach (var must in entry.MustInclude ?? new List<string>())
                {
                    if (!titles.Any(t => t.Contains(must, StringComparison.OrdinalIgnoreCase)))
                    { w.WriteLine($"FAIL [{key}] missing: \"{must}\""); violations++; }
                }
                foreach (var mustNot in entry.MustExclude ?? new List<string>())
                {
                    var hits = titles.Where(t => t.Contains(mustNot, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
                    if (hits.Count > 0)
                    { w.WriteLine($"FAIL [{key}] leaked: \"{mustNot}\" -> {string.Join("; ", hits)}"); violations++; }
                }
                if (pool.Count < 8)
                { w.WriteLine($"FAIL [{key}] THIN pool: {pool.Count} item(s)"); violations++; }
            }

            w.WriteLine($"\nchannel-canon ({(FromCatalog ? "catalog" : "db")} mode): {checkedChannels} channel(s) checked, {violations} violation(s).");
            if (violations > 0)
                throw new CommandException($"{violations} canon violation(s).", 1);
            w.WriteLine("ALL GREEN.");
        }

        /// <summary>Display titles for a pool: movie + misc titles by playable, episodes collapsed to
        /// their series title (canon speaks in shows, not episodes).</summary>
        private static async Task<List<string>> ResolveTitlesAsync(MovieDb db, List<ChannelScheduleService.EligibleItem> pool)
        {
            var playableIds = pool.Where(p => p.GroupId == 0).Select(p => p.PlayableId).Distinct().ToList();
            var seriesIds = pool.Where(p => p.GroupId != 0).Select(p => p.GroupId).Distinct().ToList();

            var titles = new List<string>();
            // Chunk the IN lists so a whole-library pool (the empty-filter tell) can't blow the query.
            foreach (var chunk in playableIds.Chunk(2000))
            {
                titles.AddRange(await db.Movies.Where(m => m.PlayableId != null && chunk.Contains(m.PlayableId.Value))
                    .Select(m => m.Title).ToListAsync());
                titles.AddRange(await db.MiscVideos.Where(mv => chunk.Contains(mv.PlayableId))
                    .Select(mv => mv.Title).ToListAsync());
            }
            foreach (var chunk in seriesIds.Chunk(2000))
                titles.AddRange(await db.Series.Where(s => chunk.Contains(s.Id)).Select(s => s.Title).ToListAsync());
            return titles;
        }

        private sealed class CanonDto
        {
            public string? Channel { get; set; }
            public List<string>? MustInclude { get; set; }
            public List<string>? MustExclude { get; set; }
        }
    }
}
