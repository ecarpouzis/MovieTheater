using System;
using MovieTheater.Core;
using Xunit;

namespace MovieTheater.Tests
{
    public class ArcadeCapabilityTokenTests
    {
        private const string Secret = "unit-test-secret";

        private static ArcadeCapabilityToken.Payload Join(long expires, string roomId = "6e2f01ab9c3d___Mario Kart 64")
            => new(UserId: 42, GameId: 7, RoomCode: "K7QX2M", CloudRetroRoomId: roomId, PlayerSlot: 1, ExpiresUnixSeconds: expires);

        private static long InAnHour() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        private static long AnHourAgo() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;

        [Fact]
        public void RoundTrips_AllFields()
        {
            var expires = InAnHour();
            var token = ArcadeCapabilityToken.Mint(Secret, Join(expires));

            Assert.True(ArcadeCapabilityToken.TryValidate(Secret, token, out var p));
            Assert.NotNull(p);
            Assert.Equal(42, p!.UserId);
            Assert.Equal(7, p.GameId);
            Assert.Equal("K7QX2M", p.RoomCode);
            // The room id embeds '___' and a space — the exact thing base64url-in-payload protects.
            Assert.Equal("6e2f01ab9c3d___Mario Kart 64", p.CloudRetroRoomId);
            Assert.Equal(1, p.PlayerSlot);
            Assert.Equal(expires, p.ExpiresUnixSeconds);
        }

        [Fact]
        public void Creator_EmptyRoomId_RoundTrips()
        {
            // Creators carry an empty CloudRetro room id (they create, not join).
            var token = ArcadeCapabilityToken.Mint(Secret, Join(InAnHour(), roomId: ""));
            Assert.True(ArcadeCapabilityToken.TryValidate(Secret, token, out var p));
            Assert.Equal(string.Empty, p!.CloudRetroRoomId);
        }

        [Fact]
        public void RoomId_WithPipe_SurvivesRoundTrip()
        {
            // A title could in principle contain the '|' field separator; base64url of the
            // room id inside the payload is exactly what keeps the parse unambiguous.
            var token = ArcadeCapabilityToken.Mint(Secret, Join(InAnHour(), roomId: "abc___Weird|Title|Game"));
            Assert.True(ArcadeCapabilityToken.TryValidate(Secret, token, out var p));
            Assert.Equal("abc___Weird|Title|Game", p!.CloudRetroRoomId);
        }

        [Fact]
        public void Rejects_WrongSecret()
        {
            var token = ArcadeCapabilityToken.Mint(Secret, Join(InAnHour()));
            Assert.False(ArcadeCapabilityToken.TryValidate("other-secret", token, out var p));
            Assert.Null(p);
        }

        [Fact]
        public void Rejects_TamperedPayload()
        {
            var token = ArcadeCapabilityToken.Mint(Secret, Join(InAnHour()));
            // Flip a character in the payload half (before the '.') — signature must fail.
            var dot = token.IndexOf('.');
            var c = token[0] == 'A' ? 'B' : 'A';
            var tampered = c + token[1..dot] + token[dot..];
            Assert.False(ArcadeCapabilityToken.TryValidate(Secret, tampered, out _));
        }

        [Fact]
        public void Rejects_Expired()
        {
            var token = ArcadeCapabilityToken.Mint(Secret, Join(AnHourAgo()));
            Assert.False(ArcadeCapabilityToken.TryValidate(Secret, token, out _));
        }

        // ── Control-token grace ───────────────────────────────────────────────────────────────────
        // The in-room control calls (quicksave/snapshot/load) validate with a grace, because their real
        // bound is the LIVE ROOM the token names, not a clock. Without it the browser's one token lapsed
        // mid-session and saving died — four times, most recently 2026-07-26 (Mario BAZR), each time on a
        // room that was otherwise streaming perfectly. The WS connect keeps the strict check.

        [Fact]
        public void Grace_Accepts_RecentlyExpired()
        {
            var token = ArcadeCapabilityToken.Mint(Secret, Join(AnHourAgo()));
            Assert.True(ArcadeCapabilityToken.TryValidate(Secret, token, TimeSpan.FromHours(12), out var p));
            Assert.Equal(42, p!.UserId);
        }

        [Fact]
        public void Grace_Still_Rejects_LongExpired()
        {
            var token = ArcadeCapabilityToken.Mint(Secret, Join(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 13 * 3600));
            Assert.False(ArcadeCapabilityToken.TryValidate(Secret, token, TimeSpan.FromHours(12), out var p));
            Assert.Null(p);
        }

        [Fact]
        public void Grace_Never_Excuses_A_BadSignature()
        {
            var token = ArcadeCapabilityToken.Mint(Secret, Join(AnHourAgo()));
            Assert.False(ArcadeCapabilityToken.TryValidate("other-secret", token, TimeSpan.FromHours(12), out _));
        }

        [Fact]
        public void NoGrace_Overload_Is_Unchanged()
        {
            var token = ArcadeCapabilityToken.Mint(Secret, Join(AnHourAgo()));
            Assert.False(ArcadeCapabilityToken.TryValidate(Secret, token, out _));
            Assert.False(ArcadeCapabilityToken.TryValidate(Secret, token, TimeSpan.Zero, out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData("no-dot-here")]
        [InlineData(".")]
        [InlineData("abc.")]
        [InlineData(".abc")]
        [InlineData("not-base64!.also-not!")]
        public void Rejects_Malformed(string token)
        {
            Assert.False(ArcadeCapabilityToken.TryValidate(Secret, token, out var p));
            Assert.Null(p);
        }
    }
}
