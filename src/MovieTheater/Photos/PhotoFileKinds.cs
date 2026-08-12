using System;
using System.Collections.Generic;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>
    /// What a file extension means to the photo pipeline (photos-plan.md §2.5 phase 1: "kind by
    /// extension" is decided during the cheap inventory pass, before any byte is read).
    ///
    /// <para>Three independent questions, deliberately not collapsed into one:</para>
    /// <list type="bullet">
    /// <item><b>Is it ours at all</b> — photo, video, or ignored clutter (sidecars, thumbs.db, …).</item>
    /// <item><b>Can a BROWSER render the original</b> — decides deep-zoom at mint time
    /// (<c>PhotoAsset.OriginalRenderable</c>, §2.2). JPEG/PNG/WebP/GIF/BMP/AVIF yes; HEIC/TIFF/RAW no.</item>
    /// <item><b>Can THIS BUILD decode the pixels</b> — ImageSharp's decoder set. It is not the same
    /// question: TIFF decodes here but no browser shows it, AVIF shows in a browser but does not decode
    /// here. A format that fails this one still gets a full row, EXIF and hashes; only the derivatives
    /// are absent (<see cref="PhotoThumbState.UnsupportedFormat"/>).</item>
    /// </list>
    /// </summary>
    public static class PhotoFileKinds
    {
        private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff",
            ".heic", ".heif", ".avif",
            // RAW. Catalogued so a RAW+JPEG pair is visible to Phase 3's Variant grouping; not decoded.
            ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".srw", ".pef",
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".wmv", ".mpg", ".mpeg", ".m2ts", ".mts",
            ".3gp", ".3g2", ".webm", ".flv", ".vob", ".mod", ".divx", ".asf",
        };

        /// <summary>Formats a browser displays from the untouched file. BMP and AVIF are included on
        /// top of §2.2's JPEG/PNG/WebP/GIF list — both are natively rendered by every browser this site
        /// targets, and marking them non-renderable would cost a 3200px derivative for nothing.</summary>
        private static readonly HashSet<string> BrowserRenderable = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".webp", ".bmp", ".avif",
        };

        /// <summary>What ImageSharp 3.x decodes. HEIC/HEIF/AVIF and RAW are absent deliberately —
        /// decoding them needs Magick.NET (a large native dependency); Phase 1 catalogues them fully
        /// and leaves their derivatives to a later, deliberate decision.</summary>
        private static readonly HashSet<string> Decodable = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".tga", ".pbm", ".qoi",
        };

        /// <summary>Files that live beside media and are not media: sidecars, OS droppings, Takeout
        /// JSON. Never rows.</summary>
        private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".xmp", ".txt", ".ini", ".db", ".ds_store", ".thm", ".lnk", ".url", ".aae", ".pdf",
        };

        public static bool TryClassify(string extension, out PhotoAssetKind kind)
        {
            kind = PhotoAssetKind.Photo;
            if (string.IsNullOrEmpty(extension)) return false;
            if (PhotoExtensions.Contains(extension)) { kind = PhotoAssetKind.Photo; return true; }
            if (VideoExtensions.Contains(extension)) { kind = PhotoAssetKind.Video; return true; }
            return false;
        }

        public static bool IsIgnored(string extension) => Ignored.Contains(extension);

        public static bool IsBrowserRenderable(string extension) => BrowserRenderable.Contains(extension);

        public static bool IsDecodable(string extension) => Decodable.Contains(extension);
    }
}
