using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TsuOrg.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TsuOrgDbContext))]
[Migration("20260809120000_AddUserAvatarBlobPath")]
public partial class AddUserAvatarBlobPath : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AvatarBlobPath",
            table: "UserAccounts",
            type: "varchar(512)",
            maxLength: 512,
            nullable: true)
            .Annotation("MySql:CharSet", "utf8mb4");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AvatarBlobPath",
            table: "UserAccounts");
    }
}
