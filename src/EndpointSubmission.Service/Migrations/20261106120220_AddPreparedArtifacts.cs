using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointSubmission.Service.Migrations
{
    /// <inheritdoc />
    public partial class AddPreparedArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PreparedArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ItemIdentifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FailureReasonsJson = table.Column<string>(type: "jsonb", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Content = table.Column<byte[]>(type: "bytea", nullable: true),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreparedArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreparedArtifacts_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PreparedArtifacts_SubmissionId_ItemKind_ItemIdentifier",
                table: "PreparedArtifacts",
                columns: new[] { "SubmissionId", "ItemKind", "ItemIdentifier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreparedArtifacts");
        }
    }
}
