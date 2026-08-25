namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// What a container says about itself: the ComicInfo.xml block for comics, the OPF package metadata for an
    /// EPUB, the document-info dictionary for a PDF. Read-only in slice 2 — the item detail endpoint shows it as
    /// the "embedded" provenance row. The scanner that WRITES it into <c>ComicEmbedded</c> is slice 5's.
    /// </summary>
    public sealed class ArchiveMetadata
    {
        // identity
        public string? IssueTitle { get; set; }
        public string? Series { get; set; }
        public string? SeriesIndex { get; set; }
        public string? AltSeries { get; set; }
        public string? AltSeriesIndex { get; set; }
        public int? AlternateCount { get; set; }
        public int? Volume { get; set; }
        public int? Count { get; set; }
        public string? SeriesGroup { get; set; }

        // publication
        public string? Publisher { get; set; }
        public string? Imprint { get; set; }
        public string? PublicationDate { get; set; }
        public string? Format { get; set; }
        public string? AgeRating { get; set; }
        public string? Language { get; set; }
        public string? Web { get; set; }
        public string? Gtin { get; set; }

        // creators
        public string? Writers { get; set; }
        public string? Pencillers { get; set; }
        public string? Inker { get; set; }
        public string? Colorist { get; set; }
        public string? Letterer { get; set; }
        public string? CoverArtist { get; set; }
        public string? Editor { get; set; }
        public string? Translator { get; set; }

        // content
        public string? Description { get; set; }
        public string? Genre { get; set; }
        public string? Tags { get; set; }
        public string? Characters { get; set; }
        public string? Teams { get; set; }
        public string? Locations { get; set; }
        public string? StoryArc { get; set; }
        public string? StoryArcNumber { get; set; }
        public string? MainCharacterOrTeam { get; set; }
        public bool? BlackAndWhite { get; set; }
        public string? Manga { get; set; }
        public string? Notes { get; set; }

        // technical
        public string? Identifier { get; set; }
        public int? PageCount { get; set; }
        public int? Rating { get; set; }
    }
}
