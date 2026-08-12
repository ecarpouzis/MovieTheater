using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Controllers;
using MovieTheater.Db;
using MovieTheater.Photos;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Phase 6: the Google Takeout mesh (docs/photos-plan.md §2.10), driven against a GENERATED archive
    /// and a throwaway SQLite file. No test reads a real Takeout export, no test touches the collection,
    /// and the download lane runs only into a temp directory the fixture owns and deletes.
    ///
    /// <para>The properties under test are the ones whose failure would be quiet and expensive: a
    /// sidecar paired to the wrong media file (which would date the wrong photograph), an item
    /// duplicated on the next quarter's archive (which the unique index CANNOT catch, because two of its
    /// three columns are nullable), a sidecar overwriting a camera's own date, and — the one that costs
    /// real money — a download lane that runs before the match pass has drained and fetches photographs
    /// the family already owns.</para>
    /// </summary>
    public class PhotoGoogleMeshTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        private PhotoGoogleMeshOptions Options(bool download = false) => new PhotoGoogleMeshOptions
        {
            TakeoutDir = fixture.TakeoutDir,
            ThumbCacheDir = fixture.ThumbCache,
            SyncDir = download ? fixture.GoogleSyncDir : null,
            HomeTimeZone = "America/New_York",
            BatchSize = 3,
        };

        private PhotoGoogleMesh Mesh(PhotoGoogleMeshOptions? options = null, List<string>? log = null) =>
            new PhotoGoogleMesh(fixture.NewDb, options ?? Options(), line => log?.Add(line));

        // ── The fixtures ────────────────────────────────────────────────────────────────────────

        /// <summary>A small local library, ingested for real (walk → metadata → hash) so the rows carry
        /// the SHA-256 and pHash the cascade's second and third rungs consult.</summary>
        private void WriteLibraryFiles()
        {
            if (File.Exists(fixture.FullPath("Camera/IMG_0001.jpg"))) return;
            fixture.WriteJpeg("Camera/IMG_0001.jpg", 256, 192, seed: 11, exifDateTimeOriginal: "2019:05:01 09:00:00");
            fixture.WriteJpeg("Camera/IMG_0002.jpg", 256, 192, seed: 12, exifDateTimeOriginal: "2019:05:02 09:00:00");
            // No EXIF at all: its date can only ever come from a name, a folder, or Google.
            fixture.WriteJpegQuality("Album Scans/print.jpg", quality: 92, width: 256, height: 192, seed: 13);
            fixture.WriteJpeg("Camera/IMG_0009.jpg", 256, 192, seed: 19, exifDateTimeOriginal: "2019:05:09 09:00:00");
        }

        private async Task BuildLibraryAsync()
        {
            WriteLibraryFiles();

            var pipeline = fixture.Pipeline();
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Hash, null, 0);
        }

        /// <summary>
        /// The archive, carrying every quirk §2.10 names plus one item per matching rung:
        /// <list type="bullet">
        /// <item>a plain item whose name and size match a local file — rung 1;</item>
        /// <item>a byte-identical copy under a DIFFERENT name — rung 2, SHA-256;</item>
        /// <item>the same picture re-encoded at a lower quality under a different name — rung 3, pHash;</item>
        /// <item>a name Takeout truncated on disk, whose sidecar carries the real one;</item>
        /// <item>an <c>-edited</c> export with no sidecar of its own;</item>
        /// <item>a <c>(1)</c> duplicate whose sidecar moved the counter past the extension;</item>
        /// <item>a <c>*.supplemental-metadata.json</c> sidecar, and a truncated one;</item>
        /// <item>a live-photo pair sharing a single sidecar;</item>
        /// <item>a malformed sidecar and an album manifest, neither of which may be fatal;</item>
        /// <item>two genuinely Google-only photographs.</item>
        /// </list>
        /// </summary>
        private void BuildArchive()
        {
            // The archive's first two items are Google handing back exactly what was uploaded, so the
            // collection files have to exist on disk — ingested or not.
            WriteLibraryFiles();
            const string album = "Takeout/Google Photos/Photos from 2019";

            // Rung 1: same name, same bytes as the local file.
            fixture.CopyIntoTakeout("Camera/IMG_0001.jpg", $"{album}/IMG_0001.jpg");
            fixture.WriteTakeoutSidecar($"{album}/IMG_0001.jpg.json", "IMG_0001.jpg",
                new DateTime(2019, 5, 1, 13, 0, 0), description: "the first one");

            // Rung 2: identical bytes, different name — the name key misses and the hash proves it.
            fixture.CopyIntoTakeout("Camera/IMG_0002.jpg", $"{album}/renamed-by-google.jpg");
            fixture.WriteTakeoutSidecar($"{album}/renamed-by-google.jpg.supplemental-metadata.json",
                "renamed-by-google.jpg", new DateTime(2019, 5, 2, 13, 0, 0));

            // Rung 3: the same PICTURE, re-encoded hard, under a different name. Byte-different, so
            // SHA-256 says nothing; perceptually the same, which is the whole point of the rung.
            fixture.WriteTakeoutJpeg($"{album}/scan-reencoded.jpg", 256, 192, seed: 13, quality: 25);
            fixture.WriteTakeoutSidecar($"{album}/scan-reencoded.jpg.supplemental-metad.json",
                "scan-reencoded.jpg", new DateTime(1987, 6, 1, 16, 0, 0), latitude: 40.7, longitude: -74.0);

            // Truncated on disk; the sidecar carries the authoritative name.
            fixture.WriteTakeoutJpeg($"{album}/a-very-long-name-that-tak.jpg", 64, 48, seed: 31);
            fixture.WriteTakeoutSidecar($"{album}/a-very-long-name-that-tak.jpg.json",
                "a-very-long-name-that-takeout-truncated-on-disk.jpg", new DateTime(2019, 7, 4, 16, 0, 0));

            // An -edited export: no sidecar of its own, and it is NOT the original.
            fixture.WriteTakeoutJpeg($"{album}/IMG_0009.jpg", 256, 192, seed: 19);
            fixture.WriteTakeoutSidecar($"{album}/IMG_0009.jpg.json", "IMG_0009.jpg",
                new DateTime(2019, 5, 9, 13, 0, 0));
            fixture.WriteTakeoutJpeg($"{album}/IMG_0009-edited.jpg", 256, 192, seed: 19, quality: 60);

            // The (1) duplicate: the counter sits AFTER the extension in the sidecar's own name.
            fixture.WriteTakeoutJpeg($"{album}/IMG_0100(1).jpg", 96, 72, seed: 41);
            fixture.WriteTakeoutSidecar($"{album}/IMG_0100.jpg(1).json", "IMG_0100.jpg",
                new DateTime(2019, 8, 1, 16, 0, 0));

            // A live-photo pair: one sidecar between the still and its clip.
            fixture.WriteTakeoutBytes($"{album}/IMG_0200.heic", 900, seed: 51);
            fixture.WriteTakeoutBytes($"{album}/IMG_0200.mp4", 1500, seed: 52);
            fixture.WriteTakeoutSidecar($"{album}/IMG_0200.heic.json", "IMG_0200.heic",
                new DateTime(2019, 9, 1, 16, 0, 0));

            // Neither of these may end a pass: one is broken, one is not an item at all.
            fixture.WriteTakeoutJpeg($"{album}/broken.jpg", 64, 48, seed: 61);
            fixture.WriteTakeoutText($"{album}/broken.jpg.json", "{ this is not json");
            fixture.WriteTakeoutText($"{album}/metadata.json",
                "{ \"title\": \"Photos from 2019\", \"description\": \"an album, not a photograph\" }");
        }

        private static async Task DrainAsync(PhotoGoogleMesh mesh, PhotoGoogleMeshPass pass) =>
            await mesh.RunAsync(pass, null, 0);

        // ── Sidecar pairing (§2.10's quirks) ────────────────────────────────────────────────────

        [Theory]
        // The plain case, and the newer suffix plus every truncation of it.
        [InlineData("IMG_0001.jpg.json", "IMG_0001.jpg")]
        [InlineData("IMG_0001.jpg.supplemental-metadata.json", "IMG_0001.jpg")]
        [InlineData("IMG_0001.jpg.supplemental-metad.json", "IMG_0001.jpg")]
        [InlineData("IMG_0001.jpg.supplemental-me.json", "IMG_0001.jpg")]
        // The duplicate counter moves back past the extension.
        [InlineData("IMG_0100.jpg(1).json", "IMG_0100(1).jpg")]
        [InlineData("IMG_0100.jpg.supplemental-metadata(2).json", "IMG_0100(2).jpg")]
        // "(Copy)" is a file name, not a Takeout counter — rewriting it would invent a pairing.
        [InlineData("Wedding (Copy).jpg.json", "Wedding (Copy).jpg")]
        public void SidecarNameResolvesToItsMediaFile(string sidecar, string expected) =>
            Assert.Equal(expected, PhotoGoogleSidecar.TargetFileName(sidecar));

        [Fact]
        public void ArchiveDirectoryPairsEveryQuirk()
        {
            BuildArchive();
            var directory = fixture.TakeoutPath("Takeout/Google Photos/Photos from 2019");
            var items = PhotoGoogleSidecar.ReadDirectory(directory, fixture.TakeoutDir, out var unparseable);

            // Exactly ONE malformed sidecar. The album manifest is valid JSON that is not an item and
            // must not inflate the health number.
            Assert.Equal(1, unparseable);

            PhotoGoogleArchiveItem Find(string diskName) =>
                items.Single(i => string.Equals(i.DiskFileName, diskName, StringComparison.OrdinalIgnoreCase));

            // The JSON title is the authority: the media file on disk is truncated, the row is not.
            var truncated = Find("a-very-long-name-that-tak.jpg");
            Assert.Equal("a-very-long-name-that-takeout-truncated-on-disk.jpg", truncated.FileName);
            Assert.True(truncated.OwnsSidecar);

            // The newer suffix, and a truncation of it, both resolve exactly.
            Assert.Equal("exact", Find("renamed-by-google.jpg").SidecarMatch);
            Assert.Equal("exact", Find("scan-reencoded.jpg").SidecarMatch);

            // The -edited export borrows the original's sidecar and keeps its OWN name — otherwise two
            // rows would claim one identity whenever their sizes happened to agree.
            var edited = Find("IMG_0009-edited.jpg");
            Assert.Equal("edited", edited.SidecarMatch);
            Assert.False(edited.OwnsSidecar);
            Assert.Equal("IMG_0009-edited.jpg", edited.FileName);
            Assert.Equal(new DateTime(2019, 5, 9, 13, 0, 0, DateTimeKind.Utc), edited.Sidecar!.PhotoTakenUtc);

            // The (1) duplicate finds the sidecar whose counter moved.
            Assert.Equal("exact", Find("IMG_0100(1).jpg").SidecarMatch);

            // The live-photo pair: the still owns the sidecar, the clip shares it by stem.
            Assert.Equal("exact", Find("IMG_0200.heic").SidecarMatch);
            var clip = Find("IMG_0200.mp4");
            Assert.Equal("stem", clip.SidecarMatch);
            Assert.False(clip.OwnsSidecar);
            Assert.Equal(new DateTime(2019, 9, 1, 16, 0, 0, DateTimeKind.Utc), clip.Sidecar!.PhotoTakenUtc);

            // A broken sidecar costs its media file its metadata and nothing else.
            var broken = Find("broken.jpg");
            Assert.Null(broken.Sidecar);
            Assert.Equal("broken.jpg", broken.FileName);

            // Null Island is Google's "no location", not a point in the Atlantic.
            Assert.Null(Find("IMG_0001.jpg").Sidecar!.Latitude);
            Assert.Equal(40.7, Find("scan-reencoded.jpg").Sidecar!.Latitude);

            // ⚠ A 1987 date SURVIVES. The video pass refuses anything before 1990 because a container's
            // unset creation_time surfaces as the QuickTime 1904 epoch; a Takeout sidecar has no such
            // sentinel, and a family's scanned prints carry exactly these dates — applying the video
            // floor here would discard the most valuable metadata in the archive (§2.7).
            Assert.Equal(new DateTime(1987, 6, 1, 16, 0, 0, DateTimeKind.Utc),
                Find("scan-reencoded.jpg").Sidecar!.PhotoTakenUtc);
        }

        [Fact]
        public void ZeroedAndFutureTimestampsAreRefused()
        {
            // The shape a zeroed field takes, and a clock that ran away. Both would otherwise land on
            // the timeline's ends wearing a more convincing date than the undated shelf.
            Assert.Null(PhotoGoogleSidecar.ParseJson(
                "{\"title\":\"x.jpg\",\"photoTakenTime\":{\"timestamp\":\"0\"}}")?.PhotoTakenUtc);
            Assert.Null(PhotoGoogleSidecar.ParseJson(
                "{\"title\":\"x.jpg\",\"photoTakenTime\":{\"timestamp\":\"3600\"}}")?.PhotoTakenUtc);
            var future = new DateTimeOffset(DateTime.UtcNow.AddYears(1)).ToUnixTimeSeconds();
            Assert.Null(PhotoGoogleSidecar.ParseJson(
                $"{{\"title\":\"x.jpg\",\"photoTakenTime\":{{\"timestamp\":\"{future}\"}}}}")?.PhotoTakenUtc);
        }

        // ── Matching precedence (§2.10 step 2) ──────────────────────────────────────────────────

        [Fact]
        public async Task MatchesByEachRungInOrder()
        {
            await BuildLibraryAsync();
            BuildArchive();

            var mesh = Mesh();
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Match);

            using var db = fixture.NewDb();
            var byName = db.PhotoGoogleItems.Single(i => i.TakeoutFileName == "IMG_0001.jpg");
            Assert.Equal(PhotoGoogleItemStatus.Matched, byName.Status);
            Assert.Equal("name+size", byName.MatchMethod);
            Assert.Null(byName.MatchDistance);

            var bySha = db.PhotoGoogleItems.Single(i => i.TakeoutFileName == "renamed-by-google.jpg");
            Assert.Equal("sha256", bySha.MatchMethod);

            // The re-encode: neither the name nor the bytes agree, and the picture still does.
            var byPHash = db.PhotoGoogleItems.Single(i => i.TakeoutFileName == "scan-reencoded.jpg");
            Assert.Equal("phash", byPHash.MatchMethod);
            // The distance IS the lower-confidence marker, so it must be recorded.
            Assert.NotNull(byPHash.MatchDistance);
            Assert.InRange(byPHash.MatchDistance!.Value, 0, 8);
            var scan = db.PhotoAssets.Single(a => a.Path == "Album Scans/print.jpg");
            Assert.Equal(scan.Id, byPHash.MatchedPhotoAssetId);

            // Genuinely Google-only: nothing in the library looks like them.
            var googleOnly = db.PhotoGoogleItems.Where(i => i.Status == PhotoGoogleItemStatus.Unmatched).ToList();
            Assert.Contains(googleOnly, i => i.TakeoutFileName.StartsWith("a-very-long-name", StringComparison.Ordinal));
            Assert.Contains(googleOnly, i => i.TakeoutFileName == "IMG_0100.jpg");

            // Nothing is left undecided — which is also the download lane's precondition.
            Assert.Empty(db.PhotoGoogleItems.Where(i => i.Status == PhotoGoogleItemStatus.Pending));
        }

        [Fact]
        public async Task RerunningTheSameArchiveChangesNothing()
        {
            await BuildLibraryAsync();
            BuildArchive();

            var mesh = Mesh();
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Match);

            List<(int Id, string Name, PhotoGoogleItemStatus Status, int? Asset, string? Method)> Snapshot()
            {
                using var db = fixture.NewDb();
                return db.PhotoGoogleItems
                    .OrderBy(i => i.Id)
                    .Select(i => new { i.Id, i.TakeoutFileName, i.Status, i.MatchedPhotoAssetId, i.MatchMethod })
                    .ToList()
                    .Select(i => (i.Id, i.TakeoutFileName, i.Status, i.MatchedPhotoAssetId, i.MatchMethod))
                    .ToList();
            }

            var before = Snapshot();
            Assert.NotEmpty(before);

            // Next quarter's archive is this quarter's archive. A fresh engine, because a real re-run is
            // a new process.
            var again = Mesh();
            var scan = await again.RunAsync(PhotoGoogleMeshPass.Scan, null, 0);
            var match = await again.RunAsync(PhotoGoogleMeshPass.Match, null, 0);

            // Every item was recognised; none was minted a second time. (The unique index could not have
            // caught this: two of its three columns are nullable, so SQL Server filters it.)
            Assert.False(scan.Counts.ContainsKey("new"));
            Assert.Equal(before.Count, scan.Counts["unchanged"]);
            // Nothing re-entered the match queue: an item once Matched stays Matched.
            Assert.Equal(0, match.Processed);

            Assert.Equal(before, Snapshot());
        }

        // ── Backfill and the conflict rule (§2.10 step 3 / §2.7) ────────────────────────────────

        [Fact]
        public void SourceRankIsNotTheEnumOrdering()
        {
            // The trap this table exists to close: VideoContainer is the HIGHEST enum value (Phase 5
            // appended it to a live int column) and is a peer of Exif, not a human's answer.
            Assert.Equal(PhotoGoogleMesh.SourceRank(TakenAtSource.Exif),
                PhotoGoogleMesh.SourceRank(TakenAtSource.VideoContainer));
            Assert.True(PhotoGoogleMesh.SourceRank(TakenAtSource.Manual)
                        > PhotoGoogleMesh.SourceRank(TakenAtSource.VideoContainer));
            // §2.7's stated hierarchy, in both directions.
            Assert.True(PhotoGoogleMesh.SourceRank(TakenAtSource.GoogleSidecar)
                        > PhotoGoogleMesh.SourceRank(TakenAtSource.FilenameParsed));
            Assert.True(PhotoGoogleMesh.SourceRank(TakenAtSource.GoogleSidecar)
                        > PhotoGoogleMesh.SourceRank(TakenAtSource.FolderInferred));
            Assert.True(PhotoGoogleMesh.SourceRank(TakenAtSource.GoogleSidecar)
                        < PhotoGoogleMesh.SourceRank(TakenAtSource.Exif));
            Assert.True(PhotoGoogleMesh.SourceRank(TakenAtSource.GoogleSidecar)
                        < PhotoGoogleMesh.SourceRank(TakenAtSource.Manual));
        }

        [Fact]
        public async Task SidecarWinsOverAWeakerSourceAndIsFlagged()
        {
            // A file whose only date came from its NAME — the weaker source Google outranks.
            fixture.WriteJpeg("Camera/IMG_20140312_101530.jpg", 256, 192, seed: 21);
            var pipeline = fixture.Pipeline();
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Hash, null, 0);

            using (var db = fixture.NewDb())
            {
                var row = db.PhotoAssets.Single();
                Assert.Equal(TakenAtSource.FilenameParsed, row.TakenAtSource);
            }

            fixture.CopyIntoTakeout("Camera/IMG_20140312_101530.jpg", "Takeout/x/IMG_20140312_101530.jpg");
            // A whole day away from the filename's guess, so the disagreement is unambiguous.
            fixture.WriteTakeoutSidecar("Takeout/x/IMG_20140312_101530.jpg.json", "IMG_20140312_101530.jpg",
                new DateTime(2014, 3, 13, 20, 15, 30), latitude: 51.5, longitude: -0.12);

            var mesh = Mesh();
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Match);

            using var after = fixture.NewDb();
            var asset = after.PhotoAssets.Single();
            // Flag-BUT-WRITE: the better date is taken, and the fact that it displaced one is recorded.
            Assert.Equal(TakenAtSource.GoogleSidecar, asset.TakenAtSource);
            Assert.Equal(new DateTime(2014, 3, 13, 20, 15, 30, DateTimeKind.Utc), asset.TakenAtUtcRaw);
            // Wall clock, not UTC (§2.7): 20:15 UTC in the home zone is 16:15 that afternoon.
            Assert.Equal(new DateTime(2014, 3, 13, 16, 15, 30), asset.TakenAt);
            // GPS lands because the row had none.
            Assert.Equal(51.5, asset.GpsLat);

            var item = after.PhotoGoogleItems.Single();
            Assert.Equal("takenAt-overwritten:FilenameParsed", item.Disagreements);
        }

        [Fact]
        public async Task SidecarLosesToExifAndOnlyRecordsTheDisagreement()
        {
            fixture.WriteJpeg("Camera/IMG_0500.jpg", 256, 192, seed: 22,
                exifDateTimeOriginal: "2016:04:05 08:30:00");
            var pipeline = fixture.Pipeline();
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Hash, null, 0);

            fixture.CopyIntoTakeout("Camera/IMG_0500.jpg", "Takeout/x/IMG_0500.jpg");
            fixture.WriteTakeoutSidecar("Takeout/x/IMG_0500.jpg.json", "IMG_0500.jpg",
                new DateTime(2016, 4, 9, 12, 30, 0), latitude: 12.0, longitude: 13.0);

            // A coordinate already on the row, which the sidecar must not move.
            using (var db = fixture.NewDb())
            {
                var row = db.PhotoAssets.Single();
                row.GpsLat = 40.0;
                row.GpsLon = -75.0;
                db.SaveChanges();
            }

            var mesh = Mesh();
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);
            var match = await mesh.RunAsync(PhotoGoogleMeshPass.Match, null, 0);

            using var after = fixture.NewDb();
            var asset = after.PhotoAssets.Single();
            // The camera's own stamp stands, untouched.
            Assert.Equal(TakenAtSource.Exif, asset.TakenAtSource);
            Assert.Equal(new DateTime(2016, 4, 5, 8, 30, 0), asset.TakenAt);
            Assert.Equal(40.0, asset.GpsLat);
            Assert.Equal(-75.0, asset.GpsLon);

            var item = after.PhotoGoogleItems.Single();
            Assert.Equal("takenAt:Exif,gps", item.Disagreements);
            Assert.Equal(1, match.Counts["date-disagreements"]);
            Assert.Equal(1, match.Counts["gps-disagreements"]);
        }

        [Fact]
        public async Task AnAgreeingSidecarFlagsNothing()
        {
            fixture.WriteJpeg("Camera/IMG_0600.jpg", 256, 192, seed: 23,
                exifDateTimeOriginal: "2016:04:05 08:30:00");
            var pipeline = fixture.Pipeline();
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Hash, null, 0);

            fixture.CopyIntoTakeout("Camera/IMG_0600.jpg", "Takeout/x/IMG_0600.jpg");
            // 12:30 UTC is 08:30 in the home zone — the same moment, expressed the other way.
            fixture.WriteTakeoutSidecar("Takeout/x/IMG_0600.jpg.json", "IMG_0600.jpg",
                new DateTime(2016, 4, 5, 12, 30, 0));

            var mesh = Mesh();
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Match);

            using var after = fixture.NewDb();
            Assert.Null(after.PhotoGoogleItems.Single().Disagreements);
            Assert.Equal(TakenAtSource.Exif, after.PhotoAssets.Single().TakenAtSource);
        }

        // ── Google-only derivatives (§2.10 step 4 + §2.2) ───────────────────────────────────────

        [Fact]
        public async Task GoogleOnlyItemsGetThumbsInTheirOwnNamespace()
        {
            await BuildLibraryAsync();
            BuildArchive();

            var mesh = Mesh();
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Match);
            var thumbs = await mesh.RunAsync(PhotoGoogleMeshPass.Thumbs, null, 0);
            Assert.True(thumbs.Counts["thumbs"] > 0);

            using var db = fixture.NewDb();
            var item = db.PhotoGoogleItems.First(i => i.Status == PhotoGoogleItemStatus.Unmatched
                                                      && i.TakeoutFileName.EndsWith(".jpg"));
            var key = PhotoGoogleMesh.GoogleThumbKey(item);
            foreach (var size in PhotoThumbCache.GoogleVariants)
            {
                var relative = PhotoThumbCache.GoogleRelativePath(item.Id, key, size);
                // Its own namespace: an item id and an asset id are different id spaces over different
                // tables, and one cache directory serves both.
                Assert.StartsWith("google/", relative);
                Assert.True(File.Exists(Path.Combine(fixture.ThumbCache,
                    relative.Replace('/', Path.DirectorySeparatorChar))), relative);
            }

            // The pass is not self-draining (the fact lives in a directory, not a column), so a second
            // run must recognise its own output rather than re-decoding the archive.
            var again = await Mesh().RunAsync(PhotoGoogleMeshPass.Thumbs, null, 0);
            Assert.False(again.Counts.ContainsKey("thumbs"));
            Assert.True(again.Counts["already"] > 0);
        }

        // ── The download lane (§2.10's one additive write) ──────────────────────────────────────

        [Fact]
        public async Task DownloadRefusesWithoutASyncDirectory()
        {
            await BuildLibraryAsync();
            BuildArchive();
            var log = new List<string>();
            var mesh = Mesh(Options(download: false), log);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Match);

            var result = await mesh.RunAsync(PhotoGoogleMeshPass.Download, null, 0);

            Assert.Equal(1, result.Counts["refused"]);
            Assert.Contains(log, line => line.Contains("PhotosGoogleSyncDir"));
            Assert.Empty(fixture.GoogleSyncFilesOnDisk());
        }

        [Fact]
        public async Task DownloadRefusesUntilTheArchiveHasDrained()
        {
            await BuildLibraryAsync();
            BuildArchive();
            var log = new List<string>();
            var mesh = Mesh(Options(download: true), log);
            // Scanned but NOT matched: the state §2.10 forbids downloading from, because the pHash rung
            // has not yet ruled these items out and half of them are photographs we already own.
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);

            var refused = await mesh.RunAsync(PhotoGoogleMeshPass.Download, null, 0);
            Assert.Equal(1, refused.Counts["refused"]);
            Assert.Contains(log, line => line.Contains("match pass"));
            Assert.Empty(fixture.GoogleSyncFilesOnDisk());

            // Drain, and the same command proceeds.
            await DrainAsync(mesh, PhotoGoogleMeshPass.Match);
            var ran = await mesh.RunAsync(PhotoGoogleMeshPass.Download, null, 0);
            Assert.False(ran.Counts.ContainsKey("refused"));
            Assert.True(ran.Counts["downloaded"] > 0);

            // Foldered by the sidecar's WALL-CLOCK year, undated items on their own shelf.
            var files = fixture.GoogleSyncFilesOnDisk();
            Assert.Contains(files, f => f.StartsWith("2019/", StringComparison.Ordinal));
            Assert.Contains(files, f => f.StartsWith("undated/", StringComparison.Ordinal));
            // Nothing that matched the library was fetched.
            Assert.DoesNotContain(files, f => f.EndsWith("/IMG_0001.jpg", StringComparison.Ordinal));

            using (var db = fixture.NewDb())
            {
                var downloaded = db.PhotoGoogleItems.Where(i => i.Status == PhotoGoogleItemStatus.Downloaded).ToList();
                Assert.NotEmpty(downloaded);
                Assert.All(downloaded, i => Assert.False(string.IsNullOrEmpty(i.DownloadedPath)));
            }

            // Re-running downloads nothing: the items left the queue when they were stamped.
            var second = await Mesh(Options(download: true)).RunAsync(PhotoGoogleMeshPass.Download, null, 0);
            Assert.False(second.Counts.ContainsKey("downloaded"));
            Assert.Equal(files, fixture.GoogleSyncFilesOnDisk());
        }

        [Fact]
        public async Task DownloadNeverOverwritesAndSkipsIgnoredItems()
        {
            await BuildLibraryAsync();
            BuildArchive();
            var mesh = Mesh(Options(download: true));
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Match);

            string plantedPath;
            string ignoredName;
            using (var db = fixture.NewDb())
            {
                var unmatched = db.PhotoGoogleItems
                    .Where(i => i.Status == PhotoGoogleItemStatus.Unmatched)
                    .OrderBy(i => i.Id)
                    .ToList();
                Assert.True(unmatched.Count >= 2);

                // A file already sitting where one item would land. The lane must leave it exactly as it
                // is — no overwrite, no rename, no "(1)".
                var collide = unmatched[0];
                var year = collide.TakenAtUtc == null
                    ? "undated"
                    : PhotoDates.ToWallClock(collide.TakenAtUtc.Value,
                        PhotoDates.ResolveHomeZone("America/New_York")).Year.ToString();
                plantedPath = Path.Combine(fixture.GoogleSyncDir, year, collide.TakeoutFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(plantedPath)!);
                File.WriteAllText(plantedPath, "a file that was already here");

                // And one the family said no to.
                var ignored = unmatched[1];
                ignored.Status = PhotoGoogleItemStatus.Ignored;
                ignoredName = ignored.TakeoutFileName;
                db.SaveChanges();
            }

            var result = await Mesh(Options(download: true)).RunAsync(PhotoGoogleMeshPass.Download, null, 0);

            Assert.Equal(1, result.Counts["exists-skipped"]);
            Assert.Equal("a file that was already here", File.ReadAllText(plantedPath));
            // "No" is an answer; the lane must not re-ask it.
            Assert.DoesNotContain(fixture.GoogleSyncFilesOnDisk(),
                f => f.EndsWith("/" + ignoredName, StringComparison.OrdinalIgnoreCase));
        }

        // ── Review surface (§2.10 + §4) ─────────────────────────────────────────────────────────

        [Fact]
        public async Task ReviewEndpointsReportAndActOnTheMesh()
        {
            await BuildLibraryAsync();
            BuildArchive();
            var mesh = Mesh();
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Match);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Thumbs);

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            var stats = PhotosControllerHarness.Body(await controller.GoogleMesh());
            Assert.True(stats.GetProperty("ran").GetBoolean());
            Assert.True(stats.GetProperty("drained").GetBoolean());
            Assert.True(PhotosControllerHarness.Int(stats, "matched") >= 3);
            Assert.True(PhotosControllerHarness.Int(stats, "googleOnly") >= 2);
            var methods = stats.GetProperty("byMethod").EnumerateArray()
                .Select(m => m.GetProperty("method").GetString()).ToList();
            Assert.Contains("name+size", methods);
            Assert.Contains("sha256", methods);
            Assert.Contains("phash", methods);

            var list = PhotosControllerHarness.Body(await controller.GoogleOnly());
            var ids = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
            Assert.NotEmpty(ids);

            // Ignoring is a member action, and it removes the item from the default list.
            var ignore = PhotosControllerHarness.Body(
                await controller.IgnoreGoogleItems(new PhotoGoogleIgnoreRequest { Ids = new List<int> { ids[0] }, Ignored = true }));
            Assert.Equal(1, PhotosControllerHarness.Int(ignore, "updated"));

            var after = PhotosControllerHarness.Body(await controller.GoogleOnly());
            Assert.DoesNotContain(ids[0],
                after.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));

            // …but it is visible, and reversible, when asked for.
            var withIgnored = PhotosControllerHarness.Body(await controller.GoogleOnly(includeIgnored: true));
            Assert.Contains(ids[0],
                withIgnored.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));

            // The matched half surfaces the sidecar's DESCRIPTION on the asset, which is where it lives
            // in the absence of a caption column.
            var matched = db.PhotoGoogleItems.Single(i => i.TakeoutFileName == "IMG_0001.jpg");
            var asset = PhotosControllerHarness.Body(await controller.Asset(matched.MatchedPhotoAssetId!.Value));
            Assert.Equal("the first one", asset.GetProperty("google").GetProperty("description").GetString());
        }

        /// <summary>
        /// The Google-only list puts UNDATED items LAST, like every other surface (§2.7's shelf).
        ///
        /// <para>The intent was written in a comment beside the query and the query did the opposite:
        /// <c>OrderByDescending(is-null)</c> sorts <c>true</c> (1) ahead of <c>false</c> (0), so the
        /// items nobody can place led the review queue and the recognizable ones — the whole reason a
        /// family opens the list — were pages down.</para>
        /// </summary>
        [Fact]
        public async Task GoogleOnlyPutsUndatedItemsLast()
        {
            using var db = fixture.NewDb();
            var now = DateTime.UtcNow;
            foreach (var (name, taken) in new (string, DateTime?)[]
            {
                ("undated-a.jpg", null),
                ("older.jpg", new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                ("undated-b.jpg", null),
                ("newer.jpg", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            })
                db.PhotoGoogleItems.Add(new PhotoGoogleItem
                {
                    TakeoutFileName = name,
                    TakeoutRelativePath = "Takeout/Google Photos/x/" + name,
                    TakenAtUtc = taken,
                    Status = PhotoGoogleItemStatus.Unmatched,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
            await db.SaveChangesAsync();

            var controller = PhotosControllerHarness.Build(fixture, db);
            var names = PhotosControllerHarness.Body(await controller.GoogleOnly())
                .GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("fileName").GetString())
                .ToList();

            // Newest first among the dated, then the undated — the timeline's shape.
            Assert.Equal(new[] { "newer.jpg", "older.jpg" }, names.Take(2));
            Assert.Equal(2, names.Skip(2).Count(n => n!.StartsWith("undated", StringComparison.Ordinal)));
        }

        // ── The scan pass's two batch-level traps ───────────────────────────────────────────────

        /// <summary>
        /// One Takeout item living in TWO directories of the same archive is upserted ONCE.
        ///
        /// <para>Google's albums are copies of the same photograph, so this is the normal shape of an
        /// export rather than an edge case. The scan looked each item up in the DATABASE before
        /// inserting it — but the batch saves once, at the end, so a second directory reached inside the
        /// same batch found nothing and inserted a twin under the identical identity triple. The unique
        /// index cannot catch it either: two of its three columns are nullable, so SQL Server filters it
        /// to rows where both are non-null.</para>
        /// </summary>
        [Fact]
        public async Task AnItemInTwoAlbumDirectoriesIsUpsertedOnce()
        {
            const string first = "Takeout/Google Photos/Photos from 2019";
            const string second = "Takeout/Google Photos/A Family Album";
            var taken = new DateTime(2019, 5, 1, 13, 0, 0);

            fixture.WriteTakeoutJpeg($"{first}/IMG_5000.jpg", 64, 48, seed: 71);
            fixture.WriteTakeoutSidecar($"{first}/IMG_5000.jpg.json", "IMG_5000.jpg", taken);
            // The album copy: same name, same bytes, same sidecar date — the same photograph.
            fixture.WriteTakeoutJpeg($"{second}/IMG_5000.jpg", 64, 48, seed: 71);
            fixture.WriteTakeoutSidecar($"{second}/IMG_5000.jpg.json", "IMG_5000.jpg", taken);

            // Both directories in ONE batch, which is the case the per-directory database lookup misses.
            var options = Options();
            options.BatchSize = 100;
            await DrainAsync(Mesh(options), PhotoGoogleMeshPass.Scan);

            using var db = fixture.NewDb();
            Assert.Equal(1, await db.PhotoGoogleItems.CountAsync(i => i.TakeoutFileName == "IMG_5000.jpg"));
        }

        /// <summary>
        /// <c>--dry-run</c> on the match pass previews the WHOLE queue, not just its first batch.
        ///
        /// <para>The dry run reported <c>remaining: 0</c> because nothing had been persisted — which
        /// stopped the driver's loop after one batch and told the operator the pass had drained. A
        /// preview whose whole job is to say what a real run would do cannot answer "nothing left" after
        /// looking at three rows.</para>
        /// </summary>
        [Fact]
        public async Task ADryRunMatchPassPreviewsEveryRowAndLeavesThemPending()
        {
            await BuildLibraryAsync();
            BuildArchive();
            await DrainAsync(Mesh(), PhotoGoogleMeshPass.Scan);

            int pending;
            using (var db = fixture.NewDb())
                pending = await db.PhotoGoogleItems.CountAsync(i => i.Status == PhotoGoogleItemStatus.Pending);
            // The batch size is 3, so a single batch could never be the whole queue.
            Assert.True(pending > 3, $"the archive should leave more than one batch pending, got {pending}");

            var options = Options();
            options.DryRun = true;
            var log = new List<string>();
            var report = await Mesh(options, log).RunAsync(PhotoGoogleMeshPass.Match, null, 0);

            Assert.Equal(pending, report.Processed);
            Assert.Equal(0, report.Remaining);
            Assert.True(log.Count > 1, "a dry run must walk the queue in chunks, not stop after the first");

            // And it wrote nothing: every row is still Pending, waiting for the real run.
            using (var db = fixture.NewDb())
                Assert.Equal(pending,
                    await db.PhotoGoogleItems.CountAsync(i => i.Status == PhotoGoogleItemStatus.Pending));
        }

        [Fact]
        public async Task ReviewEndpointsAreBehindTheFamilyGate()
        {
            // The gate is a class-level policy, so this asserts the ATTRIBUTE rather than re-hosting
            // ASP.NET — FamilyAlbumGateTests proves the middleware end to end.
            var attributes = typeof(MovieTheater.Controllers.PhotosController)
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true);
            Assert.Single(attributes);
            Assert.Equal(FamilyAlbumGate.PolicyName,
                ((Microsoft.AspNetCore.Authorization.AuthorizeAttribute)attributes[0]).Policy);

            foreach (var name in new[] { nameof(PhotosController.GoogleMesh), nameof(PhotosController.GoogleOnly),
                nameof(PhotosController.IgnoreGoogleItems) })
                Assert.NotNull(typeof(PhotosController).GetMethod(name));
            await Task.CompletedTask;
        }

        // ── Export round trip (§2.11) ───────────────────────────────────────────────────────────

        [Fact]
        public async Task MeshStateRoundTripsThroughExportAndImport()
        {
            await BuildLibraryAsync();
            BuildArchive();
            var mesh = Mesh();
            await DrainAsync(mesh, PhotoGoogleMeshPass.Scan);
            await DrainAsync(mesh, PhotoGoogleMeshPass.Match);

            using (var db = fixture.NewDb())
            {
                // A hand-made decision the export has to carry: one ignored item.
                var ignored = db.PhotoGoogleItems.First(i => i.Status == PhotoGoogleItemStatus.Unmatched);
                ignored.Status = PhotoGoogleItemStatus.Ignored;
                db.SaveChanges();
            }

            var exportDir = fixture.ExportDir("google");
            var exporter = new PhotoCurationExporter(fixture.NewDb, _ => { });
            await exporter.RunAsync(exportDir);

            // Into a database that never held any of it — the only honest way to prove a restore.
            var rebuilt = fixture.SecondaryDbFactory("rebuilt-google");
            using (var db = rebuilt())
            {
                // The assets have to exist for a matched item to resolve onto one; the export keys them
                // by hash and path, never by id.
                using var source = fixture.NewDb();
                foreach (var a in source.PhotoAssets.ToList())
                    db.PhotoAssets.Add(new PhotoAsset
                    {
                        Path = a.Path,
                        SizeBytes = a.SizeBytes,
                        FileModifiedUtc = a.FileModifiedUtc,
                        Kind = a.Kind,
                        Sha256 = a.Sha256,
                        FirstSeenUtc = a.FirstSeenUtc,
                    });
                db.SaveChanges();
            }

            var importer = new PhotoCurationImporter(rebuilt, exportDir, apply: true, _ => { });
            await importer.RunAsync(null, 0);

            using var restored = rebuilt();
            using var original = fixture.NewDb();

            var expected = original.PhotoGoogleItems
                .OrderBy(i => i.TakeoutFileName)
                .Select(i => new { i.TakeoutFileName, i.Status, i.MatchMethod, i.MatchDistance, i.Disagreements })
                .ToList();
            var actual = restored.PhotoGoogleItems
                .OrderBy(i => i.TakeoutFileName)
                .Select(i => new { i.TakeoutFileName, i.Status, i.MatchMethod, i.MatchDistance, i.Disagreements })
                .ToList();

            Assert.Equal(expected, actual);
            Assert.Contains(actual, i => i.Status == PhotoGoogleItemStatus.Ignored);
            Assert.Contains(actual, i => i.MatchMethod == "phash" && i.MatchDistance != null);
        }
    }
}
