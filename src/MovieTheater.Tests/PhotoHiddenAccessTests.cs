using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MovieTheater.Controllers;
using MovieTheater.Db;
using MovieTheater.Core;
using MovieTheater.Photos;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The Phase 4 hidden rule on the BY-ID endpoints (docs/photos-plan.md, Phase 4 addendum: hidden is
    /// visible only to an admin, on EVERY surface).
    ///
    /// <para>The list surfaces were gated from the day the rule was written and are covered in
    /// <see cref="PhotoCurationTests"/>. This file is about the other half, which was not: every
    /// endpoint that takes an asset ID answered on any id it was handed, so a family member with the
    /// network tab open could read a hidden photograph's detail, learn who is in it, and mint an
    /// original-download capability for it — by counting. The gap is invisible from the UI, which is
    /// exactly why it needs tests rather than a rule stated in a comment.</para>
    ///
    /// <para>Each case is asserted from BOTH sides: a member is refused, and an admin is not. A
    /// one-sided assertion would also pass if the endpoint had simply been broken for everybody.</para>
    /// </summary>
    public class PhotoHiddenAccessTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        private const int AdminUserId = 8;

        private static int Seed(MovieDb db, string path, bool hidden,
            PhotoAssetKind kind = PhotoAssetKind.Photo, string? jellyfinItemId = null)
        {
            var row = new PhotoAsset
            {
                Path = path,
                SizeBytes = 4096,
                FileModifiedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Kind = kind,
                FirstSeenUtc = DateTime.UtcNow,
                Hidden = hidden,
                JellyfinItemId = jellyfinItemId,
                TakenAt = new DateTime(2011, 7, 4, 10, 0, 0),
                TakenAtSource = TakenAtSource.Exif,
                ThumbState = kind == PhotoAssetKind.Video ? PhotoThumbState.VideoDeferred : PhotoThumbState.Ready,
            };
            db.PhotoAssets.Add(row);
            db.SaveChanges();
            return row.Id;
        }

        private PhotosController Member(MovieDb db) => PhotosControllerHarness.Build(fixture, db);

        private PhotosController Admin(MovieDb db) =>
            PhotosControllerHarness.Build(fixture, db, admin: true, userId: AdminUserId);

        // ── Asset detail ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The headline case: <c>/API/Photos/Asset/{id}</c> handed a member the file name, the folder,
        /// the camera, the GPS fix and the whole EXIF panel of any hidden photograph, plus a download
        /// URL for the original — from an id that is a small integer.
        /// </summary>
        [Fact]
        public async Task A_member_cannot_read_a_hidden_assets_detail_but_an_admin_can()
        {
            using var db = fixture.NewDb();
            var hidden = Seed(db, "Screenshots/private.jpg", hidden: true);

            // A 404, not a 403: a refusal would confirm there is something there to refuse.
            Assert.IsType<NotFoundResult>(await Member(db).Asset(hidden));

            var body = PhotosControllerHarness.Body(await Admin(db).Asset(hidden));
            Assert.True(body.GetProperty("hidden").GetBoolean());
            Assert.Equal("private.jpg", body.GetProperty("fileName").GetString());
        }

        [Fact]
        public async Task A_visible_asset_is_still_readable_by_a_member()
        {
            using var db = fixture.NewDb();
            var visible = Seed(db, "Vacation/keep.jpg", hidden: false);
            var body = PhotosControllerHarness.Body(await Member(db).Asset(visible));
            Assert.Equal("keep.jpg", body.GetProperty("fileName").GetString());
        }

        // ── Capability minting ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>/API/Photos/Tokens</c> is a bulk RE-MINT for cards the caller already holds, so a hidden
        /// id is SKIPPED rather than refused — but it must be skipped. Sweeping "1,2,3,…" through it
        /// with <c>size=original</c> was a download link for every hidden photo in the collection.
        /// </summary>
        [Fact]
        public async Task Tokens_silently_skips_hidden_ids_for_a_member_and_mints_them_for_an_admin()
        {
            using var db = fixture.NewDb();
            var visible = Seed(db, "Vacation/keep.jpg", hidden: false);
            var hidden = Seed(db, "Screenshots/private.jpg", hidden: true);
            var ids = $"{visible},{hidden}";

            var member = PhotosControllerHarness.Body(
                await Member(db).Tokens(ids, PhotoStreamRoutes.SizeOriginal));
            var memberUrls = member.GetProperty("urls");
            Assert.True(memberUrls.TryGetProperty(visible.ToString(), out _));
            Assert.False(memberUrls.TryGetProperty(hidden.ToString(), out _));

            var admin = PhotosControllerHarness.Body(
                await Admin(db).Tokens(ids, PhotoStreamRoutes.SizeOriginal));
            Assert.True(admin.GetProperty("urls").TryGetProperty(hidden.ToString(), out _));
        }

        // ── The facts ABOUT a hidden photograph ─────────────────────────────────────────────────

        /// <summary>Who is in a hidden photograph is the most sensitive thing about it.</summary>
        [Fact]
        public async Task A_member_cannot_read_a_hidden_assets_people_tags()
        {
            using var db = fixture.NewDb();
            var hidden = Seed(db, "Screenshots/private.jpg", hidden: true);
            var person = new FamilyPerson { Name = "Subject A", CreatedUtc = DateTime.UtcNow };
            db.FamilyPeople.Add(person);
            db.SaveChanges();
            db.PhotoPersonTags.Add(new PhotoPersonTag
            {
                PhotoAssetId = hidden,
                FamilyPersonId = person.Id,
                Source = PhotoTagSource.Manual,
                CreatedUtc = DateTime.UtcNow,
            });
            db.SaveChanges();

            Assert.IsType<NotFoundResult>(await Member(db).AssetTags(hidden));

            var admin = PhotosControllerHarness.Body(await Admin(db).AssetTags(hidden));
            Assert.Single(admin.GetProperty("tags").EnumerateArray());
        }

        [Fact]
        public async Task A_member_cannot_read_which_albums_a_hidden_asset_is_in()
        {
            using var db = fixture.NewDb();
            var hidden = Seed(db, "Screenshots/private.jpg", hidden: true);

            Assert.IsType<NotFoundResult>(await Member(db).AssetAlbums(hidden));
            PhotosControllerHarness.Body(await Admin(db).AssetAlbums(hidden));
        }

        /// <summary>Hiding and unhiding stay member work — dating a photograph nobody may look at does
        /// not.</summary>
        [Fact]
        public async Task A_member_cannot_set_the_date_of_a_hidden_asset()
        {
            using var db = fixture.NewDb();
            var hidden = Seed(db, "Screenshots/private.jpg", hidden: true);

            Assert.IsType<NotFoundResult>(await Member(db).SetAssetDate(hidden,
                new PhotoDateRequest { TakenAt = "1999-01-01T00:00:00" }));

            PhotosControllerHarness.Body(await Admin(db).SetAssetDate(hidden,
                new PhotoDateRequest { TakenAt = "1999-01-01T00:00:00" }));
        }

        // ── Video (§2.3) ────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_member_cannot_start_a_hidden_video()
        {
            using var db = fixture.NewDb();
            var hidden = Seed(db, "Screenshots/private.mp4", hidden: true,
                kind: PhotoAssetKind.Video, jellyfinItemId: "item-1");

            var refused = Assert.IsType<NotFoundObjectResult>(
                await Member(db, new StandInPlayback()).StartVideo(new PhotoVideoStartRequest { AssetId = hidden }));
            // Word for word the answer a missing row gets: the refusal must not distinguish the two.
            Assert.Contains("No such item.", System.Text.Json.JsonSerializer.Serialize(refused.Value));

            var body = PhotosControllerHarness.Body(
                await Admin(db, new StandInPlayback()).StartVideo(new PhotoVideoStartRequest { AssetId = hidden }));
            Assert.Equal("https://gateway.invalid/s/token/Videos/item-1/stream.mp4", body.GetProperty("url").GetString());
        }

        private PhotosController Member(MovieDb db, IPhotoVideoPlayback playback) =>
            PhotosControllerHarness.Build(fixture, db, playback: playback);

        private PhotosController Admin(MovieDb db, IPhotoVideoPlayback playback) =>
            PhotosControllerHarness.Build(fixture, db, admin: true, userId: AdminUserId, playback: playback);

        // ── Duplicate groups (§2.6) ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A group's member view carries the copy's path, camera, size and a view-sized capability — the
        /// photograph by another route. Hiding one copy of a pair must take it out of the review list
        /// and out of the lightbox's "other copies" strip, for a member.
        /// </summary>
        [Fact]
        public async Task A_hidden_copy_is_not_listed_as_a_groups_member_for_a_member()
        {
            using var db = fixture.NewDb();
            var visible = Seed(db, "Vacation/keep.jpg", hidden: false);
            var hidden = Seed(db, "Phone Backup/keep.jpg", hidden: true);
            var group = new PhotoDupeGroup
            {
                Kind = PhotoDupeGroupKind.Near,
                Status = PhotoDupeGroupStatus.Pending,
                CreatedUtc = DateTime.UtcNow,
            };
            db.PhotoDupeGroups.Add(group);
            db.SaveChanges();
            db.PhotoDupeMembers.Add(new PhotoDupeMember { PhotoDupeGroupId = group.Id, PhotoAssetId = visible, IsMaster = true });
            db.PhotoDupeMembers.Add(new PhotoDupeMember { PhotoDupeGroupId = group.Id, PhotoAssetId = hidden, IsMaster = false });
            db.SaveChanges();

            var memberList = PhotosControllerHarness.Body(await Member(db).DupeGroups());
            var memberIds = MemberAssetIds(memberList);
            Assert.Contains(visible, memberIds);
            Assert.DoesNotContain(hidden, memberIds);

            var adminIds = MemberAssetIds(PhotosControllerHarness.Body(await Admin(db).DupeGroups()));
            Assert.Contains(hidden, adminIds);

            // And the same through the lightbox's own "other copies" strip.
            var detail = PhotosControllerHarness.Body(await Member(db).Asset(visible));
            var strip = detail.GetProperty("group").GetProperty("members").EnumerateArray()
                .Select(m => m.GetProperty("card").GetProperty("id").GetInt32()).ToList();
            Assert.DoesNotContain(hidden, strip);
        }

        private static List<int> MemberAssetIds(System.Text.Json.JsonElement body) =>
            body.GetProperty("groups").EnumerateArray()
                .SelectMany(g => g.GetProperty("members").EnumerateArray())
                .Select(m => m.GetProperty("card").GetProperty("id").GetInt32())
                .ToList();

        // ── The mint choke point ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A person's cover photo is minted from a row nobody filtered — so hiding the cover and then
        /// reading the people list handed the thumbnail straight back. Guarded at <c>ThumbUrl</c>, which
        /// is the one place every surface goes through, so a surface added later inherits the rule
        /// instead of having to remember it.
        /// </summary>
        [Fact]
        public async Task A_hidden_cover_photo_mints_no_thumbnail_for_a_member()
        {
            using var db = fixture.NewDb();
            var hidden = Seed(db, "Screenshots/private.jpg", hidden: true);
            var row = db.PhotoAssets.First(a => a.Id == hidden);
            row.ThumbKey = "abc123";
            row.ThumbVariants = PhotoStreamRoutes.SizeGrid;
            db.FamilyPeople.Add(new FamilyPerson
            {
                Name = "Subject A",
                CoverAssetId = hidden,
                CreatedUtc = DateTime.UtcNow,
            });
            db.SaveChanges();

            // The data plane is CONFIGURED for this one. Against the default harness the assertion
            // would hold for the wrong reason — an unconfigured host mints nothing for anybody.
            var member = PhotosControllerHarness.Build(fixture, db, dataPlane: true);
            var cover = PhotosControllerHarness.Body(await member.People())
                .GetProperty("people").EnumerateArray().First().GetProperty("coverUrl");
            Assert.Equal(System.Text.Json.JsonValueKind.Null, cover.ValueKind);

            var admin = PhotosControllerHarness.Build(fixture, db, admin: true, userId: AdminUserId, dataPlane: true);
            var adminCover = PhotosControllerHarness.Body(await admin.People())
                .GetProperty("people").EnumerateArray().First().GetProperty("coverUrl");
            Assert.Equal(System.Text.Json.JsonValueKind.String, adminCover.ValueKind);
        }

        // ── Status counts ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every number <c>/API/Photos/Status</c> reports, pinned against a hand-seeded database.
        ///
        /// <para>The endpoint answers each table with ONE grouped-aggregate query rather than a
        /// <c>CountAsync</c> per field — the UI re-fetches it after every curation write, and twenty
        /// sequential round trips to answer one question is what it used to cost. A conditional sum that
        /// is subtly wrong is invisible from every other test in this suite (a badge with the wrong
        /// number still renders), so the counts are asserted here, together, against known answers.</para>
        /// </summary>
        [Fact]
        public async Task Status_reports_every_count_correctly()
        {
            using var db = fixture.NewDb();
            Seed(db, "Vacation/keep.jpg", hidden: false);
            Seed(db, "Screenshots/private.jpg", hidden: true);
            var undated = Seed(db, "Album Scans/print.jpg", hidden: false);
            db.PhotoAssets.First(a => a.Id == undated).TakenAt = null;
            var gone = Seed(db, "Vacation/deleted.jpg", hidden: false);
            db.PhotoAssets.First(a => a.Id == gone).MissingSinceUtc = DateTime.UtcNow;
            Seed(db, "Vacation/clip.mp4", hidden: false, kind: PhotoAssetKind.Video, jellyfinItemId: "item-1");
            Seed(db, "Vacation/unsynced.mp4", hidden: false, kind: PhotoAssetKind.Video);
            db.FamilyPeople.Add(new FamilyPerson { Name = "Subject A", CreatedUtc = DateTime.UtcNow });
            db.FamilyPeople.Add(new FamilyPerson { Name = "", CreatedUtc = DateTime.UtcNow });
            db.SaveChanges();

            var body = PhotosControllerHarness.Body(await Admin(db).Status());

            Assert.Equal(6, PhotosControllerHarness.Int(body, "assets"));
            Assert.Equal(4, PhotosControllerHarness.Int(body, "photos"));
            Assert.Equal(2, PhotosControllerHarness.Int(body, "videos"));
            Assert.Equal(1, PhotosControllerHarness.Int(body, "missing"));
            Assert.Equal(1, PhotosControllerHarness.Int(body, "hidden"));
            // Undated counts only what a family would SEE waiting for a date: not hidden, not missing.
            Assert.Equal(1, PhotosControllerHarness.Int(body, "undated"));
            Assert.Equal(1, PhotosControllerHarness.Int(body, "videosSynced"));
            Assert.Equal(2, PhotosControllerHarness.Int(body, "people"));
            Assert.Equal(1, PhotosControllerHarness.Int(body, "namedPeople"));
            Assert.Equal(1, PhotosControllerHarness.Int(body, "unnamedFaceGroups"));
            Assert.False(body.GetProperty("empty").GetBoolean());
        }

        /// <summary>
        /// A host before its first ingest answers all zeros rather than failing.
        ///
        /// <para>Grouping by a constant produces NO ROWS over an empty table, so the natural spelling of
        /// a grouped aggregate throws or nulls on exactly the state this endpoint exists to report — and
        /// <c>empty = true</c> is what the page renders its "nothing here yet" panel from.</para>
        /// </summary>
        [Fact]
        public async Task Status_on_an_empty_collection_is_all_zeros_rather_than_a_failure()
        {
            using var db = fixture.NewDb();
            var body = PhotosControllerHarness.Body(await Member(db).Status());

            Assert.Equal(0, PhotosControllerHarness.Int(body, "assets"));
            Assert.Equal(0, PhotosControllerHarness.Int(body, "photos"));
            Assert.Equal(0, PhotosControllerHarness.Int(body, "people"));
            Assert.Equal(0, PhotosControllerHarness.Int(body, "pendingDupeGroups"));
            Assert.Equal(0, PhotosControllerHarness.Int(body, "googleItems"));
            Assert.True(body.GetProperty("empty").GetBoolean());
        }

        /// <summary>The minting seam, answered in-process. No Jellyfin, no gateway, no network.</summary>
        private sealed class StandInPlayback : IPhotoVideoPlayback
        {
            public bool Configured => true;

            public Task<PhotoVideoStartResult> StartAsync(int userId, string? userName, string jellyfinItemId,
                PhotoVideoStartRequest request, CancellationToken cancel = default) =>
                Task.FromResult(new PhotoVideoStartResult
                {
                    PlaySessionId = "session",
                    Url = $"https://gateway.invalid/s/token/Videos/{jellyfinItemId}/stream.mp4",
                    IsHls = false,
                    DirectPlay = true,
                    DurationTicks = 420_000_000,
                    VideoCodec = "h264",
                });
        }
    }
}
