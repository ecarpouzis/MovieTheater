using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MovieTheater.Requests
{
    /// <summary>
    /// The request queue's background sweep: every half hour, run <see cref="ContentRequestMatcher.SweepAsync"/>
    /// over the open requests that have no standing proposal, in bounded chunks until it reports nothing
    /// left. This is the "alert when it's imported" half of the feature — content lands through a dozen
    /// different ingest paths (the library sync, the ingest review, the music / boardgame / arcade CLIs),
    /// and rather than hook each one, the sweep notices after the fact and the Requests page (and the
    /// admin's toast) report "N requests look like they've been added — confirm".
    ///
    /// <para>Same shape as the other maintenance loops (WatchpartyReaperService): a BackgroundService with
    /// scoped DB access, a loop that survives a failed tick, one bounded chunk per step. Off in Development
    /// — the dev connection IS the live database, and the admin's Sweep button exists for a hand pass.</para>
    /// </summary>
    public class ContentRequestSweepService : BackgroundService
    {
        private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan Tick = TimeSpan.FromMinutes(30);
        private const int ChunkSize = 50;
        private const int MaxChunksPerTick = 20;

        private readonly IServiceScopeFactory scopeFactory;
        private readonly ContentRequestSweepOptions options;
        private readonly ILogger<ContentRequestSweepService> logger;

        // Where the last tick stopped. A tick that hits its chunk budget resumes HERE next time rather
        // than from 0, so a long queue is walked end to end across ticks instead of its head being
        // re-checked forever; a tick that reaches the end wraps to 0 (new content can answer an old ask).
        private int cursor;

        public ContentRequestSweepService(IServiceScopeFactory scopeFactory, ContentRequestSweepOptions options, ILogger<ContentRequestSweepService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.options = options;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Enabled)
            {
                logger.LogInformation("Request sweep disabled (Development).");
                return;
            }
            try { await Task.Delay(FirstDelay, stoppingToken); } catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Request sweep tick failed; will retry next tick.");
                }

                try { await Task.Delay(Tick, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task TickAsync(CancellationToken cancel)
        {
            using var scope = scopeFactory.CreateScope();
            var matcher = scope.ServiceProvider.GetRequiredService<ContentRequestMatcher>();
            int from = cursor, checkedTotal = 0, proposedTotal = 0;
            var reachedEnd = false;
            for (var chunk = 0; chunk < MaxChunksPerTick; chunk++)
            {
                var result = await matcher.SweepAsync(from, ChunkSize, cancel);
                checkedTotal += result.Checked;
                proposedTotal += result.Proposed;
                if (result.NextFrom == null || result.Checked == 0) { reachedEnd = true; break; }
                from = result.NextFrom.Value;
            }
            cursor = reachedEnd ? 0 : from;
            if (checkedTotal > 0)
                logger.LogInformation("Request sweep: checked {Checked} open requests, proposed {Proposed} matches (resume at {Cursor}).", checkedTotal, proposedTotal, cursor);
        }
    }

    public sealed class ContentRequestSweepOptions
    {
        public bool Enabled { get; init; } = true;
    }
}
