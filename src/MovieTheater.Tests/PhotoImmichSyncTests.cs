using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Controllers;
using MovieTheater.Db;
using MovieTheater.Photos;
using MovieTheater.Services;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Phase 4's Immich sidecar lane (docs/photos-plan.md §2.4), driven against a STAND-IN Immich
    /// (<see cref="FakeImmich"/>) and a throwaway SQLite file. A live instance is never contacted: the
    /// sidecar is LAN-only by design, and a test that needed a container up would be a test nobody could
    /// run.
    ///
    /// <para>What these pin down is the posture, not the plumbing: our database owns all truth, the
    /// sidecar only ever proposes, and every feature keeps working with it gone. So the load-bearing
    /// assertions are the refusals — a geocode label that does NOT overwrite one somebody typed, a
    /// suggestion that does NOT clobber a human's tag, a re-sync that re-proposes NOTHING a human has
    /// answered, a duplicate candidate that respects a rejected pair, and a version outside the tested
    /// range that refuses the run rather than mis-parsing an API it has never seen.</para>
    /// </summary>
    public class PhotoImmichSyncTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        /// <summary>The sidecar's own view of the collection: its container mount, which shares nothing
        /// with our root but the tail. That mismatch is the entire reason mapping is a suffix match.</summary>
        private const string ContainerRoot = "/usr/src/app/external";

        private void BuildTree()
        {
            fixture.WriteJpeg("Vacation/one.jpg", 320, 240, seed: 5, exifDateTimeOriginal: "2011:07:04 10:00:00");
            fixture.WriteJpeg("Vacation/two.jpg", 320, 240, seed: 6, exifDateTimeOriginal: "2011:07:04 11:00:00");
            fixture.WriteJpeg("Album Scans/print.jpg", 320, 240, seed: 7);
            // Two files with the SAME name in different folders: what makes a one-segment match wrong,
            // and what the two-segment key exists to keep apart.
            fixture.WriteJpeg("Trip A/IMG_0001.jpg", 320, 240, seed: 21, exifDateTimeOriginal: "2015:01:01 09:00:00");
            fixture.WriteJpeg("Trip B/IMG_0001.jpg", 320, 240, seed: 22, exifDateTimeOriginal: "2016:01:01 09:00:00");
        }

        private async Task IngestAsync()
        {
            var pipeline = fixture.Pipeline(fixture.Options(batchSize: 50));
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Hash, null, 0);
        }

        private async Task<int> IdOf(string path)
        {
            using var db = fixture.NewDb();
            return await db.PhotoAssets.Where(a => a.Path == path).Select(a => a.Id).FirstAsync();
        }

        private PhotoImmichSync Sync(FakeImmich immich, PhotoImmichSyncOptions? options = null,
            List<string>? log = null) =>
            new PhotoImmichSync(fixture.NewDb, immich,
                options ?? new PhotoImmichSyncOptions { BatchSize = 50, ThumbCacheDir = fixture.ThumbCache },
                line => log?.Add(line));

        /// <summary>The shipped chained order: assets, people, faces, duplicates. Each lane depends on
        /// the one before it — nothing can be tagged before its asset is mapped.</summary>
        private async Task RunAllAsync(FakeImmich immich, PhotoImmichSyncOptions? options = null)
        {
            foreach (var pass in new[]
                     {
                         PhotoImmichPass.Assets, PhotoImmichPass.People,
                         PhotoImmichPass.Faces, PhotoImmichPass.Duplicates,
                     })
                await Sync(immich, options).RunAsync(pass, null, 0);
        }

        // ── Path mapping (§2.4) ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Assets_map_by_root_relative_path_suffix_not_by_absolute_path()
        {
            BuildTree();
            await IngestAsync();

            var immich = new FakeImmich();
            immich.AddAsset("im-1", $"{ContainerRoot}/Vacation/one.jpg");
            immich.AddAsset("im-2", $"{ContainerRoot}/Vacation/two.jpg");

            var result = await Sync(immich).RunAsync(PhotoImmichPass.Assets, null, 0);
            Assert.Equal(2, result.Counts["mapped"]);

            using var db = fixture.NewDb();
            Assert.Equal("im-1", (await db.PhotoAssets.SingleAsync(a => a.Path == "Vacation/one.jpg")).ImmichAssetId);
            Assert.Equal("im-2", (await db.PhotoAssets.SingleAsync(a => a.Path == "Vacation/two.jpg")).ImmichAssetId);
        }

        /// <summary>The two-segment key. A phone-backup tree is full of <c>IMG_0001.jpg</c>, and a file
        /// name alone would map the wrong photograph — attaching a stranger's face suggestions to a
        /// family picture.</summary>
        [Fact]
        public async Task Same_named_files_in_different_folders_map_separately()
        {
            BuildTree();
            await IngestAsync();

            var immich = new FakeImmich();
            immich.AddAsset("im-a", $"{ContainerRoot}/Trip A/IMG_0001.jpg");
            immich.AddAsset("im-b", $"{ContainerRoot}/Trip B/IMG_0001.jpg");

            await Sync(immich).RunAsync(PhotoImmichPass.Assets, null, 0);

            using var db = fixture.NewDb();
            Assert.Equal("im-a", (await db.PhotoAssets.SingleAsync(a => a.Path == "Trip A/IMG_0001.jpg")).ImmichAssetId);
            Assert.Equal("im-b", (await db.PhotoAssets.SingleAsync(a => a.Path == "Trip B/IMG_0001.jpg")).ImmichAssetId);
        }

        /// <summary>An asset the sidecar has and we do not is counted, not invented. Nothing in this
        /// vertical creates a row from something Immich said.</summary>
        [Fact]
        public async Task An_unknown_path_is_reported_rather_than_creating_a_row()
        {
            BuildTree();
            await IngestAsync();

            var immich = new FakeImmich();
            immich.AddAsset("im-x", $"{ContainerRoot}/Somewhere Else/nothing.jpg");

            var before = await CountAssetsAsync();
            var result = await Sync(immich).RunAsync(PhotoImmichPass.Assets, null, 0);
            Assert.Equal(1, result.Counts["unmapped"]);
            Assert.Equal(before, await CountAssetsAsync());
        }

        private async Task<int> CountAssetsAsync()
        {
            using var db = fixture.NewDb();
            return await db.PhotoAssets.CountAsync();
        }

        // ── Geocode: only where null (§2.4) ─────────────────────────────────────────────────────

        /// <summary>
        /// The label fills ONLY where it is null. A place a family member typed — or one a Takeout
        /// sidecar supplied — outranks a machine's guess from GPS, and this pass is the machine.
        /// </summary>
        [Fact]
        public async Task Reverse_geocode_fills_empty_labels_and_leaves_existing_ones_alone()
        {
            BuildTree();
            await IngestAsync();

            using (var seed = fixture.NewDb())
            {
                var typed = await seed.PhotoAssets.SingleAsync(a => a.Path == "Vacation/two.jpg");
                typed.LocationLabel = "Somewhere a person typed";
                typed.LocationSource = PhotoLocationSource.Manual;
                await seed.SaveChangesAsync();
            }

            var immich = new FakeImmich();
            immich.AddAsset("im-1", $"{ContainerRoot}/Vacation/one.jpg", city: "Placeville", state: "Somestate");
            immich.AddAsset("im-2", $"{ContainerRoot}/Vacation/two.jpg", city: "Elsewhere", state: "Otherstate");

            var result = await Sync(immich).RunAsync(PhotoImmichPass.Assets, null, 0);
            Assert.Equal(1, result.Counts["geocode-filled"]);
            Assert.Equal(1, result.Counts["geocode-kept-existing"]);

            using var db = fixture.NewDb();
            var filled = await db.PhotoAssets.SingleAsync(a => a.Path == "Vacation/one.jpg");
            Assert.Equal("Placeville, Somestate", filled.LocationLabel);
            // Source-stamped, so dropping Immich leaves it obvious which labels are re-derivable.
            Assert.Equal(PhotoLocationSource.ImmichGeocode, filled.LocationSource);

            var kept = await db.PhotoAssets.SingleAsync(a => a.Path == "Vacation/two.jpg");
            Assert.Equal("Somewhere a person typed", kept.LocationLabel);
            Assert.Equal(PhotoLocationSource.Manual, kept.LocationSource);
        }

        // ── Clusters → people, and naming one (§2.8) ────────────────────────────────────────────

        /// <summary>
        /// The §5 Phase 4 acceptance criterion, end to end: a cluster arrives UNNAMED, a member names it,
        /// and its suggestions are suddenly suggestions about a person — across the whole library, from
        /// one act.
        /// </summary>
        [Fact]
        public async Task An_unnamed_cluster_is_imported_named_by_a_member_and_fans_suggestions_out()
        {
            BuildTree();
            await IngestAsync();

            var immich = new FakeImmich();
            immich.AddAsset("im-1", $"{ContainerRoot}/Vacation/one.jpg");
            immich.AddAsset("im-2", $"{ContainerRoot}/Vacation/two.jpg");
            immich.AddCluster("cluster-1");
            immich.AddFace("im-1", "cluster-1");
            immich.AddFace("im-2", "cluster-1");

            await RunAllAsync(immich);

            using (var db = fixture.NewDb())
            {
                // Imported with NO name: Immich's own name for a cluster is never taken. Names live in
                // our rows and nowhere else (§6), and naming is a human's act.
                var cluster = await db.FamilyPeople.SingleAsync();
                Assert.Equal("", cluster.Name);
                Assert.Equal("cluster-1", cluster.ImmichPersonId);

                var suggestions = await db.PhotoPersonTags.ToListAsync();
                Assert.Equal(2, suggestions.Count);
                Assert.All(suggestions, s => Assert.Equal(PhotoTagSource.Suggested, s.Source));
                // Boxes came across as fractions, so the queue can draw them over OUR derivatives with
                // Immich gone.
                Assert.All(suggestions, s => Assert.NotNull(s.BoxX));
            }

            using var db2 = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db2);
            var clusterId = await db2.FamilyPeople.Select(p => p.Id).SingleAsync();
            await controller.UpdatePerson(clusterId, new PhotoPersonRequest { Name = "Subject A" });

            var queue = PhotosControllerHarness.Body(await controller.TagQueue("suggested"));
            var names = queue.GetProperty("items").EnumerateArray()
                .SelectMany(i => i.GetProperty("tags").EnumerateArray())
                .Select(t => t.GetProperty("name").GetString())
                .ToList();
            Assert.Equal(2, names.Count);
            Assert.All(names, n => Assert.Equal("Subject A", n));
        }

        // ── The re-sync contract: nothing a human answered is ever re-proposed ───────────────────

        /// <summary>
        /// The property the whole lane is judged on. A member confirms one suggestion and refuses
        /// another; the identical sync then runs again and must propose NEITHER — not the confirmed one
        /// (which would duplicate or downgrade a human's tag) and not the refused one (which would make
        /// the queue re-ask an answered question, which is how a review queue becomes something nobody
        /// opens).
        /// </summary>
        [Fact]
        public async Task A_re_sync_re_proposes_nothing_a_human_has_confirmed_or_refused()
        {
            BuildTree();
            await IngestAsync();

            var immich = new FakeImmich();
            immich.AddAsset("im-1", $"{ContainerRoot}/Vacation/one.jpg");
            immich.AddAsset("im-2", $"{ContainerRoot}/Vacation/two.jpg");
            immich.AddCluster("cluster-1");
            immich.AddFace("im-1", "cluster-1");
            immich.AddFace("im-2", "cluster-1");

            await RunAllAsync(immich);

            int confirmedAsset, refusedAsset;
            using (var db = fixture.NewDb())
            {
                var controller = PhotosControllerHarness.Build(fixture, db);
                var tags = await db.PhotoPersonTags.OrderBy(t => t.PhotoAssetId).ToListAsync();
                confirmedAsset = tags[0].PhotoAssetId;
                refusedAsset = tags[1].PhotoAssetId;
                await controller.ConfirmTag(tags[0].Id);
                await controller.RejectTag(tags[1].Id);
            }

            var second = await Sync(immich).RunAsync(PhotoImmichPass.Faces, null, 0);
            Assert.Equal(1, second.Counts["suggestion-skipped-human-tag"]);
            Assert.Equal(1, second.Counts["suggestion-skipped-rejected"]);
            Assert.False(second.Counts.ContainsKey("suggestions-added"));

            using var check = fixture.NewDb();
            var rows = await check.PhotoPersonTags.ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(PhotoTagSource.Confirmed, rows.Single(r => r.PhotoAssetId == confirmedAsset).Source);
            Assert.Equal(PhotoTagSource.Rejected, rows.Single(r => r.PhotoAssetId == refusedAsset).Source);
        }

        /// <summary>A suggestion lands on the group MASTER, exactly as a hand tag does (§2.6) — the sync
        /// goes through the same helper, so there is no second path that could forget.</summary>
        [Fact]
        public async Task Suggestions_land_on_the_group_master()
        {
            fixture.WriteJpeg("Phone Backup/dupe.jpg", 320, 240, seed: 9, exifDateTimeOriginal: "2012:01:02 08:00:00");
            fixture.WriteJpeg("Vacation/dupe.jpg", 320, 240, seed: 9, exifDateTimeOriginal: "2012:01:02 08:00:00");
            await IngestAsync();
            await new PhotoDupePass(fixture.NewDb, new PhotoDupeOptions { BatchSize = 100 }, _ => { })
                .RunAsync(PhotoDupePassKind.Exact, null, 0);

            var a = await IdOf("Phone Backup/dupe.jpg");
            int master;
            using (var db = fixture.NewDb()) master = await PhotoDupeMasters.MasterForAsync(db, a);
            var nonMasterPath = master == a ? "Vacation/dupe.jpg" : "Phone Backup/dupe.jpg";

            var immich = new FakeImmich();
            immich.AddAsset("im-n", $"{ContainerRoot}/{nonMasterPath}");
            immich.AddCluster("cluster-1");
            immich.AddFace("im-n", "cluster-1");

            await RunAllAsync(immich);

            using var check = fixture.NewDb();
            var tag = await check.PhotoPersonTags.SingleAsync();
            Assert.Equal(master, tag.PhotoAssetId);
        }

        // ── Duplicate candidates → the Near lane (§2.6) ─────────────────────────────────────────

        [Fact]
        public async Task Duplicate_candidates_arrive_as_pending_near_groups()
        {
            BuildTree();
            await IngestAsync();

            var immich = new FakeImmich();
            immich.AddAsset("im-1", $"{ContainerRoot}/Trip A/IMG_0001.jpg");
            immich.AddAsset("im-2", $"{ContainerRoot}/Trip B/IMG_0001.jpg");
            immich.AddDuplicate("dupe-1", "im-1", "im-2");

            await RunAllAsync(immich);

            using var db = fixture.NewDb();
            var group = await db.PhotoDupeGroups.Include(g => g.Members).SingleAsync();
            // Never auto-resolved: nobody has yet agreed these are the same picture (§2.6).
            Assert.Equal(PhotoDupeGroupKind.Near, group.Kind);
            Assert.Equal(PhotoDupeGroupStatus.Pending, group.Status);
            Assert.Equal(2, group.Members.Count);
            Assert.Single(group.Members.Where(m => m.IsMaster));
        }

        /// <summary>
        /// "These are not the same photo" binds the PAIR and is kind-agnostic (§2.6), so a human's
        /// refusal blocks the sidecar's lane exactly as it blocks the perceptual-hash one. Without that,
        /// rejecting a group would un-collapse its copies and the very next sync would propose them
        /// again — the loop a review queue must never have.
        /// </summary>
        [Fact]
        public async Task A_rejected_pair_is_never_proposed_again_by_the_sidecar_lane()
        {
            BuildTree();
            await IngestAsync();

            var immich = new FakeImmich();
            immich.AddAsset("im-1", $"{ContainerRoot}/Trip A/IMG_0001.jpg");
            immich.AddAsset("im-2", $"{ContainerRoot}/Trip B/IMG_0001.jpg");
            immich.AddDuplicate("dupe-1", "im-1", "im-2");

            await RunAllAsync(immich);

            using (var db = fixture.NewDb())
            {
                var controller = PhotosControllerHarness.Build(fixture, db);
                var groupId = await db.PhotoDupeGroups.Select(g => g.Id).SingleAsync();
                await controller.RejectDupeGroup(groupId);
            }

            var again = await Sync(immich).RunAsync(PhotoImmichPass.Duplicates, null, 0);
            Assert.True(again.Counts.ContainsKey("rejected-pair-skipped"));

            using var check = fixture.NewDb();
            var groups = await check.PhotoDupeGroups.ToListAsync();
            // The tombstone, and nothing else. No second Pending group about the same pair.
            Assert.Single(groups);
            Assert.Equal(PhotoDupeGroupStatus.Rejected, groups[0].Status);
        }

        // ── Chunking, resumption and idempotence ────────────────────────────────────────────────

        /// <summary>The standing bulk-job contract: bounded work per call, a cursor that resumes, and a
        /// re-run of a drained pass that changes nothing.</summary>
        [Fact]
        public async Task The_face_lane_resumes_from_its_cursor_and_re_running_changes_nothing()
        {
            BuildTree();
            await IngestAsync();

            var immich = new FakeImmich();
            immich.AddAsset("im-1", $"{ContainerRoot}/Vacation/one.jpg");
            immich.AddAsset("im-2", $"{ContainerRoot}/Vacation/two.jpg");
            immich.AddAsset("im-3", $"{ContainerRoot}/Album Scans/print.jpg");
            immich.AddCluster("cluster-1");
            immich.AddFace("im-1", "cluster-1");
            immich.AddFace("im-2", "cluster-1");
            immich.AddFace("im-3", "cluster-1");

            await Sync(immich).RunAsync(PhotoImmichPass.Assets, null, 0);
            await Sync(immich).RunAsync(PhotoImmichPass.People, null, 0);

            var options = new PhotoImmichSyncOptions { BatchSize = 1, ThumbCacheDir = fixture.ThumbCache };
            // One batch, then stop — the shape a kill mid-run leaves behind.
            var first = await Sync(immich, options).RunAsync(PhotoImmichPass.Faces, null, 1);
            Assert.Equal(1, first.Processed);
            Assert.True(first.Remaining > 0);

            using (var partial = fixture.NewDb())
                Assert.Equal(1, await partial.PhotoPersonTags.CountAsync());

            // Resumed from the cursor the killed run handed back.
            var rest = await Sync(immich, options).RunAsync(PhotoImmichPass.Faces, first.NextCursor, 0);
            Assert.Equal(0, rest.Remaining);

            using (var drained = fixture.NewDb())
                Assert.Equal(3, await drained.PhotoPersonTags.CountAsync());

            var idempotent = await Sync(immich, options).RunAsync(PhotoImmichPass.Faces, null, 0);
            Assert.False(idempotent.Counts.ContainsKey("suggestions-added"));
            using var check = fixture.NewDb();
            Assert.Equal(3, await check.PhotoPersonTags.CountAsync());
        }

        [Fact]
        public async Task A_dry_run_writes_nothing()
        {
            BuildTree();
            await IngestAsync();

            var immich = new FakeImmich();
            immich.AddAsset("im-1", $"{ContainerRoot}/Vacation/one.jpg", city: "Placeville");
            immich.AddCluster("cluster-1");
            immich.AddFace("im-1", "cluster-1");

            await RunAllAsync(immich, new PhotoImmichSyncOptions { BatchSize = 50, DryRun = true });

            using var db = fixture.NewDb();
            Assert.Empty(await db.FamilyPeople.ToListAsync());
            Assert.Empty(await db.PhotoPersonTags.ToListAsync());
            Assert.Null((await db.PhotoAssets.SingleAsync(a => a.Path == "Vacation/one.jpg")).ImmichAssetId);
        }

        // ── The client itself, over real HTTP (§2.4) ────────────────────────────────────────────

        /// <summary>
        /// The parsing half, exercised over a loopback server speaking the tested Immich shapes — because
        /// a client whose reading is only asserted through its own fake proves nothing about a real
        /// payload. In particular: the box arrives in PIXELS and must come out as fractions, and the api
        /// key must actually be sent (the stand-in answers 401 without it).
        /// </summary>
        [Fact]
        public async Task The_client_reads_the_tested_shapes_and_converts_boxes_to_fractions()
        {
            var data = new FakeImmich();
            data.AddAsset("im-1", $"{ContainerRoot}/Vacation/one.jpg", city: "Placeville", state: "Somestate");
            data.AddCluster("cluster-1");
            data.AddFace("im-1", "cluster-1", confidence: 0.75, x: 0.1, y: 0.2, w: 0.3, h: 0.25);
            data.AddDuplicate("dupe-1", "im-1", "im-2");
            data.Thumbnails["cluster-1"] = new byte[] { 1, 2, 3, 4 };

            using var server = data.Serve(PickPort());
            using var client = BuildClient(server, "test-key");

            var version = await client.RequireSupportedVersionAsync();
            Assert.Equal(ImmichClient.TestedMajor, version.Major);

            var assets = await client.AssetsAsync(1, 50);
            Assert.Single(assets.Items);
            Assert.Equal($"{ContainerRoot}/Vacation/one.jpg", assets.Items[0].OriginalPath);
            Assert.Equal("Placeville", assets.Items[0].City);
            Assert.False(assets.HasNextPage);

            var people = await client.PeopleAsync(1, 50);
            Assert.Single(people.People);

            var faces = await client.FacesForAssetAsync("im-1");
            var face = Assert.Single(faces);
            Assert.Equal("cluster-1", face.PersonId);
            Assert.Equal(0.1, face.X!.Value, 3);
            Assert.Equal(0.3, face.W!.Value, 3);
            Assert.Equal(0.75, face.Confidence!.Value, 3);

            var duplicates = await client.DuplicatesAsync();
            Assert.Single(duplicates);
            Assert.Equal(2, duplicates[0].AssetIds.Count);

            Assert.Equal(4, (await client.PersonThumbnailAsync("cluster-1"))!.Length);
            Assert.Null(await client.PersonThumbnailAsync("cluster-nope"));
        }

        [Fact]
        public async Task Without_the_api_key_nothing_is_readable()
        {
            var data = new FakeImmich();
            using var server = data.Serve(PickPort());
            using var client = BuildClient(server, "wrong-key");
            await Assert.ThrowsAsync<HttpRequestException>(() => client.VersionAsync());
        }

        /// <summary>
        /// §2.4's version pin. An untested MAJOR refuses the run with a message a human can act on,
        /// rather than parsing a payload whose shape it has never seen: mis-parsing a face box into a tag
        /// row is a silent wrong answer, and refusing is a loud right one.
        /// </summary>
        [Fact]
        public async Task An_untested_major_version_refuses_the_run()
        {
            var data = new FakeImmich { Version = new ImmichVersion(2, 0, 0) };
            using var server = data.Serve(PickPort());
            using var client = BuildClient(server, "test-key");

            var thrown = await Assert.ThrowsAsync<ImmichVersionUnsupportedException>(
                () => client.RequireSupportedVersionAsync());
            Assert.Equal(2, thrown.Version.Major);
            Assert.Contains("tested major", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_unconfigured_host_gets_no_client_at_all_rather_than_an_error()
        {
            var config = new MovieTheaterConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            // The normal state of every host but the gateway-adjacent one — and the album is fully
            // usable in it (§2.4).
            Assert.Null(ImmichClient.TryCreate(config));
        }

        // ── Face crops (§2.4) ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// The crop is fetched server-side and cached into the derivative cache the gateway already
        /// serves, so the browser is handed an ordinary path and never learns a sidecar exists. When
        /// there is no crop — Immich gone, or never deployed — the answer is simply null, and the queue
        /// draws the stored box over our own thumb instead.
        /// </summary>
        [Fact]
        public async Task A_face_crop_is_cached_once_and_a_missing_one_is_not_an_error()
        {
            var immich = new FakeImmich();
            immich.Thumbnails["cluster-1"] = new byte[] { 9, 9, 9 };

            var first = await PhotoFaceCrops.EnsureAsync(fixture.ThumbCache, immich, "cluster-1");
            Assert.NotNull(first);
            Assert.True(File.Exists(PhotoFaceCrops.FullPath(fixture.ThumbCache, "cluster-1")));

            var callsBefore = immich.Calls["thumbnail"];
            Assert.Equal(first, await PhotoFaceCrops.EnsureAsync(fixture.ThumbCache, immich, "cluster-1"));
            // Cached: the second ask did not go back to the wire.
            Assert.Equal(callsBefore, immich.Calls["thumbnail"]);

            Assert.Null(await PhotoFaceCrops.EnsureAsync(fixture.ThumbCache, immich, "cluster-none"));
            // The sidecar being gone does NOT lose the crops it already produced — the cache is ours,
            // and Immich is disposable precisely because nothing of ours depends on it still running.
            Assert.Equal(first, await PhotoFaceCrops.EnsureAsync(fixture.ThumbCache, null, "cluster-1"));
            // And with no sidecar and no cached crop, null is an ordinary answer rather than an
            // exception path: the queue draws the stored box over our own thumb instead.
            Assert.Null(await PhotoFaceCrops.EnsureAsync(fixture.ThumbCache, null, "cluster-2"));
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        private static ImmichClient BuildClient(FakeImmichServer server, string apiKey)
        {
            var config = new MovieTheaterConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
            {
                ImmichBaseUrl = server.BaseUrl,
                ImmichApiKey = apiKey,
            };
            return ImmichClient.TryCreate(config, timeout: TimeSpan.FromSeconds(10))!;
        }

        /// <summary>A free loopback port, taken by binding one and letting go. Tests run in parallel, so
        /// a hardcoded port would be a flake waiting for a busy machine.</summary>
        private static int PickPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
