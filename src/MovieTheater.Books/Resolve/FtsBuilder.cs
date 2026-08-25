using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// ItemFts bodies: the resolved title, series, creators, publisher and the synopsis the pointer names
    /// (today's field set — v2-model.md §19 Q3). The synopsis text is read from its leg at index time, so the
    /// index carries prose without any table having copied it.
    /// </summary>
    public static class FtsBuilder
    {
        private const string BodySql = @"
SELECT i.Id,
       coalesce(i.ResolvedTitle, i.Title, ''), coalesce(i.ResolvedSeries, ''), coalesce(i.ResolvedCreatorsCsv, ''), coalesce(i.ResolvedPublisher, ''),
       CASE i.ResolvedSynopsisSource
            WHEN 1 THEN cv.Description
            WHEN 2 THEN coalesce(ce.Summary, bd.Description)
            WHEN 3 THEN lc.Description
            WHEN 4 THEN ew.Description
            WHEN 5 THEN mu.Description
            WHEN 6 THEN cv.Deck
            WHEN 7 THEN coalesce(bi.Synopsis, si.Synopsis)
            ELSE NULL END
FROM Item i
LEFT JOIN Series s ON s.Id = i.SeriesId
LEFT JOIN CvVolume cv ON cv.Id = s.CvVolumeId
LEFT JOIN ExternalWork ew ON ew.Id = s.ExternalWorkId
LEFT JOIN MuSeries mu ON mu.Id = s.MuSeriesId
LEFT JOIN ComicEmbedded ce ON ce.ItemId = i.Id
LEFT JOIN BookDetail bd ON bd.ItemId = i.Id
LEFT JOIN ItemProviderLink lk ON lk.ItemId = i.Id AND lk.Provider = 2 AND lk.Status = 1 AND lk.Quality IN (2, 3)
LEFT JOIN LocgComic lc ON lc.LocgComicId = CAST(lk.ProviderKey AS INTEGER)
LEFT JOIN Insight si ON si.SubjectKind = 1 AND si.SubjectId = i.SeriesId AND si.IsCurrent = 1
LEFT JOIN Insight bi ON bi.SubjectKind = 0 AND bi.SubjectId = i.Id AND bi.IsCurrent = 1
WHERE i.Id > $after ORDER BY i.Id LIMIT $n";

        /// <summary>Index one chunk of items after <paramref name="afterId"/>; returns the last id indexed.</summary>
        public static long IndexBatch(TargetWriter hot, long afterId, int batchSize, out int indexed)
        {
            indexed = 0;
            var last = afterId;
            var rows = new List<(long id, string body)>();
            using (var cmd = hot.CreateCommand(BodySql))
            {
                cmd.Parameters.AddWithValue("$after", afterId);
                cmd.Parameters.AddWithValue("$n", batchSize);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var parts = new List<string>();
                    for (var i = 1; i <= 4; i++) { var v = rd.GetString(i); if (v.Length > 0) parts.Add(v); }
                    if (!rd.IsDBNull(5)) { var syn = SynopsisRules.StripHtml(rd.GetString(5)); if (syn.Length > 0) parts.Add(syn); }
                    rows.Add((rd.GetInt64(0), string.Join(" \n ", parts)));
                }
            }
            foreach (var (id, body) in rows)
            {
                hot.Exec("DELETE FROM ItemFts WHERE rowid = $id", ("$id", id));
                hot.Exec(ItemFts.InsertSql, ("$id", id), ("$body", body));
                last = id;
                indexed++;
            }
            return last;
        }
    }
}
