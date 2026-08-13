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
    /// The Gallery shelf (docs/photos-plan.md §2.12).
    ///
    /// <para>What these pin down is the SHAPE of the exclusion, because every one of its edges is a
    /// place the feature could go wrong quietly. The timeline, the undated shelf and person pages drop
    /// the archive; the folder view and album pages deliberately do NOT, and an assertion that only
    /// checked the first three would pass just as happily against a build that had hidden the art
    /// instead of moving it — which is the exact mistake this phase exists to avoid. A settled duplicate
    /// group moves as a unit. Hidden still beats everything. And the CLI is idempotent, which is the
    /// only property that makes a 1,600-file rule safe to re-run.</para>
    /// </summary>
    public class PhotoShelfTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        // ── Fixture ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A miniature of the real tree the owner described: a family folder, and a "Misc Pics" pile
        /// with loose files at its root, per-artist subfolders below it, and a deeper tree of its own
        /// carrying one corner that must not reach a non-admin. The real run's command sequence is
        /// expressible against exactly this shape, which is the point of building it this way.
        /// </summary>
        private void BuildTree()
        {
            fixture.WriteJpeg("Family/f1.jpg", 640, 480, seed: 11, exifDateTimeOriginal: "2011:06:04 12:00:00");
            fixture.WriteJpeg("Family/f2.jpg", 640, 480, seed: 12, exifDateTimeOriginal: "2012:07:04 12:00:00");
            // No EXIF date — the undated shelf's inhabitant.
            fixture.WriteJpeg("Family/f3-undated.jpg", 640, 480, seed: 13);

            // Loose at the root of the pile.
            fixture.WriteJpeg("Misc Pics/loose-1.jpg", 400, 300, seed: 21);
            fixture.WriteJpeg("Misc Pics/loose-2.jpg", 400, 300, seed: 22);

            // The per-artist subfolders.
            fixture.WriteJpeg("Misc Pics/Misc Pics/Beksinski/b1.jpg", 400, 300, seed: 31);
            fixture.WriteJpeg("Misc Pics/Misc Pics/Beksinski/b2.jpg", 400, 300, seed: 32);
            fixture.WriteJpeg("Misc Pics/Misc Pics/Misc/m1.jpg", 400, 300, seed: 33);

            // The deep tree, with the corner that also needs hiding.
            fixture.WriteJpeg("Misc Pics/SAMisc/sa1.jpg", 400, 300, seed: 41);
            fixture.WriteJpeg("Misc Pics/SAMisc/sa2.jpg", 400, 300, seed: 42);
            fixture.WriteJpeg("Misc Pics/SAMisc/NWS/nws1.jpg", 400, 300, seed: 43);
        }

        private async Task IngestAsync(bool hash = false)
        {
            var pipeline = fixture.Pipeline(fixture.Options(batchSize: 50));
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            if (hash) await pipeline.RunAsync(PhotoIngestPass.Hash, null, 0);
        }

        private async Task<int> IdOf(string path)
        {
            using var db = fixture.NewDb();
            return await db.PhotoAssets.Where(a => a.Path == path).Select(a => a.Id).FirstAsync();
        }

        /// <summary>Files a set of paths onto a shelf directly. Used where the test is about the QUERY
        /// rather than about how the rows got that way — the CLI has its own section below.</summary>
        private async Task ShelveAsync(PhotoShelf shelf, params string[] paths)
        {
            using var db = fixture.NewDb();
            var rows = await db.PhotoAssets.Where(a => paths.Contains(a.Path)).ToListAsync();
            Assert.Equal(paths.Length, rows.Count);
            foreach (var row in rows) row.Shelf = shelf;
            await db.SaveChangesAsync();
        }

        private static List<string> Paths(JsonElement body) =>
            body.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("path").GetString()!)
                .ToList();

        private static JsonElement Read(Microsoft.AspNetCore.Mvc.IActionResult result) =>
            PhotosControllerHarness.Body(result);

        // ── The exclusion, and its exact edges ──────────────────────────────────────────────────

        [Fact]
        public async Task The_timeline_leaves_the_gallery_shelf_out()
        {
            BuildTree();
            await IngestAsync();
            await ShelveAsync(PhotoShelf.Archive, "Misc Pics/loose-1.jpg", "Misc Pics/Misc Pics/Beksinski/b1.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var body = Read(await controller.Timeline());

            var paths = Paths(body);
            Assert.Contains("Family/f1.jpg", paths);
            Assert.DoesNotContain("Misc Pics/loose-1.jpg", paths);
            Assert.DoesNotContain("Misc Pics/Misc Pics/Beksinski/b1.jpg", paths);
            // The pile that was NOT filed is still on the timeline — the exclusion is the shelf, not
            // the folder name, and nothing here guesses. (It is undated, like all of this art, so it
            // is the undated shelf it is still on.)
            var undated = Paths(Read(await controller.Timeline(undated: true)));
            Assert.Contains("Misc Pics/loose-2.jpg", undated);
        }

        [Fact]
        public async Task The_undated_shelf_leaves_the_gallery_shelf_out()
        {
            BuildTree();
            await IngestAsync();
            // The art has no EXIF dates either, so without the shelf filter it would ALL land here —
            // which is what makes the undated shelf the surface most likely to leak this.
            await ShelveAsync(PhotoShelf.Archive, "Misc Pics/loose-1.jpg", "Misc Pics/loose-2.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var paths = Paths(Read(await controller.Timeline(undated: true)));

            Assert.Contains("Family/f3-undated.jpg", paths);
            Assert.DoesNotContain("Misc Pics/loose-1.jpg", paths);
            Assert.DoesNotContain("Misc Pics/loose-2.jpg", paths);
        }

        [Fact]
        public async Task The_folder_view_still_shows_the_gallery_shelf_and_marks_it()
        {
            BuildTree();
            await IngestAsync();
            await ShelveAsync(PhotoShelf.Archive, "Misc Pics/loose-1.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var body = Read(await controller.Folders("Misc Pics"));

            var paths = Paths(body);
            // The "what is actually on disk" surface shows both shelves. If this ever starts filtering,
            // the Gallery has silently become a second hide list.
            Assert.Contains("Misc Pics/loose-1.jpg", paths);
            Assert.Contains("Misc Pics/loose-2.jpg", paths);

            var cards = body.GetProperty("items").EnumerateArray()
                .ToDictionary(i => i.GetProperty("path").GetString()!, i => i.GetProperty("shelf").GetString()!);
            Assert.Equal("Archive", cards["Misc Pics/loose-1.jpg"]);
            Assert.Equal("Timeline", cards["Misc Pics/loose-2.jpg"]);
        }

        [Fact]
        public async Task An_album_shows_its_gallery_assets_to_an_ordinary_member()
        {
            BuildTree();
            await IngestAsync();
            await ShelveAsync(PhotoShelf.Archive,
                "Misc Pics/Misc Pics/Beksinski/b1.jpg", "Misc Pics/Misc Pics/Beksinski/b2.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = Read(await controller.CreateAlbum(new PhotoAlbumCreateRequest
            {
                Title = "Beksinski",
                AssetIds = new List<int>
                {
                    await IdOf("Misc Pics/Misc Pics/Beksinski/b1.jpg"),
                    await IdOf("Misc Pics/Misc Pics/Beksinski/b2.jpg"),
                },
            }));
            var slug = created.GetProperty("album").GetProperty("slug").GetString()!;

            var body = Read(await controller.Album(slug));
            var paths = body.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("card").GetProperty("path").GetString()!)
                .ToList();

            // The whole reason the Gallery is a section rather than a longer hide list: a member who
            // opens the collection sees the artwork.
            Assert.Equal(2, paths.Count);
            Assert.Contains("Misc Pics/Misc Pics/Beksinski/b1.jpg", paths);
        }

        [Fact]
        public async Task A_person_page_leaves_the_gallery_out_but_says_how_much_it_left()
        {
            BuildTree();
            await IngestAsync();

            using (var db = fixture.NewDb())
            {
                var controller = PhotosControllerHarness.Build(fixture, db);
                var person = Read(await controller.CreatePerson(new PhotoPersonRequest { Name = "Someone" }));
                var personId = person.GetProperty("person").GetProperty("id").GetInt32();
                await controller.AddTags(new PhotoTagRequest
                {
                    FamilyPersonId = personId,
                    AssetIds = new List<int>
                    {
                        await IdOf("Family/f1.jpg"),
                        await IdOf("Misc Pics/loose-1.jpg"),
                        await IdOf("Misc Pics/loose-2.jpg"),
                    },
                });
            }

            await ShelveAsync(PhotoShelf.Archive, "Misc Pics/loose-1.jpg", "Misc Pics/loose-2.jpg");

            using var db2 = fixture.NewDb();
            var controller2 = PhotosControllerHarness.Build(fixture, db2);
            var personId2 = await db2.FamilyPeople.Select(p => p.Id).FirstAsync();
            var body = Read(await controller2.PersonTimeline(personId2));

            Assert.Equal(new List<string> { "Family/f1.jpg" }, Paths(body));
            Assert.Equal(1, body.GetProperty("total").GetInt32());
            // …and the count-chip, so the omission is visible. An exclusion nobody can see is
            // indistinguishable from data loss, and this one has no checkbox to reveal it.
            Assert.Equal(2, body.GetProperty("archived").GetInt32());
        }

        [Fact]
        public async Task Hidden_still_beats_the_shelf_for_a_non_admin()
        {
            BuildTree();
            await IngestAsync();
            await ShelveAsync(PhotoShelf.Archive, "Misc Pics/SAMisc/NWS/nws1.jpg");

            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.FirstAsync(a => a.Path == "Misc Pics/SAMisc/NWS/nws1.jpg");
                row.Hidden = true;
                await db.SaveChangesAsync();
            }

            var id = await IdOf("Misc Pics/SAMisc/NWS/nws1.jpg");

            using var db2 = fixture.NewDb();
            // A member sees it nowhere — not in the folder view it would otherwise appear in, and not
            // by id. The two flags compose and Hidden wins.
            var member = PhotosControllerHarness.Build(fixture, db2);
            Assert.DoesNotContain("Misc Pics/SAMisc/NWS/nws1.jpg", Paths(Read(await member.Folders("Misc Pics/SAMisc/NWS"))));
            Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await member.Asset(id));

            using var db3 = fixture.NewDb();
            var admin = PhotosControllerHarness.Build(fixture, db3, admin: true);
            var adminBody = Read(await admin.Folders("Misc Pics/SAMisc/NWS", includeHidden: true));
            Assert.Contains("Misc Pics/SAMisc/NWS/nws1.jpg", Paths(adminBody));
        }

        // ── Group-coherent moves ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Shelving_one_copy_shelves_the_whole_settled_group()
        {
            BuildTree();
            // Byte-identical twins in two folders — the exact-dupe case, which settles without a human.
            fixture.WriteJpeg("Misc Pics/twin.jpg", 400, 300, seed: 77);
            fixture.WriteJpeg("Family/twin.jpg", 400, 300, seed: 77);
            await IngestAsync(hash: true);
            await new PhotoDupePass(fixture.NewDb, new PhotoDupeOptions { BatchSize = 100 }, _ => { })
                .RunAsync(PhotoDupePassKind.Exact, null, 0);

            using (var check = fixture.NewDb())
                Assert.Equal(2, await check.PhotoDupeMembers.CountAsync());

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            // The member selects the copy the FOLDER view offered them, which may well be the
            // non-master — the case where a non-coherent move would visibly do nothing.
            var body = Read(await controller.Shelf(new PhotoShelfRequest
            {
                Ids = new List<int> { await IdOf("Misc Pics/twin.jpg") },
                Shelf = "Archive",
            }));

            Assert.Equal(1, body.GetProperty("requested").GetInt32());
            Assert.Equal(2, body.GetProperty("changed").GetInt32());
            Assert.Equal(1, body.GetProperty("groupMembersIncluded").GetInt32());

            using var after = fixture.NewDb();
            Assert.Equal(2, await after.PhotoAssets.CountAsync(a => a.Shelf == PhotoShelf.Archive));
        }

        [Fact]
        public async Task An_unsettled_near_group_is_not_dragged_along()
        {
            BuildTree();
            // The same picture at two sizes: a NEAR pair, which stays Pending because nobody has agreed
            // those are one photograph. Sweeping a family's scans into the Gallery on a hash's say-so is
            // exactly what the dupe review UI exists to prevent.
            fixture.WriteJpeg("Misc Pics/scan.jpg", 256, 192, seed: 91);
            fixture.WriteJpegScaled("Family/scan-small.jpg", 0.5, 256, 192, seed: 91);
            await IngestAsync(hash: true);
            await new PhotoDupePass(fixture.NewDb, new PhotoDupeOptions { BatchSize = 100 }, _ => { })
                .RunAsync(PhotoDupePassKind.Near, null, 0);

            using (var check = fixture.NewDb())
            {
                var group = await check.PhotoDupeGroups.FirstOrDefaultAsync();
                Assert.NotNull(group);
                Assert.Equal(PhotoDupeGroupStatus.Pending, group!.Status);
                Assert.Equal(PhotoDupeGroupKind.Near, group.Kind);
            }

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var body = Read(await controller.Shelf(new PhotoShelfRequest
            {
                Ids = new List<int> { await IdOf("Misc Pics/scan.jpg") },
                Shelf = "Archive",
            }));

            Assert.Equal(1, body.GetProperty("changed").GetInt32());
            Assert.Equal(0, body.GetProperty("groupMembersIncluded").GetInt32());

            using var after = fixture.NewDb();
            Assert.Equal(PhotoShelf.Timeline,
                await after.PhotoAssets.Where(a => a.Path == "Family/scan-small.jpg").Select(a => a.Shelf).FirstAsync());
        }

        [Fact]
        public async Task A_move_is_reversible_and_a_repeat_changes_nothing()
        {
            BuildTree();
            await IngestAsync();
            var id = await IdOf("Misc Pics/loose-1.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var ids = new List<int> { id };

            Assert.Equal(1, Read(await controller.Shelf(new PhotoShelfRequest { Ids = ids, Shelf = "Archive" }))
                .GetProperty("changed").GetInt32());
            // Idempotent: the second press of the same button is not a second edit.
            Assert.Equal(0, Read(await controller.Shelf(new PhotoShelfRequest { Ids = ids, Shelf = "Archive" }))
                .GetProperty("changed").GetInt32());
            Assert.Equal(1, Read(await controller.Shelf(new PhotoShelfRequest { Ids = ids, Shelf = "Timeline" }))
                .GetProperty("changed").GetInt32());

            using var after = fixture.NewDb();
            Assert.Equal(0, await after.PhotoAssets.CountAsync(a => a.Shelf == PhotoShelf.Archive));
        }

        [Fact]
        public async Task An_unknown_shelf_is_refused_rather_than_defaulted()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            // Binding this to 0 would mean "put it back on the family timeline" — the one wrong answer
            // that looks like success.
            var result = await controller.Shelf(new PhotoShelfRequest
            {
                Ids = new List<int> { await IdOf("Family/f1.jpg") },
                Shelf = "Attic",
            });
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        }

        // ── The two album indexes ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_family_index_and_the_gallery_index_are_disjoint_and_artists_lead()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);

            async Task<int> MakeAsync(string title, PhotoShelf shelf, string? artist)
            {
                var created = Read(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = title }));
                var albumId = created.GetProperty("album").GetProperty("id").GetInt32();
                if (shelf != PhotoShelf.Timeline || artist != null)
                {
                    await controller.UpdateAlbum(albumId, new PhotoAlbumUpdateRequest
                    {
                        Shelf = shelf.ToString(),
                        ArtistName = artist,
                        ArtistNameSet = artist != null,
                    });
                }
                return albumId;
            }

            await MakeAsync("Wedding", PhotoShelf.Timeline, null);
            await MakeAsync("SA Misc", PhotoShelf.Archive, null);
            await MakeAsync("Beksinski", PhotoShelf.Archive, "Beksinski");

            var family = Read(await controller.Albums()).GetProperty("albums").EnumerateArray()
                .Select(a => a.GetProperty("title").GetString()!).ToList();
            Assert.Equal(new List<string> { "Wedding" }, family);

            var gallery = Read(await controller.Gallery()).GetProperty("albums").EnumerateArray().ToList();
            // Artist collections lead, then the plain piles — the ordering is the server's, so the
            // page cannot disagree with it.
            Assert.Equal(new List<string> { "Beksinski", "SA Misc" },
                gallery.Select(a => a.GetProperty("title").GetString()!).ToList());
            Assert.Equal("Beksinski", gallery[0].GetProperty("artistName").GetString());
            Assert.True(gallery[1].GetProperty("artistName").ValueKind == JsonValueKind.Null);
        }

        [Fact]
        public async Task A_gallery_album_keeps_the_ordinary_album_url()
        {
            BuildTree();
            await IngestAsync();

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var created = Read(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Brom" }));
            var albumId = created.GetProperty("album").GetProperty("id").GetInt32();
            var slug = created.GetProperty("album").GetProperty("slug").GetString()!;

            await controller.UpdateAlbum(albumId, new PhotoAlbumUpdateRequest
            {
                Shelf = "Archive",
                ArtistName = "Brom",
                ArtistNameSet = true,
            });

            // Same URL before and after the move: a link a family member sent last year still opens.
            var body = Read(await controller.Album(slug));
            Assert.Equal("Archive", body.GetProperty("album").GetProperty("shelf").GetString());
            Assert.Equal("Brom", body.GetProperty("album").GetProperty("artistName").GetString());
        }

        [Fact]
        public async Task Status_counts_the_gallery_separately_from_the_family_album()
        {
            BuildTree();
            await IngestAsync();
            await ShelveAsync(PhotoShelf.Archive, "Misc Pics/loose-1.jpg", "Misc Pics/loose-2.jpg");

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db);
            var wedding = Read(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Wedding" }));
            var art = Read(await controller.CreateAlbum(new PhotoAlbumCreateRequest { Title = "Beksinski" }));
            await controller.UpdateAlbum(art.GetProperty("album").GetProperty("id").GetInt32(),
                new PhotoAlbumUpdateRequest { Shelf = "Archive", ArtistName = "Beksinski", ArtistNameSet = true });
            Assert.True(wedding.GetProperty("album").GetProperty("id").GetInt32() > 0);

            var status = Read(await controller.Status());
            Assert.Equal(2, status.GetProperty("archived").GetInt32());
            // The two album numbers ADD UP to the table rather than double-counting part of it.
            Assert.Equal(1, status.GetProperty("albums").GetInt32());
            Assert.Equal(1, status.GetProperty("archiveAlbums").GetInt32());
            Assert.Equal(1, status.GetProperty("artistCollections").GetInt32());
            // Undated now means "undated on the FAMILY TIMELINE". Nine files in the tree carry no EXIF
            // date; the two that were filed as art stop counting, leaving seven.
            Assert.Equal(7, status.GetProperty("undated").GetInt32());
        }

        // ── The CLI ─────────────────────────────────────────────────────────────────────────────

        private PhotoShelfPass Pass(PhotoShelfPass.Options options, List<string>? log = null) =>
            new PhotoShelfPass(fixture.NewDb, options, 500, line => log?.Add(line));

        private static PhotoShelfPass.Options Rule(string prefix, params string[] excludes) =>
            new PhotoShelfPass.Options
            {
                PathPrefix = prefix,
                ExcludePrefixes = excludes.ToList(),
                Shelf = PhotoShelf.Archive,
            };

        [Fact]
        public async Task The_pass_files_a_prefix_and_honours_every_exclusion()
        {
            BuildTree();
            await IngestAsync();

            // The real run's hardest rule: "the loose files at the top of Misc Pics, but neither of the
            // deep trees under it". If exclusions were applied as anything but an AND of NOTs, this is
            // where it shows.
            var result = await Pass(Rule("Misc Pics", "Misc Pics/Misc Pics", "Misc Pics/SAMisc")).RunAsync(null, 0);

            Assert.Equal(2, result.Processed);
            Assert.Equal(2, result.Counts["shelved"]);

            using var db = fixture.NewDb();
            var archived = await db.PhotoAssets.Where(a => a.Shelf == PhotoShelf.Archive)
                .Select(a => a.Path).OrderBy(p => p).ToListAsync();
            Assert.Equal(new List<string> { "Misc Pics/loose-1.jpg", "Misc Pics/loose-2.jpg" }, archived);
        }

        [Fact]
        public async Task The_pass_is_idempotent()
        {
            BuildTree();
            await IngestAsync();

            var options = Rule("Misc Pics/SAMisc");
            options.AlbumTitle = "SA Misc";
            var first = await Pass(options).RunAsync(null, 0);
            Assert.Equal(3, first.Counts["shelved"]);
            Assert.Equal(3, first.Counts["album-entries-added"]);
            Assert.Equal(1, first.Counts["album-created"]);

            var second = await Pass(options).RunAsync(null, 0);
            // Same matches, no edits: every counter that represents a WRITE is absent, and `already`
            // accounts for all of them. This is the property that makes the rule safe to re-run after
            // a killed invocation.
            Assert.Equal(3, second.Processed);
            Assert.Equal(3, second.Counts["already"]);
            Assert.False(second.Counts.ContainsKey("shelved"));
            Assert.False(second.Counts.ContainsKey("album-entries-added"));
            Assert.False(second.Counts.ContainsKey("album-created"));
            Assert.Equal(1, second.Counts["album-found"]);

            using var db = fixture.NewDb();
            Assert.Equal(1, await db.PhotoAlbums.CountAsync());
            Assert.Equal(3, await db.PhotoAlbumEntries.CountAsync());
        }

        [Fact]
        public async Task The_pass_creates_an_artist_collection_and_re_asserts_it()
        {
            BuildTree();
            await IngestAsync();

            var options = Rule("Misc Pics/Misc Pics/Beksinski");
            options.AlbumTitle = "Beksinski";
            options.ArtistName = "Beksinski";
            await Pass(options).RunAsync(null, 0);

            using (var db = fixture.NewDb())
            {
                var album = await db.PhotoAlbums.SingleAsync();
                Assert.Equal("Beksinski", album.ArtistName);
                Assert.Equal(PhotoShelf.Archive, album.Shelf);
                Assert.Equal("beksinski", album.Slug);
            }

            // Re-running mints no second album and no second slug — the album is found by TITLE, which
            // is what the operator types again, rather than by the slug it produced.
            var second = await Pass(options).RunAsync(null, 0);
            Assert.Equal(1, second.Counts["album-found"]);

            using var after = fixture.NewDb();
            Assert.Equal(1, await after.PhotoAlbums.CountAsync());
            Assert.Equal(2, await after.PhotoAlbumEntries.CountAsync());
        }

        [Fact]
        public async Task Adding_an_artist_to_a_rule_that_already_ran_is_a_correction()
        {
            BuildTree();
            await IngestAsync();

            var plain = Rule("Misc Pics/Misc Pics/Beksinski");
            plain.AlbumTitle = "Beksinski";
            await Pass(plain).RunAsync(null, 0);

            var withArtist = Rule("Misc Pics/Misc Pics/Beksinski");
            withArtist.AlbumTitle = "Beksinski";
            withArtist.ArtistName = "Beksinski";
            var result = await Pass(withArtist).RunAsync(null, 0);

            Assert.Equal(1, result.Counts["album-updated"]);
            using var db = fixture.NewDb();
            Assert.Equal("Beksinski", (await db.PhotoAlbums.SingleAsync()).ArtistName);
        }

        [Fact]
        public async Task The_hide_flag_composes_with_the_shelf()
        {
            BuildTree();
            await IngestAsync();

            var options = Rule("Misc Pics/SAMisc/NWS");
            options.Hide = true;
            var result = await Pass(options).RunAsync(null, 0);

            Assert.Equal(1, result.Counts["shelved"]);
            Assert.Equal(1, result.Counts["hidden"]);

            using var db = fixture.NewDb();
            var row = await db.PhotoAssets.FirstAsync(a => a.Path == "Misc Pics/SAMisc/NWS/nws1.jpg");
            // Two statements, not one conflated flag: off the family record AND not for a non-admin.
            Assert.Equal(PhotoShelf.Archive, row.Shelf);
            Assert.True(row.Hidden);

            // …and re-running hides nothing further.
            Assert.False((await Pass(options).RunAsync(null, 0)).Counts.ContainsKey("hidden"));
        }

        [Fact]
        public async Task A_dry_run_reports_the_real_numbers_and_writes_nothing()
        {
            BuildTree();
            await IngestAsync();

            var options = Rule("Misc Pics/SAMisc");
            options.AlbumTitle = "SA Misc";
            options.DryRun = true;
            var result = await Pass(options).RunAsync(null, 0);

            Assert.Equal(3, result.Counts["shelved"]);
            Assert.Equal(3, result.Counts["album-entries-added"]);
            Assert.Equal(1, result.Counts["album-would-create"]);

            using var db = fixture.NewDb();
            Assert.Equal(0, await db.PhotoAssets.CountAsync(a => a.Shelf == PhotoShelf.Archive));
            Assert.Equal(0, await db.PhotoAlbums.CountAsync());
        }

        [Fact]
        public async Task The_pass_chunks_and_resumes_on_its_cursor()
        {
            BuildTree();
            await IngestAsync();

            var log = new List<string>();
            var options = Rule("Misc Pics");
            // Two rows per batch over eight matches: the cursor has to carry across four of them.
            var pass = new PhotoShelfPass(fixture.NewDb, options, 2, line => log.Add(line));

            var first = await pass.RunAsync(null, 1);
            Assert.Equal(2, first.Processed);
            Assert.True(first.Remaining > 0);
            Assert.Contains("processed: 2", log[0]);

            // Resumed in a NOTIONALLY NEW PROCESS, from the printed cursor and nothing else.
            var rest = await new PhotoShelfPass(fixture.NewDb, options, 2, _ => { })
                .RunAsync(first.NextCursor, 0);
            Assert.Equal(0, rest.Remaining);

            using var db = fixture.NewDb();
            // Eight files live under "Misc Pics/", and all eight were filed across the two invocations.
            Assert.Equal(8, await db.PhotoAssets.CountAsync(a => a.Shelf == PhotoShelf.Archive));
        }

        [Fact]
        public async Task The_pass_can_send_a_subtree_back_to_the_timeline()
        {
            BuildTree();
            await IngestAsync();

            await Pass(Rule("Misc Pics")).RunAsync(null, 0);

            var back = Rule("Misc Pics/SAMisc");
            back.Shelf = PhotoShelf.Timeline;
            var result = await Pass(back).RunAsync(null, 0);
            Assert.Equal(3, result.Counts["shelved"]);

            using var db = fixture.NewDb();
            // Eight were filed; the three under SAMisc came back.
            Assert.Equal(5, await db.PhotoAssets.CountAsync(a => a.Shelf == PhotoShelf.Archive));
        }

        [Fact]
        public async Task The_real_run_sequence_leaves_the_timeline_shorter_and_the_folder_view_unchanged()
        {
            BuildTree();
            await IngestAsync();

            int FolderCount(JsonElement body) => body.GetProperty("total").GetInt32();

            int beforeTimeline, beforeFolder;
            using (var db = fixture.NewDb())
            {
                var controller = PhotosControllerHarness.Build(fixture, db);
                beforeTimeline = Paths(Read(await controller.Timeline(take: 400))).Count
                                 + Paths(Read(await controller.Timeline(take: 400, undated: true))).Count;
                beforeFolder = FolderCount(Read(await controller.Folders("Misc Pics")));
            }

            // The owner's sequence, in miniature.
            var beksinski = Rule("Misc Pics/Misc Pics/Beksinski");
            beksinski.AlbumTitle = "Beksinski";
            beksinski.ArtistName = "Beksinski";
            await Pass(beksinski).RunAsync(null, 0);

            var miscArt = Rule("Misc Pics/Misc Pics/Misc");
            miscArt.AlbumTitle = "Misc Art";
            await Pass(miscArt).RunAsync(null, 0);

            var saMisc = Rule("Misc Pics/SAMisc");
            saMisc.AlbumTitle = "SA Misc";
            await Pass(saMisc).RunAsync(null, 0);

            var loose = Rule("Misc Pics", "Misc Pics/Misc Pics", "Misc Pics/SAMisc");
            loose.AlbumTitle = "Misc Pics";
            await Pass(loose).RunAsync(null, 0);

            var nws = Rule("Misc Pics/SAMisc/NWS");
            nws.Hide = true;
            await Pass(nws).RunAsync(null, 0);

            using var after = fixture.NewDb();
            var controller2 = PhotosControllerHarness.Build(fixture, after);
            var afterTimeline = Paths(Read(await controller2.Timeline(take: 400))).Count
                                + Paths(Read(await controller2.Timeline(take: 400, undated: true))).Count;
            var afterFolder = FolderCount(Read(await controller2.Folders("Misc Pics")));

            // The point of the whole phase, as two numbers. The family record got shorter by exactly
            // the eight pictures that were filed as art…
            Assert.Equal(11, beforeTimeline);
            Assert.Equal(3, afterTimeline);
            // …and the folder view — the "what is actually on disk" surface — did not move at all.
            Assert.Equal(2, beforeFolder);
            Assert.Equal(beforeFolder, afterFolder);

            // The one corner that ALSO got hidden is the exception, and it is hidden rather than gone:
            // a member sees nothing there, an admin asking for it sees the file.
            Assert.Equal(0, FolderCount(Read(await controller2.Folders("Misc Pics/SAMisc/NWS"))));
            using var adminDb = fixture.NewDb();
            var adminController = PhotosControllerHarness.Build(fixture, adminDb, admin: true);
            Assert.Equal(1, FolderCount(Read(await adminController.Folders("Misc Pics/SAMisc/NWS", includeHidden: true))));

            Assert.Equal(4, await after.PhotoAlbums.CountAsync(a => a.Shelf == PhotoShelf.Archive));
            Assert.Equal(1, await after.PhotoAlbums.CountAsync(a => a.ArtistName == "Beksinski"));
        }

        // ── Export round-trip (§2.11) ───────────────────────────────────────────────────────────

        [Fact]
        public async Task An_export_carries_the_shelf_and_the_artist_into_a_rebuilt_database()
        {
            BuildTree();
            await IngestAsync();

            var beksinski = Rule("Misc Pics/Misc Pics/Beksinski");
            beksinski.AlbumTitle = "Beksinski";
            beksinski.ArtistName = "Beksinski";
            await Pass(beksinski).RunAsync(null, 0);
            // A bare shelf move with NO album — the case whose only trace is the column, and which an
            // export that keyed on album membership would silently drop.
            await Pass(Rule("Misc Pics/SAMisc")).RunAsync(null, 0);

            var dir = fixture.ExportDir("shelf");
            var manifest = await new PhotoCurationExporter(fixture.NewDb, _ => { }).RunAsync(dir);
            Assert.True(manifest.Complete);

            // The rebuilt database: the assets exist (a re-ingest would have made them), the curation
            // does not.
            var rebuilt = fixture.SecondaryDbFactory("shelf-rebuilt");
            using (var db = rebuilt())
            {
                using var source = fixture.NewDb();
                foreach (var a in await source.PhotoAssets.AsNoTracking().ToListAsync())
                {
                    db.PhotoAssets.Add(new PhotoAsset
                    {
                        Path = a.Path,
                        Sha256 = a.Sha256,
                        SizeBytes = a.SizeBytes,
                        Kind = a.Kind,
                        FileModifiedUtc = a.FileModifiedUtc,
                        FirstSeenUtc = a.FirstSeenUtc,
                        // Deliberately born on the default shelf, with no artist anywhere: what the
                        // import restores has to be the export's word, not a leftover.
                    });
                }
                await db.SaveChangesAsync();
            }

            await new PhotoCurationImporter(rebuilt, dir, apply: true, _ => { }).RunAsync(null, 0);

            using var restored = rebuilt();
            Assert.Equal(2, await restored.PhotoAssets.CountAsync(a => a.Path.StartsWith("Misc Pics/Misc Pics/Beksinski/")
                                                                       && a.Shelf == PhotoShelf.Archive));
            Assert.Equal(3, await restored.PhotoAssets.CountAsync(a => a.Path.StartsWith("Misc Pics/SAMisc/")
                                                                       && a.Shelf == PhotoShelf.Archive));
            // …and the family photographs did NOT come back archived.
            Assert.Equal(0, await restored.PhotoAssets.CountAsync(a => a.Path.StartsWith("Family/")
                                                                       && a.Shelf == PhotoShelf.Archive));

            var album = await restored.PhotoAlbums.SingleAsync();
            Assert.Equal("Beksinski", album.ArtistName);
            Assert.Equal(PhotoShelf.Archive, album.Shelf);
        }

        [Fact]
        public async Task An_export_written_before_this_phase_still_imports()
        {
            BuildTree();
            await IngestAsync();

            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.FirstAsync(a => a.Path == "Family/f1.jpg");
                row.Hidden = true;
                await db.SaveChangesAsync();
            }

            var dir = fixture.ExportDir("pre-phase7");
            await new PhotoCurationExporter(fixture.NewDb, _ => { }).RunAsync(dir);

            // Strip the field the way a file written last month would not have had it at all. An older
            // export must stay importable — the reason to have one is that it is old.
            var assetsFile = System.IO.Path.Combine(dir, PhotoCurationExportFormat.AssetsFile);
            var text = System.IO.File.ReadAllText(assetsFile);
            Assert.DoesNotContain("\"shelf\"", text);

            var rebuilt = fixture.SecondaryDbFactory("pre-phase7-rebuilt");
            using (var db = rebuilt())
            {
                using var source = fixture.NewDb();
                foreach (var a in await source.PhotoAssets.AsNoTracking().Where(a => a.Path == "Family/f1.jpg").ToListAsync())
                    db.PhotoAssets.Add(new PhotoAsset
                    {
                        Path = a.Path, Sha256 = a.Sha256, SizeBytes = a.SizeBytes, Kind = a.Kind,
                        FileModifiedUtc = a.FileModifiedUtc, FirstSeenUtc = a.FirstSeenUtc,
                        Shelf = PhotoShelf.Archive,
                    });
                await db.SaveChangesAsync();
            }

            await new PhotoCurationImporter(rebuilt, dir, apply: true, _ => { }).RunAsync(null, 0);

            using var restored = rebuilt();
            var row2 = await restored.PhotoAssets.SingleAsync();
            Assert.True(row2.Hidden);
            // An absent shelf reads as Timeline, which is what every row written before Phase 7 meant.
            Assert.Equal(PhotoShelf.Timeline, row2.Shelf);
        }
    }
}
