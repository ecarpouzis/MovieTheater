using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Services;
using MovieTheater.Web;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The overnight PNG→WebP pass. It runs unattended on the pod against the real images mount, so the
    /// properties that matter are the ones nobody will be awake to check: it converts only thumbnails, it
    /// resumes rather than restarts, it stops, and a second run is free.
    /// </summary>
    public class ThumbsRecodeServiceTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "mt-thumbs-" + Guid.NewGuid().ToString("N"));

        public ThumbsRecodeServiceTests() => Directory.CreateDirectory(dir);
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        private static byte[] Png(int w = 200, int h = 200)
        {
            using var img = new Image<Rgba32>(w, h);
            var rng = new Random(3);
            img.ProcessPixelRows(a =>
            {
                for (var y = 0; y < a.Height; y++)
                {
                    var row = a.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                        row[x] = new Rgba32((byte)(x + rng.Next(30)), (byte)(y + rng.Next(30)), (byte)((x ^ y) & 0xFF));
                }
            });
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            return ms.ToArray();
        }

        private void Write(string rel, byte[] bytes)
        {
            var p = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }

        private ThumbsRecodeService Service(int chunk) => new(
            new MovieTheaterConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()) { MoviePostersDir = dir },
            NullLogger<ThumbsRecodeService>.Instance,
            new ThumbsRecodeOptions
            {
                Enabled = true,
                ChunkSize = chunk,
                Pause = TimeSpan.FromMilliseconds(1),
                StartDelay = TimeSpan.Zero,
            });

        private ThumbsRecodeState State() =>
            JsonSerializer.Deserialize<ThumbsRecodeState>(File.ReadAllText(Path.Combine(dir, ThumbRecoder.StateFileName)))!;

        private static async Task RunToCompletionAsync(ThumbsRecodeService svc)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await svc.StartAsync(cts.Token);
            // StartAsync only KICKS OFF ExecuteAsync; awaiting the ExecuteTask is what waits for the walk
            // to run itself out. StopAsync would cancel it instead, which is the opposite of the test.
            await svc.ExecuteTask!;
            await svc.StopAsync(cts.Token);
        }

        [Fact]
        public async Task It_converts_every_thumbnail_leaves_the_full_size_originals_and_stops()
        {
            Write("12_s.png", Png());
            Write("music_7_s.png", Png());
            Write("arcade/snes/3001.png", Png());
            Write("arcade/n64/4002-g1.png", Png());
            Write("12.png", Png(400, 600));           // full-size original — must not be touched
            var fullSizeBefore = File.ReadAllBytes(Path.Combine(dir, "12.png"));

            await RunToCompletionAsync(Service(chunk: 2));   // forces several chunks

            foreach (var rel in new[] { "12_s.png", "music_7_s.png", "arcade/snes/3001.png", "arcade/n64/4002-g1.png" })
                Assert.Equal(ImageBytes.Webp, ImageBytes.ContentTypeOf(File.ReadAllBytes(Path.Combine(dir, rel))));

            // The full-size original is byte-identical: it is what the detail views serve and the source
            // for any future re-encode.
            Assert.Equal(fullSizeBefore, File.ReadAllBytes(Path.Combine(dir, "12.png")));

            var s = State();
            Assert.NotNull(s.DoneUtc);              // it TERMINATES rather than looping forever
            Assert.Equal(4, s.Rewritten);
            Assert.Equal(0, s.Failed);
            Assert.True(s.BytesAfter < s.BytesBefore);
        }

        /// <summary>
        /// A build whose scope covers MORE than the one that finished must re-open the walk. The first
        /// version stamped itself complete having never looked at BoardgameImagesDir — a different
        /// directory entirely — so without this a root added later would be silently ignored forever.
        /// </summary>
        [Fact]
        public async Task A_wider_scope_reopens_a_finished_walk()
        {
            Write("a_s.png", Png());
            File.WriteAllText(Path.Combine(dir, ThumbRecoder.StateFileName),
                JsonSerializer.Serialize(new ThumbsRecodeState
                {
                    Cursor = "zzz", DoneUtc = DateTime.UtcNow, Scope = "an-older-narrower-scope",
                }));

            await RunToCompletionAsync(Service(chunk: 10));

            Assert.Equal(ImageBytes.Webp, ImageBytes.ContentTypeOf(File.ReadAllBytes(Path.Combine(dir, "a_s.png"))));
            Assert.Equal(ThumbRecoder.Scope, State().Scope);
            Assert.NotNull(State().DoneUtc);
        }

        [Fact]
        public async Task It_resumes_from_the_cursor_instead_of_starting_over()
        {
            Write("a_s.png", Png());
            Write("b_s.png", Png());
            Write("c_s.png", Png());

            // A run that was killed after one file: the state file is all that survives.
            File.WriteAllText(Path.Combine(dir, ThumbRecoder.StateFileName),
                JsonSerializer.Serialize(new ThumbsRecodeState { Cursor = "posters|a_s.png", Processed = 1, Rewritten = 1, Scope = ThumbRecoder.Scope }));
            var untouched = File.ReadAllBytes(Path.Combine(dir, "a_s.png"));

            await RunToCompletionAsync(Service(chunk: 10));

            // a_s.png is BEFORE the cursor, so it was never re-read — still the PNG we wrote.
            Assert.Equal(untouched, File.ReadAllBytes(Path.Combine(dir, "a_s.png")));
            Assert.Equal(ImageBytes.Webp, ImageBytes.ContentTypeOf(File.ReadAllBytes(Path.Combine(dir, "b_s.png"))));
            Assert.Equal(ImageBytes.Webp, ImageBytes.ContentTypeOf(File.ReadAllBytes(Path.Combine(dir, "c_s.png"))));
            // The counters CONTINUE the earlier run rather than resetting.
            Assert.Equal(3, State().Processed);
        }

        [Fact]
        public async Task A_second_run_is_free_and_a_broken_file_is_counted_not_deleted()
        {
            Write("ok_s.png", Png());
            Write("broken_s.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 }); // PNG magic, no image
            await RunToCompletionAsync(Service(chunk: 10));

            Assert.Equal(ImageBytes.Webp, ImageBytes.ContentTypeOf(File.ReadAllBytes(Path.Combine(dir, "ok_s.png"))));
            // Still there, still the bytes it had: a file that will not survive the round trip is stepped
            // over and counted, never removed.
            Assert.Equal(8, new FileInfo(Path.Combine(dir, "broken_s.png")).Length);
            Assert.Equal(1, State().Failed);
            Assert.Equal(1, State().Rewritten);

            // Done is done: a fresh service over the same directory does no work at all.
            var doneAt = State().DoneUtc;
            await RunToCompletionAsync(Service(chunk: 10));
            Assert.Equal(doneAt, State().DoneUtc);
            Assert.Equal(1, State().Rewritten);
            // And no temp files were left behind anywhere.
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories));
        }
    }
}
