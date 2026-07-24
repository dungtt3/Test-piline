using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Eaap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3_Security : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Cve",
                table: "Warnings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cwe",
                table: "Warnings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSuppressed",
                table: "Warnings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SecuritySeverity",
                table: "Warnings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "WarningSuppressed",
                table: "TrendPoints",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Suppressions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppressions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Suppressions_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Warnings_SecuritySeverity",
                table: "Warnings",
                column: "SecuritySeverity");

            migrationBuilder.CreateIndex(
                name: "IX_Suppressions_Fingerprint",
                table: "Suppressions",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_Suppressions_RepositoryId_Fingerprint",
                table: "Suppressions",
                columns: new[] { "RepositoryId", "Fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Suppressions");

            migrationBuilder.DropIndex(
                name: "IX_Warnings_SecuritySeverity",
                table: "Warnings");

            migrationBuilder.DropColumn(
                name: "Cve",
                table: "Warnings");

            migrationBuilder.DropColumn(
                name: "Cwe",
                table: "Warnings");

            migrationBuilder.DropColumn(
                name: "IsSuppressed",
                table: "Warnings");

            migrationBuilder.DropColumn(
                name: "SecuritySeverity",
                table: "Warnings");

            migrationBuilder.DropColumn(
                name: "WarningSuppressed",
                table: "TrendPoints");
        }
    }
}
