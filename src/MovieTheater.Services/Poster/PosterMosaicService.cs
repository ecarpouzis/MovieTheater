using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Db;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Services.Poster
{
    /// <summary>
    /// Output format for the generated mosaic image.
    /// </summary>
    public enum MosaicOutputFormat
    {
        Png,
        Jpeg,
        WebP
    }

    /// <summary>
    /// Options for controlling mosaic generation and output optimization.
    /// </summary>
    /// <remarks>
    /// <para>Output always preserves the source image aspect ratio.</para>
    /// <para><b>Size Control:</b></para>
    /// <list type="bullet">
    ///   <item><see cref="OutputScale"/> controls output size relative to source (1.0 = same size). Larger output = more posters.</item>
    ///   <item><see cref="TileScale"/> controls poster size in output pixels (1.0 = 150×200 px). Smaller posters = more posters.</item>
    ///   <item><see cref="MaxOutputDimension"/> optionally caps the largest dimension.</item>
    /// </list>
    /// </remarks>
    public class MosaicOptions
    {
        // ─────────────────────────────────────────────────────────────────────
        // Size Control
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Scale factor for output size relative to source image. 
        /// 1.0 = same size as source, 2.0 = double (4× posters), 0.5 = half (¼ posters).
        /// </summary>
        public double OutputScale { get; set; } = 1.0;

        /// <summary>
        /// Multiplier for poster size in output pixels. Base size is 150×200.
        /// 1.0 = 150×200 px posters, 0.5 = 75×100 px (4× more posters), 2.0 = 300×400 px (¼ posters).
        /// </summary>
        public double TileScale { get; set; } = 1.0;

        /// <summary>
        /// Maximum output dimension (width or height). Output is scaled down proportionally if exceeded. 0 = no limit.
        /// </summary>
        public int MaxOutputDimension { get; set; } = 0;

        // ─────────────────────────────────────────────────────────────────────
        // Color Matching
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Number of top color-matched candidates to consider for each cell (1-6000).</summary>
        public int TopK { get; set; } = 50;

        /// <summary>Radius (in cells) to check for duplicate posters (0-50).</summary>
        public int ExcludeRadius { get; set; } = 2;

        /// <summary>Exponential decay divisor for color distance weighting in CIE LAB ΔE² units. Higher = more tolerant of color differences.</summary>
        public double ColorDecayFactor { get; set; } = 100.0;

        /// <summary>Base penalty multiplier for adjacent duplicate posters (0-1). Lower = stronger penalty.</summary>
        public double AdjacencyPenaltyBase { get; set; } = 0.1;

        // ─────────────────────────────────────────────────────────────────────
        // Output Format
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Output image format.</summary>
        public MosaicOutputFormat OutputFormat { get; set; } = MosaicOutputFormat.Png;

        /// <summary>Quality for lossy formats (1-100). Only applies to Jpeg and WebP.</summary>
        public int Quality { get; set; } = 85;

        /// <summary>PNG compression level (1-9). Higher = smaller file, slower encoding.</summary>
        public PngCompressionLevel PngCompressionLevel { get; set; } = PngCompressionLevel.DefaultCompression;
    }

    public class PosterMosaicService
    {
        /// <summary>
        /// 3-dimensional k-d tree for CIE LAB color nearest-neighbor lookup with top-K support.
        /// Thread-safe for concurrent read operations after construction.
        /// </summary>
        private class KDTree3
        {
            private class Node
            {
                public float L, A, B;
                public int MovieId;
                public Node? Left;
                public Node? Right;
            }

            private readonly Node? root;
            private readonly int count;

            public int Count => count;

            public KDTree3(List<(int MovieId, float L, float A, float B)> points)
            {
                count = points?.Count ?? 0;
                if (count == 0) { root = null; return; }
                var pts = points!.Select(p => new Node { MovieId = p.MovieId, L = p.L, A = p.A, B = p.B }).ToList();
                root = Build(pts, 0);
            }

            private static Node? Build(List<Node> pts, int depth)
            {
                if (pts == null || pts.Count == 0) return null;
                int axis = depth % 3;
                pts.Sort((a, b) => axis switch
                {
                    0 => a.L.CompareTo(b.L),
                    1 => a.A.CompareTo(b.A),
                    _ => a.B.CompareTo(b.B),
                });
                int mid = pts.Count / 2;
                var node = pts[mid];
                node.Left = Build(pts.GetRange(0, mid), depth + 1);
                node.Right = Build(pts.GetRange(mid + 1, pts.Count - mid - 1), depth + 1);
                return node;
            }

            /// <summary>
            /// Find the K nearest neighbors in CIE LAB space.
            /// Returns squared ΔE distances, ascending.
            /// </summary>
            public List<(int MovieId, double Distance)> NearestK(float l, float a, float b, int k)
            {
                if (root == null || k <= 0) return [];

                var heap = new SortedSet<(double Dist, int MovieId, int Tiebreaker)>();
                int tiebreaker = 0;

                void Search(Node? node, int depth)
                {
                    if (node == null) return;

                    double dl = node.L - l;
                    double da = node.A - a;
                    double db = node.B - b;
                    double dist = dl * dl + da * da + db * db;

                    if (heap.Count < k)
                        heap.Add((dist, node.MovieId, tiebreaker++));
                    else if (dist < heap.Max.Dist)
                    {
                        heap.Remove(heap.Max);
                        heap.Add((dist, node.MovieId, tiebreaker++));
                    }

                    int axis = depth % 3;
                    double diff = axis switch { 0 => l - node.L, 1 => a - node.A, _ => b - node.B };
                    var first = diff < 0 ? node.Left : node.Right;
                    var second = diff < 0 ? node.Right : node.Left;

                    Search(first, depth + 1);
                    if (heap.Count < k || diff * diff < heap.Max.Dist)
                        Search(second, depth + 1);
                }

                Search(root, 0);
                return heap.Select(h => (h.MovieId, h.Dist)).OrderBy(x => x.Dist).ToList();
            }
        }

        private const int BaseTileWidth = 150;
        private const int BaseTileHeight = 200;

        private readonly IServiceScopeFactory scopeFactory;
        private readonly IPosterImageRepository imageRepo;

        // Cached color data and k-d tree, rebuilt when InvalidateCache() is called
        private readonly SemaphoreSlim cacheLock = new(1, 1);
        private KDTree3? cachedTree;
        private DateTime cacheBuiltAt = DateTime.MinValue;

        public PosterMosaicService(IServiceScopeFactory scopeFactory, IPosterImageRepository imageRepo)
        {
            this.scopeFactory = scopeFactory;
            this.imageRepo = imageRepo;
        }

        /// <summary>
        /// Invalidates the cached color tree. Call this when movie poster data is updated.
        /// </summary>
        public async Task InvalidateCacheAsync()
        {
            await cacheLock.WaitAsync();
            try
            {
                cachedTree = null;
                cacheBuiltAt = DateTime.MinValue;
            }
            finally
            {
                cacheLock.Release();
            }
        }

        /// <summary>
        /// Gets the time when the cache was last built, or null if not cached.
        /// </summary>
        public DateTime? CacheBuiltAt => cacheBuiltAt == DateTime.MinValue ? null : cacheBuiltAt;

        /// <summary>
        /// Gets the number of cached poster colors, or 0 if not cached.
        /// </summary>
        public int CachedPosterCount => cachedTree?.Count ?? 0;

        private async Task<KDTree3> GetOrBuildTreeAsync()
        {
            // Fast path: cache is valid
            if (cachedTree != null) return cachedTree;

            await cacheLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (cachedTree != null) return cachedTree;

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
                var candidates = await db.MoviePosterDetails
                    .Where(pd => !string.IsNullOrEmpty(pd.DominantColor))
                    .Select(pd => new { pd.MovieId, pd.DominantColor })
                    .ToListAsync();

                if (candidates.Count == 0)
                    throw new InvalidOperationException("No posters with DominantColor available");

                var candidateColors = new List<(int MovieId, float L, float A, float B)>(candidates.Count);
                foreach (var c in candidates)
                {
                    if (TryParseHexColor(c.DominantColor, out byte r, out byte g, out byte b))
                    {
                        var (l, a, b2) = RgbToLab(r, g, b);
                        candidateColors.Add((c.MovieId, l, a, b2));
                    }
                }

                if (candidateColors.Count == 0)
                    throw new InvalidOperationException("No valid poster colors available");

                cachedTree = new KDTree3(candidateColors);
                cacheBuiltAt = DateTime.UtcNow;
                return cachedTree;
            }
            finally
            {
                cacheLock.Release();
            }
        }

        private static bool TryParseHexColor(string? hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            hex = hex.Trim();
            if (hex.StartsWith('#')) hex = hex[1..];
            if (hex.Length != 6) return false;

            try
            {
                r = Convert.ToByte(hex[0..2], 16);
                g = Convert.ToByte(hex[2..4], 16);
                b = Convert.ToByte(hex[4..6], 16);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Converts sRGB to CIE LAB. Raw values: L 0–100, a/b roughly –128–127.
        private static (float L, float A, float B) RgbToLab(byte r, byte g, byte b)
        {
            double rl = SrgbToLinear(r / 255.0);
            double gl = SrgbToLinear(g / 255.0);
            double bl = SrgbToLinear(b / 255.0);

            double xd = rl * 0.4124564 + gl * 0.3575761 + bl * 0.1804375;
            double yd = rl * 0.2126729 + gl * 0.7151522 + bl * 0.0721750;
            double zd = rl * 0.0193339 + gl * 0.1191920 + bl * 0.9503041;

            double fx = LabF(xd / 0.95047);
            double fy = LabF(yd / 1.00000);
            double fz = LabF(zd / 1.08883);

            return ((float)(116.0 * fy - 16.0), (float)(500.0 * (fx - fy)), (float)(200.0 * (fy - fz)));
        }

        private static double SrgbToLinear(double c) =>
            c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        private static double LabF(double t) =>
            t > 0.008856 ? Math.Cbrt(t) : 7.787037 * t + 16.0 / 116.0;

        /// <summary>
        /// Builds a poster mosaic from the source image using default options.
        /// </summary>
        public Task<byte[]> BuildPosterMosaicBytes(byte[] sourceBytes, int topK, int excludeRadius, double tileScale = 1.0)
        {
            return BuildPosterMosaicBytes(sourceBytes, new MosaicOptions
            {
                TopK = topK,
                ExcludeRadius = excludeRadius,
                TileScale = tileScale
            });
        }

        /// <summary>
        /// Builds a poster mosaic from the source image with full control over options.
        /// </summary>
        public async Task<byte[]> BuildPosterMosaicBytes(byte[] sourceBytes, MosaicOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            int topK = Math.Clamp(options.TopK, 1, 6000);
            int excludeRadius = Math.Clamp(options.ExcludeRadius, 0, 50);
            double tileScale = Math.Clamp(options.TileScale, 0.01, 10.0);
            double outputScale = Math.Clamp(options.OutputScale, 0.1, 100.0);
            double colorDecay = Math.Clamp(options.ColorDecayFactor, 1.0, 1000000.0);
            double adjacencyPenalty = Math.Clamp(options.AdjacencyPenaltyBase, 0.001, 1.0);

            using var srcImage = Image.Load<Rgba32>(sourceBytes);

            // Poster aspect ratio is fixed at 3:4 (150:200)
            const double PosterAspect = (double)BaseTileWidth / BaseTileHeight;
            double sourceAspect = (double)srcImage.Width / srcImage.Height;

            // Reference tile size (target poster dimensions in output pixels)
            int refTileW = Math.Max(1, (int)Math.Round(BaseTileWidth * tileScale));
            int refTileH = Math.Max(1, (int)Math.Round(BaseTileHeight * tileScale));

            // Target output dimensions from source size × OutputScale
            double targetW = srcImage.Width * outputScale;
            double targetH = srcImage.Height * outputScale;

            // Apply max dimension constraint while preserving aspect ratio
            if (options.MaxOutputDimension > 0)
            {
                int maxDim = options.MaxOutputDimension;
                if (targetW > maxDim || targetH > maxDim)
                {
                    double shrinkFactor = Math.Min(maxDim / targetW, maxDim / targetH);
                    targetW *= shrinkFactor;
                    targetH *= shrinkFactor;
                }
            }

            // For output aspect to match source aspect with 3:4 posters:
            // outputAspect = posterAspect × (columns / rows) = sourceAspect
            // Therefore: columns / rows = sourceAspect / posterAspect
            double targetColRowRatio = sourceAspect / PosterAspect;

            // Calculate tile count based on target area and reference tile size
            double targetTileCount = (targetW * targetH) / ((double)refTileW * refTileH);

            // Solve for rows and columns:
            // columns × rows ≈ targetTileCount
            // columns / rows = targetColRowRatio
            // → rows = sqrt(targetTileCount / targetColRowRatio)
            int rows = Math.Max(1, (int)Math.Round(Math.Sqrt(targetTileCount / targetColRowRatio)));
            int columns = Math.Max(1, (int)Math.Round(rows * targetColRowRatio));

            // Calculate poster dimensions maintaining exact 3:4 aspect ratio
            // Choose the size that best fits the target output
            int posterW1 = Math.Max(1, (int)Math.Round(targetW / columns));
            int posterH1 = Math.Max(1, (int)Math.Round(posterW1 / PosterAspect));

            int posterH2 = Math.Max(1, (int)Math.Round(targetH / rows));
            int posterW2 = Math.Max(1, (int)Math.Round(posterH2 * PosterAspect));

            // Pick the option that produces output closest to target dimensions
            int outW1 = columns * posterW1, outH1 = rows * posterH1;
            int outW2 = columns * posterW2, outH2 = rows * posterH2;

            double err1 = Math.Abs(outW1 - targetW) + Math.Abs(outH1 - targetH);
            double err2 = Math.Abs(outW2 - targetW) + Math.Abs(outH2 - targetH);

            int posterWidth, posterHeight, outputWidth, outputHeight;
            if (err1 <= err2)
            {
                posterWidth = posterW1;
                posterHeight = posterH1;
                outputWidth = outW1;
                outputHeight = outH1;
            }
            else
            {
                posterWidth = posterW2;
                posterHeight = posterH2;
                outputWidth = outW2;
                outputHeight = outH2;
            }

            // Check estimated image size (RGBA32 = 4 bytes per pixel, limit to 2GB)
            const long MaxImageBytes = 2L * 1024 * 1024 * 1024;
            long estimatedImageBytes = (long)outputWidth * outputHeight * 4;
            if (estimatedImageBytes > MaxImageBytes)
            {
                throw new InvalidOperationException(
                    $"Estimated image size ({estimatedImageBytes / (1024 * 1024):N0} MB) exceeds 2 GB limit. " +
                    $"Reduce outputScale or increase tileScale. Current dimensions: {outputWidth}×{outputHeight} ({columns}×{rows} tiles)");
            }

            using var small = srcImage.Clone(ctx => ctx.Resize(columns, rows));

            // Use cached k-d tree for fast nearest-neighbor lookups
            var tree = await GetOrBuildTreeAsync();
            int effectiveTopK = Math.Min(topK, tree.Count);

            var chosenMovieIds = new int[rows, columns];
            var usageCounts = new Dictionary<int, int>();
            var rng = new Random();

            // Pre-allocate weight buffer to avoid allocations in tight loop
            var weightBuffer = new List<(int MovieId, double Distance, double Weight)>(effectiveTopK);

            small.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        var (lx, ly, lz) = RgbToLab(p.R, p.G, p.B);
                        var topCandidates = tree.NearestK(lx, ly, lz, effectiveTopK);

                        // Compute weights for each candidate
                        weightBuffer.Clear();
                        double totalWeight = 0.0;

                        foreach (var (movieId, dist) in topCandidates)
                        {
                            usageCounts.TryGetValue(movieId, out int ucount);

                            // Count adjacency occurrences
                            int adjacentCount = 0;
                            if (excludeRadius > 0)
                            {
                                for (int ny = Math.Max(0, y - excludeRadius); ny <= Math.Min(rows - 1, y + excludeRadius); ny++)
                                {
                                    for (int nx = Math.Max(0, x - excludeRadius); nx <= Math.Min(columns - 1, x + excludeRadius); nx++)
                                    {
                                        if ((ny != y || nx != x) && chosenMovieIds[ny, nx] == movieId)
                                            adjacentCount++;
                                    }
                                }
                            }

                            double w = Math.Exp(-dist / colorDecay);
                            w /= (1.0 + ucount);
                            w *= Math.Pow(adjacencyPenalty, adjacentCount);
                            if (w <= 0) w = 1e-12;

                            weightBuffer.Add((movieId, dist, w));
                            totalWeight += w;
                        }

                        // Weighted-random selection
                        int chosenId = weightBuffer.Count > 0 ? weightBuffer[0].MovieId : 0;
                        if (totalWeight > 0)
                        {
                            double rv = rng.NextDouble() * totalWeight;
                            double acc = 0.0;
                            foreach (var (movieId, _, weight) in weightBuffer)
                            {
                                acc += weight;
                                if (rv <= acc) { chosenId = movieId; break; }
                            }
                        }

                        usageCounts.TryGetValue(chosenId, out int cur);
                        usageCounts[chosenId] = cur + 1;
                        chosenMovieIds[y, x] = chosenId;
                    }
                }
            });

            // Load poster bytes in parallel
            var uniqueIds = chosenMovieIds.Cast<int>().Where(id => id != 0).Distinct().ToList();
            var posterBytesById = new ConcurrentDictionary<int, byte[]>();

            await Parallel.ForEachAsync(uniqueIds, async (id, ct) =>
            {
                try
                {
                    var bytes = await imageRepo.GetImage(id, PosterImageVariant.Thumbnail)
                        ?? await imageRepo.GetImage(id, PosterImageVariant.Main);
                    if (bytes != null)
                        posterBytesById[id] = bytes;
                }
                catch
                {
                    // ignore load errors for individual posters
                }
            });

            if (posterBytesById.IsEmpty)
                throw new InvalidOperationException("No poster image files available for selected posters");

            using var combined = new Image<Rgba32>(columns * posterWidth, rows * posterHeight);

            var resizedPosters = new Dictionary<int, Image<Rgba32>>();
            try
            {
                foreach (var kvp in posterBytesById)
                {
                    var img = Image.Load<Rgba32>(kvp.Value);
                    img.Mutate(x => x.Resize(posterWidth, posterHeight));
                    resizedPosters[kvp.Key] = img;
                }

                var fallbackPoster = resizedPosters.Values.First();

                // Compose all tiles in a single Mutate call to avoid per-tile pipeline overhead
                combined.Mutate(ctx =>
                {
                    for (int ry = 0; ry < rows; ry++)
                    {
                        for (int rx = 0; rx < columns; rx++)
                        {
                            var movieId = chosenMovieIds[ry, rx];
                            var posterImg = resizedPosters.GetValueOrDefault(movieId) ?? fallbackPoster;
                            ctx.DrawImage(posterImg, new Point(rx * posterWidth, ry * posterHeight), 1f);
                        }
                    }
                });
            }
            finally
            {
                foreach (var img in resizedPosters.Values)
                    img.Dispose();
            }

            // Estimate output size to reduce MemoryStream reallocations
            int estimatedSize = outputWidth * outputHeight * (options.OutputFormat == MosaicOutputFormat.Png ? 1 : 3) / 4;
            using var outMs = new MemoryStream(Math.Max(estimatedSize, 65536));

            switch (options.OutputFormat)
            {
                case MosaicOutputFormat.Jpeg:
                    await combined.SaveAsJpegAsync(outMs, new JpegEncoder
                    {
                        Quality = Math.Clamp(options.Quality, 1, 100)
                    });
                    break;

                case MosaicOutputFormat.WebP:
                    await combined.SaveAsWebpAsync(outMs, new WebpEncoder
                    {
                        Quality = Math.Clamp(options.Quality, 1, 100),
                        FileFormat = WebpFileFormatType.Lossy
                    });
                    break;


                case MosaicOutputFormat.Png:
                default:
                    await combined.SaveAsPngAsync(outMs, new PngEncoder
                    {
                        CompressionLevel = options.PngCompressionLevel
                    });
                    break;
            }

            return outMs.ToArray();
        }
    }
}
