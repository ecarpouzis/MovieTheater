-- ============================================================================
-- RedesignPerson: re-key Person from ImdbNameId(string) to a synthetic int Id,
-- so people that come from APIs / manual text (which have no IMDB nm id) can be
-- stored. DATA-PRESERVING and transactional: existing scraped rows keep their nm
-- (now in the nullable, unique ImdbNameId column) and every MovieCredit is
-- re-pointed to the new Person.Id. No table is dropped.
--
-- Safety: restore-point copies Person_backup_20260611 / MovieCredit_backup_20260611
-- were taken first. Run inside one transaction; THROW aborts + rolls back on any gap.
-- ============================================================================
SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;
GO

-- 1) Remove MovieCredit's dependencies on Person(ImdbNameId).
ALTER TABLE [MovieCredit] DROP CONSTRAINT [FK_MovieCredit_Person_PersonImdbNameId];
DROP INDEX [IX_MovieCredit_MovieID_PersonImdbNameId_Role] ON [MovieCredit];
DROP INDEX [IX_MovieCredit_PersonImdbNameId] ON [MovieCredit];
GO

-- 2) Person: add synthetic Id (future PK) and NameKey; backfill NameKey.
ALTER TABLE [Person] ADD [Id] int IDENTITY(1,1) NOT NULL;
ALTER TABLE [Person] ADD [NameKey] nvarchar(200) NULL;
GO
UPDATE [Person] SET [NameKey] = LOWER(LTRIM(RTRIM([DisplayName]))) WHERE [DisplayName] IS NOT NULL;
GO

-- 3) Swap Person PK: drop PK on ImdbNameId, make it nullable+unique, PK on Id.
ALTER TABLE [Person] DROP CONSTRAINT [PK_Person];
ALTER TABLE [Person] ALTER COLUMN [ImdbNameId] nvarchar(20) NULL;
ALTER TABLE [Person] ADD CONSTRAINT [PK_Person] PRIMARY KEY ([Id]);
GO
CREATE UNIQUE INDEX [IX_Person_ImdbNameId] ON [Person] ([ImdbNameId]) WHERE [ImdbNameId] IS NOT NULL;
CREATE INDEX [IX_Person_NameKey] ON [Person] ([NameKey]);
GO

-- 4) MovieCredit: add PersonId, backfill from the old nm link, enforce.
ALTER TABLE [MovieCredit] ADD [PersonId] int NULL;
GO
UPDATE mc SET mc.[PersonId] = p.[Id]
FROM [MovieCredit] mc
JOIN [Person] p ON p.[ImdbNameId] = mc.[PersonImdbNameId];
GO
IF EXISTS (SELECT 1 FROM [MovieCredit] WHERE [PersonId] IS NULL)
    THROW 50000, 'RedesignPerson: some MovieCredit rows did not map to a Person; aborting.', 1;
GO
ALTER TABLE [MovieCredit] ALTER COLUMN [PersonId] int NOT NULL;
ALTER TABLE [MovieCredit] ADD CONSTRAINT [FK_MovieCredit_Person_PersonId]
    FOREIGN KEY ([PersonId]) REFERENCES [Person]([Id]);
ALTER TABLE [MovieCredit] DROP COLUMN [PersonImdbNameId];
GO
CREATE UNIQUE INDEX [IX_MovieCredit_MovieID_PersonId_Role] ON [MovieCredit] ([MovieID], [PersonId], [Role]);
CREATE INDEX [IX_MovieCredit_PersonId] ON [MovieCredit] ([PersonId]);
GO

COMMIT;
GO
