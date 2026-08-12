-- Session settings, so a re-run needs no `sqlcmd -I`: SQL Server refuses filtered indexes and
-- indexed/computed columns under the OFF defaults some clients connect with (the QUOTED_IDENTIFIER
-- trap this repo has hit before). Prepended only -- no DDL below this line was changed.
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [PhotoAsset] (
    [Id] int NOT NULL IDENTITY,
    [Path] nvarchar(850) NOT NULL,
    [SizeBytes] bigint NOT NULL,
    [FileModifiedUtc] datetime2 NOT NULL,
    [Kind] int NOT NULL,
    [Sha256] nvarchar(64) NULL,
    [PHash] bigint NULL,
    [DHash] bigint NULL,
    [Width] int NULL,
    [Height] int NULL,
    [DurationSec] float NULL,
    [TakenAt] datetime2 NULL,
    [TakenAtUtcRaw] datetime2 NULL,
    [TakenAtSource] int NOT NULL,
    [YearMin] int NULL,
    [YearMax] int NULL,
    [GpsLat] float NULL,
    [GpsLon] float NULL,
    [LocationLabel] nvarchar(256) NULL,
    [LocationSource] int NOT NULL,
    [CameraMake] nvarchar(128) NULL,
    [CameraModel] nvarchar(128) NULL,
    [OriginalRenderable] bit NOT NULL,
    [RawMetadataJson] nvarchar(max) NULL,
    [Hidden] bit NOT NULL,
    [IngestBatch] nvarchar(128) NULL,
    [JellyfinItemId] nvarchar(64) NULL,
    [ImmichAssetId] nvarchar(64) NULL,
    [FirstSeenUtc] datetime2 NOT NULL,
    [MissingSinceUtc] datetime2 NULL,
    CONSTRAINT [PK_PhotoAsset] PRIMARY KEY ([Id])
);
GO

CREATE TABLE [PhotoDupeGroup] (
    [Id] int NOT NULL IDENTITY,
    [Kind] int NOT NULL,
    [Status] int NOT NULL,
    [CreatedUtc] datetime2 NOT NULL,
    [ResolvedUtc] datetime2 NULL,
    [ResolvedByUserId] int NULL,
    CONSTRAINT [PK_PhotoDupeGroup] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PhotoDupeGroup_Users_ResolvedByUserId] FOREIGN KEY ([ResolvedByUserId]) REFERENCES [Users] ([UserID]) ON DELETE NO ACTION
);
GO

CREATE TABLE [FamilyPerson] (
    [Id] int NOT NULL IDENTITY,
    [Name] nvarchar(200) NOT NULL,
    [BirthYear] int NULL,
    [UserId] int NULL,
    [CoverAssetId] int NULL,
    [ImmichPersonId] nvarchar(64) NULL,
    [CreatedUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_FamilyPerson] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_FamilyPerson_PhotoAsset_CoverAssetId] FOREIGN KEY ([CoverAssetId]) REFERENCES [PhotoAsset] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_FamilyPerson_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([UserID]) ON DELETE NO ACTION
);
GO

CREATE TABLE [PhotoAlbum] (
    [Id] int NOT NULL IDENTITY,
    [Title] nvarchar(300) NOT NULL,
    [Slug] nvarchar(200) NOT NULL,
    [Description] nvarchar(max) NULL,
    [CoverAssetId] int NULL,
    [RangeStart] datetime2 NULL,
    [RangeEnd] datetime2 NULL,
    [SortOrder] int NOT NULL,
    [CreatedByUserId] int NULL,
    [CreatedUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_PhotoAlbum] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PhotoAlbum_PhotoAsset_CoverAssetId] FOREIGN KEY ([CoverAssetId]) REFERENCES [PhotoAsset] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PhotoAlbum_Users_CreatedByUserId] FOREIGN KEY ([CreatedByUserId]) REFERENCES [Users] ([UserID]) ON DELETE NO ACTION
);
GO

