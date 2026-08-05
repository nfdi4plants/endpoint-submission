using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EndpointSubmission.Service.Migrations
{
    /// <inheritdoc />
    public partial class InitialSubmissionSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Recipient = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Sent = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Submissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    ProjectName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DataHubHost = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RoCrateUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    PackagePayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Target = table.Column<int>(type: "integer", nullable: false),
                    SubmitterGitLabUser = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SubmitterGitLabUserId = table.Column<long>(type: "bigint", nullable: false),
                    SubmitterEmail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    StatusTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Submissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Accessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Alias = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    AccessionNumber = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SecondaryAccession = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Accessions_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExternalSubmissionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ErrorSummary = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionAttempts_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Step = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionEvents_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionPayloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionPayloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionPayloads_SubmissionAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "SubmissionAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionReceipts_SubmissionAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "SubmissionAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accessions_SubmissionId",
                table: "Accessions",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionAttempts_SubmissionId_AttemptNumber",
                table: "SubmissionAttempts",
                columns: new[] { "SubmissionId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionEvents_SubmissionId_OccurredAt",
                table: "SubmissionEvents",
                columns: new[] { "SubmissionId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionPayloads_AttemptId",
                table: "SubmissionPayloads",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionReceipts_AttemptId",
                table: "SubmissionReceipts",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_ProjectId_CommitSha_Endpoint_Target",
                table: "Submissions",
                columns: new[] { "ProjectId", "CommitSha", "Endpoint", "Target" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_StatusTokenHash",
                table: "Submissions",
                column: "StatusTokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Accessions");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "SubmissionEvents");

            migrationBuilder.DropTable(
                name: "SubmissionPayloads");

            migrationBuilder.DropTable(
                name: "SubmissionReceipts");

            migrationBuilder.DropTable(
                name: "SubmissionAttempts");

            migrationBuilder.DropTable(
                name: "Submissions");
        }
    }
}
