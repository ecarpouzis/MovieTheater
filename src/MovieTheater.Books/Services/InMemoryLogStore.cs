using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MovieTheater.Books.Services
{
    /// <summary>One captured log line.</summary>
    public sealed record LogEntry(long Seq, DateTime At, string Level, string Category, string Message, string? Exception);

    /// <summary>
    /// A bounded ring buffer of the host's own log lines, so <c>GET /admin/logs</c> can answer "what did the
    /// scan just say" without anyone opening a file on the media host.
    ///
    /// <para><b>Bounded on purpose.</b> It holds the last <see cref="Capacity"/> lines and drops the oldest; a
    /// log viewer that grows without limit is a memory leak with a UI. Nothing here is persisted — this is the
    /// live tail, not the log.</para>
    /// </summary>
    public sealed class InMemoryLogStore
    {
        public const int Capacity = 2_000;

        private readonly ConcurrentQueue<LogEntry> entries = new();
        private long seq;

        public void Add(string level, string category, string message, string? exception)
        {
            entries.Enqueue(new LogEntry(Interlocked.Increment(ref seq), DateTime.UtcNow, level, category, message, exception));
            while (entries.Count > Capacity) entries.TryDequeue(out _);
        }

        /// <summary>The newest lines first, optionally only at or above a level and only after a sequence number.</summary>
        public IReadOnlyList<LogEntry> Tail(int count = 200, string? minLevel = null, long afterSeq = 0)
        {
            var floor = minLevel == null ? 0 : Rank(minLevel);
            return entries
                .Where(e => e.Seq > afterSeq && Rank(e.Level) >= floor)
                .OrderByDescending(e => e.Seq)
                .Take(Math.Clamp(count, 1, Capacity))
                .ToList();
        }

        public void Clear() { while (entries.TryDequeue(out _)) { } }

        private static int Rank(string level) => level switch
        {
            "Trace" => 0, "Debug" => 1, "Information" => 2, "Warning" => 3, "Error" => 4, "Critical" => 5, _ => 2,
        };
    }

    /// <summary>The <see cref="ILoggerProvider"/> that feeds <see cref="InMemoryLogStore"/>. Registered
    /// alongside the console provider, never instead of it.</summary>
    public sealed class InMemoryLoggerProvider : ILoggerProvider
    {
        private readonly InMemoryLogStore store;
        public InMemoryLoggerProvider(InMemoryLogStore store) => this.store = store;
        public ILogger CreateLogger(string categoryName) => new StoreLogger(store, categoryName);
        public void Dispose() { }

        private sealed class StoreLogger : ILogger
        {
            private readonly InMemoryLogStore store;
            private readonly string category;
            public StoreLogger(InMemoryLogStore store, string category) { this.store = store; this.category = category; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                store.Add(logLevel.ToString(), category, formatter(state, exception), exception?.ToString());
            }
        }
    }
}
