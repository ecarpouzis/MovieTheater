using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>
    /// Writes a curation export (docs/photos-plan.md §2.11) — people, tags, hand-set dates, albums,
    /// dupe resolutions, curation flags and the Google mesh state, as versioned JSON keyed by content
    /// hash + relative path.
    ///
    /// <para><b>Bulk-job shape.</b> One section per unit of work, each streamed to its own file in
    /// bounded PAGES so a hundred thousand rows never land in memory at once, with a progress line per
    /// page. A killed run resumes: a section whose file already exists is skipped, and the manifest is
    /// written LAST so "complete" cannot be claimed by a half-finished directory. Re-running a section
    /// is deterministic, so resuming can never half-apply anything — an export writes only to its own
    /// new directory and touches no database row and no file on the NAS.</para>
    /// </summary>
    public sealed class PhotoCurationExporter
    {
        private readonly Func<MovieDb> dbFactory;
        private readonly Action<string> log;
        private readonly int pageSize;

        public PhotoCurationExporter(Func<MovieDb> dbFactory, Action<string> log, int pageSize = 2000)
        {
            this.dbFactory = dbFactory;
            this.log = log;
            this.pageSize = Math.Max(1, pageSize);
        }

        /// <summary>
        /// Writes (or resumes) an export into <paramref name="directory"/>.
        /// <paramref name="maxSections"/> bounds one invocation; 0 runs them all. Returns the manifest
        /// as it now stands — <c>Complete</c> false means "call again to finish".
        /// </summary>
        public async Task<PhotoExportManifest> RunAsync(string directory, int maxSections = 0)
        {
            System.IO.Directory.CreateDirectory(directory);
            var manifest = ReadManifest(directory) ?? new PhotoExportManifest
            {
                Version = PhotoCurationExportFormat.Version,
                CreatedUtc = DateTime.UtcNow,
            };

            var done = new HashSet<string>(manifest.Sections, StringComparer.OrdinalIgnoreCase);
            var ran = 0;
            foreach (var section in PhotoCurationExportFormat.Sections)
            {
                if (done.Contains(section)) continue;
                if (maxSections > 0 && ran >= maxSections) break;

                var count = await WriteSectionAsync(directory, section);
                manifest.Sections.Add(section);
                manifest.Counts[section] = count;
                ran++;
                log($"{{ section: \"{section}\", rows: {count}, remaining: {PhotoCurationExportFormat.Sections.Count - manifest.Sections.Count} }}");
                // Written after every section so a kill leaves a resumable directory rather than a
                // pile of files nothing knows the state of.
                WriteManifest(directory, manifest);
            }

            manifest.Complete = manifest.Sections.Count == PhotoCurationExportFormat.Sections.Count;
            WriteManifest(directory, manifest);
            return manifest;
        }

        private Task<int> WriteSectionAsync(string directory, string section)
        {
            var file = Path.Combine(directory, section);
            switch (section)
            {
                case PhotoCurationExportFormat.AssetsFile: return WriteAssetsAsync(file);
                case PhotoCurationExportFormat.PeopleFile: return WritePeopleAsync(file);
                case PhotoCurationExportFormat.PersonTagsFile: return WritePersonTagsAsync(file);
                case PhotoCurationExportFormat.AlbumsFile: return WriteAlbumsAsync(file);
                case PhotoCurationExportFormat.DupeGroupsFile: return WriteDupeGroupsAsync(file);
                case PhotoCurationExportFormat.GoogleItemsFile: return WriteGoogleItemsAsync(file);
                case PhotoCurationExportFormat.CurationBatchesFile: return WriteCurationBatchesAsync(file);
                default: throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown export section.");
            }
        }

        // ── Sections ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The assets worth carrying: everything a human touched, plus everything any other exported
        /// row points at. An asset whose only facts are its EXIF and its pixels is deliberately absent —
        /// a re-ingest re-derives all of that from the files, and an export that copied it would be a
        /// slow, stale duplicate of the collection instead of a record of the labor.
        /// </summary>
        private async Task<int> WriteAssetsAsync(string file)
        {
            using var writer = new ArrayFileWriter(file);
            using var db = dbFactory();

            var cursor = 0;
            var total = 0;
            while (true)
            {
                var page = await CuratedAssets(db)
                    .Where(a => a.Id > cursor)
                    .OrderBy(a => a.Id)
                    .Take(pageSize)
                    .Select(a => new
                    {
                        a.Id, a.Sha256, a.Path, a.SizeBytes, a.Hidden, a.Shelf, a.TakenAt, a.TakenAtUtcRaw,
                        a.TakenAtSource, a.YearMin, a.YearMax, a.LocationLabel, a.LocationSource, a.IngestBatch,
                    })
                    .ToListAsync();
                if (page.Count == 0) break;

                foreach (var a in page)
                    writer.Write(new PhotoAssetExport
                    {
                        Sha256 = a.Sha256,
                        Path = a.Path,
                        SizeBytes = a.SizeBytes,
                        Hidden = a.Hidden,
                        // Written only when it is not the default: a Gallery shelf is a fact worth
                        // carrying, "this is on the timeline like everything else" is not, and an
                        // export of 100k assets should not repeat it 100k times.
                        Shelf = a.Shelf == PhotoShelf.Timeline ? null : a.Shelf.ToString(),
                        TakenAt = a.TakenAt,
                        TakenAtUtcRaw = a.TakenAtUtcRaw,
                        TakenAtSource = a.TakenAtSource.ToString(),
                        YearMin = a.YearMin,
                        YearMax = a.YearMax,
                        LocationLabel = a.LocationLabel,
                        LocationSource = a.LocationSource == PhotoLocationSource.Unknown ? null : a.LocationSource.ToString(),
                        IngestBatch = a.IngestBatch,
                    });

                cursor = page[page.Count - 1].Id;
                total += page.Count;
                log($"  assets: {total} written (cursor {cursor})");
            }

            writer.Complete();
            return total;
        }

        private static IQueryable<PhotoAsset> CuratedAssets(MovieDb db)
        {
            var referenced = db.PhotoPersonTags.Select(t => t.PhotoAssetId)
                .Union(db.PhotoAlbumEntries.Select(e => e.PhotoAssetId))
                .Union(db.PhotoDupeMembers.Select(m => m.PhotoAssetId))
                .Union(db.PhotoGoogleItems.Where(g => g.MatchedPhotoAssetId != null).Select(g => g.MatchedPhotoAssetId!.Value))
                .Union(db.PhotoAlbums.Where(a => a.CoverAssetId != null).Select(a => a.CoverAssetId!.Value))
                .Union(db.FamilyPeople.Where(p => p.CoverAssetId != null).Select(p => p.CoverAssetId!.Value))
                // A pending hide proposal names assets that may otherwise be untouched — an export
                // taken mid-review must carry them, or the restored proposal would point at nothing.
                .Union(db.PhotoCurationBatchItems.Select(i => i.PhotoAssetId));

            return db.PhotoAssets.Where(a =>
                a.Hidden
                // §2.12: being on the Gallery shelf is itself curation — somebody decided this picture
                // is art rather than family record. Usually the asset is an album member too and would
                // have been caught by `referenced`, but a bare shelf move (no --album) has no other
                // trace, and an export that dropped it would restore the memes onto the timeline.
                || a.Shelf == PhotoShelf.Archive
                || a.TakenAtSource == TakenAtSource.Manual
                || a.TakenAtSource == TakenAtSource.Estimated
                || a.TakenAtSource == TakenAtSource.GoogleSidecar
                || a.LocationSource == PhotoLocationSource.Manual
                || referenced.Contains(a.Id));
        }

        private async Task<int> WritePeopleAsync(string file)
        {
            using var writer = new ArrayFileWriter(file);
            using var db = dbFactory();

            var cursor = 0;
            var total = 0;
            while (true)
            {
                var page = await db.FamilyPeople
                    .Where(p => p.Id > cursor)
                    .OrderBy(p => p.Id)
                    .Take(pageSize)
                    .Select(p => new
                    {
                        p.Id, p.Name, p.BirthYear, p.ImmichPersonId, p.CreatedUtc,
                        UserName = p.User != null ? p.User.Username : null,
                        Cover = p.CoverAsset != null ? new { p.CoverAsset.Sha256, p.CoverAsset.Path } : null,
                    })
                    .ToListAsync();
                if (page.Count == 0) break;

                foreach (var p in page)
                    writer.Write(new PhotoPersonExport
                    {
                        Key = p.Id,
                        Name = p.Name,
                        BirthYear = p.BirthYear,
                        UserName = p.UserName,
                        CoverAsset = p.Cover == null ? null : new PhotoAssetKey { Sha256 = p.Cover.Sha256, Path = p.Cover.Path },
                        ImmichPersonId = p.ImmichPersonId,
                        CreatedUtc = p.CreatedUtc,
                    });

                cursor = page[page.Count - 1].Id;
                total += page.Count;
            }

            writer.Complete();
            return total;
        }

        private async Task<int> WritePersonTagsAsync(string file)
        {
            using var writer = new ArrayFileWriter(file);
            using var db = dbFactory();

            var cursor = 0;
            var total = 0;
            while (true)
            {
                var page = await db.PhotoPersonTags
                    .Where(t => t.Id > cursor)
                    .OrderBy(t => t.Id)
                    .Take(pageSize)
                    .Select(t => new
                    {
                        t.Id, t.FamilyPersonId, t.Source, t.Confidence, t.BoxX, t.BoxY, t.BoxW, t.BoxH,
                        t.ImmichPersonId, t.CreatedUtc, t.ConfirmedUtc,
                        t.PhotoAsset.Sha256, t.PhotoAsset.Path,
                        PersonName = t.FamilyPerson.Name,
                    })
                    .ToListAsync();
                if (page.Count == 0) break;

                foreach (var t in page)
                    writer.Write(new PhotoPersonTagExport
                    {
                        PersonKey = t.FamilyPersonId,
                        PersonName = t.PersonName,
                        Asset = new PhotoAssetKey { Sha256 = t.Sha256, Path = t.Path },
                        Source = t.Source.ToString(),
                        Confidence = t.Confidence,
                        BoxX = t.BoxX,
                        BoxY = t.BoxY,
                        BoxW = t.BoxW,
                        BoxH = t.BoxH,
                        ImmichPersonId = t.ImmichPersonId,
                        CreatedUtc = t.CreatedUtc,
                        ConfirmedUtc = t.ConfirmedUtc,
                    });

                cursor = page[page.Count - 1].Id;
                total += page.Count;
                log($"  person-tags: {total} written");
            }

            writer.Complete();
            return total;
        }

        /// <summary>Albums carry their entries inline: an album without its membership is not a
        /// restorable album, and the two must never land in the export half a run apart.</summary>
        private async Task<int> WriteAlbumsAsync(string file)
        {
            using var writer = new ArrayFileWriter(file);
            using var db = dbFactory();

            var cursor = 0;
            var total = 0;
            while (true)
            {
                var page = await db.PhotoAlbums
                    .Where(a => a.Id > cursor)
                    .OrderBy(a => a.Id)
                    .Take(Math.Max(1, pageSize / 20))
                    .Select(a => new
                    {
                        a.Id, a.Title, a.Slug, a.Description, a.RangeStart, a.RangeEnd, a.SortOrder, a.CreatedUtc,
                        a.Shelf, a.ArtistName,
                        CreatedBy = a.CreatedByUser != null ? a.CreatedByUser.Username : null,
                        Cover = a.CoverAsset != null ? new { a.CoverAsset.Sha256, a.CoverAsset.Path } : null,
                    })
                    .ToListAsync();
                if (page.Count == 0) break;

                var ids = page.Select(a => a.Id).ToList();
                var entries = await db.PhotoAlbumEntries
                    .Where(e => ids.Contains(e.PhotoAlbumId))
                    .OrderBy(e => e.PhotoAlbumId).ThenBy(e => e.SortOrder).ThenBy(e => e.Id)
                    .Select(e => new { e.PhotoAlbumId, e.SortOrder, e.Caption, e.PhotoAsset.Sha256, e.PhotoAsset.Path })
                    .ToListAsync();
                var byAlbum = entries.GroupBy(e => e.PhotoAlbumId).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var a in page)
                    writer.Write(new PhotoAlbumExport
                    {
                        Title = a.Title,
                        Slug = a.Slug,
                        Description = a.Description,
                        CoverAsset = a.Cover == null ? null : new PhotoAssetKey { Sha256 = a.Cover.Sha256, Path = a.Cover.Path },
                        RangeStart = a.RangeStart,
                        RangeEnd = a.RangeEnd,
                        SortOrder = a.SortOrder,
                        Shelf = a.Shelf == PhotoShelf.Timeline ? null : a.Shelf.ToString(),
                        ArtistName = a.ArtistName,
                        CreatedByUserName = a.CreatedBy,
                        CreatedUtc = a.CreatedUtc,
                        Entries = byAlbum.TryGetValue(a.Id, out var rows)
                            ? rows.Select(e => new PhotoAlbumEntryExport
                            {
                                Asset = new PhotoAssetKey { Sha256 = e.Sha256, Path = e.Path },
                                SortOrder = e.SortOrder,
                                Caption = e.Caption,
                            }).ToList()
                            : new List<PhotoAlbumEntryExport>(),
                    });

                cursor = page[page.Count - 1].Id;
                total += page.Count;
                log($"  albums: {total} written");
            }

            writer.Complete();
            return total;
        }

        private async Task<int> WriteDupeGroupsAsync(string file)
        {
            using var writer = new ArrayFileWriter(file);
            using var db = dbFactory();

            var cursor = 0;
            var total = 0;
            while (true)
            {
                var page = await db.PhotoDupeGroups
                    .Where(g => g.Id > cursor)
                    .OrderBy(g => g.Id)
                    .Take(Math.Max(1, pageSize / 4))
                    .Select(g => new { g.Id, g.Kind, g.Status, g.CreatedUtc, g.ResolvedUtc })
                    .ToListAsync();
                if (page.Count == 0) break;

                var ids = page.Select(g => g.Id).ToList();
                var members = await db.PhotoDupeMembers
                    .Where(m => ids.Contains(m.PhotoDupeGroupId))
                    .OrderBy(m => m.PhotoDupeGroupId).ThenBy(m => m.Id)
                    .Select(m => new { m.PhotoDupeGroupId, m.IsMaster, m.Similarity, m.PhotoAsset.Sha256, m.PhotoAsset.Path })
                    .ToListAsync();
                var byGroup = members.GroupBy(m => m.PhotoDupeGroupId).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var g in page)
                    writer.Write(new PhotoDupeGroupExport
                    {
                        Kind = g.Kind.ToString(),
                        Status = g.Status.ToString(),
                        CreatedUtc = g.CreatedUtc,
                        ResolvedUtc = g.ResolvedUtc,
                        Members = byGroup.TryGetValue(g.Id, out var rows)
                            ? rows.Select(m => new PhotoDupeMemberExport
                            {
                                Asset = new PhotoAssetKey { Sha256 = m.Sha256, Path = m.Path },
                                IsMaster = m.IsMaster,
                                Similarity = m.Similarity,
                            }).ToList()
                            : new List<PhotoDupeMemberExport>(),
                    });

                cursor = page[page.Count - 1].Id;
                total += page.Count;
                log($"  dupe-groups: {total} written");
            }

            writer.Complete();
            return total;
        }

        private async Task<int> WriteGoogleItemsAsync(string file)
        {
            using var writer = new ArrayFileWriter(file);
            using var db = dbFactory();

            var cursor = 0;
            var total = 0;
            while (true)
            {
                var page = await db.PhotoGoogleItems
                    .Where(i => i.Id > cursor)
                    .OrderBy(i => i.Id)
                    .Take(pageSize)
                    .Select(i => new
                    {
                        i.Id, i.TakeoutFileName, i.TakeoutRelativePath, i.TakenAtUtc, i.SizeBytes, i.SidecarJson,
                        i.Status, i.MatchMethod, i.MatchDistance, i.Disagreements, i.DownloadedPath,
                        i.FirstSeenUtc, i.LastSeenUtc,
                        Matched = i.MatchedPhotoAsset != null ? new { i.MatchedPhotoAsset.Sha256, i.MatchedPhotoAsset.Path } : null,
                    })
                    .ToListAsync();
                if (page.Count == 0) break;

                foreach (var i in page)
                    writer.Write(new PhotoGoogleItemExport
                    {
                        TakeoutFileName = i.TakeoutFileName,
                        TakeoutRelativePath = i.TakeoutRelativePath,
                        TakenAtUtc = i.TakenAtUtc,
                        SizeBytes = i.SizeBytes,
                        SidecarJson = i.SidecarJson,
                        MatchedAsset = i.Matched == null ? null : new PhotoAssetKey { Sha256 = i.Matched.Sha256, Path = i.Matched.Path },
                        Status = i.Status.ToString(),
                        MatchMethod = i.MatchMethod,
                        MatchDistance = i.MatchDistance,
                        Disagreements = i.Disagreements,
                        DownloadedPath = i.DownloadedPath,
                        FirstSeenUtc = i.FirstSeenUtc,
                        LastSeenUtc = i.LastSeenUtc,
                    });

                cursor = page[page.Count - 1].Id;
                total += page.Count;
                log($"  google-items: {total} written");
            }

            writer.Complete();
            return total;
        }

        /// <summary>
        /// The review batches (§2.5/§2.9), items inline. Ingest-approval and baseline rows carry no
        /// items and are tiny; a hide proposal carries one row per proposed asset, so the batches are
        /// paged small and their items fetched per page rather than all at once.
        /// </summary>
        private async Task<int> WriteCurationBatchesAsync(string file)
        {
            using var writer = new ArrayFileWriter(file);
            using var db = dbFactory();

            var cursor = 0;
            var total = 0;
            while (true)
            {
                var page = await db.PhotoCurationBatches
                    .Where(b => b.Id > cursor)
                    .OrderBy(b => b.Id)
                    .Take(Math.Max(1, pageSize / 20))
                    .Select(b => new
                    {
                        b.Id, b.Kind, b.BatchId, b.Status, b.CreatedUtc, b.DecidedUtc, b.AppliedCount,
                        b.Cursor, b.Complete,
                        DecidedBy = b.DecidedByUser != null ? b.DecidedByUser.Username : null,
                    })
                    .ToListAsync();
                if (page.Count == 0) break;

                var ids = page.Select(b => b.Id).ToList();
                var items = await db.PhotoCurationBatchItems
                    .Where(i => ids.Contains(i.PhotoCurationBatchId))
                    .OrderBy(i => i.PhotoCurationBatchId).ThenBy(i => i.Id)
                    .Select(i => new { i.PhotoCurationBatchId, i.Rule, i.PhotoAsset.Sha256, i.PhotoAsset.Path })
                    .ToListAsync();
                var byBatch = items.GroupBy(i => i.PhotoCurationBatchId).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var b in page)
                    writer.Write(new PhotoCurationBatchExport
                    {
                        Kind = b.Kind.ToString(),
                        BatchId = b.BatchId,
                        Status = b.Status.ToString(),
                        CreatedUtc = b.CreatedUtc,
                        DecidedUtc = b.DecidedUtc,
                        DecidedByUserName = b.DecidedBy,
                        AppliedCount = b.AppliedCount,
                        Cursor = b.Cursor,
                        Complete = b.Complete,
                        Items = byBatch.TryGetValue(b.Id, out var rows)
                            ? rows.Select(i => new PhotoCurationBatchItemExport
                            {
                                Asset = new PhotoAssetKey { Sha256 = i.Sha256, Path = i.Path },
                                Rule = i.Rule,
                            }).ToList()
                            : new List<PhotoCurationBatchItemExport>(),
                    });

                cursor = page[page.Count - 1].Id;
                total += page.Count;
                log($"  curation-batches: {total} written");
            }

            writer.Complete();
            return total;
        }

        // ── Manifest + file plumbing ─────────────────────────────────────────────────────────────

        public static PhotoExportManifest? ReadManifest(string directory)
        {
            var file = Path.Combine(directory, PhotoCurationExportFormat.ManifestFile);
            try
            {
                if (!File.Exists(file)) return null;
                return JsonSerializer.Deserialize<PhotoExportManifest>(File.ReadAllText(file), PhotoCurationExportFormat.Json);
            }
            catch (Exception e) when (e is IOException || e is JsonException)
            {
                return null;
            }
        }

        private static void WriteManifest(string directory, PhotoExportManifest manifest)
        {
            var file = Path.Combine(directory, PhotoCurationExportFormat.ManifestFile);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".part";
            File.WriteAllText(temp, JsonSerializer.Serialize(manifest, PhotoCurationExportFormat.Json));
            File.Move(temp, file, overwrite: true);
        }

        /// <summary>
        /// A JSON array written one element at a time, so a section of any size costs one row of
        /// memory. Temp-then-move: a killed export must not leave a truncated array at a section's
        /// path, because a resumed run would see the file and skip the section — the export would then
        /// claim rows it does not have, which is the one failure a backup must never have.
        /// </summary>
        private sealed class ArrayFileWriter : IDisposable
        {
            private readonly string destination;
            private readonly string temp;
            private readonly FileStream stream;
            private readonly Utf8JsonWriter writer;
            private bool completed;

            public ArrayFileWriter(string destination)
            {
                this.destination = destination;
                temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartArray();
            }

            public void Write<T>(T item) => JsonSerializer.Serialize(writer, item, PhotoCurationExportFormat.Json);

            public void Complete()
            {
                writer.WriteEndArray();
                writer.Flush();
                writer.Dispose();
                stream.Dispose();
                File.Move(temp, destination, overwrite: true);
                completed = true;
            }

            public void Dispose()
            {
                if (completed) return;
                try
                {
                    writer.Dispose();
                    stream.Dispose();
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (IOException) { }
            }
        }
    }
}
