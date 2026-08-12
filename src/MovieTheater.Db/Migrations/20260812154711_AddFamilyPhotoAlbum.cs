using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddFamilyPhotoAlbum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhotoAsset",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Path = table.Column<string>(type: "nvarchar(850)", maxLength: 850, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FileModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PHash = table.Column<long>(type: "bigint", nullable: true),
                    DHash = table.Column<long>(type: "bigint", nullable: true),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    DurationSec = table.Column<double>(type: "float", nullable: true),
                    TakenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TakenAtUtcRaw = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TakenAtSource = table.Column<int>(type: "int", nullable: false),
                    YearMin = table.Column<int>(type: "int", nullable: true),
                    YearMax = table.Column<int>(type: "int", nullable: true),
                    GpsLat = table.Column<double>(type: "float", nullable: true),
                    GpsLon = table.Column<double>(type: "float", nullable: true),
                    LocationLabel = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LocationSource = table.Column<int>(type: "int", nullable: false),
                    CameraMake = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CameraModel = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OriginalRenderable = table.Column<bool>(type: "bit", nullable: false),
                    RawMetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Hidden = table.Column<bool>(type: "bit", nullable: false),
                    IngestBatch = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    JellyfinItemId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ImmichAssetId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MissingSinceUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoAsset", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PhotoDupeGroup",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoDupeGroup", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoDupeGroup_Users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FamilyPerson",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BirthYear = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    CoverAssetId = table.Column<int>(type: "int", nullable: true),
                    ImmichPersonId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilyPerson", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamilyPerson_PhotoAsset_CoverAssetId",
                        column: x => x.CoverAssetId,
                        principalTable: "PhotoAsset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FamilyPerson_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhotoAlbum",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CoverAssetId = table.Column<int>(type: "int", nullable: true),
                    RangeStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RangeEnd = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoAlbum", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoAlbum_PhotoAsset_CoverAssetId",
                        column: x => x.CoverAssetId,
                        principalTable: "PhotoAsset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhotoAlbum_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhotoGoogleItem",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TakeoutFileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TakeoutRelativePath = table.Column<string>(type: "nvarchar(850)", maxLength: 850, nullable: true),
                    TakenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    SidecarJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MatchedPhotoAssetId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    MatchMethod = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoGoogleItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoGoogleItem_PhotoAsset_MatchedPhotoAssetId",
                        column: x => x.MatchedPhotoAssetId,
                        principalTable: "PhotoAsset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhotoDupeMember",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhotoDupeGroupId = table.Column<int>(type: "int", nullable: false),
                    PhotoAssetId = table.Column<int>(type: "int", nullable: false),
                    IsMaster = table.Column<bool>(type: "bit", nullable: false),
                    Similarity = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoDupeMember", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoDupeMember_PhotoAsset_PhotoAssetId",
                        column: x => x.PhotoAssetId,
                        principalTable: "PhotoAsset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhotoDupeMember_PhotoDupeGroup_PhotoDupeGroupId",
                        column: x => x.PhotoDupeGroupId,
                        principalTable: "PhotoDupeGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhotoPersonTag",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhotoAssetId = table.Column<int>(type: "int", nullable: false),
                    FamilyPersonId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: true),
                    BoxX = table.Column<double>(type: "float", nullable: true),
                    BoxY = table.Column<double>(type: "float", nullable: true),
                    BoxW = table.Column<double>(type: "float", nullable: true),
                    BoxH = table.Column<double>(type: "float", nullable: true),
                    ImmichPersonId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConfirmedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoPersonTag", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoPersonTag_FamilyPerson_FamilyPersonId",
                        column: x => x.FamilyPersonId,
                        principalTable: "FamilyPerson",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhotoPersonTag_PhotoAsset_PhotoAssetId",
                        column: x => x.PhotoAssetId,
                        principalTable: "PhotoAsset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhotoAlbumEntry",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhotoAlbumId = table.Column<int>(type: "int", nullable: false),
                    PhotoAssetId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Caption = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoAlbumEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoAlbumEntry_PhotoAlbum_PhotoAlbumId",
                        column: x => x.PhotoAlbumId,
                        principalTable: "PhotoAlbum",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PhotoAlbumEntry_PhotoAsset_PhotoAssetId",
                        column: x => x.PhotoAssetId,
                        principalTable: "PhotoAsset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FamilyPerson_CoverAssetId",
                table: "FamilyPerson",
                column: "CoverAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_FamilyPerson_ImmichPersonId",
                table: "FamilyPerson",
                column: "ImmichPersonId",
                unique: true,
                filter: "[ImmichPersonId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FamilyPerson_Name",
                table: "FamilyPerson",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_FamilyPerson_UserId",
                table: "FamilyPerson",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbum_CoverAssetId",
                table: "PhotoAlbum",
                column: "CoverAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbum_CreatedByUserId",
                table: "PhotoAlbum",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbum_Slug",
                table: "PhotoAlbum",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbumEntry_PhotoAlbumId_PhotoAssetId",
                table: "PhotoAlbumEntry",
                columns: new[] { "PhotoAlbumId", "PhotoAssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbumEntry_PhotoAlbumId_SortOrder",
                table: "PhotoAlbumEntry",
                columns: new[] { "PhotoAlbumId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAlbumEntry_PhotoAssetId",
                table: "PhotoAlbumEntry",
                column: "PhotoAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_Hidden_TakenAt",
                table: "PhotoAsset",
                columns: new[] { "Hidden", "TakenAt" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "Path", "Kind", "Width", "Height", "DurationSec", "TakenAtSource", "MissingSinceUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_IngestBatch",
                table: "PhotoAsset",
                column: "IngestBatch");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_JellyfinItemId",
                table: "PhotoAsset",
                column: "JellyfinItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_MissingSinceUtc",
                table: "PhotoAsset",
                column: "MissingSinceUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_Path",
                table: "PhotoAsset",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_PHash",
                table: "PhotoAsset",
                column: "PHash");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_Sha256",
                table: "PhotoAsset",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoDupeGroup_ResolvedByUserId",
                table: "PhotoDupeGroup",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoDupeGroup_Status_Kind",
                table: "PhotoDupeGroup",
                columns: new[] { "Status", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoDupeMember_Master",
                table: "PhotoDupeMember",
                column: "PhotoDupeGroupId",
                unique: true,
                filter: "[IsMaster] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoDupeMember_PhotoAssetId_IsMaster",
                table: "PhotoDupeMember",
                columns: new[] { "PhotoAssetId", "IsMaster" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoDupeMember_PhotoDupeGroupId_PhotoAssetId",
                table: "PhotoDupeMember",
                columns: new[] { "PhotoDupeGroupId", "PhotoAssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoGoogleItem_MatchedPhotoAssetId",
                table: "PhotoGoogleItem",
                column: "MatchedPhotoAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoGoogleItem_Status",
                table: "PhotoGoogleItem",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoGoogleItem_TakeoutFileName_TakenAtUtc_SizeBytes",
                table: "PhotoGoogleItem",
                columns: new[] { "TakeoutFileName", "TakenAtUtc", "SizeBytes" },
                unique: true,
                filter: "[TakenAtUtc] IS NOT NULL AND [SizeBytes] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoPersonTag_FamilyPersonId_Source",
                table: "PhotoPersonTag",
                columns: new[] { "FamilyPersonId", "Source" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoPersonTag_PhotoAssetId_FamilyPersonId",
                table: "PhotoPersonTag",
                columns: new[] { "PhotoAssetId", "FamilyPersonId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotoAlbumEntry");

            migrationBuilder.DropTable(
                name: "PhotoDupeMember");

            migrationBuilder.DropTable(
                name: "PhotoGoogleItem");

            migrationBuilder.DropTable(
                name: "PhotoPersonTag");

            migrationBuilder.DropTable(
                name: "PhotoAlbum");

            migrationBuilder.DropTable(
                name: "PhotoDupeGroup");

            migrationBuilder.DropTable(
                name: "FamilyPerson");

            migrationBuilder.DropTable(
                name: "PhotoAsset");
        }
    }
}
