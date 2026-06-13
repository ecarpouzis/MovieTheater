using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Channels
{
    /// <summary>
    /// In-memory presence + skip-vote tally for the shared TV channels (streaming-plan.md §8).
    /// A channel is a broadcast — everyone sees the same item at the same offset — so "skip"
    /// is a collective decision: a strict majority of the people currently watching can move
    /// the whole channel to the next movie. Friends-scale and ephemeral, so this lives in
    /// memory (a singleton); nothing here needs to survive a restart.
    ///
    /// Presence is inferred from the Now poll: each poll touches the viewer, and a viewer not
    /// seen within <see cref="ViewerTtl"/> has left. Votes are scoped to the current schedule
    /// item, so they reset the moment the item changes (it ended, or a skip already fired).
    /// </summary>
    public class ChannelSkipService
    {
        // Must exceed the client's Now-poll interval with margin so an active viewer is never
        // pruned between polls.
        private static readonly TimeSpan ViewerTtl = TimeSpan.FromSeconds(30);

        private readonly object gate = new();
        private readonly Dictionary<int, ChannelState> states = new();

        private sealed class ChannelState
        {
            public long ItemId;                                       // the schedule item votes are about
            public bool SkipFired;                                    // a skip already triggered for ItemId
            public readonly Dictionary<int, DateTime> Viewers = new(); // userId -> last seen
            public readonly HashSet<int> Votes = new();               // userIds who voted to skip ItemId
        }

        public sealed record SkipStatus(int Viewers, int Votes, int Required, bool YouVoted);

        // Strict majority of current viewers; a lone viewer (1) needs 1, so they skip instantly.
        private static int RequiredVotes(int viewers) => viewers <= 1 ? 1 : viewers / 2 + 1;

        /// <summary>Record that <paramref name="userId"/> is watching <paramref name="itemId"/> right now.</summary>
        public SkipStatus Touch(int channelId, long itemId, int userId)
        {
            lock (gate)
            {
                var state = StateFor(channelId, itemId);
                var now = DateTime.UtcNow;
                state.Viewers[userId] = now;
                Prune(state, now);
                return Status(state, userId);
            }
        }

        /// <summary>
        /// Cast (or re-affirm) a skip vote for the current item. Returns whether this vote
        /// carried the majority — the caller then performs the actual schedule skip. The
        /// "fired" latch makes that fire exactly once per item even under concurrent votes.
        /// </summary>
        public (bool Skip, SkipStatus Status) Vote(int channelId, long itemId, int userId)
        {
            lock (gate)
            {
                var state = StateFor(channelId, itemId);
                var now = DateTime.UtcNow;
                state.Viewers[userId] = now; // voting is also presence
                state.Votes.Add(userId);
                Prune(state, now);

                var status = Status(state, userId);
                bool skip = !state.SkipFired && status.Votes >= status.Required;
                if (skip)
                    state.SkipFired = true;
                return (skip, status);
            }
        }

        private ChannelState StateFor(int channelId, long itemId)
        {
            if (!states.TryGetValue(channelId, out var state))
                states[channelId] = state = new ChannelState { ItemId = itemId };

            // A new item (natural advance or a prior skip) wipes the slate.
            if (state.ItemId != itemId)
            {
                state.ItemId = itemId;
                state.SkipFired = false;
                state.Votes.Clear();
            }
            return state;
        }

        // Drop viewers (and their votes) that have gone quiet, so the majority is always
        // measured against who's actually watching.
        private static void Prune(ChannelState state, DateTime now)
        {
            var gone = state.Viewers.Where(kv => now - kv.Value > ViewerTtl).Select(kv => kv.Key).ToList();
            foreach (var userId in gone)
            {
                state.Viewers.Remove(userId);
                state.Votes.Remove(userId);
            }
        }

        private static SkipStatus Status(ChannelState state, int userId) =>
            new(state.Viewers.Count, state.Votes.Count, RequiredVotes(state.Viewers.Count), state.Votes.Contains(userId));
    }
}
