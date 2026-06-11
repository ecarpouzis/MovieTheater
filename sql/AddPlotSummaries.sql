BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610182559_AddPlotSummaries'
)
BEGIN
    ALTER TABLE [Movie] ADD [PlotSynopsis] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610182559_AddPlotSummaries'
)
BEGIN
    CREATE TABLE [MoviePlotSummary] (
        [Id] int NOT NULL IDENTITY,
        [MovieID] int NOT NULL,
        [Ordering] int NOT NULL,
        [Author] nvarchar(max) NULL,
        [Text] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_MoviePlotSummary] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_MoviePlotSummary_Movie_MovieID] FOREIGN KEY ([MovieID]) REFERENCES [Movie] ([id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610182559_AddPlotSummaries'
)
BEGIN
    CREATE INDEX [IX_MoviePlotSummary_MovieID] ON [MoviePlotSummary] ([MovieID]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610182559_AddPlotSummaries'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260610182559_AddPlotSummaries', N'8.0.0');
END;
GO

COMMIT;
GO

