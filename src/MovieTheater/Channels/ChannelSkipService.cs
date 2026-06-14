using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Channels
{
    /// <summary>
    /// In-memory presence + collective-vote tally for the shared TV channels (streaming-plan.md §8).
    /// A channel is a broadcast — everyone sees the same item at the same offset — so acting on the
    /// current film is a collective decision: a strict majority of the people currently watching can
    /// move the whole channel. Two independent polls run per channel: <b>skip</b> (jump to the next
    /// movie) and <b>restart</b> (replay the current movie from the top). Friends-scale and ephemeral,
    /// so this lives in memory (a singleton); nothing here needs to survive a restart.
    ///
    /// Presence is inferred from the Now poll: each poll touches the viewer, and a viewer not
    /// seen within <see cref="ViewerTtl"/> has left. Votes are scoped to the current schedule
    /// item, so they reset the moment the item changes (it ended, or a skip already fired). A
    /// restart keeps the same item, so its poll is cleared explicitly once it carries.
    /// </summary>
    public class ChannelSkipService
    {
        // Must exceed the client's Now-poll interval with margin so an active viewer is never
        // pruned between polls.
        private static readonly TimeSpan ViewerTtl = TimeSpan.FromSeconds(30);

        private readonly object gate = new();
        private readonly Dictionary<int, ChannelState> states = new();

        private sealed class Poll
        {
            public bool Fired;                          // a trigger already fired for the current item
            public readonly HashSet<int> Votes = new(); // userIds who voted in this poll
        }

        private sealed class ChannelState
        {
            public long ItemId;                                       // the schedule item votes are about
            public readonly Dictionary<int, DateTime> Viewers = new(); // userId -> last seen
            public readonly Poll Skip = new();
            public readonly Poll Restart = new();
            public DateTime? PausedAt;                                 // non-null = channel frozen at this instant
        }

        public sealed record PollStatus(int Viewers, int Votes, int Required, bool YouVoted);
        public sealed record ChannelStatus(PollStatus Skip, PollStatus Restart);

        // Strict majority of current viewers; a lone viewer (1) needs 1, so they act instantly.
        private static int RequiredVotes(int viewers) => viewers <= 1 ? 1 : viewers / 2 + 1;

        /// <summary>Record that <paramref name="userId"/> is watching <paramref name="itemId"/> right now.</summary>
        public ChannelStatus Touch(int channelId, long itemId, int userId)
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

        /// <summary>Cast (or re-affirm) a vote to skip to the next movie. See <see cref="VotePoll"/>.</summary>
        public (bool Carried, ChannelStatus Status) VoteSkip(int channelId, long itemId, int userId) =>
            VotePoll(channelId, itemId, userId, s => s.Skip);

        /// <summary>Cast (or re-affirm) a vote to restart the current movie. See <see cref="VotePoll"/>.</summary>
        public (bool Carried, ChannelStatus Status) VoteRestart(int channelId, long itemId, int userId) =>
            VotePoll(channelId, itemId, userId, s => s.Restart);

        /// <summary>
        /// Cast a vote in one of the channel's polls. Returns whether this vote carried the majority —
        /// the caller then performs the actual schedule change. The "fired" latch makes that fire
        /// exactly once per item even under concurrent votes.
        /// </summary>
        private (bool, ChannelStatus) VotePoll(int channelId, long itemId, int userId, Func<ChannelState, Poll> select)
        {
            lock (gate)
            {
                var state = StateFor(channelId, itemId);
                var now = DateTime.UtcNow;
                state.Viewers[userId] = now; // voting is also presence
                var poll = select(state);
                poll.Votes.Add(userId);
                Prune(state, now);

                var pollStatus = PollStatusFor(state, poll, userId);
                bool carried = !poll.Fired && pollStatus.Votes >= pollStatus.Required;
                if (carried)
                    poll.Fired = true;
                return (carried, Status(state, userId));
            }
        }

        /// <summary>
        /// The instant the channel was paused, or null if it's playing. A pause is a shared broadcast
        /// state — frozen here in memory alongside the vote tallies, since like them it's ephemeral and
        /// needn't survive a restart (a restart simply resumes the channel at the true live position).
        /// </summary>
        public DateTime? PausedSince(int channelId)
        {
            lock (gate)
                return states.TryGetValue(channelId, out var state) ? state.PausedAt : null;
        }

        /// <summary>
        /// Flip the shared pause for a channel. Unlike skip/restart this isn't a vote — anyone watching
        /// can pause or resume the whole channel with one tap. Returns the new paused instant (non-null =
        /// now paused) and, when resuming, how long it had been frozen so the caller can slide the
        /// schedule forward by exactly that much.
        /// </summary>
        public (DateTime? PausedAt, TimeSpan WasPausedFor) TogglePause(int channelId, long itemId, int userId)
        {
            lock (gate)
            {
                var state = StateFor(channelId, itemId);
                var now = DateTime.UtcNow;
                state.Viewers[userId] = now; // toggling is also presence
                Prune(state, now);
                if (state.PausedAt is DateTime since)
                {
                    state.PausedAt = null;
                    return (null, now - since);
                }
                state.PausedAt = now;
                return (now, TimeSpan.Zero);
            }
        }

        /// <summary>
        /// Clear the restart poll after a restart completes. Unlike skip, a restart keeps the same
        /// schedule item, so it can't rely on the item changing to reset — without this, a channel
        /// could only ever be restarted once per film.
        /// </summary>
        public void ClearRestart(int channelId, long itemId)
        {
            lock (gate)
            {
                if (states.TryGetValue(channelId, out var state) && state.ItemId == itemId)
                {
                    state.Restart.Fired = false;
                    state.Restart.Votes.Clear();
                }
            }
        }

        private ChannelState StateFor(int channelId, long itemId)
        {
            if (!states.TryGetValue(channelId, out var state))
                states[channelId] = state = new ChannelState { ItemId = itemId };

            // A new item (natural advance or a prior skip) wipes the slate for both polls.
            if (state.ItemId != itemId)
            {
                state.ItemId = itemId;
                ResetPoll(state.Skip);
                ResetPoll(state.Restart);
            }
            return state;
        }

        private static void ResetPoll(Poll poll)
        {
            poll.Fired = false;
            poll.Votes.Clear();
        }

        // Drop viewers (and their votes) that have gone quiet, so the majority is always
        // measured against who's actually watching.
        private static void Prune(ChannelState state, DateTime now)
        {
            var gone = state.Viewers.Where(kv => now - kv.Value > ViewerTtl).Select(kv => kv.Key).ToList();
            foreach (var userId in gone)
            {
                state.Viewers.Remove(userId);
                state.Skip.Votes.Remove(userId);
                state.Restart.Votes.Remove(userId);
            }
        }

        private static ChannelStatus Status(ChannelState state, int userId) =>
            new(PollStatusFor(state, state.Skip, userId), PollStatusFor(state, state.Restart, userId));

        private static PollStatus PollStatusFor(ChannelState state, Poll poll, int userId) =>
            new(state.Viewers.Count, poll.Votes.Count, RequiredVotes(state.Viewers.Count), poll.Votes.Contains(userId));
    }
}
