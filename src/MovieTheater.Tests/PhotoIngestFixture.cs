using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Photos;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Tests
{
    /// <summary>
    /// A throwaway photo collection, GENERATED. Nothing in this suite reads the real library: the
    /// collection lives on a NAS that no build and no test may touch, and the first ingest run against
    /// it is a human-supervised checkpoint. Everything the pipeline is asked to prove — EXIF dates,
    /// orientation, scans, moves, videos, undecodable formats — is manufactured here with known
    /// answers, which is also the only way those answers can be asserted at all.
    ///
    /// <para>The database is a SQLite FILE built from the shipped <see cref="MovieDb"/> model, for the
    /// same reason: the configured SQL Server connection string IS the live shared production database.
    /// A file (rather than in-memory) so a test can close every connection and reopen it, which is how
    /// "killed mid-run and resumed" is expressed honestly.</para>
    /// </summary>
    public sealed class PhotoIngestFixture : IDisposable
    {
        public readonly string Root;
        public readonly string ThumbCache;
        public readonly string ReportDir;

        /// <summary>A stand-in extracted Google Takeout archive (§2.10). Generated, like the collection:
        /// no test reads a real archive, and every quirk the mesh has to survive is manufactured here
        /// with a known answer.</summary>
        public readonly string TakeoutDir;

        /// <summary>The download lane's destination. A temp directory and NOTHING else, ever — the real
        /// setting has no default and the one additive NAS write is never exercised outside fixtures.</summary>
        public readonly string GoogleSyncDir;

        private readonly string workDir;
        private readonly DbContextOptions<MovieDb> dbOptions;

        public PhotoIngestFixture()
        {
            workDir = Path.Combine(Path.GetTempPath(), "photo-ingest-tests", Guid.NewGuid().ToString("N"));
            Root = Path.Combine(workDir, "collection");
            ThumbCache = Path.Combine(workDir, "thumbcache");
            ReportDir = Path.Combine(workDir, "reports");
            TakeoutDir = Path.Combine(workDir, "takeout");
            GoogleSyncDir = Path.Combine(workDir, "google-sync");
            Directory.CreateDirectory(Root);

            dbOptions = new DbContextOptionsBuilder<MovieDb>()
                .UseSqlite("Data Source=" + Path.Combine(workDir, "photos.db") + ";Pooling=False")
                .Options;
            using var db = new MovieDb(dbOptions);
            db.Database.EnsureCreated();
        }

        public MovieDb NewDb() => new MovieDb(dbOptions);

        /// <summary>
        /// A SECOND, independent database file — the "rebuilt DB" half of the §2.11 round trip. A
        /// restore is only proven by importing an export into a database that never held the rows, and
        /// that cannot be expressed with one file.
        /// </summary>
        public Func<MovieDb> SecondaryDbFactory(string name = "rebuilt")
        {
            var options = new DbContextOptionsBuilder<MovieDb>()
                .UseSqlite("Data Source=" + Path.Combine(workDir, name + ".db") + ";Pooling=False")
                .Options;
            using (var db = new MovieDb(options)) db.Database.EnsureCreated();
            return () => new MovieDb(options);
        }

        /// <summary>Where a test's export lands. Under the report directory, never the collection root
        /// (§2.11: exports are never written beside the originals).</summary>
        public string ExportDir(string name = "export") => Path.Combine(ReportDir, "exports", name);

        /// <summary>The curation store over a caller-owned context. Phase 3 moved review state out of
        /// <c>PhotosReportDir</c> and into rows, so a store is now a view over a database rather than
        /// over a directory.</summary>
        public PhotoCurationStore CurationStore(MovieDb db) => new PhotoCurationStore(db);

        public PhotoIngestOptions Options(int batchSize = 50) => new PhotoIngestOptions
        {
            Root = Root,
            ThumbCacheDir = ThumbCache,
            ReportDir = ReportDir,
            HomeTimeZone = "America/New_York",
            BatchSize = batchSize,
            IngestBatch = "test-batch",
        };

        public PhotoIngestPipeline Pipeline(PhotoIngestOptions? options = null, List<string>? log = null) =>
            new PhotoIngestPipeline(NewDb, options ?? Options(), line => log?.Add(line));

        // ── Fixture authoring ────────────────────────────────────────────────────────────────────

        public string FullPath(string relativePath) =>
            Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        private void EnsureParent(string full) =>
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        /// <summary>
        /// A JPEG whose pixels are a deterministic function of <paramref name="seed"/> — two files with
        /// the same seed are byte-identical (exact-dupe and move cases), and different seeds produce
        /// genuinely different PICTURES so a perceptual hash has something real to disagree about.
        /// </summary>
        public string WriteJpeg(string relativePath, int width = 64, int height = 48, int seed = 1,
            string? exifDateTimeOriginal = null, ushort? orientation = null,
            string? make = null, string? model = null,
            string? gpsDateStamp = null, string? gpsTimeStamp = null)
        {
            var full = FullPath(relativePath);
            EnsureParent(full);
            using var image = Paint(width, height, seed);

            if (exifDateTimeOriginal != null || orientation != null || make != null || model != null || gpsDateStamp != null)
            {
                var exif = new ExifProfile();
                if (exifDateTimeOriginal != null) exif.SetValue(ExifTag.DateTimeOriginal, exifDateTimeOriginal);
                if (orientation != null) exif.SetValue(ExifTag.Orientation, orientation.Value);
                if (make != null) exif.SetValue(ExifTag.Make, make);
                if (model != null) exif.SetValue(ExifTag.Model, model);
                if (gpsDateStamp != null)
                {
                    exif.SetValue(ExifTag.GPSDateStamp, gpsDateStamp);
                    if (gpsTimeStamp != null)
                    {
                        var parts = gpsTimeStamp.Split(':');
                        exif.SetValue(ExifTag.GPSTimestamp, new[]
                        {
                            new Rational(uint.Parse(parts[0]), 1),
                            new Rational(uint.Parse(parts[1]), 1),
                            new Rational(uint.Parse(parts[2]), 1),
                        });
                    }
                }
                image.Metadata.ExifProfile = exif;
            }

            image.SaveAsJpeg(full);
            return relativePath;
        }

        public string WritePng(string relativePath, int width = 40, int height = 40, int seed = 2)
        {
            var full = FullPath(relativePath);
            EnsureParent(full);
            using var image = Paint(width, height, seed);
            image.SaveAsPng(full);
            return relativePath;
        }

        /// <summary>A file the pipeline classifies but never decodes — the skeleton-row lanes.</summary>
        public string WriteOpaque(string relativePath, int bytes = 2048, int seed = 3)
        {
            var full = FullPath(relativePath);
            EnsureParent(full);
            var buffer = new byte[bytes];
            new Random(seed).NextBytes(buffer);
            File.WriteAllBytes(full, buffer);
            return relativePath;
        }

        /// <summary>
        /// The same PICTURE re-encoded at a different JPEG quality (docs/photos-plan.md §2.6's
        /// re-encode case). Byte-different, so SHA-256 says nothing; perceptually the same, which is
        /// exactly what the near lane has to notice.
        /// </summary>
        public string WriteJpegQuality(string relativePath, int quality, int width = 256, int height = 192, int seed = 1)
        {
            var full = FullPath(relativePath);
            EnsureParent(full);
            using var image = Paint(width, height, seed);
            image.SaveAsJpeg(full, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });
            return relativePath;
        }

        /// <summary>The same picture at a different size — the scanned-print problem in miniature: a
        /// resize changes every byte and almost none of the low-frequency structure a pHash reads.</summary>
        public string WriteJpegScaled(string relativePath, double scale, int width = 256, int height = 192, int seed = 1)
        {
            var full = FullPath(relativePath);
            EnsureParent(full);
            using var image = Paint(width, height, seed);
            image.Mutate(c => c.Resize(Math.Max(8, (int)(width * scale)), Math.Max(8, (int)(height * scale))));
            image.SaveAsJpeg(full);
            return relativePath;
        }

        /// <summary>
        /// The picture stored sideways with the EXIF orientation flag that puts it back — the shape a
        /// phone actually writes. The pipeline hashes the AUTO-ORIENTED image, so this must read as the
        /// same picture; a hash taken from the stored pixels would call it a different photograph, and
        /// the family's rotated copies would never group.
        /// </summary>
        public string WriteJpegRotated(string relativePath, int width = 256, int height = 192, int seed = 1)
        {
            var full = FullPath(relativePath);
            EnsureParent(full);
            using var image = Paint(width, height, seed);
            image.Mutate(c => c.Rotate(90));
            var exif = new ExifProfile();
            // 8 = "rotate the stored pixels 270° to display them" — the inverse of the 90° applied
            // above, so AutoOrient reproduces the original picture exactly.
            exif.SetValue(ExifTag.Orientation, (ushort)8);
            image.Metadata.ExifProfile = exif;
            image.SaveAsJpeg(full);
            return relativePath;
        }

        /// <summary>A stand-in for a RAW negative: catalogued and hashed by the pipeline, never decoded
        /// (§2.2 leaves RAW/HEIC derivatives to a later, deliberate decision).</summary>
        public string WriteRaw(string relativePath, int bytes = 4096, int seed = 9) =>
            WriteOpaque(relativePath, bytes, seed);

        /// <summary>A stand-in for a video half — a motion photo's clip or a Live Photo's .mov. Phase 1
        /// carries videos as skeleton rows, so its bytes are never read for pixels.</summary>
        public string WriteVideo(string relativePath, int bytes = 8192, int seed = 11) =>
            WriteOpaque(relativePath, bytes, seed);

        // ── Takeout fixture authoring (docs/photos-plan.md §2.10) ────────────────────────────────

        public string TakeoutPath(string relativePath) =>
            Path.Combine(TakeoutDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>A photo inside the archive. <paramref name="seed"/> shares the collection's palette
        /// function, so the same seed is the same PICTURE on both sides — which is what lets a re-encode
        /// be the pHash case and a byte copy be the SHA case.</summary>
        public string WriteTakeoutJpeg(string relativePath, int width = 256, int height = 192, int seed = 1,
            int quality = 90)
        {
            var full = TakeoutPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var image = Paint(width, height, seed);
            image.SaveAsJpeg(full, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });
            return relativePath;
        }

        /// <summary>A byte-for-byte copy of a collection file into the archive — Google handing back
        /// exactly what was uploaded, which is the SHA-256 rung's case.</summary>
        public string CopyIntoTakeout(string collectionRelative, string takeoutRelative)
        {
            var full = TakeoutPath(takeoutRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Copy(FullPath(collectionRelative), full, overwrite: true);
            return takeoutRelative;
        }

        public string WriteTakeoutBytes(string relativePath, int bytes = 1024, int seed = 5)
        {
            var full = TakeoutPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var buffer = new byte[bytes];
            new Random(seed).NextBytes(buffer);
            File.WriteAllBytes(full, buffer);
            return relativePath;
        }

        /// <summary>Raw text at an archive path — the malformed-sidecar and album-manifest cases.</summary>
        public void WriteTakeoutText(string relativePath, string contents)
        {
            var full = TakeoutPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
        }

        /// <summary>A per-item sidecar in Takeout's own shape (§2.10). <paramref name="title"/> is the
        /// authoritative original file name, which the media file on disk may not be.</summary>
        public void WriteTakeoutSidecar(string relativePath, string title, DateTime? takenUtc = null,
            string? description = null, double? latitude = null, double? longitude = null)
        {
            var seconds = takenUtc == null
                ? null
                : (long?)new DateTimeOffset(DateTime.SpecifyKind(takenUtc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();

            var geo = latitude != null && longitude != null
                ? $"\"geoData\": {{ \"latitude\": {latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, "
                  + $"\"longitude\": {longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"altitude\": 0.0 }},"
                : "\"geoData\": { \"latitude\": 0.0, \"longitude\": 0.0, \"altitude\": 0.0 },";

            var taken = seconds == null
                ? "\"imageViews\": \"3\","
                : $"\"photoTakenTime\": {{ \"timestamp\": \"{seconds.Value}\", \"formatted\": \"x\" }},";

            var json = "{\n"
                       + $"  \"title\": {JsonEscape(title)},\n"
                       + $"  \"description\": {JsonEscape(description ?? "")},\n"
                       + $"  {taken}\n"
                       + $"  {geo}\n"
                       + "  \"url\": \"https://photos.google.com/x\"\n"
                       + "}";
            WriteTakeoutText(relativePath, json);
        }

        private static string JsonEscape(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>Every file the download lane put on disk, sync-dir-relative. An INDEPENDENT reading
        /// of the destination — never the pass's own report of what it did.</summary>
        public List<string> GoogleSyncFilesOnDisk() =>
            Directory.Exists(GoogleSyncDir)
                ? Directory.EnumerateFiles(GoogleSyncDir, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(GoogleSyncDir, p).Replace('\\', '/'))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList()
                : new List<string>();

        public void Move(string fromRelative, string toRelative)
        {
            var to = FullPath(toRelative);
            EnsureParent(to);
            File.Move(FullPath(fromRelative), to);
        }

        public void Delete(string relativePath) => File.Delete(FullPath(relativePath));

        /// <summary>An INDEPENDENT count of what is on disk — never derived from the pipeline's own
        /// bookkeeping, because "did the chunked walk see everything exactly once" is precisely the
        /// question a self-reported total cannot answer.</summary>
        public List<string> MediaFilesOnDisk() =>
            Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Where(p => PhotoFileKinds.TryClassify(Path.GetExtension(p), out _))
                .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

        private static Image<Rgba32> Paint(int width, int height, int seed)
        {
            var image = new Image<Rgba32>(width, height);
            var random = new Random(seed);
            // Big soft blocks rather than per-pixel noise: a JPEG of pure noise survives neither the
            // encoder nor a downscale, and both hashes would then be measuring the codec.
            var blockW = Math.Max(1, width / 4);
            var blockH = Math.Max(1, height / 4);
            for (var by = 0; by < height; by += blockH)
                for (var bx = 0; bx < width; bx += blockW)
                {
                    var color = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                    for (var y = by; y < Math.Min(height, by + blockH); y++)
                        for (var x = bx; x < Math.Min(width, bx + blockW); x++)
                            image[x, y] = color;
                }
            image.Mutate(c => c.BoxBlur(1));
            return image;
        }

        public void Dispose()
        {
            // SQLite keeps the file handle in a connection pool; release it so the directory can go.
            // Pooling=False so the temp file unlocks when the context closes. The fixtures used to call the PROCESS-GLOBAL SqliteConnection.ClearAllPools() here, which reached into every OTHER test class running in parallel and closed its pooled connections mid-test
            // an occasional, unreproducible failure somewhere else in the suite.
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { /* a temp directory that outlives the run is not a test failure */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
