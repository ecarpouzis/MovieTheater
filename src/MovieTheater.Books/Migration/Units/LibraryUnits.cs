using MovieTheater.Books.Db;

namespace MovieTheater.Books.Migration.Units
{
    /// <summary>LibraryPaths → LibraryRoot.</summary>
    public sealed class RootsUnit : StageUnit
    {
        public override string Stage => "roots";
        public override string? SourceTable => "LibraryPaths";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("LibraryRoot", new { Id = r.Int("Id"), Path = MigrationContext.NormPath(r.S("Path") ?? ""), Kind = Transforms.Kind(r.I("Category")), IsCalibre = r.B("IsCalibreLibrary"), Enabled = true });
            c.Inserted++;
        }
    }

    /// <summary>Folders (+ FolderAggregates) → Folder, pass 1: every row with ParentId NULL so no batch can reference a parent a later batch inserts.</summary>
    public sealed class FoldersUnit : StageUnit
    {
        public override string Stage => "folders";
        public override string? SourceTable => "Folders";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.Int("Id");
            var path = MigrationContext.NormPath(r.S("FolderPath") ?? "");
            var rootId = ctx.RootOf(path);
            if (rootId == null) { c.Unmapped++; c.Bump("no-root"); return; }
            hot.Upsert("Folder", new
            {
                Id = id, RootId = (int)rootId.Value, ParentId = (int?)null, Kind = Transforms.Kind(r.I("Category")), Path = path,
                Name = r.S("FolderName"), NormalizedName = r.S("NormalizedName"), Depth = ctx.DepthOf(path, rootId),
                TopFolderId = (int?)ctx.TopFolderOf(id), DirectChildCount = 0, DescendantItemCount = 0,
                FolderModifiedAt = r.At("FolderModifiedAt"), IndexedAt = r.At("IndexedAt"), HasIcon = ctx.FolderIconExists(id),
            });
            c.Inserted++;
        }
    }

    /// <summary>FolderAggregates → the two counters on Folder (a third pass over the folder rows, keyed by FolderId).</summary>
    public sealed class FolderAggregatesUnit : StageUnit
    {
        public override string Stage => "folders";
        public override string? SourceTable => "FolderAggregates";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            if (!ctx.FolderExists(r.L("FolderId"))) { c.Unmapped++; c.Bump("folder-missing"); return; }
            hot.Update("Folder", "Id", r.Int("FolderId"), new { DirectChildCount = r.Int("DirectChildCount"), DescendantItemCount = r.Int("DescendantComicCount") });
            c.Inserted++;
        }
    }

    /// <summary>Folders → Folder, pass 2: the ParentId fix-up (every parent now exists).</summary>
    public sealed class FolderParentsUnit : StageUnit
    {
        public override string Stage => "folders";
        public override string? SourceTable => "Folders";
        public override string Suffix => ":parents";
        public override string? SourceWhere => "ParentId IS NOT NULL";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var parent = r.L("ParentId");
            if (!ctx.FolderExists(parent)) { c.Unmapped++; c.Bump("parent-missing"); return; }
            hot.Update("Folder", "Id", r.Int("Id"), new { ParentId = (int)parent!.Value });
            c.Inserted++;
        }
    }

    public sealed class PublishersUnit : StageUnit
    {
        public override string Stage => "publishers";
        public override string? SourceTable => "Publishers";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("Publisher", new { Id = r.Int("Id"), Name = r.S("Name") ?? "", FullName = r.S("FullName") });
            c.Inserted++;
        }
    }

    /// <summary>v1 Series → Series (ids preserved; the Resolved* columns are the resolver's).</summary>
    public sealed class SeriesUnit : StageUnit
    {
        public override string Stage => "series";
        public override string? SourceTable => "Series";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("Series", new
            {
                Id = r.Int("Id"), ParsedKey = r.S("ParsedKey"), CanonicalKey = r.S("CanonicalKey") ?? "", Name = r.S("ResolvedName"),
                DisplayNameOverride = r.T("DisplayNameOverride"), IssueCount = r.Int("IssueCount"), YearStart = r.I("YearStart"), YearEnd = r.I("YearEnd"),
                IsOngoing = r.B("IsOngoing"), Franchise = r.T("Franchise"),
                CvVolumeId = r.I("ComicvineVolumeId"), ExternalWorkId = r.I("ExternalWorkId"),
            });
            if (r.L("ComicvineVolumeId") != null && !ctx.CvVolumeExists(r.L("ComicvineVolumeId"))) c.Bump("cv-volume-not-fetched");
            c.Inserted++;
        }
    }

    public sealed class SeriesAliasesUnit : StageUnit
    {
        public override string Stage => "series-aliases";
        public override string? SourceTable => "SeriesParsedKeys";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            if (!ctx.SeriesExists(r.L("SeriesId"))) { c.Unmapped++; c.Bump("series-missing"); return; }
            hot.Upsert("SeriesAlias", new { ParsedKey = r.S("ParsedKey") ?? "", SeriesId = r.Int("SeriesId") });
            c.Inserted++;
        }
    }

    public sealed class SeriesMergesUnit : StageUnit
    {
        public override string Stage => "series-merges";
        public override string? SourceTable => "SeriesMergeLogs";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("SeriesMerge", new { OldSeriesId = r.Int("OldSeriesId"), NewSeriesId = r.I("NewSeriesId"), MergedAt = r.At("MergedAt") });
            c.Inserted++;
        }
    }

    /// <summary>
    /// Comics → Item + ItemState + ItemSignature + (ComicEmbedded | BookDetail) + ItemCredit + ItemTag. The
    /// base-entity split of v2-model.md §3: one row per concern, all keyed by the preserved Comics.Id.
    /// </summary>
    public sealed class ItemsUnit : StageUnit
    {
        public override string Stage => "items";
        public override string? SourceTable => "Comics";

        private static readonly (string Col, string Role)[] ComicInfoRoles =
        {
            ("Writers", "Writer"), ("Pencillers", "Penciller"), ("Inker", "Inker"), ("Colorist", "Colorist"),
            ("Letterer", "Letterer"), ("CoverArtist", "Cover Artist"), ("Editor", "Editor"),
        };

        private static readonly string[] EmbeddedCols =
        {
            "SeriesName", "SeriesIndex", "AltSeriesName", "AltSeriesIndex", "Volume", "IssueTitle", "Description", "Publisher", "Imprint", "Genre", "Tags",
            "Characters", "Teams", "Locations", "StoryArc", "Web", "Language", "Format", "PublicationDate", "Writers", "Pencillers", "Inker", "Colorist",
            "Letterer", "CoverArtist", "Editor", "BlackAndWhite", "Manga", "EmbeddedRating", "Identifier", "Notes", "Count", "AgeRating",
        };

        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.Int("Id");
            var kind = Transforms.Kind(r.I("Category"));
            var path = MigrationContext.NormPath(r.S("FilePath") ?? "");
            var rootId = ctx.RootOf(path);
            var folderId = r.L("ParentFolderId");
            if (rootId == null || !ctx.FolderExists(folderId)) { c.Unmapped++; c.Bump(rootId == null ? "no-root" : "folder-missing"); return; }
            var publisherId = ctx.PublisherExists(r.L("PublisherId")) ? r.I("PublisherId") : null;
            var topFolder = ctx.FolderExists(r.L("FolderGroupId")) ? r.I("FolderGroupId") : null;

            hot.Upsert("Item", new
            {
                Id = id, RootId = (int)rootId.Value, FolderId = (int)folderId!.Value, TopFolderId = topFolder, Kind = kind, Path = path,
                FileName = r.S("FileName") ?? Path.GetFileName(path), Extension = r.S("FileExtension"), ContainerFormat = Transforms.Container(r.S("FileExtension")),
                FileSize = r.L("FileSize") ?? 0, FileModifiedAt = r.At("FileModifiedAt"), IndexedAt = r.At("IndexedAt"), PageCount = r.I("PageCount"),
                Title = r.S("Title"), NormalizedTitle = r.S("NormalizedTitle"), CalibreBookId = kind == ItemKind.Book ? (int?)ctx.CalibreBookId(id) : null,
                PublisherId = publisherId, IsExcluded = r.B("ExcludedFromLibrary"), KeepInDirectory = r.B("KeepInDirectory"),
            });
            hot.Upsert("ItemState", new
            {
                ItemId = id, IsBroken = r.B("IsBroken"), BrokenReason = r.T("BrokenReason"), BrokenCheckedAt = r.At("BrokenCheckedAt"),
                ThumbnailError = r.T("ThumbnailError"), ThumbnailCheckedAt = r.At("ThumbnailCheckedAt"), CoverWidth = r.I("CoverWidth"), CoverHeight = r.I("CoverHeight"),
                CoverDimsComputedFor = r.T("CoverDimsComputedFor"), ExclusionReason = r.T("ExclusionReason"), ExcludedAt = r.At("ExcludedAt"),
            });
            if (r.T("ContentFingerprint") != null || r.L("CoverPHash") != null || r.T("PageSignature") != null || r.T("SignaturesComputedFor") != null)
                hot.Upsert("ItemSignature", new { ItemId = id, ContentFingerprint = r.T("ContentFingerprint"), CoverPHash = r.L("CoverPHash"), PageSignature = r.T("PageSignature"), SignaturesComputedFor = r.T("SignaturesComputedFor") });

            var ordinal = 0;
            var tags = new HashSet<(string, string, TagSource)>();
            if (kind == ItemKind.Comic)
            {
                if (EmbeddedCols.Any(col => r.Raw(col) != null))
                {
                    hot.Upsert("ComicEmbedded", new
                    {
                        ItemId = id, Series = r.S("SeriesName"), Number = r.S("SeriesIndex"), AltSeries = r.S("AltSeriesName"), AltNumber = r.S("AltSeriesIndex"), Volume = r.I("Volume"),
                        Title = r.S("IssueTitle"), Summary = r.S("Description"), Publisher = r.S("Publisher"), Imprint = r.S("Imprint"), Genre = r.S("Genre"), Tags = r.S("Tags"),
                        Characters = r.S("Characters"), Teams = r.S("Teams"), Locations = r.S("Locations"), StoryArc = r.S("StoryArc"), Web = r.S("Web"), Language = r.S("Language"),
                        Format = r.S("Format"), PublicationDate = r.S("PublicationDate"), Writers = r.S("Writers"), Pencillers = r.S("Pencillers"), Inker = r.S("Inker"), Colorist = r.S("Colorist"),
                        Letterer = r.S("Letterer"), CoverArtist = r.S("CoverArtist"), Editor = r.S("Editor"), BlackAndWhite = r.L("BlackAndWhite") is long bw ? (bool?)(bw != 0) : null,
                        Manga = r.S("Manga"), Rating = r.I("EmbeddedRating"), Identifier = r.S("Identifier"), Notes = r.S("Notes"), Count = r.I("Count"), AgeRating = r.S("AgeRating"),
                    });
                    c.Bump("embedded");
                }
                foreach (var (col, role) in ComicInfoRoles)
                    foreach (var name in Transforms.SplitNames(r.S(col)))
                        hot.Upsert("ItemCredit", new { ItemId = id, Source = TagSource.ComicInfo, Ordinal = ordinal++, Role = role, Name = name, NormalizedName = Transforms.NormalizeName(name), ProviderPersonId = (string?)null });
                foreach (var g in Transforms.SplitTags(r.S("Genre")))
                    tags.Add(("genre", g, ctx.IsCvdbResolvedName(g) ? TagSource.Cv : TagSource.ComicInfo));
                foreach (var t in Transforms.SplitTags(r.S("Tags")))
                    tags.Add(("tag", t, TagSource.ComicInfo));
            }
            else
            {
                hot.Upsert("BookDetail", new
                {
                    ItemId = id, Isbn = r.T("Identifier"), SeriesName = r.T("SeriesName"), SeriesIndex = r.D("SeriesIndex"), Publisher = r.T("Publisher"),
                    PublishedOn = r.T("PublicationDate"), Language = r.T("Language"), Description = r.S("Description"),
                });
                foreach (var name in Transforms.SplitNames(r.S("Writers")))
                    hot.Upsert("ItemCredit", new { ItemId = id, Source = TagSource.Calibre, Ordinal = ordinal++, Role = "Author", Name = name, NormalizedName = Transforms.NormalizeName(name), ProviderPersonId = (string?)null });
                foreach (var t in Transforms.SplitTags(r.S("Tags"))) tags.Add(("tag", t, TagSource.Calibre));
                foreach (var g in Transforms.SplitTags(r.S("Genre"))) tags.Add(("genre", g, TagSource.Calibre));
            }
            foreach (var (cat, val, src) in tags)
                hot.Upsert("ItemTag", new { ItemId = id, Category = cat, Value = val, Source = src });
            c.Bump("credits", ordinal);
            c.Bump("tags", tags.Count);
            c.Inserted++;
        }

        public override void Finalize(MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            c.Bump("calibre-links-loaded", ctx.CalibreLinkCount);
        }
    }

    /// <summary>ComicParsedDetails → ComicDetail, and Item.SeriesId materialized from the v1 resolution.</summary>
    public sealed class ComicDetailsUnit : StageUnit
    {
        public override string Stage => "comic-details";
        public override string? SourceTable => "ComicParsedDetails";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.L("ComicId");
            if (!ctx.ItemExists(id)) { c.Unmapped++; c.Bump("item-missing"); return; }
            var format = Transforms.Format(r.S("Format"));
            if (format == ComicFormat.Unknown && r.T("Format") is string raw && raw != "null") c.Bump("format-unknown");
            hot.Upsert("ComicDetail", new
            {
                ItemId = (int)id!.Value, ParsedSeriesKey = r.S("Series"), IssueNo = r.S("IssueNo"), Year = r.I("Year"), VolumeNo = r.I("VolumeNo"), Publisher = r.S("Publisher"),
                Format = format, FormatRaw = r.T("Format"), IsCollection = r.B("IsCollection"), EventName = r.T("EventName"), IssueTitle = r.T("IssueTitle"),
                Confidence = Transforms.ConfidenceOf(r.S("Confidence")), SeriesSource = Transforms.ParseSourceOf(r.S("SeriesSource")), IssueSource = Transforms.ParseSourceOf(r.S("IssueSource")),
                YearSource = Transforms.ParseSourceOf(r.S("YearSource")), PublisherSource = Transforms.ParseSourceOf(r.S("PublisherSource")),
                FolderSeries = r.T("FolderSeries"), FolderYear = r.I("FolderYear"), ParseNotes = r.T("ParseNotes"), ParsedAt = r.At("ParsedAt"),
            });
            var seriesId = r.L("SeriesId");
            if (seriesId != null)
            {
                if (ctx.SeriesExists(seriesId)) hot.Update("Item", "Id", (int)id.Value, new { SeriesId = (int)seriesId.Value });
                else c.Bump("series-missing");
            }
            c.Inserted++;
        }
    }
}
