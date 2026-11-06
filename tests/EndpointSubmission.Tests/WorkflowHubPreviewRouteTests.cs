using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointSubmission.Tests;

public class WorkflowHubPreviewRouteTests
{
    [Fact]
    public async Task The_preview_route_serves_the_page_to_the_status_token_only()
    {
        using var factory = new WorkflowHubAppFactory(new FakeWorkflowHubGateway());
        var minted = new StatusTokenService().Mint();
        var submission = new Submission
        {
            ProjectName = "group/my-arc",
            DataHubHost = "git.example.org",
            Endpoint = "workflowhub",
            StatusTokenHash = minted.Hash
        };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SubmissionDbContext>();
            db.Submissions.Add(submission);
            db.PreparedArtifacts.Add(new PreparedArtifact
            {
                SubmissionId = submission.Id,
                ItemKind = "workflow",
                ItemIdentifier = "workflows/align",
                Status = PreparedArtifactStatus.Ready,
                Content = [1, 2, 3],
                SizeBytes = 3
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();

        var page = await client.GetAsync($"/workflowhub/submissions/{submission.Id}?t={minted.Token}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<h1>Submit to WorkflowHub</h1>", html);
        Assert.Contains("dev.workflowhub.eu", html);
        Assert.Contains("workflows/align", html);

        var denied = await client.GetAsync($"/workflowhub/submissions/{submission.Id}?t=wrong");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, denied.StatusCode);
    }

    /// <summary>Seeds a submission with one ready item and returns the page the route serves.</summary>
    private static async Task<string> Preview(
        SubmissionTarget target, bool allowProduction = false, string sandboxClientId = "test-client",
        string publicBaseUrl = "http://localhost:8081", string productionClientId = "test-client")
    {
        using var factory = new WorkflowHubAppFactory(
            new FakeWorkflowHubGateway(), allowProduction, sandboxClientId, publicBaseUrl, productionClientId);
        var minted = new StatusTokenService().Mint();
        var submission = new Submission
        {
            ProjectName = "group/my-arc",
            DataHubHost = "git.example.org",
            Endpoint = "workflowhub",
            Target = target,
            StatusTokenHash = minted.Hash
        };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SubmissionDbContext>();
            db.Submissions.Add(submission);
            db.PreparedArtifacts.Add(new PreparedArtifact
            {
                SubmissionId = submission.Id,
                ItemKind = "workflow",
                ItemIdentifier = "workflows/align",
                Status = PreparedArtifactStatus.Ready,
                Content = [1, 2, 3],
                SizeBytes = 3
            });
            await db.SaveChangesAsync();
        }

        var page = await factory.CreateClient().GetAsync($"/workflowhub/submissions/{submission.Id}?t={minted.Token}");
        Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
        return await page.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task The_preview_route_lists_production_items_read_only_while_production_upload_is_off()
    {
        var html = await Preview(SubmissionTarget.Production, allowProduction: false);

        Assert.Contains("Uploading to the production WorkflowHub is not enabled on this deployment.", html);
        Assert.Contains("wfh-notice wfh-notice-warning", html);
        Assert.Contains("Ready to upload", html);
        Assert.Contains("workflows/align", html);
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain(@"type=""checkbox""", html);
        Assert.DoesNotContain("<select", html);
        Assert.DoesNotContain("Continue to WorkflowHub", html);
        Assert.DoesNotContain("/js/workflowhub.js", html);
    }

    [Theory]
    [InlineData("", "http://localhost:8081")]
    [InlineData("test-client", "")]
    public async Task The_preview_route_lists_items_read_only_while_the_login_is_not_configured(
        string sandboxClientId, string publicBaseUrl)
    {
        var html = await Preview(SubmissionTarget.Sandbox, sandboxClientId: sandboxClientId, publicBaseUrl: publicBaseUrl);

        Assert.Contains("WorkflowHub login is not configured on this deployment.", html);
        Assert.Contains("wfh-notice wfh-notice-warning", html);
        Assert.DoesNotContain("is not enabled on this deployment", html);
        Assert.Contains("workflows/align", html);
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("Continue to WorkflowHub", html);
        Assert.DoesNotContain("/js/workflowhub.js", html);
    }

    [Fact]
    public async Task The_production_off_notice_wins_over_the_missing_login_notice()
    {
        var html = await Preview(SubmissionTarget.Production, allowProduction: false, productionClientId: "");

        Assert.Contains("Uploading to the production WorkflowHub is not enabled on this deployment.", html);
        Assert.DoesNotContain("login is not configured", html);
    }

    [Fact]
    public async Task The_preview_route_offers_the_upload_form_for_production_items_when_production_upload_is_on()
    {
        var html = await Preview(SubmissionTarget.Production, allowProduction: true);

        Assert.DoesNotContain("is not enabled on this deployment", html);
        Assert.DoesNotContain("login is not configured", html);
        Assert.Contains("<form", html);
        Assert.Contains(@"type=""checkbox""", html);
        Assert.Contains("/js/workflowhub.js", html);
    }
}
