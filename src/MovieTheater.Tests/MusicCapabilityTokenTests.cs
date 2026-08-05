using MovieTheater.Core;

namespace MovieTheater.Tests
{
    public class MusicCapabilityTokenTests
    {
        private const string Secret = "test-secret";

        private static MusicCapabilityToken.Payload FreshPayload(string relPath = "Artist (2000)/Artist - Album (2000)/01 - Song.mp3") =>
            new(UserId: 7, TrackId: 42, RelativePath: relPath,
                ExpiresUnixSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600);

        [Fact]
        public void Mint_ThenValidate_RoundTrips()
        {
            var token = MusicCapabilityToken.Mint(Secret, FreshPayload());
            Assert.True(MusicCapabilityToken.TryValidate(Secret, token, out var payload));
            Assert.NotNull(payload);
            Assert.Equal(7, payload!.UserId);
            Assert.Equal(42, payload.TrackId);
            Assert.Equal("Artist (2000)/Artist - Album (2000)/01 - Song.mp3", payload.RelativePath);
        }

        [Fact]
        public void TamperedPayload_Rejected()
        {
            var token = MusicCapabilityToken.Mint(Secret, FreshPayload());
            var other = MusicCapabilityToken.Mint(Secret, FreshPayload("Other (2001)/track.mp3"));
            // Graft the other token's payload onto this token's signature.
            var frankenstein = other.Split('.')[0] + "." + token.Split('.')[1];
            Assert.False(MusicCapabilityToken.TryValidate(Secret, frankenstein, out _));
        }

        [Fact]
        public void WrongSecret_Rejected()
        {
            var token = MusicCapabilityToken.Mint(Secret, FreshPayload());
            Assert.False(MusicCapabilityToken.TryValidate("other-secret", token, out _));
        }

        [Fact]
        public void Expired_Rejected()
        {
            var expired = new MusicCapabilityToken.Payload(7, 42, "a/b.mp3",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5);
            var token = MusicCapabilityToken.Mint(Secret, expired);
            Assert.False(MusicCapabilityToken.TryValidate(Secret, token, out _));
        }

        [Fact]
        public void Garbage_Rejected()
        {
            Assert.False(MusicCapabilityToken.TryValidate(Secret, "", out _));
            Assert.False(MusicCapabilityToken.TryValidate(Secret, "not-a-token", out _));
            Assert.False(MusicCapabilityToken.TryValidate(Secret, "a.b", out _));
        }
    }
}
