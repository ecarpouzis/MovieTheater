using System;
using System.Security.Cryptography;
using System.Text;
using MovieTheater.Core;
using Xunit;

namespace MovieTheater.Tests
{
    public class ArcadeTurnCredentialTests
    {
        [Fact]
        public void Mint_MatchesTurnRestApiVector()
        {
            // Known vector: the coturn/pion REST scheme is username="<expiry>:<userId>",
            // credential=base64(HMAC-SHA1(secret, username)). Pinned so a change to the algorithm
            // (e.g. SHA-256, or base64url) is caught — those silently break server-side auth.
            var c = ArcadeTurnCredential.Mint(secret: "test-secret", ttlSeconds: 3600, userId: 42, nowUnixSeconds: 1893452400);

            Assert.Equal("1893456000:42", c.Username);
            Assert.Equal("D3cY1YlHT03Glheqhr2SFxE0Aig=", c.Password);
        }

        [Fact]
        public void Username_EmbedsExpiryAndUser()
        {
            var c = ArcadeTurnCredential.Mint("s", ttlSeconds: 600, userId: 7, nowUnixSeconds: 1_000_000);
            Assert.Equal("1000600:7", c.Username);
        }

        [Fact]
        public void Password_IsBase64HmacSha1OfUsername()
        {
            // Independently recompute the HMAC to prove the contract the TURN server will re-derive.
            const string secret = "another-secret";
            var c = ArcadeTurnCredential.Mint(secret, ttlSeconds: 120, userId: 99, nowUnixSeconds: 2_000_000);

            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
            var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(c.Username)));
            Assert.Equal(expected, c.Password);
        }

        [Fact]
        public void Mint_RequiresSecret()
        {
            Assert.Throws<ArgumentException>(() => ArcadeTurnCredential.Mint("", 60, 1, 1_000_000));
        }
    }
}
