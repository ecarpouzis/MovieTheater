using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Books.Db.Migrations.Legs
{
    /// <inheritdoc />
    public partial class InitialV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BarcodeScan",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    CodesJson = table.Column<string>(type: "TEXT", nullable: true),
                    PagesScanned = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ScannedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BarcodeScan", x => x.ItemId);
                });

            migrationBuilder.CreateTable(
                name: "CvVolumeRaw",
                columns: table => new
                {
                    CvVolumeId = table.Column<int>(type: "INTEGER", nullable: false),
                    ConceptsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CharactersJson = table.Column<string>(type: "TEXT", nullable: true),
                    LocationsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ObjectsJson = table.Column<string>(type: "TEXT", nullable: true),
                    TeamsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvVolumeRaw", x => x.CvVolumeId);
                });

            migrationBuilder.CreateTable(
                name: "GcdIssue",
                columns: table => new
                {
                    GcdIssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    GcdSeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    SeriesName = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesYearBegan = table.Column<int>(type: "INTEGER", nullable: true),
                    Number = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    KeyDate = table.Column<string>(type: "TEXT", nullable: true),
                    PublicationDate = table.Column<string>(type: "TEXT", nullable: true),
                    ValidIsbn = table.Column<string>(type: "TEXT", nullable: true),
                    Isbn = table.Column<string>(type: "TEXT", nullable: true),
                    Barcode = table.Column<string>(type: "TEXT", nullable: true),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Price = table.Column<string>(type: "TEXT", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", nullable: true),
                    Format = table.Column<string>(type: "TEXT", nullable: true),
                    VariantOfId = table.Column<int>(type: "INTEGER", nullable: true),
                    VariantName = table.Column<string>(type: "TEXT", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StoryGenres = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GcdIssue", x => x.GcdIssueId);
                });

            migrationBuilder.CreateTable(
                name: "GcdSeries",
                columns: table => new
                {
                    GcdSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    SortName = table.Column<string>(type: "TEXT", nullable: true),
                    YearBegan = table.Column<int>(type: "INTEGER", nullable: true),
                    YearEnded = table.Column<int>(type: "INTEGER", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", nullable: true),
                    Format = table.Column<string>(type: "TEXT", nullable: true),
                    IssueCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    HasIsbn = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    HasBarcode = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Binding = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GcdSeries", x => x.GcdSeriesId);
                });

            migrationBuilder.CreateTable(
                name: "LinkCandidates",
                columns: table => new
                {
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    CandidatesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkCandidates", x => new { x.Scope, x.Key, x.Provider });
                });

            migrationBuilder.CreateTable(
                name: "LocgComicRaw",
                columns: table => new
                {
                    LocgComicId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocgSeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    SeriesName = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    IssueNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Format = table.Column<string>(type: "TEXT", nullable: true),
                    ReleaseDate = table.Column<string>(type: "TEXT", nullable: true),
                    CoverDate = table.Column<string>(type: "TEXT", nullable: true),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CommunityRating = table.Column<double>(type: "REAL", nullable: true),
                    RatingCount = table.Column<int>(type: "INTEGER", nullable: true),
                    IsKey = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    KeyType = table.Column<string>(type: "TEXT", nullable: true),
                    KeyReason = table.Column<string>(type: "TEXT", nullable: true),
                    Isbn = table.Column<string>(type: "TEXT", nullable: true),
                    Upc = table.Column<string>(type: "TEXT", nullable: true),
                    DistributorSku = table.Column<string>(type: "TEXT", nullable: true),
                    CoverPrice = table.Column<string>(type: "TEXT", nullable: true),
                    EstimatedValue = table.Column<string>(type: "TEXT", nullable: true),
                    CoverUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    StoryCount = table.Column<int>(type: "INTEGER", nullable: true),
                    StoryIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ScrapedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocgComicRaw", x => x.LocgComicId);
                });

            migrationBuilder.CreateTable(
                name: "LocgContainment",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ContainerLocgComicId = table.Column<int>(type: "INTEGER", nullable: true),
                    ContainedLocgComicId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChapterTitle = table.Column<string>(type: "TEXT", nullable: true),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    StoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    ScrapedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocgContainment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocgCreatorRaw",
                columns: table => new
                {
                    LocgComicId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    PeopleId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocgCreatorRaw", x => new { x.LocgComicId, x.Ordinal });
                });

            migrationBuilder.CreateTable(
                name: "LocgSeries",
                columns: table => new
                {
                    LocgSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", nullable: true),
                    YearBegin = table.Column<int>(type: "INTEGER", nullable: true),
                    YearEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    YearText = table.Column<string>(type: "TEXT", nullable: true),
                    IssueCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocgSeries", x => x.LocgSeriesId);
                });

            migrationBuilder.CreateTable(
                name: "LocgSeriesInference",
                columns: table => new
                {
                    GcdSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocgSeriesId = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesName = table.Column<string>(type: "TEXT", nullable: true),
                    Support = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocgSeriesInference", x => x.GcdSeriesId);
                });

            migrationBuilder.CreateTable(
                name: "MarvelIssue",
                columns: table => new
                {
                    MarvelIssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    MarvelSeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    Number = table.Column<string>(type: "TEXT", nullable: true),
                    Slug = table.Column<string>(type: "TEXT", nullable: true),
                    ScrapedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarvelIssue", x => x.MarvelIssueId);
                });

            migrationBuilder.CreateTable(
                name: "MarvelSeries",
                columns: table => new
                {
                    MarvelSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    YearStart = table.Column<int>(type: "INTEGER", nullable: true),
                    YearEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    ScrapedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarvelSeries", x => x.MarvelSeriesId);
                });

            migrationBuilder.CreateTable(
                name: "MarvelSeriesLink",
                columns: table => new
                {
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    MarvelSeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    MatchedKey = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarvelSeriesLink", x => x.SeriesId);
                });

            migrationBuilder.CreateTable(
                name: "MuSeriesRaw",
                columns: table => new
                {
                    MuSeriesId = table.Column<long>(type: "INTEGER", nullable: false),
                    GenresJson = table.Column<string>(type: "TEXT", nullable: true),
                    CategoriesJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MuSeriesRaw", x => x.MuSeriesId);
                });

            migrationBuilder.CreateTable(
                name: "OlSeriesInference",
                columns: table => new
                {
                    GcdSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    OlWorkKey = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesString = table.Column<string>(type: "TEXT", nullable: true),
                    SubjectsJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsbnSupport = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OlSeriesInference", x => x.GcdSeriesId);
                });

            migrationBuilder.CreateTable(
                name: "OpenLibraryEdition",
                columns: table => new
                {
                    Isbn = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Subtitle = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Publishers = table.Column<string>(type: "TEXT", nullable: true),
                    PublishDate = table.Column<string>(type: "TEXT", nullable: true),
                    Pages = table.Column<int>(type: "INTEGER", nullable: true),
                    SubjectsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CoverUrl = table.Column<string>(type: "TEXT", nullable: true),
                    OlEditionKey = table.Column<string>(type: "TEXT", nullable: true),
                    OlWorkKey = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesString = table.Column<string>(type: "TEXT", nullable: true),
                    PhysicalFormat = table.Column<string>(type: "TEXT", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenLibraryEdition", x => x.Isbn);
                });

            migrationBuilder.CreateTable(
                name: "OpenLibraryWork",
                columns: table => new
                {
                    WorkKey = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    SubjectsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesString = table.Column<string>(type: "TEXT", nullable: true),
                    EditionCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenLibraryWork", x => x.WorkKey);
                });

            migrationBuilder.CreateTable(
                name: "ProviderResponseCache",
                columns: table => new
                {
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestKey = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseJson = table.Column<string>(type: "TEXT", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderResponseCache", x => new { x.Provider, x.RequestKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_GcdIssue_Barcode",
                table: "GcdIssue",
                column: "Barcode");

            migrationBuilder.CreateIndex(
                name: "IX_GcdIssue_GcdSeriesId",
                table: "GcdIssue",
                column: "GcdSeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_GcdIssue_ValidIsbn",
                table: "GcdIssue",
                column: "ValidIsbn");

            migrationBuilder.CreateIndex(
                name: "IX_LocgContainment_ContainedLocgComicId",
                table: "LocgContainment",
                column: "ContainedLocgComicId");

            migrationBuilder.CreateIndex(
                name: "IX_LocgContainment_ContainerLocgComicId_ContainedLocgComicId",
                table: "LocgContainment",
                columns: new[] { "ContainerLocgComicId", "ContainedLocgComicId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenLibraryEdition_OlWorkKey",
                table: "OpenLibraryEdition",
                column: "OlWorkKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BarcodeScan");

            migrationBuilder.DropTable(
                name: "CvVolumeRaw");

            migrationBuilder.DropTable(
                name: "GcdIssue");

            migrationBuilder.DropTable(
                name: "GcdSeries");

            migrationBuilder.DropTable(
                name: "LinkCandidates");

            migrationBuilder.DropTable(
                name: "LocgComicRaw");

            migrationBuilder.DropTable(
                name: "LocgContainment");

            migrationBuilder.DropTable(
                name: "LocgCreatorRaw");

            migrationBuilder.DropTable(
                name: "LocgSeries");

            migrationBuilder.DropTable(
                name: "LocgSeriesInference");

            migrationBuilder.DropTable(
                name: "MarvelIssue");

            migrationBuilder.DropTable(
                name: "MarvelSeries");

            migrationBuilder.DropTable(
                name: "MarvelSeriesLink");

            migrationBuilder.DropTable(
                name: "MuSeriesRaw");

            migrationBuilder.DropTable(
                name: "OlSeriesInference");

            migrationBuilder.DropTable(
                name: "OpenLibraryEdition");

            migrationBuilder.DropTable(
                name: "OpenLibraryWork");

            migrationBuilder.DropTable(
                name: "ProviderResponseCache");
        }
    }
}
