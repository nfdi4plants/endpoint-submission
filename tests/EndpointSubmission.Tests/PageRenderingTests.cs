using System.Text.Json;
using System.Text.RegularExpressions;
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
using Microsoft.Extensions.Options;

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
        Assert.Contains("<h1>Submit to WorkflowHub</h1>", workflowHubResult.ResponseContent);
        Assert.DoesNotContain("ENA submission preview", workflowHubResult.ResponseContent);
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
        SubmissionTarget target = SubmissionTarget.Sandbox,
        string? uploaded = null, string? failed = null, string? unknown = null,
        string? partial = null, string? skipped = null,
        IOptions<EndpointSubmissionOptions>? submissionOptions = null)
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
            submission.Id, minted.Token, new SubmissionPageAccess(db, tokens), db, default,
            submissionOptions: submissionOptions, uploaded: uploaded, failed: failed, unknown: unknown, partial: partial, skipped: skipped));
        return (result.ResponseContent!, artifacts, minted.Token);
    }

    /// <summary>The list entry or details element of an item that contains <paramref name="marker"/>.</summary>
    private static string ItemWith(string html, string marker)
    {
        var item = Assert.Single(
            Regex.Split(html, @"(?=<(?:li|details) class=""wfh-item)")
                .Where(chunk => chunk.StartsWith("<li class=\"wfh-item") || chunk.StartsWith("<details class=\"wfh-item"))
                .Where(chunk => chunk.Contains(marker)));
        if (!item.StartsWith("<li"))
            return item[..(item.IndexOf("</details>", StringComparison.Ordinal) + "</details>".Length)];

        // A ready item can hold a nested list, so the item ends at the li that closes its own.
        var depth = 0;
        foreach (Match tag in Regex.Matches(item, @"<li[\s>]|</li>"))
        {
            depth += tag.Value == "</li>" ? -1 : 1;
            if (depth == 0) return item[..(tag.Index + tag.Length)];
        }

        throw new InvalidOperationException("The item has no closing li.");
    }

    private static PreparedArtifact[] OneReady(Submission s)
        => [Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready, title: "Align reads")];

    [Fact]
    public async Task WorkflowHub_preview_has_a_header_and_references_the_stylesheet_and_script()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady);

        Assert.Contains("<title>Submit to WorkflowHub</title>", html);
        Assert.Contains("<h1>Submit to WorkflowHub</h1>", html);
        Assert.Contains("<hgroup>", html);
        Assert.Contains(@"href=""/css/pico.cyan.min.css""", html);
        Assert.Contains(@"<link rel=""stylesheet"" href=""/css/workflowhub.css"" />", html);
        Assert.Contains(@"<script src=""/js/workflowhub.js"" defer></script>", html);
        Assert.DoesNotContain("cdn.", html);
        Assert.DoesNotContain("<style", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_summarizes_the_submission_in_a_card()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady);

        var card = html[html.IndexOf(@"<article class=""wfh-summary"">", StringComparison.Ordinal)..];
        card = card[..card.IndexOf("</article>", StringComparison.Ordinal)];
        Assert.Contains("<dt>ARC</dt><dd>arc</dd>", card);
        Assert.Contains("<dt>DataHUB</dt><dd>git.example.org</dd>", card);
        Assert.Contains(@"<dt>Commit</dt><dd><code title=""0123456789abcdef"">01234567</code></dd>", card);
        Assert.Contains("<dt>WorkflowHub</dt><dd>dev.workflowhub.eu", card);
        Assert.Contains(">test instance</span>", card);
        Assert.Contains("<dt>License</dt><dd><strong>MIT</strong>", card);
        Assert.DoesNotContain("wfh-fact-warning", card);
    }

    [Fact]
    public async Task WorkflowHub_preview_marks_the_production_instance()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady, target: SubmissionTarget.Production);

        Assert.Contains("<dt>WorkflowHub</dt><dd>workflowhub.eu", html);
        Assert.Contains(">production</span>", html);
        Assert.DoesNotContain("test instance", html);
    }

    [Theory]
    [InlineData("""{"licensePresent":false}""", "The ARC has no LICENSE file, so nothing can be uploaded.")]
    [InlineData("""{"licensePresent":true}""", "The ARC license was not recognized as an SPDX license. Entries arrive without a license, and you can set one on WorkflowHub afterwards.")]
    [InlineData("not json", "The ARC license was not recognized as an SPDX license.")]
    public async Task WorkflowHub_preview_shows_a_missing_or_unrecognized_license_as_a_warning(string metadata, string wording)
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
            [Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready, metadata: metadata)]);

        Assert.Contains(@"<div class=""wfh-fact wfh-fact-wide wfh-fact-warning""><dt>License</dt>", html);
        Assert.Contains(wording, html);
    }

    [Fact]
    public async Task WorkflowHub_preview_escapes_the_license_id()
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
            [Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready,
                metadata: """{"licenseSpdxId":"<b>MIT</b>","licensePresent":true}""")]);

        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;b&gt;MIT&lt;/b&gt;", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_offers_a_labelled_checkbox_and_private_visibility_for_a_ready_item()
    {
        var (html, artifacts, token) = await RenderWorkflowHub(OneReady);

        var id = artifacts[0].Id;
        var row = ItemWith(html, "workflows/align");
        Assert.Contains($"""<input type="checkbox" id="item-{id}" name="item" value="{id}">""", row);
        Assert.Contains($"""<label for="item-{id}">""", row);
        Assert.Contains(@"<span class=""wfh-badge wfh-badge-workflow"">Workflow</span> <strong>Align reads</strong>", row);
        Assert.Contains(@"<span class=""wfh-id"">workflows/align</span>", row);
        Assert.Contains($"""<label for="visibility-{id}">Visibility</label>""", row);
        Assert.Contains($"""<select id="visibility-{id}" name="visibility-{id}">""", row);
        Assert.Contains("""<option value="private" selected>Private</option>""", row);
        Assert.Contains("""<option value="public">Public</option>""", row);
        Assert.Contains(@"<span class=""wfh-size"">2.0 KB</span>", row);

        Assert.Contains(@"method=""post"" action=""/workflowhub/submissions/", html);
        Assert.Contains("/authorize\"", html);
        Assert.Contains($"""<input type="hidden" name="t" value="{token}">""", html);
        Assert.Contains(@"<button type=""submit"" data-wfh-submit>Continue to WorkflowHub</button>", html);
        Assert.DoesNotContain("disabled", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_hides_select_all_until_the_script_shows_it()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady);

        Assert.Contains(@"<label class=""wfh-select-all"" hidden>", html);
        Assert.Contains("data-wfh-select-all", html);
        Assert.Contains("data-wfh-empty hidden>Select at least one item.</span>", html);
    }

    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(9626, "9.4 KB")]
    [InlineData(3 * 1024 * 1024, "3.0 MB")]
    public async Task WorkflowHub_preview_shows_sizes_on_one_line(long bytes, string expected)
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
        {
            var artifact = Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready);
            artifact.SizeBytes = bytes;
            return [artifact];
        });

        Assert.Contains($@"<span class=""wfh-size"">{expected}</span>", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_lists_the_LFS_pointer_files_of_a_ready_item_in_closed_details()
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
        [
            Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready, title: "Align reads",
                metadata: """{"licenseSpdxId":"MIT","licensePresent":true,"lfsPointerFiles":["data/a.fastq","data/<b>raw</b>.fastq"]}"""),
            Artifact(s, "run", "runs/r1", PreparedArtifactStatus.Ready, title: "Run one")
        ]);

        // The details element is part of the item and sits after its label, so opening it cannot toggle the checkbox.
        var row = ItemWith(html, "workflows/align");
        const string opening = @"<details class=""wfh-lfs"">";
        Assert.True(row.IndexOf("</label>", StringComparison.Ordinal) < row.IndexOf(opening, StringComparison.Ordinal));

        Assert.Contains("<summary>2 files stay Git LFS pointers</summary>", row);
        Assert.Contains("<li><code>data/a.fastq</code></li>", row);
        Assert.Contains("<li><code>data/&lt;b&gt;raw&lt;/b&gt;.fastq</code></li>", row);
        Assert.DoesNotContain("<b>", row);

        Assert.DoesNotContain("wfh-lfs", ItemWith(html, "runs/r1"));
        Assert.Single(Regex.Matches(html, @"class=""wfh-lfs"""));
    }

    [Fact]
    public async Task WorkflowHub_preview_lists_the_LFS_pointer_files_in_a_read_only_item()
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
            [
                Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready, title: "Align reads",
                    metadata: """{"licenseSpdxId":"MIT","licensePresent":true,"lfsPointerFiles":["data/<i>raw</i>.csv"]}""")
            ],
            target: SubmissionTarget.Production,
            submissionOptions: Options.Create(new EndpointSubmissionOptions { AllowProductionTarget = false }));

        var row = ItemWith(html, "workflows/align");
        Assert.Contains("wfh-item-readonly", row);
        Assert.DoesNotContain(@"type=""checkbox""", row);
        Assert.Contains(@"<details class=""wfh-lfs"">", row);
        Assert.Contains("<summary>1 file stays a Git LFS pointer</summary>", row);
        Assert.Contains("<li><code>data/&lt;i&gt;raw&lt;/i&gt;.csv</code></li>", row);
    }

    [Theory]
    [InlineData("""{"licenseSpdxId":"MIT","licensePresent":true,"lfsPointerFiles":"x"}""", 0)]
    [InlineData("not json", 0)]
    [InlineData("""{"lfsPointerFiles":[1,"data/a.csv",null]}""", 1)]
    public async Task WorkflowHub_preview_lists_only_the_usable_LFS_pointer_entries_of_odd_metadata(
        string metadata, int expectedEntries)
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
        [
            Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready, title: "Align reads", metadata: metadata)
        ]);

        var row = ItemWith(html, "workflows/align");
        Assert.Equal(expectedEntries, Regex.Matches(row, "<li><code>").Count);
        if (expectedEntries == 0)
        {
            Assert.DoesNotContain("wfh-lfs", row);
            return;
        }

        Assert.Contains("<summary>1 file stays a Git LFS pointer</summary>", row);
        Assert.Contains("<li><code>data/a.csv</code></li>", row);
    }

    [Fact]
    public async Task WorkflowHub_preview_lists_items_that_are_not_ready_in_closed_details_with_a_count()
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
        [
            Artifact(s, "run", "runs/r1", PreparedArtifactStatus.NotReady, title: "Run fastqc",
                failures: ["The run has no outputs.", "The run has no title.", "The run has no creator."]),
            Artifact(s, "run", "runs/r2", PreparedArtifactStatus.PreparationFailed, failures: ["Zip assembly failed."]),
            Artifact(s, "run", "runs/r3", PreparedArtifactStatus.NotReady, failures: ["The run has no outputs."])
        ]);

        Assert.Contains("These items need changes in the ARC before they can be uploaded.", html);

        var notReady = ItemWith(html, "runs/r1");
        Assert.StartsWith(@"<details class=""wfh-item wfh-item-blocked"">", notReady);
        Assert.Contains("<strong>Run fastqc</strong>", notReady);
        Assert.Contains("3 things to fix in the ARC", notReady);
        Assert.Contains("<li>The run has no outputs.</li><li>The run has no title.</li><li>The run has no creator.</li>", notReady);

        var failed = ItemWith(html, "runs/r2");
        Assert.Contains("could not be packed", failed);
        Assert.Contains("<li>Zip assembly failed.</li>", failed);

        Assert.Contains("1 thing to fix in the ARC", ItemWith(html, "runs/r3"));

        Assert.All(new[] { notReady, failed }, item =>
        {
            Assert.DoesNotContain(@"type=""checkbox""", item);
            Assert.DoesNotContain(@"name=""item""", item);
        });
    }

    [Fact]
    public async Task WorkflowHub_preview_offers_no_form_when_nothing_can_be_selected()
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
        [
            Artifact(s, "run", "runs/r1", PreparedArtifactStatus.NotReady, failures: ["The run has no outputs."]),
            Artifact(s, "run", "runs/r2", PreparedArtifactStatus.PreparationFailed, failures: ["Zip assembly failed."])
        ]);

        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("/authorize", html);
        Assert.DoesNotContain("Select all", html);
        Assert.DoesNotContain("Continue to WorkflowHub", html);
        Assert.Contains(@"href=""/css/workflowhub.css""", html);
        Assert.DoesNotContain("/js/workflowhub.js", html);
        Assert.Contains("runs/r1", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_escapes_identifiers_titles_and_failure_texts()
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
        [
            Artifact(s, "run", "<script>id()</script>", PreparedArtifactStatus.NotReady,
                title: "<img src=x onerror=t()>", failures: ["<script>fail()</script>"]),
            Artifact(s, "workflow", "workflows/<i>x</i>", PreparedArtifactStatus.Ready, title: "\"><b>t</b>")
        ]);

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("<i>", html);
        Assert.Contains("&lt;script&gt;id()&lt;/script&gt;", html);
        Assert.Contains("&lt;script&gt;fail()&lt;/script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=t()&gt;", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_turns_backticks_in_reasons_into_code_after_escaping()
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
        [
            Artifact(s, "run", "runs/r1", PreparedArtifactStatus.NotReady, failures:
            [
                "Name the inputs with `<script>alert(1)</script>` and the outputs with `result`.",
                "A lone ` backtick stays as it is.",
                "Quotes `\"a\" & 'b'` are escaped too."
            ])
        ]);

        var item = ItemWith(html, "runs/r1");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("Name the inputs with <code>&lt;script&gt;alert(1)&lt;/script&gt;</code> and the outputs with <code>result</code>.", item);
        Assert.Contains("<li>A lone ` backtick stays as it is.</li>", item);
        Assert.Contains("<code>&quot;a&quot; &amp; &#39;b&#39;</code>", item);
        Assert.Equal(3, Regex.Matches(item, "<code>").Count);
    }

    [Fact]
    public async Task WorkflowHub_preview_explains_team_membership_and_the_public_moment()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady);

        Assert.Contains(
            "Uploading requires membership in a WorkflowHub team.", html);
        Assert.Contains("<li>One upload takes at most 25 items.</li>", html);
        Assert.Contains(
            "WorkflowHub makes a new entry public for a moment before the service switches it to private.", html);
        Assert.Contains(
            "Data files outside workflows/ that are stored with Git LFS stay pointer files in the crates, which keeps them under WorkflowHub's 100 MB limit. The full files stay in the ARC on the DataHUB.", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_explains_the_login_and_asks_for_no_token()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady);

        Assert.Contains("<h2 id=\"wfh-upload-heading\">Upload</h2>", html);
        Assert.Contains(
            "WorkflowHub asks you to log in and to confirm that this service may upload for you. Then it sends you back to this page.",
            html);
        Assert.Contains("""role="status" aria-live="polite" data-wfh-status""", html);
        Assert.DoesNotContain("type=\"password\"", html);
        Assert.DoesNotContain("<h3>", html);
        Assert.DoesNotContain("name=\"team\"", html);
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

        var start = html.IndexOf(@"<div class=""wfh-notice wfh-notice-error"" role=""alert"">", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var notice = html[start..html.IndexOf("</div>", start, StringComparison.Ordinal)];
        Assert.Contains(message, notice);
        Assert.Contains("Click the badge in the DataHUB again to retry.", notice);
        Assert.DoesNotContain("<form", html);
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
        Assert.Contains("wfh-notice wfh-notice-info", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("<form", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_links_an_uploaded_item_instead_of_offering_it()
    {
        var (html, _, _) = await RenderWorkflowHub(
            s =>
            [
                Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready, title: "Align reads"),
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

        Assert.Contains("Already on WorkflowHub", html);
        var uploaded = ItemWith(html, "workflows/align");
        Assert.Contains(@"<span class=""wfh-badge wfh-badge-workflow"">Workflow</span>", uploaded);
        Assert.Contains("<strong>Align reads</strong>", uploaded);
        Assert.Contains("Entry 42", uploaded);
        Assert.Contains(
            """<a href="https://workflowhub.eu/workflows/42" target="_blank" rel="noopener noreferrer">Open <span class="wfh-sr-only">Align reads </span>on WorkflowHub</a>""",
            uploaded);
        Assert.DoesNotContain(@"type=""checkbox""", uploaded);
        Assert.Contains(@"name=""item""", ItemWith(html, "workflows/other"));
    }

    [Fact]
    public async Task WorkflowHub_preview_has_no_uploaded_section_without_accessions()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady);

        Assert.DoesNotContain("Already on WorkflowHub", html);
        Assert.DoesNotContain("on WorkflowHub</a>", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_shows_the_activity_in_closed_details_with_utc_times_and_level_badges()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady, (db, s) =>
        {
            db.SubmissionEvents.Add(new SubmissionEvent
            {
                SubmissionId = s.Id,
                Step = "a",
                Message = "Prepared <b>1</b> item.",
                OccurredAt = new DateTimeOffset(2026, 11, 2, 14, 3, 59, TimeSpan.FromHours(1))
            });
            db.SubmissionEvents.Add(new SubmissionEvent
            {
                SubmissionId = s.Id,
                Step = "b",
                Level = SubmissionEventLevel.Error,
                Message = "Upload failed.",
                OccurredAt = new DateTimeOffset(2026, 11, 2, 14, 4, 0, TimeSpan.Zero)
            });
        });

        var activity = html[html.IndexOf(@"<section class=""wfh-section wfh-activity"">", StringComparison.Ordinal)..];
        Assert.Contains("<details>", activity);
        Assert.DoesNotContain("<details open", activity);
        Assert.Contains("<summary>Activity <span class=\"wfh-count\">2</span></summary>", activity);
        Assert.Contains("2026-11-02 13:03 UTC", activity);
        Assert.Contains("2026-11-02 14:04 UTC", activity);
        Assert.Contains(@"<span class=""wfh-badge wfh-badge-level-info"">info</span>", activity);
        Assert.Contains(@"<span class=""wfh-badge wfh-badge-level-error"">error</span>", activity);
        Assert.Contains("Prepared &lt;b&gt;1&lt;/b&gt; item.", activity);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("<table", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_shows_a_success_banner_from_the_upload_counts()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady, uploaded: "2", failed: "0", partial: "0", skipped: "0");

        var banner = html[html.IndexOf("data-wfh-result", StringComparison.Ordinal)..];
        banner = banner[..banner.IndexOf("</div>", StringComparison.Ordinal)];
        Assert.Contains("<p>2 items uploaded to WorkflowHub.</p>", banner);
        Assert.Contains("wfh-notice wfh-notice-success", html);
        Assert.Single(Regex.Matches(banner, "<p>"));
    }

    [Fact]
    public async Task WorkflowHub_preview_uses_the_singular_for_one_item()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady, uploaded: "1");

        Assert.Contains("<p>1 item uploaded to WorkflowHub.</p>", html);
    }

    [Theory]
    [InlineData("1", "0", "1", "0", "0", "warning")]
    [InlineData("0", "0", "0", "1", "0", "warning")]
    [InlineData("1", "1", "0", "0", "0", "warning")]
    [InlineData("0", "2", "0", "0", "0", "error")]
    [InlineData("0", "0", "0", "0", "3", "info")]
    public async Task WorkflowHub_preview_picks_the_banner_style_from_the_counts(
        string uploaded, string failed, string partial, string unknown, string skipped, string style)
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady,
            uploaded: uploaded, failed: failed, partial: partial, unknown: unknown, skipped: skipped);

        Assert.Contains($@"wfh-notice wfh-notice-{style}"" role=""status"" data-wfh-result", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_banner_says_what_went_wrong_in_plain_sentences()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady, failed: "2", unknown: "1", partial: "1", skipped: "1");

        Assert.Contains("<p>2 items could not be uploaded.", html);
        Assert.Contains("1 item has an unknown result. The entry may exist on WorkflowHub and may be public. Check before uploading again.", html);
        Assert.Contains("1 item was created on WorkflowHub, but setting the visibility failed.", html);
        Assert.Contains("1 item was skipped because it is already on WorkflowHub.", html);
        Assert.DoesNotContain("uploaded to WorkflowHub.", html);
    }

    [Theory]
    [InlineData("abc", "-1", "1.5", "<script>alert(1)</script>", "99999999999")]
    [InlineData("", " ", "+2", "0x10", "1e3")]
    [InlineData("-2", "-0", "0", "00", "٣")]
    public async Task WorkflowHub_preview_ignores_junk_in_the_upload_counts(
        string uploaded, string failed, string unknown, string partial, string skipped)
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady,
            uploaded: uploaded, failed: failed, unknown: unknown, partial: partial, skipped: skipped);

        Assert.DoesNotContain("data-wfh-result", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("alert(1)", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_keeps_the_valid_counts_next_to_junk()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady, uploaded: "3", failed: "<b>", skipped: "-1");

        Assert.Contains("<p>3 items uploaded to WorkflowHub.</p>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("could not be uploaded", html);
        Assert.DoesNotContain("skipped", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_shows_no_banner_without_counts()
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady);

        Assert.DoesNotContain("data-wfh-result", html);
    }

    [Fact]
    public void WorkflowHub_upload_counts_survive_a_round_trip_through_the_query_string()
    {
        var counts = WorkflowHubUploadCounts.FromResult(new WorkflowHubUploadResult(null, 2, 1, 3, 4, 5));

        Assert.Equal(new WorkflowHubUploadCounts(2, 3, 5, 1, 4), counts);
        Assert.Equal("&uploaded=2&failed=3&unknown=5&partial=1&skipped=4", counts.ToQuery());
        Assert.Equal(
            new WorkflowHubUploadCounts(2, 3, 5, 1, 4),
            WorkflowHubUploadCounts.Parse("2", "3", "5", "1", "4"));
        Assert.Equal(
            new WorkflowHubUploadCounts(2, 3, 0, 1, 4),
            WorkflowHubUploadCounts.Parse("2", "3", null, "1", "4"));
        Assert.Null(WorkflowHubUploadCounts.Parse(null, null, null, null, null));
        Assert.Null(WorkflowHubUploadCounts.Parse("0", "x", "-1", "", " "));
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

    /// <summary>Seeds an attempt for a seeded artifact, linked the way the upload service links it.</summary>
    private static void AddAttempt(
        SubmissionDbContext db, Submission submission, string identifier, int attemptNumber,
        SubmissionStatus status, string? errorSummary = null)
    {
        var artifact = db.PreparedArtifacts.Local.Single(a => a.ItemIdentifier == identifier);
        var attempt = new SubmissionAttempt
        {
            SubmissionId = submission.Id,
            AttemptNumber = attemptNumber,
            Status = status,
            ErrorSummary = errorSummary
        };
        db.SubmissionAttempts.Add(attempt);
        db.SubmissionPayloads.Add(new SubmissionPayload
        {
            AttemptId = attempt.Id,
            ObjectKind = "workflowhub-crate",
            Content = JsonSerializer.Serialize(new { method = "POST", artifactId = artifact.Id })
        });
    }

    private static void AddAccession(SubmissionDbContext db, Submission submission, string identifier, string number)
        => db.Accessions.Add(new Accession
        {
            SubmissionId = submission.Id,
            ObjectKind = "workflowhub-workflow",
            Alias = $"workflow/{identifier}",
            AccessionNumber = number
        });

    private const string CautionText =
        "An earlier upload of this item got no clear answer from WorkflowHub. An entry may already exist and may be public. Check WorkflowHub before uploading it again.";

    private const string CautionLink =
        """<a href="https://dev.workflowhub.eu/workflows" target="_blank" rel="noopener noreferrer">Check on WorkflowHub</a>""";

    [Fact]
    public async Task WorkflowHub_preview_flags_a_ready_item_whose_upload_had_an_unknown_outcome()
    {
        var unknown = $"{WorkflowHubUploadService.UnknownOutcomeLead}workflow workflows/align. The entry may exist on WorkflowHub and may be public.";
        var (html, artifacts, _) = await RenderWorkflowHub(
            s =>
            [
                Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready),
                Artifact(s, "workflow", "workflows/other", PreparedArtifactStatus.Ready)
            ],
            (db, s) =>
            {
                AddAttempt(db, s, "workflows/align", 1, SubmissionStatus.Failed, unknown);
                AddAttempt(db, s, "workflows/other", 2, SubmissionStatus.Failed, "WorkflowHub rejected the upload (HTTP 422): invalid crate");
            });

        var flagged = ItemWith(html, "workflows/align");
        Assert.StartsWith(@"<li class=""wfh-item wfh-item-ready wfh-item-caution"">", flagged);
        Assert.Contains($"""<input type="checkbox" id="item-{artifacts[0].Id}" name="item" value="{artifacts[0].Id}" aria-describedby="caution-{artifacts[0].Id}" data-wfh-caution>""", flagged);
        Assert.Contains($"""<p class="wfh-item-warning" id="caution-{artifacts[0].Id}">""", flagged);
        Assert.Contains(CautionText, flagged);
        Assert.Contains(CautionLink, flagged);

        var clean = ItemWith(html, "workflows/other");
        Assert.DoesNotContain("wfh-item-caution", clean);
        Assert.DoesNotContain("data-wfh-caution", clean);
        Assert.DoesNotContain("aria-describedby", clean);
        Assert.DoesNotContain(CautionText, clean);
    }

    [Fact]
    public async Task WorkflowHub_preview_flags_a_ready_item_whose_upload_never_finished()
    {
        var (html, _, _) = await RenderWorkflowHub(
            s => [Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready)],
            (db, s) => AddAttempt(db, s, "workflows/align", 1, SubmissionStatus.Submitting));

        var row = ItemWith(html, "workflows/align");
        Assert.Contains("wfh-item-caution", row);
        Assert.Contains("data-wfh-caution>", row);
        Assert.Contains(CautionText, row);
        Assert.Contains(CautionLink, row);
    }

    [Fact]
    public async Task WorkflowHub_preview_flags_a_ready_item_whose_visibility_step_failed_without_an_accession()
    {
        var (html, _, _) = await RenderWorkflowHub(
            s => [Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready)],
            (db, s) => AddAttempt(db, s, "workflows/align", 1, SubmissionStatus.PartiallyFailed,
                "The entry was created, but setting its visibility failed: timeout"));

        var row = ItemWith(html, "workflows/align");
        Assert.Contains("wfh-item-caution", row);
        Assert.Contains("data-wfh-caution>", row);
        Assert.Contains(CautionText, row);
        Assert.Contains(CautionLink, row);
    }

    [Fact]
    public async Task WorkflowHub_preview_marks_an_uploaded_item_whose_visibility_was_not_set()
    {
        var (html, _, _) = await RenderWorkflowHub(
            s =>
            [
                Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready),
                Artifact(s, "workflow", "workflows/other", PreparedArtifactStatus.Ready)
            ],
            (db, s) =>
            {
                AddAccession(db, s, "workflows/align", "42");
                AddAccession(db, s, "workflows/other", "43");
                AddAttempt(db, s, "workflows/align", 1, SubmissionStatus.PartiallyFailed,
                    "The entry was created, but setting its visibility failed: timeout");
                AddAttempt(db, s, "workflows/other", 2, SubmissionStatus.Submitted);
            });

        var row = ItemWith(html, "workflows/align");
        Assert.Contains(@"<span class=""wfh-badge wfh-badge-warning"">Visibility not set</span>", row);
        Assert.Contains("Setting the visibility failed. The entry may be public. Check it on WorkflowHub.", row);
        Assert.DoesNotContain(@"type=""checkbox""", row);
        Assert.DoesNotContain("Visibility not set", ItemWith(html, "workflows/other"));
    }

    [Theory]
    [InlineData("1", "1 item was created on WorkflowHub, but setting the visibility failed. The entry may be public. Check the visibility on WorkflowHub.")]
    [InlineData("2", "2 items were created on WorkflowHub, but setting the visibility failed. The entries may be public. Check the visibility on WorkflowHub.")]
    public async Task WorkflowHub_preview_banner_warns_that_a_partly_uploaded_entry_may_be_public(string partial, string line)
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady, partial: partial);

        Assert.Contains($"<p>{line}</p>", html);
    }

    [Theory]
    [InlineData("1", null, null, null, true)]
    [InlineData(null, "1", null, null, true)]
    [InlineData(null, null, "1", null, true)]
    [InlineData(null, null, null, "2", false)]
    public async Task WorkflowHub_preview_opens_the_activity_only_after_a_problem(
        string? failed, string? unknown, string? partial, string? uploaded, bool opened)
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady,
            failed: failed, unknown: unknown, partial: partial, uploaded: uploaded);

        var activity = html[html.IndexOf(@"<section class=""wfh-section wfh-activity"">", StringComparison.Ordinal)..];
        Assert.Equal(opened, activity.Contains("<details open>"));
        Assert.Equal(!opened, activity.Contains("<details>"));
    }

    [Fact]
    public async Task WorkflowHub_preview_names_the_item_for_screen_readers_in_the_uploaded_link()
    {
        var (html, _, _) = await RenderWorkflowHub(
            s => [Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready, title: "<script>x()</script>")],
            (db, s) => AddAccession(db, s, "workflows/align", "42"));

        var row = ItemWith(html, "workflows/align");
        Assert.Contains(
            @"Open <span class=""wfh-sr-only"">&lt;script&gt;x()&lt;/script&gt; </span>on WorkflowHub</a>", row);
        Assert.DoesNotContain("<script>x()", html);
    }

    [Fact]
    public void WorkflowHub_upload_counts_are_not_empty_when_their_sum_wraps_to_zero()
    {
        Assert.False(new WorkflowHubUploadCounts(int.MaxValue, int.MaxValue, 2, 0, 0).IsEmpty);
        Assert.True(new WorkflowHubUploadCounts(0, 0, 0, 0, 0).IsEmpty);
    }

    [Theory]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1048524, "1023.9 KB")]
    [InlineData(1048525, "1.0 MB")]
    [InlineData(1048575, "1.0 MB")]
    [InlineData(1048576, "1.0 MB")]
    public async Task WorkflowHub_preview_picks_the_size_unit_after_rounding(long bytes, string expected)
    {
        var (html, _, _) = await RenderWorkflowHub(s =>
        {
            var artifact = Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready);
            artifact.SizeBytes = bytes;
            return [artifact];
        });

        Assert.Contains($@"<span class=""wfh-size"">{expected}</span>", html);
        Assert.DoesNotContain("1024.0 KB", html);
    }

    [Theory]
    [InlineData("2", "0", "0", "0", "1", "success")]
    [InlineData("0", "1", "0", "1", "0", "warning")]
    public async Task WorkflowHub_preview_banner_style_ignores_skipped_items_and_counts_unknown_results(
        string uploaded, string failed, string partial, string unknown, string skipped, string style)
    {
        var (html, _, _) = await RenderWorkflowHub(OneReady,
            uploaded: uploaded, failed: failed, partial: partial, unknown: unknown, skipped: skipped);

        Assert.Contains($@"wfh-notice wfh-notice-{style}"" role=""status"" data-wfh-result", html);
    }

    /// <summary>Seeds another submission of the same ARC that uploaded an item to an entry on WorkflowHub.</summary>
    private static void AddEarlierEntry(
        SubmissionDbContext db, string identifier, string number, string? version = null,
        Action<Submission>? change = null)
    {
        var earlier = Submission("workflowhub", "arc", "earlier-hash");
        change?.Invoke(earlier);
        db.Submissions.Add(earlier);
        db.Accessions.Add(new Accession
        {
            SubmissionId = earlier.Id,
            ObjectKind = "workflowhub-workflow",
            Alias = $"workflow/{identifier}",
            AccessionNumber = number,
            SecondaryAccession = version
        });
    }

    private static PreparedArtifact[] AlignAndOther(Submission s)
        =>
        [
            Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready, title: "Align reads"),
            Artifact(s, "workflow", "workflows/other", PreparedArtifactStatus.Ready)
        ];

    [Fact]
    public async Task WorkflowHub_preview_offers_a_new_version_of_the_earlier_entry_with_it_preselected()
    {
        var (html, artifacts, _) = await RenderWorkflowHub(
            AlignAndOther, (db, _) => AddEarlierEntry(db, "workflows/align", "42", "3"));

        var id = artifacts[0].Id;
        var row = ItemWith(html, "workflows/align");
        Assert.Contains("Already on WorkflowHub as entry 42, version 3.", row);
        Assert.Contains(
            """<a href="https://dev.workflowhub.eu/workflows/42" target="_blank" rel="noopener noreferrer">Open <span class="wfh-sr-only">Align reads </span>on WorkflowHub</a>""",
            row);
        Assert.Contains($"""<input type="radio" name="mode-{id}" value="version" checked> Upload as a new version of entry 42</label>""", row);
        Assert.Contains($"""<input type="radio" name="mode-{id}" value="new"> Upload as a new entry</label>""", row);
        Assert.Contains(@"<div class=""wfh-visibility"" data-wfh-visibility>", row);
        Assert.Contains($"""<select id="visibility-{id}" name="visibility-{id}">""", row);
        Assert.Contains("Applies to a new entry only.", row);
        Assert.Contains($"""<input type="checkbox" id="item-{id}" name="item" value="{id}">""", row);

        var other = ItemWith(html, "workflows/other");
        Assert.DoesNotContain("mode-", other);
        Assert.DoesNotContain("Already on WorkflowHub", other);
        Assert.DoesNotContain("data-wfh-visibility", other);
        Assert.DoesNotContain("Applies to a new entry only.", other);
        Assert.Contains(@"<div class=""wfh-visibility"">", other);

        // The entry of an earlier commit is no upload of this submission, so the page has no uploaded section.
        Assert.DoesNotContain("wfh-uploaded-heading", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_leaves_out_the_version_when_the_record_has_none()
    {
        var (html, _, _) = await RenderWorkflowHub(
            AlignAndOther, (db, _) => AddEarlierEntry(db, "workflows/align", "42"));

        var row = ItemWith(html, "workflows/align");
        Assert.Contains("Already on WorkflowHub as entry 42.", row);
        Assert.DoesNotContain("version 3", row);
        Assert.DoesNotContain(", version", row);
        Assert.Contains("Upload as a new version of entry 42", row);
    }

    [Fact]
    public async Task WorkflowHub_preview_escapes_the_item_name_around_the_entry_and_the_choice()
    {
        var (html, _, _) = await RenderWorkflowHub(
            s => [Artifact(s, "workflow", "workflows/align", PreparedArtifactStatus.Ready, title: "<script>x()</script>")],
            (db, _) => AddEarlierEntry(db, "workflows/align", "42", "3"));

        Assert.DoesNotContain("<script>x()", html);
        Assert.Contains(@"Open <span class=""wfh-sr-only"">&lt;script&gt;x()&lt;/script&gt; </span>on WorkflowHub</a>", html);
        Assert.Contains("<legend class=\"wfh-sr-only\">Upload choice for &lt;script&gt;x()&lt;/script&gt;</legend>", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_ignores_earlier_entries_of_the_other_target_and_other_projects()
    {
        var (html, _, _) = await RenderWorkflowHub(
            AlignAndOther,
            (db, _) =>
            {
                AddEarlierEntry(db, "workflows/align", "42", "3", e => e.Target = SubmissionTarget.Production);
                AddEarlierEntry(db, "workflows/other", "43", "1", e => e.ProjectId = 3);
            });

        Assert.DoesNotContain("Already on WorkflowHub", html);
        Assert.DoesNotContain("mode-", html);
        Assert.DoesNotContain("data-wfh-visibility", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_names_the_earlier_entry_without_a_choice_in_a_read_only_item()
    {
        var (html, _, _) = await RenderWorkflowHub(
            AlignAndOther,
            (db, _) => AddEarlierEntry(db, "workflows/align", "42", "3", e => e.Target = SubmissionTarget.Production),
            target: SubmissionTarget.Production,
            submissionOptions: Options.Create(new EndpointSubmissionOptions { AllowProductionTarget = false }));

        var row = ItemWith(html, "workflows/align");
        Assert.Contains("wfh-item-readonly", row);
        Assert.Contains("Already on WorkflowHub as entry 42, version 3.", row);
        Assert.Contains(@"href=""https://workflowhub.eu/workflows/42""", row);
        Assert.DoesNotContain("mode-", html);
        Assert.DoesNotContain("<form", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_keeps_an_item_selectable_with_its_choice_after_a_refused_new_version()
    {
        var refused = "WorkflowHub rejected the new version of entry 42 (HTTP 403): not allowed to edit";
        var (html, artifacts, _) = await RenderWorkflowHub(
            AlignAndOther,
            (db, s) =>
            {
                AddEarlierEntry(db, "workflows/align", "42", "3");
                AddAttempt(db, s, "workflows/align", 1, SubmissionStatus.Failed, refused);
            });

        var row = ItemWith(html, "workflows/align");
        Assert.DoesNotContain("wfh-item-caution", row);
        Assert.DoesNotContain(CautionText, row);
        Assert.Contains($"""<input type="checkbox" id="item-{artifacts[0].Id}" name="item" value="{artifacts[0].Id}">""", row);
        Assert.Contains($"""name="mode-{artifacts[0].Id}" value="version" checked""", row);
        Assert.Contains($"""name="mode-{artifacts[0].Id}" value="new">""", row);
    }

    [Fact]
    public async Task WorkflowHub_preview_flags_an_item_whose_new_version_had_an_unknown_outcome()
    {
        var unknown = $"{WorkflowHubUploadService.UnknownOutcomeLead}workflow workflows/align. A new version of entry 42 may have been created. " +
                      "Check https://dev.workflowhub.eu/workflows/42 before uploading this item again.";
        var (html, artifacts, _) = await RenderWorkflowHub(
            AlignAndOther,
            (db, s) =>
            {
                AddEarlierEntry(db, "workflows/align", "42", "3");
                AddAttempt(db, s, "workflows/align", 1, SubmissionStatus.Failed, unknown);
            });

        var row = ItemWith(html, "workflows/align");
        Assert.StartsWith(@"<li class=""wfh-item wfh-item-ready wfh-item-caution"">", row);
        Assert.Contains(CautionText, row);
        Assert.Contains(CautionLink, row);
        Assert.Contains($"""name="mode-{artifacts[0].Id}" value="version" checked""", row);
    }

    [Fact]
    public async Task WorkflowHub_preview_shows_a_chosen_new_entry_again_when_the_form_comes_back()
    {
        var db = new SubmissionDbContext(new DbContextOptionsBuilder<SubmissionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var submission = Submission("workflowhub", "arc", "hash");
        var artifacts = AlignAndOther(submission);
        db.Submissions.Add(submission);
        db.PreparedArtifacts.AddRange(artifacts);
        AddEarlierEntry(db, "workflows/align", "42", "3");
        await db.SaveChangesAsync();
        var state = new WorkflowHubFormState(
            new HashSet<Guid> { artifacts[0].Id }, new Dictionary<Guid, bool>(), null, null,
            new HashSet<Guid> { artifacts[0].Id });

        var html = await WorkflowHubPreviewHandlers.RenderViewAsync(
            db, submission, "token", "https://dev.workflowhub.eu", "", default, state);

        Assert.Contains($"""name="mode-{artifacts[0].Id}" value="version">""", html);
        Assert.Contains($"""name="mode-{artifacts[0].Id}" value="new" checked>""", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_remembers_the_shown_entry_of_every_selectable_item_inside_the_form()
    {
        var (html, artifacts, _) = await RenderWorkflowHub(
            AlignAndOther, (db, _) => AddEarlierEntry(db, "workflows/align", "42", "3"));

        Assert.Contains(
            $"""<input type="hidden" name="entry-{artifacts[0].Id}" value="42">""", ItemWith(html, "workflows/align"));
        Assert.Contains(
            $"""<input type="hidden" name="entry-{artifacts[1].Id}" value="">""", ItemWith(html, "workflows/other"));

        var form = html[html.IndexOf("<form", StringComparison.Ordinal)..html.IndexOf("</form>", StringComparison.Ordinal)];
        Assert.Equal(2, Regex.Matches(form, @"name=""entry-").Count);
        Assert.Equal(2, Regex.Matches(html, @"name=""entry-").Count);
    }

    [Fact]
    public async Task WorkflowHub_preview_has_no_entry_fields_when_the_items_are_read_only()
    {
        var (html, _, _) = await RenderWorkflowHub(
            AlignAndOther,
            (db, _) => AddEarlierEntry(db, "workflows/align", "42", "3", e => e.Target = SubmissionTarget.Production),
            target: SubmissionTarget.Production,
            submissionOptions: Options.Create(new EndpointSubmissionOptions { AllowProductionTarget = false }));

        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("name=\"entry-", html);
    }

    [Fact]
    public async Task WorkflowHub_preview_shows_the_current_entry_in_the_hidden_field_also_when_the_form_comes_back()
    {
        var db = new SubmissionDbContext(new DbContextOptionsBuilder<SubmissionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var submission = Submission("workflowhub", "arc", "hash");
        var artifacts = AlignAndOther(submission);
        db.Submissions.Add(submission);
        db.PreparedArtifacts.AddRange(artifacts);
        AddEarlierEntry(db, "workflows/align", "55", "3");
        await db.SaveChangesAsync();
        // The user had ticked both items, chose a new entry for the first one and left the second on private.
        var state = new WorkflowHubFormState(
            new HashSet<Guid> { artifacts[0].Id, artifacts[1].Id }, new Dictionary<Guid, bool>(), null, null,
            new HashSet<Guid> { artifacts[0].Id });

        var html = await WorkflowHubPreviewHandlers.RenderViewAsync(
            db, submission, "token", "https://dev.workflowhub.eu", "", default, state);

        Assert.Contains($"""<input type="hidden" name="entry-{artifacts[0].Id}" value="55">""", html);
        Assert.Contains($"""<input type="hidden" name="entry-{artifacts[1].Id}" value="">""", html);
        Assert.Contains($"""name="mode-{artifacts[0].Id}" value="new" checked>""", html);
    }
}
