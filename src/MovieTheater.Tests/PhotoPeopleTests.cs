using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Controllers;
using MovieTheater.Db;
using MovieTheater.Photos;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Phase 4 people and tagging (docs/photos-plan.md §2.7, §2.8), driven against the generated fixture
    /// tree and a throwaway SQLite file — never the real collection, never the configured database.
    ///
    /// <para>The properties under test are the ones whose failure would be quiet and expensive: a tag
    /// that lands on a copy nobody sees (§2.6's master redirect), a machine overwriting a human's answer,
    /// a refusal that does not stick and is re-asked forever, and a circa RANGE written as an exact
    /// January 1st — the failure §2.7 exists to prevent, wearing a more convincing date than the undated
    /// shelf it escaped.</para>
    ///
    /// <para>No family names appear anywhere here. The fixture's people are placeholders, which is the
    /// §6 rule and also the honest thing: the code has never needed to know one.</para>
    /// </summary>
    public class PhotoPeopleTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        private void BuildTree()
        {
            fixture.WriteJpeg("Vacation/one.jpg", 320, 240, seed: 5, exifDateTimeOriginal: "2011:07:04 10:00:00");
            fixture.WriteJpeg("Vacation/two.jpg", 320, 240, seed: 6, exifDateTimeOriginal: "2011:07:04 11:00:00");
            fixture.WriteJpeg("Album Scans/undated.jpg", 320, 240, seed: 7);
            // Two byte-identical copies in different folders: the merge-needed shape, and the reason the
            // master redirect exists at all.
            fixture.WriteJpeg("Phone Backup/dupe.jpg", 320, 240, seed: 9, exifDateTimeOriginal: "2012:01:02 08:00:00");
            fixture.WriteJpeg("Vacation/dupe.jpg", 320, 240, seed: 9, exifDateTimeOriginal: "2012:01:02 08:00:00");
            fixture.WriteJpeg("Misc Pics/junk.jpg", 320, 240, seed: 11, exifDateTimeOriginal: "2013:01:01 08:00:00");
        }

        private async Task IngestAsync()
        {
            var pipeline = fixture.Pipeline(fixture.Options(batchSize: 50));
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Hash, null, 0);
        }

        private async Task GroupDupesAsync() =>
            await new PhotoDupePass(fixture.NewDb, new PhotoDupeOptions { BatchSize = 100 }, _ => { })
                .RunAsync(PhotoDupePassKind.Exact, null, 0);

        private async Task<int> IdOf(string path)
        {
            using var db = fixture.NewDb();
            return await db.PhotoAssets.Where(a => a.Path == path).Select(a => a.Id).FirstAsync();
        }

        private static int PersonId(JsonElement body) => body.GetProperty("person").GetProperty("id").GetInt32();

        // ── People CRUD (§2.8) ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_member_can_add_edit_and_delete_a_person()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            var created = PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A", BirthYear = 1990 }));
            Assert.True(created.GetProperty("created").GetBoolean());
            var id = PersonId(created);

            var updated = PhotosControllerHarness.Body(await controller.UpdatePerson(id,
                new PhotoPersonRequest { Name = "Subject A2", BirthYear = 1991, BirthYearSet = true }));
            Assert.Equal("Subject A2", updated.GetProperty("person").GetProperty("name").GetString());
            Assert.Equal(1991, updated.GetProperty("person").GetProperty("birthYear").GetInt32());

            var list = PhotosControllerHarness.Body(await controller.People());
            Assert.Single(list.GetProperty("people").EnumerateArray());

            var deleted = PhotosControllerHarness.Body(
                await controller.DeletePerson(id, new PhotoAlbumDeleteRequest { Confirm = true }));
            Assert.True(deleted.GetProperty("deleted").GetBoolean());
        }

        /// <summary>A birth year outside living memory is a typo, not a fact — and §2.7's hint is only
        /// worth showing if the bound is real.</summary>
        [Fact]
        public async Task An_impossible_birth_year_is_dropped_rather_than_stored()
        {
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A", BirthYear = 1492 }));
            Assert.Equal(JsonValueKind.Null, created.GetProperty("person").GetProperty("birthYear").ValueKind);
        }

        [Fact]
        public async Task Deleting_a_person_without_confirming_is_refused()
        {
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" }));
            var result = await controller.DeletePerson(PersonId(created), new PhotoAlbumDeleteRequest());
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        }

        // ── The master redirect (§2.6) ──────────────────────────────────────────────────────────

        /// <summary>
        /// §2.6's load-bearing rule: "tagging or dating any member redirects the write to the master".
        /// Tagging the copy in a phone-backup folder must produce a tag on the copy the timeline shows —
        /// otherwise a family's tagging pass lands on rows nobody ever sees again.
        /// </summary>
        [Fact]
        public async Task Tagging_a_duplicate_copy_writes_the_tag_on_the_master()
        {
            BuildTree();
            await IngestAsync();
            await GroupDupesAsync();

            var a = await IdOf("Phone Backup/dupe.jpg");
            var b = await IdOf("Vacation/dupe.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));

            var master = await PhotoDupeMasters.MasterForAsync(db, a);
            var nonMaster = master == a ? b : a;

            var written = PhotosControllerHarness.Body(await controller.AddTags(new PhotoTagRequest
            {
                AssetIds = new List<int> { nonMaster },
                FamilyPersonId = person,
            }));
            Assert.Equal(1, PhotosControllerHarness.Int(written, "added"));
            // Reported, never silent: "I tagged this one and it shows on that one" needs a reason.
            Assert.Equal(1, PhotosControllerHarness.Int(written, "redirectedToMasters"));

            using var check = fixture.NewDb();
            var rows = await check.PhotoPersonTags.ToListAsync();
            Assert.Single(rows);
            Assert.Equal(master, rows[0].PhotoAssetId);

            // Reading the tags off the copy still answers "yes, this person is in this photograph".
            var read = PhotosControllerHarness.Body(await controller.AssetTags(nonMaster));
            Assert.True(read.GetProperty("redirected").GetBoolean());
            Assert.Single(read.GetProperty("tags").EnumerateArray());
        }

        /// <summary>Selecting both copies of one photograph and tagging must make ONE tag, not one tag
        /// and one invisible row.</summary>
        [Fact]
        public async Task Tagging_both_copies_at_once_makes_one_tag()
        {
            BuildTree();
            await IngestAsync();
            await GroupDupesAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));

            var written = PhotosControllerHarness.Body(await controller.AddTags(new PhotoTagRequest
            {
                AssetIds = new List<int> { await IdOf("Phone Backup/dupe.jpg"), await IdOf("Vacation/dupe.jpg") },
                FamilyPersonId = person,
            }));
            Assert.Equal(1, PhotosControllerHarness.Int(written, "added"));

            using var check = fixture.NewDb();
            Assert.Equal(1, await check.PhotoPersonTags.CountAsync());
        }

        [Fact]
        public async Task Tagging_the_same_person_twice_is_a_no_op()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            var asset = await IdOf("Vacation/one.jpg");

            await controller.AddTags(new PhotoTagRequest { AssetIds = new List<int> { asset }, FamilyPersonId = person });
            var again = PhotosControllerHarness.Body(await controller.AddTags(
                new PhotoTagRequest { AssetIds = new List<int> { asset }, FamilyPersonId = person }));

            Assert.Equal(0, PhotosControllerHarness.Int(again, "added"));
            Assert.Equal(1, PhotosControllerHarness.Int(again, "unchanged"));
            using var check = fixture.NewDb();
            Assert.Equal(1, await check.PhotoPersonTags.CountAsync());
        }

        /// <summary>The type-ahead's "add …": a name instead of an id creates the person in the same
        /// round trip as the tag.</summary>
        [Fact]
        public async Task Tagging_by_name_creates_the_person()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var written = PhotosControllerHarness.Body(await controller.AddTags(new PhotoTagRequest
            {
                AssetIds = new List<int> { await IdOf("Vacation/one.jpg") },
                Name = "Subject B",
            }));
            Assert.Equal("Subject B", written.GetProperty("person").GetProperty("name").GetString());
            Assert.Equal(1, PhotosControllerHarness.Int(written, "added"));
        }

        /// <summary>An untag DELETES the row. Recording it as a refusal instead would permanently bar a
        /// machine from ever proposing a person who really is in the picture.</summary>
        [Fact]
        public async Task Untagging_deletes_the_row_rather_than_leaving_a_tombstone()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            var asset = await IdOf("Vacation/one.jpg");

            await controller.AddTags(new PhotoTagRequest { AssetIds = new List<int> { asset }, FamilyPersonId = person });
            await controller.RemoveTags(new PhotoTagRequest { AssetIds = new List<int> { asset }, FamilyPersonId = person });

            using var check = fixture.NewDb();
            Assert.Empty(await check.PhotoPersonTags.ToListAsync());
        }

        // ── Suggestions: confirm, reject, and the no-clobber rules (§2.4/§2.8) ──────────────────

        [Fact]
        public async Task Confirming_a_suggestion_promotes_the_same_row()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            var asset = await IdOf("Vacation/one.jpg");

            await PhotoPersonTags.SuggestAsync(db, asset, person, "cluster-1", 0.9, 0.1, 0.1, 0.2, 0.2);
            await db.SaveChangesAsync();
            var tagId = await db.PhotoPersonTags.Select(t => t.Id).FirstAsync();

            await controller.ConfirmTag(tagId);

            using var check = fixture.NewDb();
            var rows = await check.PhotoPersonTags.ToListAsync();
            // One row transitioning, not two rows racing (§2.8).
            Assert.Single(rows);
            Assert.Equal(PhotoTagSource.Confirmed, rows[0].Source);
            Assert.NotNull(rows[0].ConfirmedUtc);
        }

        [Fact]
        public async Task Rejecting_a_suggestion_leaves_a_tombstone_that_is_not_a_tag()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            var asset = await IdOf("Vacation/one.jpg");

            await PhotoPersonTags.SuggestAsync(db, asset, person, "cluster-1", 0.9, null, null, null, null);
            await db.SaveChangesAsync();
            var tagId = await db.PhotoPersonTags.Select(t => t.Id).FirstAsync();

            await controller.RejectTag(tagId);

            using var check = fixture.NewDb();
            var row = await check.PhotoPersonTags.SingleAsync();
            // The row survives — that IS the "do not propose this again" record — but it counts as
            // nobody being in the photograph.
            Assert.Equal(PhotoTagSource.Rejected, row.Source);
            Assert.False(PhotoPersonTags.IsAffirmed(row.Source));

            var tags = PhotosControllerHarness.Body(await controller.AssetTags(asset));
            Assert.Empty(tags.GetProperty("tags").EnumerateArray());
        }

        /// <summary>
        /// The no-clobber rules, all three at once (§2.4). A sidecar that could overwrite a human's
        /// answer, revive a refusal, or add a second row for the same face would make re-running the sync
        /// destructive — and this vertical's whole value is the irreplaceable human labor it would be
        /// destroying (§2.11).
        /// </summary>
        [Fact]
        public async Task A_suggestion_never_clobbers_a_human_answer_and_never_duplicates_itself()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            var confirmedAsset = await IdOf("Vacation/one.jpg");
            var refusedAsset = await IdOf("Vacation/two.jpg");

            // A hand tag, and a refusal.
            await controller.AddTags(new PhotoTagRequest
            {
                AssetIds = new List<int> { confirmedAsset },
                FamilyPersonId = person,
            });
            await PhotoPersonTags.SuggestAsync(db, refusedAsset, person, "cluster-1", 0.8, null, null, null, null);
            await db.SaveChangesAsync();
            var refusedTag = await db.PhotoPersonTags
                .Where(t => t.PhotoAssetId == refusedAsset).Select(t => t.Id).FirstAsync();
            await controller.RejectTag(refusedTag);

            using var second = fixture.NewDb();
            Assert.Equal("suggestion-skipped-human-tag",
                await PhotoPersonTags.SuggestAsync(second, confirmedAsset, person, "cluster-1", 0.99, null, null, null, null));
            Assert.Equal("suggestion-skipped-rejected",
                await PhotoPersonTags.SuggestAsync(second, refusedAsset, person, "cluster-1", 0.99, null, null, null, null));
            await second.SaveChangesAsync();

            using var check = fixture.NewDb();
            var rows = await check.PhotoPersonTags.ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(PhotoTagSource.Manual, rows.Single(r => r.PhotoAssetId == confirmedAsset).Source);
            Assert.Equal(PhotoTagSource.Rejected, rows.Single(r => r.PhotoAssetId == refusedAsset).Source);
        }

        [Fact]
        public async Task Re_suggesting_the_same_face_refreshes_the_row_instead_of_adding_one()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            var asset = await IdOf("Vacation/one.jpg");

            Assert.Equal("suggestions-added",
                await PhotoPersonTags.SuggestAsync(db, asset, person, "c1", 0.5, 0.1, 0.1, 0.1, 0.1));
            await db.SaveChangesAsync();
            Assert.Equal("suggestions-refreshed",
                await PhotoPersonTags.SuggestAsync(db, asset, person, "c1", 0.95, 0.2, 0.2, 0.3, 0.3));
            await db.SaveChangesAsync();

            using var check = fixture.NewDb();
            var row = await check.PhotoPersonTags.SingleAsync();
            Assert.Equal(0.95, row.Confidence);
            Assert.Equal(0.2, row.BoxX);
        }

        // ── Naming and mapping a cluster (§2.8) ─────────────────────────────────────────────────

        [Fact]
        public async Task An_unnamed_cluster_is_listed_apart_from_people_and_naming_it_moves_it_across()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            db.FamilyPeople.Add(new FamilyPerson { Name = "", ImmichPersonId = "cluster-1", CreatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var clusterRow = await db.FamilyPeople.SingleAsync();

            var before = PhotosControllerHarness.Body(await controller.People());
            Assert.Empty(before.GetProperty("people").EnumerateArray());
            Assert.Single(before.GetProperty("unnamed").EnumerateArray());

            var named = PhotosControllerHarness.Body(await controller.UpdatePerson(clusterRow.Id,
                new PhotoPersonRequest { Name = "Subject A" }));
            Assert.True(named.GetProperty("named").GetBoolean());

            var after = PhotosControllerHarness.Body(await controller.People());
            Assert.Single(after.GetProperty("people").EnumerateArray());
            Assert.Empty(after.GetProperty("unnamed").EnumerateArray());
        }

        /// <summary>
        /// Mapping a cluster onto somebody who already exists. The merge must never weaken a human's tag
        /// into a machine's guess, and it must carry the cluster link across — otherwise the next sync
        /// would import the same faces again as a fresh unnamed group.
        /// </summary>
        [Fact]
        public async Task Mapping_a_cluster_onto_an_existing_person_keeps_the_stronger_claim()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            var shared = await IdOf("Vacation/one.jpg");
            var only = await IdOf("Vacation/two.jpg");

            await controller.AddTags(new PhotoTagRequest { AssetIds = new List<int> { shared }, FamilyPersonId = person });

            db.FamilyPeople.Add(new FamilyPerson { Name = "", ImmichPersonId = "cluster-1", CreatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var cluster = await db.FamilyPeople.Where(p => p.Name == "").Select(p => p.Id).SingleAsync();
            await PhotoPersonTags.SuggestAsync(db, shared, cluster, "cluster-1", 0.9, null, null, null, null);
            await PhotoPersonTags.SuggestAsync(db, only, cluster, "cluster-1", 0.9, null, null, null, null);
            await db.SaveChangesAsync();

            var merged = PhotosControllerHarness.Body(await controller.MergePerson(cluster,
                new PhotoPersonMergeRequest { IntoPersonId = person }));
            Assert.Equal(1, PhotosControllerHarness.Int(merged, "moved"));
            Assert.Equal(1, PhotosControllerHarness.Int(merged, "dropped"));

            using var check = fixture.NewDb();
            Assert.Empty(await check.FamilyPeople.Where(p => p.Name == "").ToListAsync());
            // The hand tag survived the machine's suggestion arriving on the same photograph.
            Assert.Equal(PhotoTagSource.Manual,
                (await check.PhotoPersonTags.SingleAsync(t => t.PhotoAssetId == shared)).Source);
            // The cluster link travelled, so a re-sync finds this cluster already answered.
            Assert.Equal("cluster-1", (await check.FamilyPeople.SingleAsync()).ImmichPersonId);
        }

        // ── Person pages (§2.8) ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_person_page_lists_their_photos_and_who_else_is_in_them()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var a = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            var b = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject B" })));
            var shared = await IdOf("Vacation/one.jpg");
            var alone = await IdOf("Vacation/two.jpg");

            await controller.AddTags(new PhotoTagRequest { AssetIds = new List<int> { shared, alone }, FamilyPersonId = a });
            await controller.AddTags(new PhotoTagRequest { AssetIds = new List<int> { shared }, FamilyPersonId = b });

            var detail = PhotosControllerHarness.Body(await controller.Person(a));
            Assert.Equal(2, PhotosControllerHarness.Int(detail, "tagCount"));
            var chips = detail.GetProperty("alsoWith").EnumerateArray().ToList();
            Assert.Single(chips);
            Assert.Equal("Subject B", chips[0].GetProperty("name").GetString());

            var timeline = PhotosControllerHarness.Body(await controller.PersonTimeline(a));
            Assert.Equal(new[] { alone, shared }.OrderBy(x => x),
                PhotosControllerHarness.ItemIds(timeline).OrderBy(x => x));
        }

        /// <summary>A suggestion is a question, not a tag: a person page must not claim photographs
        /// nobody has confirmed them into.</summary>
        [Fact]
        public async Task A_person_page_counts_only_what_a_human_agreed_to()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));

            await PhotoPersonTags.SuggestAsync(db, await IdOf("Vacation/one.jpg"), person, "c1", 0.9, null, null, null, null);
            await db.SaveChangesAsync();

            var detail = PhotosControllerHarness.Body(await controller.Person(person));
            Assert.Equal(0, PhotosControllerHarness.Int(detail, "tagCount"));
            Assert.Equal(1, PhotosControllerHarness.Int(detail, "suggestionCount"));
            Assert.Empty(PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await controller.PersonTimeline(person))));
        }

        /// <summary>The Phase 4 rule reaches the person page too: a member sees the curated set, an
        /// admin who asked sees everything.</summary>
        [Fact]
        public async Task A_person_page_hides_hidden_photos_from_a_member()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var member = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await member.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            var junk = await IdOf("Misc Pics/junk.jpg");

            await member.AddTags(new PhotoTagRequest { AssetIds = new List<int> { junk }, FamilyPersonId = person });
            await member.Hide(new PhotoHideRequest { Ids = new List<int> { junk }, Hidden = true });

            Assert.Empty(PhotosControllerHarness.ItemIds(PhotosControllerHarness.Body(
                await member.PersonTimeline(person, includeHidden: true))));

            var admin = PhotosControllerHarness.Build(fixture, db, admin: true, userId: 8);
            Assert.Contains(junk, PhotosControllerHarness.ItemIds(PhotosControllerHarness.Body(
                await admin.PersonTimeline(person, includeHidden: true))));
        }

        // ── The tag queue (§2.8) ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_untagged_lane_lists_photos_nobody_has_tagged_and_empties_as_they_are()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var before = PhotosControllerHarness.Body(await controller.TagQueue("untagged"));
            var beforeTotal = PhotosControllerHarness.Int(before, "total");
            Assert.True(beforeTotal > 0);

            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            await controller.AddTags(new PhotoTagRequest
            {
                AssetIds = new List<int> { await IdOf("Vacation/one.jpg") },
                FamilyPersonId = person,
            });

            var after = PhotosControllerHarness.Body(await controller.TagQueue("untagged"));
            Assert.Equal(beforeTotal - 1, PhotosControllerHarness.Int(after, "total"));
        }

        /// <summary>Refusing a machine's guess must not remove a photograph from the MANUAL queue: a
        /// human has still not said who is in it.</summary>
        [Fact]
        public async Task A_refused_suggestion_leaves_the_photo_in_the_untagged_lane()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A" })));
            var asset = await IdOf("Vacation/one.jpg");

            await PhotoPersonTags.SuggestAsync(db, asset, person, "c1", 0.9, null, null, null, null);
            await db.SaveChangesAsync();
            var tagId = await db.PhotoPersonTags.Select(t => t.Id).FirstAsync();

            // While it is a live suggestion the photo is in the suggestions lane and out of the manual one.
            Assert.Contains(asset, PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await controller.TagQueue("suggested"))));

            await controller.RejectTag(tagId);

            Assert.DoesNotContain(asset, PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await controller.TagQueue("suggested"))));
            Assert.Contains(asset, PhotosControllerHarness.ItemIds(
                PhotosControllerHarness.Body(await controller.TagQueue("untagged"))));
        }

        // ── Dates (§2.7) ────────────────────────────────────────────────────────────────────────

        /// <summary>Wall-clock in, wall-clock out (§2.7): no offset is applied, because EXIF carries
        /// none and "Christmas morning" must not land on December 24th.</summary>
        [Fact]
        public async Task Setting_an_exact_date_stores_it_as_typed_and_marks_it_manual()
        {
            BuildTree();
            await IngestAsync();
            var asset = await IdOf("Album Scans/undated.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var saved = PhotosControllerHarness.Body(await controller.SetAssetDate(asset,
                new PhotoDateRequest { TakenAt = "1987-12-25T07:30", TakenAtSet = true }));

            Assert.Equal("Manual", saved.GetProperty("takenAtSource").GetString());
            using var check = fixture.NewDb();
            var row = await check.PhotoAssets.SingleAsync(a => a.Id == asset);
            Assert.Equal(new DateTime(1987, 12, 25, 7, 30, 0), row.TakenAt);
            Assert.Null(row.TakenAtUtcRaw);
        }

        /// <summary>
        /// The Phase 1 addendum's rule, restated where a HUMAN can trigger it: a year is not a wall
        /// clock. Writing January 1st would pile a decade onto one day, wearing a more convincing date
        /// than the undated shelf it escaped.
        /// </summary>
        [Fact]
        public async Task Setting_a_circa_range_never_invents_an_exact_date()
        {
            BuildTree();
            await IngestAsync();
            var asset = await IdOf("Album Scans/undated.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var saved = PhotosControllerHarness.Body(await controller.SetAssetDate(asset,
                new PhotoDateRequest { YearMin = 1989, YearMax = 1986, YearsSet = true }));

            // Reversed bounds are corrected rather than refused: a human typing them backwards has still
            // said something perfectly clear.
            Assert.Equal(1986, saved.GetProperty("yearMin").GetInt32());
            Assert.Equal(1989, saved.GetProperty("yearMax").GetInt32());
            Assert.Equal(JsonValueKind.Null, saved.GetProperty("takenAt").ValueKind);
            Assert.Equal("Estimated", saved.GetProperty("takenAtSource").GetString());
        }

        /// <summary>A date, like a tag, attaches to the group master (§2.6).</summary>
        [Fact]
        public async Task Dating_a_duplicate_copy_writes_the_date_on_the_master()
        {
            BuildTree();
            await IngestAsync();
            await GroupDupesAsync();

            var a = await IdOf("Phone Backup/dupe.jpg");
            var b = await IdOf("Vacation/dupe.jpg");

            using var db = fixture.NewDb();
            var master = await PhotoDupeMasters.MasterForAsync(db, a);
            var nonMaster = master == a ? b : a;

            var controller = PhotosControllerHarness.Build(fixture, db);
            var saved = PhotosControllerHarness.Body(await controller.SetAssetDate(nonMaster,
                new PhotoDateRequest { TakenAt = "2012-01-02T08:00", TakenAtSet = true }));
            Assert.True(saved.GetProperty("redirected").GetBoolean());
            Assert.Equal(master, PhotosControllerHarness.Int(saved, "assetId"));
        }

        /// <summary>§2.7's birth-year hint: surfaced, never applied. The endpoint reports the bound the
        /// tagged people imply and writes nothing at all.</summary>
        [Fact]
        public async Task The_birth_year_hint_is_reported_and_never_written()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var person = PersonId(PhotosControllerHarness.Body(
                await controller.CreatePerson(new PhotoPersonRequest { Name = "Subject A", BirthYear = 2012 })));
            var asset = await IdOf("Album Scans/undated.jpg");
            await controller.AddTags(new PhotoTagRequest { AssetIds = new List<int> { asset }, FamilyPersonId = person });

            var tags = PhotosControllerHarness.Body(await controller.AssetTags(asset));
            Assert.Equal(2012, PhotosControllerHarness.Int(tags, "earliestYearHint"));

            using var check = fixture.NewDb();
            var row = await check.PhotoAssets.SingleAsync(a => a.Id == asset);
            Assert.Null(row.TakenAt);
            Assert.Null(row.YearMin);
        }
    }
}
