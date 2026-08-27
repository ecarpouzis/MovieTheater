using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Db;

namespace MovieTheater.Web
{
    /// <summary>
    /// The photo rail's query model (R9 S2c): every facet the Photos rail can set, as repeatable query
    /// params (<c>?album=summer-2019&amp;person=4&amp;exPerson=9&amp;kind=video&amp;camera=iPhone%2012&amp;yearMin=2015&amp;q=beach</c>).
    /// The SPA writes these from the catalog's URL contract (<c>f=token:value</c> / <c>x=token:value</c> /
    /// <c>y=</c> / <c>q=</c>); the same shape rides the offset browse, the grouped browse and the facet
    /// counts, so they cannot disagree.
    /// </summary>
    public sealed class PhotoBrowseFilterQuery
    {
        public string? q { get; set; }
        /// <summary>Album slugs (the family shelf's albums).</summary>
        public string[]? album { get; set; }
        public string[]? exAlbum { get; set; }
        /// <summary>FamilyPerson ids — affirmed tags only (Manual / Confirmed); a suggestion is a question.</summary>
        public int[]? person { get; set; }
        public int[]? exPerson { get; set; }
        /// <summary>photo | video.</summary>
        public string? kind { get; set; }
        /// <summary>Exact <see cref="PhotoAsset.CameraModel"/> values.</summary>
        public string[]? camera { get; set; }
        public string[]? exCamera { get; set; }
        public int? yearMin { get; set; }
        public int? yearMax { get; set; }
    }

    /// <summary>
    /// The combinable filter behind the Photos facet rail: ANDed includes (every named album must hold
    /// the photo, every named person must be in it), NOTed excludes, the kind, the camera, the year
    /// range and a text over the path / location. Pure: it narrows the caller's ALREADY-GATED timeline
    /// query (shelf, missing, hidden, dupe-collapse, quarantine live in the controller), so it runs
    /// against SQLite in the tests as written.
    /// </summary>
    public sealed class PhotoBrowseFilter
    {
        public string Q { get; init; } = "";
        public IReadOnlyList<string> Albums { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ExAlbums { get; init; } = Array.Empty<string>();
        public IReadOnlyList<int> Persons { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> ExPersons { get; init; } = Array.Empty<int>();
        public PhotoAssetKind? Kind { get; init; }
        public IReadOnlyList<string> Cameras { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ExCameras { get; init; } = Array.Empty<string>();
        public int? YearMin { get; init; }
        public int? YearMax { get; init; }

        public static readonly PhotoBrowseFilter Empty = new();

        public bool IsEmpty =>
            Q.Length == 0 && Albums.Count == 0 && ExAlbums.Count == 0 && Persons.Count == 0 && ExPersons.Count == 0
            && Kind == null && Cameras.Count == 0 && ExCameras.Count == 0 && YearMin == null && YearMax == null;

        private static IReadOnlyList<string> Clean(string[]? values) =>
            (values ?? Array.Empty<string>()).Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private static IReadOnlyList<int> CleanIds(int[]? values) =>
            (values ?? Array.Empty<int>()).Where(v => v > 0).Distinct().ToList();

        public static PhotoBrowseFilter Parse(PhotoBrowseFilterQuery? query)
        {
            if (query == null) return Empty;
            PhotoAssetKind? kind = (query.kind ?? "").Trim().ToLowerInvariant() switch
            {
                "photo" => PhotoAssetKind.Photo,
                "video" => PhotoAssetKind.Video,
                _ => null,
            };
            var yearMin = query.yearMin is int a && a > 0 ? a : (int?)null;
            var yearMax = query.yearMax is int b && b > 0 ? b : (int?)null;
            if (yearMin != null && yearMax != null && yearMin > yearMax) (yearMin, yearMax) = (yearMax, yearMin);
            return new PhotoBrowseFilter
            {
                Q = (query.q ?? "").Trim(),
                Albums = Clean(query.album),
                ExAlbums = Clean(query.exAlbum),
                Persons = CleanIds(query.person),
                ExPersons = CleanIds(query.exPerson),
                Kind = kind,
                Cameras = Clean(query.camera),
                ExCameras = Clean(query.exCamera),
                YearMin = yearMin,
                YearMax = yearMax,
            };
        }

        /// <summary>Narrow <paramref name="query"/> (the gated timeline rows) to what the filter keeps.</summary>
        public IQueryable<PhotoAsset> Apply(IQueryable<PhotoAsset> query, MovieDb db)
        {
            if (IsEmpty) return query;
            if (Q.Length > 0)
            {
                var q = Q;
                query = query.Where(a => a.Path.Contains(q) || (a.LocationLabel != null && a.LocationLabel.Contains(q)));
            }
            if (YearMin is int min) query = query.Where(a => a.TakenAt != null && a.TakenAt.Value.Year >= min);
            if (YearMax is int max) query = query.Where(a => a.TakenAt != null && a.TakenAt.Value.Year <= max);
            if (Kind is PhotoAssetKind kind) query = query.Where(a => a.Kind == kind);
            foreach (var slug in Albums)
            {
                var s = slug;
                query = query.Where(a => db.PhotoAlbumEntries.Any(e => e.PhotoAssetId == a.Id && e.PhotoAlbum.Slug == s));
            }
            foreach (var slug in ExAlbums)
            {
                var s = slug;
                query = query.Where(a => !db.PhotoAlbumEntries.Any(e => e.PhotoAssetId == a.Id && e.PhotoAlbum.Slug == s));
            }
            foreach (var personId in Persons)
            {
                var id = personId;
                query = query.Where(a => db.PhotoPersonTags.Any(t => t.PhotoAssetId == a.Id && t.FamilyPersonId == id
                    && (t.Source == PhotoTagSource.Manual || t.Source == PhotoTagSource.Confirmed)));
            }
            foreach (var personId in ExPersons)
            {
                var id = personId;
                query = query.Where(a => !db.PhotoPersonTags.Any(t => t.PhotoAssetId == a.Id && t.FamilyPersonId == id
                    && (t.Source == PhotoTagSource.Manual || t.Source == PhotoTagSource.Confirmed)));
            }
            if (Cameras.Count > 0)
            {
                var cameras = Cameras.ToList();
                query = query.Where(a => a.CameraModel != null && cameras.Contains(a.CameraModel));
            }
            if (ExCameras.Count > 0)
            {
                var cameras = ExCameras.ToList();
                query = query.Where(a => a.CameraModel == null || !cameras.Contains(a.CameraModel));
            }
            return query;
        }
    }
}
