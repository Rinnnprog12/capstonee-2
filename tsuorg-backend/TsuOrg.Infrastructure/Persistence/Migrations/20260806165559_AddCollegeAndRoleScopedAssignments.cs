using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TsuOrg.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollegeAndRoleScopedAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Organizations_UserAccounts_OfficerId",
                table: "Organizations");

            migrationBuilder.AddColumn<Guid>(
                name: "CollegeId",
                table: "UserAccounts",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Organizations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Semester",
                table: "Organizations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "longtext",
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Organizations",
                type: "varchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "College",
                table: "Organizations",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "longtext",
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Acronym",
                table: "Organizations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "CollegeId",
                table: "Organizations",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "PrimaryAdviserUserId",
                table: "Organizations",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "OrganizationMemberships",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MembershipRole",
                table: "OrganizationMemberships",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Colleges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Colleges", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            // Create indexes that replace FK-covering ones BEFORE dropping the old index.
            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberships_UserAccountId_MembershipRole_IsActive",
                table: "OrganizationMemberships",
                columns: new[] { "UserAccountId", "MembershipRole", "IsActive" });

            migrationBuilder.DropIndex(
                name: "IX_OrganizationMemberships_UserAccountId",
                table: "OrganizationMemberships");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_CollegeId",
                table: "UserAccounts",
                column: "CollegeId");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_CollegeId",
                table: "Organizations",
                column: "CollegeId");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_PrimaryAdviserUserId",
                table: "Organizations",
                column: "PrimaryAdviserUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberships_OrganizationId_MembershipRole_IsActi~",
                table: "OrganizationMemberships",
                columns: new[] { "OrganizationId", "MembershipRole", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Colleges_Code",
                table: "Colleges",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Organizations_Colleges_CollegeId",
                table: "Organizations",
                column: "CollegeId",
                principalTable: "Colleges",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Organizations_UserAccounts_OfficerId",
                table: "Organizations",
                column: "OfficerId",
                principalTable: "UserAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Organizations_UserAccounts_PrimaryAdviserUserId",
                table: "Organizations",
                column: "PrimaryAdviserUserId",
                principalTable: "UserAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_UserAccounts_Colleges_CollegeId",
                table: "UserAccounts",
                column: "CollegeId",
                principalTable: "Colleges",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Organizations_Colleges_CollegeId",
                table: "Organizations");

            migrationBuilder.DropForeignKey(
                name: "FK_Organizations_UserAccounts_OfficerId",
                table: "Organizations");

            migrationBuilder.DropForeignKey(
                name: "FK_Organizations_UserAccounts_PrimaryAdviserUserId",
                table: "Organizations");

            migrationBuilder.DropForeignKey(
                name: "FK_UserAccounts_Colleges_CollegeId",
                table: "UserAccounts");

            migrationBuilder.DropTable(
                name: "Colleges");

            migrationBuilder.DropIndex(
                name: "IX_UserAccounts_CollegeId",
                table: "UserAccounts");

            migrationBuilder.DropIndex(
                name: "IX_Organizations_CollegeId",
                table: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_Organizations_PrimaryAdviserUserId",
                table: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_OrganizationMemberships_OrganizationId_MembershipRole_IsActi~",
                table: "OrganizationMemberships");

            // Recreate FK-covering index before dropping the composite that includes UserAccountId.
            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberships_UserAccountId",
                table: "OrganizationMemberships",
                column: "UserAccountId");

            migrationBuilder.DropIndex(
                name: "IX_OrganizationMemberships_UserAccountId_MembershipRole_IsActive",
                table: "OrganizationMemberships");

            migrationBuilder.DropColumn(
                name: "CollegeId",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "CollegeId",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "PrimaryAdviserUserId",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "OrganizationMemberships");

            migrationBuilder.DropColumn(
                name: "MembershipRole",
                table: "OrganizationMemberships");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Organizations",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Semester",
                table: "Organizations",
                type: "longtext",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Organizations",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldMaxLength: 256)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "College",
                table: "Organizations",
                type: "longtext",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(128)",
                oldMaxLength: 128,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Acronym",
                table: "Organizations",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddForeignKey(
                name: "FK_Organizations_UserAccounts_OfficerId",
                table: "Organizations",
                column: "OfficerId",
                principalTable: "UserAccounts",
                principalColumn: "Id");
        }
    }
}
