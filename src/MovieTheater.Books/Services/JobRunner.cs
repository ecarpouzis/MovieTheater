using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MovieTheater.Books.Services
{
    /// <summary>One batch's report, in the shape every job here answers with.</summary>
    public sealed record JobProgress(long Processed, long Remaining, string? NextCursor, long Failed, string? Line)
    {
        public static readonly JobProgress Empty = new(0, 0, null, 0, null);
    }

    /// <summary>What a status endpoint shows for one job kind.</summary>
    public sealed record JobStatus(
        string Kind, string State, long Processed, long Remaining, string? NextCursor, long Failed,
        DateTime? StartedAt, DateTime? FinishedAt, string? Error, string? LastLine, int Batches);

    /// <summary>
    /// The one place a long job runs behind an HTTP request.
    ///
    /// <para><b>A controller never loops.</b> An admin endpoint starts a job, asks for its status, or stops it;
    /// the loop lives here, on a background task, and the request returns immediately with the FIRST batch's
    /// numbers and a status URL. That is the whole reason this class exists: an endpoint that ran a 141k-row
    /// walk to completion inside one request would be killed by the first proxy timeout, and the work would be
    /// lost in a way nothing could report on.</para>
    ///
    /// <para><b>One job at a time per KIND.</b> Two scans would fight over the same cursor; a scan and a
    /// thumbnail pass would not. Starting a kind that is already running is a 409, never a silent second run.</para>
    ///
    /// <para><b>Cancellation is cooperative and safe</b> because every job commits its cursor WITH its batch:
    /// stopping mid-run costs at most one batch and the next start resumes from where it stopped. The status
    /// snapshot survives the run so an operator can read what happened after it finished.</para>
    /// </summary>
    public sealed class JobRunner
    {
        private sealed class Entry
        {
            public string Kind = "";
            public string State = "idle";
            public long Processed, Remaining, Failed;
            public string? NextCursor, Error, LastLine;
            public int Batches;
            public DateTime? StartedAt, FinishedAt;
            public CancellationTokenSource? Cts;
            public Task? Task;
        }

        private readonly ConcurrentDictionary<string, Entry> jobs = new(StringComparer.OrdinalIgnoreCase);
        private readonly IServiceScopeFactory scopes;
        private readonly ILogger<JobRunner> logger;

        public JobRunner(IServiceScopeFactory scopes, ILogger<JobRunner> logger)
        {
            this.scopes = scopes;
            this.logger = logger;
        }

        /// <summary>A job's body: run ONE bounded batch against a fresh scope and report it.</summary>
        public delegate Task<JobProgress> BatchStep(IServiceProvider services, CancellationToken ct);

        public bool IsRunning(string kind) => jobs.TryGetValue(kind, out var e) && e.State == "running";

        public IReadOnlyList<JobStatus> All() => jobs.Values.Select(Snapshot).OrderBy(s => s.Kind, StringComparer.Ordinal).ToList();

        public JobStatus? Status(string kind) => jobs.TryGetValue(kind, out var e) ? Snapshot(e) : null;

        /// <summary>
        /// Start a job. Runs the FIRST batch inline so the caller gets real numbers back rather than a promise,
        /// then hands the rest to a background loop. Throws when that kind is already running.
        /// </summary>
        public async Task<JobStatus> StartAsync(string kind, BatchStep step, int maxBatches = 0, CancellationToken ct = default)
        {
            var entry = jobs.GetOrAdd(kind, k => new Entry { Kind = k });
            lock (entry)
            {
                if (entry.State == "running") throw new InvalidOperationException($"Job '{kind}' is already running.");
                entry.State = "running";
                entry.Processed = entry.Remaining = entry.Failed = 0;
                entry.NextCursor = entry.Error = entry.LastLine = null;
                entry.Batches = 0;
                entry.StartedAt = DateTime.UtcNow;
                entry.FinishedAt = null;
                entry.Cts = new CancellationTokenSource();
            }

            var token = entry.Cts!.Token;
            try
            {
                var first = await RunOneAsync(step, token);
                Apply(entry, first);
                if (first.Processed == 0) { Finish(entry, null); return Snapshot(entry); }
            }
            catch (Exception ex)
            {
                Finish(entry, ex.Message);
                throw;
            }

            entry.Task = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested && (maxBatches <= 0 || entry.Batches < maxBatches))
                    {
                        var progress = await RunOneAsync(step, token);
                        // The no-progress safety break: a batch that moved nothing is the end of the run, or a
                        // defect. Either way, looping again would spin.
                        var stalled = progress.Processed == 0 || progress.NextCursor == entry.NextCursor;
                        Apply(entry, progress);
                        if (stalled) break;
                    }
                    Finish(entry, null);
                }
                catch (OperationCanceledException) { Finish(entry, null, "stopped"); }
                catch (Exception ex)
                {
                    logger.LogError(ex, "job {Kind} failed", kind);
                    Finish(entry, ex.Message);
                }
            }, CancellationToken.None);

            return Snapshot(entry);
        }

        /// <summary>Ask a job to stop. It stops at its next batch boundary, with its cursor committed.</summary>
        public bool Stop(string kind)
        {
            if (!jobs.TryGetValue(kind, out var entry) || entry.State != "running") return false;
            entry.Cts?.Cancel();
            entry.State = "stopping";
            return true;
        }

        private async Task<JobProgress> RunOneAsync(BatchStep step, CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            return await step(scope.ServiceProvider, ct);
        }

        private static void Apply(Entry entry, JobProgress progress)
        {
            lock (entry)
            {
                entry.Processed += progress.Processed;
                entry.Remaining = progress.Remaining;
                entry.NextCursor = progress.NextCursor;
                entry.Failed += progress.Failed;
                entry.LastLine = progress.Line;
                entry.Batches++;
            }
        }

        private static void Finish(Entry entry, string? error, string state = "done")
        {
            lock (entry)
            {
                entry.State = error == null ? state : "failed";
                entry.Error = error;
                entry.FinishedAt = DateTime.UtcNow;
                entry.Cts?.Dispose();
                entry.Cts = null;
            }
        }

        private static JobStatus Snapshot(Entry e)
        {
            lock (e)
                return new JobStatus(e.Kind, e.State, e.Processed, e.Remaining, e.NextCursor, e.Failed,
                    e.StartedAt, e.FinishedAt, e.Error, e.LastLine, e.Batches);
        }
    }
}
