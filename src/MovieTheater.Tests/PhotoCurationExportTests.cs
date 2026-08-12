using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Photos;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The durability lane (docs/photos-plan.md §2.11): <c>photos-export</c> and
    /// <c>photos-import --dry-run</c>, and the acceptance §5 Phase 2 states — "an export round-trips
    /// through import dry-run losslessly".
    ///
    /// <para>The scenario that matters is the one nobody wants to rehearse in anger: the database is
    /// gone, the collection is re-ingested from disk, folders have been reorganized in the meantime, and
    /// years of tags, dates, albums and master picks have to land back on the RIGHT photos. That is what
    /// the second half of this file does — export from one database, rebuild a second one by walking the
    /// same files (with one of them moved), and prove the curation reattaches by CONTENT.</para>
    /// </summary>
    public class PhotoCurationExportTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        private const string PersonName = "Test Subject A";
        private const string SecondPersonName = "Test Subject B";

        private void BuildTree()
        {
            fixture.WriteJpeg("Trip/t1.jpg", 640, 480, seed: 51, exifDateTimeOriginal: "2015:08:01 10:00:00");
            fixture.WriteJpeg("Trip/t2.jpg", 640, 480, seed: 52, exifDateTimeOriginal: "2015:08:01 11:00:00");
            fixture.WriteJpeg("Scans/s1.jpg", 640, 480, seed: 53);
            fixture.WriteJpeg("Screenshots/shot.jpg", 640, 480, seed: 54, exifDateTimeOriginal: "2021:01:01 10:00:00");
            // Byte-identical twins: the exact-dupe case, and the ambiguity the importer must refuse to
            // guess at when neither path survives.
            fixture.WriteJpeg("Trip/dupe-a.jpg", 320, 240, seed: 55);
            fixture.WriteJpeg("Backup/dupe-b.jpg", 320, 240, seed: 55);
        }

        private async Task IngestAsync(Func<MovieDb>? into = null)
        {
            var pipeline = into == null
                ? fixture.Pipeline(fixture.Options(batchSize: 50))
                : new PhotoIngestPipeline(into, fixture.Options(batchSize: 50), _ => { });
            await pipeline.RunAsync(PhotoIngestPass.Walk, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Metadata, null, 0);
            await pipeline.RunAsync(PhotoIngestPass.Hash, null, 0);
        }

        /// <summary>Every kind of curation §2.11 calls irreplaceable, written by hand into the source
        /// database so the round trip has something real to carry.</summary>
        private async Task CurateAsync()
        {
            using var db = fixture.NewDb();
            var byPath = await db.PhotoAssets.ToDictionaryAsync(a => a.Path, a => a);

            byPath["Screenshots/shot.jpg"].Hidden = true;

            // A hand-set date on an undated scan, and a circa range beside it (§2.7).
            var scan = byPath["Scans/s1.jpg"];
            scan.TakenAt = new DateTime(1987, 6, 1, 12, 0, 0);
            scan.TakenAtSource = TakenAtSource.Manual;
            scan.YearMin = 1986;
            scan.YearMax = 1988;
            scan.LocationLabel = "A Town, A State";
            scan.LocationSource = PhotoLocationSource.Manual;

            var person = new FamilyPerson { Name = PersonName, BirthYear = 1980, CreatedUtc = DateTime.UtcNow };
            var second = new FamilyPerson { Name = SecondPersonName, CreatedUtc = DateTime.UtcNow };
            db.FamilyPeople.AddRange(person, second);
            await db.SaveChangesAsync();

            db.PhotoPersonTags.Add(new PhotoPersonTag
            {
                PhotoAssetId = byPath["Trip/t1.jpg"].Id,
                FamilyPersonId = person.Id,
                Source = PhotoTagSource.Manual,
                BoxX = 0.1, BoxY = 0.2, BoxW = 0.3, BoxH = 0.4,
                CreatedUtc = DateTime.UtcNow,
            });
            db.PhotoPersonTags.Add(new PhotoPersonTag
            {
                PhotoAssetId = byPath["Trip/t2.jpg"].Id,
                FamilyPersonId = second.Id,
                Source = PhotoTagSource.Confirmed,
                ConfirmedUtc = DateTime.UtcNow,
                CreatedUtc = DateTime.UtcNow,
            });

            var album = new PhotoAlbum
            {
                Title = "The Trip",
                Slug = "the-trip",
                Description = "two photos",
                RangeStart = new DateTime(2015, 8, 1),
                CreatedUtc = DateTime.UtcNow,
            };
            db.PhotoAlbums.Add(album);
            await db.SaveChangesAsync();
            db.PhotoAlbumEntries.Add(new PhotoAlbumEntry { PhotoAlbumId = album.Id, PhotoAssetId = byPath["Trip/t2.jpg"].Id, SortOrder = 0, Caption = "the second one, first" });
            db.PhotoAlbumEntries.Add(new PhotoAlbumEntry { PhotoAlbumId = album.Id, PhotoAssetId = byPath["Trip/t1.jpg"].Id, SortOrder = 1 });
            album.CoverAssetId = byPath["Trip/t1.jpg"].Id;

            var group = new PhotoDupeGroup
            {
                Kind = PhotoDupeGroupKind.Exact,
                Status = PhotoDupeGroupStatus.Resolved,
                CreatedUtc = DateTime.UtcNow,
                ResolvedUtc = DateTime.UtcNow,
            };
            db.PhotoDupeGroups.Add(group);
            await db.SaveChangesAsync();
            db.PhotoDupeMembers.Add(new PhotoDupeMember { PhotoDupeGroupId = group.Id, PhotoAssetId = byPath["Trip/dupe-a.jpg"].Id, IsMaster = true });
            db.PhotoDupeMembers.Add(new PhotoDupeMember { PhotoDupeGroupId = group.Id, PhotoAssetId = byPath["Backup/dupe-b.jpg"].Id, IsMaster = false });

            db.PhotoGoogleItems.Add(new PhotoGoogleItem
            {
                TakeoutFileName = "t1.jpg",
                TakenAtUtc = new DateTime(2015, 8, 1, 14, 0, 0, DateTimeKind.Utc),
                SizeBytes = byPath["Trip/t1.jpg"].SizeBytes,
                MatchedPhotoAssetId = byPath["Trip/t1.jpg"].Id,
                Status = PhotoGoogleItemStatus.Matched,
                MatchMethod = "sha256",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
            });

            await db.SaveChangesAsync();
        }

        private async Task<PhotoExportManifest> ExportAsync(string dir, Func<MovieDb>? from = null, int maxSections = 0) =>
            await new PhotoCurationExporter(from ?? fixture.NewDb, _ => { }, pageSize: 2).RunAsync(dir, maxSections);

        private static async Task<PhotoCurationImportReport> ImportAsync(
            Func<MovieDb> into, string dir, bool apply, int batchSize = 250) =>
            await new PhotoCurationImporter(into, dir, apply, _ => { }, batchSize).RunAsync(null, 0);

        private static PhotoImportSectionReport Section(PhotoCurationImportReport report, string section) =>
            report.Sections.TryGetValue(section, out var s) ? s : new PhotoImportSectionReport();

        /// <summary>
        /// A REJECTION survives the round trip (§2.11 + §2.4's tombstone stance).
        ///
        /// <para>A refused suggestion is kept as a <see cref="PhotoTagSource.Rejected"/> row precisely so
        /// the next Immich sync does not propose the identical face again. The importer used to carry its
        /// own ranking table that scored Rejected at 0 — tied with Suggested — so the tombstone could
        /// never be applied over the suggestion it was written to bury: importing an export into a
        /// database where the sync had re-proposed the face left the "no" on the floor, silently, and the
        /// tag queue re-asked a question the family had already answered. The ranking is now
        /// <see cref="PhotoPersonTags.Rank"/>, in one place.</para>
        /// </summary>
        [Fact]
        public async Task A_rejection_tombstone_is_applied_over_a_re_proposed_suggestion()
        {
            BuildTree();
            await IngestAsync();

            var person = new FamilyPerson { Name = PersonName, CreatedUtc = DateTime.UtcNow };
            using (var db = fixture.NewDb())
            {
                db.FamilyPeople.Add(person);
                await db.SaveChangesAsync();
                var asset = await db.PhotoAssets.FirstAsync(a => a.Path == "Trip/t1.jpg");
                db.PhotoPersonTags.Add(new PhotoPersonTag
                {
                    PhotoAssetId = asset.Id,
                    FamilyPersonId = person.Id,
                    // The family looked at the machine's guess and said no.
                    Source = PhotoTagSource.Rejected,
                    CreatedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            var dir = fixture.ExportDir("rejection");
            await ExportAsync(dir);

            // The rebuilt database: the same photographs, and a sidecar sync that has re-proposed the
            // very face the export says was refused.
            var rebuilt = fixture.SecondaryDbFactory("rejection");
            await IngestAsync(rebuilt);
            using (var db = rebuilt())
            {
                var restored = new FamilyPerson { Name = PersonName, CreatedUtc = DateTime.UtcNow };
                db.FamilyPeople.Add(restored);
                await db.SaveChangesAsync();
                var asset = await db.PhotoAssets.FirstAsync(a => a.Path == "Trip/t1.jpg");
                db.PhotoPersonTags.Add(new PhotoPersonTag
                {
                    PhotoAssetId = asset.Id,
                    FamilyPersonId = restored.Id,
                    Source = PhotoTagSource.Suggested,
                    Confidence = 0.9,
                    CreatedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            var report = await ImportAsync(rebuilt, dir, apply: true);
            Assert.Equal(1, Section(report, PhotoCurationExportFormat.PersonTagsFile).Updated);

            using (var db = rebuilt())
            {
                var tag = await db.PhotoPersonTags.SingleAsync();
                Assert.Equal(PhotoTagSource.Rejected, tag.Source);
            }
        }

        // ── Export ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task An_export_writes_every_section_and_a_manifest_that_says_so()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            var dir = fixture.ExportDir();
            var manifest = await ExportAsync(dir);

            Assert.True(manifest.Complete);
            foreach (var section in PhotoCurationExportFormat.Sections)
                Assert.True(File.Exists(Path.Combine(dir, section)), $"{section} was not written");

            Assert.Equal(2, manifest.Counts[PhotoCurationExportFormat.PeopleFile]);
            Assert.Equal(2, manifest.Counts[PhotoCurationExportFormat.PersonTagsFile]);
            Assert.Equal(1, manifest.Counts[PhotoCurationExportFormat.AlbumsFile]);
            Assert.Equal(1, manifest.Counts[PhotoCurationExportFormat.DupeGroupsFile]);
            Assert.Equal(1, manifest.Counts[PhotoCurationExportFormat.GoogleItemsFile]);
            // Hidden + hand-dated + everything referenced by the rows above; the untouched photos are
            // deliberately absent — a re-ingest re-derives those from the files.
            Assert.Equal(6, manifest.Counts[PhotoCurationExportFormat.AssetsFile]);

            // Never beside the originals (§2.11), and no XMP sidecar anywhere near the collection.
            Assert.StartsWith(Path.GetFullPath(fixture.ReportDir), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.xmp", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task Every_reference_is_keyed_by_content_hash_and_relative_path()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            var dir = fixture.ExportDir();
            await ExportAsync(dir);

            var json = File.ReadAllText(Path.Combine(dir, PhotoCurationExportFormat.PersonTagsFile));
            var tags = System.Text.Json.JsonSerializer.Deserialize<List<PhotoPersonTagExport>>(json, PhotoCurationExportFormat.Json)!;
            Assert.All(tags, t =>
            {
                Assert.False(string.IsNullOrEmpty(t.Asset.Sha256));
                Assert.False(string.IsNullOrEmpty(t.Asset.Path));
                // Row ids are local to one database; the export exists for the case where it is gone.
                Assert.DoesNotContain("\"photoAssetId\"", json, StringComparison.OrdinalIgnoreCase);
            });
        }

        [Fact]
        public async Task A_killed_export_resumes_instead_of_starting_over()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            var dir = fixture.ExportDir("partial");
            var first = await ExportAsync(dir, maxSections: 2);
            Assert.False(first.Complete);
            Assert.Equal(2, first.Sections.Count);

            var finished = await ExportAsync(dir);
            Assert.True(finished.Complete);
            Assert.Equal(PhotoCurationExportFormat.Sections.Count, finished.Sections.Count);
        }

        // ── The round trip (§5 Phase 2 acceptance) ──────────────────────────────────────────────

        [Fact]
        public async Task An_export_imported_back_over_its_own_database_proposes_no_changes()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            var dir = fixture.ExportDir();
            await ExportAsync(dir);

            var report = await ImportAsync(fixture.NewDb, dir, apply: false);

            // Lossless: the export describes exactly the state it came from, so a dry run against that
            // same state has nothing to create and nothing to update. Anything else here means a field
            // is being dropped on the way out or mangled on the way back in.
            foreach (var section in PhotoCurationExportFormat.Sections)
            {
                var s = Section(report, section);
                Assert.Equal(0, s.Created);
                Assert.Equal(0, s.Updated);
                Assert.Equal(0, s.Unmatched);
                Assert.Equal(0, s.Ambiguous);
                Assert.Equal(s.Examined, s.Skipped);
            }
        }

        [Fact]
        public async Task A_dry_run_reports_the_delta_after_the_database_drifts_and_writes_nothing()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            var dir = fixture.ExportDir();
            await ExportAsync(dir);

            // Drift, of the kinds a restore actually meets.
            using (var db = fixture.NewDb())
            {
                var shot = await db.PhotoAssets.FirstAsync(a => a.Path == "Screenshots/shot.jpg");
                shot.Hidden = false;
                var tag = await db.PhotoPersonTags.FirstAsync();
                db.PhotoPersonTags.Remove(tag);
                var album = await db.PhotoAlbums.FirstAsync();
                album.Title = "Renamed by someone";
                var entry = await db.PhotoAlbumEntries.FirstAsync(e => e.PhotoAlbumId == album.Id);
                db.PhotoAlbumEntries.Remove(entry);
                await db.SaveChangesAsync();
            }

            var report = await ImportAsync(fixture.NewDb, dir, apply: false);

            var assets = Section(report, PhotoCurationExportFormat.AssetsFile);
            Assert.Equal(1, assets.Updated);
            Assert.Equal(1, assets.Extra["hide"]);

            Assert.Equal(1, Section(report, PhotoCurationExportFormat.PersonTagsFile).Created);

            var albums = Section(report, PhotoCurationExportFormat.AlbumsFile);
            Assert.Equal(1, albums.Updated);
            Assert.Equal(1, albums.Extra["entries-created"]);

            // And a dry run is a dry run: the drift is still there afterwards.
            using var after = fixture.NewDb();
            Assert.False(await after.PhotoAssets.Where(a => a.Path == "Screenshots/shot.jpg").Select(a => a.Hidden).FirstAsync());
            Assert.Equal("Renamed by someone", await after.PhotoAlbums.Select(a => a.Title).FirstAsync());
            Assert.Equal(1, await after.PhotoPersonTags.CountAsync());
        }

        [Fact]
        public async Task A_rebuilt_database_gets_its_curation_back_by_content_even_after_a_move()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            var dir = fixture.ExportDir();
            await ExportAsync(dir);

            // The disaster this lane exists for: the database is gone, the files were reorganized in
            // the meantime, and a fresh walk rebuilds the catalogue with entirely different row ids.
            fixture.Move("Trip/t1.jpg", "2015 Trip/Day 1/t1.jpg");
            var rebuilt = fixture.SecondaryDbFactory();
            await IngestAsync(rebuilt);

            using (var check = rebuilt())
            {
                Assert.Equal(0, await check.PhotoPersonTags.CountAsync());
                Assert.Equal(0, await check.PhotoAlbums.CountAsync());
            }

            var report = await ImportAsync(rebuilt, dir, apply: true);
            Assert.Equal(2, Section(report, PhotoCurationExportFormat.PeopleFile).Created);
            Assert.Equal(2, Section(report, PhotoCurationExportFormat.PersonTagsFile).Created);
            Assert.Equal(1, Section(report, PhotoCurationExportFormat.AlbumsFile).Created);

            using var db = rebuilt();
            // The tag landed on the photo that MOVED — matched by its hash, at a path that did not
            // exist when the export was taken. That is the whole §2.11 promise in one assertion.
            var moved = await db.PhotoAssets.FirstAsync(a => a.Path == "2015 Trip/Day 1/t1.jpg");
            var tag = await db.PhotoPersonTags.Include(t => t.FamilyPerson).FirstAsync(t => t.PhotoAssetId == moved.Id);
            Assert.Equal(PersonName, tag.FamilyPerson.Name);
            Assert.Equal(PhotoTagSource.Manual, tag.Source);
            Assert.Equal(0.3, tag.BoxW);

            var scan = await db.PhotoAssets.FirstAsync(a => a.Path == "Scans/s1.jpg");
            Assert.Equal(new DateTime(1987, 6, 1, 12, 0, 0), scan.TakenAt);
            Assert.Equal(TakenAtSource.Manual, scan.TakenAtSource);
            Assert.Equal(1986, scan.YearMin);
            Assert.Equal(PhotoLocationSource.Manual, scan.LocationSource);

            Assert.True(await db.PhotoAssets.Where(a => a.Path == "Screenshots/shot.jpg").Select(a => a.Hidden).FirstAsync());

            var album = await db.PhotoAlbums.Include(a => a.Entries).FirstAsync();
            Assert.Equal("The Trip", album.Title);
            Assert.Equal("the-trip", album.Slug);
            Assert.Equal(2, album.Entries.Count);
            Assert.Equal(moved.Id, album.CoverAssetId);
            Assert.Equal("the second one, first", album.Entries.OrderBy(e => e.SortOrder).First().Caption);

            var group = await db.PhotoDupeGroups.Include(g => g.Members).FirstAsync();
            Assert.Equal(PhotoDupeGroupStatus.Resolved, group.Status);
            Assert.Single(group.Members.Where(m => m.IsMaster));

            Assert.Equal(1, await db.PhotoGoogleItems.CountAsync(i => i.MatchedPhotoAssetId != null));
        }

        [Fact]
        public async Task Re_importing_over_a_restored_database_is_idempotent()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            var dir = fixture.ExportDir();
            await ExportAsync(dir);

            var rebuilt = fixture.SecondaryDbFactory();
            await IngestAsync(rebuilt);
            await ImportAsync(rebuilt, dir, apply: true);

            // Running a restore twice must not double a single row — the second pass is all skips.
            var second = await ImportAsync(rebuilt, dir, apply: true);
            foreach (var section in PhotoCurationExportFormat.Sections)
            {
                Assert.Equal(0, Section(second, section).Created);
                Assert.Equal(0, Section(second, section).Updated);
            }

            using var db = rebuilt();
            Assert.Equal(2, await db.PhotoPersonTags.CountAsync());
            Assert.Equal(1, await db.PhotoAlbums.CountAsync());
            Assert.Equal(2, await db.PhotoAlbumEntries.CountAsync());
            Assert.Equal(1, await db.PhotoDupeGroups.CountAsync());
        }

        [Fact]
        public async Task A_human_typed_date_is_never_overwritten_by_an_older_automatic_one()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            var dir = fixture.ExportDir();
            await ExportAsync(dir);

            // Someone dated the screenshot by hand AFTER the export was taken. A restore is not
            // evidence that the older machine-read value is better (§2.7: Manual outranks everything).
            using (var db = fixture.NewDb())
            {
                var shot = await db.PhotoAssets.FirstAsync(a => a.Path == "Screenshots/shot.jpg");
                shot.TakenAt = new DateTime(2021, 3, 3, 8, 0, 0);
                shot.TakenAtSource = TakenAtSource.Manual;
                await db.SaveChangesAsync();
            }

            var report = await ImportAsync(fixture.NewDb, dir, apply: true);
            Assert.Equal(1, Section(report, PhotoCurationExportFormat.AssetsFile).Extra["kept-local-manual-date"]);

            using var after = fixture.NewDb();
            var kept = await after.PhotoAssets.FirstAsync(a => a.Path == "Screenshots/shot.jpg");
            Assert.Equal(new DateTime(2021, 3, 3, 8, 0, 0), kept.TakenAt);
            Assert.Equal(TakenAtSource.Manual, kept.TakenAtSource);
        }

        [Fact]
        public async Task Two_local_copies_of_the_same_bytes_at_unknown_paths_are_reported_not_guessed()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            using (var db = fixture.NewDb())
            {
                // Curation on one of the byte-identical twins.
                var a = await db.PhotoAssets.FirstAsync(x => x.Path == "Trip/dupe-a.jpg");
                a.Hidden = true;
                await db.SaveChangesAsync();
            }

            var dir = fixture.ExportDir("ambiguous");
            await ExportAsync(dir);

            // Both twins move, so the hash matches two rows and neither path matches. §2.5's stance on
            // ambiguity: report it, never guess — a wrong guess here attaches years of tags to the
            // wrong copy of a photograph.
            fixture.Move("Trip/dupe-a.jpg", "Sorted/one.jpg");
            fixture.Move("Backup/dupe-b.jpg", "Sorted/two.jpg");
            var rebuilt = fixture.SecondaryDbFactory("ambiguous");
            await IngestAsync(rebuilt);

            var report = await ImportAsync(rebuilt, dir, apply: true);
            Assert.True(Section(report, PhotoCurationExportFormat.AssetsFile).Ambiguous > 0);

            using var db2 = rebuilt();
            Assert.Equal(0, await db2.PhotoAssets.CountAsync(a => a.Path.StartsWith("Sorted/") && a.Hidden));
        }

        [Fact]
        public async Task An_import_is_chunked_and_resumable()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            var dir = fixture.ExportDir();
            await ExportAsync(dir);

            var rebuilt = fixture.SecondaryDbFactory();
            await IngestAsync(rebuilt);

            // The driver loop lives in the caller; one batch at a time, in a FRESH importer each
            // round, resumed from nothing but the cursor the previous one printed.
            var cursor = (string?)null;
            var total = new PhotoCurationImportReport();
            for (var i = 0; i < 200; i++)
            {
                var log = new List<string>();
                var stepper = new PhotoCurationImporter(rebuilt, dir, apply: true, log.Add, batchSize: 1);
                var report = await stepper.RunAsync(cursor, 1);
                total.Merge(report);

                var line = log.Last();
                cursor = line.Split("nextCursor: \"")[1].Split('"')[0];
                if (line.Contains("remaining: 0")) break;
            }

            Assert.Equal(2, Section(total, PhotoCurationExportFormat.PersonTagsFile).Created);
            using var db = rebuilt();
            Assert.Equal(2, await db.PhotoPersonTags.CountAsync());
            Assert.Equal(2, await db.PhotoAlbumEntries.CountAsync());
        }

        [Fact]
        public async Task A_dry_run_over_an_empty_database_creates_nothing_at_all()
        {
            BuildTree();
            await IngestAsync();
            await CurateAsync();

            var dir = fixture.ExportDir();
            await ExportAsync(dir);

            var empty = fixture.SecondaryDbFactory("untouched");
            var report = await ImportAsync(empty, dir, apply: false);

            // Nothing to attach to yet: the assets have to be re-ingested from disk first, and the
            // report says so rather than inventing rows for photos this database has never seen.
            Assert.True(Section(report, PhotoCurationExportFormat.AssetsFile).Unmatched > 0);
            using var db = empty();
            Assert.Equal(0, await db.FamilyPeople.CountAsync());
            Assert.Equal(0, await db.PhotoAlbums.CountAsync());
        }
    }
}
