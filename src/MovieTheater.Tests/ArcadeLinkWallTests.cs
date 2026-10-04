using System;
using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The wall-seed rule (worker patch 0050): max sustained × 1.15, clamped to [5000, 40000], 0 = no seed —
    /// and, since 2026-10-04, only for a device that collided, and never from history older than the trusted
    /// boundary.
    /// </summary>
    public class ArcadeLinkWallTests
    {
        [Fact]
        public void No_history_means_no_seed()
        {
            Assert.Equal(0, ArcadeLinkWall.FromSustained(null));
            Assert.Equal(0, ArcadeLinkWall.FromSustained(0));
            Assert.Equal(0, ArcadeLinkWall.FromSustained(-5));
        }

        [Fact]
        public void The_34MB5J_phone_gets_its_sustained_plus_15_percent()
        {
            Assert.Equal(15074, ArcadeLinkWall.FromSustained(13108));
        }

        [Fact]
        public void Clamped_to_the_seedable_range()
        {
            Assert.Equal(5000, ArcadeLinkWall.FromSustained(1200));
            Assert.Equal(40000, ArcadeLinkWall.FromSustained(60000));
        }

        [Fact]
        public void A_device_that_never_collided_is_not_seeded()
        {
            // The phone beside its router, 2026-10-04: sustained 20072, one soft cut, no hard descent. Its
            // sustained rate is what the room sent, not what the link can carry.
            Assert.Equal(0, ArcadeLinkWall.Seed(20072, 0));
            Assert.Equal(0, ArcadeLinkWall.Seed(null, 0));
        }

        [Fact]
        public void A_device_that_collided_is_seeded_from_its_best_sustained_rate()
        {
            Assert.Equal(15074, ArcadeLinkWall.Seed(13108, 1));
            Assert.Equal(15074, ArcadeLinkWall.Seed(13108, 7));
            // It collided but never held a healthy rate: nothing to seed from.
            Assert.Equal(0, ArcadeLinkWall.Seed(0, 3));
            Assert.Equal(0, ArcadeLinkWall.Seed(null, 3));
        }

        [Fact]
        public void The_window_never_reaches_behind_the_trusted_boundary()
        {
            var t = ArcadeLinkWall.TrustedSinceUtc;
            // An hour after the boundary: 24 h back would include the defective stack's rows.
            Assert.Equal(t, ArcadeLinkWall.WindowStart(t.AddHours(1)));
            Assert.Equal(t, ArcadeLinkWall.WindowStart(t.Add(ArcadeLinkWall.Ttl)));
            // Two days later the ordinary 24 h window applies.
            Assert.Equal(t.AddHours(24), ArcadeLinkWall.WindowStart(t.AddHours(48)));
            Assert.Equal(DateTimeKind.Utc, t.Kind);
        }
    }
}
