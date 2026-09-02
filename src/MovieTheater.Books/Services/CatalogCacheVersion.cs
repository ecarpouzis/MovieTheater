using Microsoft.Extensions.Primitives;

namespace MovieTheater.Books.Services
{
    /// <summary>
    /// The one lever that expires every cached CATALOG payload at once — the Explore pages, the browse facets,
    /// the group heads. Each of those entries is registered against <see cref="Token"/>; <see cref="Invalidate"/>
    /// trips it, the shared <c>IMemoryCache</c> evicts them, and the next request (or the warmer's next pass)
    /// composes fresh.
    ///
    /// <para><b>Why a token and not a shorter TTL.</b> The payloads are expensive and change only when the
    /// catalog does — a resolve pass, a scan, an insight import — so time is the wrong clock. Before this the
    /// warmer "re-ran" the actions after a data change, but each action answered from its own cache first, so
    /// a resolve left <c>/explore</c> serving the old page until the day seed rolled (the 2026-09-01 port
    /// finding). Now the warmer invalidates FIRST, then warms, and a live request never sees the gap: the
    /// warmer's own pass repopulates the same keys.</para>
    ///
    /// <para>The superseded <see cref="CancellationTokenSource"/> is cancelled and deliberately NOT disposed:
    /// the cache still consults the old token's <c>IsCancellationRequested</c> lazily on the next read of a
    /// stale entry, and a disposed source is exactly the object that read would touch. A handful of small
    /// objects per day is the price, and it is nothing.</para>
    /// </summary>
    public sealed class CatalogCacheVersion
    {
        private CancellationTokenSource source = new();
        private long generation;

        /// <summary>Monotonic; bumps on every <see cref="Invalidate"/>. Reported by the admin route, useful in logs.</summary>
        public long Generation => Interlocked.Read(ref generation);

        /// <summary>A fresh change token bound to the CURRENT generation — attach it to every catalog cache entry.</summary>
        public IChangeToken Token => new CancellationChangeToken(Volatile.Read(ref source).Token);

        /// <summary>Expire every entry registered against the current generation.</summary>
        public long Invalidate()
        {
            var old = Interlocked.Exchange(ref source, new CancellationTokenSource());
            var gen = Interlocked.Increment(ref generation);
            old.Cancel();
            return gen;
        }
    }
}
