using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;

namespace MovieTheater.Web
{
    /// <summary>
    /// The PNG→WebP thumbnail conversion, in ONE place: the <c>thumbs-recode</c> CLI and the
    /// <see cref="ThumbsRecodeService"/> that runs it on the pod both call this, so what a hand run does
    /// and what the server does overnight cannot diverge.
    ///
    /// <para><b>Which files.</b> Two populations, and between them every thumbnail the site serves: the
    /// root's <c>*_s.png</c> (<c>{id}_s.png</c>, <c>{bucket}_{id}_s.png</c>, <c>music_{id}_s.png</c>) and
    /// everything under <c>arcade/</c> — box art is written only by <c>ArcadeBoxArt.Thumbnail</c>, so that
    /// whole tree is thumbnails, and unlike a poster it has no full-size counterpart. The full-size
    /// <c>{id}.png</c> originals at the root are NEVER opened: they are what the detail views serve and
    /// the source for any future re-encode, which is the whole reason a thumbnail may be lossy.</para>
    ///
    /// <para><b>The guards.</b> A file is rewritten only if it decodes, re-encodes, decodes AGAIN at the
    /// same pixel size, and comes out smaller. Anything else keeps the bytes it has — a thumbnail that
    /// will not survive the round trip is left exactly as it was and counted, never deleted. The write is
    /// a temp file in the same directory moved over the original, so a crash cannot leave a truncated
    /// image where a cover used to be.</para>
    /// </summary>
    public static class ThumbRecoder
    {
        /// <summary>The state file the SERVICE keeps beside the images it is converting.</summary>
        public const string StateFileName = "thumbs-recode.state.json";

        public sealed record Outcome(bool Rewritten, string Reason, int Before, int After);

        /// <summary>
        /// Every candidate, as a path RELATIVE to the root, ordered ordinally. The ordering is the
        /// contract: a cursor only means "everything after this" if the walk is deterministic.
        /// </summary>
        public static List<string> Candidates(string dir)
        {
            var arcadeDir = Path.Combine(dir, "arcade");
            return Directory.EnumerateFiles(dir, "*_s.png", SearchOption.TopDirectoryOnly)
                .Concat(Directory.Exists(arcadeDir)
                    // The arcade tree is walked whole, so it must be filtered by EXTENSION: `*.*` also
                    // matched a crashed run's `.webp.tmp` leftovers and anything else that ever lands
                    // there. A `.version` sidecar at the root is already safe — `*_s.png` does not match
                    // `1_s.png.version` on .NET 10 (verified; the legacy 8.3 pattern quirk is gone).
                    ? Directory.EnumerateFiles(arcadeDir, "*.*", SearchOption.AllDirectories)
                        .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
                    : Enumerable.Empty<string>())
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>What may be a cached cover. Anything else in the tree is left alone.</summary>
        private static readonly HashSet<string> ImageExtensions =
            new(new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" }, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Convert one file. <paramref name="apply"/> false does everything except the write, so a dry run
        /// and a real run take exactly the same decisions.
        /// </summary>
        public static async Task<Outcome> RecodeAsync(string dir, string rel, int quality, bool apply, CancellationToken ct = default)
        {
            var file = Path.Combine(dir, rel);
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); }
            // Cancellation propagates for the same reason it does on the write below: a shutdown is not a
            // failed file, and the caller must not step its cursor past one it never looked at.
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new Outcome(false, "unreadable: " + ex.Message, 0, 0); }

            if (ImageBytes.ContentTypeOf(bytes) != ImageBytes.Png)
                return new Outcome(false, "already " + ImageBytes.ContentTypeOf(bytes), bytes.Length, bytes.Length);

            byte[] encoded;
            int w, h;
            try
            {
                using var img = Image.Load(bytes);
                w = img.Width; h = img.Height;
                using var ms = new MemoryStream();
                img.Save(ms, new WebpEncoder { Quality = quality, FileFormat = WebpFileFormatType.Lossy });
                encoded = ms.ToArray();
            }
            catch (Exception ex) { return new Outcome(false, "will not decode/encode: " + ex.Message, bytes.Length, bytes.Length); }

            if (encoded.Length >= bytes.Length)
                return new Outcome(false, $"webp not smaller ({encoded.Length} >= {bytes.Length})", bytes.Length, bytes.Length);

            try
            {
                using var check = Image.Load(encoded);
                if (check.Width != w || check.Height != h)
                    return new Outcome(false, $"re-decode {check.Width}x{check.Height} != {w}x{h}", bytes.Length, bytes.Length);
            }
            catch (Exception ex) { return new Outcome(false, "re-decode failed: " + ex.Message, bytes.Length, bytes.Length); }

            if (!apply) return new Outcome(true, "would rewrite", bytes.Length, encoded.Length);

            var tmp = file + ".webp.tmp";
            try
            {
                await File.WriteAllBytesAsync(tmp, encoded, ct);
                File.Move(tmp, file, overwrite: true);
                return new Outcome(true, "rewritten", bytes.Length, encoded.Length);
            }
            // A shutdown is not a failure and must not be counted as one — nor may the caller advance its
            // cursor past a file that is still PNG, or that file would never be converted again.
            catch (OperationCanceledException)
            {
                TryDelete(tmp);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                return new Outcome(false, "write failed: " + ex.Message, bytes.Length, bytes.Length);
            }
        }

        /// <summary>A half-written temp file is litter on a shared mount; drop it rather than leave it.</summary>
        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }
}
