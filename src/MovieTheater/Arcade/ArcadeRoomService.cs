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
            /// <summary>Per-room video codec the creator chose ("av1"/"h264"; "" = worker config default).
            /// Every join descriptor must carry it — each peer's WebRTC track mime is fixed at INIT time
            /// and must match the room's one encoder. In-memory only: after a pod-restart Rehydrate it is
            /// lost ("" = default), so a post-restart JOINER of a codec-overridden room would get a
            /// mismatched track (video broken for them only) — accepted rare-edge for v1.</summary>
            public string VideoCodec = "";
            public readonly Dictionary<int, int> Seats = new();         // slot -> userId (players only)
            public readonly HashSet<int> Spectators = new();            // userIds watching, no controller
            public readonly Dictionary<int, DateTime> Viewers = new();  // userId -> last seen (players AND spectators)
            public DateTime CreatedUtc;
            /// <summary>Last time this room's liveness was written to ArcadeSession.LastSeenUtc. Throttles
            /// that UPDATE to one per <see cref="HeartbeatPersistEvery"/> instead of one per heartbeat.</summary>
            public DateTime LastPersistedUtc;
        }

        /// <summary>How often a live room's heartbeat is persisted to the durable row. Well under the
        /// reaper's stale window, so a genuinely live room can never be mistaken for a corpse.</summary>
        public static readonly TimeSpan HeartbeatPersistEvery = TimeSpan.FromSeconds(30);

        public enum BindResult { Ok, NotFound, NotCreator, AlreadyBound }
        public enum JoinOutcome { Ok, NotFound, NotBound, Full, NotSeated }

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
        public void CreateRoom(string roomCode, int gameId, int maxPlayers, int creatorUserId, string videoCodec = "")
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
                    VideoCodec = videoCodec ?? "",
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

        /// <summary>The room's per-room video codec ("" = worker config default / room unknown). Joiners'
        /// descriptors must carry it so their track mime matches the room's encoder.</summary>
        public string RoomVideoCodec(string roomCode)
        {
            lock (gate)
            {
                return rooms.TryGetValue(roomCode, out var state) ? state.VideoCodec : "";
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

                // Already seated (reconnect / duplicate Join) → same seat, no new allocation. A user can
                // hold SEVERAL seats (local multiplayer claims extras), so answer with their PRIMARY —
                // the lowest — which is the one their main session's input pump drives. A spectator
                // stays a spectator even if a player seat has since freed up: silently promoting them would
                // hand a controller to someone whose page never opened an input pump.
                if (state.Seats.TryGetValue2(userId, out var existingSlot))
                {
                    state.Viewers[userId] = now;
                    return new JoinResult(JoinOutcome.Ok, existingSlot);
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

        /// <summary>
        /// Local multiplayer: give an ALREADY-SEATED player an ADDITIONAL controller port, so several
        /// controllers plugged into one machine can each hold a real seat. The extra seat is a normal
        /// entry in <see cref="RoomState.Seats"/> (slot → userId, same userId repeated) — the browser
        /// opens one extra input-only CloudRetro connection per claimed seat, because the wire protocol
        /// routes input by CONNECTION, not by any in-frame player id. Spectators can't claim (their page
        /// never opens an input pump); presence rides the user's one heartbeat, covering every seat.
        /// </summary>
        public JoinResult TryClaimExtraSeat(string roomCode, int userId)
        {
            lock (gate)
            {
                if (!rooms.TryGetValue(roomCode, out var state))
                    return new JoinResult(JoinOutcome.NotFound, -1);

                var now = DateTime.UtcNow;
                Prune(roomCode, state, now);
                if (!rooms.ContainsKey(roomCode))
                    return new JoinResult(JoinOutcome.NotFound, -1);

                if (state.CloudRetroRoomId == null)
                    return new JoinResult(JoinOutcome.NotBound, -1);

                if (!state.Seats.ContainsValue(userId))
                    return new JoinResult(JoinOutcome.NotSeated, -1);

                int slot = LowestFreeSlot(state);
                if (slot < 0)
                    return new JoinResult(JoinOutcome.Full, -1);

                state.Seats[slot] = userId;
                state.Viewers[userId] = now;
                return new JoinResult(JoinOutcome.Ok, slot);
            }
        }

        /// <summary>Release one of a user's EXTRA seats (local player removed). Only a seat they own, and
        /// never their last one — the primary seat is freed by <see cref="Leave"/>, not by this.</summary>
        public bool ReleaseSeat(string roomCode, int userId, int slot)
        {
            lock (gate)
            {
                if (!rooms.TryGetValue(roomCode, out var state)) return false;
                if (!state.Seats.TryGetValue(slot, out var owner) || owner != userId) return false;
                if (state.Seats.Count(kv => kv.Value == userId) < 2) return false;
                state.Seats.Remove(slot);
                state.Viewers[userId] = DateTime.UtcNow;
                return true;
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

        /// <summary>
        /// Codes of the rooms this pod currently holds live, AFTER pruning the expired ones. The reaper
        /// uses it as a "hands off" list: never close a DB row for a room the registry is still serving,
        /// however old its durable stamp looks. It is a safety net only — NOT a liveness oracle. An empty
        /// set means "this pod knows of no rooms", which is also exactly what a pod says one second after
        /// it starts, so absence from this set is never on its own grounds to close anything.
        /// </summary>
        public IReadOnlyCollection<string> LiveRoomCodes()
        {
            lock (gate)
            {
                PruneAll(DateTime.UtcNow);
                return rooms.Keys.ToHashSet(StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// True at most once per <see cref="HeartbeatPersistEvery"/> per room — the caller then writes
        /// <c>ArcadeSession.LastSeenUtc</c>. Claiming the slot and stamping it happen together under the
        /// lock, so N concurrent heartbeats from N players produce ONE write, not N.
        /// </summary>
        public bool ShouldPersistHeartbeat(string roomCode, DateTime now)
        {
            lock (gate)
            {
                if (!rooms.TryGetValue(roomCode, out var state))
                    return false;
                if (now - state.LastPersistedUtc < HeartbeatPersistEvery)
                    return false;
                state.LastPersistedUtc = now;
                return true;
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
            // ALL their seats, not just the first hit — a local-multiplayer host holds several, and
            // leaving/pruning must free every controller port they were occupying.
            foreach (var slot in state.Seats.Where(kv => kv.Value == userId).Select(kv => kv.Key).ToList())
                state.Seats.Remove(slot);
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
            // Slot order, not dictionary insertion order — the roster renders P1..Pn from this list.
            return new RoomStatus(state.CloudRetroRoomId != null, state.MaxPlayers,
                state.Seats.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList(),
                state.Spectators.ToList(), yourSlot, state.Spectators.Contains(userId));
        }
    }

    internal static class SeatDictExtensions
    {
        /// <summary>Reverse lookup: the LOWEST slot a user occupies, if any — their PRIMARY seat, since a
        /// local-multiplayer host holds several.</summary>
        public static bool TryGetValue2(this Dictionary<int, int> seats, int userId, out int slot)
        {
            slot = -1;
            bool found = false;
            foreach (var kv in seats)
                if (kv.Value == userId && (!found || kv.Key < slot)) { slot = kv.Key; found = true; }
            return found;
        }
    }
}
