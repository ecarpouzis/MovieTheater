using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>Per-section tally of what an import did, or would do. Kept as counts plus a capped list
    /// of examples: a restore is judged by "how many, and which kind", and a report that prints a line
    /// per row is a report nobody reads.</summary>
    public sealed class PhotoImportSectionReport
    {
        public int Examined { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int Unmatched { get; set; }
        public int Ambiguous { get; set; }
        public Dictionary<string, int> Extra { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<string> Examples { get; } = new List<string>();

        private const int MaxExamples = 20;

        public void Note(string example)
        {
            if (Examples.Count < MaxExamples) Examples.Add(example);
        }

        public void Add(string key, int n = 1)
        {
            if (n == 0) return;
            Extra[key] = (Extra.TryGetValue(key, out var v) ? v : 0) + n;
        }

        public string Summary() =>
            $"examined {Examined}, create {Created}, update {Updated}, skip {Skipped}"
            + (Unmatched > 0 ? $", unmatched {Unmatched}" : "")
            + (Ambiguous > 0 ? $", ambiguous {Ambiguous}" : "")
            + (Extra.Count > 0 ? "  [" + string.Join(", ", Extra.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}: {kv.Value}")) + "]" : "");
    }

    public sealed class PhotoCurationImportReport
    {
        public Dictionary<string, PhotoImportSectionReport> Sections { get; } =
            new Dictionary<string, PhotoImportSectionReport>(StringComparer.Ordinal);

        public PhotoImportSectionReport For(string section)
        {
            if (!Sections.TryGetValue(section, out var report)) Sections[section] = report = new PhotoImportSectionReport();
            return report;
        }

        public void Merge(PhotoCurationImportReport other)
        {
            foreach (var kv in other.Sections)
            {
                var mine = For(kv.Key);
                mine.Examined += kv.Value.Examined;
                mine.Created += kv.Value.Created;
                mine.Updated += kv.Value.Updated;
                mine.Skipped += kv.Value.Skipped;
                mine.Unmatched += kv.Value.Unmatched;
                mine.Ambiguous += kv.Value.Ambiguous;
                foreach (var e in kv.Value.Extra) mine.Add(e.Key, e.Value);
                foreach (var e in kv.Value.Examples) mine.Note(e);
            }
        }
    }

    /// <summary>
    /// Reads a curation export and reports — or applies — the delta against the current database
    /// (docs/photos-plan.md §2.11).
    ///
    /// <para><b>Dry-run is the default and the whole point.</b> §2.11 asks for a matching
    /// <c>photos-import --dry-run</c> that "proves round-trip fidelity once in CI-like fashion before
    /// it's ever needed in anger": knowing the restore works is the deliverable, and a restore lane
    /// that can only be tested by running it is not one. Writing requires an explicit opt-in from the
    /// caller, which the CLI spells <c>--apply</c>.</para>
    ///
    /// <para><b>Matching is content-first</b> (§2.11: "keyed by content hash + relative path"). The
    /// SHA-256 wins, the path is the fallback, and a hash that matches several local rows is reported
    /// as AMBIGUOUS rather than guessed at — the same stance the walk takes on ambiguous move pairings
    /// (§2.5). A restore that quietly attaches ten years of tags to the wrong copy of a photo would be
    /// worse than one that stops and says so.</para>
    ///
    /// <para><b>A human's word is never overwritten by a machine's.</b> Where the local row carries a
    /// <c>Manual</c> date and the export does not, the local one stands (§2.7) and the difference is
    /// counted. Everywhere else the export wins, because that is what restoring means.</para>
    ///
    /// <para>Bulk-job shape: bounded items per batch, <c>{processed, remaining, nextCursor}</c> per
    /// chunk, and a cursor (<c>section:index</c>) that resumes exactly where it stopped.</para>
    /// </summary>
    public sealed class PhotoCurationImporter
    {
        private readonly Func<MovieDb> dbFactory;
        private readonly string exportDir;
        private readonly bool apply;
        private readonly Action<string> log;
        private readonly int batchSize;

        private readonly Dictionary<string, object> sectionCache = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>Export person key → local FamilyPerson id, learned in the people section and used by
        /// the tags section. Only populated in apply mode; a dry run reports tags against people it
        /// knows the people section would have created.</summary>
        private readonly Dictionary<int, int> personKeyToLocalId = new Dictionary<int, int>();

        public PhotoCurationImporter(Func<MovieDb> dbFactory, string exportDir, bool apply, Action<string> log, int batchSize = 250)
        {
            this.dbFactory = dbFactory;
            this.exportDir = exportDir;
            this.apply = apply;
            this.log = log;
            this.batchSize = Math.Max(1, batchSize);
        }

        /// <summary>Drives bounded batches to completion (or <paramref name="maxBatches"/> of them),
        /// printing the per-chunk line and stopping deterministically on no progress.</summary>
        public async Task<PhotoCurationImportReport> RunAsync(string? cursor, int maxBatches = 0)
        {
            var total = new PhotoCurationImportReport();
            var position = ParseCursor(cursor);
            var batches = 0;

            while (maxBatches <= 0 || batches < maxBatches)
            {
                var (report, next, remaining, processed) = await BatchAsync(position);
                batches++;
                total.Merge(report);
                position = next;

                log($"{{ processed: {processed}, remaining: {remaining}, nextCursor: \"{FormatCursor(next)}\" }}");
                if (remaining <= 0) break;
                if (processed <= 0)
                {
                    log("No progress in a batch while items remained — stopping.");
                    break;
                }
            }

            return total;
        }

        // ── One bounded batch ────────────────────────────────────────────────────────────────────

        private async Task<(PhotoCurationImportReport report, (int section, int index) next, int remaining, int processed)>
            BatchAsync((int section, int index) position)
        {
            var report = new PhotoCurationImportReport();
            var sections = PhotoCurationExportFormat.Sections;

            // Skip past sections that are absent from this export (a partial export is importable).
            while (position.section < sections.Count && CountOf(sections[position.section]) <= position.index)
            {
                position = (position.section + 1, 0);
            }
            if (position.section >= sections.Count) return (report, position, 0, 0);

            var section = sections[position.section];
            var take = Math.Min(batchSize, CountOf(section) - position.index);

            using var db = dbFactory();
            switch (section)
            {
                case PhotoCurationExportFormat.AssetsFile:
                    await ImportAssetsAsync(db, report, Slice<PhotoAssetExport>(section, position.index, take)); break;
                case PhotoCurationExportFormat.PeopleFile:
                    await ImportPeopleAsync(db, report, Slice<PhotoPersonExport>(section, position.index, take)); break;
                case PhotoCurationExportFormat.PersonTagsFile:
                    await ImportPersonTagsAsync(db, report, Slice<PhotoPersonTagExport>(section, position.index, take)); break;
                case PhotoCurationExportFormat.AlbumsFile:
                    await ImportAlbumsAsync(db, report, Slice<PhotoAlbumExport>(section, position.index, take)); break;
                case PhotoCurationExportFormat.DupeGroupsFile:
                    await ImportDupeGroupsAsync(db, report, Slice<PhotoDupeGroupExport>(section, position.index, take)); break;
                case PhotoCurationExportFormat.GoogleItemsFile:
                    await ImportGoogleItemsAsync(db, report, Slice<PhotoGoogleItemExport>(section, position.index, take)); break;
                case PhotoCurationExportFormat.CurationBatchesFile:
                    await ImportCurationBatchesAsync(db, report, Slice<PhotoCurationBatchExport>(section, position.index, take)); break;
            }

            if (apply) await db.SaveChangesAsync();

            var next = (position.section, position.index + take);
            var remaining = Remaining(next);
            return (report, next, remaining, take);
        }

        private int Remaining((int section, int index) position)
        {
            var sections = PhotoCurationExportFormat.Sections;
            var remaining = 0;
            for (var s = position.section; s < sections.Count; s++)
                remaining += Math.Max(0, CountOf(sections[s]) - (s == position.section ? position.index : 0));
            return remaining;
        }

        // ── Sections ─────────────────────────────────────────────────────────────────────────────

        private async Task ImportAssetsAsync(MovieDb db, PhotoCurationImportReport report, List<PhotoAssetExport> items)
        {
            var section = report.For(PhotoCurationExportFormat.AssetsFile);
            var resolver = await ResolverAsync(db, items);

            foreach (var item in items)
            {
                section.Examined++;
                var match = resolver.Resolve(item);
                if (match.Asset == null)
                {
                    if (match.Ambiguous) { section.Ambiguous++; section.Note($"ambiguous: {item.Path}"); }
                    else { section.Unmatched++; section.Note($"no local asset: {item.Path}"); }
                    continue;
                }

                var row = match.Asset;
                var changed = false;

                if (row.Hidden != item.Hidden) { row.Hidden = item.Hidden; changed = true; section.Add(item.Hidden ? "hide" : "unhide"); }

                // §2.12: an absent shelf is the Timeline, which is both the enum's default and what
                // every export written before Phase 7 meant — so an older file restores unchanged
                // rather than needing its own branch.
                var exportedShelf = item.Shelf == null ? PhotoShelf.Timeline : ParseEnum(item.Shelf, PhotoShelf.Timeline);
                if (row.Shelf != exportedShelf)
                {
                    row.Shelf = exportedShelf;
                    changed = true;
                    section.Add(exportedShelf == PhotoShelf.Archive ? "shelf-archive" : "shelf-timeline");
                }

                var exportedSource = ParseEnum(item.TakenAtSource, TakenAtSource.Unknown);
                // §2.7: Manual outranks every automatic source and is never overwritten by one. A
                // restore therefore does not undo a date a human typed after the export was taken.
                if (row.TakenAtSource == TakenAtSource.Manual && exportedSource != TakenAtSource.Manual)
                {
                    section.Add("kept-local-manual-date");
                }
                else if (row.TakenAt != item.TakenAt || row.TakenAtSource != exportedSource
                         || row.TakenAtUtcRaw != item.TakenAtUtcRaw || row.YearMin != item.YearMin || row.YearMax != item.YearMax)
                {
                    row.TakenAt = item.TakenAt;
                    row.TakenAtUtcRaw = item.TakenAtUtcRaw;
                    row.TakenAtSource = exportedSource;
                    row.YearMin = item.YearMin;
                    row.YearMax = item.YearMax;
                    changed = true;
                    section.Add("date");
                }

                var exportedLocation = item.LocationSource == null
                    ? PhotoLocationSource.Unknown
                    : ParseEnum(item.LocationSource, PhotoLocationSource.Unknown);
                if (row.LocationSource == PhotoLocationSource.Manual && exportedLocation != PhotoLocationSource.Manual)
                {
                    section.Add("kept-local-manual-location");
                }
                else if (row.LocationLabel != item.LocationLabel || row.LocationSource != exportedLocation)
                {
                    row.LocationLabel = item.LocationLabel;
                    row.LocationSource = exportedLocation;
                    changed = true;
                    section.Add("location");
                }

                if (changed) section.Updated++; else section.Skipped++;
                if (!apply && changed) section.Note($"would update: {item.Path}");
            }
        }

        private async Task ImportPeopleAsync(MovieDb db, PhotoCurationImportReport report, List<PhotoPersonExport> items)
        {
            var section = report.For(PhotoCurationExportFormat.PeopleFile);
            var names = items.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var local = await db.FamilyPeople.Where(p => names.Contains(p.Name)).ToListAsync();
            var resolver = await ResolverAsync(db, items.Where(p => p.CoverAsset != null).Select(p => p.CoverAsset!));

            var userNames = items.Where(p => p.UserName != null).Select(p => p.UserName!).Distinct().ToList();
            var users = userNames.Count == 0
                ? new List<User>()
                : await db.Users.Where(u => u.Username != null && userNames.Contains(u.Username)).ToListAsync();

            foreach (var item in items)
            {
                section.Examined++;
                // Name is the person's identity across databases; birth year breaks a tie between two
                // people who share one. Nothing here invents a person from a tag.
                var candidates = local.Where(p => string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                var row = candidates.FirstOrDefault(p => p.BirthYear == item.BirthYear) ?? candidates.FirstOrDefault();

                if (row == null)
                {
                    section.Created++;
                    if (!apply) { section.Note($"would create person"); continue; }

                    row = new FamilyPerson
                    {
                        Name = item.Name,
                        BirthYear = item.BirthYear,
                        ImmichPersonId = item.ImmichPersonId,
                        CreatedUtc = item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc,
                        UserId = users.FirstOrDefault(u => string.Equals(u.Username, item.UserName, StringComparison.OrdinalIgnoreCase))?.UserID,
                        CoverAssetId = item.CoverAsset == null ? null : resolver.Resolve(item.CoverAsset).Asset?.Id,
                    };
                    db.FamilyPeople.Add(row);
                    local.Add(row);
                    // Flushed here so the tag section in a later batch can look the person up by name.
                    await db.SaveChangesAsync();
                    personKeyToLocalId[item.Key] = row.Id;
                    continue;
                }

                personKeyToLocalId[item.Key] = row.Id;

                // Fill blanks; never overwrite a local value that disagrees — a person's details are
                // hand-entered on both sides, and a restore is not evidence the newer one is wrong.
                var changed = false;
                if (row.BirthYear == null && item.BirthYear != null) { row.BirthYear = item.BirthYear; changed = true; }
                if (row.ImmichPersonId == null && item.ImmichPersonId != null) { row.ImmichPersonId = item.ImmichPersonId; changed = true; }
                if (row.CoverAssetId == null && item.CoverAsset != null)
                {
                    var cover = resolver.Resolve(item.CoverAsset).Asset;
                    if (cover != null) { row.CoverAssetId = cover.Id; changed = true; }
                }
                if (row.UserId == null && item.UserName != null)
                {
                    var user = users.FirstOrDefault(u => string.Equals(u.Username, item.UserName, StringComparison.OrdinalIgnoreCase));
                    if (user != null) { row.UserId = user.UserID; changed = true; }
                }

                if (row.BirthYear != null && item.BirthYear != null && row.BirthYear != item.BirthYear)
                    section.Add("birth-year-conflict");

                if (changed) section.Updated++; else section.Skipped++;
            }
        }

        private async Task ImportPersonTagsAsync(MovieDb db, PhotoCurationImportReport report, List<PhotoPersonTagExport> items)
        {
            var section = report.For(PhotoCurationExportFormat.PersonTagsFile);
            var resolver = await ResolverAsync(db, items.Select(t => t.Asset));

            var assetIds = items.Select(t => resolver.Resolve(t.Asset).Asset?.Id).Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
            var existing = assetIds.Count == 0
                ? new List<PhotoPersonTag>()
                : await db.PhotoPersonTags.Where(t => assetIds.Contains(t.PhotoAssetId)).ToListAsync();

            // Resolved by NAME, not by the export's person key: this section may run in a later process
            // than the people section (the import is chunked and resumable), where no key map survives.
            var names = items.Select(t => t.PersonName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var people = names.Count == 0
                ? new List<FamilyPerson>()
                : await db.FamilyPeople.Where(p => names.Contains(p.Name)).ToListAsync();

            foreach (var item in items)
            {
                section.Examined++;
                var match = resolver.Resolve(item.Asset);
                if (match.Asset == null)
                {
                    if (match.Ambiguous) section.Ambiguous++; else section.Unmatched++;
                    continue;
                }

                if (!personKeyToLocalId.TryGetValue(item.PersonKey, out var personId))
                {
                    var person = people.FirstOrDefault(p => string.Equals(p.Name, item.PersonName, StringComparison.OrdinalIgnoreCase));
                    if (person != null)
                    {
                        personId = person.Id;
                    }
                    else if (!apply)
                    {
                        // Dry run: the people section already reported this person as a create, so the
                        // tag would land with it. Counted, never invented.
                        section.Created++;
                        continue;
                    }
                    else
                    {
                        // Applying, and the person is genuinely absent — a partial export whose people
                        // section is missing. Reported rather than conjuring a person out of a tag.
                        section.Unmatched++;
                        section.Note("tag names a person this database does not have");
                        continue;
                    }
                }

                var row = existing.FirstOrDefault(t => t.PhotoAssetId == match.Asset.Id && t.FamilyPersonId == personId);
                var exportedSource = ParseEnum(item.Source, PhotoTagSource.Manual);
                if (row == null)
                {
                    section.Created++;
                    if (!apply) continue;
                    row = new PhotoPersonTag
                    {
                        PhotoAssetId = match.Asset.Id,
                        FamilyPersonId = personId,
                        Source = exportedSource,
                        Confidence = item.Confidence,
                        BoxX = item.BoxX,
                        BoxY = item.BoxY,
                        BoxW = item.BoxW,
                        BoxH = item.BoxH,
                        ImmichPersonId = item.ImmichPersonId,
                        CreatedUtc = item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc,
                        ConfirmedUtc = item.ConfirmedUtc,
                    };
                    db.PhotoPersonTags.Add(row);
                    existing.Add(row);
                    continue;
                }

                // A suggestion may be promoted by the export; a confirmation is never demoted back to
                // a suggestion by one. The ranking is PhotoPersonTags.Rank and nothing local — a copy
                // here scored Rejected at 0, tied with Suggested, so a rejection TOMBSTONE in an export
                // could never be applied over the suggestion it was written to bury. A restore that
                // silently drops every "no" is not a restore (§2.11).
                if (PhotoPersonTags.Rank(exportedSource) > PhotoPersonTags.Rank(row.Source))
                {
                    row.Source = exportedSource;
                    row.ConfirmedUtc = item.ConfirmedUtc ?? row.ConfirmedUtc;
                    section.Updated++;
                }
                else section.Skipped++;
            }
        }

        private async Task ImportAlbumsAsync(MovieDb db, PhotoCurationImportReport report, List<PhotoAlbumExport> items)
        {
            var section = report.For(PhotoCurationExportFormat.AlbumsFile);
            var slugs = items.Select(a => a.Slug).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var local = await db.PhotoAlbums.Where(a => slugs.Contains(a.Slug)).ToListAsync();
            var allKeys = items.SelectMany(a => a.Entries.Select(e => e.Asset))
                .Concat(items.Where(a => a.CoverAsset != null).Select(a => a.CoverAsset!));
            var resolver = await ResolverAsync(db, allKeys);

            var albumIds = local.Select(a => a.Id).ToList();
            var entries = albumIds.Count == 0
                ? new List<PhotoAlbumEntry>()
                : await db.PhotoAlbumEntries.Where(e => albumIds.Contains(e.PhotoAlbumId)).ToListAsync();

            foreach (var item in items)
            {
                section.Examined++;
                var album = local.FirstOrDefault(a => string.Equals(a.Slug, item.Slug, StringComparison.OrdinalIgnoreCase));
                var coverId = item.CoverAsset == null ? null : resolver.Resolve(item.CoverAsset).Asset?.Id;

                if (album == null)
                {
                    section.Created++;
                    section.Add("entries-created", item.Entries.Count(e => resolver.Resolve(e.Asset).Asset != null));
                    section.Add("entries-unmatched", item.Entries.Count(e => resolver.Resolve(e.Asset).Asset == null));
                    if (!apply) { section.Note($"would create album: {item.Slug}"); continue; }

                    album = new PhotoAlbum
                    {
                        Title = item.Title,
                        Slug = item.Slug,
                        Description = item.Description,
                        CoverAssetId = coverId,
                        RangeStart = item.RangeStart,
                        RangeEnd = item.RangeEnd,
                        SortOrder = item.SortOrder,
                        // §2.12: which index the collection belongs on, and whose work it is. An older
                        // export carries neither, which restores as a plain family album — what every
                        // album written before Phase 7 was.
                        Shelf = item.Shelf == null ? PhotoShelf.Timeline : ParseEnum(item.Shelf, PhotoShelf.Timeline),
                        ArtistName = item.ArtistName,
                        CreatedUtc = item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc,
                    };
                    db.PhotoAlbums.Add(album);
                    await db.SaveChangesAsync();
                    local.Add(album);

                    foreach (var entry in item.Entries)
                    {
                        var asset = resolver.Resolve(entry.Asset).Asset;
                        if (asset == null) continue;
                        db.PhotoAlbumEntries.Add(new PhotoAlbumEntry
                        {
                            PhotoAlbumId = album.Id,
                            PhotoAssetId = asset.Id,
                            SortOrder = entry.SortOrder,
                            Caption = entry.Caption,
                        });
                    }
                    continue;
                }

                var changed = false;
                if (album.Title != item.Title) { album.Title = item.Title; changed = true; }
                if (album.Description != item.Description) { album.Description = item.Description; changed = true; }
                if (album.RangeStart != item.RangeStart) { album.RangeStart = item.RangeStart; changed = true; }
                if (album.RangeEnd != item.RangeEnd) { album.RangeEnd = item.RangeEnd; changed = true; }
                if (album.SortOrder != item.SortOrder) { album.SortOrder = item.SortOrder; changed = true; }
                var albumShelf = item.Shelf == null ? PhotoShelf.Timeline : ParseEnum(item.Shelf, PhotoShelf.Timeline);
                if (album.Shelf != albumShelf) { album.Shelf = albumShelf; changed = true; }
                if (album.ArtistName != item.ArtistName) { album.ArtistName = item.ArtistName; changed = true; }
                if (coverId != null && album.CoverAssetId != coverId) { album.CoverAssetId = coverId; changed = true; }

                foreach (var entry in item.Entries)
                {
                    var match = resolver.Resolve(entry.Asset);
                    if (match.Asset == null) { section.Add("entries-unmatched"); continue; }

                    var row = entries.FirstOrDefault(e => e.PhotoAlbumId == album.Id && e.PhotoAssetId == match.Asset.Id);
                    if (row == null)
                    {
                        section.Add("entries-created");
                        changed = true;
                        if (!apply) continue;
                        row = new PhotoAlbumEntry
                        {
                            PhotoAlbumId = album.Id,
                            PhotoAssetId = match.Asset.Id,
                            SortOrder = entry.SortOrder,
                            Caption = entry.Caption,
                        };
                        db.PhotoAlbumEntries.Add(row);
                        entries.Add(row);
                        continue;
                    }

                    if (row.SortOrder != entry.SortOrder || row.Caption != entry.Caption)
                    {
                        row.SortOrder = entry.SortOrder;
                        row.Caption = entry.Caption;
                        section.Add("entries-updated");
                        changed = true;
                    }
                }

                if (changed) section.Updated++; else section.Skipped++;
            }
        }

        private async Task ImportDupeGroupsAsync(MovieDb db, PhotoCurationImportReport report, List<PhotoDupeGroupExport> items)
        {
            var section = report.For(PhotoCurationExportFormat.DupeGroupsFile);
            var resolver = await ResolverAsync(db, items.SelectMany(g => g.Members.Select(m => m.Asset)));

            // A group has no identity of its own — it IS its member set, so that is what an existing
            // group is recognized by. Anything else would duplicate a resolved group on every restore.
            var memberAssetIds = items.SelectMany(g => g.Members)
                .Select(m => resolver.Resolve(m.Asset).Asset?.Id)
                .Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
            var localMembers = memberAssetIds.Count == 0
                ? new List<PhotoDupeMember>()
                : await db.PhotoDupeMembers.Where(m => memberAssetIds.Contains(m.PhotoAssetId)).ToListAsync();
            var localGroupIds = localMembers.Select(m => m.PhotoDupeGroupId).Distinct().ToList();
            var allMembersOfThose = localGroupIds.Count == 0
                ? new List<PhotoDupeMember>()
                : await db.PhotoDupeMembers.Where(m => localGroupIds.Contains(m.PhotoDupeGroupId)).ToListAsync();
            var localGroups = localGroupIds.Count == 0
                ? new List<PhotoDupeGroup>()
                : await db.PhotoDupeGroups.Where(g => localGroupIds.Contains(g.Id)).ToListAsync();
            var setByGroup = allMembersOfThose.GroupBy(m => m.PhotoDupeGroupId)
                .ToDictionary(g => g.Key, g => new HashSet<int>(g.Select(m => m.PhotoAssetId)));

            foreach (var item in items)
            {
                section.Examined++;
                var resolved = item.Members
                    .Select(m => new { Member = m, Asset = resolver.Resolve(m.Asset).Asset })
                    .ToList();
                if (resolved.Any(r => r.Asset == null))
                {
                    section.Unmatched++;
                    section.Note("dupe group has members with no local asset");
                    continue;
                }

                var wanted = new HashSet<int>(resolved.Select(r => r.Asset!.Id));
                var existingId = setByGroup.FirstOrDefault(kv => kv.Value.SetEquals(wanted)).Key;
                var group = existingId == 0 ? null : localGroups.FirstOrDefault(g => g.Id == existingId);

                if (group == null)
                {
                    section.Created++;
                    if (!apply) continue;

                    group = new PhotoDupeGroup
                    {
                        Kind = ParseEnum(item.Kind, PhotoDupeGroupKind.Near),
                        Status = ParseEnum(item.Status, PhotoDupeGroupStatus.Pending),
                        CreatedUtc = item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc,
                        ResolvedUtc = item.ResolvedUtc,
                    };
                    db.PhotoDupeGroups.Add(group);
                    await db.SaveChangesAsync();
                    foreach (var r in resolved)
                        db.PhotoDupeMembers.Add(new PhotoDupeMember
                        {
                            PhotoDupeGroupId = group.Id,
                            PhotoAssetId = r.Asset!.Id,
                            IsMaster = r.Member.IsMaster,
                            Similarity = r.Member.Similarity,
                        });
                    continue;
                }

                var changed = false;
                var status = ParseEnum(item.Status, PhotoDupeGroupStatus.Pending);
                if (group.Status != status) { group.Status = status; group.ResolvedUtc = item.ResolvedUtc; changed = true; }
                foreach (var r in resolved)
                {
                    var member = allMembersOfThose.FirstOrDefault(m => m.PhotoDupeGroupId == group.Id && m.PhotoAssetId == r.Asset!.Id);
                    if (member != null && member.IsMaster != r.Member.IsMaster)
                    {
                        member.IsMaster = r.Member.IsMaster;
                        changed = true;
                        section.Add("master-repointed");
                    }
                }
                if (changed) section.Updated++; else section.Skipped++;
            }
        }

        private async Task ImportGoogleItemsAsync(MovieDb db, PhotoCurationImportReport report, List<PhotoGoogleItemExport> items)
        {
            var section = report.For(PhotoCurationExportFormat.GoogleItemsFile);
            var resolver = await ResolverAsync(db, items.Where(i => i.MatchedAsset != null).Select(i => i.MatchedAsset!));

            var names = items.Select(i => i.TakeoutFileName).Distinct().ToList();
            var local = await db.PhotoGoogleItems.Where(i => names.Contains(i.TakeoutFileName)).ToListAsync();

            foreach (var item in items)
            {
                section.Examined++;
                // (file name, taken time, size) — the §2.10 identity triple; sidecars carry no stable id.
                var row = local.FirstOrDefault(i =>
                    string.Equals(i.TakeoutFileName, item.TakeoutFileName, StringComparison.OrdinalIgnoreCase)
                    && i.TakenAtUtc == item.TakenAtUtc && i.SizeBytes == item.SizeBytes);

                var matchedId = item.MatchedAsset == null ? null : resolver.Resolve(item.MatchedAsset).Asset?.Id;
                var status = ParseEnum(item.Status, PhotoGoogleItemStatus.Pending);

                if (row == null)
                {
                    section.Created++;
                    if (!apply) continue;
                    db.PhotoGoogleItems.Add(new PhotoGoogleItem
                    {
                        TakeoutFileName = item.TakeoutFileName,
                        TakeoutRelativePath = item.TakeoutRelativePath,
                        TakenAtUtc = item.TakenAtUtc,
                        SizeBytes = item.SizeBytes,
                        SidecarJson = item.SidecarJson,
                        MatchedPhotoAssetId = matchedId,
                        Status = matchedId == null ? status : PhotoGoogleItemStatus.Matched,
                        MatchMethod = item.MatchMethod,
                        // Phase 6 (§2.10): the distance a resemblance match was accepted at, the
                        // disagreements the pass flagged, and where the download lane put it.
                        MatchDistance = item.MatchDistance,
                        Disagreements = item.Disagreements,
                        DownloadedPath = item.DownloadedPath,
                        FirstSeenUtc = item.FirstSeenUtc == default ? DateTime.UtcNow : item.FirstSeenUtc,
                        LastSeenUtc = item.LastSeenUtc == default ? DateTime.UtcNow : item.LastSeenUtc,
                    });
                    continue;
                }

                var changed = false;
                if (row.MatchedPhotoAssetId == null && matchedId != null)
                {
                    row.MatchedPhotoAssetId = matchedId;
                    row.MatchMethod = item.MatchMethod;
                    row.MatchDistance = item.MatchDistance;
                    row.Status = PhotoGoogleItemStatus.Matched;
                    changed = true;
                }
                else if (row.Status != status && row.MatchedPhotoAssetId == null)
                {
                    row.Status = status;
                    if (status == PhotoGoogleItemStatus.Downloaded && item.DownloadedPath != null)
                        row.DownloadedPath = item.DownloadedPath;
                    changed = true;
                }

                // A flagged disagreement is the pass's question to a human, so a restore SUPPLIES one
                // the local database does not have and never erases one it does: the local row's flags
                // came from a mesh run against the current library, which is newer information than any
                // export can be.
                if (row.Disagreements == null && item.Disagreements != null)
                {
                    row.Disagreements = item.Disagreements;
                    changed = true;
                }
                if (changed) section.Updated++; else section.Skipped++;
            }
        }

        /// <summary>
        /// The review batches (§2.5/§2.9). Identity is (kind, batch id) — the same pair the unique index
        /// uses — so re-importing an export upserts rather than duplicating a night's ingest approval.
        ///
        /// <para><b>A decision is never un-made by a restore.</b> Where the local batch has already been
        /// decided and the export has not, the local verdict stands and the difference is counted;
        /// where the local one is still pending, the export's verdict is applied, which is what
        /// restoring an afternoon of review means. Items are added, never removed: a proposal that grew
        /// since the export is still the same proposal.</para>
        /// </summary>
        private async Task ImportCurationBatchesAsync(MovieDb db, PhotoCurationImportReport report, List<PhotoCurationBatchExport> items)
        {
            var section = report.For(PhotoCurationExportFormat.CurationBatchesFile);
            var resolver = await ResolverAsync(db, items.SelectMany(b => b.Items.Select(i => i.Asset)));

            var batchIds = items.Select(b => b.BatchId).Distinct().ToList();
            var local = await db.PhotoCurationBatches
                .Where(b => batchIds.Contains(b.BatchId))
                .ToListAsync();

            var userNames = items.Where(b => b.DecidedByUserName != null).Select(b => b.DecidedByUserName!).Distinct().ToList();
            var users = userNames.Count == 0
                ? new List<User>()
                : await db.Users.Where(u => u.Username != null && userNames.Contains(u.Username)).ToListAsync();

            foreach (var item in items)
            {
                section.Examined++;
                var kind = ParseEnum(item.Kind, PhotoCurationBatchKind.HideProposal);
                var status = ParseEnum(item.Status, PhotoCurationBatchStatus.Pending);
                var decidedBy = item.DecidedByUserName == null
                    ? null
                    : users.FirstOrDefault(u => string.Equals(u.Username, item.DecidedByUserName, StringComparison.OrdinalIgnoreCase))?.UserID;

                var row = local.FirstOrDefault(b => b.Kind == kind
                    && string.Equals(b.BatchId, item.BatchId, StringComparison.OrdinalIgnoreCase));

                if (row == null)
                {
                    section.Created++;
                    section.Add("items-created", item.Items.Count(i => resolver.Resolve(i.Asset).Asset != null));
                    section.Add("items-unmatched", item.Items.Count(i => resolver.Resolve(i.Asset).Asset == null));
                    if (!apply) { section.Note($"would create batch: {item.Kind}/{item.BatchId}"); continue; }

                    row = new PhotoCurationBatch
                    {
                        Kind = kind,
                        BatchId = item.BatchId,
                        Status = status,
                        CreatedUtc = item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc,
                        DecidedUtc = item.DecidedUtc,
                        DecidedByUserId = decidedBy,
                        AppliedCount = item.AppliedCount,
                        Cursor = item.Cursor,
                        Complete = item.Complete,
                    };
                    db.PhotoCurationBatches.Add(row);
                    await db.SaveChangesAsync();
                    local.Add(row);

                    foreach (var entry in item.Items)
                    {
                        var asset = resolver.Resolve(entry.Asset).Asset;
                        if (asset == null) continue;
                        db.PhotoCurationBatchItems.Add(new PhotoCurationBatchItem
                        {
                            PhotoCurationBatchId = row.Id,
                            PhotoAssetId = asset.Id,
                            Path = entry.Asset.Path,
                            Sha256 = entry.Asset.Sha256,
                            Rule = entry.Rule,
                        });
                    }
                    continue;
                }

                var changed = false;
                if (row.Status == PhotoCurationBatchStatus.Pending && status != PhotoCurationBatchStatus.Pending)
                {
                    row.Status = status;
                    row.DecidedUtc = item.DecidedUtc;
                    row.DecidedByUserId = decidedBy;
                    row.AppliedCount = item.AppliedCount;
                    changed = true;
                    section.Add("decision-restored");
                }
                else if (row.Status != PhotoCurationBatchStatus.Pending && status != row.Status)
                {
                    section.Add("kept-local-decision");
                }

                var existingAssets = new HashSet<int>(await db.PhotoCurationBatchItems
                    .Where(i => i.PhotoCurationBatchId == row.Id)
                    .Select(i => i.PhotoAssetId)
                    .ToListAsync());
                foreach (var entry in item.Items)
                {
                    var asset = resolver.Resolve(entry.Asset).Asset;
                    if (asset == null) { section.Add("items-unmatched"); continue; }
                    if (!existingAssets.Add(asset.Id)) continue;

                    section.Add("items-created");
                    changed = true;
                    if (!apply) continue;
                    db.PhotoCurationBatchItems.Add(new PhotoCurationBatchItem
                    {
                        PhotoCurationBatchId = row.Id,
                        PhotoAssetId = asset.Id,
                        Path = entry.Asset.Path,
                        Sha256 = entry.Asset.Sha256,
                        Rule = entry.Rule,
                    });
                }

                if (changed) section.Updated++; else section.Skipped++;
            }
        }

        // ── Asset resolution (§2.11: content hash first, path second) ────────────────────────────

        private sealed class AssetMatch
        {
            public PhotoAsset? Asset;
            public bool Ambiguous;
        }

        /// <summary>
        /// Resolves the batch's asset keys in ONE pair of queries rather than one query per row, and
        /// answers from the loaded candidates thereafter. Bounded by the batch, so a hundred-thousand
        /// row export never loads the asset table.
        /// </summary>
        private sealed class BatchResolver
        {
            private readonly Dictionary<string, List<PhotoAsset>> bySha = new Dictionary<string, List<PhotoAsset>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, PhotoAsset> byPath = new Dictionary<string, PhotoAsset>(StringComparer.OrdinalIgnoreCase);

            public BatchResolver(IEnumerable<PhotoAsset> candidates)
            {
                foreach (var asset in candidates)
                {
                    if (!string.IsNullOrEmpty(asset.Sha256))
                    {
                        if (!bySha.TryGetValue(asset.Sha256!, out var list)) bySha[asset.Sha256!] = list = new List<PhotoAsset>();
                        list.Add(asset);
                    }
                    byPath[asset.Path] = asset;
                }
            }

            public AssetMatch Resolve(PhotoAssetKey key)
            {
                if (!string.IsNullOrEmpty(key.Sha256) && bySha.TryGetValue(key.Sha256!, out var list))
                {
                    if (list.Count == 1) return new AssetMatch { Asset = list[0] };
                    // Several local rows share the content. The path breaks the tie when it can; when
                    // it cannot, this is reported rather than guessed (§2.5's ambiguity stance).
                    var exact = list.FirstOrDefault(a => string.Equals(a.Path, key.Path, StringComparison.OrdinalIgnoreCase));
                    return exact != null ? new AssetMatch { Asset = exact } : new AssetMatch { Ambiguous = true };
                }

                return byPath.TryGetValue(key.Path, out var byName)
                    ? new AssetMatch { Asset = byName }
                    : new AssetMatch();
            }
        }

        private async Task<BatchResolver> ResolverAsync(MovieDb db, IEnumerable<PhotoAssetKey> keys)
        {
            var list = keys.ToList();
            var shas = list.Where(k => !string.IsNullOrEmpty(k.Sha256)).Select(k => k.Sha256!).Distinct().ToList();
            var paths = list.Select(k => k.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            if (shas.Count == 0 && paths.Count == 0) return new BatchResolver(Array.Empty<PhotoAsset>());

            var candidates = await db.PhotoAssets
                .Where(a => (a.Sha256 != null && shas.Contains(a.Sha256)) || paths.Contains(a.Path))
                .ToListAsync();
            return new BatchResolver(candidates);
        }

        // ── Export files ─────────────────────────────────────────────────────────────────────────

        public bool Exists => File.Exists(Path.Combine(exportDir, PhotoCurationExportFormat.ManifestFile));

        public PhotoExportManifest? Manifest => PhotoCurationExporter.ReadManifest(exportDir);

        private List<T> Section<T>(string section)
        {
            if (sectionCache.TryGetValue(section, out var cached)) return (List<T>)cached;

            var file = Path.Combine(exportDir, section);
            List<T> rows;
            try
            {
                rows = File.Exists(file)
                    ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(file), PhotoCurationExportFormat.Json) ?? new List<T>()
                    : new List<T>();
            }
            catch (JsonException e)
            {
                // A corrupt section is reported, not silently treated as empty: "nothing to restore"
                // and "the backup is damaged" must never look the same.
                throw new InvalidDataException($"Export section {section} could not be read: {e.Message}", e);
            }

            sectionCache[section] = rows;
            return rows;
        }

        private int CountOf(string section) => section switch
        {
            PhotoCurationExportFormat.AssetsFile => Section<PhotoAssetExport>(section).Count,
            PhotoCurationExportFormat.PeopleFile => Section<PhotoPersonExport>(section).Count,
            PhotoCurationExportFormat.PersonTagsFile => Section<PhotoPersonTagExport>(section).Count,
            PhotoCurationExportFormat.AlbumsFile => Section<PhotoAlbumExport>(section).Count,
            PhotoCurationExportFormat.DupeGroupsFile => Section<PhotoDupeGroupExport>(section).Count,
            PhotoCurationExportFormat.GoogleItemsFile => Section<PhotoGoogleItemExport>(section).Count,
            PhotoCurationExportFormat.CurationBatchesFile => Section<PhotoCurationBatchExport>(section).Count,
            _ => 0,
        };

        private List<T> Slice<T>(string section, int index, int take) =>
            Section<T>(section).Skip(index).Take(take).ToList();

        // ── Cursor ───────────────────────────────────────────────────────────────────────────────

        public static string FormatCursor((int section, int index) position) =>
            position.section.ToString(CultureInfo.InvariantCulture) + ":" + position.index.ToString(CultureInfo.InvariantCulture);

        private static (int section, int index) ParseCursor(string? cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return (0, 0);
            var parts = cursor!.Split(':');
            var section = parts.Length > 0 && int.TryParse(parts[0], out var s) ? s : 0;
            var index = parts.Length > 1 && int.TryParse(parts[1], out var i) ? i : 0;
            return (Math.Max(0, section), Math.Max(0, index));
        }

        private static T ParseEnum<T>(string? value, T fallback) where T : struct =>
            Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(typeof(T), parsed) ? parsed : fallback;
    }
}
