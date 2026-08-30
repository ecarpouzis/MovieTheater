using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Books;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Db;
using MovieTheater.Books.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// Fixture archives generated on the fly under the temp directory, plus a throwaway books.db seeded with
    /// items that point AT THEM. Nothing here reads the library share, the standalone site's cache, or the real
    /// v2 files — the whole point is that an archive claim is testable without a 141k-file library.
    /// </summary>
    public sealed class ArchiveFixture : IDisposable
    {
        public readonly string WorkDir;
        public readonly string CacheDir;
        public readonly string HotPath;

        /// <summary>Three pages, deliberately zipped OUT of name order so a test can prove the reader sorts.</summary>
        public readonly string CbzPath;
        /// <summary>The same three pages, but the file is named .cbr — the misnamed-container case.</summary>
        public readonly string MisnamedCbrPath;
        /// <summary>A CBZ whose page 0 is a 2:1 landscape wraparound — the spread-crop case.</summary>
        public readonly string SpreadCbzPath;
        public readonly string EpubPath;
        /// <summary>An EPUB named .zip — 87% of the library's 3,936 .zip "books" are exactly this.</summary>
        public readonly string EpubAsZipPath;
        /// <summary>A comic named .zip — same extension, and it must NOT reach the EPUB reader.</summary>
        public readonly string ComicAsZipPath;
        /// <summary>A row in the catalog whose file is NOT on disk — the recorded-error case.</summary>
        public readonly string MissingPath;

        public ArchiveFixture()
        {
            WorkDir = Path.Combine(Path.GetTempPath(), "books-archive-tests", Guid.NewGuid().ToString("N"));
            CacheDir = Path.Combine(WorkDir, "cache");
            Directory.CreateDirectory(WorkDir);
            Directory.CreateDirectory(CacheDir);

            CbzPath = Path.Combine(WorkDir, "three-pages.cbz");
            WriteCbz(CbzPath, [("02_second.png", 300, 450), ("01_first.png", 320, 480), ("03_third.png", 310, 460)], withComicInfo: true);

            MisnamedCbrPath = Path.Combine(WorkDir, "actually-a-zip.cbr");
            File.Copy(CbzPath, MisnamedCbrPath);

            SpreadCbzPath = Path.Combine(WorkDir, "wraparound.cbz");
            WriteCbz(SpreadCbzPath, [("01_cover.png", 1200, 600), ("02_page.png", 600, 900)], withComicInfo: false);

            EpubPath = Path.Combine(WorkDir, "novel.epub");
            WriteEpub(EpubPath);

            EpubAsZipPath = Path.Combine(WorkDir, "novel.zip");
            File.Copy(EpubPath, EpubAsZipPath);

            ComicAsZipPath = Path.Combine(WorkDir, "comic.zip");
            File.Copy(CbzPath, ComicAsZipPath);

            MissingPath = Path.Combine(WorkDir, "not-on-disk.cbz");

            HotPath = Path.Combine(WorkDir, "books.db");
            using var db = NewDb();
            db.Database.Migrate();
            Seed(db);
        }

        public BooksDb NewDb() => new(BooksDbOptions.Hot(HotPath));

        public BooksOptions Options() => new()
        {
            DbPath = HotPath,
            CacheDir = CacheDir,
            ArchiveCacheGb = 0,
            EnableCacheWarmer = false,
            PublicBaseUrl = "http://localhost:2204",
            MediaTokenSecret = "test-media-secret",
        };

        public IReadOnlyList<IArchiveReader> Readers()
        {
            var sevenZip = new SevenZipCliExtractor(Options(), NullLogger<SevenZipCliExtractor>.Instance);
            var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
            return
            [
                new CbzArchiveReader(sevenZip),
                new CbrArchiveReader(sevenZip),
                new PdfArchiveReader(),
                new EpubArchiveReader(cache),
                new MobiArchiveReader(),
            ];
        }

        public ThumbnailService Thumbnails() =>
            new(Readers(), Options(), NullLogger<ThumbnailService>.Instance);

        // Item ids the seeded catalog uses.
        public const int CbzItemId = 1;
        public const int SpreadItemId = 2;
        public const int EpubItemId = 3;
        public const int MissingItemId = 4;

        private void Seed(BooksDb db)
        {
            db.LibraryRoots.Add(new LibraryRoot { Id = 1, Path = WorkDir, Kind = ItemKind.Comic, Enabled = true });
            db.Folders.Add(new Folder { Id = 1, RootId = 1, Kind = ItemKind.Comic, Path = WorkDir, Name = "fixtures", NormalizedName = "fixtures", Depth = 0 });
            db.Items.AddRange(
                Item(CbzItemId, CbzPath, ".cbz", ContainerFormat.Cbz, "Three Pages"),
                Item(SpreadItemId, SpreadCbzPath, ".cbz", ContainerFormat.Cbz, "Wraparound"),
                Item(EpubItemId, EpubPath, ".epub", ContainerFormat.Epub, "Novel"),
                Item(MissingItemId, MissingPath, ".cbz", ContainerFormat.Cbz, "Not On Disk"));
            db.SaveChanges();
        }

        private static Item Item(int id, string path, string ext, ContainerFormat format, string title) => new()
        {
            Id = id,
            RootId = 1,
            FolderId = 1,
            TopFolderId = 1,
            Kind = ItemKind.Comic,
            Path = path,
            FileName = Path.GetFileName(path),
            Extension = ext,
            ContainerFormat = format,
            FileSize = 1000 + id,
            FileModifiedAt = new DateTime(2026, 1, 1, 0, 0, id, DateTimeKind.Utc),
            IndexedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            ResolvedTitle = title,
        };

        // ── fixture writers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>A zip of solid-colour PNGs. The colour is derived from the entry name so a test can tell
        /// WHICH page it got back, which is the only way to assert an ordering.</summary>
        public static void WriteCbz(string path, (string Name, int Width, int Height)[] pages, bool withComicInfo)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, w, h) in pages)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(PngBytes(w, h, ColorFor(name)));
            }
            if (!withComicInfo) return;
            var meta = zip.CreateEntry("ComicInfo.xml");
            using var metaStream = meta.Open();
            metaStream.Write(Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\"?><ComicInfo><Series>Fixture Series</Series><Number>7</Number>" +
                "<Title>A Fixture Issue</Title><Year>1988</Year><Month>3</Month><Day>4</Day>" +
                "<Writer>A Writer</Writer><Genre>Test</Genre><BlackAndWhite>Yes</BlackAndWhite></ComicInfo>"));
        }

        public static Rgb24 ColorFor(string name) =>
            new((byte)(name.Length * 7 % 256), (byte)(name[0] % 256), (byte)(name[^5] % 256));

        public static byte[] PngBytes(int width, int height, Rgb24 color)
        {
            using var image = new Image<Rgb24>(width, height, color);
            // A single flat colour would fail the blank-swatch test, so a corner block gives it real spread —
            // the fixtures have to look like covers to the analyzer or every cover assertion is vacuous.
            for (var y = 0; y < height / 2; y++)
                for (var x = 0; x < width / 2; x++)
                    image[x, y] = new Rgb24((byte)(color.R ^ 0xFF), (byte)(color.G ^ 0x7F), (byte)(color.B ^ 0x3F));
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        /// <summary>A minimal but real EPUB: container.xml → an OPF with a manifest, a two-document spine, an
        /// EPUB 3 nav with two TOC entries, and a cover image.</summary>
        public static void WriteEpub(string path)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            void Text(string name, string content)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }

            Text("mimetype", "application/epub+zip");
            Text("META-INF/container.xml",
                "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
                "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");

            Text("OEBPS/content.opf",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">" +
                "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                "<dc:identifier id=\"bookid\">urn:uuid:fixture</dc:identifier>" +
                "<dc:title>A Fixture Novel</dc:title><dc:creator>A Novelist</dc:creator>" +
                "<dc:language>en</dc:language><dc:publisher>A Press</dc:publisher>" +
                "<meta name=\"cover\" content=\"cover-image\"/></metadata>" +
                "<manifest>" +
                "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>" +
                "<item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                "<item id=\"ch2\" href=\"ch2.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                "<item id=\"cover-image\" href=\"cover.png\" media-type=\"image/png\" properties=\"cover-image\"/>" +
                "<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>" +
                "</manifest>" +
                "<spine><itemref idref=\"ch1\"/><itemref idref=\"ch2\"/></spine></package>");

            Text("OEBPS/nav.xhtml",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">" +
                "<head><title>Contents</title></head><body><nav epub:type=\"toc\" id=\"toc\"><ol>" +
                "<li><a href=\"ch1.xhtml\">Chapter One</a></li><li><a href=\"ch2.xhtml\">Chapter Two</a></li>" +
                "</ol></nav></body></html>");

            Text("OEBPS/ch1.xhtml",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Chapter One</title>" +
                "<link rel=\"stylesheet\" href=\"style.css\"/></head><body><h1>Chapter One</h1>" +
                "<p>It was a fixture, and it was good.</p><img src=\"cover.png\" alt=\"\"/></body></html>");

            Text("OEBPS/ch2.xhtml",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Chapter Two</title></head>" +
                "<body><h1>Chapter Two</h1><p>And then it ended.</p></body></html>");

            Text("OEBPS/style.css", "body { margin: 1em; }");

            using (var s = zip.CreateEntry("OEBPS/cover.png").Open())
                s.Write(PngBytes(600, 900, new Rgb24(20, 90, 160)));
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(WorkDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Slice 2's reader contract: the sniffer routes by container, a CBZ's pages come back in one fixed order,
    /// an EPUB yields a real spine and TOC, the spread rule fires exactly at its threshold, and the thumbnail job
    /// is chunked, resumable and does not fall over on a file that is not there.
    /// </summary>
    public class ArchiveTests : IClassFixture<ArchiveFixture>
    {
        private readonly ArchiveFixture fixture;
        public ArchiveTests(ArchiveFixture fixture) => this.fixture = fixture;

        // ── the sniffer ───────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void The_sniffer_routes_by_container_not_by_extension()
        {
            // A ZIP saved as .cbr is routed to the ZIP reader — the ~35 % of "unreadable" comics that were only
            // ever misnamed.
            Assert.Equal(".cbz", ArchiveFormatSniffer.ResolveReaderExtension(fixture.MisnamedCbrPath, ".cbr"));
            Assert.Equal(ArchiveFormatSniffer.Container.Zip, ArchiveFormatSniffer.Detect(fixture.MisnamedCbrPath));

            // An EPUB is a ZIP, and is deliberately NOT re-routed: it needs the EPUB reader.
            Assert.Equal(".epub", ArchiveFormatSniffer.ResolveReaderExtension(fixture.EpubPath, ".epub"));
            Assert.Equal(".pdf", ArchiveFormatSniffer.ResolveReaderExtension(fixture.CbzPath, ".pdf"));

            // A file that cannot be opened means "trust the extension", not an exception.
            Assert.Equal(".cbz", ArchiveFormatSniffer.ResolveReaderExtension(fixture.MissingPath, ".cbz"));
            Assert.Equal(ArchiveFormatSniffer.Container.Unknown, ArchiveFormatSniffer.Detect(fixture.MissingPath));
        }

        /// <summary>
        /// A bare <c>.zip</c> says nothing about what is inside, and the library holds 3,936 of them with no
        /// cover because no reader claimed the extension. Routing them by container alone is not enough either:
        /// 87% are EPUBs, and the comic reader would hand back whichever image sorted first instead of the
        /// book's cover. So the ZIP is split by its OCF signature — and the comic case must survive it.
        /// </summary>
        [Fact]
        public void A_zip_is_routed_by_what_is_inside_it()
        {
            Assert.Equal(".epub", ArchiveFormatSniffer.ResolveReaderExtension(fixture.EpubAsZipPath, ".zip"));
            Assert.Equal(".cbz", ArchiveFormatSniffer.ResolveReaderExtension(fixture.ComicAsZipPath, ".zip"));

            Assert.True(ArchiveFormatSniffer.IsEpubZip(fixture.EpubAsZipPath));
            Assert.False(ArchiveFormatSniffer.IsEpubZip(fixture.ComicAsZipPath));
            // Not a zip at all: no opinion rather than an exception.
            Assert.False(ArchiveFormatSniffer.IsEpubZip(fixture.MissingPath));

            // The .epub extension still short-circuits — an EPUB correctly named is never re-sniffed.
            Assert.Equal(".epub", ArchiveFormatSniffer.ResolveReaderExtension(fixture.EpubPath, ".epub"));
        }

        /// <summary>A <c>.rar</c> reaches the reader that can open RAR, instead of no reader at all.</summary>
        [Fact]
        public void A_rar_reaches_a_reader()
        {
            // The fixture's "rar" is a ZIP by content; the point is that .rar is now SNIFFED rather than
            // dropped, so the container decides and the file is no longer unreadable by default.
            Assert.Equal(".cbz", ArchiveFormatSniffer.ResolveReaderExtension(fixture.MisnamedCbrPath, ".rar"));
        }

        /// <summary>
        /// The container probe answers about the BYTES, not the parser — the distinction that decides whether an
        /// item gets the broken flag. Intact ⇒ true, truncated ⇒ false, not-a-sniffable-container ⇒ no opinion.
        /// </summary>
        [Fact]
        public void Container_openability_is_the_bytes_question_not_the_parsers()
        {
            Assert.True(ArchiveFormatSniffer.CanOpenContainer(fixture.CbzPath));
            // An EPUB is a ZIP; the probe reads it as one without asking VersOne whether it likes the package.
            Assert.True(ArchiveFormatSniffer.CanOpenContainer(fixture.EpubPath));

            // Truncated: the local header still says PK, but the central directory is gone.
            var truncated = Path.Combine(fixture.WorkDir, "truncated.cbz");
            var bytes = File.ReadAllBytes(fixture.CbzPath);
            File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
            Assert.False(ArchiveFormatSniffer.CanOpenContainer(truncated));

            // Not a container we can sniff, and a file that is not there at all: no opinion either way.
            var text = Path.Combine(fixture.WorkDir, "notes.txt");
            File.WriteAllText(text, "not an archive");
            Assert.Null(ArchiveFormatSniffer.CanOpenContainer(text));
            Assert.Null(ArchiveFormatSniffer.CanOpenContainer(fixture.MissingPath));
        }

        [Fact]
        public void Reader_selection_follows_the_sniffed_container()
        {
            var readers = fixture.Readers();
            var chosen = readers.ForFile(fixture.MisnamedCbrPath, ".cbr");
            Assert.IsType<CbzArchiveReader>(chosen);
            Assert.IsType<EpubArchiveReader>(readers.ForFile(fixture.EpubPath, ".epub"));
        }

        // ── CBZ ───────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Cbz_pages_are_ordered_by_name_and_the_cover_is_page_zero()
        {
            var reader = new CbzArchiveReader(new SevenZipCliExtractor(fixture.Options(), NullLogger<SevenZipCliExtractor>.Instance));

            // Three images plus a ComicInfo.xml — the metadata entry is not a page.
            Assert.Equal(3, await reader.GetPageCountAsync(fixture.CbzPath));

            var names = await reader.GetPageNamesAsync(fixture.CbzPath);
            Assert.Equal(["01_first.png", "02_second.png", "03_third.png"], names);

            // Page 0 is the FIRST name in that order, not the first entry the zip happened to store.
            var page0 = await Bytes(reader.GetPageAsync(fixture.CbzPath, 0));
            using (var image = Image.Load<Rgb24>(page0))
            {
                Assert.Equal(320, image.Width);
                Assert.Equal(480, image.Height);
            }

            var cover = await Bytes(reader.GetCoverAsync(fixture.CbzPath));
            Assert.Equal(page0, cover);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.GetPageAsync(fixture.CbzPath, 3));
        }

        [Fact]
        public async Task Cbz_reads_its_embedded_comicinfo()
        {
            var reader = new CbzArchiveReader(new SevenZipCliExtractor(fixture.Options(), NullLogger<SevenZipCliExtractor>.Instance));
            var meta = await reader.ReadMetadataAsync(fixture.CbzPath);
            Assert.NotNull(meta);
            Assert.Equal("Fixture Series", meta!.Series);
            Assert.Equal("7", meta.SeriesIndex);
            Assert.Equal("A Fixture Issue", meta.IssueTitle);
            Assert.True(meta.BlackAndWhite);
            // Year/Month/Day fold into one partial-date string, zero-padded.
            Assert.Equal("1988-03-04", meta.PublicationDate);
        }

        // ── EPUB ──────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Epub_spine_and_toc_resolve_to_reading_order_positions()
        {
            var epub = new EpubReaderService(NullLogger<EpubReaderService>.Instance);

            var info = await epub.GetSpineInfoAsync(fixture.EpubPath);
            Assert.Equal(2, info.Items.Count);
            Assert.Equal(0, info.Items[0].Index);
            Assert.Contains("ch1", info.Items[0].Href);
            Assert.Contains("ch2", info.Items[1].Href);
            // A reflowable novel: no rendition:layout and no pre-paginated spine items.
            Assert.False(info.FixedLayout);
            Assert.Equal("ltr", info.Direction);

            var toc = await epub.GetTocAsync(fixture.EpubPath);
            Assert.Equal(2, toc.Count);
            Assert.Equal("Chapter One", toc[0].Label);
            // The whole point of the TOC shape: each entry names the SPINE INDEX it jumps to.
            Assert.Equal(0, toc[0].SpineIndex);
            Assert.Equal(1, toc[1].SpineIndex);

            var html = await epub.GetChapterHtmlAsync(fixture.EpubPath, 0);
            Assert.Contains("Chapter One", html);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => epub.GetChapterHtmlAsync(fixture.EpubPath, 5));

            // A resource resolves relative to the chapter that referenced it, and carries its real MIME type.
            var css = await epub.GetResourceAsync(fixture.EpubPath, "style.css", "OEBPS/ch1.xhtml");
            Assert.NotNull(css);
            Assert.Contains("text/css", css!.MimeType);

            var image = await epub.GetResourceAsync(fixture.EpubPath, "cover.png", "OEBPS/ch1.xhtml");
            Assert.NotNull(image);
            Assert.True(image!.Content.Length > 0);
        }

        [Fact]
        public void Epub_href_normalization_cannot_escape_the_container()
        {
            // The .. segments are RESOLVED, not stripped: an href can never climb above the container root, so a
            // traversal attempt lands on a name that simply is not in the package.
            Assert.Equal("cover.png", EpubReaderService.NormalizeHref("OEBPS/../cover.png"));
            Assert.Equal("etc/passwd", EpubReaderService.NormalizeHref("../../../etc/passwd"));
            Assert.Equal("a/b.png", EpubReaderService.NormalizeHref("/a/./b.png?v=2#frag"));
            Assert.Equal("OEBPS/img/x.png", EpubReaderService.ResolveHref("OEBPS/ch1.xhtml", "img/x.png"));
        }

        [Fact]
        public async Task Epub_cover_is_the_declared_cover_not_the_first_spine_image()
        {
            var reader = new EpubArchiveReader(new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 }));
            var cover = await Bytes(reader.GetCoverAsync(fixture.EpubPath));
            using var image = Image.Load<Rgb24>(cover);
            Assert.Equal(600, image.Width);
            Assert.Equal(900, image.Height);
        }

        // ── the spread rule ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Spread_crop_fires_only_above_the_threshold_and_keeps_the_right_half()
        {
            Assert.Equal(1.15, CoverImageAnalyzer.SpreadAspectThreshold);

            // 2:1 — a back|front wraparound. The RIGHT half survives: that is the front cover.
            using (var wide = new Image<Rgb24>(1200, 600, new Rgb24(10, 10, 10)))
            {
                for (var y = 0; y < 600; y++)
                    for (var x = 600; x < 1200; x++)
                        wide[x, y] = new Rgb24(200, 30, 30);
                Assert.True(CoverImageAnalyzer.TryCropSpread(wide));
                Assert.Equal(600, wide.Width);
                Assert.Equal(600, wide.Height);
                Assert.Equal(new Rgb24(200, 30, 30), wide[10, 10]);
            }

            // A normal portrait cover is untouched.
            using (var portrait = new Image<Rgb24>(660, 1000, new Rgb24(1, 2, 3)))
            {
                Assert.False(CoverImageAnalyzer.TryCropSpread(portrait));
                Assert.Equal(660, portrait.Width);
            }

            // AT the threshold is not over it — the comparison is strictly greater.
            using (var atThreshold = new Image<Rgb24>(1150, 1000, new Rgb24(1, 2, 3)))
            {
                Assert.False(CoverImageAnalyzer.TryCropSpread(atThreshold));
                Assert.Equal(1150, atThreshold.Width);
            }

            using (var justOver = new Image<Rgb24>(1160, 1000, new Rgb24(1, 2, 3)))
            {
                Assert.True(CoverImageAnalyzer.TryCropSpread(justOver));
                Assert.Equal(580, justOver.Width);
            }
        }

        [Fact]
        public void The_cover_analyzer_rejects_a_blank_swatch_and_a_tiny_image()
        {
            Assert.False(CoverImageAnalyzer.IsUsableCover(Flat(800, 1200)));
            Assert.False(CoverImageAnalyzer.IsUsableCover(ArchiveFixture.PngBytes(46, 60, new Rgb24(30, 60, 90))));
            Assert.True(CoverImageAnalyzer.IsUsableCover(ArchiveFixture.PngBytes(600, 900, new Rgb24(30, 60, 90))));
            Assert.False(CoverImageAnalyzer.IsUsableCover(null));
            Assert.False(CoverImageAnalyzer.IsUsableCover([]));

            static byte[] Flat(int w, int h)
            {
                using var image = new Image<Rgb24>(w, h, new Rgb24(120, 120, 120));
                using var ms = new MemoryStream();
                image.SaveAsPng(ms);
                return ms.ToArray();
            }
        }

        // ── the thumbnail service ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_thumbnail_is_a_webp_named_for_the_item_id_and_records_the_cropped_cover_size()
        {
            using var scratch = new ArchiveFixture();
            var thumbnails = scratch.Thumbnails();

            var result = await thumbnails.TryGetOrGenerateAsync(ArchiveFixture.SpreadItemId, scratch.SpreadCbzPath, ".cbz");
            Assert.True(result.Success, result.Error);
            Assert.Equal(Path.Combine(scratch.CacheDir, "2.webp"), result.Path);
            Assert.True(File.Exists(result.Path!));

            // The recorded dimensions are the CROPPED cover (1200x600 wraparound → 600x600), because the client
            // lays out against the picture it will actually be shown.
            Assert.Equal(600, result.Width);
            Assert.Equal(600, result.Height);

            using var written = Image.Load(result.Path!);
            Assert.True(written.Width <= ThumbnailService.TargetWidth);
            Assert.True(written.Height <= ThumbnailService.TargetHeight);

            // A second call is a cache hit: no regeneration, and therefore no measurements.
            var second = await thumbnails.TryGetOrGenerateAsync(ArchiveFixture.SpreadItemId, scratch.SpreadCbzPath, ".cbz");
            Assert.True(second.Success);
            Assert.Null(second.Width);
        }

        [Fact]
        public async Task A_missing_file_is_a_recorded_failure_not_a_crash()
        {
            using var scratch = new ArchiveFixture();
            var result = await scratch.Thumbnails().TryGetOrGenerateAsync(ArchiveFixture.MissingItemId, scratch.MissingPath, ".cbz");
            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            // The ARCHIVE was the problem, so this is the case that may set the broken flag.
            Assert.True(result.ArchiveUnreadable);
        }

        // ── the thumbnail job ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_thumbnail_job_is_chunked_resumable_and_does_no_double_work()
        {
            using var scratch = new ArchiveFixture();
            var job = new ThumbnailJob(scratch.Thumbnails(), NullLogger<ThumbnailJob>.Instance);

            // Batch one: two of the four seeded items, and the cursor stops on the second.
            ThumbnailBatchResult first;
            using (var db = scratch.NewDb()) first = await job.RunBatchAsync(db, 2);
            Assert.Equal(2, first.Processed);
            Assert.Equal(2, first.Remaining);
            Assert.Equal(2, first.NextCursor);
            Assert.False(first.Done);

            // A NEW context, as a restarted process would have: the cursor came off disk, not out of memory.
            ThumbnailBatchResult second;
            using (var db = scratch.NewDb()) second = await job.RunBatchAsync(db, 2);
            Assert.Equal(2, second.Processed);
            Assert.Equal(0, second.Remaining);
            Assert.Equal(ArchiveFixture.MissingItemId, second.NextCursor);
            // The missing file failed and was RECORDED; the run did not stop.
            Assert.Equal(1, second.Failed);

            // Past the end: nothing processed, and Done tells the driver to stop.
            using (var db = scratch.NewDb())
            {
                var third = await job.RunBatchAsync(db, 2);
                Assert.Equal(0, third.Processed);
                Assert.True(third.Done);
            }

            using (var db = scratch.NewDb())
            {
                var status = await job.StatusAsync(db);
                Assert.Equal(4, status.Processed);
                Assert.Equal(1, status.Failed);
                Assert.Equal(0, status.Remaining);

                // ItemState is the only table the job wrote, and it carries both outcomes.
                var missing = await db.ItemStates.AsNoTracking().FirstAsync(s => s.ItemId == ArchiveFixture.MissingItemId);
                Assert.NotNull(missing.ThumbnailError);
                Assert.NotNull(missing.ThumbnailCheckedAt);
                Assert.True(missing.IsBroken);

                var ok = await db.ItemStates.AsNoTracking().FirstAsync(s => s.ItemId == ArchiveFixture.CbzItemId);
                Assert.Null(ok.ThumbnailError);
                Assert.Equal(320, ok.CoverWidth);
                Assert.Equal(480, ok.CoverHeight);
                Assert.Equal(ThumbnailJob.FileSignature(1000 + ArchiveFixture.CbzItemId,
                    new DateTime(2026, 1, 1, 0, 0, ArchiveFixture.CbzItemId, DateTimeKind.Utc)), ok.CoverDimsComputedFor);
            }

            // Re-running from the start SKIPS everything already on disk: the work is idempotent, never doubled.
            using (var db = scratch.NewDb())
            {
                await job.ResetAsync(db);
                var replay = await job.RunBatchAsync(db, 10);
                Assert.Equal(4, replay.Processed);
                Assert.Equal(0, replay.Generated);
                Assert.Equal(3, replay.Skipped);   // the three that generated a file
                Assert.Equal(1, replay.Failed);    // the missing one is re-checked, never "done"
            }
        }

        [Fact]
        public async Task A_killed_batch_repeats_at_most_that_batch()
        {
            using var scratch = new ArchiveFixture();
            var job = new ThumbnailJob(scratch.Thumbnails(), NullLogger<ThumbnailJob>.Instance);

            // A cancellation mid-batch is what a killed process looks like: the batch's writes and its cursor
            // commit together, so neither lands and the batch is simply redone.
            using (var db = scratch.NewDb())
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.RunBatchAsync(db, 4, cts.Token));
            }

            using (var db = scratch.NewDb())
            {
                var status = await job.StatusAsync(db);
                Assert.Equal(0, status.Cursor);
                Assert.Equal(0, status.Processed);
            }

            using (var db = scratch.NewDb())
            {
                var result = await job.RunBatchAsync(db, 10);
                Assert.Equal(4, result.Processed);
            }
        }

        // ── page scaling ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Page_scaling_shrinks_to_the_budget_and_doubles_it_for_a_landscape_spread()
        {
            var scaling = new ImageScalingService(fixture.Options());

            var portrait = ArchiveFixture.PngBytes(1600, 2400, new Rgb24(20, 40, 60));
            using (var image = Image.Load(await Bytes(scaling.ScalePageAsync(new MemoryStream(portrait), 800))))
                Assert.Equal(800, image.Width);

            // A landscape page at the same index is a two-page spread across the same viewport, so it is allowed
            // twice the pixels — otherwise every spread arrives at half the resolution of the pages around it.
            var landscape = ArchiveFixture.PngBytes(2400, 1200, new Rgb24(20, 40, 60));
            using (var image = Image.Load(await Bytes(scaling.ScalePageAsync(new MemoryStream(landscape), 800))))
                Assert.Equal(1600, image.Width);

            // Under budget: never upscaled.
            var small = ArchiveFixture.PngBytes(400, 600, new Rgb24(20, 40, 60));
            using (var image = Image.Load(await Bytes(scaling.ScalePageAsync(new MemoryStream(small), 800))))
                Assert.Equal(400, image.Width);
        }

        private static async Task<byte[]> Bytes(Task<Stream> task)
        {
            await using var stream = await task;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
    }
}
