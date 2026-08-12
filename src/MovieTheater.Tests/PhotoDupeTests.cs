using System;
using System.Collections.Generic;
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
    /// Phase 3 duplicate grouping (docs/photos-plan.md §2.6), driven against the generated fixture tree
    /// and a throwaway SQLite file — never the real collection, never the configured database.
    ///
    /// <para>The properties under test are the ones whose failure would be quiet and expensive: a near
    /// duplicate that resolves itself, a pair re-proposed after a human said no, a variant pair offered
    /// for "pick the better copy" (which would collapse the half a browser can render), a master that
    /// moves between runs, and a collapse that reaches the FOLDER view — which would make grouping look
    /// like deletion, the one thing this vertical promises it never does.</para>
    /// </summary>
    public class PhotoDupeTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        // ── The fixture tree ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every shape §2.6 names, with known answers:
        /// three byte-identical copies across folders (the merge-needed case), one picture saved at two
        /// qualities plus a rescaled and a rotated copy (the scanned-print case), a RAW+JPEG pair, a
        /// motion photo, a Live Photo, and two unrelated files that merely share a basename.
        /// </summary>
        private void BuildTree()
        {
            // Exact: the same bytes in three places. The fixture's painter is deterministic, so one seed
            // written three times is three identical files.
            fixture.WriteJpeg("Vacation/beach.jpg", 320, 240, seed: 5, exifDateTimeOriginal: "2011:07:04 10:00:00");
            fixture.WriteJpeg("Vacation/beach copy.jpg", 320, 240, seed: 5, exifDateTimeOriginal: "2011:07:04 10:00:00");
            fixture.WriteJpeg("Phone Backup/beach.jpg", 320, 240, seed: 5, exifDateTimeOriginal: "2011:07:04 10:00:00");

            // Near: one picture, four different files. Same subject, no two of them byte-equal.
            fixture.WriteJpegQuality("Album Scans/print.jpg", quality: 95, seed: 42);
            fixture.WriteJpegQuality("Album Scans/print-again.jpg", quality: 30, seed: 42);
            fixture.WriteJpegScaled("Album Scans/print-small.jpg", scale: 0.5, seed: 42);
            fixture.WriteJpegRotated("Album Scans/print-sideways.jpg", seed: 42);

            // A photograph of something else entirely: the control.
            fixture.WriteJpeg("Vacation/pier.jpg", 320, 240, seed: 77, exifDateTimeOriginal: "2011:07:04 12:00:00");

            // Variant: RAW + JPEG, a motion photo, a Live Photo.
            fixture.WriteJpeg("Camera/IMG_1000.jpg", 320, 240, seed: 51, exifDateTimeOriginal: "2019:05:01 09:00:00");
            fixture.WriteRaw("Camera/IMG_1000.dng", seed: 51);
            fixture.WriteJpeg("Camera/IMG_2000.jpg", 320, 240, seed: 52, exifDateTimeOriginal: "2019:05:01 09:05:00");
            fixture.WriteVideo("Camera/IMG_2000.mp4", seed: 52);
            fixture.WriteOpaque("Camera/IMG_3000.heic", seed: 53);
            fixture.WriteVideo("Camera/IMG_3000.mov", seed: 53);

            // The trap: the same basename in two different folders, years apart. A 2007 camera's
            // IMG_9000.jpg is not the still half of a 2019 phone's IMG_9000.mp4.
            fixture.WriteJpeg("Trip A/IMG_9000.jpg", 320, 240, seed: 61, exifDateTimeOriginal: "2007:03:01 09:00:00");
            fixture.WriteVideo("Trip B/IMG_9000.mp4", seed: 62);
        }

        private async Task IngestAsync()
        {
            var pipeline = fixture.Pipeline(fixture.Options(batchSize: 50));
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Hash, null, 0);
        }

        private PhotoDupePass Pass(PhotoDupeOptions? options = null, List<string>? log = null) =>
            new PhotoDupePass(fixture.NewDb, options ?? new PhotoDupeOptions { BatchSize = 100 },
                line => log?.Add(line));

        /// <summary>The shipped chained order: exact, then variant, then near (§2.6 — near proposes about
        /// what browse actually shows, so the settled lanes go first).</summary>
        private async Task RunAllAsync(PhotoDupeOptions? options = null)
        {
            await Pass(options).RunAsync(PhotoDupePassKind.Exact, null, 0);
            await Pass(options).RunAsync(PhotoDupePassKind.Variant, null, 0);
            await Pass(options).RunAsync(PhotoDupePassKind.Near, null, 0);
        }

        private async Task<int> IdOf(string path)
        {
            using var db = fixture.NewDb();
            return await db.PhotoAssets.Where(a => a.Path == path).Select(a => a.Id).FirstAsync();
        }

        /// <summary>The whole grouping state as comparable text — what "re-running changes nothing" is
        /// measured against, rather than a count that could stay equal while the contents rotated.</summary>
        private async Task<List<string>> SnapshotAsync()
        {
            using var db = fixture.NewDb();
            var rows = await db.PhotoDupeMembers
                .Select(m => new
                {
                    m.PhotoDupeGroup.Kind, m.PhotoDupeGroup.Status, m.IsMaster, m.Similarity,
                    m.PhotoAsset.Path,
                    Members = m.PhotoDupeGroup.Members.Count,
                })
                .ToListAsync();
            return rows
                .Select(r => $"{r.Kind}/{r.Status}/{r.Members} {r.Path} master={r.IsMaster} sim={r.Similarity?.ToString("0.000") ?? "-"}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
        }

        private async Task<PhotoDupeGroup?> GroupContainingAsync(string path, PhotoDupeGroupKind kind)
        {
            using var db = fixture.NewDb();
            var id = await db.PhotoAssets.Where(a => a.Path == path).Select(a => a.Id).FirstAsync();
            var groupId = await db.PhotoDupeMembers
                .Where(m => m.PhotoAssetId == id && m.PhotoDupeGroup.Kind == kind)
                .Select(m => (int?)m.PhotoDupeGroupId)
                .FirstOrDefaultAsync();
            if (groupId == null) return null;
            return await db.PhotoDupeGroups
                .Include(g => g.Members).ThenInclude(m => m.PhotoAsset)
                .FirstAsync(g => g.Id == groupId.Value);
        }

        private static List<string> Paths(PhotoDupeGroup group) =>
            group.Members.Select(m => m.PhotoAsset.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();

        private static string MasterPath(PhotoDupeGroup group) =>
            group.Members.Single(m => m.IsMaster).PhotoAsset.Path;

        // ── Exact (§2.6) ────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Byte_identical_copies_group_across_folders_and_arrive_auto_mastered()
        {
            BuildTree();
            await IngestAsync();
            await Pass().RunAsync(PhotoDupePassKind.Exact, null, 0);

            var group = await GroupContainingAsync("Vacation/beach.jpg", PhotoDupeGroupKind.Exact);
            Assert.NotNull(group);
            Assert.Equal(
                new[] { "Phone Backup/beach.jpg", "Vacation/beach copy.jpg", "Vacation/beach.jpg" },
                Paths(group!));
            // Auto-mastered, and STILL listed for review — §2.6 asks for both.
            Assert.Equal(PhotoDupeGroupStatus.Pending, group!.Status);
            Assert.Single(group.Members.Where(m => m.IsMaster));
            // Equality has no degree.
            Assert.All(group.Members, m => Assert.Null(m.Similarity));

            using var db = fixture.NewDb();
            // The pier is nobody's duplicate.
            Assert.Empty(await db.PhotoDupeMembers
                .Where(m => m.PhotoAsset.Path == "Vacation/pier.jpg" && m.PhotoDupeGroup.Kind == PhotoDupeGroupKind.Exact)
                .ToListAsync());
        }

        [Fact]
        public void The_default_master_is_resolution_then_size_then_exif_then_a_stable_tie_break()
        {
            var small = new PhotoAsset { Id = 1, Width = 100, Height = 100, SizeBytes = 900_000 };
            var big = new PhotoAsset { Id = 2, Width = 400, Height = 400, SizeBytes = 10 };
            // Resolution outranks file size…
            Assert.Equal(2, PhotoDupeMasters.PickMaster(new[] { small, big }).Id);

            var sameSizeSmallFile = new PhotoAsset { Id = 3, Width = 400, Height = 400, SizeBytes = 10 };
            var sameSizeBigFile = new PhotoAsset { Id = 4, Width = 400, Height = 400, SizeBytes = 5000 };
            // …file size outranks EXIF…
            Assert.Equal(4, PhotoDupeMasters.PickMaster(new[] { sameSizeSmallFile, sameSizeBigFile }).Id);

            var noExif = new PhotoAsset { Id = 5, Width = 400, Height = 400, SizeBytes = 5000 };
            var withExif = new PhotoAsset { Id = 6, Width = 400, Height = 400, SizeBytes = 5000, CameraMake = "Test" };
            Assert.Equal(6, PhotoDupeMasters.PickMaster(new[] { noExif, withExif }).Id);

            // …and identical copies fall to the id, which is what makes a re-run produce the same answer
            // instead of swapping the master flag between two rows forever.
            var twinA = new PhotoAsset { Id = 8, Width = 400, Height = 400, SizeBytes = 5000 };
            var twinB = new PhotoAsset { Id = 7, Width = 400, Height = 400, SizeBytes = 5000 };
            Assert.Equal(7, PhotoDupeMasters.PickMaster(new[] { twinA, twinB }).Id);
            Assert.Equal(7, PhotoDupeMasters.PickMaster(new[] { twinB, twinA }).Id);
        }

        [Fact]
        public async Task A_group_whose_content_changed_underneath_it_is_revalidated_not_kept()
        {
            BuildTree();
            await IngestAsync();
            await Pass().RunAsync(PhotoDupePassKind.Exact, null, 0);

            // A re-ingest finds one of the three copies is no longer that photograph.
            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.FirstAsync(a => a.Path == "Phone Backup/beach.jpg");
                row.Sha256 = new string('a', 64);
                await db.SaveChangesAsync();
            }

            await Pass().RunAsync(PhotoDupePassKind.Exact, null, 0);

            var group = await GroupContainingAsync("Vacation/beach.jpg", PhotoDupeGroupKind.Exact);
            Assert.NotNull(group);
            Assert.Equal(new[] { "Vacation/beach copy.jpg", "Vacation/beach.jpg" }, Paths(group!));
            Assert.Single(group!.Members.Where(m => m.IsMaster));
            Assert.Null(await GroupContainingAsync("Phone Backup/beach.jpg", PhotoDupeGroupKind.Exact));
        }

        // ── Near (§2.6) ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_same_print_at_two_qualities_rescaled_and_rotated_is_proposed_as_one_near_group()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            var group = await GroupContainingAsync("Album Scans/print.jpg", PhotoDupeGroupKind.Near);
            Assert.NotNull(group);
            Assert.Equal(
                new[]
                {
                    "Album Scans/print-again.jpg", "Album Scans/print-sideways.jpg",
                    "Album Scans/print-small.jpg", "Album Scans/print.jpg",
                },
                Paths(group!));

            // NEVER auto-resolved (§2.6): a master is proposed, and a human settles it.
            Assert.Equal(PhotoDupeGroupStatus.Pending, group!.Status);
            Assert.Null(group.ResolvedUtc);
            Assert.Null(group.ResolvedByUserId);
            Assert.Single(group.Members.Where(m => m.IsMaster));
            // A similarity score on the members the search actually reached.
            Assert.Contains(group.Members, m => m.Similarity != null && m.Similarity > 0.8);

            // The rotated copy is the load-bearing one: hashes are taken from the AUTO-ORIENTED image,
            // so a photo and its EXIF-rotated twin must not read as two different pictures.
            Assert.Contains("Album Scans/print-sideways.jpg", Paths(group));
            // …and a genuinely different photograph stays out.
            Assert.DoesNotContain("Vacation/pier.jpg", Paths(group));
        }

        [Fact]
        public async Task Exact_copies_are_not_proposed_a_second_time_as_near_duplicates()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            using var db = fixture.NewDb();
            var near = await db.PhotoDupeGroups
                .Where(g => g.Kind == PhotoDupeGroupKind.Near)
                .Include(g => g.Members).ThenInclude(m => m.PhotoAsset)
                .ToListAsync();

            // The three identical beach copies are one Exact group; two of them are collapsed, and the
            // near lane works on what browse shows, so they cannot become a second review item.
            Assert.DoesNotContain(near, g => g.Members.Count(m => m.PhotoAsset.Path.EndsWith("beach.jpg")
                                                                  || m.PhotoAsset.Path.EndsWith("beach copy.jpg")) > 1);
        }

        [Fact]
        public async Task A_pair_a_human_rejected_is_never_proposed_again()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            var group = await GroupContainingAsync("Album Scans/print.jpg", PhotoDupeGroupKind.Near);
            Assert.NotNull(group);
            var groupId = group!.Id;

            using (var db = fixture.NewDb())
            {
                var controller = PhotosControllerHarness.Build(fixture, db);
                var body = PhotosControllerHarness.Body(await controller.RejectDupeGroup(groupId));
                Assert.Equal("Rejected", body.GetProperty("status").GetString());
            }

            // A fresh run — a fresh BK-tree, a fresh rejected-pair set, the whole lane from scratch.
            await Pass().RunAsync(PhotoDupePassKind.Near, null, 0);

            using var after = fixture.NewDb();
            var groups = await after.PhotoDupeGroups.Where(g => g.Kind == PhotoDupeGroupKind.Near).ToListAsync();
            Assert.Single(groups);
            Assert.Equal(PhotoDupeGroupStatus.Rejected, groups[0].Status);
            Assert.Equal(groupId, groups[0].Id);
        }

        // ── Variant (§2.6) ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_raw_and_its_jpeg_pair_as_a_settled_variant_mastered_by_the_jpeg()
        {
            BuildTree();
            await IngestAsync();
            await Pass().RunAsync(PhotoDupePassKind.Variant, null, 0);

            var group = await GroupContainingAsync("Camera/IMG_1000.jpg", PhotoDupeGroupKind.Variant);
            Assert.NotNull(group);
            Assert.Equal(new[] { "Camera/IMG_1000.dng", "Camera/IMG_1000.jpg" }, Paths(group!));
            // Settled by the pass: no human is asked which of a RAW and its JPEG is the better copy.
            Assert.Equal(PhotoDupeGroupStatus.Resolved, group!.Status);
            Assert.NotNull(group.ResolvedUtc);
            // Machine-settled records WHEN and deliberately no WHO.
            Assert.Null(group.ResolvedByUserId);
            // The DISPLAY half is master, even though the RAW is the bigger file.
            Assert.Equal("Camera/IMG_1000.jpg", MasterPath(group));
        }

        [Fact]
        public async Task A_motion_photo_and_a_live_photo_are_each_one_item_with_the_still_as_master()
        {
            BuildTree();
            await IngestAsync();
            await Pass().RunAsync(PhotoDupePassKind.Variant, null, 0);

            var motion = await GroupContainingAsync("Camera/IMG_2000.jpg", PhotoDupeGroupKind.Variant);
            Assert.NotNull(motion);
            Assert.Equal(new[] { "Camera/IMG_2000.jpg", "Camera/IMG_2000.mp4" }, Paths(motion!));
            Assert.Equal("Camera/IMG_2000.jpg", MasterPath(motion!));

            var live = await GroupContainingAsync("Camera/IMG_3000.heic", PhotoDupeGroupKind.Variant);
            Assert.NotNull(live);
            Assert.Equal(new[] { "Camera/IMG_3000.heic", "Camera/IMG_3000.mov" }, Paths(live!));
            // The .heic is not browser-renderable, but it is still the display half beside a video.
            Assert.Equal("Camera/IMG_3000.heic", MasterPath(live!));
        }

        [Fact]
        public async Task The_same_basename_in_two_folders_is_never_paired()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            Assert.Null(await GroupContainingAsync("Trip A/IMG_9000.jpg", PhotoDupeGroupKind.Variant));
            Assert.Null(await GroupContainingAsync("Trip B/IMG_9000.mp4", PhotoDupeGroupKind.Variant));
        }

        [Fact]
        public async Task A_variant_pair_is_never_offered_for_review()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            var variant = await GroupContainingAsync("Camera/IMG_1000.jpg", PhotoDupeGroupKind.Variant);
            Assert.NotNull(variant);

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            var listed = PhotosControllerHarness.Body(await controller.DupeGroups(status: "all"));
            var ids = listed.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("id").GetInt32()).ToList();
            Assert.DoesNotContain(variant!.Id, ids);

            // And it cannot be settled by hand either — "which of a RAW and its JPEG is better" has no
            // answer, and answering it wrongly would collapse the half a browser can show.
            Assert.IsType<BadRequestObjectResult>(
                await controller.ResolveDupeGroup(variant.Id, new PhotoDupeResolveRequest { MasterAssetId = 1 }));
            Assert.IsType<BadRequestObjectResult>(await controller.RejectDupeGroup(variant.Id));
        }

        // ── Determinism + idempotency (§5 Phase 3 acceptance) ───────────────────────────────────

        [Fact]
        public async Task Running_the_passes_again_creates_nothing_and_changes_nothing()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();
            var first = await SnapshotAsync();

            await RunAllAsync();
            var second = await SnapshotAsync();

            Assert.Equal(first, second);

            using var db = fixture.NewDb();
            // Every group still has exactly one master; the filtered unique index says so too, but a
            // second master would be the failure mode nobody notices until the timeline shows both.
            var groups = await db.PhotoDupeGroups.Include(g => g.Members).ToListAsync();
            Assert.All(groups, g => Assert.Single(g.Members.Where(m => m.IsMaster)));
            Assert.All(groups, g => Assert.True(g.Members.Count >= 2, "a group of one is not a group"));
        }

        [Fact]
        public async Task The_passes_are_chunked_resumable_and_terminate()
        {
            BuildTree();
            await IngestAsync();

            foreach (var kind in new[] { PhotoDupePassKind.Exact, PhotoDupePassKind.Variant, PhotoDupePassKind.Near })
            {
                // The driver loop lives in the caller (the standing bulk-job rule); each call does one
                // bounded batch and hands back the cursor it stopped at.
                var options = new PhotoDupeOptions { BatchSize = 2, MaxPairsPerBatch = 3 };
                var cursor = (string?)null;
                var batches = 0;
                var drained = false;
                for (var i = 0; i < 100; i++)
                {
                    var result = await Pass(options).RunAsync(kind, cursor, 1);
                    batches++;
                    cursor = result.NextCursor;
                    if (result.Remaining <= 0) { drained = true; break; }
                    Assert.True(result.Processed > 0, $"{kind}: a batch made no progress while work remained");
                }
                Assert.True(drained, $"{kind}: the queue never reported itself drained");
                // The row-paged lanes must actually take several batches at this size; the exact lane
                // pages by SHA KEY and this tree has one duplicated key, so a single batch is correct
                // there and asserting otherwise would be asserting the fixture rather than the code.
                if (kind != PhotoDupePassKind.Exact)
                    Assert.True(batches > 1, $"{kind}: batch size 2 over this tree should have taken several batches");
            }

            // Chunked to the same answer a single drain gives.
            var chunked = await SnapshotAsync();
            await RunAllAsync();
            Assert.Equal(chunked, await SnapshotAsync());
        }

        // ── Collapse (§2.6 + Task 3) ────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_timeline_collapses_non_masters_and_the_folder_view_shows_every_copy()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            var group = await GroupContainingAsync("Vacation/beach.jpg", PhotoDupeGroupKind.Exact);
            var master = group!.Members.Single(m => m.IsMaster).PhotoAssetId;
            var copies = group.Members.Where(m => !m.IsMaster).Select(m => m.PhotoAssetId).ToList();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            var timeline = PhotosControllerHarness.ItemIds(PhotosControllerHarness.Body(await controller.Timeline()));
            Assert.Contains(master, timeline);
            foreach (var copy in copies) Assert.DoesNotContain(copy, timeline);

            // The motion photo is ONE timeline item (§5 Phase 3 acceptance).
            var video = await IdOf("Camera/IMG_2000.mp4");
            var still = await IdOf("Camera/IMG_2000.jpg");
            Assert.Contains(still, timeline);
            Assert.DoesNotContain(video, timeline);

            // …and the folder view still shows everything, badged rather than dimmed: nothing was
            // deleted, moved or renamed, and this surface must never suggest otherwise (§6). The group
            // spans folders — that IS the merge-needed case — so each folder shows its own copies.
            var vacation = PhotosControllerHarness.Body(await controller.Folders("Vacation"));
            var vacationIds = PhotosControllerHarness.ItemIds(vacation);
            var vacationCopy = await IdOf("Vacation/beach copy.jpg");
            Assert.Contains(await IdOf("Vacation/beach.jpg"), vacationIds);
            Assert.Contains(vacationCopy, vacationIds);

            var backup = PhotosControllerHarness.Body(await controller.Folders("Phone Backup"));
            Assert.Contains(await IdOf("Phone Backup/beach.jpg"), PhotosControllerHarness.ItemIds(backup));

            // A copy the timeline collapsed says so on its card, rather than simply being absent.
            var badged = vacation.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("id").GetInt32() == vacationCopy);
            var badge = badged.GetProperty("group");
            Assert.Equal("Exact", badge.GetProperty("kind").GetString());
            Assert.Equal(3, badge.GetProperty("size").GetInt32());
            Assert.Equal(master == vacationCopy, badge.GetProperty("isMaster").GetBoolean());
            Assert.Equal(master != vacationCopy, badge.GetProperty("collapsed").GetBoolean());
        }

        [Fact]
        public async Task A_pending_near_group_collapses_nothing_until_a_human_resolves_it()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            var group = await GroupContainingAsync("Album Scans/print.jpg", PhotoDupeGroupKind.Near);
            var members = group!.Members.Select(m => m.PhotoAssetId).ToList();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            // Nobody has agreed these are the same picture yet, so every one of them is still browsable.
            // On the UNDATED shelf: these are scans, and §2.7 refuses to invent a wall clock for them —
            // which is precisely why the collapse filter has to apply to both timeline modes.
            var before = PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await controller.Timeline(undated: true)));
            foreach (var id in members) Assert.Contains(id, before);

            var pick = members[1];
            var resolved = PhotosControllerHarness.Body(
                await controller.ResolveDupeGroup(group.Id, new PhotoDupeResolveRequest { MasterAssetId = pick }));
            Assert.Equal("Resolved", resolved.GetProperty("status").GetString());
            Assert.Equal(members.Count - 1, PhotosControllerHarness.Int(resolved, "collapsed"));

            // Immediately, on the next page load.
            var after = PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await controller.Timeline(undated: true)));
            Assert.Contains(pick, after);
            foreach (var id in members.Where(m => m != pick)) Assert.DoesNotContain(id, after);
        }

        [Fact]
        public async Task The_master_helper_answers_identity_for_an_ungrouped_photo_and_the_master_for_a_copy()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            var group = await GroupContainingAsync("Vacation/beach.jpg", PhotoDupeGroupKind.Exact);
            var master = group!.Members.Single(m => m.IsMaster).PhotoAssetId;
            var copy = group.Members.First(m => !m.IsMaster).PhotoAssetId;
            var loner = await IdOf("Vacation/pier.jpg");

            using var db = fixture.NewDb();
            Assert.Equal(loner, await PhotoDupeMasters.MasterForAsync(db, loner));
            Assert.Equal(master, await PhotoDupeMasters.MasterForAsync(db, copy));
            Assert.Equal(master, await PhotoDupeMasters.MasterForAsync(db, master));

            var map = await PhotoDupeMasters.MasterMapAsync(db, new[] { loner, copy, master });
            Assert.Equal(loner, map[loner]);
            Assert.Equal(master, map[copy]);
            Assert.Equal(master, map[master]);
        }

        [Fact]
        public async Task Adding_a_duplicate_to_an_album_adds_the_master_and_reports_the_redirect()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            var group = await GroupContainingAsync("Vacation/beach.jpg", PhotoDupeGroupKind.Exact);
            var master = group!.Members.Single(m => m.IsMaster).PhotoAssetId;
            var copies = group.Members.Where(m => !m.IsMaster).Select(m => m.PhotoAssetId).ToList();
            var pier = await IdOf("Vacation/pier.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            // A selection of three cards that is really two photographs.
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest
            {
                Title = "Selection",
                AssetIds = new List<int> { copies[0], copies[1], pier },
            }));
            Assert.Equal(2, PhotosControllerHarness.Int(created, "added"));
            Assert.Equal(2, PhotosControllerHarness.Int(created, "redirectedToMasters"));

            var albumId = created.GetProperty("album").GetProperty("id").GetInt32();
            var entries = await db.PhotoAlbumEntries.Where(e => e.PhotoAlbumId == albumId)
                .Select(e => e.PhotoAssetId).ToListAsync();
            Assert.Contains(master, entries);
            Assert.Contains(pier, entries);
            foreach (var copy in copies) Assert.DoesNotContain(copy, entries);
        }

        // ── The review surface (§2.6) ───────────────────────────────────────────────────────────

        [Fact]
        public async Task The_review_list_carries_what_a_human_decides_on()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var body = PhotosControllerHarness.Body(await controller.DupeGroups());

            var groups = body.GetProperty("groups").EnumerateArray().ToList();
            Assert.NotEmpty(groups);
            foreach (var group in groups)
            {
                Assert.NotEqual("Variant", group.GetProperty("kind").GetString());
                foreach (var member in group.GetProperty("members").EnumerateArray())
                {
                    // Resolution, size, format and WHICH FOLDER — the merge-needed folders' whole story.
                    Assert.True(member.TryGetProperty("folder", out _));
                    Assert.True(member.TryGetProperty("format", out _));
                    Assert.True(member.GetProperty("sizeBytes").GetInt64() > 0);
                    Assert.True(member.TryGetProperty("width", out _));
                }
            }

            // Member-visible: settling a duplicate is curation, the same policy that lets any family
            // member accept a hide batch. Nothing here is admin-only.
            Assert.IsType<JsonResult>(await controller.DupeGroups());
        }

        [Fact]
        public async Task A_master_pick_must_be_one_of_the_group_copies()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            var group = await GroupContainingAsync("Album Scans/print.jpg", PhotoDupeGroupKind.Near);
            var outsider = await IdOf("Vacation/pier.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            Assert.IsType<BadRequestObjectResult>(
                await controller.ResolveDupeGroup(group!.Id, new PhotoDupeResolveRequest { MasterAssetId = outsider }));
        }

        [Fact]
        public async Task The_lightbox_can_reach_the_other_copies()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            var copy = await IdOf("Phone Backup/beach.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var body = PhotosControllerHarness.Body(await controller.Asset(copy));

            var group = body.GetProperty("group");
            Assert.Equal("Exact", group.GetProperty("kind").GetString());
            Assert.Equal(3, group.GetProperty("members").GetArrayLength());
        }

        // ── Export / import round trip, including the Phase 3 state (§2.11) ─────────────────────

        [Fact]
        public async Task An_export_carries_the_dupe_groups_and_the_curation_batches_into_a_rebuilt_database()
        {
            BuildTree();
            await IngestAsync();
            await RunAllAsync();

            // A resolved near group and a decided hide proposal: two pieces of irreplaceable human
            // labor, one of which could not travel at all before Phase 3 made it rows.
            var near = await GroupContainingAsync("Album Scans/print.jpg", PhotoDupeGroupKind.Near);
            var pick = near!.Members.OrderBy(m => m.PhotoAssetId).Last().PhotoAssetId;
            using (var db = fixture.NewDb())
            {
                var controller = PhotosControllerHarness.Build(fixture, db);
                await controller.ResolveDupeGroup(near.Id, new PhotoDupeResolveRequest { MasterAssetId = pick });
            }
            await new PhotoSuggestHidePass(fixture.NewDb, new PhotoHideSuggestions.Options(), 100, _ => { })
                .RunAsync("hide-round-trip", null, 0);

            var dir = fixture.ExportDir("phase3");
            var manifest = await new PhotoCurationExporter(fixture.NewDb, _ => { }, pageSize: 4).RunAsync(dir, 0);
            Assert.True(manifest.Complete);
            Assert.Equal(2, manifest.Version);
            Assert.True(manifest.Counts[PhotoCurationExportFormat.DupeGroupsFile] > 0);
            Assert.True(manifest.Counts[PhotoCurationExportFormat.CurationBatchesFile] > 0);

            // The rebuilt database: the same files walked again, nothing else carried over.
            var rebuilt = fixture.SecondaryDbFactory("rebuilt-phase3");
            var pipeline = new PhotoIngestPipeline(rebuilt, fixture.Options(batchSize: 50), _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Hash, null, 0);

            await new PhotoCurationImporter(rebuilt, dir, apply: true, _ => { }, 100).RunAsync(null, 0);

            using var after = rebuilt();
            var restored = await after.PhotoDupeGroups
                .Include(g => g.Members).ThenInclude(m => m.PhotoAsset)
                .ToListAsync();
            var restoredNear = restored.Single(g => g.Kind == PhotoDupeGroupKind.Near);
            Assert.Equal(PhotoDupeGroupStatus.Resolved, restoredNear.Status);
            Assert.Single(restoredNear.Members.Where(m => m.IsMaster));
            Assert.Equal(4, restoredNear.Members.Count);
            Assert.Contains(restored, g => g.Kind == PhotoDupeGroupKind.Variant);
            Assert.Contains(restored, g => g.Kind == PhotoDupeGroupKind.Exact);

            var batch = await after.PhotoCurationBatches
                .Include(b => b.Items)
                .FirstAsync(b => b.Kind == PhotoCurationBatchKind.HideProposal && b.BatchId == "hide-round-trip");
            Assert.True(batch.Complete);
            Assert.NotEmpty(batch.Items);
            Assert.All(batch.Items, i => Assert.False(string.IsNullOrEmpty(i.Rule)));

            // And a second import is a no-op rather than a second copy of everything.
            var again = await new PhotoCurationImporter(rebuilt, dir, apply: true, _ => { }, 100).RunAsync(null, 0);
            Assert.Equal(0, again.Sections[PhotoCurationExportFormat.DupeGroupsFile].Created);
            Assert.Equal(0, again.Sections[PhotoCurationExportFormat.CurationBatchesFile].Created);
        }

        // ── The hash index (§2.6) ───────────────────────────────────────────────────────────────

        [Fact]
        public void The_bucketed_bk_tree_finds_every_neighbour_within_the_threshold()
        {
            // The pigeonhole claim the buckets rest on: with threshold + 1 blocks, two hashes within the
            // threshold must agree exactly on one block, so bucketing loses NOTHING. A brute-force
            // comparison is the only honest way to assert that.
            var random = new Random(1234);
            var hashes = new List<long>();
            for (var i = 0; i < 400; i++) hashes.Add(unchecked((long)(ulong)random.NextInt64()));
            // Plus deliberate near neighbours of the first hash, at 1..8 bits.
            for (var bits = 1; bits <= 8; bits++)
            {
                var mutated = hashes[0];
                for (var b = 0; b < bits; b++) mutated ^= 1L << (b * 7 % 64);
                hashes.Add(mutated);
            }

            const int threshold = 8;
            var index = new PhotoHashIndex(threshold);
            for (var i = 0; i < hashes.Count; i++) index.Add(i, hashes[i]);

            for (var i = 0; i < hashes.Count; i++)
            {
                var expected = Enumerable.Range(0, hashes.Count)
                    .Where(j => PhotoHashes.HammingDistance(hashes[i], hashes[j]) <= threshold)
                    .OrderBy(j => j)
                    .ToList();
                var found = index.Query(hashes[i]).Select(n => n.AssetId).OrderBy(j => j).ToList();
                Assert.Equal(expected, found);
            }
        }
    }
}
