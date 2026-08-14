BEGIN TRANSACTION;
GO

ALTER TABLE [MediaFile] ADD [ContentFingerprint] nvarchar(64) NULL;
GO

CREATE TABLE [MediaKeyframes] (
    [Fingerprint] nvarchar(64) NOT NULL,
    [TotalDurationTicks] bigint NOT NULL,
    [KeyframeTicks] nvarchar(max) NOT NULL,
    [SizeBytes] bigint NOT NULL,
    [SourceItemId] nvarchar(64) NULL,
    [CapturedUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_MediaKeyframes] PRIMARY KEY ([Fingerprint])
);
GO

CREATE INDEX [IX_MediaFile_ContentFingerprint] ON [MediaFile] ([ContentFingerprint]) WHERE [ContentFingerprint] IS NOT NULL;
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260814031146_AddKeyframeCustody', N'8.0.22');
GO

COMMIT;
GO

