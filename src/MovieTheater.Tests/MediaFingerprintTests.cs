using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MovieTheater.Core;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The sampled content fingerprint (<see cref="MediaFingerprint"/>) that keys keyframe custody.
    /// What matters: determinism (the same bytes always produce the same value, which is the entire
    /// contract), sensitivity at the sampled regions, the length being part of the digest, and the
    /// small-file whole-hash path.
    /// </summary>
    public class MediaFingerprintTests
    {
        private static byte[] Bytes(long length, int seed = 7)
        {
            // Deterministic pseudo-random content — Random with a fixed seed is stable per runtime,
            // but these tests only ever compare values computed in the SAME process, so what matters
            // is that two arrays from the same (length, seed) are identical, which this guarantees.
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static Task<string> Compute(byte[] data)
        {
            using var stream = new MemoryStream(data);
            return MediaFingerprint.ComputeAsync(stream, data.Length);
        }

        [Fact]
        public async Task The_same_bytes_always_produce_the_same_value()
        {
            var data = Bytes(20_000_000);
            Assert.Equal(await Compute(data), await Compute(data));
        }

        [Fact]
        public async Task The_value_is_64_hex_chars()
        {
            var fp = await Compute(Bytes(1000));
            Assert.Equal(64, fp.Length);
            Assert.All(fp, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
        }

        [Theory]
        [InlineData(0)]              // head
        [InlineData(5_000_000)]      // first interior block (20M / 4)
        [InlineData(10_000_000)]     // middle block
        [InlineData(15_000_000)]     // third block
        [InlineData(19_999_999)]     // tail
        public async Task A_changed_byte_in_any_sampled_region_changes_the_value(long offset)
        {
            var data = Bytes(20_000_000);
            var before = await Compute(data);
            data[offset] ^= 0xFF;
            Assert.NotEqual(before, await Compute(data));
        }

        [Fact]
        public async Task Different_lengths_differ_even_when_the_sampled_bytes_agree()
        {
            // Two files of all-zeros: every sampled region reads identical bytes, so the length baked
            // into the digest is the only thing telling them apart — and it must.
            var a = await Compute(new byte[20_000_000]);
            var b = await Compute(new byte[30_000_000]);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public async Task A_small_file_is_hashed_whole()
        {
            // Below the threshold every byte matters — including one in a region the sampling scheme
            // would never visit on a large file.
            var data = Bytes(1_000_000);
            var before = await Compute(data);
            data[500_000] ^= 0xFF;
            Assert.NotEqual(before, await Compute(data));
        }

        [Fact]
        public void The_sampled_regions_never_overlap_above_the_threshold()
        {
            // Overlap wouldn't be wrong, but non-overlap is what makes "~3.5 MB read per file" true —
            // and the threshold constant is what guarantees it, so pin the relationship.
            foreach (var length in new[] { MediaFingerprint.WholeFileThreshold + 1, 20_000_000L, 60L * 1024 * 1024 * 1024 })
            {
                var regions = MediaFingerprint.Regions(length).OrderBy(r => r.Offset).ToArray();
                for (var i = 1; i < regions.Length; i++)
                    Assert.True(regions[i - 1].Offset + regions[i - 1].Count <= regions[i].Offset,
                        $"regions overlap at length {length}");
                Assert.Equal(0, regions[0].Offset);
                Assert.Equal(length, regions[^1].Offset + regions[^1].Count);
            }
        }
    }
}
