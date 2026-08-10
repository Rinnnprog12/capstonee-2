using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TsuOrg.Infrastructure.Persistence.Migrations;

/// <summary>
/// Figure 10 — Submission Confirmation locks metadata and stores a SHA-256 audit hash.
/// </summary>
[DbContext(typeof(TsuOrgDbContext))]
[Migration("20260810001500_AddDocumentMetadataLock")]
public partial class AddDocumentMetadataLock : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsMetadataLocked",
            table: "Documents",
            type: "tinyint(1)",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "MetadataLockHash",
            table: "Documents",
            type: "varchar(64)",
            maxLength: 64,
            nullable: true)
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "MetadataLockedAt",
            table: "Documents",
            type: "datetime(6)",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Documents_IsMetadataLocked",
            table: "Documents",
            column: "IsMetadataLocked");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Documents_IsMetadataLocked",
            table: "Documents");

        migrationBuilder.DropColumn(name: "IsMetadataLocked", table: "Documents");
        migrationBuilder.DropColumn(name: "MetadataLockHash", table: "Documents");
        migrationBuilder.DropColumn(name: "MetadataLockedAt", table: "Documents");
    }
}
