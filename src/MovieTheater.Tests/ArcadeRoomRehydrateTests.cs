using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// A pod restart wipes the live room registry; the heartbeat path rebuilds a room from its durable
    /// ArcadeSession row. The codec and controller scheme decide what every JOINER's client negotiates and
    /// sends, so they must come back too — a rehydrated H.264 room that forgot its codec handed joiners the
    /// default AV1 track, which binds and then shows nothing.
    /// </summary>
    public class ArcadeRoomRehydrateTests
    {
        [Fact]
        public void Rehydrate_restores_codec_and_controller_scheme()
        {
            var rooms = new ArcadeRoomService();
            rooms.Rehydrate("RHY", gameId: 7, maxPlayers: 2, creatorUserId: 1, "roomid___Game", "h264", "wiimote");

            Assert.Equal("h264", rooms.RoomVideoCodec("RHY"));
            Assert.Equal("wiimote", rooms.RoomControllerScheme("RHY"));
        }

        [Fact]
        public void Rehydrate_of_an_older_row_falls_back_to_worker_defaults()
        {
            var rooms = new ArcadeRoomService();
            rooms.Rehydrate("OLD", gameId: 7, maxPlayers: 2, creatorUserId: 1, "roomid___Game");

            Assert.Equal("", rooms.RoomVideoCodec("OLD"));
            Assert.Equal("", rooms.RoomControllerScheme("OLD"));
        }

        [Fact]
        public void Rehydrate_never_overwrites_a_live_room()
        {
            var rooms = new ArcadeRoomService();
            rooms.CreateRoom("LIVE", gameId: 7, maxPlayers: 2, creatorUserId: 1, videoCodec: "av1");
            rooms.Rehydrate("LIVE", gameId: 7, maxPlayers: 2, creatorUserId: 1, "roomid___Game", "h264", "");

            Assert.Equal("av1", rooms.RoomVideoCodec("LIVE"));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("   ", null)]
        [InlineData("av1:Ss- h264:SsP h265:SsP m0 auto=h264", "av1:Ss- h264:SsP h265:SsP m0 auto=h264")]
        [InlineData("<script>av1</script>", "scriptav1script")]
        public void Codec_probe_is_reduced_to_token_characters(string? raw, string? expected)
        {
            Assert.Equal(expected, ArcadeRoomService.SanitizeCodecProbe(raw));
        }

        [Fact]
        public void Codec_probe_is_capped_at_the_column_width()
        {
            Assert.Equal(80, ArcadeRoomService.SanitizeCodecProbe(new string('a', 200))!.Length);
        }
    }
}
