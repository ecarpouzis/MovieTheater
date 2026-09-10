-- A3: containers (CollectionNode.TrackRole = 1) with ContainsCount = 0 whose winning span
--     names issues the library DOES hold in the same series.
-- A4: main-tier ReadingOrderEntry rows on collection-shaped files.
.mode list
.separator '|'

CREATE TEMP VIEW s AS
SELECT ItemId, Source, IssueStart, IssueEnd FROM CollectedEditionSpan
WHERE IssueStart IS NOT NULL AND IssueEnd IS NOT NULL;
CREATE TEMP VIEW winspan AS
SELECT s.ItemId, s.Source, s.IssueStart, s.IssueEnd FROM s
JOIN (SELECT ItemId, min(Source) w FROM s GROUP BY ItemId) m ON m.ItemId = s.ItemId AND m.w = s.Source;

-- issue numbers the library actually holds, per series, from the reading order's main tier
CREATE TEMP TABLE held AS
SELECT ro.SeriesId, ro.ReadNumber AS n FROM ReadingOrderEntry ro
JOIN ComicDetail cd ON cd.ItemId = ro.ItemId
WHERE ro.ReadNumber IS NOT NULL AND ro.ReadTier = 0
  AND coalesce(cd.IsCollection,0) = 0;
CREATE INDEX ix_held ON held(SeriesId, n);

SELECT '--- A3 containers total / with ContainsCount 0 ---';
SELECT count(*) FROM CollectionNode WHERE TrackRole = 1;
SELECT count(*) FROM CollectionNode WHERE TrackRole = 1 AND ContainsCount = 0;

SELECT '--- A3 empty containers that HAVE a winning span ---';
SELECT count(*) FROM CollectionNode cn JOIN winspan w ON w.ItemId = cn.ItemId
WHERE cn.TrackRole = 1 AND cn.ContainsCount = 0;

SELECT '--- A3 ATTRIBUTION FAILURES: empty container, span covers issues we hold ---';
SELECT count(*) AS items, count(DISTINCT cn.SeriesId) AS series FROM CollectionNode cn
JOIN winspan w ON w.ItemId = cn.ItemId
WHERE cn.TrackRole = 1 AND cn.ContainsCount = 0
  AND EXISTS (SELECT 1 FROM held h WHERE h.SeriesId = cn.SeriesId AND h.n BETWEEN w.IssueStart AND w.IssueEnd);

SELECT '--- A3 by winning span source ---';
SELECT w.Source, count(*) FROM CollectionNode cn JOIN winspan w ON w.ItemId = cn.ItemId
WHERE cn.TrackRole = 1 AND cn.ContainsCount = 0
  AND EXISTS (SELECT 1 FROM held h WHERE h.SeriesId = cn.SeriesId AND h.n BETWEEN w.IssueStart AND w.IssueEnd)
GROUP BY 1 ORDER BY 2 DESC;

SELECT '--- A3 samples ---';
SELECT cn.ItemId, cn.SeriesId, cn.Level, coalesce(cn.SpanLabel,'') AS label, w.Source AS spanSrc,
       (SELECT count(*) FROM held h WHERE h.SeriesId=cn.SeriesId AND h.n BETWEEN w.IssueStart AND w.IssueEnd) AS heldInRange,
       i.FileName
FROM CollectionNode cn JOIN winspan w ON w.ItemId = cn.ItemId JOIN Item i ON i.Id = cn.ItemId
WHERE cn.TrackRole = 1 AND cn.ContainsCount = 0
  AND EXISTS (SELECT 1 FROM held h WHERE h.SeriesId=cn.SeriesId AND h.n BETWEEN w.IssueStart AND w.IssueEnd)
ORDER BY heldInRange DESC LIMIT 10;

SELECT '--- A4 main-tier reading-order rows on collection-shaped files ---';
SELECT count(*) AS rows, count(DISTINCT ro.SeriesId) AS series
FROM ReadingOrderEntry ro JOIN Item i ON i.Id = ro.ItemId
WHERE ro.ReadTier = 0 AND ro.Source <> 7
  AND i.FileName NOT LIKE '%#%'
  AND i.FileName REGEXP '(\b([Vv]ol(ume)?|[Bb]ook|[Bb]k)\.? *#?[0-9]+|\b[Tt][Pp][Bb]\b|\bHC\b|\b[Oo]mnibus\b)';

SELECT '--- A4 ... restricted to those parsed Format=0 & IsCollection=0 (the A1 population) ---';
SELECT count(*) AS rows, count(DISTINCT ro.SeriesId) AS series
FROM ReadingOrderEntry ro JOIN Item i ON i.Id = ro.ItemId JOIN ComicDetail cd ON cd.ItemId = i.Id
WHERE ro.ReadTier = 0 AND cd.Format = 0 AND cd.IsCollection = 0
  AND i.FileName NOT LIKE '%#%'
  AND i.FileName REGEXP '(\b([Vv]ol(ume)?|[Bb]ook|[Bb]k)\.? *#?[0-9]+|\b[Tt][Pp][Bb]\b|\bHC\b|\b[Oo]mnibus\b)';

SELECT '--- A4 ... AND in a series with a real issue run (>=3 orderable main rows): true damage ---';
SELECT count(*) AS rows, count(DISTINCT ro.SeriesId) AS series FROM ReadingOrderEntry ro
JOIN Item i ON i.Id = ro.ItemId JOIN ComicDetail cd ON cd.ItemId = i.Id
JOIN (SELECT SeriesId, count(*) c FROM ReadingOrderEntry WHERE ReadTier=0 AND ReadIndex IS NOT NULL GROUP BY 1 HAVING c>=3) r
  ON r.SeriesId = ro.SeriesId
WHERE ro.ReadTier = 0 AND cd.Format = 0 AND cd.IsCollection = 0
  AND i.FileName NOT LIKE '%#%'
  AND i.FileName REGEXP '(\b([Vv]ol(ume)?|[Bb]ook|[Bb]k)\.? *#?[0-9]+|\b[Tt][Pp][Bb]\b|\bHC\b|\b[Oo]mnibus\b)';

SELECT '--- A4 samples: collection interleaved with issues ---';
SELECT ro.SeriesId, ro.ItemId, ro.ReadIndex, ro.ReadNumber, ro.Source, i.FileName
FROM ReadingOrderEntry ro JOIN Item i ON i.Id=ro.ItemId JOIN ComicDetail cd ON cd.ItemId=i.Id
WHERE ro.ReadTier = 0 AND cd.Format = 0 AND cd.IsCollection = 0 AND ro.Source <> 7
  AND i.FileName NOT LIKE '%#%'
  AND i.FileName REGEXP '(\b([Vv]ol(ume)?|[Bb]ook|[Bb]k)\.? *#?[0-9]+|\b[Oo]mnibus\b)'
ORDER BY i.PageCount DESC LIMIT 10;

SELECT '--- A4 how many of those already carry a CollectedEditionSpan (C3 belt-and-braces) ---';
SELECT count(*) FROM ReadingOrderEntry ro JOIN Item i ON i.Id=ro.ItemId JOIN ComicDetail cd ON cd.ItemId=i.Id
JOIN winspan w ON w.ItemId = ro.ItemId
WHERE ro.ReadTier=0 AND ro.Source <> 7 AND cd.Format=0 AND cd.IsCollection=0
  AND i.FileName NOT LIKE '%#%'
  AND i.FileName REGEXP '(\b([Vv]ol(ume)?|[Bb]ook|[Bb]k)\.? *#?[0-9]+|\b[Tt][Pp][Bb]\b|\bHC\b|\b[Oo]mnibus\b)';
