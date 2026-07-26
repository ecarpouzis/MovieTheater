using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A durable cache of RetroAchievements Web API responses, so the site fetches a given piece of RA data
    /// once for the WHOLE friend group and keeps it across pod restarts / replicas — RA is a community-run
    /// service that asks consumers to be gentle, and most of what we read (a game's achievement set, its
    /// leaderboard list) is essentially static. The controller layers a short in-memory cache on top for
    /// burst coalescing; THIS is the persistent tier, and also the stale-fallback when RA is unreachable.
    ///
    /// <para>One row per semantic request (<see cref="CacheKey"/> = the RA API path, e.g.
    /// <c>API_GetGameExtended.php?i=1234</c> — never the auth key). <see cref="Payload"/> is the raw JSON
    /// response; <see cref="FetchedUtc"/> drives freshness (each caller decides its own max age).</para>
    /// </summary>
    [Table("ArcadeRaApiCache")]
    public class ArcadeRaApiCache
    {
        [Key]
        public int Id { get; set; }

        /// <summary>The RA Web API path + query that produced this payload (the auth z/y params are appended
        /// only at fetch time and are NOT part of the key). Unique — see the index in <c>MovieDb</c>.</summary>
        [MaxLength(256)]
        public string CacheKey { get; set; } = default!;

        /// <summary>The raw JSON body RA returned. Re-parsed on read (cheap); we store the body, not a
        /// projection, so a caller can change what it extracts without a re-fetch.</summary>
        public string Payload { get; set; } = default!;

        /// <summary>When this payload was fetched from RA. Freshness = now - FetchedUtc &lt; the caller's max age.</summary>
        public DateTime FetchedUtc { get; set; }
    }
}
