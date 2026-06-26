BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    ALTER TABLE [Channel] ADD [CatalogKey] nvarchar(64) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    ALTER TABLE [Channel] ADD [Category] nvarchar(48) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    ALTER TABLE [Channel] ADD [LogoPath] nvarchar(256) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    ALTER TABLE [Channel] ADD [RotationJson] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    ALTER TABLE [Channel] ADD [ScheduleStrategy] nvarchar(32) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    ALTER TABLE [Channel] ADD [SeasonEndDay] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    ALTER TABLE [Channel] ADD [SeasonEndMonth] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    ALTER TABLE [Channel] ADD [SeasonStartDay] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    ALTER TABLE [Channel] ADD [SeasonStartMonth] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    CREATE INDEX [IX_TitleInsight_SubjectKind_SubjectId_SpecVersion_GeneratedUtc_Id] ON [TitleInsight] ([SubjectKind], [SubjectId], [SpecVersion], [GeneratedUtc], [Id]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Channel_CatalogKey] ON [Channel] ([CatalogKey]) WHERE [CatalogKey] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260626010740_AddChannelCatalogAndStrategy'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260626010740_AddChannelCatalogAndStrategy', N'8.0.22');
END;
GO

COMMIT;
GO

