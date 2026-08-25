CREATE TABLE BarcodeScans (
      ComicId INTEGER PRIMARY KEY, CodesJson TEXT, PagesScanned INTEGER, Error TEXT,
      ScannedAt TEXT);

CREATE TABLE BarneyMatches (
  ComicId INTEGER PRIMARY KEY, ProgNo INTEGER, MatchMethod TEXT, CreatedAt TEXT);

CREATE TABLE BarneyProgs (
  ProgNo INTEGER PRIMARY KEY, CoverDate TEXT, Price TEXT, StripsJson TEXT, ScrapedAt TEXT);

CREATE TABLE "Bookmarks" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Bookmarks" PRIMARY KEY AUTOINCREMENT,
    "Username" TEXT NOT NULL,
    "ComicId" INTEGER NOT NULL,
    "LastPage" INTEGER NULL,
    "LastSpineItemIndex" INTEGER NULL,
    "LastScrollPercent" REAL NULL,
    "Status" INTEGER NOT NULL,
    "UpdatedAt" TEXT NOT NULL, HiddenFromHistory INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT "FK_Bookmarks_Comics_ComicId" FOREIGN KEY ("ComicId") REFERENCES "Comics" ("Id") ON DELETE CASCADE
);

CREATE TABLE ClaudeBookMetadata (
            ComicId       INTEGER PRIMARY KEY,
            Rating        INTEGER,
            Synopsis      TEXT,
            Confidence    TEXT    NOT NULL DEFAULT 'Unknown',
            KnownBook     INTEGER NOT NULL DEFAULT 0,
            Maturity      INTEGER,
            Author        TEXT,
            YearPublished INTEGER,
            GeneratedAt   TEXT    NOT NULL,
            ModelId       TEXT    NOT NULL DEFAULT '',
            TagsCsv       TEXT,
            FOREIGN KEY (ComicId) REFERENCES Comics(Id) ON DELETE CASCADE
        );

CREATE TABLE ClaudeBookTags (
            ComicId  INTEGER NOT NULL,
            Category TEXT    NOT NULL,
            Tag      TEXT    NOT NULL,
            PRIMARY KEY (ComicId, Category, Tag),
            FOREIGN KEY (ComicId) REFERENCES ClaudeBookMetadata(ComicId) ON DELETE CASCADE
        );

CREATE TABLE "ClaudeSeriesMetadata" (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    SeriesName  TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    Rating      INTEGER,
    Synopsis    TEXT,
    Confidence  TEXT    NOT NULL DEFAULT 'Unknown',
    KnownSeries INTEGER NOT NULL DEFAULT 0,
    GeneratedAt TEXT    NOT NULL,
    ModelId     TEXT    NOT NULL DEFAULT '',
    Author      TEXT,
    Artist      TEXT,
    YearBegin   INTEGER,
    YearEnd     INTEGER
, ReviewFlag TEXT, TagsCsv TEXT);

CREATE TABLE "ClaudeSeriesTags" (
    MetadataId INTEGER NOT NULL,
    Category   TEXT    NOT NULL,
    Tag        TEXT    NOT NULL,
    PRIMARY KEY (MetadataId, Category, Tag),
    FOREIGN KEY (MetadataId) REFERENCES "ClaudeSeriesMetadata"(Id) ON DELETE CASCADE
);

CREATE TABLE ComicCollectionNodes (
    ComicId INTEGER NOT NULL PRIMARY KEY, SeriesId INTEGER, CollectionLevel INTEGER NOT NULL DEFAULT 0,
    TrackRole TEXT NOT NULL DEFAULT 'primary', SpanStart INTEGER NOT NULL DEFAULT 0, SpanEnd INTEGER NOT NULL DEFAULT 0,
    ContainsCount INTEGER NOT NULL DEFAULT 0, ParentComicId INTEGER, SpanSource TEXT NOT NULL DEFAULT 'inferred', SpanLabel TEXT);

CREATE VIRTUAL TABLE ComicFts USING fts5(
    body,
    content='',
    tokenize='unicode61 remove_diacritics 1'
);

CREATE TABLE 'ComicFts_config'(k PRIMARY KEY, v) WITHOUT ROWID;

CREATE TABLE 'ComicFts_data'(id INTEGER PRIMARY KEY, block BLOB);

CREATE TABLE 'ComicFts_docsize'(id INTEGER PRIMARY KEY, sz BLOB);

CREATE TABLE 'ComicFts_idx'(segid, term, pgno, PRIMARY KEY(segid, term)) WITHOUT ROWID;

CREATE TABLE ComicParsedDetails (
    ComicId         INTEGER NOT NULL PRIMARY KEY,
    Series          TEXT,
    IssueNo         TEXT,
    Year            INTEGER,
    VolumeNo        INTEGER,
    Publisher       TEXT,
    Format          TEXT    NOT NULL DEFAULT 'Unknown',
    IsCollection    INTEGER NOT NULL DEFAULT 0,
    Confidence      TEXT    NOT NULL DEFAULT 'Low',
    SeriesSource    TEXT    NOT NULL DEFAULT 'None',
    IssueSource     TEXT    NOT NULL DEFAULT 'None',
    YearSource      TEXT    NOT NULL DEFAULT 'None',
    PublisherSource TEXT    NOT NULL DEFAULT 'None',
    FolderSeries    TEXT,
    FolderYear      INTEGER,
    ParseNotes      TEXT,
    ParsedAt        TEXT    NOT NULL, EventName TEXT, IssueTitle TEXT, ClaudeSeriesMetadataId INTEGER, ComicvineVolumeId INTEGER, ExternalWorkId INTEGER, SeriesId INTEGER,
    FOREIGN KEY (ComicId) REFERENCES Comics(Id) ON DELETE CASCADE
);

