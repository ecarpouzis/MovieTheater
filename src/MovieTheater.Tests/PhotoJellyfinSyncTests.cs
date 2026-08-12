using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Photos;
using MovieTheater.Services.Jellyfin;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// <c>photos-sync-jellyfin</c> (docs/photos-plan.md §2.3): mapping the family library onto photo
    /// assets, clearing ids for items that vanished, and the reserved-folder-name audit.
    ///
    /// <para><b>No media server is contacted.</b> The library listing comes from
    /// <see cref="StandInJellyfin"/>, an in-process <see cref="IPhotoJellyfinSource"/>. That seam is the
    /// whole reason the engine takes an interface: the configured Jellyfin endpoint is the live server
    /// and a test suite must never be the thing that calls it.</para>
    /// </summary>
    public class PhotoJellyfinSyncTests
    {
        // Invented paths — no real collection layout appears in code (§6).
        private const string PhotosRootDb = @"Q:\7 - Family Album";
        private const string PhotosRootUnc = @"\\media\share\7 - Family Album";

        private static List<JellyfinPathMapping> Mappings() => new()
        {
            new JellyfinPathMapping { DbPrefix = @"Q:\", JellyfinPrefix = @"\\media\share\" },
        };

        private static PhotoJellyfinPaths Paths(string root = PhotosRootDb) =>
            PhotoJellyfinPaths.Build(root, Mappings());

        // ── Path mapping, both vocabularies ─────────────────────────────────────────────────────

        [Theory]
        // Jellyfin reports UNC; the table stores root-relative with forward slashes.
        [InlineData(PhotosRootUnc + @"\Vacation\clip.mp4", "Vacation/clip.mp4")]
        // The same server on Windows, reporting the drive-letter form.
        [InlineData(PhotosRootDb + @"\Vacation\clip.mp4", "Vacation/clip.mp4")]
        // A Linux Jellyfin's forward slashes.
        [InlineData(@"//media/share/7 - Family Album/Vacation/clip.mp4", "Vacation/clip.mp4")]
        // Case disagreement in the root, which SMB produces routinely.
        [InlineData(@"\\MEDIA\SHARE\7 - family album\Vacation\clip.mp4", "Vacation/clip.mp4")]
        // A loose file at the collection root (§1 says there are some).
        [InlineData(PhotosRootDb + @"\loose.mp4", "loose.mp4")]
        public void A_jellyfin_path_becomes_the_root_relative_key(string absolute, string expected)
        {
            Assert.Equal(expected, Paths().ToRootRelative(absolute));
        }

        [Theory]
        // Outside the collection entirely.
        [InlineData(@"Q:\1 - Movies\A\Alien (1979)\Alien.mkv")]
        // The prefix trap again: a sibling whose name merely starts with the root's.
        [InlineData(@"Q:\7 - Family Album Extra\clip.mp4")]
        // The root itself is not a file in it.
        [InlineData(PhotosRootDb)]
        [InlineData("")]
        public void A_path_outside_the_root_maps_to_nothing(string absolute)
        {
            // Null, never a guess: mapping the wrong photograph would attach a family video to
            // somebody else's row (§2.5's stance).
            Assert.Null(Paths().ToRootRelative(absolute));
        }

        [Fact]
        public void The_root_may_be_configured_as_UNC_and_still_map_a_drive_letter_path()
        {
            var paths = Paths(PhotosRootUnc);
            Assert.Equal("Vacation/clip.mp4", paths.ToRootRelative(PhotosRootDb + @"\Vacation\clip.mp4"));
            Assert.Equal("Vacation/clip.mp4", paths.ToRootRelative(PhotosRootUnc + @"\Vacation\clip.mp4"));
        }

        [Fact]
        public void The_ORIGINAL_casing_of_the_relative_part_survives()
        {
            // The comparison is case-insensitive but the stored Path came from a filesystem walk;
            // lower-casing the key here would fail the lookup on a case-sensitive server collation.
            Assert.Equal("Vacation/Clip.MP4",
                Paths().ToRootRelative(@"\\MEDIA\SHARE\7 - FAMILY ALBUM\Vacation\Clip.MP4"));
        }

        // ── Stamping ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task It_stamps_the_item_id_onto_the_matching_video_row()
        {
            using var fixture = new PhotoIngestFixture();
            var videoId = Seed(fixture, "Vacation/clip.mp4", PhotoAssetKind.Video);
            var photoId = Seed(fixture, "Vacation/photo.jpg", PhotoAssetKind.Photo);

            var source = new StandInJellyfin()
                .With("item-1", PhotosRootUnc + @"\Vacation\clip.mp4")
                // Jellyfin indexing something the album calls a photo must NOT stamp it — a play
                // button on a photograph is a worse answer than no button.
                .With("item-2", PhotosRootUnc + @"\Vacation\photo.jpg");

            var result = await Run(fixture, source, PhotoJellyfinPass.Items);

            Assert.Equal(1, Count(result, "stamped"));
            Assert.Equal(1, Count(result, "not-a-video"));

            using var db = fixture.NewDb();
            Assert.Equal("item-1", (await db.PhotoAssets.FindAsync(videoId))!.JellyfinItemId);
            Assert.Null((await db.PhotoAssets.FindAsync(photoId))!.JellyfinItemId);
        }

        [Fact]
        public async Task Re_running_stamps_nothing_new()
        {
            // Idempotence is the property the whole bulk-job rule rests on: a driver loop that has to
            // stop at exactly the right moment is a driver loop that will one day not.
            using var fixture = new PhotoIngestFixture();
            Seed(fixture, "Vacation/clip.mp4", PhotoAssetKind.Video);
            var source = new StandInJellyfin().With("item-1", PhotosRootUnc + @"\Vacation\clip.mp4");

            var first = await Run(fixture, source, PhotoJellyfinPass.Items);
            var second = await Run(fixture, source, PhotoJellyfinPass.Items);

            Assert.Equal(1, Count(first, "stamped"));
            Assert.Equal(0, Count(second, "stamped"));
            Assert.Equal(1, Count(second, "already-stamped"));
        }

        [Fact]
        public async Task An_unmatched_item_is_reported_on_BOTH_sides()
        {
            using var fixture = new PhotoIngestFixture();
            Seed(fixture, "Vacation/known.mp4", PhotoAssetKind.Video);
            Seed(fixture, "Vacation/never-indexed.mp4", PhotoAssetKind.Video);

            var source = new StandInJellyfin()
                .With("item-1", PhotosRootUnc + @"\Vacation\known.mp4")
                .With("item-2", PhotosRootUnc + @"\Vacation\not-in-album.mp4")
                .With("item-3", @"Q:\1 - Movies\somehow.mkv");

            var engine = Engine(fixture, source);
            await engine.RunAsync(PhotoJellyfinPass.Items, null, 0);
            await engine.RunAsync(PhotoJellyfinPass.Clear, null, 0);

            // The media server knows a file the album has not ingested…
            Assert.Contains(engine.UnmatchedJellyfinPaths, p => p.EndsWith("not-in-album.mp4", StringComparison.Ordinal));
            // …including one that is not even under the collection root.
            Assert.Contains(engine.UnmatchedJellyfinPaths, p => p.EndsWith("somehow.mkv", StringComparison.Ordinal));
            // …and the album holds a video the media server cannot play.
            Assert.Contains(engine.UnmatchedAssetPaths, p => p == "Vacation/never-indexed.mp4");
        }

        [Fact]
        public async Task A_dry_run_writes_nothing()
        {
            using var fixture = new PhotoIngestFixture();
            var id = Seed(fixture, "Vacation/clip.mp4", PhotoAssetKind.Video);
            var source = new StandInJellyfin().With("item-1", PhotosRootUnc + @"\Vacation\clip.mp4");

            var result = await Run(fixture, source, PhotoJellyfinPass.Items, dryRun: true);

            Assert.Equal(1, Count(result, "stamped"));
            using var db = fixture.NewDb();
            Assert.Null((await db.PhotoAssets.FindAsync(id))!.JellyfinItemId);
        }

        [Fact]
        public async Task Chunking_covers_every_item_exactly_once()
        {
            using var fixture = new PhotoIngestFixture();
            var source = new StandInJellyfin();
            for (var i = 0; i < 7; i++)
            {
                Seed(fixture, $"Vacation/clip{i}.mp4", PhotoAssetKind.Video);
                source.With($"item-{i}", PhotosRootUnc + $@"\Vacation\clip{i}.mp4");
            }

            // Two items per batch, driven to completion the way a caller drives one.
            var engine = Engine(fixture, source, batchSize: 2);
            var total = await engine.RunAsync(PhotoJellyfinPass.Items, null, 0);

            Assert.Equal(7, total.Processed);
            Assert.Equal(0, total.Remaining);
            using var db = fixture.NewDb();
            Assert.Equal(7, await db.PhotoAssets.CountAsync(a => a.JellyfinItemId != null));
        }

        // ── Clearing ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task An_id_whose_item_vanished_is_cleared()
        {
            using var fixture = new PhotoIngestFixture();
            var goneId = Seed(fixture, "Vacation/gone.mp4", PhotoAssetKind.Video, jellyfinItemId: "old-item");
            var stillId = Seed(fixture, "Vacation/still.mp4", PhotoAssetKind.Video, jellyfinItemId: "item-1");

            var source = new StandInJellyfin().With("item-1", PhotosRootUnc + @"\Vacation\still.mp4");
            var result = await Run(fixture, source, PhotoJellyfinPass.Clear);

            Assert.Equal(1, Count(result, "cleared"));
            Assert.Equal(1, Count(result, "still-present"));

            using var db = fixture.NewDb();
            Assert.Null((await db.PhotoAssets.FindAsync(goneId))!.JellyfinItemId);
            Assert.Equal("item-1", (await db.PhotoAssets.FindAsync(stillId))!.JellyfinItemId);
        }

        [Fact]
        public async Task An_EMPTY_library_answer_clears_nothing()
        {
            // "The library reported nothing" and "every video was deleted" are indistinguishable from
            // here, and only one of them justifies unstamping the whole album. A server that answers
            // an empty list — restarting, misconfigured, mid-scan — must not empty the play buttons.
            using var fixture = new PhotoIngestFixture();
            var id = Seed(fixture, "Vacation/clip.mp4", PhotoAssetKind.Video, jellyfinItemId: "item-1");

            var result = await Run(fixture, new StandInJellyfin(), PhotoJellyfinPass.Clear);

            Assert.Equal(0, Count(result, "cleared"));
            using var db = fixture.NewDb();
            Assert.Equal("item-1", (await db.PhotoAssets.FindAsync(id))!.JellyfinItemId);
        }

        [Fact]
        public async Task Clearing_is_idempotent()
        {
            using var fixture = new PhotoIngestFixture();
            Seed(fixture, "Vacation/gone.mp4", PhotoAssetKind.Video, jellyfinItemId: "old-item");
            Seed(fixture, "Vacation/still.mp4", PhotoAssetKind.Video, jellyfinItemId: "item-1");
            var source = new StandInJellyfin().With("item-1", PhotosRootUnc + @"\Vacation\still.mp4");

            await Run(fixture, source, PhotoJellyfinPass.Clear);
            var second = await Run(fixture, source, PhotoJellyfinPass.Clear);

            Assert.Equal(0, Count(second, "cleared"));
            Assert.Equal(1, Count(second, "never-stamped"));
        }

        // ── Reserved-folder-name audit (§2.3's ⚠ trap) ──────────────────────────────────────────

        [Theory]
        [InlineData("Vacation/Trailers/clip.mp4", "Trailers")]
        [InlineData("Vacation/Behind The Scenes/clip.mp4", "Behind The Scenes")]
        [InlineData("Scenes/clip.mp4", "Scenes")]
        [InlineData("A/Extras/B/clip.mp4", "Extras")]
        // The NEAREST reserved ancestor, because that is the folder a human would act on.
        [InlineData("Extras/Shorts/clip.mp4", "Shorts")]
        // Whole-segment only: Jellyfin's own rule is on the folder NAME.
        [InlineData("Vacation/Trailers from 2004/clip.mp4", null)]
        [InlineData("Vacation/clip.mp4", null)]
        // The FILE name is never the rule — a video called Scenes.mp4 indexes perfectly well.
        [InlineData("Vacation/Scenes.mp4", null)]
        public void The_reserved_name_rule_is_whole_segment_and_folder_only(string path, string? expected)
        {
            Assert.Equal(expected, PhotoJellyfinReservedFolders.ReservedSegment(path));
        }

        [Fact]
        public async Task The_audit_flags_a_planted_reserved_folder_and_nothing_else()
        {
            using var fixture = new PhotoIngestFixture();
            Seed(fixture, "Vacation/clip.mp4", PhotoAssetKind.Video);
            Seed(fixture, "Vacation/Trailers/home-movie.mp4", PhotoAssetKind.Video);
            Seed(fixture, "Vacation/Trailers/another.mp4", PhotoAssetKind.Video);
            // A PHOTO in the same reserved folder is fine — Jellyfin's folder walk is about the video
            // library, and the album shows the picture regardless.
            Seed(fixture, "Vacation/Trailers/still.jpg", PhotoAssetKind.Photo);

            var result = await Run(fixture, new StandInJellyfin(), PhotoJellyfinPass.Audit);

            Assert.Equal(2, Count(result, "reported"));

            using var db = fixture.NewDb();
            var batch = await db.PhotoCurationBatches
                .SingleAsync(b => b.Kind == PhotoCurationBatchKind.JellyfinReserved);
            Assert.True(batch.Complete);
            var items = await db.PhotoCurationBatchItems.Where(i => i.PhotoCurationBatchId == batch.Id).ToListAsync();
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.Equal("jellyfin-reserved:Trailers", i.Rule));
            Assert.All(items, i => Assert.StartsWith("Vacation/Trailers/", i.Path, StringComparison.Ordinal));
        }

        [Fact]
        public async Task The_audit_endpoint_groups_by_folder_for_an_admin()
        {
            using var fixture = new PhotoIngestFixture();
            Seed(fixture, "Vacation/Trailers/a.mp4", PhotoAssetKind.Video);
            Seed(fixture, "Vacation/Trailers/b.mp4", PhotoAssetKind.Video);
            Seed(fixture, "Party/Scenes/c.mp4", PhotoAssetKind.Video);
            await Run(fixture, new StandInJellyfin(), PhotoJellyfinPass.Audit);

            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db, admin: true);
            var body = PhotosControllerHarness.Body(await controller.JellyfinAudit());

            Assert.True(body.GetProperty("ran").GetBoolean());
            Assert.Equal(3, body.GetProperty("affected").GetInt32());
            var folders = body.GetProperty("folders").EnumerateArray()
                .ToDictionary(f => f.GetProperty("folder").GetString()!, f => f.GetProperty("count").GetInt32());
            Assert.Equal(2, folders["Vacation/Trailers"]);
            Assert.Equal(1, folders["Party/Scenes"]);
        }

        [Fact]
        public async Task The_audit_endpoint_is_admin_only()
        {
            // Being in the album is not being an operator: this readout describes the pipeline, not
            // the photographs (the IngestStatus precedent).
            using var fixture = new PhotoIngestFixture();
            using var db = fixture.NewDb();
            var controller = PhotosControllerHarness.Build(fixture, db, admin: false);
            Assert.IsType<ForbidResult>(await controller.JellyfinAudit());
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        private static int Seed(PhotoIngestFixture fixture, string path, PhotoAssetKind kind,
            string? jellyfinItemId = null, double? durationSec = null)
        {
            using var db = fixture.NewDb();
            var row = new PhotoAsset
            {
                Path = path,
                SizeBytes = 1024,
                FileModifiedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Kind = kind,
                FirstSeenUtc = DateTime.UtcNow,
                JellyfinItemId = jellyfinItemId,
                DurationSec = durationSec,
                ThumbState = kind == PhotoAssetKind.Video ? PhotoThumbState.VideoDeferred : PhotoThumbState.Pending,
            };
            db.PhotoAssets.Add(row);
            db.SaveChanges();
            return row.Id;
        }

        private static PhotoJellyfinSync Engine(PhotoIngestFixture fixture, IPhotoJellyfinSource source,
            int batchSize = 200, bool dryRun = false) =>
            new PhotoJellyfinSync(fixture.NewDb, source, Paths(),
                new PhotoJellyfinSyncOptions { BatchSize = batchSize, DryRun = dryRun, AuditBatchId = "test-audit" },
                _ => { });

        private static Task<PhotoIngestBatchResult> Run(PhotoIngestFixture fixture, IPhotoJellyfinSource source,
            PhotoJellyfinPass pass, bool dryRun = false) =>
            Engine(fixture, source, dryRun: dryRun).RunAsync(pass, null, 0);

        private static int Count(PhotoIngestBatchResult result, string key) =>
            result.Counts.TryGetValue(key, out var value) ? value : 0;

        /// <summary>An in-process family library. Never a network call.</summary>
        private sealed class StandInJellyfin : IPhotoJellyfinSource
        {
            private readonly List<PhotoJellyfinItem> items = new();

            public StandInJellyfin With(string id, string path)
            {
                items.Add(new PhotoJellyfinItem { Id = id, Path = path });
                return this;
            }

            public Task<IReadOnlyList<PhotoJellyfinItem>> ItemsAsync(CancellationToken cancel = default) =>
                Task.FromResult<IReadOnlyList<PhotoJellyfinItem>>(items);

            public Task<string> DescribeAsync(CancellationToken cancel = default) =>
                Task.FromResult("stand-in (no server was contacted)");
        }
    }
}
