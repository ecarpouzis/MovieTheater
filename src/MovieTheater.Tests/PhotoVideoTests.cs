using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Photos;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Phase 5's video half (docs/photos-plan.md §2.3): ffprobe parsing, the poster grab, the ingest
    /// pass that fills a video row, the teeth the variant lane gains once durations exist, and the
    /// gated stream-start endpoint.
    ///
    /// <para><b>Nothing here reads the NAS and nothing contacts a media server.</b> The parsing is
    /// asserted against a GOLDEN readout captured from a real ffprobe over a synthesized clip; the
    /// pipeline is driven through the <see cref="IPhotoVideoTools"/> seam; and the end-to-end
    /// binary test synthesizes its own four-second clip with ffmpeg and skips itself when ffmpeg is
    /// absent, so the suite is green on a machine that has never had it.</para>
    /// </summary>
    public class PhotoVideoTests
    {
        // ── ffprobe parsing (golden outputs) ────────────────────────────────────────────────────

        /// <summary>
        /// A REAL ffprobe readout, captured from <c>ffprobe -print_format json -show_format
        /// -show_streams</c> over a 4-second 320×240 H.264 clip synthesized by ffmpeg with an explicit
        /// <c>creation_time</c>. Verbatim apart from the filename, which is replaced so no machine's
        /// paths enter the repository (§6).
        /// </summary>
        private const string GoldenProbeJson = @"{
    ""streams"": [
        {
            ""index"": 0,
            ""codec_name"": ""h264"",
            ""codec_long_name"": ""H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10"",
            ""profile"": ""High"",
            ""codec_type"": ""video"",
            ""codec_tag_string"": ""avc1"",
            ""width"": 320,
            ""height"": 240,
            ""coded_width"": 320,
            ""coded_height"": 240,
            ""has_b_frames"": 2,
            ""sample_aspect_ratio"": ""1:1"",
            ""display_aspect_ratio"": ""4:3"",
            ""pix_fmt"": ""yuv420p"",
            ""level"": 12,
            ""field_order"": ""progressive"",
            ""r_frame_rate"": ""15/1"",
            ""avg_frame_rate"": ""15/1"",
            ""time_base"": ""1/15360"",
            ""start_pts"": 0,
            ""start_time"": ""0.000000"",
            ""duration_ts"": 61440,
            ""duration"": ""4.000000"",
            ""bit_rate"": ""33662"",
            ""nb_frames"": ""60"",
            ""disposition"": { ""default"": 1, ""forced"": 0 },
            ""tags"": {
                ""creation_time"": ""2019-07-04T14:30:00.000000Z"",
                ""language"": ""und"",
                ""handler_name"": ""VideoHandler"",
                ""encoder"": ""Lavc61.19.101 libx264""
            }
        }
    ],
    ""format"": {
        ""filename"": ""clip.mp4"",
        ""nb_streams"": 1,
        ""format_name"": ""mov,mp4,m4a,3gp,3g2,mj2"",
        ""format_long_name"": ""QuickTime / MOV"",
        ""start_time"": ""0.000000"",
        ""duration"": ""4.000000"",
        ""size"": ""18425"",
        ""bit_rate"": ""36850"",
        ""probe_score"": 100,
        ""tags"": {
            ""major_brand"": ""isom"",
            ""creation_time"": ""2019-07-04T14:30:00.000000Z"",
            ""encoder"": ""Lavf61.7.103""
        }
    }
}";

        [Fact]
        public void The_golden_readout_parses_into_duration_dimensions_and_a_UTC_date()
        {
            var info = FfmpegVideoTools.ParseProbeJson(GoldenProbeJson);

            Assert.NotNull(info);
            Assert.Equal(4.0, info!.DurationSec);
            Assert.Equal(320, info.Width);
            Assert.Equal(240, info.Height);
            Assert.Equal(new DateTime(2019, 7, 4, 14, 30, 0, DateTimeKind.Utc), info.CreationTimeUtc);
        }

        [Fact]
        public void The_readout_is_persisted_in_the_shape_the_EXIF_panel_already_reads()
        {
            // §2.5 keeps raw measurements rather than recomputing them, and the lightbox's info panel
            // parses PhotoAsset.RawMetadataJson as a two-level directory→tag map. Emitting anything
            // else here would leave a video with a panel that silently renders nothing.
            var info = FfmpegVideoTools.ParseProbeJson(GoldenProbeJson)!;

            Assert.Contains("ffprobe format", info.Sections.Keys);
            Assert.Contains("ffprobe video 0", info.Sections.Keys);
            Assert.Equal("h264", info.Sections["ffprobe video 0"]["codec_name"]);
            // One level of nesting is flattened with a prefix rather than dropped — creation_time and
            // a phone's make/model live in `tags`.
            Assert.Equal("2019-07-04T14:30:00.000000Z", info.Sections["ffprobe format"]["tags.creation_time"]);
        }

        [Theory]
        // A phone records landscape and declares the turn; the DISPLAY dimensions are what the
        // justified grid lays out from, so a portrait clip must report portrait.
        [InlineData(@"""tags"": { ""rotate"": ""90"" }", 240, 320)]
        [InlineData(@"""tags"": { ""rotate"": ""270"" }", 240, 320)]
        [InlineData(@"""tags"": { ""rotate"": ""180"" }", 320, 240)]
        // Newer ffmpeg reports it as displaymatrix side data instead of a tag.
        [InlineData(@"""side_data_list"": [ { ""side_data_type"": ""Display Matrix"", ""rotation"": -90 } ]", 240, 320)]
        [InlineData("", 320, 240)]
        public void A_quarter_turn_swaps_the_reported_dimensions(string extra, int width, int height)
        {
            var json = "{\"streams\":[{\"codec_type\":\"video\",\"width\":320,\"height\":240"
                       + (extra.Length > 0 ? "," + extra : "") + "}]}";
            var info = FfmpegVideoTools.ParseProbeJson(json)!;
            Assert.Equal(width, info.Width);
            Assert.Equal(height, info.Height);
        }

        [Theory]
        // QuickTime's epoch. An UNSET creation_time surfaces as exactly this, and storing it would put
        // a wall of confidently-dated 1904 clips at the oldest end of a family timeline.
        [InlineData("1904-01-01T00:00:00.000000Z")]
        // A camera whose clock was never set.
        [InlineData("1970-01-01T00:00:00.000000Z")]
        // A clock fault the other way.
        [InlineData("2999-01-01T00:00:00.000000Z")]
        // Not a date at all.
        [InlineData("not a date")]
        [InlineData("")]
        public void A_nonsensical_container_date_is_DROPPED(string creationTime)
        {
            var json = "{\"format\":{\"duration\":\"12.0\",\"tags\":{\"creation_time\":\"" + creationTime + "\"}}}";
            var info = FfmpegVideoTools.ParseProbeJson(json)!;
            Assert.Null(info.CreationTimeUtc);
            // The duration still lands: one bad field must not cost the whole readout.
            Assert.Equal(12.0, info.DurationSec);
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("[1,2,3]")]
        [InlineData("")]
        public void Unparseable_output_is_NO_ANSWER_rather_than_a_wrong_one(string output)
        {
            Assert.Null(FfmpegVideoTools.ParseProbeJson(output));
        }

        [Theory]
        [InlineData("-4")]           // negative
        [InlineData("0")]
        [InlineData("N/A")]          // ffprobe's own "I don't know"
        [InlineData("99999999")]     // longer than a week — a parse artefact, not a home video
        public void An_impossible_duration_is_refused(string duration)
        {
            var json = "{\"format\":{\"duration\":\"" + duration + "\"}}";
            Assert.Null(FfmpegVideoTools.ParseProbeJson(json)!.DurationSec);
        }

        // ── The timeout kill ────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_binary_that_never_finishes_is_KILLED_and_reports_no_answer()
        {
            // A wedged ffmpeg on a corrupt file would otherwise hold a bulk pass open indefinitely,
            // which is the failure mode the standing rule about bounded work exists to prevent. The
            // stand-in is the OS shell told to sleep — a real process that genuinely outlives the
            // ceiling, rather than a mock asserting that a timer was set.
            var (executable, sleepArgs) = SleepCommand();
            if (executable == null) return;   // no shell to drive; nothing to assert

            var tools = new FfmpegVideoTools(executable, executable, TimeSpan.FromMilliseconds(400));
            var stopwatch = Stopwatch.StartNew();
            var result = tools.Probe(sleepArgs);
            stopwatch.Stop();

            Assert.Null(result);
            // It returned near the ceiling rather than after the sleep — proof the kill happened
            // instead of the wait simply completing.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8),
                $"the probe took {stopwatch.Elapsed.TotalSeconds:0.0}s; it should have been killed at 0.4s");
        }

        [Fact]
        public void A_missing_binary_is_a_configuration_fact_not_a_crash()
        {
            var tools = new FfmpegVideoTools(Path.Combine(Path.GetTempPath(), "no-such-ffprobe-" + Guid.NewGuid().ToString("N")), null);
            Assert.True(tools.Available);            // configured…
            Assert.Null(tools.Probe("whatever"));    // …and simply has no answer
        }

        [Fact]
        public void An_unconfigured_host_reports_itself_unavailable()
        {
            var tools = new FfmpegVideoTools(null, null);
            Assert.False(tools.Available);
            Assert.False(tools.CanGrabFrames);
        }

        // ── The ingest pass ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_video_pass_fills_the_row_and_writes_grid_and_view_posters()
        {
            using var fixture = new PhotoIngestFixture();
            fixture.WriteVideo("Vacation/clip.mp4");
            var options = fixture.Options();
            options.VideoTools = new StandInVideoTools
            {
                Duration = 42.5,
                Width = 1920,
                Height = 1080,
                CreationTimeUtc = new DateTime(2019, 7, 4, 18, 30, 0, DateTimeKind.Utc),
            };

            var pipeline = new PhotoIngestPipeline(fixture.NewDb, options, _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            var result = await pipeline.RunAsync(PhotoIngestPass.Video, null, 0);

            Assert.Equal(1, Count(result, "durations"));
            Assert.Equal(1, Count(result, "posters"));

            using var db = fixture.NewDb();
            var row = await db.PhotoAssets.SingleAsync();
            Assert.Equal(42.5, row.DurationSec);
            Assert.Equal(PhotoThumbState.Ready, row.ThumbState);
            Assert.Equal("grid,view", row.ThumbVariants);
            Assert.NotNull(row.ThumbKey);
            Assert.NotNull(row.RawMetadataJson);

            // Both derivatives are actually ON DISK under the names a capability token would carry —
            // the gateway 404s a missing thumb by design, so advertising one that was never written
            // would turn an ingest gap into a broken image.
            foreach (var size in new[] { "grid", "view" })
            {
                var relative = PhotoThumbCache.RelativePath(row.Id, row.ThumbKey!, size);
                var file = Path.Combine(fixture.ThumbCache, relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(file), $"the {size} poster was advertised but not written: {relative}");
            }

            // No `zoom`: that derivative exists so an un-renderable ORIGINAL still has a deep-zoom
            // target (§2.2), and nobody deep-zooms a poster frame.
            Assert.False(PhotoThumbCache.Has(row.ThumbVariants, "zoom"));
        }

        [Fact]
        public async Task The_container_date_becomes_wall_clock_with_the_raw_UTC_kept()
        {
            // §2.7's conversion path, the second true-UTC source after GPS: 18:30 UTC on the 4th of
            // July is 14:30 in the configured home zone, and the raw instant is kept beside it so the
            // conversion stays revisitable.
            using var fixture = new PhotoIngestFixture();
            fixture.WriteVideo("Vacation/clip.mp4");
            var options = fixture.Options();
            options.HomeTimeZone = "America/New_York";
            options.VideoTools = new StandInVideoTools
            {
                Duration = 10,
                CreationTimeUtc = new DateTime(2019, 7, 4, 18, 30, 0, DateTimeKind.Utc),
            };

            var pipeline = new PhotoIngestPipeline(fixture.NewDb, options, _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Video, null, 0);

            using var db = fixture.NewDb();
            var row = await db.PhotoAssets.SingleAsync();
            Assert.Equal(new DateTime(2019, 7, 4, 18, 30, 0, DateTimeKind.Utc), row.TakenAtUtcRaw);
            Assert.Equal(new DateTime(2019, 7, 4, 14, 30, 0), row.TakenAt);
            Assert.Equal(TakenAtSource.VideoContainer, row.TakenAtSource);
        }

        [Fact]
        public async Task A_hand_set_date_is_never_overwritten_by_the_container()
        {
            // §2.7: a human's answer outranks every machine. A filename guess does not.
            using var fixture = new PhotoIngestFixture();
            fixture.WriteVideo("Vacation/clip.mp4");
            var options = fixture.Options();
            options.VideoTools = new StandInVideoTools
            {
                Duration = 10,
                CreationTimeUtc = new DateTime(2019, 7, 4, 18, 30, 0, DateTimeKind.Utc),
            };
            var pipeline = new PhotoIngestPipeline(fixture.NewDb, options, _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);

            var manual = new DateTime(1994, 5, 1, 9, 0, 0);
            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync();
                row.TakenAt = manual;
                row.TakenAtSource = TakenAtSource.Manual;
                await db.SaveChangesAsync();
            }

            await pipeline.RunAsync(PhotoIngestPass.Video, null, 0);

            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync();
                Assert.Equal(manual, row.TakenAt);
                Assert.Equal(TakenAtSource.Manual, row.TakenAtSource);
                // The rest of the readout still landed — only the date was protected.
                Assert.Equal(10, row.DurationSec);
            }
        }

        [Fact]
        public async Task With_no_tools_the_pass_changes_nothing_and_leaves_the_queue_intact()
        {
            // A host with no ffprobe is a normal host. The videos stay exactly as Phase 1 left them,
            // and a host that later gains the binary must still find the work waiting.
            using var fixture = new PhotoIngestFixture();
            fixture.WriteVideo("Vacation/clip.mp4");
            var options = fixture.Options();
            options.VideoTools = null;

            var pipeline = new PhotoIngestPipeline(fixture.NewDb, options, _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            var result = await pipeline.RunAsync(PhotoIngestPass.Video, null, 0);

            Assert.Equal(1, Count(result, "no-video-tools"));
            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync();
                // Neither finished nor failed — nothing was stamped, so the row is exactly where the
                // walk left it.
                Assert.NotEqual(PhotoThumbState.Ready, row.ThumbState);
                Assert.NotEqual(PhotoThumbState.Failed, row.ThumbState);
                Assert.Null(row.DurationSec);
                Assert.Null(row.IngestError);
            }

            // …and the work is genuinely still waiting: the same pass on a host that HAS the binaries
            // finds it and finishes it, rather than the row having silently left the queue.
            options.VideoTools = new StandInVideoTools { Duration = 10 };
            Assert.Equal(1, Count(await pipeline.RunAsync(PhotoIngestPass.Video, null, 0), "posters"));
        }

        [Fact]
        public async Task An_unreadable_video_leaves_the_queue_instead_of_retrying_forever()
        {
            using var fixture = new PhotoIngestFixture();
            fixture.WriteVideo("Vacation/broken.mp4");
            var options = fixture.Options();
            options.VideoTools = new StandInVideoTools { ProbeFails = true };

            var pipeline = new PhotoIngestPipeline(fixture.NewDb, options, _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            var first = await pipeline.RunAsync(PhotoIngestPass.Video, null, 0);
            Assert.Equal(1, Count(first, "probe-errors"));

            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync();
                Assert.Equal(PhotoThumbState.Failed, row.ThumbState);
                Assert.NotNull(row.IngestError);
            }

            // Second run: the queue is empty, so "drain until remaining is 0" terminates.
            var second = await pipeline.RunAsync(PhotoIngestPass.Video, null, 0);
            Assert.Equal(0, second.Processed);
            Assert.Equal(0, second.Remaining);
        }

        [Fact]
        public async Task The_photo_thumb_pass_cannot_demote_a_finished_video_poster()
        {
            // The two passes stamp different columns and would otherwise fight: re-running the photo
            // thumb queue after the walk re-queued a row must not turn a Ready poster back into a
            // placeholder.
            using var fixture = new PhotoIngestFixture();
            fixture.WriteVideo("Vacation/clip.mp4");
            var options = fixture.Options();
            options.VideoTools = new StandInVideoTools { Duration = 10 };

            var pipeline = new PhotoIngestPipeline(fixture.NewDb, options, _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Video, null, 0);

            using (var db = fixture.NewDb())
            {
                var row = await db.PhotoAssets.SingleAsync();
                row.ThumbsUpdatedUtc = null;   // what the walk does when the bytes change
                await db.SaveChangesAsync();
            }
            await pipeline.RunAsync(PhotoIngestPass.Thumb, null, 0);

            using (var db = fixture.NewDb())
                Assert.Equal(PhotoThumbState.Ready, (await db.PhotoAssets.SingleAsync()).ThumbState);
        }

        // ── The teeth --motion-seconds gains (§2.6) ─────────────────────────────────────────────

        [Fact]
        public async Task A_LONG_video_sharing_a_stem_stops_being_paired_once_its_duration_is_known()
        {
            // §2.6's variant rule bounds the video half's length, but "videos carry no duration until
            // Phase 5 runs ffprobe, so null passes". This is that bound acquiring teeth: the SAME two
            // files pair before the duration exists and refuse to afterwards.
            using var fixture = new PhotoIngestFixture();
            fixture.WriteJpeg("Vacation/IMG_9000.jpg", exifDateTimeOriginal: "2019:07:04 14:30:00");
            fixture.WriteVideo("Vacation/IMG_9000.mp4");

            var options = fixture.Options();
            var pipeline = new PhotoIngestPipeline(fixture.NewDb, options, _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);

            // Before: no duration, so the folder+stem+time agreement carries the pairing.
            using (var db = fixture.NewDb())
            {
                var rows = await db.PhotoAssets.OrderBy(a => a.Path).ToListAsync();
                Assert.Equal(PhotoVariantPairs.RuleMotionPhoto,
                    PhotoVariantPairs.Classify(rows, new PhotoVariantPairs.Options()));
            }

            // After the video pass: a 20-minute recording is a coincidence of naming, not a capture.
            options.VideoTools = new StandInVideoTools { Duration = 20 * 60 };
            await pipeline.RunAsync(PhotoIngestPass.Video, null, 0);

            using (var db = fixture.NewDb())
            {
                var rows = await db.PhotoAssets.OrderBy(a => a.Path).ToListAsync();
                Assert.Equal(20 * 60, rows.Single(r => r.Kind == PhotoAssetKind.Video).DurationSec);
                Assert.Null(PhotoVariantPairs.Classify(rows, new PhotoVariantPairs.Options()));
            }
        }

        [Fact]
        public async Task A_SHORT_video_sharing_a_stem_still_pairs_once_its_duration_is_known()
        {
            // The other side of the same fact: a real motion photo's 1.5 seconds passes the bound, so
            // the durations arriving does not cost the pairings the lane is for.
            using var fixture = new PhotoIngestFixture();
            fixture.WriteJpeg("Vacation/IMG_9001.jpg", exifDateTimeOriginal: "2019:07:04 14:30:00");
            fixture.WriteVideo("Vacation/IMG_9001.mp4");

            var options = fixture.Options();
            options.VideoTools = new StandInVideoTools { Duration = 1.5 };
            var pipeline = new PhotoIngestPipeline(fixture.NewDb, options, _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Video, null, 0);

            using var db = fixture.NewDb();
            var rows = await db.PhotoAssets.OrderBy(a => a.Path).ToListAsync();
            Assert.Equal(PhotoVariantPairs.RuleMotionPhoto,
                PhotoVariantPairs.Classify(rows, new PhotoVariantPairs.Options()));
        }

        // ── The gated stream start (§2.3) ───────────────────────────────────────────────────────

        [Fact]
        public async Task A_member_gets_a_minted_url_for_a_synced_video()
        {
            using var fixture = new PhotoIngestFixture();
            using var db = fixture.NewDb();
            var id = SeedVideo(db, "Vacation/clip.mp4", jellyfinItemId: "item-1", durationSec: 42);

            var playback = new StandInPlayback();
            var controller = PhotosControllerHarness.Build(fixture, db, playback: playback);
            var body = PhotosControllerHarness.Body(
                await controller.StartVideo(new PhotoVideoStartRequest { AssetId = id, DeviceToken = "abcdefgh12345678" }));

            Assert.Equal("https://gateway.invalid/s/token/Videos/item-1/stream.mp4", body.GetProperty("url").GetString());
            Assert.False(body.GetProperty("isHls").GetBoolean());
            Assert.Equal(42, body.GetProperty("durationSec").GetDouble());
            // The item id came from the ROW, never from the caller — a body-supplied id would make
            // this a general-purpose media-server proxy for anyone inside the gate.
            Assert.Equal("item-1", playback.LastItemId);
            Assert.Equal(PhotosControllerHarness.MemberUserId, playback.LastUserId);
        }

        [Fact]
        public async Task An_UNSYNCED_video_is_a_409_that_explains_itself()
        {
            using var fixture = new PhotoIngestFixture();
            using var db = fixture.NewDb();
            var id = SeedVideo(db, "Vacation/clip.mp4", jellyfinItemId: null);

            var controller = PhotosControllerHarness.Build(fixture, db, playback: new StandInPlayback());
            var result = Assert.IsType<ObjectResult>(await controller.StartVideo(new PhotoVideoStartRequest { AssetId = id }));

            Assert.Equal(409, result.StatusCode);
            // Not a 404: the file exists and the album can see it. The missing piece is a pipeline
            // step the owner runs, and the UI shows that state on the tile rather than a dead button.
            Assert.Contains("not been synced", System.Text.Json.JsonSerializer.Serialize(result.Value));
        }

        [Fact]
        public async Task A_PHOTO_can_never_be_started_as_a_video()
        {
            using var fixture = new PhotoIngestFixture();
            using var db = fixture.NewDb();
            var id = SeedVideo(db, "Vacation/photo.jpg", jellyfinItemId: "item-1", kind: PhotoAssetKind.Photo);

            var controller = PhotosControllerHarness.Build(fixture, db, playback: new StandInPlayback());
            Assert.IsType<BadRequestObjectResult>(await controller.StartVideo(new PhotoVideoStartRequest { AssetId = id }));
        }

        [Fact]
        public async Task An_unconfigured_host_answers_501_rather_than_failing_oddly()
        {
            using var fixture = new PhotoIngestFixture();
            using var db = fixture.NewDb();
            var id = SeedVideo(db, "Vacation/clip.mp4", jellyfinItemId: "item-1");

            var controller = PhotosControllerHarness.Build(fixture, db, playback: new StandInPlayback { IsConfigured = false });
            var result = Assert.IsType<ObjectResult>(await controller.StartVideo(new PhotoVideoStartRequest { AssetId = id }));
            Assert.Equal(501, result.StatusCode);
        }

        [Fact]
        public async Task The_card_says_whether_a_video_can_play()
        {
            // The tile draws "not yet synced" from this rather than guessing, which is what keeps a
            // dead play button off the grid.
            using var fixture = new PhotoIngestFixture();
            using var db = fixture.NewDb();
            SeedVideo(db, "Vacation/synced.mp4", jellyfinItemId: "item-1", durationSec: 12);
            SeedVideo(db, "Vacation/unsynced.mp4", jellyfinItemId: null);
            SeedVideo(db, "Vacation/photo.jpg", jellyfinItemId: null, kind: PhotoAssetKind.Photo);

            var controller = PhotosControllerHarness.Build(fixture, db);
            var body = PhotosControllerHarness.Body(await controller.Folders("Vacation/"));

            var cards = body.GetProperty("items").EnumerateArray()
                .ToDictionary(i => i.GetProperty("path").GetString()!, i => i);
            Assert.True(cards["Vacation/synced.mp4"].GetProperty("videoSynced").GetBoolean());
            Assert.False(cards["Vacation/unsynced.mp4"].GetProperty("videoSynced").GetBoolean());
            Assert.Equal(12, cards["Vacation/synced.mp4"].GetProperty("durationSec").GetDouble());
            // A photo carries no video verdict at all, so the tile branches on presence.
            Assert.Equal(System.Text.Json.JsonValueKind.Null, cards["Vacation/photo.jpg"].GetProperty("videoSynced").ValueKind);
        }

        // ── End to end against a real ffmpeg, when this machine has one ─────────────────────────

        [Fact]
        public async Task A_real_clip_is_probed_and_postered_end_to_end()
        {
            var ffmpeg = FindBinary("ffmpeg");
            var ffprobe = FindBinary("ffprobe");
            // Nothing to drive the binaries with on this machine. The seam tests above cover the logic;
            // set FFMPEG_PATH / FFPROBE_PATH (or put them on PATH) to run this lane for real.
            if (ffmpeg == null || ffprobe == null) return;

            using var fixture = new PhotoIngestFixture();
            // SYNTHESIZED, not read from anywhere: a four-second colour-bar clip with a known date.
            var clip = Path.Combine(fixture.Root, "Vacation", "clip.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
            Assert.True(Synthesize(ffmpeg, clip), "ffmpeg could not synthesize the fixture clip");

            var options = fixture.Options();
            options.VideoTools = new FfmpegVideoTools(ffprobe, ffmpeg, TimeSpan.FromSeconds(60));

            var pipeline = new PhotoIngestPipeline(fixture.NewDb, options, _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Video, null, 0);

            using var db = fixture.NewDb();
            var row = await db.PhotoAssets.SingleAsync();
            Assert.Equal(PhotoThumbState.Ready, row.ThumbState);
            Assert.NotNull(row.DurationSec);
            Assert.InRange(row.DurationSec!.Value, 3.5, 4.5);
            Assert.Equal(320, row.Width);
            Assert.Equal(240, row.Height);
            Assert.Equal(new DateTime(2019, 7, 4, 14, 30, 0, DateTimeKind.Utc), row.TakenAtUtcRaw);

            var grid = Path.Combine(fixture.ThumbCache,
                PhotoThumbCache.RelativePath(row.Id, row.ThumbKey!, "grid").Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(grid));
            using var poster = Image.Load(grid);
            Assert.True(poster.Width > 0 && poster.Height > 0);

            // The scratch frame is cleaned up: a cache directory that accumulates one PNG per video
            // would quietly double its own size.
            var scratchDir = Path.Combine(fixture.ThumbCache, "video-frames");
            Assert.True(!Directory.Exists(scratchDir) || Directory.GetFiles(scratchDir).Length == 0);
        }

        private static bool Synthesize(string ffmpeg, string destination)
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y",
                         "-f", "lavfi", "-i", "testsrc=size=320x240:rate=15:duration=4",
                         "-c:v", "libx264", "-pix_fmt", "yuv420p",
                         "-metadata", "creation_time=2019-07-04T14:30:00.000000Z",
                         destination,
                     })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process == null) return false;
            process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            return process.WaitForExit(120_000) && process.ExitCode == 0 && File.Exists(destination);
        }

        /// <summary>Finds a binary on PATH (or via an explicit env override) without shelling out.
        /// Returns null when this machine has none, which makes the end-to-end test skip itself
        /// rather than fail for a reason that is not about this code.</summary>
        private static string? FindBinary(string name)
        {
            var overridden = Environment.GetEnvironmentVariable(name.ToUpperInvariant() + "_PATH");
            if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden)) return overridden;

            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var candidates = isWindows ? new[] { name + ".exe", name } : new[] { name };
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var candidate in candidates)
                {
                    string full;
                    try { full = Path.Combine(directory.Trim('"'), candidate); }
                    catch (ArgumentException) { continue; }   // a malformed PATH entry is not our problem
                    if (File.Exists(full)) return full;
                }
            }
            return null;
        }

        /// <summary>A shell that will sleep well past any ceiling, for the timeout test. The
        /// "arguments" are smuggled through the file-path parameter because that is the only input the
        /// seam takes — which is fine: the point is a real child process that outlives the ceiling.</summary>
        private static (string? Executable, string Argument) SleepCommand()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var cmd = Environment.GetEnvironmentVariable("COMSPEC");
                return (cmd != null && File.Exists(cmd) ? cmd : null, "ping -n 20 127.0.0.1");
            }
            return (File.Exists("/bin/sh") ? "/bin/sh" : null, "sleep 20");
        }

        // ── Stand-ins ───────────────────────────────────────────────────────────────────────────

        private static int Count(PhotoIngestBatchResult result, string key) =>
            result.Counts.TryGetValue(key, out var value) ? value : 0;

        private static int SeedVideo(MovieDb db, string path, string? jellyfinItemId,
            double? durationSec = null, PhotoAssetKind kind = PhotoAssetKind.Video)
        {
            var row = new PhotoAsset
            {
                Path = path,
                SizeBytes = 2048,
                FileModifiedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Kind = kind,
                FirstSeenUtc = DateTime.UtcNow,
                JellyfinItemId = jellyfinItemId,
                DurationSec = durationSec,
                ThumbState = PhotoThumbState.VideoDeferred,
            };
            db.PhotoAssets.Add(row);
            db.SaveChanges();
            return row.Id;
        }

        /// <summary>ffprobe/ffmpeg without ffprobe/ffmpeg: answers from a table and paints a real PNG,
        /// so the PASS is under test rather than the binaries.</summary>
        private sealed class StandInVideoTools : IPhotoVideoTools
        {
            public double? Duration { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public DateTime? CreationTimeUtc { get; set; }
            public bool ProbeFails { get; set; }
            public bool FrameFails { get; set; }
            public bool Available => true;

            public PhotoVideoInfo? Probe(string fullPath)
            {
                if (ProbeFails) return null;
                var info = new PhotoVideoInfo
                {
                    DurationSec = Duration,
                    Width = Width,
                    Height = Height,
                    CreationTimeUtc = CreationTimeUtc,
                };
                info.Sections["ffprobe format"] = new Dictionary<string, string> { ["format_name"] = "stand-in" };
                return info;
            }

            public bool TryGrabFrame(string fullPath, double seconds, string destinationFile)
            {
                if (FrameFails) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                using var image = new Image<Rgba32>(Math.Max(1, Width ?? 64), Math.Max(1, Height ?? 48));
                image.SaveAsPng(destinationFile);
                return true;
            }
        }

        /// <summary>The minting seam, answered in-process. No Jellyfin, no gateway, no network.</summary>
        private sealed class StandInPlayback : IPhotoVideoPlayback
        {
            public bool IsConfigured { get; set; } = true;
            public bool Configured => IsConfigured;
            public string? LastItemId { get; private set; }
            public int LastUserId { get; private set; }

            public Task<PhotoVideoStartResult> StartAsync(int userId, string? userName, string jellyfinItemId,
                PhotoVideoStartRequest request, System.Threading.CancellationToken cancel = default)
            {
                LastItemId = jellyfinItemId;
                LastUserId = userId;
                return Task.FromResult(new PhotoVideoStartResult
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
}
