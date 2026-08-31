-- The raw listener count behind MusicTrack.Popularity (2026-08-31).
--
-- The 0-100 score is logarithmic, so neighbouring scores can hide an enormous audience gap (73 and
-- 50 on one album are 112,303 listeners and 2,905). Storing the count lets a tracklist show the
-- real drop, and makes a future re-tune of the scale an UPDATE instead of a re-parse.
--
-- Idempotent, purely additive: one nullable column, every existing row keeps NULL.

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF COL_LENGTH('dbo.MusicTrack', 'PopularityListeners') IS NULL
BEGIN
    ALTER TABLE dbo.MusicTrack ADD PopularityListeners bigint NULL;
    PRINT 'added MusicTrack.PopularityListeners';
END
ELSE PRINT 'MusicTrack.PopularityListeners already present';
GO

SELECT COUNT(*) AS Scored,
       SUM(CASE WHEN PopularityListeners IS NOT NULL THEN 1 ELSE 0 END) AS WithListeners
FROM dbo.MusicTrack WHERE Popularity IS NOT NULL;
GO
