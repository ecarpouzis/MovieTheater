using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Books.Db.Migrations.Hot
{
    /// <inheritdoc />
    public partial class InitialV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BarneyProg",
                columns: table => new
                {
                    ProgNo = table.Column<int>(type: "INTEGER", nullable: false),
                    CoverDate = table.Column<string>(type: "TEXT", nullable: true),
                    Price = table.Column<string>(type: "TEXT", nullable: true),
                    StripsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ScrapedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BarneyProg", x => x.ProgNo);
                });

            migrationBuilder.CreateTable(
                name: "CvdbResolution",
                columns: table => new
                {
                    CvdbTag = table.Column<string>(type: "TEXT", nullable: false),
                    ComicvineId = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedName = table.Column<string>(type: "TEXT", nullable: true),
                    EntityType = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvdbResolution", x => x.CvdbTag);
                });

            migrationBuilder.CreateTable(
                name: "CvIssue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    VolumeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    IssueNumber = table.Column<string>(type: "TEXT", nullable: true),
                    CoverDate = table.Column<string>(type: "TEXT", nullable: true),
                    StoreDate = table.Column<string>(type: "TEXT", nullable: true),
                    Deck = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SiteDetailUrl = table.Column<string>(type: "TEXT", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvIssue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CvVolume",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    StartYear = table.Column<int>(type: "INTEGER", nullable: true),
                    PublisherName = table.Column<string>(type: "TEXT", nullable: true),
                    CountOfIssues = table.Column<int>(type: "INTEGER", nullable: true),
                    Deck = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SiteDetailUrl = table.Column<string>(type: "TEXT", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvVolume", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DerivedTable",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    RebuildJob = table.Column<string>(type: "TEXT", nullable: false),
                    InputFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    LastRebuiltAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RowCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DerivedTable", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateGroup",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Relationship = table.Column<int>(type: "INTEGER", nullable: true),
                    Confidence = table.Column<string>(type: "TEXT", nullable: true),
                    Evidence = table.Column<string>(type: "TEXT", nullable: true),
                    SuggestedKeeperItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReviewState = table.Column<string>(type: "TEXT", nullable: true),
                    DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateGroup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExternalWork",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Authors = table.Column<string>(type: "TEXT", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", nullable: true),
                    FirstPublishYear = table.Column<int>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CoverImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Isbn = table.Column<string>(type: "TEXT", nullable: true),
                    InfoUrl = table.Column<string>(type: "TEXT", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalWork", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupMark",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupType = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupKey = table.Column<string>(type: "TEXT", nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    WantToRead = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMark", x => new { x.UserId, x.GroupType, x.GroupKey });
                });

            migrationBuilder.CreateTable(
                name: "Insight",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    SubjectKind = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    SubjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Confidence = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Recognized = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Synopsis = table.Column<string>(type: "TEXT", nullable: true),
                    Author = table.Column<string>(type: "TEXT", nullable: true),
                    Artist = table.Column<string>(type: "TEXT", nullable: true),
                    YearBegin = table.Column<int>(type: "INTEGER", nullable: true),
                    YearEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    Maturity = table.Column<int>(type: "INTEGER", nullable: true),
                    ReviewFlag = table.Column<string>(type: "TEXT", nullable: true),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Insight", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KidSafeTag",
                columns: table => new
                {
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Tag = table.Column<string>(type: "TEXT", nullable: false),
                    AppliesTo = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KidSafeTag", x => new { x.Category, x.Tag });
                });

            migrationBuilder.CreateTable(
                name: "KnownIdentity",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    MaturityCeiling = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    KidsStyle = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownIdentity", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "LibraryRoot",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    IsCalibre = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryRoot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocgComic",
                columns: table => new
                {
                    LocgComicId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocgSeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    SeriesName = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    IssueNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Format = table.Column<string>(type: "TEXT", nullable: true),
                    CoverDate = table.Column<string>(type: "TEXT", nullable: true),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CommunityRating = table.Column<double>(type: "REAL", nullable: true),
                    RatingCount = table.Column<int>(type: "INTEGER", nullable: true),
                    IsKey = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    KeyType = table.Column<string>(type: "TEXT", nullable: true),
                    Isbn = table.Column<string>(type: "TEXT", nullable: true),
                    Upc = table.Column<string>(type: "TEXT", nullable: true),
                    CoverPrice = table.Column<string>(type: "TEXT", nullable: true),
                    CoverUrl = table.Column<string>(type: "TEXT", nullable: true),
                    StoryCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ScrapedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocgComic", x => x.LocgComicId);
                });

            migrationBuilder.CreateTable(
                name: "MigrationProgress",
                columns: table => new
                {
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Cursor = table.Column<string>(type: "TEXT", nullable: true),
                    Processed = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Total = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationProgress", x => x.Stage);
                });

            migrationBuilder.CreateTable(
                name: "MuSeries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    BayesianRating = table.Column<double>(type: "REAL", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    ScrapedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MuSeries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Publisher",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Publisher", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rating",
                columns: table => new
                {
                    TargetKind = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: true),
                    RawValue = table.Column<double>(type: "REAL", nullable: true),
                    RawScale = table.Column<string>(type: "TEXT", nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    IsOverride = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rating", x => new { x.TargetKind, x.TargetId, x.Source });
                });

            migrationBuilder.CreateTable(
                name: "ScanRun",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    RootId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ItemsSeen = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Added = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Changed = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Removed = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ParsedKey = table.Column<string>(type: "TEXT", nullable: true),
                    CanonicalKey = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayNameOverride = table.Column<string>(type: "TEXT", nullable: true),
                    IssueCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    YearStart = table.Column<int>(type: "INTEGER", nullable: true),
                    YearEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    IsOngoing = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Franchise = table.Column<string>(type: "TEXT", nullable: true),
                    PublisherId = table.Column<int>(type: "INTEGER", nullable: true),
                    CvVolumeId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalWorkId = table.Column<int>(type: "INTEGER", nullable: true),
                    MuSeriesId = table.Column<long>(type: "INTEGER", nullable: true),
                    ResolvedSynopsisSource = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ResolvedRating = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeriesInferenceDecision",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesKey = table.Column<string>(type: "TEXT", nullable: true),
                    Class = table.Column<string>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: true),
                    Target = table.Column<string>(type: "TEXT", nullable: true),
                    Confidence = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    UndoJson = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesInferenceDecision", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeriesKeyLink",
                columns: table => new
                {
                    ParsedKey = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderKey = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Score = table.Column<int>(type: "INTEGER", nullable: true),
                    StoredTopScore = table.Column<int>(type: "INTEGER", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesKeyLink", x => new { x.ParsedKey, x.Provider });
                });

            migrationBuilder.CreateTable(
                name: "SeriesMatchReview",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    Key = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesMatchReview", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeriesMerge",
                columns: table => new
                {
                    OldSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    NewSeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    MergedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesMerge", x => x.OldSeriesId);
                });

            migrationBuilder.CreateTable(
                name: "SystemState",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemState", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "TagAlias",
                columns: table => new
                {
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    AliasTag = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalTag = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagAlias", x => new { x.Category, x.AliasTag });
                });

            migrationBuilder.CreateTable(
                name: "InsightTag",
                columns: table => new
                {
                    InsightId = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsightTag", x => new { x.InsightId, x.Category, x.Value });
                    table.ForeignKey(
                        name: "FK_InsightTag_Insight_InsightId",
                        column: x => x.InsightId,
                        principalTable: "Insight",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Folder",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    RootId = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ParentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: true),
                    Depth = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    TopFolderId = table.Column<int>(type: "INTEGER", nullable: true),
                    DirectChildCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    DescendantItemCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    FolderModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IndexedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HasIcon = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folder", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Folder_Folder_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Folder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Folder_LibraryRoot_RootId",
                        column: x => x.RootId,
                        principalTable: "LibraryRoot",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MuSeriesLink",
                columns: table => new
                {
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    MuSeriesId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Method = table.Column<string>(type: "TEXT", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    MatchedKey = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MuSeriesLink", x => x.SeriesId);
                    table.ForeignKey(
                        name: "FK_MuSeriesLink_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SeriesAlias",
                columns: table => new
                {
                    ParsedKey = table.Column<string>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesAlias", x => x.ParsedKey);
                    table.ForeignKey(
                        name: "FK_SeriesAlias_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SeriesTag",
                columns: table => new
                {
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesTag", x => new { x.SeriesId, x.Category, x.Value, x.Source });
                    table.ForeignKey(
                        name: "FK_SeriesTag_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Item",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    RootId = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    FolderId = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    TopFolderId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Extension = table.Column<string>(type: "TEXT", nullable: true),
                    ContainerFormat = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    FileModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IndexedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    NormalizedTitle = table.Column<string>(type: "TEXT", nullable: true),
                    CalibreBookId = table.Column<int>(type: "INTEGER", nullable: true),
                    PublisherId = table.Column<int>(type: "INTEGER", nullable: true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsExcluded = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    KeepInDirectory = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CoverAspect = table.Column<double>(type: "REAL", nullable: true),
                    ResolvedTitle = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedSeries = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedPublisher = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedYear = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedMonth = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedDatePrecision = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ResolvedRating = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedSynopsisSource = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ResolvedCreatorsCsv = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedTagsCsv = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Item", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Item_Folder_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Item_LibraryRoot_RootId",
                        column: x => x.RootId,
                        principalTable: "LibraryRoot",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Item_Publisher_PublisherId",
                        column: x => x.PublisherId,
                        principalTable: "Publisher",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Item_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BookDetail",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Isbn = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesName = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesIndex = table.Column<double>(type: "REAL", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", nullable: true),
                    PublishedOn = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookDetail", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_BookDetail_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CollectedEditionSpan",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    IssueStart = table.Column<double>(type: "REAL", nullable: true),
                    IssueEnd = table.Column<double>(type: "REAL", nullable: true),
                    EditionTitle = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderRef = table.Column<string>(type: "TEXT", nullable: true),
                    Contiguous = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectedEditionSpan", x => new { x.ItemId, x.Source });
                    table.ForeignKey(
                        name: "FK_CollectedEditionSpan_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CollectionNode",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    Level = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    TrackRole = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    SpanStart = table.Column<int>(type: "INTEGER", nullable: true),
                    SpanEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    ContainsCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ParentItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    SpanSource = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    SpanLabel = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionNode", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_CollectionNode_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CollectionNode_Item_ParentItemId",
                        column: x => x.ParentItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComicDetail",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParsedSeriesKey = table.Column<string>(type: "TEXT", nullable: true),
                    IssueNo = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    VolumeNo = table.Column<int>(type: "INTEGER", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", nullable: true),
                    Format = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    FormatRaw = table.Column<string>(type: "TEXT", nullable: true),
                    IsCollection = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    EventName = table.Column<string>(type: "TEXT", nullable: true),
                    IssueTitle = table.Column<string>(type: "TEXT", nullable: true),
                    Confidence = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    SeriesSource = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    IssueSource = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    YearSource = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    PublisherSource = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    FolderSeries = table.Column<string>(type: "TEXT", nullable: true),
                    FolderYear = table.Column<int>(type: "INTEGER", nullable: true),
                    ParseNotes = table.Column<string>(type: "TEXT", nullable: true),
                    ParsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComicDetail", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_ComicDetail_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComicEmbedded",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Series = table.Column<string>(type: "TEXT", nullable: true),
                    Number = table.Column<string>(type: "TEXT", nullable: true),
                    AltSeries = table.Column<string>(type: "TEXT", nullable: true),
                    AltNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Volume = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", nullable: true),
                    Imprint = table.Column<string>(type: "TEXT", nullable: true),
                    Genre = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    Characters = table.Column<string>(type: "TEXT", nullable: true),
                    Teams = table.Column<string>(type: "TEXT", nullable: true),
                    Locations = table.Column<string>(type: "TEXT", nullable: true),
                    StoryArc = table.Column<string>(type: "TEXT", nullable: true),
                    Web = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Format = table.Column<string>(type: "TEXT", nullable: true),
                    PublicationDate = table.Column<string>(type: "TEXT", nullable: true),
                    Writers = table.Column<string>(type: "TEXT", nullable: true),
                    Pencillers = table.Column<string>(type: "TEXT", nullable: true),
                    Inker = table.Column<string>(type: "TEXT", nullable: true),
                    Colorist = table.Column<string>(type: "TEXT", nullable: true),
                    Letterer = table.Column<string>(type: "TEXT", nullable: true),
                    CoverArtist = table.Column<string>(type: "TEXT", nullable: true),
                    Editor = table.Column<string>(type: "TEXT", nullable: true),
                    BlackAndWhite = table.Column<bool>(type: "INTEGER", nullable: true),
                    Manga = table.Column<string>(type: "TEXT", nullable: true),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Identifier = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: true),
                    AgeRating = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComicEmbedded", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_ComicEmbedded_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateMember",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DuplicateGroupId = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Role = table.Column<string>(type: "TEXT", nullable: true),
                    SoleFileInFolder = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateMember", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DuplicateMember_DuplicateGroup_DuplicateGroupId",
                        column: x => x.DuplicateGroupId,
                        principalTable: "DuplicateGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DuplicateMember_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemCredit",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderPersonId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemCredit", x => new { x.ItemId, x.Source, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_ItemCredit_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemProviderLink",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: true),
                    SecondaryKey = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Method = table.Column<string>(type: "TEXT", nullable: true),
                    MatchedKey = table.Column<string>(type: "TEXT", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    StoredTopScore = table.Column<int>(type: "INTEGER", nullable: true),
                    Applied = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemProviderLink", x => new { x.ItemId, x.Provider });
                    table.ForeignKey(
                        name: "FK_ItemProviderLink_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemSignature",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    CoverPHash = table.Column<long>(type: "INTEGER", nullable: true),
                    PageSignature = table.Column<string>(type: "TEXT", nullable: true),
                    SignaturesComputedFor = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemSignature", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_ItemSignature_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ItemState",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsBroken = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    BrokenReason = table.Column<string>(type: "TEXT", nullable: true),
                    BrokenCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ThumbnailError = table.Column<string>(type: "TEXT", nullable: true),
                    ThumbnailCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CoverWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    CoverHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    CoverDimsComputedFor = table.Column<string>(type: "TEXT", nullable: true),
                    ExclusionReason = table.Column<string>(type: "TEXT", nullable: true),
                    ExcludedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemState", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_ItemState_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ItemTag",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemTag", x => new { x.ItemId, x.Category, x.Value, x.Source });
                    table.ForeignKey(
                        name: "FK_ItemTag_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReadingOrderEntry",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReadTier = table.Column<int>(type: "INTEGER", nullable: true),
                    ReadNumber = table.Column<double>(type: "REAL", nullable: true),
                    ReadNumberSuffix = table.Column<double>(type: "REAL", nullable: true),
                    ReadDate = table.Column<string>(type: "TEXT", nullable: true),
                    ReadDatePrecision = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ReadIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    ReadCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Source = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Confidence = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadingOrderEntry", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_ReadingOrderEntry_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserItemState",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastPage = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    LastSpineItemIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    LastScrollPercent = table.Column<double>(type: "REAL", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    WantToRead = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Favorite = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    HiddenFromHistory = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserItemState", x => new { x.UserId, x.ItemId });
                    table.ForeignKey(
                        name: "FK_UserItemState_Item_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookDetail_Isbn",
                table: "BookDetail",
                column: "Isbn");

            migrationBuilder.CreateIndex(
                name: "IX_BookDetail_SeriesName_SeriesIndex",
                table: "BookDetail",
                columns: new[] { "SeriesName", "SeriesIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectedEditionSpan_SeriesId",
                table: "CollectedEditionSpan",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionNode_ContainsCount",
                table: "CollectionNode",
                column: "ContainsCount",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionNode_ParentItemId",
                table: "CollectionNode",
                column: "ParentItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionNode_SeriesId",
                table: "CollectionNode",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_ComicDetail_EventName",
                table: "ComicDetail",
                column: "EventName");

            migrationBuilder.CreateIndex(
                name: "IX_ComicDetail_ParsedSeriesKey",
                table: "ComicDetail",
                column: "ParsedSeriesKey");

            migrationBuilder.CreateIndex(
                name: "IX_ComicDetail_Year",
                table: "ComicDetail",
                column: "Year");

            migrationBuilder.CreateIndex(
                name: "IX_CvIssue_VolumeId",
                table: "CvIssue",
                column: "VolumeId");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateMember_DuplicateGroupId",
                table: "DuplicateMember",
                column: "DuplicateGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateMember_ItemId",
                table: "DuplicateMember",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWork_Provider_ProviderKey",
                table: "ExternalWork",
                columns: new[] { "Provider", "ProviderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Folder_NormalizedName",
                table: "Folder",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_Folder_ParentId",
                table: "Folder",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Folder_Path",
                table: "Folder",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Folder_RootId",
                table: "Folder",
                column: "RootId");

            migrationBuilder.CreateIndex(
                name: "IX_Folder_TopFolderId",
                table: "Folder",
                column: "TopFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMark_UserId_GroupType_WantToRead",
                table: "GroupMark",
                columns: new[] { "UserId", "GroupType", "WantToRead" });

            migrationBuilder.CreateIndex(
                name: "IX_Insight_SubjectKind_Maturity",
                table: "Insight",
                columns: new[] { "SubjectKind", "Maturity" },
                filter: "\"IsCurrent\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Insight_SubjectKind_SubjectId_IsCurrent",
                table: "Insight",
                columns: new[] { "SubjectKind", "SubjectId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_Item_CalibreBookId",
                table: "Item",
                column: "CalibreBookId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Item_FolderId",
                table: "Item",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Item_Kind_IndexedAt_Id",
                table: "Item",
                columns: new[] { "Kind", "IndexedAt", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Item_Kind_NormalizedTitle_Id",
                table: "Item",
                columns: new[] { "Kind", "NormalizedTitle", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Item_Kind_ResolvedPublisher_Id",
                table: "Item",
                columns: new[] { "Kind", "ResolvedPublisher", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Item_Kind_ResolvedRating_Id",
                table: "Item",
                columns: new[] { "Kind", "ResolvedRating", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Item_Kind_ResolvedSeries_Id",
                table: "Item",
                columns: new[] { "Kind", "ResolvedSeries", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Item_Kind_ResolvedYear_IndexedAt_Id",
                table: "Item",
                columns: new[] { "Kind", "ResolvedYear", "IndexedAt", "Id" },
                descending: new[] { false, true, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Item_Path",
                table: "Item",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Item_PublisherId",
                table: "Item",
                column: "PublisherId");

            migrationBuilder.CreateIndex(
                name: "IX_Item_RootId",
                table: "Item",
                column: "RootId");

            migrationBuilder.CreateIndex(
                name: "IX_Item_SeriesId_Id",
                table: "Item",
                columns: new[] { "SeriesId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Item_TopFolderId",
                table: "Item",
                column: "TopFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemCredit_Role_NormalizedName_ItemId",
                table: "ItemCredit",
                columns: new[] { "Role", "NormalizedName", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemProviderLink_Provider_ProviderKey",
                table: "ItemProviderLink",
                columns: new[] { "Provider", "ProviderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemProviderLink_Provider_Status",
                table: "ItemProviderLink",
                columns: new[] { "Provider", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemSignature_ContentFingerprint",
                table: "ItemSignature",
                column: "ContentFingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_ItemSignature_CoverPHash",
                table: "ItemSignature",
                column: "CoverPHash");

            migrationBuilder.CreateIndex(
                name: "IX_ItemTag_Category_Value_ItemId",
                table: "ItemTag",
                columns: new[] { "Category", "Value", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryRoot_Path",
                table: "LibraryRoot",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Publisher_Name",
                table: "Publisher",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadingOrderEntry_SeriesId_ReadIndex",
                table: "ReadingOrderEntry",
                columns: new[] { "SeriesId", "ReadIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_Series_CanonicalKey",
                table: "Series",
                column: "CanonicalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Series_Franchise",
                table: "Series",
                column: "Franchise");

            migrationBuilder.CreateIndex(
                name: "IX_Series_Name_Id",
                table: "Series",
                columns: new[] { "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Series_ParsedKey",
                table: "Series",
                column: "ParsedKey");

            migrationBuilder.CreateIndex(
                name: "IX_Series_ResolvedRating_Id",
                table: "Series",
                columns: new[] { "ResolvedRating", "Id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesAlias_SeriesId",
                table: "SeriesAlias",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesInferenceDecision_State_Class",
                table: "SeriesInferenceDecision",
                columns: new[] { "State", "Class" });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesKeyLink_Provider_ProviderKey",
                table: "SeriesKeyLink",
                columns: new[] { "Provider", "ProviderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesKeyLink_Status",
                table: "SeriesKeyLink",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesMatchReview_Scope_Key",
                table: "SeriesMatchReview",
                columns: new[] { "Scope", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeriesTag_Category_Value_SeriesId",
                table: "SeriesTag",
                columns: new[] { "Category", "Value", "SeriesId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserItemState_ItemId",
                table: "UserItemState",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_UserItemState_UserId_UpdatedAt",
                table: "UserItemState",
                columns: new[] { "UserId", "UpdatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_UserItemState_UserId_WantToRead",
                table: "UserItemState",
                columns: new[] { "UserId", "WantToRead" });

            // FTS5 virtual table: not an EF entity (see ItemFts.cs).
            migrationBuilder.Sql(ItemFts.CreateSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ItemFts.DropSql);
            migrationBuilder.DropTable(
                name: "BarneyProg");

            migrationBuilder.DropTable(
                name: "BookDetail");

            migrationBuilder.DropTable(
                name: "CollectedEditionSpan");

            migrationBuilder.DropTable(
                name: "CollectionNode");

            migrationBuilder.DropTable(
                name: "ComicDetail");

            migrationBuilder.DropTable(
                name: "ComicEmbedded");

            migrationBuilder.DropTable(
                name: "CvdbResolution");

            migrationBuilder.DropTable(
                name: "CvIssue");

            migrationBuilder.DropTable(
                name: "CvVolume");

            migrationBuilder.DropTable(
                name: "DerivedTable");

            migrationBuilder.DropTable(
                name: "DuplicateMember");

            migrationBuilder.DropTable(
                name: "ExternalWork");

            migrationBuilder.DropTable(
                name: "GroupMark");

            migrationBuilder.DropTable(
                name: "InsightTag");

            migrationBuilder.DropTable(
                name: "ItemCredit");

            migrationBuilder.DropTable(
                name: "ItemProviderLink");

            migrationBuilder.DropTable(
                name: "ItemSignature");

            migrationBuilder.DropTable(
                name: "ItemState");

            migrationBuilder.DropTable(
                name: "ItemTag");

            migrationBuilder.DropTable(
                name: "KidSafeTag");

            migrationBuilder.DropTable(
                name: "KnownIdentity");

            migrationBuilder.DropTable(
                name: "LocgComic");

            migrationBuilder.DropTable(
                name: "MigrationProgress");

            migrationBuilder.DropTable(
                name: "MuSeries");

            migrationBuilder.DropTable(
                name: "MuSeriesLink");

            migrationBuilder.DropTable(
                name: "Rating");

            migrationBuilder.DropTable(
                name: "ReadingOrderEntry");

            migrationBuilder.DropTable(
                name: "ScanRun");

            migrationBuilder.DropTable(
                name: "SeriesAlias");

            migrationBuilder.DropTable(
                name: "SeriesInferenceDecision");

            migrationBuilder.DropTable(
                name: "SeriesKeyLink");

            migrationBuilder.DropTable(
                name: "SeriesMatchReview");

            migrationBuilder.DropTable(
                name: "SeriesMerge");

            migrationBuilder.DropTable(
                name: "SeriesTag");

            migrationBuilder.DropTable(
                name: "SystemState");

            migrationBuilder.DropTable(
                name: "TagAlias");

            migrationBuilder.DropTable(
                name: "UserItemState");

            migrationBuilder.DropTable(
                name: "DuplicateGroup");

            migrationBuilder.DropTable(
                name: "Insight");

            migrationBuilder.DropTable(
                name: "Item");

            migrationBuilder.DropTable(
                name: "Folder");

            migrationBuilder.DropTable(
                name: "Publisher");

            migrationBuilder.DropTable(
                name: "Series");

            migrationBuilder.DropTable(
                name: "LibraryRoot");
        }
    }
}
