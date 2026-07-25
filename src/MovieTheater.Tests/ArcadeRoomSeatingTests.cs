using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Seat allocation, and the invariant that keeps a spectator from becoming a player.
    ///
    /// Context: <c>ArcadeGame.MaxPlayers</c> is now each game's REAL simultaneous-player count (imported
    /// from LaunchBox by <c>arcade-launchbox-seats</c>), not the per-system controller-port blanket it used
    /// to be. That made honest 1-player rooms — Shadow of the Colossus is genuinely 1P — which would have
    /// sealed them shut. The watch-only seat exists so a friend can still drop in without being counted,
    /// seated, or handed a controller port.
    /// </summary>
    public class ArcadeRoomSeatingTests
    {
        private const int Host = 1, Friend = 2, Third = 3, Fourth = 4;

        private static ArcadeRoomService BoundRoom(string code, int maxPlayers, int creator = Host)
        {
            var rooms = new ArcadeRoomService();
            rooms.CreateRoom(code, gameId: 42, maxPlayers, creator);
            rooms.TryBind(code, creator, "roomid___Game");   // joins are refused until the creator binds
            return rooms;
        }

        [Fact]
        public void Creator_takes_player_slot_zero()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 2);
            var status = rooms.Heartbeat("AAA", Host);

            Assert.NotNull(status);
            Assert.Equal(0, status!.YourSlot);
            Assert.False(status.YouAreSpectator);
            Assert.Empty(status.SpectatorUserIds);
        }

        [Fact]
        public void Second_player_takes_the_next_controller_port()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 2);

            var join = rooms.TryJoin("AAA", Friend);

            Assert.Equal(ArcadeRoomService.JoinOutcome.Ok, join.Outcome);
            Assert.Equal(1, join.PlayerSlot);
            Assert.False(join.IsSpectator);
        }

        [Fact]
        public void A_single_player_game_still_admits_one_watcher()
        {
            // The whole point: Shadow of the Colossus is 1P, and a friend can still come and watch.
            var rooms = BoundRoom("AAA", maxPlayers: 1);

            var join = rooms.TryJoin("AAA", Friend);

            Assert.Equal(ArcadeRoomService.JoinOutcome.Ok, join.Outcome);
            Assert.True(join.IsSpectator);
            Assert.Equal(ArcadeRoomService.SpectatorSlot, join.PlayerSlot);
        }

        [Fact]
        public void A_spectator_is_never_counted_as_a_player()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 1);
            rooms.TryJoin("AAA", Friend);

            var status = rooms.Heartbeat("AAA", Host)!;
            Assert.Equal(new[] { Host }, status.PlayerUserIds);
            Assert.Equal(new[] { Friend }, status.SpectatorUserIds);

            var snapshot = Assert.Single(rooms.Snapshot());
            Assert.Equal(new[] { Host }, snapshot.PlayerUserIds);   // the lobby rail must not seat them
            Assert.Equal(new[] { Friend }, snapshot.SpectatorUserIds);
        }

        [Fact]
        public void The_watcher_sees_themselves_as_a_spectator_with_no_controller_port()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 1);
            rooms.TryJoin("AAA", Friend);

            var status = rooms.Heartbeat("AAA", Friend)!;

            Assert.True(status.YouAreSpectator);
            Assert.Null(status.YourSlot);   // no port — the shim keys "send no input" off exactly this
        }

        [Fact]
        public void Player_seats_are_filled_before_the_watch_seat()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 2);

            var second = rooms.TryJoin("AAA", Friend);
            var third = rooms.TryJoin("AAA", Third);

            Assert.False(second.IsSpectator);
            Assert.Equal(1, second.PlayerSlot);
            Assert.True(third.IsSpectator);
        }

        [Fact]
        public void The_room_is_full_once_players_and_the_watch_seat_are_taken()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 1);
            rooms.TryJoin("AAA", Friend);   // takes the one watch seat

            var fourth = rooms.TryJoin("AAA", Fourth);

            Assert.Equal(ArcadeRoomService.JoinOutcome.Full, fourth.Outcome);
        }

        [Fact]
        public void Rejoining_is_idempotent_and_never_promotes_a_watcher_to_a_player()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 2);
            rooms.TryJoin("AAA", Friend);            // player slot 1
            var watcher = rooms.TryJoin("AAA", Third);
            Assert.True(watcher.IsSpectator);

            // The player leaves, freeing a controller port. A watcher's page never opened an input pump,
            // so silently promoting them would hand a controller to someone who can't use it.
            rooms.Leave("AAA", Friend);
            var again = rooms.TryJoin("AAA", Third);

            Assert.True(again.IsSpectator);
            Assert.Equal(ArcadeRoomService.SpectatorSlot, again.PlayerSlot);
        }

        [Fact]
        public void A_returning_player_keeps_their_own_seat()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 2);
            var first = rooms.TryJoin("AAA", Friend);
            var second = rooms.TryJoin("AAA", Friend);   // duplicate Join / reconnect

            Assert.Equal(first.PlayerSlot, second.PlayerSlot);
            Assert.False(second.IsSpectator);
        }

        [Fact]
        public void Leaving_frees_the_watch_seat_for_someone_else()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 1);
            rooms.TryJoin("AAA", Friend);
            Assert.Equal(ArcadeRoomService.JoinOutcome.Full, rooms.TryJoin("AAA", Third).Outcome);

            rooms.Leave("AAA", Friend);

            var third = rooms.TryJoin("AAA", Third);
            Assert.Equal(ArcadeRoomService.JoinOutcome.Ok, third.Outcome);
            Assert.True(third.IsSpectator);
        }

        [Fact]
        public void Joins_are_refused_until_the_creator_binds_the_cloudretro_room()
        {
            var rooms = new ArcadeRoomService();
            rooms.CreateRoom("AAA", gameId: 42, maxPlayers: 1, Host);

            var join = rooms.TryJoin("AAA", Friend);

            Assert.Equal(ArcadeRoomService.JoinOutcome.NotBound, join.Outcome);
        }

        // ── Local multiplayer: one user, several controller ports ────────────────────────────────

        [Fact]
        public void A_seated_player_can_claim_extra_seats_up_to_the_player_cap()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 3);

            var p2 = rooms.TryClaimExtraSeat("AAA", Host);
            var p3 = rooms.TryClaimExtraSeat("AAA", Host);
            var p4 = rooms.TryClaimExtraSeat("AAA", Host);

            Assert.Equal(ArcadeRoomService.JoinOutcome.Ok, p2.Outcome);
            Assert.Equal(1, p2.PlayerSlot);
            Assert.Equal(ArcadeRoomService.JoinOutcome.Ok, p3.Outcome);
            Assert.Equal(2, p3.PlayerSlot);
            Assert.Equal(ArcadeRoomService.JoinOutcome.Full, p4.Outcome);
        }

        [Fact]
        public void Only_a_seated_player_can_claim_a_local_seat()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 4);

            Assert.Equal(ArcadeRoomService.JoinOutcome.NotSeated, rooms.TryClaimExtraSeat("AAA", Friend).Outcome);

            // A spectator's page never opened an input pump — they can't hold controller ports either.
            var oneP = BoundRoom("BBB", maxPlayers: 1);
            oneP.TryJoin("BBB", Friend); // watch seat
            Assert.Equal(ArcadeRoomService.JoinOutcome.NotSeated, oneP.TryClaimExtraSeat("BBB", Friend).Outcome);
        }

        [Fact]
        public void A_multi_seat_host_still_answers_with_their_primary_seat()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 4);
            rooms.TryClaimExtraSeat("AAA", Host);            // slot 1
            var friend = rooms.TryJoin("AAA", Friend);       // next free = 2

            Assert.Equal(2, friend.PlayerSlot);
            Assert.Equal(0, rooms.TryJoin("AAA", Host).PlayerSlot);       // rejoin → primary
            Assert.Equal(0, rooms.Heartbeat("AAA", Host)!.YourSlot);      // heartbeat too
            Assert.Equal(new[] { Host, Host, Friend }, rooms.Heartbeat("AAA", Host)!.PlayerUserIds);
        }

        [Fact]
        public void Releasing_an_extra_seat_frees_it_but_the_primary_is_untouchable()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 2);
            var extra = rooms.TryClaimExtraSeat("AAA", Host);

            Assert.False(rooms.ReleaseSeat("AAA", Friend, extra.PlayerSlot)); // not their seat
            Assert.True(rooms.ReleaseSeat("AAA", Host, extra.PlayerSlot));
            Assert.False(rooms.ReleaseSeat("AAA", Host, 0));                  // last seat — Leave's job

            var friend = rooms.TryJoin("AAA", Friend);                        // the freed port is usable
            Assert.Equal(extra.PlayerSlot, friend.PlayerSlot);
        }

        // ── A player who never left gets their seat back ──────────────────────────────────────────
        // Losing a seat while still playing is what broke SAVING for a whole session: the heartbeat
        // mints the gateway control token off the seat, so a seatless-but-playing user's page fell back
        // to its join token and every quicksave came back "This room pass expired". A prune (frozen tab
        // past the TTL) and the pagehide beacon both land the room in exactly this state — and on a
        // phone the beacon fires just for switching apps, after which bfcache restores the page and it
        // keeps heartbeating. Both call RemoveUser, so Leave-then-beat is the same state a prune leaves.

        [Fact]
        public void A_player_who_kept_heartbeating_gets_their_seat_back()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 2);
            rooms.TryJoin("AAA", Friend);                       // Friend = slot 1, and keeps the room alive
            rooms.Leave("AAA", Host);                           // pagehide beacon / TTL prune drops the host

            var status = rooms.Heartbeat("AAA", Host)!;

            Assert.Equal(0, status.YourSlot);                   // their OWN port back — the shim still drives it
            Assert.False(status.YouAreSpectator);
            Assert.Equal(new[] { Host, Friend }, status.PlayerUserIds);
        }

        [Fact]
        public void A_returning_player_whose_port_was_taken_gets_another_one()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 3);
            rooms.TryJoin("AAA", Friend);                       // slot 1, and keeps the room alive
            rooms.Leave("AAA", Host);                           // frees slot 0
            rooms.TryJoin("AAA", Third);                        // Third takes it

            var status = rooms.Heartbeat("AAA", Host)!;

            Assert.Equal(2, status.YourSlot);                   // the next free port, never Third's
            Assert.Equal(0, rooms.Heartbeat("AAA", Third)!.YourSlot);
        }

        [Fact]
        public void A_returning_player_stays_a_viewer_when_the_room_filled_up()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 1);
            rooms.TryJoin("AAA", Friend);                       // watch seat — keeps the room alive
            rooms.Leave("AAA", Host);
            rooms.TryJoin("AAA", Third);                        // Third takes the only player port

            var status = rooms.Heartbeat("AAA", Host)!;

            Assert.Null(status.YourSlot);                       // nothing to give them; never evict Third
            Assert.Equal(new[] { Third }, status.PlayerUserIds);
        }

        [Fact]
        public void A_stranger_who_only_heartbeats_is_never_seated()
        {
            // The re-seat is keyed on a seat this user actually held HERE, so Join — and its age gate —
            // stays the only way into a room.
            var rooms = BoundRoom("AAA", maxPlayers: 4);

            var status = rooms.Heartbeat("AAA", Fourth)!;

            Assert.Null(status.YourSlot);
            Assert.Equal(new[] { Host }, status.PlayerUserIds);
        }

        [Fact]
        public void A_returning_spectator_is_not_promoted_to_a_player()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 1);
            rooms.TryJoin("AAA", Friend);                       // watch seat
            rooms.Leave("AAA", Host);                           // frees the only player port

            var status = rooms.Heartbeat("AAA", Friend)!;

            Assert.True(status.YouAreSpectator);
            Assert.Null(status.YourSlot);                       // their page never opened an input pump
        }

        [Fact]
        public void Leaving_frees_every_seat_the_user_held()
        {
            var rooms = BoundRoom("AAA", maxPlayers: 4);
            rooms.TryClaimExtraSeat("AAA", Host);
            rooms.TryClaimExtraSeat("AAA", Host);
            rooms.TryJoin("AAA", Friend);

            rooms.Leave("AAA", Host);

            var status = rooms.Heartbeat("AAA", Friend)!;
            Assert.Equal(new[] { Friend }, status.PlayerUserIds); // no orphaned local seats
        }
    }
}
