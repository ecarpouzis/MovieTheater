-- Session settings, so a re-run needs no `sqlcmd -I`: SQL Server refuses filtered indexes and
-- indexed/computed columns under the OFF defaults some clients connect with (the QUOTED_IDENTIFIER
-- trap this repo has hit before). Prepended only -- no DDL below this line was changed.
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [PhotoAsset] ADD [HashUpdatedUtc] datetime2 NULL;
GO

ALTER TABLE [PhotoAsset] ADD [IngestError] nvarchar(512) NULL;
GO

ALTER TABLE [PhotoAsset] ADD [MetadataUpdatedUtc] datetime2 NULL;
GO

ALTER TABLE [PhotoAsset] ADD [ThumbKey] nvarchar(32) NULL;
GO

ALTER TABLE [PhotoAsset] ADD [ThumbState] int NOT NULL DEFAULT 0;
GO

ALTER TABLE [PhotoAsset] ADD [ThumbVariants] nvarchar(64) NULL;
GO

ALTER TABLE [PhotoAsset] ADD [ThumbsUpdatedUtc] datetime2 NULL;
GO

CREATE INDEX [IX_PhotoAsset_HashQueue] ON [PhotoAsset] ([Id]) WHERE [HashUpdatedUtc] IS NULL;
GO

CREATE INDEX [IX_PhotoAsset_MetadataQueue] ON [PhotoAsset] ([Id]) WHERE [MetadataUpdatedUtc] IS NULL;
GO

CREATE INDEX [IX_PhotoAsset_ThumbQueue] ON [PhotoAsset] ([Id]) WHERE [ThumbsUpdatedUtc] IS NULL;
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260812161930_AddPhotoIngestQueueColumns', N'8.0.22');
GO

COMMIT;
GO

