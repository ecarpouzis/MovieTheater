-- ============================================================================
-- Apply the additive "AddNormalizedImdbData" migration to an EXISTING MovieSite
-- database that was created out-of-band (no prior EF migrations).
--
-- This script is idempotent and ADDITIVE ONLY: it creates the EF history table,
-- marks the pre-existing schema (InitialBaseline) as already-applied WITHOUT
-- running it, then adds the new nullable Movie columns + the 4 new tables.
-- No existing table, column, or row is altered. Posters are untouched.
--
-- RUN A DATABASE BACKUP FIRST. Then run this whole file against MovieSite.
-- ============================================================================

-- 1) EF migrations history table (created by EF on first managed migration).
IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

-- 2) Baseline marker: tell EF the existing schema is already in place so it does
--    NOT try to re-create Movie/User/Boardgame/etc.
IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175720_InitialBaseline')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260610175720_InitialBaseline', N'8.0.0');
GO

-- 3) The additive delta (new columns + new tables). Idempotent: re-running is a no-op.
BEGIN TRANSACTION;
GO

IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    ALTER TABLE [Movie] ADD [ImdbNeedsReview] bit NOT NULL DEFAULT CAST(0 AS bit);
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    ALTER TABLE [Movie] ADD [ImdbRatingScraped] decimal(18,2) NULL;
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    ALTER TABLE [Movie] ADD [ImdbReleaseDate] datetime2 NULL;
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    ALTER TABLE [Movie] ADD [ImdbReviewReason] nvarchar(max) NULL;
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    ALTER TABLE [Movie] ADD [ImdbScrapedTitle] nvarchar(max) NULL;
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    ALTER TABLE [Movie] ADD [ImdbVerifiedDate] datetime2 NULL;
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    ALTER TABLE [Movie] ADD [MpaaRating] nvarchar(max) NULL;
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    ALTER TABLE [Movie] ADD [PlotFull] nvarchar(max) NULL;
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    ALTER TABLE [Movie] ADD [RuntimeMinutes] int NULL;
GO

IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    CREATE TABLE [Genre] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(100) NOT NULL,
        CONSTRAINT [PK_Genre] PRIMARY KEY ([Id])
    );
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    CREATE TABLE [Person] (
        [ImdbNameId] nvarchar(20) NOT NULL,
        [DisplayName] nvarchar(max) NULL,
        [PrimaryProfessions] nvarchar(max) NULL,
        CONSTRAINT [PK_Person] PRIMARY KEY ([ImdbNameId])
    );
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    CREATE TABLE [MovieGenre] (
        [MovieID] int NOT NULL,
        [GenreId] int NOT NULL,
        [Ordering] int NOT NULL,
        CONSTRAINT [PK_MovieGenre] PRIMARY KEY ([MovieID], [GenreId]),
        CONSTRAINT [FK_MovieGenre_Genre_GenreId] FOREIGN KEY ([GenreId]) REFERENCES [Genre] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_MovieGenre_Movie_MovieID] FOREIGN KEY ([MovieID]) REFERENCES [Movie] ([id]) ON DELETE CASCADE
    );
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    CREATE TABLE [MovieCredit] (
        [Id] int NOT NULL IDENTITY,
        [MovieID] int NOT NULL,
        [PersonImdbNameId] nvarchar(20) NOT NULL,
        [Role] int NOT NULL,
        [Ordering] int NOT NULL,
        [Character] nvarchar(max) NULL,
        CONSTRAINT [PK_MovieCredit] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_MovieCredit_Movie_MovieID] FOREIGN KEY ([MovieID]) REFERENCES [Movie] ([id]) ON DELETE CASCADE,
        CONSTRAINT [FK_MovieCredit_Person_PersonImdbNameId] FOREIGN KEY ([PersonImdbNameId]) REFERENCES [Person] ([ImdbNameId]) ON DELETE NO ACTION
    );
GO

IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    CREATE UNIQUE INDEX [IX_Genre_Name] ON [Genre] ([Name]);
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    CREATE UNIQUE INDEX [IX_MovieCredit_MovieID_PersonImdbNameId_Role] ON [MovieCredit] ([MovieID], [PersonImdbNameId], [Role]);
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    CREATE INDEX [IX_MovieCredit_PersonImdbNameId] ON [MovieCredit] ([PersonImdbNameId]);
GO
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    CREATE INDEX [IX_MovieGenre_GenreId] ON [MovieGenre] ([GenreId]);
GO

IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260610175738_AddNormalizedImdbData', N'8.0.0');
GO

COMMIT;
GO
