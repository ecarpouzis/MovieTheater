using System.Xml.Linq;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// ComicInfo.xml → <see cref="ArchiveMetadata"/>. Shared by every reader that can find one, so the parse
    /// rules (and the Year/Month/Day → one date string fold) exist once.
    /// </summary>
    internal static class ComicInfoParser
    {
        public static ArchiveMetadata Parse(XElement root) => new()
        {
            IssueTitle = Str(root, "Title"),
            Series = Str(root, "Series"),
            SeriesIndex = Str(root, "Number"),
            AltSeries = Str(root, "AlternateSeries"),
            AltSeriesIndex = Str(root, "AlternateNumber"),
            AlternateCount = Int(root, "AlternateCount"),
            Volume = Int(root, "Volume"),
            Count = Int(root, "Count"),
            SeriesGroup = Str(root, "SeriesGroup"),

            Publisher = Str(root, "Publisher"),
            Imprint = Str(root, "Imprint"),
            PublicationDate = BuildPublicationDate(root),
            Format = Str(root, "Format"),
            AgeRating = Str(root, "AgeRating"),
            Language = Str(root, "LanguageISO"),
            Web = Str(root, "Web"),
            Gtin = Str(root, "GTIN"),

            Writers = Str(root, "Writer"),
            Pencillers = Str(root, "Penciller"),
            Inker = Str(root, "Inker"),
            Colorist = Str(root, "Colorist"),
            Letterer = Str(root, "Letterer"),
            CoverArtist = Str(root, "CoverArtist"),
            Editor = Str(root, "Editor"),
            Translator = Str(root, "Translator"),

            Description = Str(root, "Summary"),
            Genre = Str(root, "Genre"),
            Tags = Str(root, "Tags"),
            Characters = Str(root, "Characters"),
            Teams = Str(root, "Teams"),
            Locations = Str(root, "Locations"),
            StoryArc = Str(root, "StoryArc"),
            StoryArcNumber = Str(root, "StoryArcNumber"),
            MainCharacterOrTeam = Str(root, "MainCharacterOrTeam"),
            BlackAndWhite = ParseBool(root, "BlackAndWhite"),
            Manga = Str(root, "Manga"),
            Notes = Str(root, "Notes"),

            Identifier = Str(root, "Identifier"),
            PageCount = Int(root, "PageCount"),
            Rating = Int(root, "CommunityRating"),
        };

        private static string? Str(XElement root, string name) =>
            root.Element(name)?.Value is { Length: > 0 } v ? v : null;

        private static int? Int(XElement root, string name) =>
            int.TryParse(root.Element(name)?.Value, out var n) ? n : null;

        private static bool? ParseBool(XElement root, string name) =>
            root.Element(name)?.Value?.Trim().ToLowerInvariant() switch
            {
                "yes" => true,
                "no" => false,
                _ => null,
            };

        /// <summary>ComicInfo splits the date over three elements; the model stores one partial-date string.</summary>
        private static string? BuildPublicationDate(XElement root)
        {
            var year = root.Element("Year")?.Value;
            var month = root.Element("Month")?.Value;
            var day = root.Element("Day")?.Value;
            if (string.IsNullOrEmpty(year)) return null;

            var result = year;
            if (string.IsNullOrEmpty(month)) return result;
            result += "-" + month.PadLeft(2, '0');
            if (!string.IsNullOrEmpty(day)) result += "-" + day.PadLeft(2, '0');
            return result;
        }
    }
}
