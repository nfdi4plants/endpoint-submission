using EndpointSubmission.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointSubmission.Service.Migrations;

[DbContext(typeof(SubmissionDbContext))]
[Migration("20260805120000_CaptureRoCrateSnapshot")]
public partial class CaptureRoCrateSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RoCrateJson",
            table: "Submissions",
            type: "jsonb",
            nullable: false,
            defaultValue: "{}");

        migrationBuilder.DropIndex(
            name: "IX_Submissions_ProjectId_CommitSha_Endpoint_Target",
            table: "Submissions");

        migrationBuilder.CreateIndex(
            name: "IX_Submissions_DataHubHost_ProjectId_CommitSha_Endpoint_Target",
            table: "Submissions",
            columns: new[] { "DataHubHost", "ProjectId", "CommitSha", "Endpoint", "Target" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Submissions_DataHubHost_ProjectId_CommitSha_Endpoint_Target",
            table: "Submissions");

        migrationBuilder.DropColumn(
            name: "RoCrateJson",
            table: "Submissions");

        migrationBuilder.CreateIndex(
            name: "IX_Submissions_ProjectId_CommitSha_Endpoint_Target",
            table: "Submissions",
            columns: new[] { "ProjectId", "CommitSha", "Endpoint", "Target" },
            unique: true);
    }
}
