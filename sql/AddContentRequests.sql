-- The request queue (2026-10-06): dbo.ContentRequest + dbo.ContentRequestVote.
--
-- The same DDL as the EF migration 20261006120000_AddContentRequests, written IDEMPOTENTLY so it can be
-- applied by hand to the live database and re-run without harm (the dev connection IS the live prod
-- DB -- see the deploy-db-ops skill). Run it through SqlConnection split on GO, batch by batch, and
-- read the PRINT output back. Purely additive -- two new tables, nothing the deployed site maps
-- changes -- so it can land before the deploy that reads it.
--
-- What it does, in order:
--   1. dbo.ContentRequest: one row per "please add this" -- the section it is for, the title / year /
--      detail / link / notes as typed, the status + resolution, the matcher's standing proposal
--      (MatchKind/MatchId/MatchTitle/MatchFoundUtc, DismissedMatchKey) and what it was fulfilled with.
--      FK to Users (NO_ACTION, like the table's other User FKs on the live DB). Index (Status, CreatedUtc).
--   2. dbo.ContentRequestVote: one "me too" per user per request (unique (RequestId, UserId)); the
--      votes go with their request (CASCADE).
--   3. The __EFMigrationsHistory row.
--
-- APPLIED 2026-10-06 through System.Data.SqlClient.SqlConnection, batch by batch, with a read-back:
--   both tables created (20 + 4 columns, 3 + 2 indexes), history row recorded.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

-- 1. The requests.
IF OBJECT_ID('dbo.ContentRequest', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ContentRequest (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ContentRequest PRIMARY KEY,
        CreatedUtc datetime2 NOT NULL,
        RequestedByUserId int NOT NULL,
        Section nvarchar(16) NOT NULL,
        Title nvarchar(200) NOT NULL,
        [Year] int NULL,
        Detail nvarchar(200) NULL,
        Link nvarchar(500) NULL,
        Notes nvarchar(1000) NULL,
        Status nvarchar(16) NOT NULL,
        ResolvedUtc datetime2 NULL,
        ResolvedByUserId int NULL,
        ResolutionNote nvarchar(400) NULL,
        MatchKind nvarchar(16) NULL,
        MatchId int NULL,
        MatchTitle nvarchar(300) NULL,
        MatchFoundUtc datetime2 NULL,
        DismissedMatchKey nvarchar(40) NULL,
        FulfilledKind nvarchar(16) NULL,
        FulfilledId int NULL,
        CONSTRAINT FK_ContentRequest_Users_RequestedByUserId FOREIGN KEY (RequestedByUserId) REFERENCES dbo.Users (UserID)
    );
    PRINT 'created dbo.ContentRequest';
END
ELSE PRINT 'dbo.ContentRequest already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContentRequest_Status_CreatedUtc' AND object_id = OBJECT_ID('dbo.ContentRequest'))
BEGIN
    CREATE INDEX IX_ContentRequest_Status_CreatedUtc ON dbo.ContentRequest (Status, CreatedUtc);
    PRINT 'created IX_ContentRequest_Status_CreatedUtc';
END
ELSE PRINT 'IX_ContentRequest_Status_CreatedUtc already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContentRequest_RequestedByUserId' AND object_id = OBJECT_ID('dbo.ContentRequest'))
BEGIN
    CREATE INDEX IX_ContentRequest_RequestedByUserId ON dbo.ContentRequest (RequestedByUserId);
    PRINT 'created IX_ContentRequest_RequestedByUserId';
END
ELSE PRINT 'IX_ContentRequest_RequestedByUserId already present';
GO

-- 2. The votes.
IF OBJECT_ID('dbo.ContentRequestVote', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ContentRequestVote (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ContentRequestVote PRIMARY KEY,
        RequestId int NOT NULL,
        UserId int NOT NULL,
        CreatedUtc datetime2 NOT NULL,
        CONSTRAINT FK_ContentRequestVote_ContentRequest_RequestId FOREIGN KEY (RequestId) REFERENCES dbo.ContentRequest (Id) ON DELETE CASCADE
    );
    PRINT 'created dbo.ContentRequestVote';
END
ELSE PRINT 'dbo.ContentRequestVote already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContentRequestVote_RequestId_UserId' AND object_id = OBJECT_ID('dbo.ContentRequestVote'))
BEGIN
    CREATE UNIQUE INDEX IX_ContentRequestVote_RequestId_UserId ON dbo.ContentRequestVote (RequestId, UserId);
    PRINT 'created IX_ContentRequestVote_RequestId_UserId';
END
ELSE PRINT 'IX_ContentRequestVote_RequestId_UserId already present';
GO

-- 3. The history row.
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261006120000_AddContentRequests')
BEGIN
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    SELECT N'20261006120000_AddContentRequests', (SELECT TOP 1 ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC);
    PRINT 'recorded 20261006120000_AddContentRequests';
END
ELSE PRINT '20261006120000_AddContentRequests already recorded';
GO
