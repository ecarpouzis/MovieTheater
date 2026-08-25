using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// A synthetic v1 database built from the REAL v1 DDL (Fixtures/schema-v1.sql) with rows manufactured to
    /// exercise every branch the migration has: comics and books, a series with several insight rows, an orphan
    /// insight, a span-corroborated LOCG match, comic- and series-typed group marks, a LOCG stub, a Calibre-linked
    /// book, an excluded shadow duplicate, a three-deep folder tree, name-keyed links. Plus the two empty v2
    /// files, migrated to the current model. Everything is a throwaway file under the temp directory.
    /// </summary>
    public sealed class V1Fixture : IDisposable
    {
        public readonly string WorkDir, V1Path, HotPath, LegsPath, CalibreLinkPath, CacheDir;
        public const string Owner = "owner";

        public V1Fixture()
        {
            WorkDir = Path.Combine(Path.GetTempPath(), "books-migrate-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkDir);
            V1Path = Path.Combine(WorkDir, "v1.db");
            HotPath = Path.Combine(WorkDir, "books.db");
            LegsPath = Path.Combine(WorkDir, "books-legs.db");
            CalibreLinkPath = Path.Combine(WorkDir, "calibre_link.json");
            CacheDir = Path.Combine(WorkDir, "cache");
            Directory.CreateDirectory(CacheDir);
            BuildV1();
            using (var hot = new BooksDb(BooksDbOptions.Hot(HotPath))) hot.Database.Migrate();
            using (var legs = new BooksLegsDb(BooksDbOptions.Legs(LegsPath))) legs.Database.Migrate();
        }

        public MigrationOptions Options(int batchSize = 50, int maxBatches = 0, string? stage = null, bool dryRun = false) => new()
        {
            SourcePath = V1Path, TargetPath = HotPath, LegsPath = LegsPath, CalibreLinkPath = CalibreLinkPath, CacheDir = CacheDir, ReportDir = WorkDir,
            BatchSize = batchSize, MaxBatches = maxBatches, Stage = stage, DryRun = dryRun, OwnerUsername = Owner,
        };

        private readonly List<V1Source> sources = new();

        public MigrationEngine Engine(MigrationOptions? options = null, List<string>? log = null)
        {
            var source = new V1Source(V1Path);
            sources.Add(source);
            var ctx = new MigrationContext(source, MappingContract.Load(), options ?? Options(), l => log?.Add(l));
            return new MigrationEngine(ctx);
        }

        public TargetWriter Hot(bool dryRun = true) => new(HotPath, MappingContract.Load(), dryRun);
        public TargetWriter Legs(bool dryRun = true) => new(LegsPath, MappingContract.Load(), dryRun);
        public BooksDb HotDb() => new(BooksDbOptions.Hot(HotPath));

        public long HotCount(string table, string? where = null)
        {
            using var w = Hot();
            return w.Scalar<long>($"SELECT count(*) FROM \"{table}\"" + (where == null ? "" : " WHERE " + where));
        }

        public long LegsCount(string table)
        {
            using var w = Legs();
            return w.Scalar<long>($"SELECT count(*) FROM \"{table}\"");
        }

        private static string Schema()
        {
            using var s = typeof(V1Fixture).Assembly.GetManifestResourceStream("MovieTheater.Books.Tests.schema-v1.sql")!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        private void BuildV1()
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = V1Path, Pooling = false }.ToString());
            conn.Open();
            // the frozen v1 file carries orphan references of its own (a ghost dedup member, a bookmark on a deleted
            // comic, an issue whose volume was never fetched); the fixture manufactures the same, so FKs stay off here
            using (var pragma = conn.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=OFF;"; pragma.ExecuteNonQuery(); }
            void Exec(string sql, params (string, object?)[] args)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            // the real DDL; the FTS virtual table needs fts5, which Microsoft.Data.Sqlite's bundle ships
            foreach (var stmt in SplitStatements(Schema()))
            {
                if (!stmt.Contains("CREATE", StringComparison.OrdinalIgnoreCase)) continue;
                // the dump lists the FTS5 shadow tables (ComicFts_config/_data/_docsize/_idx) that CREATE VIRTUAL TABLE makes itself
                if (stmt.Contains("ComicFts_", StringComparison.Ordinal) || stmt.Contains("sqlite_sequence", StringComparison.Ordinal)) continue;
                // v1's NOT NULLs are relaxed: the fixture manufactures sparse rows on purpose, and the migration must
                // read them through its tolerant accessors either way
                Exec(stmt.Replace(" NOT NULL", "", StringComparison.OrdinalIgnoreCase));
            }

            const string acl = "[\"owner\",\"friend\"]";
            Exec("INSERT INTO LibraryPaths (Id, Path, Category, IsCalibreLibrary, AuthorizedUsersJson) VALUES (1, '\\\\nas\\comics', 0, 0, $a), (2, '\\\\nas\\books', 1, 1, $a)", ("$a", acl));
            // folders: root(1) > 2000AD(2) > 2000AD (1977)(3) > Unsorted(4); root books(10) > Fiction(11)
            Exec("INSERT INTO Folders (Id, ParentId, Category, FolderPath, FolderName, NormalizedName, IndexedAt, FolderModifiedAt, AuthorizedUsersJson) VALUES " +
                 "(1, NULL, 0, '\\\\nas\\comics', 'comics', 'comics', '2026-05-27 05:54:30', '2026-05-01 00:00:00', $a)," +
                 "(2, 1, 0, '\\\\nas\\comics\\2000AD', '2000AD', '2000ad', '2026-05-27 05:54:31', '2026-05-01 00:00:00', $a)," +
                 "(3, 2, 0, '\\\\nas\\comics\\2000AD\\2000AD (1977)', '2000AD (1977)', '2000ad (1977)', '2026-05-27 05:54:32', '2026-05-01 00:00:00', $a)," +
                 "(4, 3, 0, '\\\\nas\\comics\\2000AD\\2000AD (1977)\\Unsorted', 'Unsorted', 'unsorted', '2026-05-27 05:54:33', '2026-05-01 00:00:00', $a)," +
                 "(5, 1, 0, '\\\\nas\\comics\\Batman', 'Batman', 'batman', '2026-05-27 05:54:34', '2026-05-01 00:00:00', $a)," +
                 "(10, NULL, 1, '\\\\nas\\books', 'books', 'books', '2026-05-27 05:54:35', '2026-05-01 00:00:00', $a)," +
                 "(11, 10, 1, '\\\\nas\\books\\Fiction', 'Fiction', 'fiction', '2026-05-27 05:54:36', '2026-05-01 00:00:00', $a)", ("$a", acl));
            Exec("INSERT INTO FolderAggregates (FolderId, DirectChildCount, DescendantComicCount, UpdatedAt) VALUES (2, 1, 3, '2026-06-01'), (5, 0, 3, '2026-06-01'), (11, 0, 2, '2026-06-01')");
            File.WriteAllBytes(Path.Combine(CacheDir, "f_2.jpg"), new byte[] { 1, 2, 3 });
            Exec("INSERT INTO Publishers (Id, Name, FullName) VALUES (1, 'Rebellion', 'Rebellion Developments'), (2, 'DC', 'DC Comics')");
            // series: 2000 AD (cv), Batman (cv), 'The Umbrella Academy' merged-away spelling kept as alias, Doppelganger (parsed, single issue)
            Exec("INSERT INTO Series (Id, ParsedKey, ResolvedName, ComicvineVolumeId, ExternalWorkId, CanonicalKey, IssueCount, DisplayNameOverride, YearStart, YearEnd, IsOngoing, Franchise) VALUES " +
                 "(1, '2000 AD', '2000 AD', 19752, NULL, 'cv:19752', 3, NULL, 1977, 2026, 1, '2000 AD')," +
                 "(2, 'Batman', 'Batman', 796, NULL, 'cv:796', 2, NULL, 1940, 2011, 0, 'Batman')," +
                 "(3, 'Doppelganger', 'Doppelganger', NULL, NULL, 'parsed:doppelganger', 1, NULL, 2020, 2020, 0, NULL)," +
                 "(4, 'Fantastic Four Omnibus', 'Fantastic Four', NULL, 7, 'ext:7', 1, NULL, 2023, 2023, 0, NULL)");
            Exec("INSERT INTO SeriesParsedKeys (ParsedKey, SeriesId) VALUES ('2000 AD', 1), ('2000AD', 1), ('2000AD (1977)', 1), ('Batman', 2), ('The Batman', 2), ('Doppelganger', 3), ('Fantastic Four Omnibus', 4)");
            Exec("INSERT INTO SeriesMergeLogs (OldSeriesId, NewSeriesId, CanonicalKey, MergedAt) VALUES (99, 1, '', '2026-06-01 00:00:00'), (98, 2, '', '2026-06-01T00:00:00+00:00')");
            Exec("INSERT INTO ComicvineVolumes (ComicvineId, Name, StartYear, PublisherId, PublisherName, CountOfIssues, Deck, Description, ImageUrl, SiteDetailUrl, FetchedAt, ConceptsJson, CharactersJson, LocationsJson, ObjectsJson, TeamsJson) VALUES " +
                 "(19752, '2000 AD', 1977, 1, 'Rebellion', 2400, 'The Galaxy''s Greatest Comic', '<p>Weekly British anthology comic featuring Judge Dredd and many more strips since 1977, a long-running science fiction institution.</p>', NULL, NULL, '2026-06-01 00:00:00', '[]', '[]', '[]', '[]', '[]')," +
                 "(796, 'Batman', 1940, 2, 'DC Comics', 715, 'The Dark Knight', 'Issues #1-715. Continued in Batman (2011).', NULL, NULL, '2026-06-01 00:00:00', '[]', '[]', '[]', '[]', '[]')");
            Exec("INSERT INTO ComicvineIssues (ComicvineId, VolumeId, Name, IssueNumber, CoverDate, StoreDate, Deck, Description, ImageUrl, SiteDetailUrl, FetchedAt) VALUES (5001, 19752, 'Prog 1', '1', '1977-02-26', NULL, NULL, NULL, NULL, NULL, '2026-06-01'), (5002, 4040404, 'orphan', '1', NULL, NULL, NULL, NULL, NULL, NULL, '2026-06-01')");
            Exec("INSERT INTO ExternalWorks (Id, Provider, ProviderKey, Title, Authors, Publisher, FirstPublishYear, Description, CoverImageUrl, SubjectsJson, TagsCsv, Isbn, InfoUrl, FetchedAt) VALUES " +
                 "(7, 'openlibrary', '/works/OL1W', 'Fantastic Four', 'Dan Slott, Carlos Pacheco', 'Marvel', 2023, 'The first family of comics returns in a science fiction adventure that spans galaxies and decades of continuity.', NULL, '[\"Science fiction\",\"Superhero comics\",\"Comic books, strips, etc\"]', NULL, NULL, NULL, '2026-06-01')");
            // comics
            string comic(int id, int folder, string series, string title, string idx, string? desc, string? writers, string? genre, string? pubDate, int excluded = 0) =>
                $"({id}, {folder}, 0, '\\\\nas\\comics\\x\\{title.Replace("'", "''")}.cbz', '{title.Replace("'", "''")}.cbz', '.cbz', 1000, '2025-02-03 21:39:47', '2026-05-27 05:54:30.6196618', '{title.Replace("'", "''")}', '{title.ToLowerInvariant().Replace("'", "''")}', " +
                $"{(desc == null ? "NULL" : "'" + desc.Replace("'", "''") + "'")}, 'en', '{series.Replace("'", "''")}', '{idx}', NULL, NULL, NULL, 'Rebellion', {(pubDate == null ? "NULL" : "'" + pubDate + "'")}, {(writers == null ? "NULL" : "'" + writers + "'")}, 'Carlos Ezquerra', NULL, 'weekly, sci-fi', 32, NULL, NULL, 1, NULL, NULL, NULL, NULL, NULL, 'Single Issue', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, {(genre == null ? "NULL" : "'" + genre + "'")}, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 1, 2, 0, NULL, NULL, NULL, NULL, 1000, 1500, {excluded}, {(excluded == 1 ? "'shadow duplicate'" : "NULL")}, NULL, 'fp{id}', {id * 1000000007L}, NULL, NULL, 0, NULL)";
            var cols = "(Id, ParentFolderId, Category, FilePath, FileName, FileExtension, FileSize, FileModifiedAt, IndexedAt, Title, NormalizedTitle, Description, Language, SeriesName, SeriesIndex, AltSeriesName, AltSeriesIndex, Volume, Publisher, PublicationDate, Writers, Pencillers, Identifier, Tags, PageCount, EmbeddedRating, UserRating, MetadataVersion, IssueTitle, AlternateCount, Count, SeriesGroup, Imprint, Format, AgeRating, Web, GTIN, Inker, Colorist, Letterer, CoverArtist, Editor, Translator, Genre, Characters, Teams, Locations, StoryArc, StoryArcNumber, MainCharacterOrTeam, BlackAndWhite, Manga, Notes, PublisherId, FolderGroupId, IsBroken, BrokenReason, BrokenCheckedAt, ThumbnailError, ThumbnailCheckedAt, CoverWidth, CoverHeight, ExcludedFromLibrary, ExclusionReason, ExcludedAt, ContentFingerprint, CoverPHash, PageSignature, SignaturesComputedFor, KeepInDirectory, CoverDimsComputedFor)";
            Exec("INSERT INTO Comics " + cols + " VALUES " + string.Join(",", new[]
            {
                comic(1, 4, "2000 AD", "2000 AD #1", "1", "Judge Dredd debuts in the second prog of the weekly anthology that would define British comics for decades to come.", "Pat Mills, John Wagner", "Science Fiction, Anthology", "1977-02-26"),
                comic(2, 4, "2000 AD", "2000 AD #2", "2", "Collects nothing.", "Pat Mills; John Wagner", "Harumi Kiyama, Science Fiction", "1977-03-05"),
                comic(3, 4, "2000 AD", "2000 AD #2 (copy)", "2", null, null, null, null, excluded: 1),
                comic(4, 5, "Batman", "Batman #404", "404", null, "Frank Miller", "Superhero", "1987-02"),
                comic(5, 5, "Batman", "Batman #405", "405", "<p>Year One continues &amp; Gordon arrives.</p>", "Frank Miller", "Superhero", "1987-03"),
                comic(6, 5, "Doppelganger", "Doppelganger #4", "4", null, null, null, "2020"),
                comic(7, 5, "Fantastic Four Omnibus", "Fantastic Four Omnibus Vol 1", "1", null, null, null, null),
            }));
            // books (Calibre-native): Identifier = ISBN, Writers = 'A & B'
            Exec("INSERT INTO Comics " + cols + " VALUES " +
                 "(101, 11, 1, '\\\\nas\\books\\Fiction\\Brave New World.epub', 'Brave New World.epub', '.epub', 5000, '2025-02-03 21:39:47', '2026-05-27 06:00:00', 'Brave New World', 'brave new world', 'A dystopian novel of a genetically engineered society, written in 1931 and still unsettling.', 'en', 'Classics', '1.0', NULL, NULL, NULL, 'Harper', '2006-10-17', 'Aldous Huxley', NULL, '9780060850524', 'Classics, Dystopias', 0, NULL, NULL, 1, 'Brave New World', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL)," +
                 "(102, 11, 1, '\\\\nas\\books\\Fiction\\Dune.epub', 'Dune.epub', '.epub', 6000, '2025-02-03 21:39:47', '2026-05-27 06:00:01', 'Dune', 'dune', NULL, 'en', NULL, NULL, NULL, NULL, NULL, 'Chilton', '1965', 'Frank Herbert & Brian Herbert', NULL, NULL, 'Science Fiction', 0, NULL, NULL, 1, 'Dune', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL)");
            File.WriteAllText(CalibreLinkPath, "[{\"comicId\": 101, \"calibreId\": 844, \"series\": null, \"seriesIndex\": 1.0, \"isbn\": \"9780060850524\", \"authors\": [\"Aldous Huxley\"], \"tags\": [\"Classics\"], \"title\": \"Brave New World\"}]");
            // parsed details
            Exec("INSERT INTO ComicParsedDetails (ComicId, Series, IssueNo, Year, VolumeNo, Publisher, Format, IsCollection, Confidence, SeriesSource, IssueSource, YearSource, PublisherSource, FolderSeries, FolderYear, ParseNotes, ParsedAt, EventName, IssueTitle, ClaudeSeriesMetadataId, ComicvineVolumeId, ExternalWorkId, SeriesId) VALUES " +
                 "(1, '2000 AD', '1', 1977, NULL, 'Rebellion', 'Single Issue', 0, 'High', 'Filename', 'Filename', 'Filename', 'Folder', '2000AD (1977)', 1977, NULL, '2026-06-01', NULL, NULL, 1, 19752, NULL, 1)," +
                 "(2, '2000AD', '2', 1977, NULL, 'Rebellion', 'Single Issue', 0, 'Medium', 'Metadata', 'Metadata', 'Metadata', 'Metadata', NULL, NULL, NULL, '2026-06-01', NULL, NULL, 1, 19752, NULL, 1)," +
                 "(3, '2000AD (1977)', '2', 1977, NULL, 'Rebellion', 'Single Issue', 0, 'Low', 'Manual', 'None', 'None', 'Folder', NULL, NULL, NULL, '2026-06-01', NULL, NULL, 1, 19752, NULL, 1)," +
                 "(4, 'Batman', '404', 1987, NULL, 'DC', 'Single Issue', 0, 'High', 'Filename', 'Filename', 'Filename', 'Folder', 'Batman', NULL, NULL, '2026-06-01', 'Year One', NULL, 4, 796, NULL, 2)," +
                 "(5, 'The Batman', '405', 1987, 2, 'DC', 'TPB', 0, 'High', 'Filename', 'Filename', 'Filename', 'Folder', 'Batman', NULL, NULL, '2026-06-01', 'Year One', NULL, 4, 796, NULL, 2)," +
                 "(6, 'Doppelganger', '4', 2020, NULL, NULL, 'Limed Series', 0, 'Low', 'Filename', 'Filename', 'Filename', 'Folder', NULL, NULL, NULL, '2026-06-01', NULL, NULL, NULL, NULL, NULL, 3)," +
                 "(7, 'Fantastic Four Omnibus', NULL, NULL, NULL, NULL, 'Omnibus', 1, 'Medium', 'Filename', 'None', 'None', 'Folder', NULL, NULL, NULL, '2026-06-01', NULL, NULL, NULL, NULL, 7, 4)");
            // links: name-keyed CV/External + item-level
            Exec("INSERT INTO ComicvineSeriesLinks (SeriesName, ComicvineVolumeId, SearchQuery, MatchScore, Status, AttemptedAt, AttemptCount, ErrorMessage, CandidatesJson) VALUES " +
                 "('2000 AD', 19752, '2000 ad', 100, 1, '2026-06-01', 1, NULL, NULL), ('2000AD', 19752, '2000ad', 95, 1, '2026-06-01', 1, NULL, '[{\"VolumeId\":19752,\"Score\":95},{\"VolumeId\":1,\"Score\":40}]'), ('2000AD (1977)', 19752, '2000ad 1977', 90, 1, '2026-06-01', 1, NULL, NULL)," +
                 "('Batman', 796, 'batman', 100, 6, '2026-06-01', 2, NULL, NULL), ('The Batman', 796, 'the batman', 90, 1, '2026-06-01', 1, NULL, NULL), ('Doppelganger', NULL, 'doppelganger', NULL, 2, '2026-06-01', 3, NULL, NULL)");
            Exec("INSERT INTO ExternalSeriesLinks (SeriesName, ExternalWorkId, MatchedProvider, SearchQuery, MatchScore, Status, AttemptedAt, AttemptCount, ErrorMessage, CandidatesJson) VALUES ('Fantastic Four Omnibus', 7, 'openlibrary', 'fantastic four', 90, 1, '2026-06-01', 1, NULL, '[{\"provider\":\"openlibrary\",\"key\":\"/works/OL1W\",\"score\":90}]')");
            Exec("INSERT INTO ComicvineMatches (ComicId, Status, ComicvineIssueId, ComicvineVolumeId, SearchQuery, LastAttemptedAt, AttemptCount, ErrorMessage, CandidatesJson, Applied) VALUES (1, 1, 5001, 19752, NULL, '2026-06-01', 1, NULL, '[{\"VolumeId\":19752,\"IssueId\":5001,\"Score\":70}]', 1), (2, 0, NULL, NULL, NULL, NULL, 0, NULL, NULL, 0), (4, 3, NULL, NULL, NULL, '2026-06-01', 2, NULL, '[{\"VolumeId\":796,\"Score\":60},{\"VolumeId\":797,\"Score\":60}]', 0)");
            Exec("INSERT INTO LocgComics (LocgComicId, LocgSeriesId, SeriesName, Title, IssueNumber, Format, ReleaseDate, CoverDate, PageCount, Description, CommunityRating, RatingCount, IsKey, KeyType, KeyReason, Isbn, Upc, DistributorSku, CoverPrice, EstimatedValue, CoverUrl, Url, StoryCount, StoryIdsJson, CreatorsJson, RawJson, ScrapedAt) VALUES " +
                 "(4686349, 1, '2000 AD', '2000 AD #1', '1', 'Comic', '1977-02-26', '1977-02-26', 32, 'Judge Dredd arrives in Mega-City One in a story that launched a legend. Comic • 32 pages • $0.75 Cover Date Feb 1977', 4.2, 12, 1, 'First appearance', NULL, NULL, NULL, NULL, '$0.75', NULL, NULL, NULL, 3, '[1,2,3]', '[{\"role\":\"Writer\",\"name\":\"Pat Mills\",\"peopleId\":\"248\"},{\"role\":\"Artist\",\"name\":\"Carlos Ezquerra\",\"peopleId\":\"249\"}]', NULL, '2026-06-01')," +
                 "(4686350, 1, '2000 AD', 'Prog 2 chapter stub', '2', NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL, NULL, NULL, '2026-06-01')");
            Exec("INSERT INTO LocgMatches (ComicId, LocgComicId, LocgSeriesId, Slug, Status, MatchMethod, MatchedKey, Confidence, ErrorMessage, LastScrapedAt, Applied, MatchQuality) VALUES " +
                 "(1, 4686349, NULL, NULL, 'matched', 'series-issue', '2000 ad|1', 1.0, NULL, '2026-06-01', 1, 'span-corroborated'), (2, NULL, NULL, NULL, 'NoMatch', NULL, NULL, NULL, NULL, '2026-06-01', 0, NULL), (4, NULL, NULL, NULL, 'cleared-pageaudit', 'series-issue', NULL, NULL, NULL, '2026-06-01', 0, 'Conflict')");
            Exec("INSERT INTO GcdIssues (GcdIssueId, GcdSeriesId, SeriesName, SeriesYearBegan, Number, Title, KeyDate, PublicationDate, ValidIsbn, Isbn, Barcode, PageCount, Price, Publisher, Format, VariantOfId, VariantName, ImportedAt, StoryGenres, TagsCsv) VALUES (900, 50, '2000 AD', 1977, '1', NULL, '1977-02-26', NULL, NULL, NULL, NULL, 32, NULL, 'IPC', NULL, NULL, NULL, '2026-06-01', 'science fiction;superhero;advocacy', 'Science Fiction, Superhero')");
            Exec("INSERT INTO GcdMatches (ComicId, GcdIssueId, GcdSeriesId, Status, MatchMethod, MatchedKey, Confidence, CandidateCount, ErrorMessage, Applied, CreatedAt) VALUES (1, 900, 50, 'Matched', 'isbn', NULL, 0.9, 1, NULL, 1, '2026-06-01'), (5, NULL, NULL, 'Pending', NULL, NULL, NULL, 0, NULL, 0, '2026-06-01')");
            Exec("INSERT INTO BarneyProgs (ProgNo, CoverDate, Price, StripsJson, ScrapedAt) VALUES (1, '1977-02-26', '8p', '[]', '2026-06-01')");
            Exec("INSERT INTO BarneyMatches (ComicId, ProgNo, MatchMethod, CreatedAt) VALUES (1, 1, 'prog-number', '2026-06-01')");
            Exec("INSERT INTO MangaUpdatesSeries (MuSeriesId, Title, Year, Type, Status, Completed, Description, GenresJson, CategoriesJson, BayesianRating, Url, RawJson, ScrapedAt, TagsCsv) VALUES (77, 'Akira', 1982, 'Manga', 'Complete', 1, 'Neo-Tokyo, 2019. A biker gang, a secret government project, and a boy with terrible power.', '[\"Action\",\"Sci-fi\",\"Seinen\"]', '[\"Post-Apocalyptic\",\"Adapted to Anime\"]', 9.1, NULL, '{}', '2026-06-01', NULL)");
            Exec("INSERT INTO MangaUpdatesMatches (SeriesId, MuSeriesId, Status, MatchMethod, Confidence, MatchedKey, CandidatesJson, CreatedAt) VALUES (3, 77, 'matched', 'exact', 0.95, 'doppelganger', NULL, '2026-06-01'), (2, NULL, 'Ambiguous', 'fuzzy', 0.3, 'batman', '[{\"id\": 1, \"title\": \"Batman\"}]', '2026-06-01')");
            // reading order, containment, spans
            Exec("INSERT INTO ComicReadingOrder (ComicId, GroupKey, ReadTier, ReadNumber, ReadNumberSuffix, ReadDate, ReadDatePrecision, ReadIndex, ReadCount, Source, Confidence, Notes, ComputedAt) VALUES " +
                 "(1, 'cv:19752', 0, 1, 0, '1977-02-26', 'Day', 1, 3, 'ComicVine', 'High', NULL, '2026-06-13'), (2, 'cv:19752', 0, 2, 0, '1977-03-01', 'Year', 2, 3, 'IssueNo+Date', 'Medium', NULL, '2026-06-13'), (4, '', 0, 404, 0, '1987-02-01', 'Month', 1, 2, 'Date', 'Low', NULL, '2026-06-13'), (5, '', 0, 405, 0, '1987-03-01', 'Month', 2, 2, 'Date', 'Low', NULL, '2026-06-13'), (6, 'parsed:doppelganger', 0, 4, 0, '2020-07-01', 'Year', 1, 1, 'Unordered', 'Low', NULL, '2026-06-13')");
            Exec("INSERT INTO ComicCollectionNodes (ComicId, SeriesId, CollectionLevel, TrackRole, SpanStart, SpanEnd, ContainsCount, ParentComicId, SpanSource, SpanLabel) VALUES (1, 1, 0, 'primary', 1, 1, 1, NULL, 'inferred', NULL), (7, 4, 3, 'container', 1, 60, 60, NULL, 'curated', '#1-60'), (5, 2, 0, 'primary', 405, 405, 1, 7, 'none', NULL)");
            Exec("INSERT INTO CuratedCollectedEditions (ComicId, SeriesId, IssueStart, IssueEnd, EditionTitle, Source, Confidence, Note, CreatedAt) VALUES (7, 4, 1, 60, 'Fantastic Four Omnibus Vol 1', 'claude', 0.97, 'indicia', '2026-06-08')");
            Exec("INSERT INTO LocgCollectedEditions (ComicId, SeriesId, IssueStart, IssueEnd, EditionTitle, LocgComicId, ContainedCount, Contiguous, Confidence, CreatedAt) VALUES (7, 4, 1, 60, 'FF Omnibus', 4686349, 60, 1, 0.7, '2026-06-12T02:34:39.5199730Z')");
            // insights: series 1 has THREE rows (sonnet High newest, opus Medium, haiku High older), series 2 one row, an orphan name, books
            Exec("INSERT INTO ClaudeSeriesMetadata (Id, SeriesName, Rating, Synopsis, Confidence, KnownSeries, GeneratedAt, ModelId, Author, Artist, YearBegin, YearEnd, ReviewFlag, TagsCsv) VALUES " +
                 "(1, '2000 AD', 88, 'The long-running British weekly.', 'High', 1, '2026-05-30 17:43:55', 'claude-sonnet-4-6', 'Pat Mills', 'Carlos Ezquerra', 1977, NULL, NULL, 'Science Fiction, Anthology')," +
                 "(2, '2000AD', 90, 'Opus take on the weekly.', 'Medium', 1, '2026-06-02 10:00:00', 'claude-opus-4-8', NULL, NULL, 1977, NULL, NULL, NULL)," +
                 "(3, '2000AD (1977)', 70, 'Haiku take.', 'High', 1, '2026-05-01 10:00:00', 'claude-haiku-4-5', NULL, NULL, NULL, NULL, NULL, NULL)," +
                 "(4, 'batman', 92, 'The Dark Knight of Gotham.', 'High', 1, '2026-05-29T03:38:53.841755+00:00', 'claude-opus-4-8', 'Bob Kane', 'Bill Finger', 1940, NULL, NULL, 'Superhero')," +
                 "(5, 'No Such Series Anywhere', 10, 'orphan', 'Low', 0, '2026-05-29', 'file-metadata', NULL, NULL, NULL, NULL, NULL, NULL)");
            Exec("INSERT INTO ClaudeSeriesTags (MetadataId, Category, Tag) VALUES (1, 'genre', 'sci-fi'), (1, 'genre', 'anthology'), (1, 'character-focus', 'anthology'), (1, 'character-focus', 'Judge Dredd'), (1, 'audience', 'teen'), (1, 'era', '1970s'), (2, 'genre', 'science-fiction'), (3, 'genre', 'sci-fi'), (4, 'genre', 'superhero'), (4, 'audience', 'teen'), (4, 'tone', 'dark'), (5, 'genre', 'x')");
            Exec("INSERT INTO ClaudeBookMetadata (ComicId, Rating, Synopsis, Confidence, KnownBook, Maturity, Author, YearPublished, GeneratedAt, ModelId, TagsCsv) VALUES (101, 85, 'Huxley''s dystopia.', 'High', 1, 2, 'Aldous Huxley', 1932, '2026-06-02 18:14:35', 'claude-opus-4-8', 'Dystopian'), (102, NULL, NULL, 'Low', 0, NULL, 'Frank Herbert', 1965, '2026-06-02 18:14:35', 'openlibrary', NULL)");
            Exec("INSERT INTO ClaudeBookTags (ComicId, Category, Tag) VALUES (101, 'genre', 'dystopian'), (101, 'audience', 'adult'), (102, 'setting', 'desert')");
            Exec("INSERT INTO KidSafeTags (Category, Tag, AppliesTo, UpdatedAt) VALUES ('audience', 'children', 'book', '2026-06-04'), ('audience', 'all-ages', 'comic', '2026-06-04')");
            Exec("INSERT INTO TagAliases (Category, AliasTag, CanonicalTag, Source) VALUES ('audience', 'adult readers', 'adult', 'Rule'), ('genre', 'science-fiction', 'sci-fi', 'Rule')");
            Exec("INSERT INTO CvdbResolutions (CvdbTag, ComicvineId, ResolvedName, EntityType, Status, ResolvedAt) VALUES ('CVDB100956', 100956, 'Harumi Kiyama', 'character', 'Resolved', '2026-05-30')");
            Exec("INSERT INTO LibraryComicRatings (ComicId, Rating, Note, Sources, GeneratedAt, ModelId) VALUES (1, 84, 'LOCG community 4.2/5', 'locg,series', '2026-06-20', 'blend-v3')");
            Exec("INSERT INTO LibrarySeriesRatings (SeriesId, Rating, Note, Sources, GeneratedAt, ModelId) VALUES (1, 86, NULL, 'series', '2026-06-20', 'blend-v3'), (2, 91, NULL, 'series', '2026-06-20', 'blend-v3')");
            Exec("INSERT INTO LibraryRatingOverrides (TargetType, TargetId, Rating, Note, CreatedAt) VALUES ('series', 2, 95, 'hand-set', '2026-06-21')");
            Exec("INSERT INTO SeriesInferenceDecisions (Id, SeriesKey, Class, Action, Target, Confidence, EvidenceJson, State, UndoJson, DecidedBy, DecidedAt) VALUES (1, '2000 AD Annual', 'Consolidation', 'fold', '2000 AD', 'High', NULL, 'AutoApplied', '[]', 'claude-round2', '2026-06-07')");
            Exec("INSERT INTO SeriesMatchReviews (Id, Scope, Key, State, Note, DecidedBy, DecidedAt) VALUES (1, 'link', 'Batman', 'Fixed', 'Cleared wrong CV volume', 'admin', '2026-06-09')");
            Exec("INSERT INTO DuplicateGroups (Id, Relationship, Confidence, Evidence, SuggestedKeeperComicId, ReviewState, DetectedAt) VALUES (907, 2, 'High', 'same fingerprint', 2, 'Pending', '2026-06-04')");
            Exec("INSERT INTO DuplicateMembers (Id, DuplicateGroupId, ComicId, Role, SoleFileInFolder) VALUES (1, 907, 2, 'Keeper', 0), (2, 907, 3, 'Shadow', 0), (3, 907, 999999, 'Ghost', 0)");
            Exec("INSERT INTO ComicvineApiCaches (RequestKey, ResponseJson, FetchedAt) VALUES ('volsearch:2000 ad', '{\"status_code\":1}', '2026-06-07')");
            Exec("INSERT INTO LocgContainments (Id, ContainerLocgComicId, ContainedLocgComicId, ChapterTitle, Ordinal, Source, StoryId, ScrapedAt) VALUES (1, 4686349, 4686350, 'Prog 2', 1, 'collects', NULL, '2026-06-01')");
            Exec("INSERT INTO LocgSeries (LocgSeriesId, Name, Publisher, YearBegin, YearEnd, YearText, IssueCount, ImportedAt) VALUES (1, '2000 AD', 'Rebellion', 1977, NULL, '1977 - Present', 2400, '2026-06-01')");
            Exec("INSERT INTO BarcodeScans (ComicId, CodesJson, PagesScanned, Error, ScannedAt) VALUES (4, '[]', 0, NULL, '2026-06-12')");
            // users + activity: the owner, a kid, a test account
            Exec("INSERT INTO Users (Id, Username, PasswordHash, IsAdmin, CreatedAt, MustChangePassword, MaxMaturity, KidsStyle) VALUES (2, 'owner', 'x', 1, '2026-05-27', 0, 3, 'pop'), (3, 'kid', 'x', 0, '2026-05-27', 0, 0, 'bubble'), (4, 'tester', 'x', 0, '2026-05-27', 0, 3, NULL)");
            Exec("INSERT INTO Bookmarks (Id, Username, ComicId, LastPage, LastSpineItemIndex, LastScrollPercent, Status, UpdatedAt, HiddenFromHistory) VALUES (30, 'owner', 1, -1, NULL, NULL, 2, '2026-05-27 16:28:27.1249468', 0), (31, 'owner', 2, 12, NULL, NULL, 1, '2026-06-11 06:36:57', 0), (32, 'owner', 101, 0, 4, 0.25, 1, '2026-06-12', 1), (33, 'kid', 1, 3, NULL, NULL, 1, '2026-06-12', 0), (34, 'owner', 999999, 0, NULL, NULL, 0, '2026-06-12', 0)");
            Exec("INSERT INTO ComicUserLists (Id, Username, ComicId, ListType, AddedAt) VALUES (1, 'owner', 2, 1, '2026-06-02 07:17:00'), (2, 'owner', 4, 1, '2026-06-02 07:18:00'), (3, 'tester', 1, 1, '2026-06-03')");
            Exec("INSERT INTO GroupUserMetadata (Id, Username, GroupType, GroupKey, IsFavorite, IsRead, WantToRead, Rating, Notes, UpdatedAt) VALUES " +
                 "(1, 'owner', 'series', '1', 1, 1, 0, 80, NULL, '2026-06-05'), (2, 'owner', 'series', 'batman', 0, 1, 0, NULL, NULL, '2026-06-04'), (3, 'owner', 'series', 'Nowhere Series', 0, 0, 1, NULL, NULL, '2026-06-04')," +
                 "(15, 'owner', 'comic', '4', 1, 0, 0, 30, 'meh', '2026-06-04 15:44:27'), (16, 'owner', 'comic', '5', 0, 0, 1, NULL, NULL, '2026-06-04'), (17, 'kid', 'series', '1', 0, 1, 0, NULL, NULL, '2026-06-04')");
            Exec("INSERT INTO SiteSettings (Key, Value, UpdatedAt) VALUES ('FolderCountAggregatesUpdatedAtUtc', '2026-06-01', '2026-06-01')");
            Exec("INSERT INTO SystemState (Key, Value) VALUES ('series_resolution_fingerprint', 'abc'), ('claude_tagfold_fingerprint', 'def'), ('cvcache_seed_fingerprint', 'ghi')");
        }

        private static IEnumerable<string> SplitStatements(string sql)
        {
            // the DDL dump is one statement per CREATE ...; blocks end with ");" or ";" at line end; split on ";\n" boundaries outside parentheses
            var depth = 0; var start = 0; var s = sql;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                else if (s[i] == ';' && depth <= 0)
                {
                    var stmt = s.Substring(start, i - start).Trim();
                    if (stmt.Length > 0) yield return stmt;
                    start = i + 1;
                }
            }
            var tail = s.Substring(start).Trim();
            if (tail.Length > 0) yield return tail;
        }

        public void Dispose()
        {
            foreach (var s in sources) s.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(WorkDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
