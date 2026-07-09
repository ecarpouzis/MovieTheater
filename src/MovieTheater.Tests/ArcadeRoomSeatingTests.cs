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
    }
}
