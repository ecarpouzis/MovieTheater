using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Books.Db;
using MovieTheater.Core;

namespace MovieTheater.BooksHost.Web
{
    /// <summary>
    /// Remembers the last identity payload seen per user in <see cref="KnownIdentity"/> so the cache warmer can
    /// fabricate the principals whose cache keys (username + ceiling + admin) real requests will read. One write
    /// per user per CHANGE: the in-memory last-seen copy makes the steady state free, and the persisted row
    /// makes a restart's warm-up cold for a user only until their first request.
    /// </summary>
    public sealed class KnownIdentityRecorder
    {
        private readonly ConcurrentDictionary<int, (string Username, bool IsAdmin, int Ceiling)> lastSeen = new();
        private readonly ConcurrentDictionary<int, DateTime> lastStamped = new();
        private static readonly TimeSpan RestampEvery = TimeSpan.FromHours(6);

        public async Task RecordAsync(IServiceProvider services, BooksIdentityToken.Payload payload)
        {
            var now = DateTime.UtcNow;
            var facts = (payload.Username, payload.IsAdmin, payload.MaturityCeiling);
            var changed = !lastSeen.TryGetValue(payload.UserId, out var seen) || seen != facts;
            var stale = !lastStamped.TryGetValue(payload.UserId, out var at) || now - at > RestampEvery;
            if (!changed && !stale) return;

            var db = services.GetService<BooksDb>();
            if (db == null) return; // a host with no catalog configured still answers ping
            var row = await db.KnownIdentities.FindAsync(payload.UserId);
            if (row == null) db.KnownIdentities.Add(new KnownIdentity { UserId = payload.UserId, Username = payload.Username, IsAdmin = payload.IsAdmin, MaturityCeiling = payload.MaturityCeiling, LastSeenAt = now });
            else { row.Username = payload.Username; row.IsAdmin = payload.IsAdmin; row.MaturityCeiling = payload.MaturityCeiling; row.LastSeenAt = now; }
            await db.SaveChangesAsync();
            // Memoize only AFTER the row is written: a failed write must be retried by the next request, not
            // remembered as done (the first production ping hit a missing native SQLite library and the memo
            // then hid it behind a 200 for every request after).
            lastSeen[payload.UserId] = facts;
            lastStamped[payload.UserId] = now;
        }

        /// <summary>For tests and the warmer: what has been seen since start.</summary>
        public IReadOnlyDictionary<int, (string Username, bool IsAdmin, int Ceiling)> LastSeen => lastSeen;
    }
}