CREATE TABLE [PhotoGoogleItem] (
    [Id] int NOT NULL IDENTITY,
    [TakeoutFileName] nvarchar(400) NOT NULL,
    [TakeoutRelativePath] nvarchar(850) NULL,
    [TakenAtUtc] datetime2 NULL,
    [SizeBytes] bigint NULL,
    [SidecarJson] nvarchar(max) NULL,
    [MatchedPhotoAssetId] int NULL,
    [Status] int NOT NULL,
    [MatchMethod] nvarchar(32) NULL,
    [FirstSeenUtc] datetime2 NOT NULL,
    [LastSeenUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_PhotoGoogleItem] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PhotoGoogleItem_PhotoAsset_MatchedPhotoAssetId] FOREIGN KEY ([MatchedPhotoAssetId]) REFERENCES [PhotoAsset] ([Id]) ON DELETE NO ACTION
);
GO

CREATE TABLE [PhotoDupeMember] (
    [Id] int NOT NULL IDENTITY,
    [PhotoDupeGroupId] int NOT NULL,
    [PhotoAssetId] int NOT NULL,
    [IsMaster] bit NOT NULL,
    [Similarity] float NULL,
    CONSTRAINT [PK_PhotoDupeMember] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PhotoDupeMember_PhotoAsset_PhotoAssetId] FOREIGN KEY ([PhotoAssetId]) REFERENCES [PhotoAsset] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PhotoDupeMember_PhotoDupeGroup_PhotoDupeGroupId] FOREIGN KEY ([PhotoDupeGroupId]) REFERENCES [PhotoDupeGroup] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [PhotoPersonTag] (
    [Id] int NOT NULL IDENTITY,
    [PhotoAssetId] int NOT NULL,
    [FamilyPersonId] int NOT NULL,
    [Source] int NOT NULL,
    [Confidence] float NULL,
    [BoxX] float NULL,
    [BoxY] float NULL,
    [BoxW] float NULL,
    [BoxH] float NULL,
    [ImmichPersonId] nvarchar(64) NULL,
    [CreatedUtc] datetime2 NOT NULL,
    [ConfirmedUtc] datetime2 NULL,
    CONSTRAINT [PK_PhotoPersonTag] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PhotoPersonTag_FamilyPerson_FamilyPersonId] FOREIGN KEY ([FamilyPersonId]) REFERENCES [FamilyPerson] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PhotoPersonTag_PhotoAsset_PhotoAssetId] FOREIGN KEY ([PhotoAssetId]) REFERENCES [PhotoAsset] ([Id]) ON DELETE NO ACTION
);
GO

CREATE TABLE [PhotoAlbumEntry] (
    [Id] int NOT NULL IDENTITY,
    [PhotoAlbumId] int NOT NULL,
    [PhotoAssetId] int NOT NULL,
    [SortOrder] int NOT NULL,
    [Caption] nvarchar(1000) NULL,
    CONSTRAINT [PK_PhotoAlbumEntry] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PhotoAlbumEntry_PhotoAlbum_PhotoAlbumId] FOREIGN KEY ([PhotoAlbumId]) REFERENCES [PhotoAlbum] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_PhotoAlbumEntry_PhotoAsset_PhotoAssetId] FOREIGN KEY ([PhotoAssetId]) REFERENCES [PhotoAsset] ([Id]) ON DELETE NO ACTION
);
GO

CREATE INDEX [IX_FamilyPerson_CoverAssetId] ON [FamilyPerson] ([CoverAssetId]);
GO

CREATE UNIQUE INDEX [IX_FamilyPerson_ImmichPersonId] ON [FamilyPerson] ([ImmichPersonId]) WHERE [ImmichPersonId] IS NOT NULL;
GO

CREATE INDEX [IX_FamilyPerson_Name] ON [FamilyPerson] ([Name]);
GO

CREATE INDEX [IX_FamilyPerson_UserId] ON [FamilyPerson] ([UserId]);
GO

CREATE INDEX [IX_PhotoAlbum_CoverAssetId] ON [PhotoAlbum] ([CoverAssetId]);
GO

