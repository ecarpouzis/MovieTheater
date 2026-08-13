using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The on-disk shape of a curation export (docs/photos-plan.md §2.11). This vertical's value is
    /// irreplaceable human labor — person tags, hand-set dates, master picks, albums, captions — and
    /// "the DB is backed up" must not be the only answer, so the whole of it is dumped as versioned
    /// JSON that a rebuilt database can absorb.
    ///
    /// <para><b>Everything is keyed by content hash + relative path</b>, never by row id. Ids are local
    /// to one database; an export exists precisely for the case where that database is gone. The path
    /// is the readable half and the hash is the durable one (§2.5's identity rule), so an export
    /// re-applies after the folder reorganizations this collection is guaranteed to see.</para>
    /// </summary>
    public static class PhotoCurationExportFormat
    {
        /// <summary>
        /// Bumped when a reader would MISREAD an older file. Additive fields do not bump it — an older
        /// export must stay importable, since the reason to have one is that it is old.
        ///
        /// <para><b>2 (Phase 3)</b> adds the curation-batch section: the review state that used to live
        /// as JSON under <c>PhotosReportDir</c> and now lives in rows, and which is therefore now
        /// something a rebuilt database would otherwise lose. A v1 export is still read — its missing
        /// section simply imports as zero rows, which is exactly what it means.</para>
        /// </summary>
        public const int Version = 2;

        public const string ManifestFile = "manifest.json";
        public const string AssetsFile = "assets.json";
        public const string PeopleFile = "people.json";
        public const string PersonTagsFile = "person-tags.json";
        public const string AlbumsFile = "albums.json";
        public const string DupeGroupsFile = "dupe-groups.json";
        public const string GoogleItemsFile = "google-items.json";
        public const string CurationBatchesFile = "curation-batches.json";

        /// <summary>Section order is also IMPORT order: people before the tags that name them, assets
        /// before everything that points at one. New sections are APPENDED — the import's cursor is a
        /// <c>section:index</c> pair, so inserting one in the middle would make an in-flight cursor
        /// resume in the wrong place.</summary>
        public static readonly IReadOnlyList<string> Sections = new[]
        {
            AssetsFile, PeopleFile, PersonTagsFile, AlbumsFile, DupeGroupsFile, GoogleItemsFile,
            CurationBatchesFile,
        };

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public sealed class PhotoExportManifest
    {
        public int Version { get; set; } = PhotoCurationExportFormat.Version;

        public DateTime CreatedUtc { get; set; }

        /// <summary>Which sections finished. Present-and-complete is what makes a resumed export
        /// distinguishable from a killed one.</summary>
        public List<string> Sections { get; set; } = new List<string>();

        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Set only when every section landed. A partial export is still readable — the import
        /// simply reports the sections it did not find.</summary>
        public bool Complete { get; set; }
    }

    /// <summary>How every reference to an asset is written (§2.11): content hash first, relative path
    /// second. Both are carried because either can be the one that survives — a hash is useless before
    /// the hash pass has run, and a path is useless after a reorganization.</summary>
    public class PhotoAssetKey
    {
        public string? Sha256 { get; set; }

        public string Path { get; set; } = "";
    }

    /// <summary>An asset's own curation: the flags and the hand-set dates. The pixels, the EXIF and the
    /// derivatives are all re-derivable from the files, so none of them are here.</summary>
    public sealed class PhotoAssetExport : PhotoAssetKey
    {
        public long SizeBytes { get; set; }

        public bool Hidden { get; set; }

        /// <summary>Phase 7 (§2.12): which shelf this asset was filed on. Additive, so it does not bump
        /// the format version — an older export carries none, which reads as
        /// <see cref="MovieTheater.Db.PhotoShelf.Timeline"/>, and that is true of every asset written
        /// before the Gallery existed. Exported because filing a thousand memes off the timeline is
        /// human labor of exactly the kind §2.11 exists to protect.</summary>
        public string? Shelf { get; set; }

        public DateTime? TakenAt { get; set; }

        public DateTime? TakenAtUtcRaw { get; set; }

        public string TakenAtSource { get; set; } = "";

        public int? YearMin { get; set; }

        public int? YearMax { get; set; }

        public string? LocationLabel { get; set; }

        public string? LocationSource { get; set; }

        /// <summary>Kept for provenance only; the import never re-stamps it, because on the rebuilt
        /// database the row belongs to whichever ingest actually created it.</summary>
        public string? IngestBatch { get; set; }
    }

    public sealed class PhotoPersonExport
    {
        /// <summary>Export-local id, so tags can name a person unambiguously even when two people share
        /// a display name. It is NOT the row id on import — it is resolved to a local person by name.</summary>
        public int Key { get; set; }

        public string Name { get; set; } = "";

        public int? BirthYear { get; set; }

        /// <summary>The linked site login by USERNAME, not by id — ids are local to a database.</summary>
        public string? UserName { get; set; }

        public PhotoAssetKey? CoverAsset { get; set; }

        public string? ImmichPersonId { get; set; }

        public DateTime CreatedUtc { get; set; }
    }

    public sealed class PhotoPersonTagExport
    {
        public int PersonKey { get; set; }

        /// <summary>The person's name, carried on the tag as well as in the people section. Denormalized
        /// deliberately: an import is chunked and may resume in a LATER PROCESS, where the key→person
        /// map built by the people section no longer exists — a tag has to be resolvable on its own.</summary>
        public string PersonName { get; set; } = "";

        public PhotoAssetKey Asset { get; set; } = new PhotoAssetKey();

        public string Source { get; set; } = "";

        public double? Confidence { get; set; }

        public double? BoxX { get; set; }

        public double? BoxY { get; set; }

        public double? BoxW { get; set; }

        public double? BoxH { get; set; }

        public string? ImmichPersonId { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime? ConfirmedUtc { get; set; }
    }

    public sealed class PhotoAlbumExport
    {
        public string Title { get; set; } = "";

        /// <summary>The album's identity across databases: a link someone may have sent.</summary>
        public string Slug { get; set; } = "";

        public string? Description { get; set; }

        public PhotoAssetKey? CoverAsset { get; set; }

        public DateTime? RangeStart { get; set; }

        public DateTime? RangeEnd { get; set; }

        public int SortOrder { get; set; }

        /// <summary>Phase 7 (§2.12): which index this album belongs on. Additive; absent reads as the
        /// family album shelf, which is what every album written before the Gallery was.</summary>
        public string? Shelf { get; set; }

        /// <summary>Phase 7 (§2.12): the artist, when this is an artist collection. Additive and
        /// nullable in both directions — most albums have none, and that is not a missing value.</summary>
        public string? ArtistName { get; set; }

        public string? CreatedByUserName { get; set; }

        public DateTime CreatedUtc { get; set; }

        public List<PhotoAlbumEntryExport> Entries { get; set; } = new List<PhotoAlbumEntryExport>();
    }

    public sealed class PhotoAlbumEntryExport
    {
        public PhotoAssetKey Asset { get; set; } = new PhotoAssetKey();

        public int SortOrder { get; set; }

        public string? Caption { get; set; }
    }

    public sealed class PhotoDupeGroupExport
    {
        public string Kind { get; set; } = "";

        public string Status { get; set; } = "";

        public DateTime CreatedUtc { get; set; }

        public DateTime? ResolvedUtc { get; set; }

        public List<PhotoDupeMemberExport> Members { get; set; } = new List<PhotoDupeMemberExport>();
    }

    public sealed class PhotoDupeMemberExport
    {
        public PhotoAssetKey Asset { get; set; } = new PhotoAssetKey();

        public bool IsMaster { get; set; }

        public double? Similarity { get; set; }
    }

    /// <summary>
    /// A review batch (§2.5 ingest quarantine / §2.9 hide proposals) as it travels between databases.
    /// It is here because Phase 3 turned it into rows: a decision a family member made about ten
    /// thousand screenshots is exactly the "irreplaceable human labor" §2.11 exists to protect, and
    /// before Phase 3 an export could not carry it at all.
    /// </summary>
    public sealed class PhotoCurationBatchExport
    {
        public string Kind { get; set; } = "";

        /// <summary>The batch's own name — the ingest marker, or the proposal id. Its identity across
        /// databases, together with the kind.</summary>
        public string BatchId { get; set; } = "";

        public string Status { get; set; } = "";

        public DateTime CreatedUtc { get; set; }

        public DateTime? DecidedUtc { get; set; }

        /// <summary>Who decided, by USERNAME — ids are local to a database.</summary>
        public string? DecidedByUserName { get; set; }

        public int AppliedCount { get; set; }

        public string? Cursor { get; set; }

        public bool Complete { get; set; }

        public List<PhotoCurationBatchItemExport> Items { get; set; } = new List<PhotoCurationBatchItemExport>();
    }

    public sealed class PhotoCurationBatchItemExport
    {
        public PhotoAssetKey Asset { get; set; } = new PhotoAssetKey();

        public string Rule { get; set; } = "";
    }

    public sealed class PhotoGoogleItemExport
    {
        public string TakeoutFileName { get; set; } = "";

        public string? TakeoutRelativePath { get; set; }

        public DateTime? TakenAtUtc { get; set; }

        public long? SizeBytes { get; set; }

        public string? SidecarJson { get; set; }

        public PhotoAssetKey? MatchedAsset { get; set; }

        public string Status { get; set; } = "";

        public string? MatchMethod { get; set; }

        /// <summary>Phase 6: the pHash distance a third-rung match was accepted at (§2.10). Additive, so
        /// it does not bump the format version — a v2 export simply carries none, which reads as "this
        /// match was not made by resemblance", and that is true of every match made before Phase 6.</summary>
        public int? MatchDistance { get; set; }

        /// <summary>Phase 6: which fields the sidecar disagreed with the local row about. Exported
        /// because a disagreement is the pass's REVIEW OUTPUT — the thing a human is meant to look at —
        /// and an export that dropped it would restore a mesh with its questions already erased.</summary>
        public string? Disagreements { get; set; }

        /// <summary>Phase 6: where the download lane put a Google-only item, when it ran (§2.10).</summary>
        public string? DownloadedPath { get; set; }

        public DateTime FirstSeenUtc { get; set; }

        public DateTime LastSeenUtc { get; set; }
    }
}
