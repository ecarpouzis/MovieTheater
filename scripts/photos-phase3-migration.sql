-- Session settings, so a re-run needs no `sqlcmd -I`: SQL Server refuses filtered indexes and
-- indexed/computed columns under the OFF defaults some clients connect with (the QUOTED_IDENTIFIER
-- trap this repo has hit before). Prepended only -- no DDL below this line was changed.
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [PhotoCurationBatch] (
    [Id] int NOT NULL IDENTITY,
    [Kind] int NOT NULL,
    [BatchId] nvarchar(128) NOT NULL,
    [Status] int NOT NULL,
    [CreatedUtc] datetime2 NOT NULL,
    [DecidedUtc] datetime2 NULL,
    [DecidedByUserId] int NULL,
    [AppliedCount] int NOT NULL,
    [Cursor] nvarchar(128) NULL,
    [Complete] bit NOT NULL,
    CONSTRAINT [PK_PhotoCurationBatch] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PhotoCurationBatch_Users_DecidedByUserId] FOREIGN KEY ([DecidedByUserId]) REFERENCES [Users] ([UserID]) ON DELETE NO ACTION
);
GO

CREATE TABLE [PhotoCurationBatchItem] (
    [Id] int NOT NULL IDENTITY,
    [PhotoCurationBatchId] int NOT NULL,
    [PhotoAssetId] int NOT NULL,
    [Path] nvarchar(850) NOT NULL,
    [Sha256] nvarchar(64) NULL,
    [Rule] nvarchar(64) NOT NULL,
    CONSTRAINT [PK_PhotoCurationBatchItem] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PhotoCurationBatchItem_PhotoAsset_PhotoAssetId] FOREIGN KEY ([PhotoAssetId]) REFERENCES [PhotoAsset] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PhotoCurationBatchItem_PhotoCurationBatch_PhotoCurationBatchId] FOREIGN KEY ([PhotoCurationBatchId]) REFERENCES [PhotoCurationBatch] ([Id]) ON DELETE CASCADE
);
GO

CREATE INDEX [IX_PhotoCurationBatch_DecidedByUserId] ON [PhotoCurationBatch] ([DecidedByUserId]);
GO

CREATE UNIQUE INDEX [IX_PhotoCurationBatch_Kind_BatchId] ON [PhotoCurationBatch] ([Kind], [BatchId]);
GO

CREATE INDEX [IX_PhotoCurationBatch_Kind_Status_CreatedUtc] ON [PhotoCurationBatch] ([Kind], [Status], [CreatedUtc]);
GO

CREATE INDEX [IX_PhotoCurationBatchItem_PhotoAssetId] ON [PhotoCurationBatchItem] ([PhotoAssetId]);
GO

CREATE UNIQUE INDEX [IX_PhotoCurationBatchItem_PhotoCurationBatchId_PhotoAssetId] ON [PhotoCurationBatchItem] ([PhotoCurationBatchId], [PhotoAssetId]);
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260812175434_AddPhotoCurationBatches', N'8.0.22');
GO

COMMIT;
GO