CREATE TABLE ComicReadingOrder (
    ComicId           INTEGER NOT NULL PRIMARY KEY,
    GroupKey          TEXT    NOT NULL DEFAULT '',
    ReadTier          INTEGER NOT NULL DEFAULT 40,
    ReadNumber        REAL,
    ReadNumberSuffix  REAL    NOT NULL DEFAULT 0,
    ReadDate          TEXT,
    ReadDatePrecision TEXT    NOT NULL DEFAULT 'None',
    ReadIndex         INTEGER,
    ReadCount         INTEGER,
    Source            TEXT    NOT NULL DEFAULT 'Unordered',
    Confidence        TEXT    NOT NULL DEFAULT 'Low',
    Notes             TEXT,
    ComputedAt        TEXT    NOT NULL,
    FOREIGN KEY (ComicId) REFERENCES Comics(Id) ON DELETE CASCADE
);

CREATE TABLE "ComicTagAssociation" (
    "ComicsId" INTEGER NOT NULL,
    "TagAssociationsId" INTEGER NOT NULL,
    CONSTRAINT "PK_ComicTagAssociation" PRIMARY KEY ("ComicsId", "TagAssociationsId"),
    CONSTRAINT "FK_ComicTagAssociation_Comics_ComicsId" FOREIGN KEY ("ComicsId") REFERENCES "Comics" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_ComicTagAssociation_Tags_TagAssociationsId" FOREIGN KEY ("TagAssociationsId") REFERENCES "Tags" ("Id") ON DELETE CASCADE
);

CREATE TABLE ComicUserLists (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Username TEXT NOT NULL,
    ComicId INTEGER NOT NULL,
    ListType INTEGER NOT NULL,
    AddedAt TEXT NOT NULL,
    FOREIGN KEY (ComicId) REFERENCES Comics(Id) ON DELETE CASCADE,
    UNIQUE(Username, ComicId, ListType)
);

CREATE TABLE "Comics" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Comics" PRIMARY KEY AUTOINCREMENT,
    "ParentFolderId" INTEGER NULL,
    "Category" INTEGER NOT NULL,
    "FilePath" TEXT NOT NULL,
    "FileName" TEXT NOT NULL,
    "FileExtension" TEXT NOT NULL,
    "FileSize" INTEGER NOT NULL,
    "FileModifiedAt" TEXT NOT NULL,
    "IndexedAt" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "NormalizedTitle" TEXT NOT NULL,
    "Description" TEXT NULL,
    "Language" TEXT NULL,
    "SeriesName" TEXT NULL,
    "SeriesIndex" TEXT NULL,
    "AltSeriesName" TEXT NULL,
    "AltSeriesIndex" TEXT NULL,
    "Volume" INTEGER NULL,
    "Publisher" TEXT NULL,
    "PublicationDate" TEXT NULL,
    "Writers" TEXT NULL,
    "Pencillers" TEXT NULL,
    "Identifier" TEXT NULL,
    "Tags" TEXT NULL,
    "PageCount" INTEGER NOT NULL,
    "EmbeddedRating" INTEGER NULL,
    "UserRating" INTEGER NULL, MetadataVersion INTEGER NOT NULL DEFAULT 0, IssueTitle TEXT, AlternateCount INTEGER, Count INTEGER, SeriesGroup TEXT, Imprint TEXT, Format TEXT, AgeRating TEXT, Web TEXT, GTIN TEXT, Inker TEXT, Colorist TEXT, Letterer TEXT, CoverArtist TEXT, Editor TEXT, Translator TEXT, Genre TEXT, Characters TEXT, Teams TEXT, Locations TEXT, StoryArc TEXT, StoryArcNumber TEXT, MainCharacterOrTeam TEXT, BlackAndWhite INTEGER, Manga TEXT, Notes TEXT, PublisherId INTEGER NULL, FolderGroupId INTEGER NULL, IsBroken INTEGER NOT NULL DEFAULT 0, BrokenReason TEXT, BrokenCheckedAt TEXT, ThumbnailError TEXT, ThumbnailCheckedAt TEXT, CoverWidth INTEGER, CoverHeight INTEGER, ExcludedFromLibrary INTEGER NOT NULL DEFAULT 0, ExclusionReason TEXT, ExcludedAt TEXT, ContentFingerprint TEXT, CoverPHash INTEGER, PageSignature TEXT, SignaturesComputedFor TEXT, KeepInDirectory INTEGER NOT NULL DEFAULT 0, CoverDimsComputedFor TEXT,
    CONSTRAINT "FK_Comics_Folders_ParentFolderId" FOREIGN KEY ("ParentFolderId") REFERENCES "Folders" ("Id") ON DELETE SET NULL
);

CREATE TABLE ComicvineApiCaches (
    RequestKey   TEXT NOT NULL PRIMARY KEY,
    ResponseJson TEXT NOT NULL,
    FetchedAt    TEXT NOT NULL DEFAULT ''
);