CREATE INDEX [IX_PhotoAlbum_CreatedByUserId] ON [PhotoAlbum] ([CreatedByUserId]);
GO

CREATE UNIQUE INDEX [IX_PhotoAlbum_Slug] ON [PhotoAlbum] ([Slug]);
GO

CREATE UNIQUE INDEX [IX_PhotoAlbumEntry_PhotoAlbumId_PhotoAssetId] ON [PhotoAlbumEntry] ([PhotoAlbumId], [PhotoAssetId]);
GO

CREATE INDEX [IX_PhotoAlbumEntry_PhotoAlbumId_SortOrder] ON [PhotoAlbumEntry] ([PhotoAlbumId], [SortOrder]);
GO

CREATE INDEX [IX_PhotoAlbumEntry_PhotoAssetId] ON [PhotoAlbumEntry] ([PhotoAssetId]);
GO

CREATE INDEX [IX_PhotoAsset_Hidden_TakenAt] ON [PhotoAsset] ([Hidden], [TakenAt] DESC) INCLUDE ([Path], [Kind], [Width], [Height], [DurationSec], [TakenAtSource], [MissingSinceUtc]);
GO

CREATE INDEX [IX_PhotoAsset_IngestBatch] ON [PhotoAsset] ([IngestBatch]);
GO

CREATE INDEX [IX_PhotoAsset_JellyfinItemId] ON [PhotoAsset] ([JellyfinItemId]);
GO

CREATE INDEX [IX_PhotoAsset_MissingSinceUtc] ON [PhotoAsset] ([MissingSinceUtc]);
GO

CREATE UNIQUE INDEX [IX_PhotoAsset_Path] ON [PhotoAsset] ([Path]);
GO

CREATE INDEX [IX_PhotoAsset_PHash] ON [PhotoAsset] ([PHash]);
GO

CREATE INDEX [IX_PhotoAsset_Sha256] ON [PhotoAsset] ([Sha256]);
GO

CREATE INDEX [IX_PhotoDupeGroup_ResolvedByUserId] ON [PhotoDupeGroup] ([ResolvedByUserId]);
GO

CREATE INDEX [IX_PhotoDupeGroup_Status_Kind] ON [PhotoDupeGroup] ([Status], [Kind]);
GO

CREATE UNIQUE INDEX [IX_PhotoDupeMember_Master] ON [PhotoDupeMember] ([PhotoDupeGroupId]) WHERE [IsMaster] = 1;
GO

CREATE INDEX [IX_PhotoDupeMember_PhotoAssetId_IsMaster] ON [PhotoDupeMember] ([PhotoAssetId], [IsMaster]);
GO

CREATE UNIQUE INDEX [IX_PhotoDupeMember_PhotoDupeGroupId_PhotoAssetId] ON [PhotoDupeMember] ([PhotoDupeGroupId], [PhotoAssetId]);
GO

CREATE INDEX [IX_PhotoGoogleItem_MatchedPhotoAssetId] ON [PhotoGoogleItem] ([MatchedPhotoAssetId]);
GO

CREATE INDEX [IX_PhotoGoogleItem_Status] ON [PhotoGoogleItem] ([Status]);
GO

CREATE UNIQUE INDEX [IX_PhotoGoogleItem_TakeoutFileName_TakenAtUtc_SizeBytes] ON [PhotoGoogleItem] ([TakeoutFileName], [TakenAtUtc], [SizeBytes]) WHERE [TakenAtUtc] IS NOT NULL AND [SizeBytes] IS NOT NULL;
GO

CREATE INDEX [IX_PhotoPersonTag_FamilyPersonId_Source] ON [PhotoPersonTag] ([FamilyPersonId], [Source]);
GO

CREATE UNIQUE INDEX [IX_PhotoPersonTag_PhotoAssetId_FamilyPersonId] ON [PhotoPersonTag] ([PhotoAssetId], [FamilyPersonId]);
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260812154711_AddFamilyPhotoAlbum', N'8.0.22');
GO

COMMIT;
GO

