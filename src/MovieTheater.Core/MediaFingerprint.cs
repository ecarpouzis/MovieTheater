using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Core
{
    /// <summary>
    /// Content identity for a large media file WITHOUT reading the whole file: hex SHA-256 over the
    /// file's length plus sampled byte regions — head, tail, and three interior blocks. ~3.5 MB of
    /// reads whether the file is 700 MB or 60 GB, which is what makes fingerprinting the whole
    /// library an evening instead of another 25 TB marathon (.claude/skills/backfill-marathon).
    ///
    /// <para><b>What it is for.</b> Keying data that belongs to the BYTES — the banked keyframe lists
    /// in <c>MediaKeyframes</c> — so it survives any rename of file, folder or drive. It is strictly
    /// stronger than the (filename+size) pairing the Jellyfin sync's move detection already trusts:
    /// a false match requires two different encodes to agree on length, head, tail and three sampled
    /// interiors at once, which real video containers do not do.</para>
    ///
    /// <para><b>What it is NOT.</b> Not a cryptographic statement about every byte (a flipped bit
    /// between sample points goes unseen), and not comparable to full-file SHA-256 values from other
    /// systems. The <c>mtfp1</c> version prefix is baked into the hash so a future change to the
    /// sampling scheme can never silently collide with values computed under this one.</para>
    /// </summary>
    public static class MediaFingerprint
    {
        /// <summary>Hash-input version tag. Bump if the sampling scheme ever changes.</summary>
        private const string Version = "mtfp1";

        private const int EdgeBytes = 64 * 1024;
        private const int InteriorBytes = 1024 * 1024;

        /// <summary>Files at or below this size are hashed whole — the sampling machinery would read
        /// most of the file anyway, and whole-file is the stronger statement when it is this cheap.
        /// Also guarantees the sampled regions never overlap above the threshold.</summary>
        public const long WholeFileThreshold = 8L * 1024 * 1024;

        public static async Task<string> ComputeAsync(Stream stream, long length, CancellationToken cancel = default)
        {
            if (!stream.CanSeek) throw new ArgumentException("Fingerprinting needs a seekable stream.", nameof(stream));

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(System.Text.Encoding.ASCII.GetBytes(Version));
            sha.AppendData(BitConverter.GetBytes(length));

            if (length <= WholeFileThreshold)
            {
                stream.Seek(0, SeekOrigin.Begin);
                await AppendAsync(sha, stream, length, cancel).ConfigureAwait(false);
            }
            else
            {
                // Head, three interior quarters, tail — in offset order, so the digest is a pure
                // function of (scheme, length, sampled bytes) and nothing about read scheduling.
                foreach (var (offset, count) in Regions(length))
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    await AppendAsync(sha, stream, count, cancel).ConfigureAwait(false);
                }
            }

            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        /// <summary>The sampled (offset, byteCount) regions for a file above the whole-file threshold.
        /// Exposed for the tests, which assert the scheme rather than reverse-engineering it.</summary>
        public static (long Offset, int Count)[] Regions(long length) => new[]
        {
            (0L, EdgeBytes),
            (length / 4, InteriorBytes),
            (length / 2, InteriorBytes),
            (3 * (length / 4), InteriorBytes),
            (length - EdgeBytes, EdgeBytes),
        };

        public static async Task<string> ComputeFileAsync(string path, CancellationToken cancel = default)
        {
            // Sequential-scan hint deliberately absent: the access pattern is five seeks, and SMB
            // read-ahead for a pattern like that only drags extra bytes across the wire.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            return await ComputeAsync(stream, stream.Length, cancel).ConfigureAwait(false);
        }

        private static async Task AppendAsync(IncrementalHash sha, Stream stream, long count, CancellationToken cancel)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                var remaining = count;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancel)
                        .ConfigureAwait(false);
                    if (read <= 0) break; // A short file region hashes what exists; length is already in the digest.
                    sha.AppendData(buffer, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
