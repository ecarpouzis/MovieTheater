using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// The live (baselined) Users table was created without IDENTITY on UserID, while the
    /// EF model expects a database-generated key — so inserting a new User (e.g. the
    /// auto-create path in /API/Login) failed with "Cannot insert the value NULL into
    /// column 'UserID'". SQL Server can't add IDENTITY to an existing column, so this
    /// rebuilds the table in place, preserving rows and IDs. Databases created fresh from
    /// migrations already have IDENTITY; the rebuild is harmless there.
    /// </summary>
    public partial class FixUsersIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE dbo.Viewing DROP CONSTRAINT FK_UserID_Users;
ALTER TABLE dbo.UserSettings DROP CONSTRAINT FK_UserSettings_Users_UserID;

CREATE TABLE dbo.Users_New (
    UserID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users_New PRIMARY KEY,
    Username nvarchar(max) NULL,
    LastLogin datetime2 NULL,
    PasswordHash nvarchar(max) NULL
);

SET IDENTITY_INSERT dbo.Users_New ON;
INSERT INTO dbo.Users_New (UserID, Username, LastLogin, PasswordHash)
SELECT UserID, Username, LastLogin, PasswordHash FROM dbo.Users;
SET IDENTITY_INSERT dbo.Users_New OFF;

DROP TABLE dbo.Users;
EXEC sp_rename 'dbo.Users_New', 'Users';
EXEC sp_rename 'dbo.PK_Users_New', 'PK_Users', 'OBJECT';

ALTER TABLE dbo.Viewing WITH CHECK ADD CONSTRAINT FK_UserID_Users
    FOREIGN KEY (UserID) REFERENCES dbo.Users (UserID);
ALTER TABLE dbo.UserSettings WITH CHECK ADD CONSTRAINT FK_UserSettings_Users_UserID
    FOREIGN KEY (UserID) REFERENCES dbo.Users (UserID);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: rebuild without IDENTITY, restoring the pre-fix shape.
            migrationBuilder.Sql(@"
ALTER TABLE dbo.Viewing DROP CONSTRAINT FK_UserID_Users;
ALTER TABLE dbo.UserSettings DROP CONSTRAINT FK_UserSettings_Users_UserID;

CREATE TABLE dbo.Users_New (
    UserID int NOT NULL CONSTRAINT PK_Users_New PRIMARY KEY,
    Username nvarchar(max) NULL,
    LastLogin datetime2 NULL,
    PasswordHash nvarchar(max) NULL
);

INSERT INTO dbo.Users_New (UserID, Username, LastLogin, PasswordHash)
SELECT UserID, Username, LastLogin, PasswordHash FROM dbo.Users;

DROP TABLE dbo.Users;
EXEC sp_rename 'dbo.Users_New', 'Users';
EXEC sp_rename 'dbo.PK_Users_New', 'PK_Users', 'OBJECT';

ALTER TABLE dbo.Viewing WITH CHECK ADD CONSTRAINT FK_UserID_Users
    FOREIGN KEY (UserID) REFERENCES dbo.Users (UserID);
ALTER TABLE dbo.UserSettings WITH CHECK ADD CONSTRAINT FK_UserSettings_Users_UserID
    FOREIGN KEY (UserID) REFERENCES dbo.Users (UserID);
");
        }
    }
}
