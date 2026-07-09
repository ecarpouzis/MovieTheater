using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// In-memory live state for arcade rooms (arcade-plan.md §6, D3) — a direct structural cousin of
    /// <c>ChannelSkipService</c>: a singleton, one <see cref="gate"/> lock over a dictionary, presence
    /// inferred from heartbeats and pruned on a TTL. The durable record is the <c>ArcadeSession</c>
    /// row; this owns everything ephemeral: seat assignment, the once-only CloudRetro-room bind, and
    /// which rooms have gone empty and should be reaped.
    ///
    /// One room = one CloudRetro worker = one shared emulator. The backend cannot create the CloudRetro
    /// room (§2 box) — the creator's browser does — so a room lives here <em>unbound</em> from creation
    /// until <see cref="TryBind"/> records the id the browser got back. Joins are refused until then.
    /// </summary>
    public class ArcadeRoomService
    {
        // Must exceed the room page's heartbeat interval (12 s) with margin so an active player is never
        // pruned between polls — the same rule as ChannelSkipService's viewer TTL. 90 s (was 30) because
        // Chrome throttles/freezes BACKGROUNDED tabs: an alt-tabbed player's heartbeats can stretch to
        // ~1/minute (or pause entirely under Memory Saver), and 30 s pruned their seat — then reaped a
        // solo player's room — for merely unfocusing the window (observed live 2026-07-07: focus loss →
        // seat/room gone → session teardown → the dolphin teardown crash). 90 s rides out throttling and
        // gives a frozen-then-resumed tab a window to rejoin its own seat.
        private static readonly TimeSpan ViewerTtl = TimeSpan.FromSeconds(90);

        /// <summary>
        /// Seats for people who are in the room but NOT playing. A spectator holds no controller port and
        /// sends no input (the browser shim never opens its input pump), so this is not a player seat and must
        /// never be counted as one. It exists because <c>MaxPlayers</c> is now the game's REAL player count:
        /// once Shadow of the Colossus is honestly 1P, its room would otherwise be sealed and a friend
        /// couldn't drop in to watch.
        /// </summary>
        public const int SpectatorSeats = 1;

        /// <summary>The <c>PlayerSlot</c> handed to a spectator: no controller port. Rides the capability
        /// token like any other slot, and tells the browser shim to skip t=108 and never send input.</summary>
        public const int SpectatorSlot = -1;

        private readonly object gate = new();
        private readonly Dictionary<string, RoomState> rooms = new(StringComparer.Ordinal);

        private sealed class RoomState
        {
            public int GameId;
            public int MaxPlayers;
            public int CreatorUserId;
            public string? CloudRetroRoomId;                            // null until the creator Binds (§8)
            public readonly Dictionary<int, int> Seats = new();         // slot -> userId (players only)
            public readonly HashSet<int> Spectators = new();            // userIds watching, no controller
            public readonly Dictionary<int, DateTime> Viewers = new();  // userId -> last seen (players AND spectators)
            public DateTime CreatedUtc;
        }

        public enum BindResult { Ok, NotFound, NotCreator, AlreadyBound }
        public enum JoinOutcome { Ok, NotFound, NotBound, Full }

        /// <summary><see cref="PlayerSlot"/> is <see cref="SpectatorSlot"/> when <see cref="IsSpectator"/>.</summary>
        public sealed record JoinResult(JoinOutcome Outcome, int PlayerSlot, bool IsSpectator = false);
        public sealed record RoomStatus(bool Bound, int MaxPlayers, IReadOnlyList<int> PlayerUserIds,
            IReadOnlyList<int> SpectatorUserIds, int? YourSlot, bool YouAreSpectator);
        public sealed record RoomSnapshot(string RoomCode, int GameId, int MaxPlayers, bool Bound,
            IReadOnlyList<int> PlayerUserIds, IReadOnlyList<int> SpectatorUserIds, int CreatorUserId);

        /// <summary>
        /// Register a freshly-created room with its creator in seat 0. Seeds the creator's presence so
        /// the reaper gives them the full TTL to connect and start heartbeating before the room could be
        /// swept as empty. The room starts unbound.
        /// </summary>
        public void CreateRoom(string roomCode, int gameId, int maxPlayers, int creatorUserId)
        {
            lock (gate)
            {
                var now = DateTime.UtcNow;
                var state = new RoomState
                {
                    GameId = gameId,
                    MaxPlayers = Math.Max(1, maxPlayers),
                    CreatorUserId = creatorUserId,
                    CreatedUtc = now,
                };
                state.Seats[0] = creatorUserId;
                state.Viewers[creatorUserId] = now;
                rooms[roomCode] = state;
            }
        }

        /// <summary>
        /// Rebuild a room the pod lost (a deploy restarts the site while a session is live: the emulator
        /// and the players' WebRTC never notice, but this in-memory registry is wiped — the room vanishes
        /// from the lobby rail and invite links 404 "room has ended"). Called from the Heartbeat path only:
        /// a heartbeat is proof a player's page is actually in the room, so we never resurrect a corpse
        /// from a stale DB row. Recreates the state already BOUND (the id survived in ArcadeSession); the
        /// heartbeater re-seats via TryJoin right after. No-op if the room exists (raced rehydration).
        /// </summary>
        public void Rehydrate(string roomCode, int gameId, int maxPlayers, int creatorUserId, string cloudRetroRoomId)
        {
            lock (gate)
            {
                if (rooms.ContainsKey(roomCode)) return;
                rooms[roomCode] = new RoomState
                {
                    GameId = gameId,
                    MaxPlayers = Math.Max(1, maxPlayers),
                    CreatorUserId = creatorUserId,
                    CloudRetroRoomId = cloudRetroRoomId,
                    CreatedUtc = DateTime.UtcNow,
                };
            }
        }

        /// <summary>Record the CloudRetro room id the creator's browser got back (§8 step 3). Creator-only,
        /// once-only, room must be unbound — the guards that keep a room from being hijacked or double-bound.</summary>
        public BindResult TryBind(string roomCode, int userId, string cloudRetroRoomId)
        {
            lock (gate)
            {
                if (!rooms.TryGetValue(roomCode, out var state))
                    return BindResult.NotFound;
                if (state.CreatorUserId != userId)
                    return BindResult.NotCreator;
                if (state.CloudRetroRoomId != null)
                    return BindResult.AlreadyBound;

                state.CloudRetroRoomId = cloudRetroRoomId;
                state.Viewers[userId] = DateTime.UtcNow; // binding is also presence
                return BindResult.Ok;
            }
        }

        /// <summary>
        /// Assign the caller a seat. A room must be bound first (else it's "still starting"). A returning
        /// user keeps whatever they had (idempotent reconnect); a newcomer takes the lowest free PLAYER slot
        /// below MaxPlayers, and when those are gone falls back to a spectator seat. Only when both are
        /// exhausted is the room full. Joining is also presence.
        /// </summary>
        public JoinResult TryJoin(string roomCode, int userId)
        {
            lock (gate)
            {
                if (!rooms.TryGetValue(roomCode, out var state))
                    return new JoinResult(JoinOutcome.NotFound, -1);

                var now = DateTime.UtcNow;
                Prune(roomCode, state, now);
                if (!rooms.ContainsKey(roomCode))
                    return new JoinResult(JoinOutcome.NotFound, -1); // pruning emptied and removed it

                if (state.CloudRetroRoomId == null)
                    return new JoinResult(JoinOutcome.NotBound, -1);

                // Already seated (reconnect / duplicate Join) → same seat, no new allocation. A spectator
                // stays a spectator even if a player seat has since freed up: silently promoting them would
                // hand a controller to someone whose page never opened an input pump.
                var existing = state.Seats.FirstOrDefault(kv => kv.Value == userId);
                if (existing.Value == userId && state.Seats.ContainsKey(existing.Key))
                {
                    state.Viewers[userId] = now;
                    return new JoinResult(JoinOutcome.Ok, existing.Key);
                }
                if (state.Spectators.Contains(userId))
                {
                    state.Viewers[userId] = now;
                    return new JoinResult(JoinOutcome.Ok, SpectatorSlot, IsSpectator: true);
                }

                int slot = LowestFreeSlot(state);
                if (slot >= 0)
                {
                    state.Seats[slot] = userId;
                    state.Viewers[userId] = now;
                    return new JoinResult(JoinOutcome.Ok, slot);
                }

                // Players are full — offer the watch-only seat before refusing.
                if (state.Spectators.Count < SpectatorSeats)
                {
                    state.Spectators.Add(userId);
                    state.Viewers[userId] = now;
                    return new JoinResult(JoinOutcome.Ok, SpectatorSlot, IsSpectator: true);
                }

                return new JoinResult(JoinOutcome.Full, -1);
            }
        }

        /// <summary>Presence heartbeat (the room page's 12 s poll). Returns the room's current status, or
        /// null if the room is gone.</summary>
        public RoomStatus? Heartbeat(string roomCode, int userId)
        {
            lock (gate)
            {
                if (!rooms.TryGetValue(roomCode, out var state))
                    return null;
                var now = DateTime.UtcNow;
                state.Viewers[userId] = now;
                Prune(roomCode, state, now);
                if (!rooms.ContainsKey(roomCode))
                    return null; // pruning emptied and removed it
                return StatusFor(state, userId);
            }
        }

        /// <summary>Explicit leave (also sent via sendBeacon on page hide). Frees the seat; empties the
        /// room if they were the last one in it.</summary>
        public void Leave(string roomCode, int userId)
        {
            lock (gate)
            {
                if (!rooms.TryGetValue(roomCode, out var state))
                    return;
                RemoveUser(state, userId);
                if (state.Viewers.Count == 0)
                    rooms.Remove(roomCode);
            }
        }

        /// <summary>Best-effort live-room count for the pre-create cap check (§6). CloudRetro's t=112 is
        /// the authoritative backstop.</summary>
        public int LiveRoomCount()
        {
            lock (gate)
            {
                PruneAll(DateTime.UtcNow);
                return rooms.Count;
            }
        }

        /// <summary>Snapshot of live rooms for the lobby's "who's playing what" rail. The controller puts
        /// names to the user ids and applies the age gate.</summary>
        public IReadOnlyList<RoomSnapshot> Snapshot()
        {
            lock (gate)
            {
                PruneAll(DateTime.UtcNow);
                return rooms.Select(kv => new RoomSnapshot(
                        kv.Key, kv.Value.GameId, kv.Value.MaxPlayers, kv.Value.CloudRetroRoomId != null,
                        kv.Value.Seats.Values.ToList(), kv.Value.Spectators.ToList(), kv.Value.CreatorUserId))
                    .ToList();
            }
        }

        /// <summary>The bound CloudRetro room id for a room, or null if it doesn't exist / isn't bound yet.</summary>
        public string? BoundRoomId(string roomCode)
        {
            lock (gate)
                return rooms.TryGetValue(roomCode, out var state) ? state.CloudRetroRoomId : null;
        }

        /// <summary>
        /// Sweep rooms whose players have all gone quiet (TTL expired) and return their codes so the
        /// hosted reaper can stamp <c>ArcadeSession.EndedUtc</c>. Idempotent: a room is removed here once,
        /// then its DB row is closed out.
        /// </summary>
        public IReadOnlyList<string> ReapExpired()
        {
            lock (gate)
                return PruneAll(DateTime.UtcNow);
        }

        // ── helpers (all under gate) ──

        private int LowestFreeSlot(RoomState state)
        {
            for (int s = 0; s < state.MaxPlayers; s++)
                if (!state.Seats.ContainsKey(s))
                    return s;
            return -1;
        }

        private static void RemoveUser(RoomState state, int userId)
        {
            state.Viewers.Remove(userId);
            state.Spectators.Remove(userId);
            var seat = state.Seats.FirstOrDefault(kv => kv.Value == userId);
            if (seat.Value == userId && state.Seats.ContainsKey(seat.Key))
                state.Seats.Remove(seat.Key);
        }

        // Drop players gone quiet past the TTL (freeing their seats). If that empties the room, remove it
        // and report its code as reaped.
        private bool Prune(string roomCode, RoomState state, DateTime now)
        {
            var gone = state.Viewers.Where(kv => now - kv.Value > ViewerTtl).Select(kv => kv.Key).ToList();
            foreach (var userId in gone)
                RemoveUser(state, userId);
            if (state.Viewers.Count == 0)
            {
                rooms.Remove(roomCode);
                return true;
            }
            return false;
        }

        private List<string> PruneAll(DateTime now)
        {
            var reaped = new List<string>();
            foreach (var (code, state) in rooms.ToList())
                if (Prune(code, state, now))
                    reaped.Add(code);
            return reaped;
        }

        private RoomStatus StatusFor(RoomState state, int userId)
        {
            int? yourSlot = state.Seats.TryGetValue2(userId, out var slot) ? slot : null;
            return new RoomStatus(state.CloudRetroRoomId != null, state.MaxPlayers, state.Seats.Values.ToList(),
                state.Spectators.ToList(), yourSlot, state.Spectators.Contains(userId));
        }
    }

    internal static class SeatDictExtensions
    {
        /// <summary>Reverse lookup: the slot a user occupies, if any.</summary>
        public static bool TryGetValue2(this Dictionary<int, int> seats, int userId, out int slot)
        {
            foreach (var kv in seats)
                if (kv.Value == userId) { slot = kv.Key; return true; }
            slot = -1;
            return false;
        }
    }
}
