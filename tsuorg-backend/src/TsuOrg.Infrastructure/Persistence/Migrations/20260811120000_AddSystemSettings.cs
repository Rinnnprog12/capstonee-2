using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TsuOrg.Infrastructure.Persistence.Migrations;

/// <summary>
/// Figure 29 — SOU Admin System Settings & Configuration.
/// Key/value store for global platform toggles and compliance thresholds.
/// </summary>
[DbContext(typeof(TsuOrgDbContext))]
[Migration("20260811120000_AddSystemSettings")]
public partial class AddSystemSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SystemSettings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "char(36)", nullable: false),
                Key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                Value = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SystemSettings", x => x.Id);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_SystemSettings_Key",
            table: "SystemSettings",
            column: "Key",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SystemSettings");
    }
}
