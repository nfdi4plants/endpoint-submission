using System.Text.Json;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Endpoints.ENA;
using EndpointSubmission.Service.Endpoints.WorkflowHub;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Pages;
using EndpointSubmission.Service.Pages.Components;
using EndpointSubmission.Service.Pages.Handlers;
using EndpointSubmission.Service.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace EndpointSubmission.Tests;

public class PageRenderingTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>", "&lt;script&gt;")]
    [InlineData("Tom & Jerry", "Tom &amp; Jerry")]
    [InlineData("\" onload=\"evil()", "&quot;")]
    public void Html_Text_escapes_markup(string input, string expectedFragment)
    {
        var encoded = Html.Text(input);

        Assert.Contains(expectedFragment, encoded);
        Assert.DoesNotContain("<script>", encoded);
    }

    [Fact]
    public void Html_Text_treats_null_as_empty()
        => Assert.Equal(string.Empty, Html.Text(null));

    [Fact]
    public void Layout_escapes_the_title()
    {
        // Titles will carry ARC project names, which come from the DataHUB.
        var html = Layout.Render(title: "<img src=x onerror=alert(1)>", content: "<p>ok</p>");

        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;img src=x", html);
    }

    [Fact]
    public void Layout_serves_picocss_from_wwwroot_not_a_cdn()
    {
        // A CDN link would be an external dependency on a page users hit mid-submission.
        var html = Layout.Render("t", "<p>c</p>");

        Assert.Contains(@"href=""/css/pico.cyan.min.css""", html);
        Assert.DoesNotContain("cdn.", html);
        Assert.DoesNotContain("unpkg", html);
    }

    [Fact]
    public void Layout_suppresses_the_referrer()
    {
        // Status page URLs carry the access token in the query string; a Referer header would
        // hand it to anything those pages ever link out to.
        Assert.Contains(@"name=""referrer"" content=""no-referrer""", Layout.Render("t", "c"));
    }

    [Fact]
    public void Layout_follows_the_system_colour_scheme()
        => Assert.Contains(@"content=""light dark""", Layout.Render("t", "c"));

    [Fact]
    public void Layout_emits_content_unescaped_so_components_compose()
    {
        var html = Layout.Render("t", "<p>literal <strong>markup</strong></p>");

        Assert.Contains("<p>literal <strong>markup</strong></p>", html);
    }

    [Fact]
    public void Index_handler_returns_html()
    {
        var result = IndexHandlers.Render();

        Assert.Equal("text/html", result.ContentType);
        Assert.Contains("<!DOCTYPE html>", result.ResponseContent);
        Assert.Contains("ARC Endpoint Submission", result.ResponseContent);
    }

    [Fact]
    public void Error_handler_reveals_nothing_about_the_failure()
    {
        var result = ErrorHandlers.Render();

        Assert.Equal("text/html", result.ContentType);
        Assert.DoesNotContain("Exception", result.ResponseContent);
        Assert.DoesNotContain("Stack", result.ResponseContent);
    }

    [Fact]
    public async Task Endpoint_previews_use_the_same_submission_but_render_endpoint_specific_pages()
    {
        var db = new SubmissionDbContext(new DbContextOptionsBuilder<SubmissionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var tokens = new StatusTokenService();
        var enaToken = tokens.Mint();
        var workflowHubToken = tokens.Mint();

        var ena = Submission("ena", "<script>ena arc</script>", enaToken.Hash);
        var workflowHub = Submission("workflowhub", "workflow arc", workflowHubToken.Hash);
        db.Submissions.AddRange(ena, workflowHub);
        await db.SaveChangesAsync();

        var access = new SubmissionPageAccess(db, tokens);
        var enaResult = Assert.IsType<ContentHttpResult>(
            await EnaPreviewHandlers.Render(ena.Id, enaToken.Token, access, default));
        var workflowHubResult = Assert.IsType<ContentHttpResult>(
            await WorkflowHubPreviewHandlers.Render(
                workflowHub.Id, workflowHubToken.Token, access, db, default));

        Assert.Contains("ENA submission preview", enaResult.ResponseContent);
        Assert.Contains("&lt;script&gt;ena arc&lt;/script&gt;", enaResult.ResponseContent);
        Assert.Contains("WorkflowHub submission preview", workflowHubResult.ResponseContent);
    }

    [Fact]
    public async Task Endpoint_preview_rejects_a_wrong_or_cross_endpoint_token()
    {
        var db = new SubmissionDbContext(new DbContextOptionsBuilder<SubmissionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var tokens = new StatusTokenService();
        var minted = tokens.Mint();
        var submission = Submission("ena", "arc", minted.Hash);
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();
        var access = new SubmissionPageAccess(db, tokens);

        Assert.IsType<NotFound>(
            await EnaPreviewHandlers.Render(submission.Id, "wrong", access, default));
        Assert.IsType<NotFound>(
            await WorkflowHubPreviewHandlers.Render(submission.Id, minted.Token, access, db, default));
    }

    private const string LicenseMit = """{"licenseSpdxId":"MIT","licensePresent":true}""";

    private static PreparedArtifact Artifact(
        Submission submission, string kind, string identifier, PreparedArtifactStatus status,
        string? title = null, string[]? failures = null, string metadata = LicenseMit) => new()
    {
        SubmissionId = submission.Id,
        ItemKind = kind,
        ItemIdentifier = identifier,
        Title = title,
        Status = status,
        FailureReasonsJson = JsonSerializer.Serialize(failures ?? []),
        MetadataJson = metadata,
        SizeBytes = status == PreparedArtifactStatus.Ready ? 2048 : null,
        Content = status == PreparedArtifactStatus.Ready ? [1, 2, 3] : null
    };

    /// <summary>Seeds a WorkflowHub submission with the given artifacts and renders its preview page.</summary>
    private static async Task<(string Html, PreparedArtifact[] Artifacts, string Token)> RenderWorkflowHub(
        Func<Submission, PreparedArtifact[]> build,
        Action<SubmissionDbContext, Submission>? seed = null,
        SubmissionTarget target = SubmissionTarget.Sandbox)
    {
        var db = new SubmissionDbContext(new DbContextOptionsBuilder<SubmissionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var tokens = new StatusTokenService();
        var minted = tokens.Mint();
        var submission = Submission("workflowhub", "arc", minted.Hash);
        submission.Target = target;
        db.Submissions.Add(submission);
        var artifacts = build(submission);
        db.PreparedArtifacts.AddRange(artifacts);
        seed?.Invoke(db, submission);
        await db.SaveChangesAsync();

        var result = Assert.IsType<ContentHttpResult>(await WorkflowHubPreviewHandlers.Render(
            submission.Id, minted.Token, new SubmissionPageAccess(db, tokens), db, default));
        return (result.ResponseContent!, artifacts, minted.Token);
    }

    /// <summary>The table row that contains <paramref name="marker"/>.</summary>
    private static string RowWith(string html, string marker)
    {
        var row = Assert.Single(html.Split("<tr").Where(r => r.Contains(marker)));
        return row[..row.IndexOf("</tr>", StringComparison.Ordinal)];
    }

    [Fact]
    public async Task WorkflowHub_preview_offers_a_checkbox_and_private_visibility_for_a_ready_item()
    {
        var (html, artifacts, token) = await RenderWorkflowHub(s =>
            [Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready, title: "Align reads")]);

        var id = artifacts[0].Id;
        var row = RowWith(html, "workflows/align");
        Assert.Contains($"""<input type="checkbox" name="item" value="{id}">""", row);
        Assert.Contains($"""<select name="visibility-{id}">""", row);
        Assert.Contains("""<option value="private" selected>Private</option>""", row);
        Assert.Contains("""<option value="public">Public</option>""", row);
        Assert.Contains("Align reads", row);
        Assert.Contains("2.0 KB", row);

        Assert.Contains(@"method=""post"" action=""/workflowhub/submissions/", html);
        Assert.Contains("/authorize\"", html);
        Assert.Contains($"""<input type="hidden" name="t" value="{token}">""", html);
        Assert.Contains("License: MIT. WorkflowHub shows it on every entry.", html);
        Assert.Contains("dev.workflowhub.eu", html);
        Assert.DoesNotContain("""<button type="submit" disabled>""", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_greys_out_items_that_are_not_ready_or_failed_to_pack()
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
        [
            Artifact(s, "run", "runs/r1", PreparedArtifactStatus.NotReady, failures: ["The run has no outputs."]),
            Artifact(s, "run", "runs/r2", PreparedArtifactStatus.PreparationFailed, failures: ["Zip assembly failed."])
        ]);

        var notReady = RowWith(html, "runs/r1");
        var failed = RowWith(html, "runs/r2");
        Assert.All(new[] { notReady, failed }, row =>
        {
            Assert.Contains(@"aria-disabled=""true""", row);
            Assert.Contains(@"<input type=""checkbox"" disabled", row);
            Assert.DoesNotContain(@"name=""item""", row);
        });
        Assert.Contains("<li>The run has no outputs.</li>", notReady);
        Assert.Contains("<li>Zip assembly failed.</li>", failed);
        Assert.Contains("""<button type="submit" disabled>""", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_escapes_identifiers_and_failure_texts()
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
        [
            Artifact(s, "run", "<script>id()</script>", PreparedArtifactStatus.NotReady,
                failures: ["<script>fail()</script>"])
        ]);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;id()&lt;/script&gt;", html);
        Assert.Contains("&lt;script&gt;fail()&lt;/script&gt;", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_explains_team_membership_and_the_public_moment()
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
            [Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready)]);

        Assert.Contains(
            "Uploading requires membership in a WorkflowHub team. You log in on WorkflowHub with your own account and confirm the upload there.",
            html);
        Assert.Contains(
            "WorkflowHub makes a new entry public for a moment before the service switches it to private.", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_shows_the_preparation_failure_when_there_are_no_artifacts()
    {
        const string message = "Crate preparation is not available in this deployment yet.";
        var (html, _, _) = await RenderWorkflowHub(_ => [], (db, s) => db.SubmissionEvents.Add(new SubmissionEvent
        {
            SubmissionId = s.Id,
            Step = "preparation.failed",
            Level = SubmissionEventLevel.Error,
            Message = message
        }));

        var notice = html[html.IndexOf("<article", StringComparison.Ordinal)..html.IndexOf("</article>", StringComparison.Ordinal)];
        Assert.Contains(message, notice);
        Assert.Contains("Click the badge in the DataHUB again to retry.", notice);
        Assert.Contains("""<button type="submit" disabled>""", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_says_so_and_offers_no_upload_when_the_commit_has_no_items()
    {
        var (html, _, _) = await RenderWorkflowHub(_ => [], (db, s) => db.SubmissionEvents.Add(new SubmissionEvent
        {
            SubmissionId = s.Id,
            Step = "preparation.completed",
            Message = "Prepared 0 WorkflowHub items: no workflows or runs were found in this commit."
        }));

        Assert.Contains("No workflows or runs were found in this ARC commit, so there is nothing to upload.", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("<form", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_links_an_uploaded_item_instead_of_offering_it()
    {
        var (html, _, _) = await RenderWorkflowHub(
            s =>
            [
                Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready),
                Artifact(s, "workflow", "workflows/other", PreparedArtifactStatus.Ready)
            ],
            (db, s) => db.Accessions.Add(new Accession
            {
                SubmissionId = s.Id,
                ObjectKind = "workflow",
                Alias = "workflow/workflows/align",
                AccessionNumber = "42"
            }),
            SubmissionTarget.Production);

        var uploaded = RowWith(html, "workflows/align");
        Assert.Contains("""<a href="https://workflowhub.eu/workflows/42">""", uploaded);
        Assert.Contains("Uploaded", uploaded);
        Assert.DoesNotContain(@"type=""checkbox""", uploaded);
        Assert.Contains(@"name=""item""", RowWith(html, "workflows/other"));
    }

    private static Submission Submission(string endpoint, string projectName, string tokenHash) => new()
    {
        ProjectId = 2,
        ProjectName = projectName,
        DataHubHost = "git.example.org",
        CommitSha = "0123456789abcdef",
        RoCrateUrl = "https://git.example.org/crate.json",
        RoCrateJson = JsonSerializer.Serialize(new { name = "arc" }),
        PackagePayloadJson = JsonSerializer.Serialize(new { target = "sandbox", contract_version = 1 }),
        Endpoint = endpoint,
        Target = SubmissionTarget.Sandbox,
        SubmitterGitLabUser = "user",
        SubmitterGitLabUserId = 7,
        SubmitterEmail = "user@example.org",
        StatusTokenHash = tokenHash
    };
}
