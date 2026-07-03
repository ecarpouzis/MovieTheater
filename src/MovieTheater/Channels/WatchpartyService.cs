using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Channels
{
    /// <summary>
    /// In-memory lobby state for watch parties (docs/playlists-watchparty-plan.md) — a direct cousin of
    /// <see cref="ChannelSkipService"/>. A watch party is a private channel whose shared timeline waits in a
    /// lobby until everyone has pressed Begin; this tracks, per party channel, who is present and who is
    /// ready. Ephemeral and friends-scale, so it lives in memory (a singleton); the durable "has it begun"
    /// truth is <see cref="Db.Channel.WatchpartyStartedUtc"/>. Presence is inferred from the lobby heartbeat
    /// and TTL-pruned, so a member who closes their tab drops out of the roster (and the "all ready" test).
    /// </summary>
    public class WatchpartyService
    {
        // Must exceed the lobby's ~2s heartbeat with margin so a present member is never pruned between beats.
        private static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(20);

        private readonly object gate = new();
        private readonly Dictionary<int, PartyState> parties = new(); // keyed by channel id

        private sealed class PartyState
        {
            public readonly Dictionary<int, DateTime> Present = new(); // userId -> last seen
            public readonly HashSet<int> Ready = new();
        }

        public sealed record Member(int UserId, bool Ready);

        /// <summary>Record that <paramref name="userId"/> is in the lobby right now.</summary>
        public void Touch(int channelId, int userId)
        {
            lock (gate)
            {
                var s = StateFor(channelId);
                s.Present[userId] = DateTime.UtcNow;
                Prune(s);
            }
        }

        /// <summary>Set (or clear) a member's ready flag; also counts as presence.</summary>
        public void SetReady(int channelId, int userId, bool ready)
        {
            lock (gate)
            {
                var s = StateFor(channelId);
                s.Present[userId] = DateTime.UtcNow;
                if (ready) s.Ready.Add(userId); else s.Ready.Remove(userId);
                Prune(s);
            }
        }

        /// <summary>True when every present member is ready and at least one is present — the auto-Begin gate.</summary>
        public bool AllPresentReady(int channelId)
        {
            lock (gate)
            {
                if (!parties.TryGetValue(channelId, out var s)) return false;
                Prune(s);
                return s.Present.Count > 0 && s.Present.Keys.All(s.Ready.Contains);
            }
        }

        /// <summary>The current lobby roster (present members + their ready flags), pruned of anyone gone quiet.</summary>
        public IReadOnlyList<Member> Roster(int channelId)
        {
            lock (gate)
            {
                if (!parties.TryGetValue(channelId, out var s)) return Array.Empty<Member>();
                Prune(s);
                return s.Present.Keys.Select(id => new Member(id, s.Ready.Contains(id))).ToList();
            }
        }

        /// <summary>Remove a member; returns true if that emptied the lobby (so the caller can reap an
        /// unstarted, abandoned party).</summary>
        public bool Leave(int channelId, int userId)
        {
            lock (gate)
            {
                if (!parties.TryGetValue(channelId, out var s)) return false;
                s.Present.Remove(userId);
                s.Ready.Remove(userId);
                if (s.Present.Count == 0) { parties.Remove(channelId); return true; }
                return false;
            }
        }

        private PartyState StateFor(int channelId)
        {
            if (!parties.TryGetValue(channelId, out var s))
                parties[channelId] = s = new PartyState();
            return s;
        }

        private static void Prune(PartyState s)
        {
            var now = DateTime.UtcNow;
            var gone = s.Present.Where(kv => now - kv.Value > PresenceTtl).Select(kv => kv.Key).ToList();
            foreach (var id in gone) { s.Present.Remove(id); s.Ready.Remove(id); }
        }
    }
}
