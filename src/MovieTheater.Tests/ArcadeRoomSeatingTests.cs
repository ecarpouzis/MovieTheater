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
