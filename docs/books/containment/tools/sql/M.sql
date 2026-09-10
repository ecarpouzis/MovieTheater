-- Library-wide containment metrics: run before and after the repair.
.mode list
.separator '|'

SELECT '--- M1 spans by source (0 Locg 1 Gcd 2 Cv 3 Curated) ---';
SELECT Source, count(*) FROM CollectedEditionSpan WHERE IssueStart IS NOT NULL AND IssueEnd IS NOT NULL GROUP BY 1 ORDER BY 1;

SELECT '--- M1b spans carrying an explicit match-by: title note ---';
SELECT Source, count(*) FROM CollectedEditionSpan WHERE Note LIKE '%match-by: title%' GROUP BY 1 ORDER BY 1;

SELECT '--- M2 items >=100pp with NO span from any source ---';
SELECT count(*) FROM Item i
WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0 AND coalesce(i.PageCount,0) >= 100
  AND NOT EXISTS (SELECT 1 FROM CollectedEditionSpan s WHERE s.ItemId = i.Id AND s.IssueStart IS NOT NULL AND s.IssueEnd IS NOT NULL);

SELECT '--- M2b items >=100pp total ---';
SELECT count(*) FROM Item WHERE Kind = 0 AND coalesce(IsExcluded,0) = 0 AND coalesce(PageCount,0) >= 100;

SELECT '--- M3 issue-keyed GCD spans (match-by: num, or a bare-numeric GCD MatchedKey with no note) ---';
SELECT count(*) FROM CollectedEditionSpan ces
LEFT JOIN ItemProviderLink l ON l.ItemId = ces.ItemId AND l.Provider = 3
WHERE ces.Source = 1
  AND (coalesce(ces.Note,'') LIKE '%match-by: num%'
       OR (coalesce(ces.Note,'') NOT LIKE '%match-by:%' AND l.MatchedKey REGEXP '^[0-9]+(\.[0-9]+)?$'));

SELECT '--- M3b issue-keyed GCD spans on items >=100pp ---';
SELECT count(*) FROM CollectedEditionSpan ces
JOIN Item i ON i.Id = ces.ItemId
LEFT JOIN ItemProviderLink l ON l.ItemId = ces.ItemId AND l.Provider = 3
WHERE ces.Source = 1 AND coalesce(i.PageCount,0) >= 100
  AND (coalesce(ces.Note,'') LIKE '%match-by: num%'
       OR (coalesce(ces.Note,'') NOT LIKE '%match-by:%' AND l.MatchedKey REGEXP '^[0-9]+(\.[0-9]+)?$'));

SELECT '--- M4 winning SpanSource on containers (0 None 1 Inferred 2 Cv 3 Gcd 4 Locg 5 Curated) ---';
SELECT SpanSource, count(*) FROM CollectionNode WHERE TrackRole = 1 GROUP BY 1 ORDER BY 2 DESC;

SELECT '--- M5 winners that lost to a HIGHER-confidence rival ---';
SELECT count(DISTINCT cn.ItemId) FROM CollectionNode cn
JOIN CollectedEditionSpan w ON w.ItemId = cn.ItemId
 AND w.Source = CASE cn.SpanSource WHEN 4 THEN 0 WHEN 3 THEN 1 WHEN 2 THEN 2 WHEN 5 THEN 3 END
JOIN CollectedEditionSpan lo ON lo.ItemId = cn.ItemId AND lo.Source <> w.Source
WHERE cn.TrackRole = 1 AND coalesce(lo.Confidence,-1) > coalesce(w.Confidence,-1);

SELECT '--- M6 containers with a real span (ContainsCount > 0) / total containers ---';
SELECT sum(CASE WHEN ContainsCount > 0 THEN 1 ELSE 0 END), count(*) FROM CollectionNode WHERE TrackRole = 1;

SELECT '--- M7 LOCG links pointing at a comic with NO containment edges, on items >=100pp ---';
SELECT count(*) FROM ItemProviderLink l JOIN Item i ON i.Id = l.ItemId
WHERE l.Provider = 2 AND l.Status = 1 AND coalesce(i.PageCount,0) >= 100;

SELECT '--- M8 page-count audit flags on the winning span ---';
SELECT sum(CASE WHEN w.Note LIKE '%page-audit: thin%' THEN 1 ELSE 0 END),
       sum(CASE WHEN w.Note LIKE '%page-audit: thick%' THEN 1 ELSE 0 END)
FROM CollectionNode cn JOIN CollectedEditionSpan w ON w.ItemId = cn.ItemId
 AND w.Source = CASE cn.SpanSource WHEN 4 THEN 0 WHEN 3 THEN 1 WHEN 2 THEN 2 WHEN 5 THEN 3 END
WHERE cn.TrackRole = 1;
