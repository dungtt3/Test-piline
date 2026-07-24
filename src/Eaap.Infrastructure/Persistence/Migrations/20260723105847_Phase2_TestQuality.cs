using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Eaap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_TestQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsNew",
                table: "Warnings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "GatePolicyBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Thresholds = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatePolicyBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GatePolicyBindings_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetricSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalyzerRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Metrics = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricSets_AnalysisJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AnalysisJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetricSets_AnalyzerRuns_AnalyzerRunId",
                        column: x => x.AnalyzerRunId,
                        principalTable: "AnalyzerRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrendPoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    WarningTotal = table.Column<int>(type: "integer", nullable: false),
                    WarningNew = table.Column<int>(type: "integer", nullable: false),
                    WarningResolved = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: false),
                    CoverageLine = table.Column<double>(type: "double precision", nullable: true),
                    TestsTotal = table.Column<int>(type: "integer", nullable: true),
                    TestsFailed = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrendPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrendPoints_AnalysisJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AnalysisJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrendPoints_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WarningBaselines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FirstSeenJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WarningBaselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WarningBaselines_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GatePolicyBindings_RepositoryId",
                table: "GatePolicyBindings",
                column: "RepositoryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricSets_AnalyzerRunId",
                table: "MetricSets",
                column: "AnalyzerRunId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricSets_JobId",
                table: "MetricSets",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_TrendPoints_JobId",
                table: "TrendPoints",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrendPoints_RepositoryId_CreatedAt",
                table: "TrendPoints",
                columns: new[] { "RepositoryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WarningBaselines_Fingerprint",
                table: "WarningBaselines",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_WarningBaselines_RepositoryId_Fingerprint",
                table: "WarningBaselines",
                columns: new[] { "RepositoryId", "Fingerprint" },
                unique: true);

            // Grafana reads the trend tables directly. A dedicated read-only role means the
            // dashboard datasource physically cannot mutate analysis data. The password here is a
            // local-dev default (ADR-010) with the same posture as the docker-compose credentials;
            // real deployments must rotate it. current_database() keeps this working under
            // Testcontainers, where the database is not named "eaap".
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'grafana_ro') THEN
                        CREATE ROLE grafana_ro LOGIN PASSWORD 'grafana-ro-dev';
                    END IF;
                    EXECUTE format('GRANT CONNECT ON DATABASE %I TO grafana_ro', current_database());
                END
                $$;
                """);
            migrationBuilder.Sql("""GRANT USAGE ON SCHEMA public TO grafana_ro;""");
            migrationBuilder.Sql("""GRANT SELECT ON "TrendPoints", "Warnings", "MetricSets" TO grafana_ro;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Privileges must go before the tables, and the role before the database it can connect to.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'grafana_ro') THEN
                        REVOKE ALL ON ALL TABLES IN SCHEMA public FROM grafana_ro;
                        REVOKE ALL ON SCHEMA public FROM grafana_ro;
                        EXECUTE format('REVOKE ALL ON DATABASE %I FROM grafana_ro', current_database());
                        DROP ROLE grafana_ro;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropTable(
                name: "GatePolicyBindings");

            migrationBuilder.DropTable(
                name: "MetricSets");

            migrationBuilder.DropTable(
                name: "TrendPoints");

            migrationBuilder.DropTable(
                name: "WarningBaselines");

            migrationBuilder.DropColumn(
                name: "IsNew",
                table: "Warnings");
        }
    }
}
