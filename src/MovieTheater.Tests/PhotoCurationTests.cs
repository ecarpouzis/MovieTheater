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
    /// Phase 2 curation (docs/photos-plan.md §2.9 hide flags + suggested-hide batches, §2.5 ingest-batch
    /// quarantine), driven against the generated fixture tree and a throwaway SQLite file — never the
    /// real collection, never the configured database.
    ///
    /// <para>The properties under test are the ones whose failure would be quiet and expensive: a
    /// proposal that hides something before a human said so, a scans folder proposed for hiding, a
    /// quarantine that empties an already-ingested timeline, and a hidden photo disappearing from the
    /// FOLDER view — which would make curation look like deletion, the one thing this vertical promises
    /// it never does.</para>
    /// </summary>
    public class PhotoCurationTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        /// <summary>
        /// A tree with the shapes §1 named: keepers, a screenshots pile, a misc pile, scanned prints
        /// (which must never be proposed for hiding), a saved graphic and a too-small image.
        /// </summary>
        private void BuildTree()
        {
            fixture.WriteJpeg("Vacation 2004/keep1.jpg", 640, 480, seed: 21, exifDateTimeOriginal: "2004:07:04 10:00:00");
            fixture.WriteJpeg("Vacation 2004/keep2.jpg", 640, 480, seed: 22, exifDateTimeOriginal: "2004:07:04 11:00:00");
            fixture.WriteJpeg("Vacation 2004/thumbnail.jpg", 120, 90, seed: 23, exifDateTimeOriginal: "2004:07:04 12:00:00");
            fixture.WriteJpeg("Screenshots/Screenshot_2020-01-01.jpg", 640, 480, seed: 24, exifDateTimeOriginal: "2020:01:01 09:00:00");
            fixture.WriteJpeg("Misc Pics/whatever.jpg", 640, 480, seed: 25, exifDateTimeOriginal: "2019:02:02 09:00:00");
            fixture.WriteJpeg("Album Scans/print1.jpg", 640, 480, seed: 26, make: "EPSON", model: "Perfection");
            fixture.WritePng("Graphics/logo.png", 500, 400, seed: 27);
        }

        private async Task IngestAsync(string batchId = "photos-20260101-000000")
        {
            var options = fixture.Options(batchSize: 50);
            options.IngestBatch = batchId;
            var pipeline = fixture.Pipeline(options);
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
        }

        private async Task<int> IdOf(string path)
        {
            using var db = fixture.NewDb();
            return await db.PhotoAssets.Where(a => a.Path == path).Select(a => a.Id).FirstAsync();
        }

        // ── Hide / unhide (§2.9) ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Hiding is a FLAG, and the file is untouched — which is the point of the whole mechanism (§1
        /// asked for a flag "not deletion").
        ///
        /// <para>Phase 4 narrowed WHO can see the result. §2.9's "folder view shows all" was written
        /// before show-hidden became admin-only, and it now yields: an admin who asks still sees the
        /// whole tree, badged, but a member gets the curated view on every surface. A folder tab that
        /// opted out would not be a rule, it would be a longer route to the same pictures.</para>
        /// </summary>
        [Fact]
        public async Task Hiding_a_selection_removes_it_from_browse_and_an_admin_still_sees_it_badged()
        {
            BuildTree();
            await IngestAsync();
            var hideMe = await IdOf("Screenshots/Screenshot_2020-01-01.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            var hide = PhotosControllerHarness.Body(
                await controller.Hide(new PhotoHideRequest { Ids = new List<int> { hideMe }, Hidden = true }));
            Assert.Equal(1, PhotosControllerHarness.Int(hide, "changed"));

            var timeline = PhotosControllerHarness.Body(await controller.Timeline());
            Assert.DoesNotContain(hideMe, PhotosControllerHarness.ItemIds(timeline));
            Assert.DoesNotContain(hideMe, PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await controller.Folders("Screenshots"))));

            // The row is still there, still on disk, and the folder view says so to whoever may look.
            var admin = PhotosControllerHarness.Build(fixture, db, admin: true, userId: 8);
            var folder = PhotosControllerHarness.Body(await admin.Folders("Screenshots", includeHidden: true));
            Assert.Contains(hideMe, PhotosControllerHarness.ItemIds(folder));
            var badged = folder.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("id").GetInt32() == hideMe);
            Assert.True(badged.GetProperty("hidden").GetBoolean());
        }

        /// <summary>
        /// Phase 4's rule (addendum), superseding Phase 2's member-visible toggle: any family member may
        /// hide a photo, but only an ADMIN may see what was hidden.
        ///
        /// <para>A member asking is IGNORED rather than refused. A 403 would tell a stale tab — and its
        /// user — that there is something there to be forbidden; answering the curated album is both the
        /// honest answer to "show me the photos" and the one that cannot be probed.</para>
        /// </summary>
        [Fact]
        public async Task A_member_asking_for_hidden_items_is_ignored_and_an_admin_is_honoured()
        {
            BuildTree();
            await IngestAsync();
            var hideMe = await IdOf("Misc Pics/whatever.jpg");

            using var db = fixture.NewDb();
            var member = PhotosControllerHarness.Build(fixture, db);
            await member.Hide(new PhotoHideRequest { Ids = new List<int> { hideMe }, Hidden = true });

            // Hiding is member work and it worked.
            Assert.DoesNotContain(hideMe, PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await member.Timeline())));

            // Asking to see it is not member work. Not a 403 — a JSON page without it.
            var memberAsked = PhotosControllerHarness.Body(await member.Timeline(includeHidden: true));
            Assert.DoesNotContain(hideMe, PhotosControllerHarness.ItemIds(memberAsked));
            // The answer says which view it actually is, so nothing has to be inferred from absence.
            Assert.False(memberAsked.GetProperty("includeHidden").GetBoolean());

            var admin = PhotosControllerHarness.Build(fixture, db, admin: true, userId: 8);
            var adminAsked = PhotosControllerHarness.Body(await admin.Timeline(includeHidden: true));
            Assert.Contains(hideMe, PhotosControllerHarness.ItemIds(adminAsked));
            Assert.True(adminAsked.GetProperty("includeHidden").GetBoolean());
        }

        /// <summary>
        /// The same rule on the folder tab, which §2.9 used to exempt. "Hidden is visible only to an
        /// admin" has to hold on every surface or it holds on none — a folder view that quietly opted
        /// out would not be a rule, it would be a longer route to the same pictures.
        /// </summary>
        [Fact]
        public async Task The_folder_view_hides_from_members_too_and_shows_an_admin_everything()
        {
            BuildTree();
            await IngestAsync();
            var hideMe = await IdOf("Misc Pics/whatever.jpg");

            using var db = fixture.NewDb();
            var member = PhotosControllerHarness.Build(fixture, db);
            await member.Hide(new PhotoHideRequest { Ids = new List<int> { hideMe }, Hidden = true });

            Assert.DoesNotContain(hideMe, PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await member.Folders("Misc Pics"))));
            Assert.DoesNotContain(hideMe, PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await member.Folders("Misc Pics", includeHidden: true))));

            var admin = PhotosControllerHarness.Build(fixture, db, admin: true, userId: 8);
            Assert.Contains(hideMe, PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await admin.Folders("Misc Pics", includeHidden: true))));
        }

        [Fact]
        public async Task Unhiding_puts_it_straight_back()
        {
            BuildTree();
            await IngestAsync();
            var id = await IdOf("Misc Pics/whatever.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            await controller.Hide(new PhotoHideRequest { Ids = new List<int> { id }, Hidden = true });

            var unhide = PhotosControllerHarness.Body(
                await controller.Hide(new PhotoHideRequest { Ids = new List<int> { id }, Hidden = false }));
            Assert.Equal(1, PhotosControllerHarness.Int(unhide, "changed"));
            Assert.Contains(id, PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await controller.Timeline())));
        }

        [Fact]
        public async Task Hiding_something_already_hidden_changes_nothing()
        {
            BuildTree();
            await IngestAsync();
            var id = await IdOf("Misc Pics/whatever.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            await controller.Hide(new PhotoHideRequest { Ids = new List<int> { id }, Hidden = true });

            var again = PhotosControllerHarness.Body(
                await controller.Hide(new PhotoHideRequest { Ids = new List<int> { id }, Hidden = true }));
            Assert.Equal(0, PhotosControllerHarness.Int(again, "changed"));
            Assert.Equal(1, PhotosControllerHarness.Int(again, "matched"));
        }

        [Fact]
        public async Task An_empty_selection_is_refused_rather_than_silently_doing_nothing()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            Assert.IsType<BadRequestObjectResult>(await controller.Hide(new PhotoHideRequest { Ids = new List<int>() }));
        }

        // ── Suggested-hide proposals (§2.9) ─────────────────────────────────────────────────────

        private async Task<PhotoIngestBatchResult> SuggestAsync(string batchId, int batchSize = 100)
        {
            var pass = new PhotoSuggestHidePass(
                fixture.NewDb, new PhotoHideSuggestions.Options(), batchSize, _ => { });
            return await pass.RunAsync(batchId, null, 0);
        }

        [Fact]
        public async Task Suggest_hide_proposes_the_clutter_and_never_the_scans()
        {
            BuildTree();
            await IngestAsync();

            await SuggestAsync("hide-1");
            using var readDb = fixture.NewDb();
            var proposal = await fixture.CurationStore(readDb).LoadProposalAsync("hide-1");
            Assert.NotNull(proposal);

            var byPath = proposal!.Items.ToDictionary(i => i.Path, i => i.Rule);
            Assert.Equal(PhotoHideSuggestions.RuleScreenshotFolder, byPath["Screenshots/Screenshot_2020-01-01.jpg"]);
            Assert.Equal(PhotoHideSuggestions.RuleMiscFolder, byPath["Misc Pics/whatever.jpg"]);
            Assert.Equal(PhotoHideSuggestions.RuleTinyImage, byPath["Vacation 2004/thumbnail.jpg"]);
            Assert.Equal(PhotoHideSuggestions.RuleNonPhotoFormat, byPath["Graphics/logo.png"]);

            // The scanned print and the keepers are the whole reason this is a proposal and not a sweep.
            Assert.DoesNotContain("Album Scans/print1.jpg", byPath.Keys);
            Assert.DoesNotContain("Vacation 2004/keep1.jpg", byPath.Keys);
            Assert.DoesNotContain("Vacation 2004/keep2.jpg", byPath.Keys);
        }

        [Fact]
        public async Task A_proposal_hides_nothing_by_itself()
        {
            BuildTree();
            await IngestAsync();
            await SuggestAsync("hide-1");

            using var db = fixture.NewDb();
            // The load-bearing property of the whole surface: the pass has no path to the flag.
            Assert.Equal(0, await db.PhotoAssets.CountAsync(a => a.Hidden));
        }

        [Fact]
        public async Task Accepting_a_proposal_hides_its_assets_in_one_action()
        {
            BuildTree();
            await IngestAsync();
            await SuggestAsync("hide-1");
            var screenshot = await IdOf("Screenshots/Screenshot_2020-01-01.jpg");
            var keeper = await IdOf("Vacation 2004/keep1.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            var pending = PhotosControllerHarness.Body(await controller.HideProposals());
            Assert.Single(pending.GetProperty("proposals").EnumerateArray());

            var decision = PhotosControllerHarness.Body(await controller.DecideHideProposal("hide-1", "accept"));
            Assert.Equal(4, PhotosControllerHarness.Int(decision, "applied"));
            Assert.Equal("accepted", decision.GetProperty("status").GetString());

            var timeline = PhotosControllerHarness.Body(await controller.Timeline());
            var visible = PhotosControllerHarness.ItemIds(timeline);
            Assert.DoesNotContain(screenshot, visible);
            Assert.Contains(keeper, visible);

            // And it leaves the pending list, so the review surface does not re-ask.
            Assert.Empty(PhotosControllerHarness.Body(await controller.HideProposals()).GetProperty("proposals").EnumerateArray());
        }

        [Fact]
        public async Task Rejecting_a_proposal_hides_nothing_and_closes_it()
        {
            BuildTree();
            await IngestAsync();
            await SuggestAsync("hide-1");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var decision = PhotosControllerHarness.Body(await controller.DecideHideProposal("hide-1", "reject"));

            Assert.Equal(0, PhotosControllerHarness.Int(decision, "applied"));
            Assert.Equal("rejected", decision.GetProperty("status").GetString());
            Assert.Equal(0, await db.PhotoAssets.CountAsync(a => a.Hidden));
            Assert.Empty(PhotosControllerHarness.Body(await controller.HideProposals()).GetProperty("proposals").EnumerateArray());
        }

        [Fact]
        public async Task A_decided_batch_cannot_be_decided_again()
        {
            BuildTree();
            await IngestAsync();
            await SuggestAsync("hide-1");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            await controller.DecideHideProposal("hide-1", "accept");

            // A double-click, a stale tab, a re-post: the second one must not sweep again.
            Assert.IsType<BadRequestObjectResult>(await controller.DecideHideProposal("hide-1", "reject"));
        }

        [Fact]
        public async Task Accepting_skips_assets_a_human_already_hid()
        {
            BuildTree();
            await IngestAsync();
            await SuggestAsync("hide-1");
            var screenshot = await IdOf("Screenshots/Screenshot_2020-01-01.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            await controller.Hide(new PhotoHideRequest { Ids = new List<int> { screenshot }, Hidden = true });

            var decision = PhotosControllerHarness.Body(await controller.DecideHideProposal("hide-1", "accept"));
            // Applied counts what actually changed, not what the artifact hoped for.
            Assert.Equal(3, PhotosControllerHarness.Int(decision, "applied"));
            Assert.Equal(4, PhotosControllerHarness.Int(decision, "proposed"));
        }

        [Fact]
        public async Task Suggest_hide_is_chunked_resumable_and_terminates()
        {
            BuildTree();
            await IngestAsync();

            var pass = new PhotoSuggestHidePass(fixture.NewDb, new PhotoHideSuggestions.Options(), 2, _ => { });

            // The driver loop lives in the caller (the standing bulk-job rule); the pass does one
            // bounded batch and hands back the cursor it stopped at.
            var cursor = (string?)null;
            var batches = 0;
            var processed = 0;
            for (var i = 0; i < 50; i++)
            {
                var result = await pass.RunAsync("hide-chunked", cursor, 1);
                batches++;
                processed += result.Processed;
                cursor = result.NextCursor;
                if (result.Remaining <= 0) break;
                Assert.True(result.Processed > 0, "a batch made no progress while rows remained");
            }

            using var db = fixture.NewDb();
            // Against an independent count of what was examinable, not against the pass's own report.
            Assert.Equal(await db.PhotoAssets.CountAsync(a => a.MissingSinceUtc == null && !a.Hidden), processed);
            Assert.True(batches > 2, "batch size 2 over this tree should have taken several batches");

            var proposal = await fixture.CurationStore(db).LoadProposalAsync("hide-chunked");
            Assert.NotNull(proposal);
            Assert.True(proposal!.Complete);
            Assert.Equal(4, proposal.Items.Count);
        }

        [Fact]
        public async Task A_rule_subset_proposes_only_that_rule()
        {
            BuildTree();
            await IngestAsync();

            var options = new PhotoHideSuggestions.Options
            {
                Rules = new HashSet<string>(new[] { PhotoHideSuggestions.RuleScreenshotFolder }, StringComparer.OrdinalIgnoreCase),
            };
            var pass = new PhotoSuggestHidePass(fixture.NewDb, options, 100, _ => { });
            await pass.RunAsync("hide-screens", null, 0);

            using var readDb = fixture.NewDb();
            var proposal = await fixture.CurationStore(readDb).LoadProposalAsync("hide-screens");
            Assert.NotNull(proposal);
            Assert.All(proposal!.Items, i => Assert.Equal(PhotoHideSuggestions.RuleScreenshotFolder, i.Rule));
            Assert.Single(proposal.Items);
        }

        // ── Ingest-batch quarantine (§2.5) ──────────────────────────────────────────────────────

        [Fact]
        public async Task Batches_that_already_existed_are_the_baseline_and_are_not_quarantined()
        {
            BuildTree();
            await IngestAsync("photos-20260101-000000");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            // The first read materializes the review state. Everything ingested before the feature
            // existed is approved by that act — a family must never open /photos to an empty page.
            var status = PhotosControllerHarness.Body(await controller.Status());
            Assert.Equal(0, PhotosControllerHarness.Int(status, "quarantinedBatches"));
            Assert.NotEmpty(PhotosControllerHarness.ItemIds(PhotosControllerHarness.Body(await controller.Timeline())));
        }

        [Fact]
        public async Task An_ingest_that_arrives_after_the_baseline_is_quarantined_until_approved()
        {
            BuildTree();
            await IngestAsync("photos-20260101-000000");

            using (var db = fixture.NewDb())
                await PhotosControllerHarness.Build(fixture, db).Status(); // baseline

            fixture.WriteJpeg("Vacation 2004/late.jpg", 640, 480, seed: 31, exifDateTimeOriginal: "2004:07:05 10:00:00");
            await IngestAsync("photos-20260202-000000");
            var late = await IdOf("Vacation 2004/late.jpg");

            using var db2 = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db2, admin: true);

            var quarantined = PhotosControllerHarness.Body(await controller.Timeline());
            Assert.DoesNotContain(late, PhotosControllerHarness.ItemIds(quarantined));
            Assert.Equal(1, PhotosControllerHarness.Int(quarantined, "quarantinedBatches"));

            var batches = PhotosControllerHarness.Body(await controller.IngestBatches());
            var pendingGroup = batches.GetProperty("groups").EnumerateArray()
                .First(g => !g.GetProperty("approved").GetBoolean());
            Assert.Equal("photos-20260202", pendingGroup.GetProperty("groupKey").GetString());

            await controller.ApproveIngestBatches(new PhotoApproveBatchesRequest { GroupKey = "photos-20260202" });

            var after = PhotosControllerHarness.Body(await controller.Timeline());
            Assert.Contains(late, PhotosControllerHarness.ItemIds(after));
            Assert.Equal(0, PhotosControllerHarness.Int(after, "quarantinedBatches"));
        }

        [Fact]
        public async Task The_ingest_batch_surfaces_are_admin_only_on_top_of_the_family_gate()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            // In the album, but not an operator: being family is not being the person who runs ingests.
            var member = PhotosControllerHarness.Build(fixture, db, admin: false);
            Assert.IsType<ForbidResult>(await member.IngestBatches());
            Assert.IsType<ForbidResult>(await member.ApproveIngestBatches(
                new PhotoApproveBatchesRequest { GroupKey = "photos-20260101" }));

            // …and the curation surfaces a member DOES own are not admin-gated.
            Assert.IsType<JsonResult>(await member.HideProposals());
        }

        [Theory]
        // A chunked walk mints one marker per invocation, so a night's ingest is dozens of them; the
        // review surface groups by the marker's DAY so it is one thing to approve.
        [InlineData("photos-20260812-154711", "photos-20260812")]
        [InlineData("photos-20260812-235959", "photos-20260812")]
        [InlineData("photos-20260813-000001", "photos-20260813")]
        // A hand-passed --batch-id that is not date-shaped stands alone, which is what naming one by
        // hand should mean.
        [InlineData("family-reunion-import", "family-reunion-import")]
        [InlineData("", "")]
        public void Chunked_walk_markers_group_by_their_day(string batchId, string expected)
        {
            Assert.Equal(expected, PhotoCurationStore.GroupKey(batchId));
        }

        [Fact]
        public async Task Review_state_survives_a_process_that_never_saw_the_report_directory()
        {
            // The Phase 3 point of the whole migration: the prod site pods cannot read the CLI host's
            // PhotosReportDir, and a JSON-backed review surface is simply EMPTY there while looking
            // healthy. Rows are read by anything that can reach the database — which is what this
            // second, report-dir-free store stands in for.
            BuildTree();
            await IngestAsync();
            await SuggestAsync("hide-1");

            using var db = fixture.NewDb();
            var elsewhere = new PhotoCurationStore(db);
            Assert.True(elsewhere.Configured);

            var proposals = await elsewhere.ListProposalsAsync();
            Assert.Single(proposals);
            Assert.Equal("hide-1", proposals[0].BatchId);
            var (rules, count, _) = await elsewhere.RuleCountsAsync("hide-1");
            Assert.Equal(4, count);
            Assert.NotEmpty(rules);
        }
    }
}
