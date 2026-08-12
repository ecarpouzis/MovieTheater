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
    /// Albums (docs/photos-plan.md §2.9): curated DB rows, with the folder tree as a browse view and a
    /// SEED. What these pin down is that an album is rows and only rows — deleting one takes nothing
    /// with it but its own entries, seeding from a folder COPIES membership rather than binding the
    /// album to a path, and a slug (which is a link someone may have sent) survives a retitle.
    /// </summary>
    public class PhotoAlbumTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        private void BuildTree()
        {
            fixture.WriteJpeg("Wedding/w1.jpg", 640, 480, seed: 41, exifDateTimeOriginal: "2011:06:04 12:00:00");
            fixture.WriteJpeg("Wedding/w2.jpg", 640, 480, seed: 42, exifDateTimeOriginal: "2011:06:04 13:00:00");
            fixture.WriteJpeg("Wedding/Reception/w3.jpg", 640, 480, seed: 43, exifDateTimeOriginal: "2011:06:04 20:00:00");
            fixture.WriteJpeg("Wedding/hidden-one.jpg", 640, 480, seed: 44, exifDateTimeOriginal: "2011:06:04 21:00:00");
            fixture.WriteJpeg("Other/o1.jpg", 640, 480, seed: 45, exifDateTimeOriginal: "2012:01:01 10:00:00");
        }

        private async Task IngestAsync()
        {
            var pipeline = fixture.Pipeline(fixture.Options(batchSize: 50));
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
        }

        private async Task<Dictionary<string, int>> IdsAsync()
        {
            using var db = fixture.NewDb();
            return await db.PhotoAssets.ToDictionaryAsync(a => a.Path, a => a.Id);
        }

        private static int AlbumId(System.Text.Json.JsonElement body) =>
            body.GetProperty("album").GetProperty("id").GetInt32();

        private static string Slug(System.Text.Json.JsonElement body) =>
            body.GetProperty("album").GetProperty("slug").GetString()!;

        // ── Creating ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task An_album_is_created_with_a_server_minted_slug()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(
                new PhotoAlbumCreateRequest { Title = "Summer at the Lake, 1994!", Description = "the good one" }));

            Assert.Equal("summer-at-the-lake-1994", Slug(created));
            Assert.Equal("the good one", created.GetProperty("album").GetProperty("description").GetString());
        }

        [Fact]
        public async Task A_second_album_of_the_same_name_still_reads_like_one()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            var first = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Christmas" }));
            var second = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Christmas" }));

            Assert.Equal("christmas", Slug(first));
            Assert.Equal("christmas-2", Slug(second));
        }

        [Fact]
        public async Task An_album_needs_a_title()
        {
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            Assert.IsType<BadRequestObjectResult>(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "   " }));
        }

        [Fact]
        public async Task Create_from_selection_puts_the_selection_in_the_album()
        {
            BuildTree();
            await IngestAsync();
            var ids = await IdsAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest
            {
                Title = "Picked",
                AssetIds = new List<int> { ids["Wedding/w1.jpg"], ids["Other/o1.jpg"] },
            }));

            Assert.Equal(2, PhotosControllerHarness.Int(created, "added"));
            var detail = PhotosControllerHarness.Body(await controller.Album(Slug(created)));
            Assert.Equal(new[] { ids["Wedding/w1.jpg"], ids["Other/o1.jpg"] }, PhotosControllerHarness.ItemIds(detail));
        }

        [Fact]
        public async Task Make_an_album_from_this_folder_copies_the_whole_subtree_and_skips_hidden()
        {
            BuildTree();
            await IngestAsync();
            var ids = await IdsAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            // Curated out of browse before seeding: seeding must not quietly bring it back.
            await controller.Hide(new PhotoHideRequest { Ids = new List<int> { ids["Wedding/hidden-one.jpg"] }, Hidden = true });

            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(
                new PhotoAlbumCreateRequest { Title = "The Wedding", FromFolder = "Wedding" }));

            // Recursive: an event folder in this tree carries subfolders, and "make an album from this
            // folder" means the event.
            Assert.Equal(3, PhotosControllerHarness.Int(created, "added"));
            var members = PhotosControllerHarness.ItemIds(PhotosControllerHarness.Body(await controller.Album(Slug(created))));
            Assert.Contains(ids["Wedding/Reception/w3.jpg"], members);
            Assert.DoesNotContain(ids["Wedding/hidden-one.jpg"], members);
            Assert.DoesNotContain(ids["Other/o1.jpg"], members);
        }

        [Fact]
        public async Task A_seeded_album_survives_the_folder_being_reorganized()
        {
            BuildTree();
            await IngestAsync();
            var ids = await IdsAsync();

            using (var db = fixture.NewDb())
            {
                var controller = PhotosControllerHarness.Build(fixture, db);
                await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "The Wedding", FromFolder = "Wedding" });
            }

            // The folder is never the album's identity (§2.9) — the entries are rows against asset ids,
            // and the walk's move detection re-points the PATH on the same row (§2.5).
            fixture.Move("Wedding/w1.jpg", "2011 Wedding/w1.jpg");
            await fixture.Pipeline(fixture.Options(batchSize: 50)).RunAsync(PhotoIngestPass.Walk, null, 0);

            using var db2 = fixture.NewDb();
            var controller2 = PhotosControllerHarness.Build(fixture, db2);
            var members = PhotosControllerHarness.ItemIds(PhotosControllerHarness.Body(await controller2.Album("the-wedding")));
            Assert.Contains(ids["Wedding/w1.jpg"], members);
            Assert.Equal("2011 Wedding/w1.jpg",
                await db2.PhotoAssets.Where(a => a.Id == ids["Wedding/w1.jpg"]).Select(a => a.Path).FirstAsync());
        }

        // ── Membership ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Adding_the_same_photo_twice_is_a_no_op_not_a_second_row()
        {
            BuildTree();
            await IngestAsync();
            var ids = await IdsAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Set" }));
            var id = AlbumId(created);

            await controller.AddToAlbum(id, new PhotoAlbumMembershipRequest { AssetIds = new List<int> { ids["Wedding/w1.jpg"] } });
            var second = PhotosControllerHarness.Body(await controller.AddToAlbum(id,
                new PhotoAlbumMembershipRequest { AssetIds = new List<int> { ids["Wedding/w1.jpg"], ids["Wedding/w2.jpg"] } }));

            Assert.Equal(1, PhotosControllerHarness.Int(second, "added"));
            Assert.Equal(2, PhotosControllerHarness.Int(second, "total"));
        }

        [Fact]
        public async Task An_asset_id_that_does_not_exist_never_becomes_an_entry()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Set" }));

            var added = PhotosControllerHarness.Body(await controller.AddToAlbum(AlbumId(created),
                new PhotoAlbumMembershipRequest { AssetIds = new List<int> { 999999 } }));
            Assert.Equal(0, PhotosControllerHarness.Int(added, "added"));
        }

        [Fact]
        public async Task Removing_a_photo_removes_the_entry_and_never_the_asset()
        {
            BuildTree();
            await IngestAsync();
            var ids = await IdsAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest
            {
                Title = "Set",
                AssetIds = new List<int> { ids["Wedding/w1.jpg"], ids["Wedding/w2.jpg"] },
            }));

            var removed = PhotosControllerHarness.Body(await controller.RemoveFromAlbum(AlbumId(created),
                new PhotoAlbumMembershipRequest { AssetIds = new List<int> { ids["Wedding/w1.jpg"] } }));

            Assert.Equal(1, PhotosControllerHarness.Int(removed, "removed"));
            Assert.Equal(1, PhotosControllerHarness.Int(removed, "total"));
            Assert.NotNull(await db.PhotoAssets.FindAsync(ids["Wedding/w1.jpg"]));
        }

        [Fact]
        public async Task A_cover_must_be_one_of_the_albums_photos_and_is_dropped_if_it_leaves()
        {
            BuildTree();
            await IngestAsync();
            var ids = await IdsAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest
            {
                Title = "Set",
                AssetIds = new List<int> { ids["Wedding/w1.jpg"] },
            }));
            var albumId = AlbumId(created);

            Assert.IsType<BadRequestObjectResult>(await controller.UpdateAlbum(albumId,
                new PhotoAlbumUpdateRequest { CoverAssetId = ids["Other/o1.jpg"] }));

            var set = PhotosControllerHarness.Body(await controller.UpdateAlbum(albumId,
                new PhotoAlbumUpdateRequest { CoverAssetId = ids["Wedding/w1.jpg"] }));
            Assert.Equal(ids["Wedding/w1.jpg"], set.GetProperty("album").GetProperty("coverAssetId").GetInt32());

            await controller.RemoveFromAlbum(albumId, new PhotoAlbumMembershipRequest { AssetIds = new List<int> { ids["Wedding/w1.jpg"] } });
            var album = await db.PhotoAlbums.FirstAsync(a => a.Id == albumId);
            Assert.Null(album.CoverAssetId);
        }

        // ── Editing ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Retitling_never_re_mints_the_slug()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Trip" }));

            var updated = PhotosControllerHarness.Body(await controller.UpdateAlbum(AlbumId(created),
                new PhotoAlbumUpdateRequest { Title = "Trip to the Coast" }));

            // The slug is a link a family member may already have sent to another one.
            Assert.Equal("trip", Slug(updated));
            Assert.Equal("Trip to the Coast", updated.GetProperty("album").GetProperty("title").GetString());
        }

        [Fact]
        public async Task A_date_range_can_be_set_and_cleared_and_an_absent_field_is_left_alone()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Summer" }));
            var id = AlbumId(created);

            await controller.UpdateAlbum(id, new PhotoAlbumUpdateRequest
            {
                RangeStartSet = true, RangeStart = new DateTime(1994, 6, 1),
                RangeEndSet = true, RangeEnd = new DateTime(1994, 8, 31),
            });

            // A description-only edit must not silently erase the range someone typed.
            await controller.UpdateAlbum(id, new PhotoAlbumUpdateRequest { Description = "circa" });
            var album = await db.PhotoAlbums.FirstAsync(a => a.Id == id);
            Assert.Equal(new DateTime(1994, 6, 1), album.RangeStart);

            await controller.UpdateAlbum(id, new PhotoAlbumUpdateRequest { RangeStartSet = true, RangeStart = null });
            await db.Entry(album).ReloadAsync();
            Assert.Null(album.RangeStart);
            Assert.Equal(new DateTime(1994, 8, 31), album.RangeEnd);
        }

        // ── Reordering ──────────────────────────────────────────────────────────────────────────

        private async Task<(PhotosController controller, int albumId, Dictionary<string, int> ids)> AlbumOfThree(MovieDb db)
        {
            var ids = await IdsAsync();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest
            {
                Title = "Ordered",
                AssetIds = new List<int> { ids["Wedding/w1.jpg"], ids["Wedding/w2.jpg"], ids["Wedding/Reception/w3.jpg"] },
            }));
            return (controller, AlbumId(created), ids);
        }

        [Fact]
        public async Task Reorder_sets_the_order_given()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var (controller, albumId, ids) = await AlbumOfThree(db);

            await controller.ReorderAlbum(albumId, new PhotoAlbumMembershipRequest
            {
                AssetIds = new List<int> { ids["Wedding/Reception/w3.jpg"], ids["Wedding/w1.jpg"], ids["Wedding/w2.jpg"] },
            });

            var detail = PhotosControllerHarness.Body(await controller.Album("ordered"));
            Assert.Equal(
                new[] { ids["Wedding/Reception/w3.jpg"], ids["Wedding/w1.jpg"], ids["Wedding/w2.jpg"] },
                PhotosControllerHarness.ItemIds(detail));
        }

        [Fact]
        public async Task A_partial_reorder_moves_only_what_was_sent_and_keeps_the_rest_behind_it()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var (controller, albumId, ids) = await AlbumOfThree(db);

            // Dragging one card to the front is not a reason to send the whole album back.
            var result = PhotosControllerHarness.Body(await controller.ReorderAlbum(albumId,
                new PhotoAlbumMembershipRequest { AssetIds = new List<int> { ids["Wedding/Reception/w3.jpg"] } }));

            Assert.Equal(3, PhotosControllerHarness.Int(result, "ordered"));
            Assert.Equal(0, PhotosControllerHarness.Int(result, "ignored"));
            Assert.Equal(
                new[] { ids["Wedding/Reception/w3.jpg"], ids["Wedding/w1.jpg"], ids["Wedding/w2.jpg"] },
                PhotosControllerHarness.ItemIds(PhotosControllerHarness.Body(await controller.Album("ordered"))));
        }

        [Fact]
        public async Task Reorder_drops_duplicates_and_strangers_rather_than_failing()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var (controller, albumId, ids) = await AlbumOfThree(db);

            // A stale tab re-sending a photo someone else removed must not fail the whole reorder.
            var result = PhotosControllerHarness.Body(await controller.ReorderAlbum(albumId, new PhotoAlbumMembershipRequest
            {
                AssetIds = new List<int>
                {
                    ids["Wedding/w2.jpg"], ids["Wedding/w2.jpg"], ids["Other/o1.jpg"], 999999, ids["Wedding/w1.jpg"],
                },
            }));

            Assert.Equal(3, PhotosControllerHarness.Int(result, "ordered"));
            Assert.Equal(3, PhotosControllerHarness.Int(result, "ignored"));
            Assert.Equal(
                new[] { ids["Wedding/w2.jpg"], ids["Wedding/w1.jpg"], ids["Wedding/Reception/w3.jpg"] },
                PhotosControllerHarness.ItemIds(PhotosControllerHarness.Body(await controller.Album("ordered"))));
        }

        [Fact]
        public async Task Reordering_an_empty_album_is_harmless()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Empty" }));

            var result = PhotosControllerHarness.Body(await controller.ReorderAlbum(AlbumId(created),
                new PhotoAlbumMembershipRequest { AssetIds = new List<int> { 1, 2, 3 } }));
            Assert.Equal(0, PhotosControllerHarness.Int(result, "total"));
        }

        [Fact]
        public async Task An_album_nobody_reordered_reads_chronologically()
        {
            BuildTree();
            await IngestAsync();
            var ids = await IdsAsync();
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            // Added newest-first; with every entry at sort 0 the taken-date is what should decide.
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Day" }));
            var albumId = AlbumId(created);
            using (var seed = fixture.NewDb())
            {
                seed.PhotoAlbumEntries.Add(new PhotoAlbumEntry { PhotoAlbumId = albumId, PhotoAssetId = ids["Wedding/Reception/w3.jpg"], SortOrder = 0 });
                seed.PhotoAlbumEntries.Add(new PhotoAlbumEntry { PhotoAlbumId = albumId, PhotoAssetId = ids["Wedding/w1.jpg"], SortOrder = 0 });
                await seed.SaveChangesAsync();
            }

            Assert.Equal(
                new[] { ids["Wedding/w1.jpg"], ids["Wedding/Reception/w3.jpg"] },
                PhotosControllerHarness.ItemIds(PhotosControllerHarness.Body(await controller.Album("day"))));
        }

        // ── Deleting ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Deleting_an_album_needs_an_explicit_confirmation()
        {
            BuildTree();
            await IngestAsync();
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Doomed" }));

            Assert.IsType<BadRequestObjectResult>(await controller.DeleteAlbum(AlbumId(created), new PhotoAlbumDeleteRequest()));
            Assert.Equal(1, await db.PhotoAlbums.CountAsync());
        }

        [Fact]
        public async Task Deleting_an_album_removes_rows_and_nothing_else()
        {
            BuildTree();
            await IngestAsync();
            var ids = await IdsAsync();
            var before = fixture.MediaFilesOnDisk();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = PhotosControllerHarness.Body(await controller.CreateAlbum(new PhotoAlbumCreateRequest
            {
                Title = "Doomed",
                AssetIds = new List<int> { ids["Wedding/w1.jpg"], ids["Wedding/w2.jpg"] },
            }));

            var deleted = PhotosControllerHarness.Body(await controller.DeleteAlbum(AlbumId(created),
                new PhotoAlbumDeleteRequest { Confirm = true }));

            Assert.Equal(2, PhotosControllerHarness.Int(deleted, "entriesRemoved"));
            Assert.Equal(0, await db.PhotoAlbums.CountAsync());
            Assert.Equal(0, await db.PhotoAlbumEntries.CountAsync());
            // The assets and — checked independently — the files are untouched (§6).
            Assert.Equal(5, await db.PhotoAssets.CountAsync());
            Assert.Equal(before, fixture.MediaFilesOnDisk());
        }

        [Fact]
        public async Task The_album_index_shows_counts_and_falls_back_to_the_first_photo_for_a_cover()
        {
            BuildTree();
            await IngestAsync();
            var ids = await IdsAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            await controller.CreateAlbum(new PhotoAlbumCreateRequest
            {
                Title = "Two",
                AssetIds = new List<int> { ids["Wedding/w1.jpg"], ids["Wedding/w2.jpg"] },
            });

            var index = PhotosControllerHarness.Body(await controller.Albums());
            var album = index.GetProperty("albums").EnumerateArray().Single();
            Assert.Equal(2, album.GetProperty("count").GetInt32());
            // No gateway configured in the harness, so the cover URL is honestly null rather than a
            // broken image — the same degradation the browse surfaces already make.
            Assert.Equal(System.Text.Json.JsonValueKind.Null, album.GetProperty("coverUrl").ValueKind);
        }

        [Fact]
        public async Task Which_albums_a_photo_is_in_is_answerable_for_the_lightbox()
        {
            BuildTree();
            await IngestAsync();
            var ids = await IdsAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "A", AssetIds = new List<int> { ids["Wedding/w1.jpg"] } });
            await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "B", AssetIds = new List<int> { ids["Wedding/w1.jpg"] } });

            var body = PhotosControllerHarness.Body(await controller.AssetAlbums(ids["Wedding/w1.jpg"]));
            Assert.Equal(2, body.GetProperty("albums").GetArrayLength());
        }

        [Fact]
        public async Task A_missing_album_is_a_404_not_an_empty_page()
        {
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            Assert.IsType<NotFoundResult>(await controller.Album("no-such-album"));
        }

        // ── Slugs ───────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Wedding", "wedding")]
        [InlineData("Summer 1994", "summer-1994")]
        [InlineData("  Trip: Day 2!  ", "trip-day-2")]
        [InlineData("Café Days", "cafe-days")]     // accents fold rather than vanish
        [InlineData("A---B", "a-b")]
        [InlineData("!!!", "")]                     // nothing sluggable; Unique() supplies the fallback
        [InlineData("", "")]
        public void Slugs_are_readable_and_url_safe(string title, string expected)
        {
            Assert.Equal(expected, PhotoAlbumSlug.Make(title));
        }

        [Fact]
        public void A_title_with_nothing_sluggable_still_gets_a_key()
        {
            Assert.Equal("album", PhotoAlbumSlug.Unique("???", Array.Empty<string>()));
            Assert.Equal("album-2", PhotoAlbumSlug.Unique("???", new[] { "album" }));
        }
    }
}
