using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Channels
{
    /// <summary>
    /// The durable trace of channel viewing. TV playback reports <c>passive</c> progress (so
    /// MoviePlaybackProgress never sees it) and live presence is a 30-second in-memory dictionary —
    /// before this service the family's channel watching left no record at all, and channel curation
    /// had no feedback loop. The /Now poll (the canonical "user X is watching channel Y" beat,
    /// every ~10s per viewer) calls <see cref="RecordBeat"/>; deltas accumulate in memory and a
    /// background flusher upserts one <see cref="ChannelViewStat"/> row per user/channel/local-day
    /// every few minutes — friends-scale, one small write per flush, nothing on the hot path.
    /// </summary>
    public class ChannelViewTelemetryService : BackgroundService
    {
        // Mirror ChannelSkipService.ViewerTtl: a gap longer than this is a new sitting, not watch time.
        private static readonly TimeSpan MaxBeatGap = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FlushEvery = TimeSpan.FromMinutes(5);

        private readonly object gate = new();
        private readonly Dictionary<(int UserId, int ChannelId), DateTime> lastBeat = new();
        private Dictionary<(int UserId, int ChannelId, DateOnly Date), double> pending = new();

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly ILogger<ChannelViewTelemetryService> logger;
        private readonly TimeZoneInfo tz;

        public ChannelViewTelemetryService(
            IDbContextFactory<MovieDb> dbFactory,
            MovieTheaterConfiguration config,
            ILogger<ChannelViewTelemetryService> logger)
        {
            this.dbFactory = dbFactory;
            this.logger = logger;
            // The household's local day, not the UTC date a movie night straddles. Config override
            // for portability; pods run UTC.
            var tzId = string.IsNullOrWhiteSpace(config.TelemetryTimeZone) ? "America/New_York" : config.TelemetryTimeZone;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
            catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.Utc; logger.LogWarning("Unknown TelemetryTimeZone '{Tz}' — using UTC.", tzId); }
        }

        /// <summary>Called from the /Now poll. Credits the elapsed time since this viewer's previous
        /// beat on this channel (capped at <see cref="MaxBeatGap"/> — a longer gap means they left
        /// and came back, and idle time must not count). Duplicate or racing calls are harmless:
        /// time-based deltas can't double-count.</summary>
        public void RecordBeat(int userId, int channelId, DateTime utcNow)
        {
            lock (gate)
            {
                if (lastBeat.TryGetValue((userId, channelId), out var prev))
                {
                    var delta = utcNow - prev;
                    if (delta > TimeSpan.Zero && delta <= MaxBeatGap)
                    {
                        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz));
                        var key = (userId, channelId, localDate);
                        pending[key] = pending.GetValueOrDefault(key) + delta.TotalSeconds;
                    }
                }
                lastBeat[(userId, channelId)] = utcNow;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(FlushEvery, stoppingToken); }
                catch (OperationCanceledException) { break; }
                await FlushAsync(CancellationToken.None);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Flush the tail so a deploy doesn't drop the last few minutes of an evening.
            await FlushAsync(cancellationToken);
            await base.StopAsync(cancellationToken);
        }

        private async Task FlushAsync(CancellationToken cancel)
        {
            Dictionary<(int UserId, int ChannelId, DateOnly Date), double> batch;
            lock (gate)
            {
                if (pending.Count == 0)
                {
                    // Also shed stale lastBeat entries so the map doesn't grow forever.
                    var cutoff = DateTime.UtcNow - TimeSpan.FromHours(6);
                    foreach (var k in lastBeat.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                        lastBeat.Remove(k);
                    return;
                }
                batch = pending;
                pending = new Dictionary<(int, int, DateOnly), double>();
            }

            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancel);
                var userIds = batch.Keys.Select(k => k.UserId).Distinct().ToList();
                var channelIds = batch.Keys.Select(k => k.ChannelId).Distinct().ToList();
                var dates = batch.Keys.Select(k => k.Date).Distinct().ToList();
                var rows = await db.ChannelViewStats
                    .Where(s => userIds.Contains(s.UserId) && channelIds.Contains(s.ChannelId) && dates.Contains(s.Date))
                    .ToListAsync(cancel);
                var now = DateTime.UtcNow;
                foreach (var (key, seconds) in batch)
                {
                    var row = rows.FirstOrDefault(r => r.UserId == key.UserId && r.ChannelId == key.ChannelId && r.Date == key.Date);
                    if (row == null)
                        db.ChannelViewStats.Add(new ChannelViewStat
                        { UserId = key.UserId, ChannelId = key.ChannelId, Date = key.Date, Seconds = (int)Math.Round(seconds), UpdatedUtc = now });
                    else
                    { row.Seconds += (int)Math.Round(seconds); row.UpdatedUtc = now; }
                }
                await db.SaveChangesAsync(cancel);
            }
            catch (Exception ex)
            {
                // Never let telemetry take down the host (e.g. table not migrated yet). Re-queue the
                // batch so the next flush retries — folding into whatever accumulated meanwhile.
                logger.LogWarning(ex, "Channel view telemetry flush failed; will retry next cycle.");
                lock (gate)
                {
                    foreach (var (key, seconds) in batch)
                        pending[key] = pending.GetValueOrDefault(key) + seconds;
                }
            }
        }
    }
}
