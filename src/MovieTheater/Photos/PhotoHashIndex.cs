using System;
using System.Collections.Generic;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The near-dupe candidate index (docs/photos-plan.md §2.6: "pHash Hamming distance ≤ threshold,
    /// computed via in-memory BK-tree over hash-prefix buckets per run").
    ///
    /// <para><b>Why buckets, and why they are exact.</b> The buckets are not an approximation. Split a
    /// 64-bit hash into <c>threshold + 1</c> blocks; two hashes that differ in at most
    /// <c>threshold</c> bits must, by the pigeonhole principle, agree EXACTLY on at least one block.
    /// So every real neighbour is found by looking only in the buckets the query hash itself lands in —
    /// no candidate is missed, and the search never touches the other 99% of the collection.</para>
    ///
    /// <para><b>Why a BK-tree inside each bucket.</b> A bucket still holds thousands of hashes at
    /// collection scale, and a BK-tree turns "everything within d bits" into a walk that prunes whole
    /// subtrees by the triangle inequality (|d(q,parent) − d(child,parent)| ≤ d(q,child)). Nodes are
    /// held in flat arrays rather than objects: at 150k photos and threshold 8 this index carries
    /// 9 × 150k entries, and 24 bytes an entry (≈ 32 MB) is a different proposition from an object
    /// graph with a dictionary per node.</para>
    ///
    /// <para><b>Cost, stated plainly, because the pass rebuilds it.</b> The index is PER RUN: one
    /// projection query over the hashed population (id + pHash, no rows), then
    /// <c>(threshold + 1) × n</c> inserts of a few hundred nanoseconds each — on the order of a second
    /// at 150k photos, and it is paid once per <c>photos-dupes</c> invocation, not once per batch. That
    /// is the reason a driver loop should give the near pass a decent <c>--max-batches</c> rather than
    /// calling it once per chunk: each invocation re-reads and re-builds.</para>
    /// </summary>
    public sealed class PhotoHashIndex
    {
        private readonly int threshold;
        private readonly int blockCount;
        private readonly int[] blockShift;
        private readonly ulong[] blockMask;
        private readonly Dictionary<ulong, BkTree>[] buckets;

        public int Count { get; private set; }

        /// <summary>Blocks are capped so a very loose threshold cannot ask for more blocks than a
        /// 64-bit word has useful splits. Above the cap the pigeonhole guarantee weakens to "at least
        /// one block differs by at most ⌊threshold/blocks⌋", which is why the cap sits well above any
        /// threshold worth using for photographs.</summary>
        private const int MaxBlocks = 16;

        public PhotoHashIndex(int threshold)
        {
            this.threshold = Math.Max(0, threshold);
            blockCount = Math.Min(MaxBlocks, this.threshold + 1);

            blockShift = new int[blockCount];
            blockMask = new ulong[blockCount];
            buckets = new Dictionary<ulong, BkTree>[blockCount];
            var width = 64 / blockCount;
            var extra = 64 % blockCount;
            var shift = 0;
            for (var b = 0; b < blockCount; b++)
            {
                var bits = width + (b < extra ? 1 : 0);
                blockShift[b] = shift;
                blockMask[b] = bits >= 64 ? ulong.MaxValue : ((1UL << bits) - 1UL);
                shift += bits;
                buckets[b] = new Dictionary<ulong, BkTree>();
            }
        }

        public void Add(int id, long hash)
        {
            var bits = unchecked((ulong)hash);
            for (var b = 0; b < blockCount; b++)
            {
                var key = (bits >> blockShift[b]) & blockMask[b];
                if (!buckets[b].TryGetValue(key, out var tree)) buckets[b][key] = tree = new BkTree();
                tree.Add(hash, id);
            }
            Count++;
        }

        /// <summary>
        /// Every indexed id within <c>threshold</c> bits of <paramref name="hash"/>, with its distance.
        /// Deduplicated across blocks — a close neighbour usually agrees on several of them.
        /// </summary>
        public List<PhotoHashNeighbour> Query(long hash)
        {
            var seen = new Dictionary<int, int>();
            var bits = unchecked((ulong)hash);
            for (var b = 0; b < blockCount; b++)
            {
                var key = (bits >> blockShift[b]) & blockMask[b];
                if (!buckets[b].TryGetValue(key, out var tree)) continue;
                tree.Search(hash, threshold, seen);
            }

            var result = new List<PhotoHashNeighbour>(seen.Count);
            foreach (var kv in seen) result.Add(new PhotoHashNeighbour(kv.Key, kv.Value));
            // Deterministic: nearest first, then by id. A grouping pass that emitted candidates in
            // dictionary order would produce different groups on different runs of the same data.
            result.Sort((x, y) => x.Distance != y.Distance ? x.Distance - y.Distance : x.AssetId - y.AssetId);
            return result;
        }

        /// <summary>
        /// A BK-tree in flat arrays. <see cref="dupNext"/> chains entries whose hash is IDENTICAL — a
        /// BK-tree cannot hold a zero-distance edge, and identical perceptual hashes are not an edge
        /// case here but the normal result of two copies of one photograph.
        /// </summary>
        private sealed class BkTree
        {
            private long[] hashes = new long[8];
            private int[] ids = new int[8];
            private int[] firstChild = new int[8];
            private int[] nextSibling = new int[8];
            private int[] dupNext = new int[8];
            private byte[] edge = new byte[8];
            private int count;

            public void Add(long hash, int id)
            {
                var node = NewNode(hash, id);
                if (node == 0) return; // the root

                var current = 0;
                while (true)
                {
                    var d = PhotoHashes.HammingDistance(hashes[current], hash);
                    if (d == 0)
                    {
                        dupNext[node] = dupNext[current];
                        dupNext[current] = node;
                        return;
                    }

                    var child = firstChild[current];
                    var found = -1;
                    while (child >= 0)
                    {
                        if (edge[child] == d) { found = child; break; }
                        child = nextSibling[child];
                    }
                    if (found < 0)
                    {
                        edge[node] = (byte)d;
                        nextSibling[node] = firstChild[current];
                        firstChild[current] = node;
                        return;
                    }
                    current = found;
                }
            }

            public void Search(long hash, int max, Dictionary<int, int> hits)
            {
                if (count == 0) return;

                var stack = new Stack<int>();
                stack.Push(0);
                while (stack.Count > 0)
                {
                    var node = stack.Pop();
                    var d = PhotoHashes.HammingDistance(hashes[node], hash);
                    if (d <= max)
                    {
                        for (var n = node; n >= 0; n = dupNext[n])
                        {
                            // Same id from two blocks is the same answer; keep the distance once.
                            if (!hits.ContainsKey(ids[n])) hits[ids[n]] = d;
                        }
                    }

                    for (var child = firstChild[node]; child >= 0; child = nextSibling[child])
                        if (Math.Abs(edge[child] - d) <= max) stack.Push(child);
                }
            }

            private int NewNode(long hash, int id)
            {
                if (count == hashes.Length) Grow();
                var index = count++;
                hashes[index] = hash;
                ids[index] = id;
                firstChild[index] = -1;
                nextSibling[index] = -1;
                dupNext[index] = -1;
                edge[index] = 0;
                return index;
            }

            private void Grow()
            {
                var size = hashes.Length * 2;
                Array.Resize(ref hashes, size);
                Array.Resize(ref ids, size);
                Array.Resize(ref firstChild, size);
                Array.Resize(ref nextSibling, size);
                Array.Resize(ref dupNext, size);
                Array.Resize(ref edge, size);
            }
        }
    }

    public readonly struct PhotoHashNeighbour
    {
        public PhotoHashNeighbour(int assetId, int distance)
        {
            AssetId = assetId;
            Distance = distance;
        }

        public int AssetId { get; }

        public int Distance { get; }

        /// <summary>§2.6's similarity score: 1.0 is an identical hash, and the scale is the fraction of
        /// the 64 bits that agree — a number the review UI can show without explaining a bit count.</summary>
        public double Similarity => 1.0 - (Distance / 64.0);
    }
}
