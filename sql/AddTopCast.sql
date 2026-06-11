-- ============================================================================
-- AddTopCast: add Movie.TopCast (a read cache of the top-billed actor names,
-- derived from the MovieCredit FK cast) and backfill it for existing movies.
-- Additive: only a new nullable column + an UPDATE that writes that column.
-- ============================================================================
IF COL_LENGTH('Movie', 'TopCast') IS NULL
    ALTER TABLE [Movie] ADD [TopCast] nvarchar(max) NULL;
GO

-- Backfill from the normalized cast: top 6 actors (Role=0) in billing order.
UPDATE m
SET m.[TopCast] = x.[TopCast]
FROM [Movie] m
CROSS APPLY (
    SELECT STRING_AGG(t.[DisplayName], ', ') WITHIN GROUP (ORDER BY t.[Ordering]) AS [TopCast]
    FROM (
        SELECT TOP (6) p.[DisplayName], mc.[Ordering]
        FROM [MovieCredit] mc
        JOIN [Person] p ON p.[Id] = mc.[PersonId]
        WHERE mc.[MovieID] = m.[id] AND mc.[Role] = 0
        ORDER BY mc.[Ordering]
    ) t
) x;
GO

IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260611172228_AddTopCast')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260611172228_AddTopCast', N'8.0.0');
GO
