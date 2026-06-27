using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
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
    /// Dumps N days of every channel's lineup to CSV so the catalog + filters can be eyeballed like the
    /// on-screen guide. Uses the real <see cref="ChannelScheduleService"/> — same seeds, strategies,
    /// durations and eligible sets the live guide serves — so what the CSV shows is what viewers would see.
    /// Materializing to the horizon writes the same forward ChannelScheduleItem rows the background
    /// maintainer would (deterministic, never rewritten, pruned when old): a benign extension of the lineup.
    ///
    /// Writes two files:
    ///   guide-grid.csv   — EPG layout: a row per channel, time across in fixed slots, cell = what's airing.
    ///   guide-linear.csv — a row per program (channel, start/end, duration, kind, title) for sort/filter/pivot.
    /// </summary>
    [Command("export-guide", Description = "Export N days of channel programming to CSV (grid + linear).")]
    public class ExportGuideCommand : BasicDICommand, ICommand
    {
        [CommandOption("days", Description = "How many days of programming to export (default 3).")]
        public int Days { get; set; } = 3;

        [CommandOption("slot-minutes", Description = "Grid time-slot granularity in minutes (default 30).")]
        public int SlotMinutes { get; set; } = 30;

        [CommandOption("out-dir", Description = "Directory for the CSV files (default current directory).")]
        public string? OutDir { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ExportGuideCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            Days = Math.Clamp(Days, 1, 14);
            SlotMinutes = Math.Clamp(SlotMinutes, 5, 240);
            var outDir = string.IsNullOrWhiteSpace(OutDir) ? Directory.GetCurrentDirectory() : OutDir!;
            Directory.CreateDirectory(outDir);

            using var db = await dbFactory.CreateDbContextAsync();
            using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 200_000 });
            var svc = new ChannelScheduleService(db, cache, NullLogger<ChannelScheduleService>.Instance);

            // Align the window to local midnight so day boundaries are clean in a spreadsheet. The schedule
            // is stored UTC; we read/convert at the edges only.
            var nowUtc = DateTime.UtcNow;
            var startLocal = DateTime.Now.Date;
            var endLocal = startLocal.AddDays(Days);
            var startUtc = startLocal.ToUniversalTime();
            var endUtc = endLocal.ToUniversalTime();

            // Channel order mirrors the guide: shelf (category) order, then SortOrder, then Id.
            Dictionary<string, int> shelfOrder;
            try { shelfOrder = await db.ChannelShelves.ToDictionaryAsync(s => s.Category, s => s.SortOrder); }
            catch { shelfOrder = new(); }
            int ShelfRank(string? cat) => cat != null && shelfOrder.TryGetValue(cat, out var so) ? so : int.MaxValue;

            var channels = (await db.Channels.Where(c => c.Enabled).ToListAsync())
                .Where(c => ChannelSeason.InSeason(c, nowUtc))
                .OrderBy(c => ShelfRank(c.Category))
                .ThenBy(c => c.SortOrder)
                .ThenBy(c => c.Id)
                .ToList();

            console.Output.WriteLine($"Exporting {Days} day(s) for {channels.Count} channel(s) from {startLocal:yyyy-MM-dd} (local)…");

            // Materialize each channel to the horizon and collect its windowed lineup. One channel per
            // iteration with a progress line — the eligible-set build is heavy, so this can take a few minutes.
            var lineupByChannel = new Dictionary<int, List<ChannelScheduleItem>>();
            var allPlayableIds = new HashSet<int>();
            int n = 0;
            foreach (var ch in channels)
            {
                n++;
                List<ChannelScheduleItem> items;
                try
                {
                    items = await svc.EnsureScheduleAsync(ch, endUtc);
                }
                catch (Exception ex)
                {
                    console.Output.WriteLine($"  [{n}/{channels.Count}] {ch.Name}: !! {ex.GetType().Name}: {ex.Message}");
                    lineupByChannel[ch.Id] = new List<ChannelScheduleItem>();
                    continue;
                }
                var windowed = items.Where(i => i.EndUtc > startUtc && i.StartUtc < endUtc)
                    .OrderBy(i => i.StartUtc).ToList();
                lineupByChannel[ch.Id] = windowed;
                foreach (var i in windowed) allPlayableIds.Add(i.PlayableId);
                console.Output.WriteLine($"  [{n}/{channels.Count}] {ch.Name}: {windowed.Count} programs");
            }

            console.Output.WriteLine($"Resolving {allPlayableIds.Count} title(s)…");
            var titles = await ResolveTitlesAsync(db, allPlayableIds);

            string Title(int pid) => titles.TryGetValue(pid, out var t) ? t.Title : $"(playable {pid})";
            string Kind(int pid) => titles.TryGetValue(pid, out var t) ? t.Kind : "?";

            // Channel numbers follow guide order (1-based), so the CSV lines up with the on-screen lineup.
            var channelNo = new Dictionary<int, int>();
            for (int i = 0; i < channels.Count; i++) channelNo[channels[i].Id] = i + 1;

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
            var linearPath = Path.Combine(outDir, $"guide-linear-{stamp}.csv");
            var gridPath = Path.Combine(outDir, $"guide-grid-{stamp}.csv");

            WriteLinear(linearPath, channels, channelNo, lineupByChannel, Title, Kind);
            WriteGrid(gridPath, channels, channelNo, lineupByChannel, Title, startLocal, endLocal);

            console.Output.WriteLine($"\nWrote:\n  {linearPath}\n  {gridPath}");
        }

        private static void WriteLinear(
            string path, List<Channel> channels, Dictionary<int, int> channelNo,
            Dictionary<int, List<ChannelScheduleItem>> lineup,
            Func<int, string> title, Func<int, string> kind)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Ch,Channel,Category,Strategy,Day,Start,End,DurationMin,Kind,Title,PlayableId,StartUtc");
            foreach (var ch in channels)
            {
                var strategy = string.IsNullOrWhiteSpace(ch.ScheduleStrategy) ? ch.ShuffleMode : ch.ScheduleStrategy!;
                foreach (var i in lineup[ch.Id])
                {
                    var sLocal = DateTime.SpecifyKind(i.StartUtc, DateTimeKind.Utc).ToLocalTime();
                    var eLocal = DateTime.SpecifyKind(i.EndUtc, DateTimeKind.Utc).ToLocalTime();
                    var dur = (int)Math.Round((i.EndUtc - i.StartUtc).TotalMinutes);
                    sb.Append(channelNo[ch.Id]).Append(',')
                      .Append(Csv(ch.Name)).Append(',')
                      .Append(Csv(ch.Category ?? "")).Append(',')
                      .Append(Csv(strategy)).Append(',')
                      .Append(Csv(sLocal.ToString("ddd yyyy-MM-dd"))).Append(',')
                      .Append(Csv(sLocal.ToString("HH:mm"))).Append(',')
                      .Append(Csv(eLocal.ToString("HH:mm"))).Append(',')
                      .Append(dur).Append(',')
                      .Append(Csv(kind(i.PlayableId))).Append(',')
                      .Append(Csv(title(i.PlayableId))).Append(',')
                      .Append(i.PlayableId).Append(',')
                      .Append(Csv(DateTime.SpecifyKind(i.StartUtc, DateTimeKind.Utc).ToString("o")))
                      .Append('\n');
                }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private void WriteGrid(
            string path, List<Channel> channels, Dictionary<int, int> channelNo,
            Dictionary<int, List<ChannelScheduleItem>> lineup,
            Func<int, string> title, DateTime startLocal, DateTime endLocal)
        {
            // Slot starts across the whole window (local), one column each.
            var slots = new List<DateTime>();
            for (var t = startLocal; t < endLocal; t = t.AddMinutes(SlotMinutes))
                slots.Add(t);
            var slotUtc = slots.Select(s => s.ToUniversalTime()).ToList();

            var sb = new StringBuilder();
            sb.Append("Ch,Channel");
            foreach (var s in slots)
                sb.Append(',').Append(Csv(s.ToString("ddd M/d HH:mm")));
            sb.Append('\n');

            foreach (var ch in channels)
            {
                var items = lineup[ch.Id]; // already ordered by StartUtc
                sb.Append(channelNo[ch.Id]).Append(',').Append(Csv(ch.Name));
                int p = 0;
                foreach (var su in slotUtc)
                {
                    // Advance to the item whose window contains this slot start.
                    while (p < items.Count && items[p].EndUtc <= su) p++;
                    string cell = (p < items.Count && items[p].StartUtc <= su && su < items[p].EndUtc)
                        ? title(items[p].PlayableId)
                        : "";
                    sb.Append(',').Append(Csv(cell));
                }
                sb.Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        // Resolve PlayableIds → display title + kind, mirroring ChannelController.TitlesForAsync. Chunked
        // so the IN list stays bounded.
        private static async Task<Dictionary<int, (string Title, string Kind)>> ResolveTitlesAsync(MovieDb db, IEnumerable<int> playableIds)
        {
            var map = new Dictionary<int, (string Title, string Kind)>();
            foreach (var batch in playableIds.Distinct().Chunk(1000))
            {
                var ids = batch.ToList();

                var movies = await db.Movies
                    .Where(m => m.PlayableId != null && ids.Contains(m.PlayableId.Value))
                    .Select(m => new { Pid = m.PlayableId!.Value, m.Title })
                    .ToListAsync();
                foreach (var m in movies) map[m.Pid] = (m.Title ?? "", "movie");

                var eps = await db.Episodes
                    .Where(e => e.PlayableId != null && ids.Contains(e.PlayableId.Value))
                    .Select(e => new { Pid = e.PlayableId!.Value, SeriesTitle = e.Series!.Title, e.SeasonNumber, e.EpisodeNumber, e.Title })
                    .ToListAsync();
                foreach (var e in eps)
                {
                    var code = $"S{e.SeasonNumber:00}E{e.EpisodeNumber:00}";
                    var t = string.IsNullOrWhiteSpace(e.Title)
                        ? $"{e.SeriesTitle} – {code}"
                        : $"{e.SeriesTitle} – {code} {e.Title}";
                    map[e.Pid] = (t, "series");
                }

                var misc = await db.MiscVideos
                    .Where(mv => ids.Contains(mv.PlayableId))
                    .Select(mv => new { mv.PlayableId, mv.Title })
                    .ToListAsync();
                foreach (var mv in misc)
                    map[mv.PlayableId] = (mv.Title ?? "", "misc");
            }
            return map;
        }

        // Minimal RFC-4180 escaping: quote when the field holds a comma, quote or newline.
        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool needs = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needs) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
