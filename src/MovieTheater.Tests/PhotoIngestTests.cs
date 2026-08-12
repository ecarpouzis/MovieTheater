using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Core;
using MovieTheater.Db;
using MovieTheater.Photos;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The Phase 1 ingest pipeline (docs/photos-plan.md §2.5), driven against a GENERATED fixture tree
    /// and a throwaway SQLite file — never the real collection, never the configured database.
    ///
    /// <para>The properties under test are the ones whose failure would be silent: a chunked walk that
    /// skips a folder, a move that orphans years of tags, a scanner's date accepted as a capture date,
    /// a sideways photo. Each is asserted against an INDEPENDENT fact (a fresh count of the disk, a row
    /// id captured before the move) rather than against the pipeline's own report.</para>
    /// </summary>
    public class PhotoIngestTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        /// <summary>
        /// A tree shaped like the real one where it matters: loose files at the root, a space-bearing
        /// folder name beside a nested one (the cursor-ordering trap), a scans folder, a folder whose
        /// name carries a year, and a video.
        /// </summary>
        private void BuildTree()
        {
            fixture.WriteJpeg("loose.jpg", seed: 11);
            fixture.WriteJpeg("A Folder/a1.jpg", seed: 12);
            fixture.WritePng("A Folder/a2.png", seed: 13);
            fixture.WriteJpeg("A Folder/Sub/s1.jpg", seed: 14);
            // Sorts BETWEEN "A Folder" and "A Folder/Sub" under a plain ordinal compare, and AFTER
            // both under the walk's key — the whole reason PhotoWalkCursor exists.
            fixture.WriteJpeg("A Folder 2/b1.jpg", seed: 15);
            fixture.WriteJpeg("Album Scans/scan1.jpg", seed: 16, exifDateTimeOriginal: "2019:05:04 09:00:00");
            fixture.WriteJpeg("Vacation 2004/IMG_20140312_101530.jpg", seed: 17);
            fixture.WriteJpeg("Vacation 2004/plain.jpg", seed: 18);
            fixture.WriteOpaque("Videos/clip.mp4");
            fixture.WriteJpeg("Zebra/z1.jpg", seed: 19);
        }

        private static async Task<PhotoIngestBatchResult> DriveWalk(
            PhotoIngestPipeline pipeline, List<string> cursors, int safety = 100)
        {
            // The DRIVER loop lives in the caller, per the standing bulk-job rule — the pipeline only
            // ever does one bounded batch.
            PhotoIngestBatchResult last = null!;
            string? cursor = null;
            for (var i = 0; i < safety; i++)
            {
                last = await pipeline.WalkBatchAsync(cursor);
                cursors.Add(last.NextCursor);
                if (last.Remaining <= 0) break;
                Assert.True(last.Processed > 0, "a batch made no progress while work remained");
                cursor = last.NextCursor;
            }
            Assert.True(last.Remaining <= 0, "the walk did not drain within the safety bound");
            return last;
        }

        // ── The walk: chunking, ordering, resume ─────────────────────────────────────────────────

        [Fact]
        public async Task Walk_chunked_to_completion_sees_every_file_exactly_once()
        {
            BuildTree();
            var expected = fixture.MediaFilesOnDisk();

            var cursors = new List<string>();
            await DriveWalk(fixture.Pipeline(fixture.Options(batchSize: 2)), cursors);

            using var db = fixture.NewDb();
            var paths = await db.PhotoAssets.Select(a => a.Path).ToListAsync();

            // Against an INDEPENDENT enumeration of the disk, not against the pipeline's counters.
            Assert.Equal(expected.Count, paths.Count);
            Assert.Equal(expected, paths.OrderBy(p => p, StringComparer.Ordinal).ToList());
            Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.True(cursors.Count > 2, "batch size 2 over this tree should have taken several batches");
        }

        [Fact]
        public async Task Walk_resumes_after_a_kill_mid_tree_with_no_skips_or_duplicates()
        {
            BuildTree();
            var expected = fixture.MediaFilesOnDisk();

            // One batch, then the process "dies": a brand-new pipeline and a brand-new context pick up
            // from nothing but the printed cursor.
            var first = await fixture.Pipeline(fixture.Options(batchSize: 2)).WalkBatchAsync(null);
            Assert.True(first.Remaining > 0);

            var resumed = fixture.Pipeline(fixture.Options(batchSize: 2));
            var cursor = first.NextCursor;
            for (var i = 0; i < 100; i++)
            {
                var batch = await resumed.WalkBatchAsync(cursor);
                cursor = batch.NextCursor;
                if (batch.Remaining <= 0) break;
            }

            using var db = fixture.NewDb();
            var paths = (await db.PhotoAssets.Select(a => a.Path).ToListAsync())
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, paths);
        }

        [Fact]
        public async Task Re_walking_an_unchanged_tree_inserts_nothing_and_short_circuits()
        {
            BuildTree();
            await DriveWalk(fixture.Pipeline(fixture.Options(batchSize: 3)), new List<string>());

            var second = await fixture.Pipeline(fixture.Options(batchSize: 1000)).WalkBatchAsync(null);

            Assert.False(second.Counts.ContainsKey("inserted"));
            Assert.Equal(fixture.MediaFilesOnDisk().Count, second.Counts["unchanged"]);
            Assert.False(second.Counts.ContainsKey("went-missing"));
        }

        [Fact]
        public async Task A_deleted_file_is_flagged_missing_and_never_deleted()
        {
            BuildTree();
            await DriveWalk(fixture.Pipeline(fixture.Options(batchSize: 1000)), new List<string>());

            fixture.Delete("Zebra/z1.jpg");
            await fixture.Pipeline(fixture.Options(batchSize: 1000)).WalkBatchAsync(null);

            using var db = fixture.NewDb();
            var row = await db.PhotoAssets.SingleAsync(a => a.Path == "Zebra/z1.jpg");
            Assert.NotNull(row.MissingSinceUtc);
            Assert.Equal(fixture.MediaFilesOnDisk().Count + 1, await db.PhotoAssets.CountAsync());
        }

        [Fact]
        public async Task A_dry_run_walk_writes_nothing()
        {
            BuildTree();
            var options = fixture.Options(batchSize: 1000);
            options.DryRun = true;

            var result = await fixture.Pipeline(options).WalkBatchAsync(null);

            Assert.True(result.Counts["inserted"] > 0);
            using var db = fixture.NewDb();
            Assert.Equal(0, await db.PhotoAssets.CountAsync());
        }

        // ── The identity rule (§2.5): content is identity, path is location ──────────────────────

        [Fact]
        public async Task A_moved_file_keeps_its_row_id_and_everything_hanging_off_it()
        {
            BuildTree();
            await DriveWalk(fixture.Pipeline(fixture.Options(batchSize: 1000)), new List<string>());

            int assetId;
            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync(a => a.Path == "Zebra/z1.jpg");
                assetId = row.Id;
                // Stand-in for the irreplaceable curation the identity rule exists to protect (§2.11).
                var person = new FamilyPerson { Name = "Subject A" };
                db.FamilyPeople.Add(person);
                db.PhotoPersonTags.Add(new PhotoPersonTag
                {
                    PhotoAsset = row,
                    FamilyPerson = person,
                    Source = PhotoTagSource.Manual,
                });
                await db.SaveChangesAsync();
            }

            fixture.Move("Zebra/z1.jpg", "A Folder/Sub/z1.jpg");
            await fixture.Pipeline(fixture.Options(batchSize: 1000)).WalkBatchAsync(null);

            using (var db = fixture.NewDb())
            {
                var moved = await db.PhotoAssets.SingleAsync(a => a.Id == assetId);
                Assert.Equal("A Folder/Sub/z1.jpg", moved.Path);
                Assert.Null(moved.MissingSinceUtc);
                Assert.Equal(1, await db.PhotoPersonTags.CountAsync(t => t.PhotoAssetId == assetId));
                // No second row was born for the new path.
                Assert.Equal(fixture.MediaFilesOnDisk().Count, await db.PhotoAssets.CountAsync());
            }
        }

        [Fact]
        public async Task A_move_is_re_paired_even_when_the_destination_is_walked_before_the_source()
        {
            // "A Folder" sorts before "Zebra", so a Zebra→A-Folder move is seen destination-first when
            // the batch is small enough to split them. Without the "is this row's file still on disk"
            // test, exactly half of all real moves would silently become an orphan plus a new row.
            BuildTree();
            await DriveWalk(fixture.Pipeline(fixture.Options(batchSize: 1000)), new List<string>());

            int assetId;
            using (var db = fixture.NewDb())
                assetId = (await db.PhotoAssets.SingleAsync(a => a.Path == "Zebra/z1.jpg")).Id;

            fixture.Move("Zebra/z1.jpg", "A Folder/z1.jpg");

            var pipeline = fixture.Pipeline(fixture.Options(batchSize: 1));
            var cursor = (string?)null;
            for (var i = 0; i < 100; i++)
            {
                var batch = await pipeline.WalkBatchAsync(cursor);
                cursor = batch.NextCursor;
                if (batch.Remaining <= 0) break;
            }

            using (var db = fixture.NewDb())
            {
                var moved = await db.PhotoAssets.SingleAsync(a => a.Id == assetId);
                Assert.Equal("A Folder/z1.jpg", moved.Path);
                Assert.Equal(fixture.MediaFilesOnDisk().Count, await db.PhotoAssets.CountAsync());
            }
        }

        [Fact]
        public async Task An_ambiguous_pairing_is_recorded_for_review_and_never_applied()
        {
            // Two byte-identical files with the same name in different folders, both moved at once:
            // the pipeline cannot know which went where, so §2.5 says it must do NOTHING.
            fixture.WriteJpeg("One/same.jpg", seed: 5);
            File.Copy(fixture.FullPath("One/same.jpg"), EnsureDir(fixture.FullPath("Two/same.jpg")));
            await DriveWalk(fixture.Pipeline(fixture.Options(batchSize: 1000)), new List<string>());

            fixture.Move("One/same.jpg", "Three/same.jpg");
            fixture.Move("Two/same.jpg", "Four/same.jpg");

            var log = new List<string>();
            var result = await fixture.Pipeline(fixture.Options(batchSize: 1000), log).WalkBatchAsync(null);

            Assert.Equal(2, result.Counts["ambiguous-pairings"]);
            Assert.False(result.Counts.ContainsKey("inserted"));
            Assert.False(result.Counts.ContainsKey("re-paired"));

            using var db = fixture.NewDb();
            // Both original rows stay, flagged missing — nothing was re-pointed and nothing was born.
            Assert.Equal(2, await db.PhotoAssets.CountAsync());
            Assert.Equal(2, await db.PhotoAssets.CountAsync(a => a.MissingSinceUtc != null));

            var report = Directory.GetFiles(fixture.ReportDir, "path-repair-*.json").Single();
            var text = File.ReadAllText(report);
            Assert.Contains("Three/same.jpg", text);
            Assert.Contains("Four/same.jpg", text);
        }

        private static string EnsureDir(string full)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            return full;
        }

        // ── Metadata + dates (§2.7) ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task Exif_dates_are_taken_as_wall_clock_and_scans_are_distrusted()
        {
            fixture.WriteJpeg("Trip/photo.jpg", exifDateTimeOriginal: "2014:03:12 10:15:30", seed: 21);
            // Same EXIF shape, but under a scans folder: the stamp is the DATE OF THE SCAN, and taking
            // it would date a print to the day it was digitized (§2.7).
            fixture.WriteJpeg("Album Scans/print.jpg", exifDateTimeOriginal: "2019:05:04 09:00:00", seed: 22);
            // A scanner identified by its EXIF Make rather than by its folder.
            fixture.WriteJpeg("Loose/print2.jpg", exifDateTimeOriginal: "2019:05:04 09:00:00",
                make: "EPSON", model: "Perfection V600", seed: 23);

            await RunAll();

            using var db = fixture.NewDb();
            var trip = await db.PhotoAssets.SingleAsync(a => a.Path == "Trip/photo.jpg");
            Assert.Equal(TakenAtSource.Exif, trip.TakenAtSource);
            Assert.Equal(new DateTime(2014, 3, 12, 10, 15, 30), trip.TakenAt);
            // Wall-clock, deliberately: no offset was applied and no UTC was recorded.
            Assert.Null(trip.TakenAtUtcRaw);

            foreach (var path in new[] { "Album Scans/print.jpg", "Loose/print2.jpg" })
            {
                var scan = await db.PhotoAssets.SingleAsync(a => a.Path == path);
                Assert.NotEqual(TakenAtSource.Exif, scan.TakenAtSource);
                Assert.Null(scan.TakenAt);
                // The EXIF value is not lost — it is kept raw so the dating UI can show what it saw.
                Assert.Contains("2019", scan.RawMetadataJson);
            }
        }

        [Fact]
        public async Task A_true_utc_source_is_converted_through_the_home_zone_and_kept_raw()
        {
            // GPS date+time is the one genuinely UTC-anchored clock in a photo file.
            fixture.WriteJpeg("Trip/gps.jpg", seed: 24, gpsDateStamp: "2014:07:04", gpsTimeStamp: "02:30:00");

            await RunAll();

            using var db = fixture.NewDb();
            var row = await db.PhotoAssets.SingleAsync(a => a.Path == "Trip/gps.jpg");
            Assert.Equal(new DateTime(2014, 7, 4, 2, 30, 0), row.TakenAtUtcRaw);
            // America/New_York in July is UTC-4: the wall clock is the PREVIOUS evening, which is the
            // whole reason the two representations are never mixed into one column.
            Assert.Equal(new DateTime(2014, 7, 3, 22, 30, 0), row.TakenAt);
            Assert.Equal(TakenAtSource.Exif, row.TakenAtSource);
        }

        [Fact]
        public async Task Filenames_and_folder_years_are_the_fallback_rungs()
        {
            fixture.WriteJpeg("Misc/IMG_20140312_101530.jpg", seed: 25);
            fixture.WriteJpeg("Misc/Overlook 7-4-2010.jpg", seed: 26);
            fixture.WriteJpeg("Vacation 2004/nothing.jpg", seed: 27);
            fixture.WriteJpeg("Misc/nothing.jpg", seed: 28);

            await RunAll();

            using var db = fixture.NewDb();
            var stamped = await db.PhotoAssets.SingleAsync(a => a.Path == "Misc/IMG_20140312_101530.jpg");
            Assert.Equal(TakenAtSource.FilenameParsed, stamped.TakenAtSource);
            Assert.Equal(new DateTime(2014, 3, 12, 10, 15, 30), stamped.TakenAt);

            var typed = await db.PhotoAssets.SingleAsync(a => a.Path == "Misc/Overlook 7-4-2010.jpg");
            Assert.Equal(TakenAtSource.FilenameParsed, typed.TakenAtSource);
            Assert.Equal(new DateTime(2010, 7, 4), typed.TakenAt);

            // A folder year is a BOUND, not a date: writing January 1st would pile a decade of photos
            // onto one day, which is the failure §2.7's undated shelf exists to avoid.
            var inferred = await db.PhotoAssets.SingleAsync(a => a.Path == "Vacation 2004/nothing.jpg");
            Assert.Equal(TakenAtSource.FolderInferred, inferred.TakenAtSource);
            Assert.Null(inferred.TakenAt);
            Assert.Equal(2004, inferred.YearMin);
            Assert.Equal(2004, inferred.YearMax);

            var unknown = await db.PhotoAssets.SingleAsync(a => a.Path == "Misc/nothing.jpg");
            Assert.Equal(TakenAtSource.Unknown, unknown.TakenAtSource);
            Assert.Null(unknown.TakenAt);
        }

        /// <summary>
        /// §2.7's source hierarchy and §2.11's durability, on the path that actually threatens them: the
        /// metadata pass RE-RUNNING.
        ///
        /// <para>It re-runs whenever the walk sees a file's size or mtime change, and again on every
        /// <c>--retry-errors</c>. It used to assign <c>TakenAt</c>, <c>TakenAtSource</c>, <c>YearMin</c>
        /// and <c>YearMax</c> unconditionally — so an mtime touch (a NAS copy, a permission fix, a
        /// re-scan of a print) silently threw away the date a human had typed and replaced it with EXIF,
        /// with a filename guess, or with nothing at all. Hand curation is the one thing in this
        /// vertical that cannot be regenerated.</para>
        /// </summary>
        [Fact]
        public async Task A_hand_set_date_survives_a_metadata_re_run_after_the_file_changes()
        {
            // Three shapes a human dates, each with a DIFFERENT machine answer waiting to overwrite it:
            // real EXIF, a parseable filename, and nothing whatsoever.
            fixture.WriteJpeg("Trip/exif.jpg", seed: 41, exifDateTimeOriginal: "2019:05:04 09:00:00");
            fixture.WriteJpeg("Trip/IMG_20140312_101530.jpg", seed: 42);
            fixture.WriteJpeg("Album Scans/print.jpg", seed: 43);
            await RunAll();

            using (var db = fixture.NewDb())
            {
                var manual = await db.PhotoAssets.SingleAsync(a => a.Path == "Trip/exif.jpg");
                manual.TakenAt = new DateTime(1994, 12, 25, 8, 0, 0);
                manual.TakenAtSource = TakenAtSource.Manual;

                var alsoManual = await db.PhotoAssets.SingleAsync(a => a.Path == "Trip/IMG_20140312_101530.jpg");
                alsoManual.TakenAt = new DateTime(1994, 12, 25, 9, 0, 0);
                alsoManual.TakenAtSource = TakenAtSource.Manual;

                // A circa range is the answer a box of undated prints actually supports (§2.7), and it
                // is a human's answer too — TakenAt stays null on purpose, which is exactly what the
                // old "no date found" branch would have overwritten with Unknown.
                var estimated = await db.PhotoAssets.SingleAsync(a => a.Path == "Album Scans/print.jpg");
                estimated.TakenAtSource = TakenAtSource.Estimated;
                estimated.YearMin = 1972;
                estimated.YearMax = 1975;
                await db.SaveChangesAsync();
            }

            // Touch every file so the walk re-queues the metadata pass for all three, then drain it.
            System.Threading.Thread.Sleep(10);
            fixture.WriteJpeg("Trip/exif.jpg", seed: 44, exifDateTimeOriginal: "2019:05:04 09:00:00");
            fixture.WriteJpeg("Trip/IMG_20140312_101530.jpg", seed: 45);
            fixture.WriteJpeg("Album Scans/print.jpg", seed: 46);
            await fixture.Pipeline(fixture.Options(batchSize: 1000)).WalkBatchAsync(null);
            await fixture.Pipeline(fixture.Options(batchSize: 1000)).RunAsync(PhotoIngestPass.Metadata, null, 0);

            using (var db = fixture.NewDb())
            {
                var manual = await db.PhotoAssets.SingleAsync(a => a.Path == "Trip/exif.jpg");
                Assert.Equal(TakenAtSource.Manual, manual.TakenAtSource);
                Assert.Equal(new DateTime(1994, 12, 25, 8, 0, 0), manual.TakenAt);

                var alsoManual = await db.PhotoAssets.SingleAsync(a => a.Path == "Trip/IMG_20140312_101530.jpg");
                Assert.Equal(TakenAtSource.Manual, alsoManual.TakenAtSource);
                Assert.Equal(new DateTime(1994, 12, 25, 9, 0, 0), alsoManual.TakenAt);

                var estimated = await db.PhotoAssets.SingleAsync(a => a.Path == "Album Scans/print.jpg");
                Assert.Equal(TakenAtSource.Estimated, estimated.TakenAtSource);
                Assert.Null(estimated.TakenAt);
                Assert.Equal(1972, estimated.YearMin);
                Assert.Equal(1975, estimated.YearMax);

                // The pass still did its OTHER job: this is a no-downgrade rule about dates, not a
                // reason to stop re-reading the file.
                Assert.NotNull(manual.MetadataUpdatedUtc);
            }
        }

        /// <summary>The same rule the other way round: a machine date the pass can IMPROVE is still
        /// improved. A guard that simply froze every dated row would pass the test above and quietly
        /// break the pipeline.</summary>
        [Fact]
        public async Task A_weaker_machine_date_is_still_upgraded_by_a_re_run()
        {
            fixture.WriteJpeg("Vacation 2004/plain.jpg", seed: 47);
            await RunAll();

            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync();
                Assert.Equal(TakenAtSource.FolderInferred, row.TakenAtSource);
            }

            // The file gains real EXIF (a re-export, a metadata repair). Exif outranks FolderInferred,
            // so the re-run must take it.
            System.Threading.Thread.Sleep(10);
            fixture.WriteJpeg("Vacation 2004/plain.jpg", seed: 48, exifDateTimeOriginal: "2004:07:04 10:00:00");
            await fixture.Pipeline(fixture.Options(batchSize: 1000)).WalkBatchAsync(null);
            await fixture.Pipeline(fixture.Options(batchSize: 1000)).RunAsync(PhotoIngestPass.Metadata, null, 0);

            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync();
                Assert.Equal(TakenAtSource.Exif, row.TakenAtSource);
                Assert.Equal(new DateTime(2004, 7, 4, 10, 0, 0), row.TakenAt);
            }
        }

        [Fact]
        public async Task Orientation_is_applied_to_the_stored_dimensions_and_to_the_derivatives()
        {
            // 80×40 pixels stored, EXIF orientation 6 (rotate 90° CW to display) ⇒ shown 40×80.
            fixture.WriteJpeg("Trip/sideways.jpg", width: 80, height: 40, seed: 29, orientation: 6);
            fixture.WriteJpeg("Trip/upright.jpg", width: 80, height: 40, seed: 30);

            await RunAll();

            using var db = fixture.NewDb();
            var sideways = await db.PhotoAssets.SingleAsync(a => a.Path == "Trip/sideways.jpg");
            var upright = await db.PhotoAssets.SingleAsync(a => a.Path == "Trip/upright.jpg");
            Assert.Equal(40, sideways.Width);
            Assert.Equal(80, sideways.Height);
            Assert.Equal(80, upright.Width);
            Assert.Equal(40, upright.Height);
        }

        // ── Hashes (§2.6) ────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Hashes_are_stable_identical_for_identical_bytes_and_different_for_different_pictures()
        {
            fixture.WriteJpeg("H/one.jpg", width: 96, height: 96, seed: 41);
            File.Copy(fixture.FullPath("H/one.jpg"), EnsureDir(fixture.FullPath("H/copy.jpg")));
            fixture.WriteJpeg("H/other.jpg", width: 96, height: 96, seed: 42);

            await RunAll();

            using var db = fixture.NewDb();
            var one = await db.PhotoAssets.SingleAsync(a => a.Path == "H/one.jpg");
            var copy = await db.PhotoAssets.SingleAsync(a => a.Path == "H/copy.jpg");
            var other = await db.PhotoAssets.SingleAsync(a => a.Path == "H/other.jpg");

            Assert.NotNull(one.Sha256);
            Assert.Equal(64, one.Sha256!.Length);
            Assert.Equal(one.Sha256, copy.Sha256);
            Assert.Equal(one.PHash, copy.PHash);
            Assert.Equal(one.DHash, copy.DHash);

            Assert.NotEqual(one.Sha256, other.Sha256);
            // Different pictures must be far apart perceptually, or near-dupe grouping is meaningless.
            Assert.True(PhotoHashes.HammingDistance(one.PHash!.Value, other.PHash!.Value) > 10);
        }

        [Fact]
        public async Task Re_running_the_hash_pass_over_the_same_file_reproduces_the_same_hashes()
        {
            fixture.WriteJpeg("H/one.jpg", width: 96, height: 96, seed: 43);
            await RunAll();

            long phash, dhash;
            string sha;
            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync();
                phash = row.PHash!.Value; dhash = row.DHash!.Value; sha = row.Sha256!;
                row.HashUpdatedUtc = null;
                row.PHash = null; row.DHash = null; row.Sha256 = null;
                await db.SaveChangesAsync();
            }

            await fixture.Pipeline(fixture.Options(batchSize: 1000)).RunAsync(PhotoIngestPass.Hash, null, 0);

            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync();
                Assert.Equal(sha, row.Sha256);
                Assert.Equal(phash, row.PHash);
                Assert.Equal(dhash, row.DHash);
            }
        }

        // ── Derivatives (§2.2) ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_thumb_pass_emits_grid_and_view_for_a_renderable_original()
        {
            fixture.WriteJpeg("T/photo.jpg", width: 1200, height: 900, seed: 51);
            await RunAll();

            using var db = fixture.NewDb();
            var row = await db.PhotoAssets.SingleAsync();
            Assert.Equal(PhotoThumbState.Ready, row.ThumbState);
            Assert.True(row.OriginalRenderable);
            Assert.Equal("grid,view", row.ThumbVariants);
            Assert.NotNull(row.ThumbKey);

            foreach (var size in new[] { PhotoStreamRoutes.SizeGrid, PhotoStreamRoutes.SizeView })
            {
                var relative = PhotoThumbCache.RelativePath(row.Id, row.ThumbKey!, size);
                var full = Path.Combine(fixture.ThumbCache, relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(full), $"missing derivative: {relative}");
                using var image = SixLabors.ImageSharp.Image.Load(full);
                Assert.True(Math.Max(image.Width, image.Height) <= PhotoThumbCache.MaxEdgeFor(size));
            }
            // The zoom derivative is NOT emitted for a renderable original — the lightbox deep-zooms
            // from PhotoOriginal instead (§2.2), so generating one would be tens of GB for nothing.
            Assert.False(File.Exists(Path.Combine(fixture.ThumbCache,
                PhotoThumbCache.RelativePath(row.Id, row.ThumbKey!, PhotoStreamRoutes.SizeZoom)
                    .Replace('/', Path.DirectorySeparatorChar))));
        }

        [Fact]
        public async Task A_non_renderable_original_also_gets_the_zoom_derivative()
        {
            // TIFF: ImageSharp decodes it, no browser displays it.
            var full = fixture.FullPath("T/scan.tiff");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(300, 200))
            using (var stream = File.Create(full))
                image.Save(stream, new SixLabors.ImageSharp.Formats.Tiff.TiffEncoder());

            await RunAll();

            using var db = fixture.NewDb();
            var row = await db.PhotoAssets.SingleAsync();
            Assert.False(row.OriginalRenderable);
            Assert.Equal("grid,view,zoom", row.ThumbVariants);
            Assert.Equal(PhotoThumbState.Ready, row.ThumbState);
        }

        [Fact]
        public async Task A_small_original_is_never_upscaled()
        {
            fixture.WriteJpeg("T/tiny.jpg", width: 60, height: 40, seed: 52);
            await RunAll();

            using var db = fixture.NewDb();
            var row = await db.PhotoAssets.SingleAsync();
            var full = Path.Combine(fixture.ThumbCache,
                PhotoThumbCache.RelativePath(row.Id, row.ThumbKey!, PhotoStreamRoutes.SizeView)
                    .Replace('/', Path.DirectorySeparatorChar));
            using var image = SixLabors.ImageSharp.Image.Load(full);
            Assert.Equal(60, image.Width);
            Assert.Equal(40, image.Height);
        }

        [Fact]
        public async Task Videos_are_skeleton_rows_with_a_deterministic_placeholder_state()
        {
            fixture.WriteOpaque("V/clip.mp4");
            fixture.WriteJpeg("V/still.jpg", seed: 53);

            await RunAll();

            using var db = fixture.NewDb();
            var video = await db.PhotoAssets.SingleAsync(a => a.Kind == PhotoAssetKind.Video);
            // Phase 1 does not run ffprobe (that is Phase 5), so the row is deliberately bare — but its
            // thumb state is a VALUE the UI can render, not a null the UI has to guess about.
            Assert.Equal(PhotoThumbState.VideoDeferred, video.ThumbState);
            Assert.Null(video.Width);
            Assert.Null(video.DurationSec);
            Assert.Null(video.ThumbKey);
            // It is still hashed: content identity is what re-pairs it after a folder reorganization.
            Assert.NotNull(video.Sha256);
            Assert.Null(video.PHash);
            Assert.Null(video.IngestError);
        }

        [Fact]
        public async Task A_format_this_build_cannot_decode_is_catalogued_not_failed()
        {
            // HEIC needs a decoder this build deliberately does not ship (§2.2 note).
            fixture.WriteOpaque("V/phone.heic");

            await RunAll();

            using var db = fixture.NewDb();
            var row = await db.PhotoAssets.SingleAsync();
            Assert.Equal(PhotoAssetKind.Photo, row.Kind);
            Assert.False(row.OriginalRenderable);
            Assert.Equal(PhotoThumbState.UnsupportedFormat, row.ThumbState);
            Assert.NotNull(row.Sha256);
        }

        // ── Queue mechanics ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Every_queue_drains_to_zero_and_a_second_run_finds_nothing_to_do()
        {
            BuildTree();
            await RunAll();

            foreach (var pass in new[] { PhotoIngestPass.Metadata, PhotoIngestPass.Hash, PhotoIngestPass.Thumb })
            {
                var again = await fixture.Pipeline(fixture.Options(batchSize: 1000)).QueueBatchAsync(pass, 0);
                Assert.Equal(0, again.Processed);
                Assert.Equal(0, again.Remaining);
            }
        }

        [Fact]
        public async Task Queue_batches_are_bounded_and_report_shrinking_remaining()
        {
            BuildTree();
            await DriveWalk(fixture.Pipeline(fixture.Options(batchSize: 1000)), new List<string>());

            var pipeline = fixture.Pipeline(fixture.Options(batchSize: 3));
            var seen = new List<int>();
            var cursor = 0;
            for (var i = 0; i < 100; i++)
            {
                var batch = await pipeline.QueueBatchAsync(PhotoIngestPass.Metadata, cursor);
                Assert.True(batch.Processed <= 3, "the batch exceeded its bound");
                seen.Add(batch.Remaining);
                cursor = int.Parse(batch.NextCursor);
                if (batch.Remaining <= 0) break;
            }

            Assert.Equal(0, seen[seen.Count - 1]);
            // Monotonically decreasing: a queue whose remaining count wobbles is one that is re-queuing
            // rows it already did.
            for (var i = 1; i < seen.Count; i++) Assert.True(seen[i] < seen[i - 1]);
        }

        [Fact]
        public async Task Changed_bytes_re_queue_every_derived_pass()
        {
            fixture.WriteJpeg("C/photo.jpg", width: 100, height: 80, seed: 61);
            await RunAll();

            string firstHash;
            using (var db = fixture.NewDb())
                firstHash = (await db.PhotoAssets.SingleAsync()).Sha256!;

            // Same path, different picture — the row must not keep describing the old bytes.
            System.Threading.Thread.Sleep(10);
            fixture.WriteJpeg("C/photo.jpg", width: 100, height: 80, seed: 62);
            await fixture.Pipeline(fixture.Options(batchSize: 1000)).WalkBatchAsync(null);

            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync();
                Assert.Null(row.MetadataUpdatedUtc);
                Assert.Null(row.HashUpdatedUtc);
                Assert.Null(row.ThumbsUpdatedUtc);
                Assert.Equal(PhotoThumbState.Pending, row.ThumbState);
            }

            await RunAll();
            using (var db = fixture.NewDb())
                Assert.NotEqual(firstHash, (await db.PhotoAssets.SingleAsync()).Sha256);
        }

        [Fact]
        public async Task A_missing_row_is_not_in_any_queue()
        {
            BuildTree();
            await DriveWalk(fixture.Pipeline(fixture.Options(batchSize: 1000)), new List<string>());
            fixture.Delete("Zebra/z1.jpg");
            await fixture.Pipeline(fixture.Options(batchSize: 1000)).WalkBatchAsync(null);

            var result = await fixture.Pipeline(fixture.Options(batchSize: 1000))
                .RunAsync(PhotoIngestPass.Metadata, null, 0);

            Assert.Equal(0, result.Remaining);
            using var db = fixture.NewDb();
            var gone = await db.PhotoAssets.SingleAsync(a => a.Path == "Zebra/z1.jpg");
            Assert.Null(gone.MetadataUpdatedUtc);
        }

        // ── Cursor ordering (§6) ─────────────────────────────────────────────────────────────────

        [Fact]
        public void The_walk_cursor_orders_the_way_a_depth_first_walk_visits()
        {
            var dirs = new List<string> { "A Folder 2", "A Folder", "A Folder/Sub", "Ab", "Zebra" };
            dirs.Sort(PhotoWalkCursor.Comparer);

            // A directory precedes its own contents, and its contents precede every later sibling —
            // which a plain ordinal sort of these same strings does NOT produce.
            Assert.Equal(new[] { "A Folder", "A Folder/Sub", "A Folder 2", "Ab", "Zebra" }, dirs);
            Assert.NotEqual(dirs, dirs.OrderBy(d => d, StringComparer.Ordinal).ToList());
        }

        [Fact]
        public void IsAfter_agrees_with_the_comparer_it_resumes_over()
        {
            var dirs = new List<string> { "", "A Folder", "A Folder/Sub", "A Folder 2", "Ab", "Zebra" };
            dirs.Sort(PhotoWalkCursor.Comparer);

            // For every possible cursor, "still pending" must be exactly the suffix of the sorted list.
            for (var i = 0; i < dirs.Count; i++)
            {
                var pending = dirs.Where(d => PhotoWalkCursor.IsAfter(d, dirs[i])).ToList();
                Assert.Equal(dirs.Skip(i + 1).ToList(), pending);
            }
            Assert.Equal(dirs, dirs.Where(d => PhotoWalkCursor.IsAfter(d, null)).ToList());
        }

        // ── Helper ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Drives all four passes to completion, the way the CLI's <c>--pass all</c> does.</summary>
        private async Task RunAll()
        {
            var pipeline = fixture.Pipeline(fixture.Options(batchSize: 1000));
            foreach (var pass in new[]
            {
                PhotoIngestPass.Walk, PhotoIngestPass.Metadata, PhotoIngestPass.Hash, PhotoIngestPass.Thumb,
            })
                await pipeline.RunAsync(pass, null, 0);
        }
    }
}
