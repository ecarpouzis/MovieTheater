BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    ALTER TABLE [Movie] ADD [ImdbNeedsReview] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    ALTER TABLE [Movie] ADD [ImdbRatingScraped] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    ALTER TABLE [Movie] ADD [ImdbReleaseDate] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    ALTER TABLE [Movie] ADD [ImdbReviewReason] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    ALTER TABLE [Movie] ADD [ImdbScrapedTitle] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    ALTER TABLE [Movie] ADD [ImdbVerifiedDate] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    ALTER TABLE [Movie] ADD [MpaaRating] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    ALTER TABLE [Movie] ADD [PlotFull] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    ALTER TABLE [Movie] ADD [RuntimeMinutes] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    CREATE TABLE [Genre] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(100) NOT NULL,
        CONSTRAINT [PK_Genre] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    CREATE TABLE [Person] (
        [ImdbNameId] nvarchar(20) NOT NULL,
        [DisplayName] nvarchar(max) NULL,
        [PrimaryProfessions] nvarchar(max) NULL,
        CONSTRAINT [PK_Person] PRIMARY KEY ([ImdbNameId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    CREATE TABLE [MovieGenre] (
        [MovieID] int NOT NULL,
        [GenreId] int NOT NULL,
        [Ordering] int NOT NULL,
        CONSTRAINT [PK_MovieGenre] PRIMARY KEY ([MovieID], [GenreId]),
        CONSTRAINT [FK_MovieGenre_Genre_GenreId] FOREIGN KEY ([GenreId]) REFERENCES [Genre] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_MovieGenre_Movie_MovieID] FOREIGN KEY ([MovieID]) REFERENCES [Movie] ([id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
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
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Genre_Name] ON [Genre] ([Name]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MovieCredit_MovieID_PersonImdbNameId_Role] ON [MovieCredit] ([MovieID], [PersonImdbNameId], [Role]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    CREATE INDEX [IX_MovieCredit_PersonImdbNameId] ON [MovieCredit] ([PersonImdbNameId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    CREATE INDEX [IX_MovieGenre_GenreId] ON [MovieGenre] ([GenreId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260610175738_AddNormalizedImdbData'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260610175738_AddNormalizedImdbData', N'8.0.0');
END;
GO

COMMIT;
GO

