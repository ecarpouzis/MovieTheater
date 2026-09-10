-- A2: CollectedEditionSpan conflicts.
-- EditionSource: Locg 0, Gcd 1, Cv 2, Curated 3.  Precedence Locg > Gcd > Cv > Curated
-- => winner rank: Locg=0 best, then Gcd, then Cv, then Curated  (i.e. lowest Source int wins)
.mode list
.separator '|'

CREATE TEMP VIEW s AS
SELECT ItemId, Source, IssueStart, IssueEnd, coalesce(Confidence,-1) AS Conf, coalesce(Note,'') AS Note,
       coalesce(EditionTitle,'') AS Title, SeriesId
FROM CollectedEditionSpan WHERE IssueStart IS NOT NULL AND IssueEnd IS NOT NULL;

CREATE TEMP VIEW win AS
SELECT ItemId, min(Source) AS WinSource FROM s GROUP BY ItemId;

SELECT '--- A2 span census by source ---';
SELECT Source, count(*) FROM s GROUP BY 1 ORDER BY 1;

SELECT '--- A2 items with 2+ non-null spans ---';
SELECT count(*) FROM (SELECT ItemId FROM s GROUP BY ItemId HAVING count(*) > 1);

SELECT '--- A2 items whose sources DISAGREE on (start,end) ---';
SELECT count(*) FROM (
  SELECT ItemId FROM s GROUP BY ItemId
  HAVING count(*) > 1 AND count(DISTINCT IssueStart || '/' || IssueEnd) > 1);

SELECT '--- A2 disagreeing: winning source distribution ---';
SELECT w.WinSource, count(*) FROM win w JOIN (
  SELECT ItemId FROM s GROUP BY ItemId
  HAVING count(*) > 1 AND count(DISTINCT IssueStart || '/' || IssueEnd) > 1) d ON d.ItemId = w.ItemId
GROUP BY 1 ORDER BY 1;

SELECT '--- A2 winner has LOWER Confidence than some loser (disagreeing items) ---';
SELECT count(DISTINCT wn.ItemId) FROM s wn JOIN win w ON w.ItemId = wn.ItemId AND w.WinSource = wn.Source
JOIN s lo ON lo.ItemId = wn.ItemId AND lo.Source <> wn.Source
JOIN (SELECT ItemId FROM s GROUP BY ItemId HAVING count(*)>1 AND count(DISTINCT IssueStart||'/'||IssueEnd)>1) d ON d.ItemId = wn.ItemId
WHERE lo.Conf > wn.Conf AND wn.Conf >= 0;

SELECT '--- A2 winner is GCD and loser is Cv with a WIDER span (paperback beats deluxe) ---';
SELECT count(DISTINCT wn.ItemId) FROM s wn JOIN win w ON w.ItemId=wn.ItemId AND w.WinSource=wn.Source
JOIN s lo ON lo.ItemId=wn.ItemId AND lo.Source <> wn.Source
WHERE wn.Source = 1 AND lo.Source = 2 AND (lo.IssueEnd - lo.IssueStart) > (wn.IssueEnd - wn.IssueStart);

SELECT '--- A2 ... and the GCD winner is paperback-shaped (span 4..8 issues) ---';
SELECT count(DISTINCT wn.ItemId) FROM s wn JOIN win w ON w.ItemId=wn.ItemId AND w.WinSource=wn.Source
JOIN s lo ON lo.ItemId=wn.ItemId AND lo.Source <> wn.Source
WHERE wn.Source = 1 AND lo.Source = 2
  AND (wn.IssueEnd - wn.IssueStart + 1) BETWEEN 4 AND 8
  AND (lo.IssueEnd - lo.IssueStart + 1) >= 2 * (wn.IssueEnd - wn.IssueStart + 1);

SELECT '--- A2 GCD spans whose ItemProviderLink(Gcd) MatchedKey is a BARE ISSUE NUMBER ---';
SELECT count(*) FROM CollectedEditionSpan ces
JOIN ItemProviderLink l ON l.ItemId = ces.ItemId AND l.Provider = 3
WHERE ces.Source = 1 AND l.MatchedKey REGEXP '^[0-9]+(\.[0-9]+)?$';

SELECT '--- A2 ... of those, how many are on a collection-shaped file (A1 regex) ---';
SELECT count(*) FROM CollectedEditionSpan ces
JOIN ItemProviderLink l ON l.ItemId = ces.ItemId AND l.Provider = 3
JOIN Item i ON i.Id = ces.ItemId
WHERE ces.Source = 1 AND l.MatchedKey REGEXP '^[0-9]+(\.[0-9]+)?$'
  AND i.FileName NOT LIKE '%#%'
  AND i.FileName REGEXP '(\b([Vv]ol(ume)?|[Bb]ook|[Bb]k)\.? *#?[0-9]+|\b[Tt][Pp][Bb]\b|\bHC\b|\b[Oo]mnibus\b)';

SELECT '--- A2 samples: disagreeing items, winner vs losers ---';
SELECT wn.ItemId, i.FileName, 'WIN src=' || wn.Source || ' #' || CAST(wn.IssueStart AS INT) || '-' || CAST(wn.IssueEnd AS INT) || ' conf=' || wn.Conf,
       'LOSE src=' || lo.Source || ' #' || CAST(lo.IssueStart AS INT) || '-' || CAST(lo.IssueEnd AS INT) || ' conf=' || lo.Conf || ' [' || lo.Title || ']'
FROM s wn JOIN win w ON w.ItemId=wn.ItemId AND w.WinSource=wn.Source
JOIN s lo ON lo.ItemId=wn.ItemId AND lo.Source<>wn.Source
JOIN Item i ON i.Id = wn.ItemId
WHERE (lo.IssueStart <> wn.IssueStart OR lo.IssueEnd <> wn.IssueEnd)
ORDER BY (lo.IssueEnd-lo.IssueStart) - (wn.IssueEnd-wn.IssueStart) DESC LIMIT 12;
