-- A1: collection-shaped filenames still parsed as SingleIssue / IsCollection = 0
-- rxColl = Vol./Volume/Book/Bk + N, or bare TPB / HC / Omnibus
-- "no # issue token" = FileName has no '#'
.headers on
.mode list
.separator '|'

CREATE TEMP VIEW coll AS
SELECT i.Id AS ItemId, i.SeriesId, i.FileName, coalesce(i.PageCount,0) AS PageCount,
       cd.Format, cd.IsCollection, cd.IssueSource, cd.IssueNo, cd.VolumeNo, cd.FormatRaw
FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
  AND i.FileName NOT LIKE '%#%'
  AND i.FileName REGEXP '(([Vv]ol(ume)?|[Bb]ook|[Bb]k)\.? *#?[0-9]+|[Tt][Pp][Bb]|[Hh][Cc]|[Oo]mnibus)';

SELECT '--- A1 total collection-shaped, no # ---';
SELECT count(*) AS shaped FROM coll;

SELECT '--- A1 misparsed (Format=0 AND IsCollection=0) ---';
SELECT count(*) AS misparsed, count(DISTINCT SeriesId) AS series
FROM coll WHERE Format = 0 AND IsCollection = 0;

SELECT '--- A1 by IssueSource (0 None,1 Metadata,2 MetadataAlt,3 Filename,4 FilenameLeadingIndex,5 Folder) ---';
SELECT IssueSource, count(*) AS n, count(DISTINCT SeriesId) AS series
FROM coll WHERE Format = 0 AND IsCollection = 0 GROUP BY IssueSource ORDER BY n DESC;

SELECT '--- A1 by FormatRaw ---';
SELECT coalesce(FormatRaw,'(null)') AS formatRaw, count(*) AS n
FROM coll WHERE Format = 0 AND IsCollection = 0 GROUP BY 1 ORDER BY n DESC LIMIT 15;

SELECT '--- A1 by PageCount band ---';
SELECT CASE WHEN PageCount < 60 THEN '1 <60' WHEN PageCount <= 120 THEN '2 60-120' ELSE '3 >120' END AS band,
       count(*) AS n, count(DISTINCT SeriesId) AS series
FROM coll WHERE Format = 0 AND IsCollection = 0 GROUP BY 1 ORDER BY 1;

SELECT '--- A1 IssueSource x band ---';
SELECT IssueSource,
       CASE WHEN PageCount < 60 THEN '1 <60' WHEN PageCount <= 120 THEN '2 60-120' ELSE '3 >120' END AS band,
       count(*) AS n
FROM coll WHERE Format = 0 AND IsCollection = 0 GROUP BY 1,2 ORDER BY 1,2;

SELECT '--- A1 samples (metadata-sourced issue) ---';
SELECT ItemId, SeriesId, PageCount, IssueSource, IssueNo, VolumeNo, coalesce(FormatRaw,''), FileName
FROM coll WHERE Format = 0 AND IsCollection = 0 AND IssueSource IN (1,2) ORDER BY PageCount DESC LIMIT 10;

SELECT '--- A1 samples (filename-sourced issue) ---';
SELECT ItemId, SeriesId, PageCount, IssueSource, IssueNo, VolumeNo, coalesce(FormatRaw,''), FileName
FROM coll WHERE Format = 0 AND IsCollection = 0 AND IssueSource NOT IN (1,2) ORDER BY PageCount DESC LIMIT 10;