CREATE TABLE ComicvineCharacters (
    ComicvineId              INTEGER NOT NULL PRIMARY KEY,
    Name                     TEXT    NOT NULL,
    RealName                 TEXT,
    Aliases                  TEXT,
    Deck                     TEXT,
    Description              TEXT,
    ImageUrl                 TEXT,
    Gender                   TEXT,
    Origin                   TEXT,
    PublisherId              INTEGER,
    PublisherName            TEXT,
    FirstAppearedInIssueId   INTEGER,
    FetchedAt                TEXT
);

CREATE TABLE ComicvineCollectedEditions (
    ComicId INTEGER NOT NULL PRIMARY KEY, SeriesId INTEGER, IssueStart REAL NOT NULL DEFAULT 0,
    IssueEnd REAL NOT NULL DEFAULT 0, EditionTitle TEXT, ComicvineVolumeId INTEGER,
    Confidence REAL NOT NULL DEFAULT 0, ScrapedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE ComicvineIssueCharacters (
    IssueId          INTEGER NOT NULL,
    CharacterId      INTEGER NOT NULL,
    CharacterName    TEXT    NOT NULL,
    IsFirstAppearance INTEGER NOT NULL DEFAULT 0,
    IsDiedIn          INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (IssueId, CharacterId),
    FOREIGN KEY (IssueId)     REFERENCES ComicvineIssues(ComicvineId)     ON DELETE CASCADE,
    FOREIGN KEY (CharacterId) REFERENCES ComicvineCharacters(ComicvineId) ON DELETE CASCADE
);

CREATE TABLE ComicvineIssuePeople (
    IssueId    INTEGER NOT NULL,
    PersonId   INTEGER NOT NULL,
    PersonName TEXT    NOT NULL,
    Role       TEXT,
    PRIMARY KEY (IssueId, PersonId),
    FOREIGN KEY (IssueId)  REFERENCES ComicvineIssues(ComicvineId) ON DELETE CASCADE,
    FOREIGN KEY (PersonId) REFERENCES ComicvinePeople(ComicvineId) ON DELETE CASCADE
);

CREATE TABLE ComicvineIssueStoryArcs (
    IssueId      INTEGER NOT NULL,
    StoryArcId   INTEGER NOT NULL,
    StoryArcName TEXT    NOT NULL,
    PRIMARY KEY (IssueId, StoryArcId),
    FOREIGN KEY (IssueId)    REFERENCES ComicvineIssues(ComicvineId)    ON DELETE CASCADE,
    FOREIGN KEY (StoryArcId) REFERENCES ComicvineStoryArcs(ComicvineId) ON DELETE CASCADE
);

CREATE TABLE ComicvineIssueTeams (
    IssueId          INTEGER NOT NULL,
    TeamId           INTEGER NOT NULL,
    TeamName         TEXT    NOT NULL,
    IsFirstAppearance INTEGER NOT NULL DEFAULT 0,
    IsDisbanded       INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (IssueId, TeamId),
    FOREIGN KEY (IssueId) REFERENCES ComicvineIssues(ComicvineId) ON DELETE CASCADE,
    FOREIGN KEY (TeamId)  REFERENCES ComicvineTeams(ComicvineId)  ON DELETE CASCADE
);

CREATE TABLE ComicvineIssues (
    ComicvineId   INTEGER NOT NULL PRIMARY KEY,
    VolumeId      INTEGER NOT NULL,
    Name          TEXT,
    IssueNumber   TEXT,
    CoverDate     TEXT,
    StoreDate     TEXT,
    Deck          TEXT,
    Description   TEXT,
    ImageUrl      TEXT,
    SiteDetailUrl TEXT,
    FetchedAt     TEXT    NOT NULL,
    FOREIGN KEY (VolumeId) REFERENCES ComicvineVolumes(ComicvineId)
);

CREATE TABLE ComicvineMatches (
    ComicId           INTEGER NOT NULL PRIMARY KEY,
    Status            INTEGER NOT NULL DEFAULT 0,
    ComicvineIssueId  INTEGER,
    ComicvineVolumeId INTEGER,
    SearchQuery       TEXT,
    LastAttemptedAt   TEXT,
    AttemptCount      INTEGER NOT NULL DEFAULT 0,
    ErrorMessage      TEXT,
    CandidatesJson    TEXT,
    Applied           INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (ComicId) REFERENCES Comics(Id) ON DELETE CASCADE
);

CREATE TABLE ComicvinePeople (
    ComicvineId  INTEGER NOT NULL PRIMARY KEY,
    Name         TEXT    NOT NULL,
    Aliases      TEXT,
    Deck         TEXT,
    Description  TEXT,
    ImageUrl     TEXT,
    Country      TEXT,
    Hometown     TEXT,
    Birth        TEXT,
    Death        TEXT,
    FetchedAt    TEXT
);

CREATE TABLE ComicvineSeries (
    ComicvineId      INTEGER NOT NULL PRIMARY KEY,
    Name             TEXT    NOT NULL,
    Aliases          TEXT,
    Deck             TEXT,
    Description      TEXT,
    ImageUrl         TEXT,
    PublisherId      INTEGER,
    PublisherName    TEXT,
    StartYear        INTEGER,
    CountOfEpisodes  INTEGER,
    SiteDetailUrl    TEXT,
    FetchedAt        TEXT
);

CREATE TABLE ComicvineSeriesLinks (
    SeriesName        TEXT    NOT NULL PRIMARY KEY,
    ComicvineVolumeId INTEGER,
    SearchQuery       TEXT,
    MatchScore        INTEGER,
    Status            INTEGER NOT NULL DEFAULT 0,
    AttemptedAt       TEXT,
    AttemptCount      INTEGER NOT NULL DEFAULT 0,
    ErrorMessage      TEXT, CandidatesJson TEXT,
    FOREIGN KEY (ComicvineVolumeId) REFERENCES ComicvineVolumes(ComicvineId) ON DELETE SET NULL
);

CREATE TABLE ComicvineStoryArcs (
    ComicvineId   INTEGER NOT NULL PRIMARY KEY,
    Name          TEXT    NOT NULL,
    Aliases       TEXT,
    Deck          TEXT,
    Description   TEXT,
    ImageUrl      TEXT,
    PublisherId   INTEGER,
    PublisherName TEXT,
    CountOfIssues INTEGER,
    FetchedAt     TEXT
);

CREATE TABLE ComicvineTeams (
    ComicvineId        INTEGER NOT NULL PRIMARY KEY,
    Name               TEXT    NOT NULL,
    Aliases            TEXT,
    Deck               TEXT,
    Description        TEXT,
    ImageUrl           TEXT,
    PublisherId        INTEGER,
    PublisherName      TEXT,
    CountOfTeamMembers INTEGER,
    FetchedAt          TEXT
);

CREATE TABLE ComicvineVolumeSeries (
    VolumeId   INTEGER NOT NULL,
    SeriesId   INTEGER NOT NULL,
    SeriesName TEXT    NOT NULL,
    PRIMARY KEY (VolumeId, SeriesId),
    FOREIGN KEY (VolumeId)  REFERENCES ComicvineVolumes(ComicvineId) ON DELETE CASCADE,
    FOREIGN KEY (SeriesId)  REFERENCES ComicvineSeries(ComicvineId)  ON DELETE CASCADE
);

CREATE TABLE ComicvineVolumes (
    ComicvineId   INTEGER NOT NULL PRIMARY KEY,
    Name          TEXT    NOT NULL,
    StartYear     INTEGER,
    PublisherId   INTEGER,
    PublisherName TEXT,
    CountOfIssues INTEGER,
    Deck          TEXT,
    Description   TEXT,
    ImageUrl      TEXT,
    SiteDetailUrl TEXT,
    FetchedAt     TEXT    NOT NULL
, ConceptsJson TEXT, CharactersJson TEXT, LocationsJson TEXT, ObjectsJson TEXT, TeamsJson TEXT);

CREATE TABLE CuratedCollectedEditions (
  ComicId INTEGER NOT NULL PRIMARY KEY, SeriesId INTEGER,
  IssueStart REAL NOT NULL DEFAULT 0, IssueEnd REAL NOT NULL DEFAULT 0,
  EditionTitle TEXT, Source TEXT NOT NULL DEFAULT 'claude', Confidence REAL NOT NULL DEFAULT 0,
  Note TEXT, CreatedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE CvdbResolutions (
    CvdbTag      TEXT    NOT NULL PRIMARY KEY,
    ComicvineId  INTEGER NOT NULL,
    ResolvedName TEXT,
    EntityType   TEXT,
    Status       TEXT    NOT NULL DEFAULT 'Pending',
    ResolvedAt   TEXT
);

CREATE TABLE DuplicateGroups (
    Id                     INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    Relationship           INTEGER NOT NULL,
    Confidence             TEXT    NOT NULL DEFAULT 'High',
    Evidence               TEXT,
    SuggestedKeeperComicId INTEGER,
    ReviewState            TEXT    NOT NULL DEFAULT 'Pending',
    DetectedAt             TEXT    NOT NULL
);

CREATE TABLE DuplicateMembers (
    Id               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    DuplicateGroupId INTEGER NOT NULL,
    ComicId          INTEGER NOT NULL,
    Role             TEXT    NOT NULL DEFAULT 'Member',
    SoleFileInFolder INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (DuplicateGroupId) REFERENCES DuplicateGroups(Id) ON DELETE CASCADE,
    FOREIGN KEY (ComicId)          REFERENCES Comics(Id)          ON DELETE CASCADE
);

CREATE TABLE ExternalSeriesLinks (
    SeriesName      TEXT    NOT NULL PRIMARY KEY,
    ExternalWorkId  INTEGER,
    MatchedProvider TEXT,
    SearchQuery     TEXT,
    MatchScore      INTEGER,
    Status          INTEGER NOT NULL DEFAULT 0,
    AttemptedAt     TEXT,
    AttemptCount    INTEGER NOT NULL DEFAULT 0,
    ErrorMessage    TEXT,
    CandidatesJson  TEXT,
    FOREIGN KEY (ExternalWorkId) REFERENCES ExternalWorks(Id) ON DELETE SET NULL
);

CREATE TABLE ExternalWorks (
    Id               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    Provider         TEXT    NOT NULL,
    ProviderKey      TEXT    NOT NULL,
    Title            TEXT    NOT NULL,
    Authors          TEXT,
    Publisher        TEXT,
    FirstPublishYear INTEGER,
    Description      TEXT,
    CoverImageUrl    TEXT,
    SubjectsJson     TEXT,
    TagsCsv          TEXT,
    Isbn             TEXT,
    InfoUrl          TEXT,
    FetchedAt        TEXT    NOT NULL
);

CREATE TABLE FolderAggregates (
    FolderId INTEGER NOT NULL PRIMARY KEY,
    DirectChildCount INTEGER NOT NULL,
    DescendantComicCount INTEGER NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE TABLE "Folders" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Folders" PRIMARY KEY AUTOINCREMENT,
    "ParentId" INTEGER NULL,
    "Category" INTEGER NOT NULL,
    "FolderPath" TEXT NOT NULL,
    "FolderName" TEXT NOT NULL,
    "NormalizedName" TEXT NOT NULL,
    "IndexedAt" TEXT NOT NULL,
    "FolderModifiedAt" TEXT NOT NULL,
    "AuthorizedUsersJson" TEXT NOT NULL,
    CONSTRAINT "FK_Folders_Folders_ParentId" FOREIGN KEY ("ParentId") REFERENCES "Folders" ("Id") ON DELETE RESTRICT
);

CREATE TABLE GcdCollectedEditions (
    ComicId      INTEGER NOT NULL PRIMARY KEY,
    SeriesId     INTEGER,
    IssueStart   REAL    NOT NULL DEFAULT 0,
    IssueEnd     REAL    NOT NULL DEFAULT 0,
    EditionTitle TEXT,
    SourceSeries TEXT,
    GcdIssueId   INTEGER,
    MatchBy      TEXT,
    Contiguous   INTEGER NOT NULL DEFAULT 1,
    Confidence   REAL    NOT NULL DEFAULT 0,
    Note         TEXT,
    CreatedAt    TEXT    NOT NULL DEFAULT ''
);

CREATE TABLE GcdIssues (GcdIssueId INTEGER NOT NULL PRIMARY KEY, GcdSeriesId INTEGER, SeriesName TEXT,
  SeriesYearBegan INTEGER, Number TEXT, Title TEXT, KeyDate TEXT, PublicationDate TEXT, ValidIsbn TEXT, Isbn TEXT,
  Barcode TEXT, PageCount INTEGER, Price TEXT, Publisher TEXT, Format TEXT, VariantOfId INTEGER, VariantName TEXT, ImportedAt TEXT NOT NULL DEFAULT '', StoryGenres TEXT, TagsCsv TEXT);

CREATE TABLE GcdMatches (ComicId INTEGER NOT NULL PRIMARY KEY, GcdIssueId INTEGER, GcdSeriesId INTEGER,
  Status TEXT NOT NULL DEFAULT 'Pending', MatchMethod TEXT, MatchedKey TEXT, Confidence REAL NOT NULL DEFAULT 0,
  CandidateCount INTEGER NOT NULL DEFAULT 0, ErrorMessage TEXT, Applied INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE GcdSeries (
  GcdSeriesId INTEGER NOT NULL PRIMARY KEY, Name TEXT, SortName TEXT, YearBegan INTEGER,
  YearEnded INTEGER, Publisher TEXT, Format TEXT, IssueCount INTEGER,
  HasIsbn INTEGER NOT NULL DEFAULT 0, HasBarcode INTEGER NOT NULL DEFAULT 0,
  Binding TEXT, Notes TEXT, ImportedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE GroupUserMetadata (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Username    TEXT    NOT NULL,
    GroupType   TEXT    NOT NULL,
    GroupKey    TEXT    NOT NULL,
    IsFavorite  INTEGER NOT NULL DEFAULT 0,
    IsRead      INTEGER NOT NULL DEFAULT 0,
    WantToRead  INTEGER NOT NULL DEFAULT 0,
    Rating      INTEGER NULL,
    Notes       TEXT    NULL,
    UpdatedAt   TEXT    NOT NULL,
    UNIQUE(Username, GroupType, GroupKey)
);

CREATE TABLE InducksMatches (
  ComicId INTEGER PRIMARY KEY, IssueCode TEXT, PublicationCode TEXT, Status TEXT,
  MatchMethod TEXT, Confidence REAL, CreatedAt TEXT);

CREATE TABLE KidSafeTags (
    Category  TEXT NOT NULL,
    Tag       TEXT NOT NULL,
    AppliesTo TEXT NOT NULL DEFAULT 'both',
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (Category, Tag)
);

CREATE TABLE LibraryComicRatings (
    ComicId     INTEGER NOT NULL PRIMARY KEY,
    Rating      INTEGER NOT NULL,   -- 0-100
    Note        TEXT,
    Sources     TEXT,
    GeneratedAt TEXT,
    ModelId     TEXT
);

CREATE TABLE "LibraryPaths" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_LibraryPaths" PRIMARY KEY AUTOINCREMENT,
    "Path" TEXT NOT NULL,
    "Category" INTEGER NOT NULL,
    "IsCalibreLibrary" INTEGER NOT NULL,
    "AuthorizedUsersJson" TEXT NOT NULL
);

CREATE TABLE LibraryRatingOverrides (
    TargetType TEXT NOT NULL,
    TargetId   INTEGER NOT NULL,
    Rating     INTEGER NOT NULL,
    Note       TEXT,
    CreatedAt  TEXT,
    PRIMARY KEY (TargetType, TargetId)
);

CREATE TABLE LibrarySeriesRatings (
    SeriesId    INTEGER NOT NULL PRIMARY KEY,
    Rating      INTEGER NOT NULL,   -- 0-100
    Note        TEXT,               -- why this rating
    Sources     TEXT,               -- csv of signals used
    GeneratedAt TEXT,
    ModelId     TEXT
);

CREATE TABLE LocgApiCaches (
    RequestKey   TEXT NOT NULL PRIMARY KEY,
    ResponseJson TEXT NOT NULL,
    FetchedAt    TEXT NOT NULL DEFAULT ''
);

CREATE TABLE LocgCollectedEditions (
    ComicId        INTEGER NOT NULL PRIMARY KEY,
    SeriesId       INTEGER,
    IssueStart     REAL    NOT NULL DEFAULT 0,
    IssueEnd       REAL    NOT NULL DEFAULT 0,
    EditionTitle   TEXT,
    LocgComicId    INTEGER,
    ContainedCount INTEGER NOT NULL DEFAULT 0,
    Contiguous     INTEGER NOT NULL DEFAULT 1,
    Confidence     REAL    NOT NULL DEFAULT 0,
    CreatedAt      TEXT    NOT NULL DEFAULT ''
);

CREATE TABLE LocgComics (LocgComicId INTEGER NOT NULL PRIMARY KEY, LocgSeriesId INTEGER, SeriesName TEXT,
  Title TEXT, IssueNumber TEXT, Format TEXT, ReleaseDate TEXT, CoverDate TEXT, PageCount INTEGER, Description TEXT,
  CommunityRating REAL, RatingCount INTEGER, IsKey INTEGER NOT NULL DEFAULT 0, KeyType TEXT, KeyReason TEXT, Isbn TEXT,
  Upc TEXT, DistributorSku TEXT, CoverPrice TEXT, EstimatedValue TEXT, CoverUrl TEXT, Url TEXT, StoryCount INTEGER,
  StoryIdsJson TEXT, CreatorsJson TEXT, RawJson TEXT, ScrapedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE LocgContainments (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, ContainerLocgComicId INTEGER NOT NULL,
  ContainedLocgComicId INTEGER NOT NULL, ChapterTitle TEXT, Ordinal INTEGER NOT NULL DEFAULT 0,
  Source TEXT NOT NULL DEFAULT 'stories', StoryId INTEGER, ScrapedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE LocgMatches (
    ComicId       INTEGER NOT NULL PRIMARY KEY,
    LocgComicId   INTEGER,
    LocgSeriesId  INTEGER,
    Slug          TEXT,
    Status        TEXT    NOT NULL DEFAULT 'Pending',
    MatchMethod   TEXT,
    MatchedKey    TEXT,
    Confidence    REAL    NOT NULL DEFAULT 0,
    ErrorMessage  TEXT,
    LastScrapedAt TEXT,
    Applied       INTEGER NOT NULL DEFAULT 0
, MatchQuality TEXT);

CREATE TABLE LocgSeries (
  LocgSeriesId INTEGER NOT NULL PRIMARY KEY, Name TEXT, Publisher TEXT,
  YearBegin INTEGER, YearEnd INTEGER, YearText TEXT, IssueCount INTEGER,
  ImportedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE LocgSeriesInference (
  GcdSeriesId INTEGER NOT NULL PRIMARY KEY, LocgSeriesId TEXT, SeriesName TEXT,
  Support INTEGER NOT NULL DEFAULT 0, ImportedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE MangaUpdatesMatches (
  SeriesId INTEGER PRIMARY KEY, MuSeriesId INTEGER, Status TEXT, MatchMethod TEXT,
  Confidence REAL, MatchedKey TEXT, CandidatesJson TEXT, CreatedAt TEXT);

CREATE TABLE MangaUpdatesSeries (
  MuSeriesId INTEGER PRIMARY KEY, Title TEXT, Year INTEGER, Type TEXT, Status TEXT,
  Completed INTEGER, Description TEXT, GenresJson TEXT, CategoriesJson TEXT,
  BayesianRating REAL, Url TEXT, RawJson TEXT, ScrapedAt TEXT, TagsCsv TEXT);

CREATE TABLE MarvelIssues (
  MarvelIssueId INTEGER PRIMARY KEY, MarvelSeriesId INTEGER, Number TEXT, Slug TEXT,
  ScrapedAt TEXT);

CREATE TABLE MarvelMatches (
  ComicId INTEGER PRIMARY KEY, MarvelIssueId INTEGER, MatchMethod TEXT, Confidence REAL,
  CreatedAt TEXT);

CREATE TABLE MarvelSeries (
  MarvelSeriesId INTEGER PRIMARY KEY, Slug TEXT, Name TEXT, YearStart INTEGER,
  YearEnd INTEGER, ScrapedAt TEXT);

CREATE TABLE MarvelSeriesMatches (
  SeriesId INTEGER PRIMARY KEY, MarvelSeriesId INTEGER, Status TEXT, Confidence REAL,
  MatchedKey TEXT, CreatedAt TEXT);

CREATE TABLE OlSeriesInference (
  GcdSeriesId INTEGER NOT NULL PRIMARY KEY, OlWorkKey TEXT, SeriesString TEXT,
  SubjectsJson TEXT, IsbnSupport INTEGER NOT NULL DEFAULT 0, ImportedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE OpenLibraryEditions (
  Isbn TEXT NOT NULL PRIMARY KEY, Title TEXT, Subtitle TEXT, AuthorsJson TEXT, Publishers TEXT,
  PublishDate TEXT, Pages INTEGER, SubjectsJson TEXT, CoverUrl TEXT, OlEditionKey TEXT,
  OlWorkKey TEXT, SeriesString TEXT, PhysicalFormat TEXT, ImportedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE OpenLibraryWorks (
  WorkKey TEXT NOT NULL PRIMARY KEY, Title TEXT, SubjectsJson TEXT, SeriesString TEXT,
  EditionCount INTEGER NOT NULL DEFAULT 0, ImportedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE Publishers (
    Id       INTEGER PRIMARY KEY AUTOINCREMENT,
    Name     TEXT    NOT NULL UNIQUE,
    FullName TEXT    NOT NULL
);

CREATE TABLE Series (
    Id                INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    ParsedKey         TEXT    NOT NULL,
    ResolvedName      TEXT    NOT NULL DEFAULT '',
    ComicvineVolumeId INTEGER
, ExternalWorkId INTEGER, CanonicalKey TEXT NOT NULL DEFAULT '', IssueCount INTEGER NOT NULL DEFAULT 0, DisplayNameOverride TEXT, YearStart INTEGER, YearEnd INTEGER, IsOngoing INTEGER NOT NULL DEFAULT 0, Franchise TEXT);

CREATE TABLE SeriesAliases (
    AliasName     TEXT NOT NULL PRIMARY KEY,
    CanonicalName TEXT NOT NULL,
    NormType      TEXT NOT NULL DEFAULT 'Rule',
    Confidence    TEXT NOT NULL DEFAULT 'High',
    Notes         TEXT,
    CreatedAt     TEXT NOT NULL
);

CREATE TABLE SeriesInferenceDecisions (
  Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, SeriesKey TEXT NOT NULL, Class TEXT NOT NULL,
  Action TEXT NOT NULL, Target TEXT, Confidence TEXT NOT NULL DEFAULT '', EvidenceJson TEXT,
  State TEXT NOT NULL DEFAULT 'Queued', UndoJson TEXT, DecidedBy TEXT, DecidedAt TEXT NOT NULL DEFAULT '');

CREATE TABLE SeriesMatchReviews (
    Id        INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    Scope     TEXT    NOT NULL,
    Key       TEXT    NOT NULL,
    State     TEXT    NOT NULL DEFAULT 'New',
    Note      TEXT,
    DecidedBy TEXT,
    DecidedAt TEXT    NOT NULL DEFAULT ''
);

CREATE TABLE SeriesMergeLogs (
    OldSeriesId  INTEGER NOT NULL PRIMARY KEY,
    NewSeriesId  INTEGER NOT NULL,
    CanonicalKey TEXT    NOT NULL,
    MergedAt     TEXT    NOT NULL
);

CREATE TABLE SeriesParsedKeys (
    ParsedKey TEXT    NOT NULL PRIMARY KEY,
    SeriesId  INTEGER NOT NULL
);

CREATE TABLE SeriesUserLists (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Username TEXT NOT NULL,
    SeriesName TEXT NOT NULL,
    ListType INTEGER NOT NULL,
    AddedAt TEXT NOT NULL,
    UNIQUE(Username, SeriesName, ListType)
);

CREATE TABLE "Sessions" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY AUTOINCREMENT,
    "Username" TEXT NOT NULL,
    "Token" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "LastActivityAt" TEXT NOT NULL
);

CREATE TABLE SiteSettings (
    Key TEXT NOT NULL PRIMARY KEY,
    Value TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE TABLE SystemState (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);

CREATE TABLE TagAliases (
    Category    TEXT NOT NULL,
    AliasTag    TEXT NOT NULL,
    CanonicalTag TEXT NOT NULL,
    Source      TEXT NOT NULL DEFAULT 'Rule',
    PRIMARY KEY (Category, AliasTag)
);

CREATE TABLE "Tags" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Tags" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "NormalizedName" TEXT NOT NULL
);

CREATE TABLE "Users" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
    "Username" TEXT NOT NULL,
    "PasswordHash" TEXT NOT NULL,
    "IsAdmin" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL
, MustChangePassword INTEGER NOT NULL DEFAULT 0, MaxMaturity INTEGER NOT NULL DEFAULT 3, KidsStyle TEXT NULL);

CREATE TABLE sqlite_sequence(name,seq);

CREATE INDEX "IX_Bookmarks_ComicId" ON "Bookmarks" ("ComicId");

CREATE UNIQUE INDEX "IX_Bookmarks_Username_ComicId" ON "Bookmarks" ("Username", "ComicId");

CREATE INDEX "IX_ComicTagAssociation_TagAssociationsId" ON "ComicTagAssociation" ("TagAssociationsId");

CREATE UNIQUE INDEX "IX_Comics_FilePath" ON "Comics" ("FilePath");

CREATE INDEX "IX_Comics_IndexedAt" ON "Comics" ("IndexedAt");

CREATE INDEX "IX_Comics_NormalizedTitle" ON "Comics" ("NormalizedTitle");

CREATE INDEX "IX_Comics_ParentFolderId" ON "Comics" ("ParentFolderId");

CREATE INDEX "IX_Comics_SeriesName" ON "Comics" ("SeriesName");

CREATE UNIQUE INDEX "IX_Folders_FolderPath" ON "Folders" ("FolderPath");

CREATE INDEX "IX_Folders_ParentId" ON "Folders" ("ParentId");

CREATE UNIQUE INDEX "IX_Sessions_Token" ON "Sessions" ("Token");

CREATE UNIQUE INDEX "IX_Tags_NormalizedName" ON "Tags" ("NormalizedName");

CREATE UNIQUE INDEX "IX_Users_Username" ON "Users" ("Username");

CREATE INDEX idx_claudebookmeta_maturity ON ClaudeBookMetadata(Maturity);

CREATE INDEX idx_claudebooktags_cat_tag ON ClaudeBookTags(Category, Tag);

CREATE INDEX idx_claudebooktags_comic ON ClaudeBookTags(ComicId);

CREATE INDEX idx_claudetags_cat_tag ON ClaudeSeriesTags(Category, Tag);

CREATE INDEX idx_claudetags_metadata ON ClaudeSeriesTags(MetadataId);

CREATE INDEX idx_collnode_series ON ComicCollectionNodes(SeriesId);

CREATE INDEX idx_comics_category ON Comics(Category);

CREATE INDEX idx_comics_folder_group ON Comics(FolderGroupId);

CREATE INDEX idx_comics_parent_folder ON Comics(ParentFolderId);

CREATE INDEX idx_comics_publisher ON Comics(PublisherId);

CREATE INDEX idx_curatedcolled_series ON CuratedCollectedEditions(SeriesId);

CREATE INDEX idx_cvcolled_series ON ComicvineCollectedEditions(SeriesId);

CREATE INDEX idx_dupmember_comic ON DuplicateMembers(ComicId);

CREATE INDEX idx_dupmember_group ON DuplicateMembers(DuplicateGroupId);

CREATE INDEX idx_extlinks_status ON ExternalSeriesLinks(Status);

CREATE INDEX idx_extlinks_work ON ExternalSeriesLinks(ExternalWorkId);

CREATE INDEX idx_gcdcolled_series ON GcdCollectedEditions(SeriesId);

CREATE INDEX idx_gcdissue_barcode ON GcdIssues(Barcode);

CREATE INDEX idx_gcdissue_isbn ON GcdIssues(ValidIsbn);

CREATE INDEX idx_gcdissue_series ON GcdIssues(GcdSeriesId);

CREATE INDEX idx_gcdmatch_issue ON GcdMatches(GcdIssueId);

CREATE INDEX idx_gum_user_fav ON GroupUserMetadata(Username, IsFavorite) WHERE IsFavorite = 1;

CREATE INDEX idx_gum_user_type ON GroupUserMetadata(Username, GroupType);

CREATE INDEX idx_gum_user_wtr ON GroupUserMetadata(Username, WantToRead) WHERE WantToRead = 1;

CREATE INDEX idx_infdecision_state ON SeriesInferenceDecisions(State, Class);

CREATE INDEX idx_locgcolled_series ON LocgCollectedEditions(SeriesId);

CREATE INDEX idx_locgcomic_series ON LocgComics(LocgSeriesId);

CREATE INDEX idx_locgcontain_contained ON LocgContainments(ContainedLocgComicId);

CREATE INDEX idx_locgmatch_locgid ON LocgMatches(LocgComicId);

CREATE INDEX idx_oled_work ON OpenLibraryEditions(OlWorkKey);

CREATE INDEX idx_parsed_confidence ON ComicParsedDetails(Confidence);

CREATE INDEX idx_parsed_event ON ComicParsedDetails(EventName);

CREATE INDEX idx_parsed_series ON ComicParsedDetails(Series);

CREATE INDEX idx_parsed_year ON ComicParsedDetails(Year);

CREATE INDEX idx_pd_seriesid ON ComicParsedDetails(SeriesId);

CREATE INDEX idx_readorder_group ON ComicReadingOrder(GroupKey, ReadIndex);

CREATE INDEX idx_serieslinks_status ON ComicvineSeriesLinks(Status);

CREATE INDEX idx_serieslinks_volume ON ComicvineSeriesLinks(ComicvineVolumeId);

CREATE INDEX idx_seriesparsedkeys_series ON SeriesParsedKeys(SeriesId);

CREATE INDEX idx_seriesuserl_user_type ON SeriesUserLists(Username, ListType);

CREATE INDEX idx_userl_comic ON ComicUserLists(ComicId);

CREATE INDEX idx_userl_user_type ON ComicUserLists(Username, ListType);

CREATE UNIQUE INDEX uix_claudemeta_sn ON ClaudeSeriesMetadata(lower(SeriesName));

CREATE UNIQUE INDEX uix_extworks_provider_key ON ExternalWorks(Provider, ProviderKey);

CREATE UNIQUE INDEX uix_locgcontain ON LocgContainments(ContainerLocgComicId, ContainedLocgComicId);

CREATE UNIQUE INDEX uix_series_parsedkey ON Series(ParsedKey);

CREATE UNIQUE INDEX uix_seriesmatchreview ON SeriesMatchReviews(Scope, Key);
