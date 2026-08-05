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
                workflowHub.Id, workflowHubToken.Token, access, default));

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
            await WorkflowHubPreviewHandlers.Render(submission.Id, minted.Token, access, default));
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
