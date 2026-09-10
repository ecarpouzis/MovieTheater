-- Per-series before/after: reading order + containment, one line per item.
-- usage: sqlite3 <db> -cmd ".param set :sid 14966" < series_report.sql
.mode list
.separator |
SELECT 'sid=' || :sid || ' name=' || coalesce((SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = :sid), '?');
SELECT 'idx|tier|num|sfx|src|lvl|role|contains|span|label|spanSrc|pages|file|parent|readDate';
SELECT coalesce(CAST(ro.ReadIndex AS TEXT), '-')
    || '|' || coalesce(CAST(ro.ReadTier AS TEXT), '-')
    || '|' || coalesce(printf('%g', ro.ReadNumber), '-')
    || '|' || coalesce(printf('%g', ro.ReadNumberSuffix), '-')
    || '|' || coalesce(CAST(ro.Source AS TEXT), '-')
    || '|' || coalesce(CAST(cn.Level AS TEXT), '-')
    || '|' || coalesce(CAST(cn.TrackRole AS TEXT), '-')
    || '|' || coalesce(CAST(cn.ContainsCount AS TEXT), '-')
    || '|' || coalesce(CAST(cn.SpanStart AS TEXT), '-') || '-' || coalesce(CAST(cn.SpanEnd AS TEXT), '-')
    || '|' || coalesce(cn.SpanLabel, '-')
    || '|' || coalesce(CAST(cn.SpanSource AS TEXT), '-')
    || '|' || coalesce(CAST(i.PageCount AS TEXT), '-')
    || '|' || i.FileName
    -- appended 2026-09-10: the report could not see the two facts the nesting tie-break and the
    -- container-date rule change, so it passed byte-identical through both fixes.
    || '|' || coalesce(CAST(cn.ParentItemId AS TEXT), '-')
    || '|' || coalesce(ro.ReadDate, '-')
FROM Item i
LEFT JOIN ReadingOrderEntry ro ON ro.ItemId = i.Id
LEFT JOIN CollectionNode cn ON cn.ItemId = i.Id
WHERE i.SeriesId = :sid AND i.Kind = 0 AND coalesce(i.IsExcluded, 0) = 0
ORDER BY (ro.ReadIndex IS NULL), ro.ReadIndex, i.Id;
