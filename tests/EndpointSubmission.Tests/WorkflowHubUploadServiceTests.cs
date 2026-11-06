using System.Text.Json;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Endpoints.WorkflowHub;
using EndpointSubmission.Service.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointSubmission.Tests;

public class WorkflowHubUploadServiceTests
{
    private const string Token = "wfh-secret-token-4711";
    private const int TeamId = 7;

    private sealed record Fixture(
        WorkflowHubUploadService Service,
        FakeWorkflowHubGateway Gateway,
        DbContextOptions<SubmissionDbContext> Options,
        Submission Submission,
        PreparedArtifact Align,
        PreparedArtifact Other,
        PreparedArtifact Blocked,
        PreparedArtifact Foreign)
    {
        public SubmissionDbContext NewContext() => new(Options);
    }

    private static Fixture Build(
        SubmissionTarget target = SubmissionTarget.Sandbox, ILogger<WorkflowHubUploadService>? logger = null)
    {
        var options = new DbContextOptionsBuilder<SubmissionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new SubmissionDbContext(options);

        var submission = NewSubmission(target);
        var foreignSubmission = NewSubmission(target);
        var align = Artifact(submission, "workflows/align", PreparedArtifactStatus.Ready, [1, 2, 3]);
        var other = Artifact(submission, "workflows/other", PreparedArtifactStatus.Ready, [4, 5]);
        var blocked = Artifact(submission, "workflows/blocked", PreparedArtifactStatus.NotReady, null);
        var foreign = Artifact(foreignSubmission, "workflows/foreign", PreparedArtifactStatus.Ready, [6]);
        db.Submissions.AddRange(submission, foreignSubmission);
        db.PreparedArtifacts.AddRange(align, other, blocked, foreign);
        db.SaveChanges();

        var gateway = new FakeWorkflowHubGateway();
        var service = new WorkflowHubUploadService(
            db, gateway, Microsoft.Extensions.Options.Options.Create(new WorkflowHubOptions()),
            logger ?? NullLogger<WorkflowHubUploadService>.Instance);
        return new Fixture(service, gateway, options, submission, align, other, blocked, foreign);
    }

    private static Submission NewSubmission(SubmissionTarget target) => new()
    {
        ProjectId = 2,
        ProjectName = "group/my-arc",
        DataHubHost = "git.example.org",
        CommitSha = Guid.NewGuid().ToString("N"),
        RoCrateUrl = "https://git.example.org/crate.json",
        Endpoint = "workflowhub",
        Target = target,
        SubmitterGitLabUser = "user",
        SubmitterEmail = "user@example.org",
        StatusTokenHash = "hash",
        Status = SubmissionStatus.PreviewReady
    };

    private static PreparedArtifact Artifact(
        Submission submission, string identifier, PreparedArtifactStatus status, byte[]? content) => new()
    {
        SubmissionId = submission.Id,
        ItemKind = "workflow",
        ItemIdentifier = identifier,
        Status = status,
        SizeBytes = content?.LongLength,
        Content = content,
        ContentSha256 = content is null ? null : "sha-" + identifier
    };

    private static (Guid, bool)[] Items(params (PreparedArtifact Artifact, bool MakePublic)[] selected)
        => selected.Select(s => (s.Artifact.Id, s.MakePublic)).ToArray();

    private static WorkflowHubGatewayException Rejected(string detail, string rawBody = "")
        => new(WorkflowHubFailureKind.Rejected, 422, detail, null, rawBody);

    private static WorkflowHubUpload Uploaded(bool makePublic)
        => new(41, "https://dev.workflowhub.eu/workflows/41", 1, makePublic, "{}", "{}", "{}");

    private static async Task AssertTokenNotStored(DbContextOptions<SubmissionDbContext> options)
    {
        using var db = new SubmissionDbContext(options);
        await StoredStrings.AssertNoneContain(db, Token);
    }

    [Fact]
    public async Task Upload_sends_each_selected_crate_with_its_visibility_and_records_the_result()
    {
        var f = Build();

        var result = await f.Service.UploadAsync(
            f.Submission.Id, Items((f.Align, false), (f.Other, true)), TeamId, Token, default);

        Assert.Null(result.Problem);
        Assert.Equal(2, result.Succeeded);

        Assert.Collection(f.Gateway.Uploads,
            u =>
            {
                Assert.Equal("https://dev.workflowhub.eu", u.BaseUrl);
                Assert.Equal(Token, u.Token);
                Assert.Equal(f.Align.Content, u.Crate);
                Assert.Equal(TeamId, u.TeamId);
                Assert.False(u.MakePublic);
            },
            u =>
            {
                Assert.Equal(f.Other.Content, u.Crate);
                Assert.True(u.MakePublic);
            });

        using var db = f.NewContext();
        var attempts = await db.SubmissionAttempts.OrderBy(a => a.AttemptNumber).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, attempts.Select(a => a.AttemptNumber));
        Assert.All(attempts, a =>
        {
            Assert.Equal(SubmissionStatus.Submitted, a.Status);
            Assert.NotNull(a.FinishedAt);
        });

        var payloads = await db.SubmissionPayloads.Where(p => p.AttemptId == attempts[0].Id).ToListAsync();
        var crate = Assert.Single(payloads, p => p.ObjectKind == "workflowhub-crate");
        using (var json = JsonDocument.Parse(crate.Content))
        {
            var root = json.RootElement;
            Assert.Equal("POST", root.GetProperty("method").GetString());
            Assert.Equal("https://dev.workflowhub.eu/workflows", root.GetProperty("url").GetString());
            Assert.Equal(f.Align.Id, root.GetProperty("artifactId").GetGuid());
            Assert.Equal("workflow", root.GetProperty("itemKind").GetString());
            Assert.Equal("workflows/align", root.GetProperty("itemIdentifier").GetString());
            Assert.Equal("sha-workflows/align", root.GetProperty("sha256").GetString());
            Assert.Equal(3, root.GetProperty("sizeBytes").GetInt64());
            Assert.Equal("private", root.GetProperty("requestedVisibility").GetString());

            var fields = root.GetProperty("fields").EnumerateArray().ToList();
            Assert.Equal(2, fields.Count);
            Assert.Equal("ro_crate", fields[0].GetProperty("name").GetString());
            Assert.Equal("ro-crate.zip", fields[0].GetProperty("fileName").GetString());
            Assert.Equal("application/zip", fields[0].GetProperty("contentType").GetString());
            Assert.Equal("workflow[project_ids][]", fields[1].GetProperty("name").GetString());
            Assert.Equal(TeamId.ToString(), fields[1].GetProperty("value").GetString());
        }

        var policy = Assert.Single(payloads, p => p.ObjectKind == "workflowhub-policy");
        Assert.Equal("""{"policy":0}""", policy.Content);
        Assert.Equal(2, payloads.Count);

        var receipts = (await db.SubmissionReceipts.Where(r => r.AttemptId == attempts[0].Id).ToListAsync())
            .OrderBy(r => r.ReceivedAt).ToList();
        Assert.Equal(["""{"raw":0}""", """{"policyResponse":0}"""], receipts.Select(r => r.Content).ToArray());
        Assert.All(receipts, r => Assert.True(r.Success));

        var accession = await db.Accessions.SingleAsync(a => a.Alias == "workflow/workflows/align");
        Assert.Equal("workflowhub-workflow", accession.ObjectKind);
        Assert.Equal("41", accession.AccessionNumber);
        Assert.Equal("1", accession.SecondaryAccession);

        var succeeded = await db.SubmissionEvents.Where(e => e.Step == "upload.succeeded").OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, succeeded.Count);
        Assert.Equal(SubmissionEventLevel.Info, succeeded[0].Level);
        Assert.Equal(
            "Uploaded workflow workflows/align to WorkflowHub as https://dev.workflowhub.eu/workflows/41?version=1 (private).",
            succeeded[0].Message);
        Assert.EndsWith("(public).", succeeded[1].Message);

        Assert.Equal(SubmissionStatus.Submitted, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task A_rejected_upload_fails_the_attempt_and_the_submission_without_storing_the_token()
    {
        var f = Build();
        f.Gateway.Respond = (_, _) => throw Rejected("HTTP detail", rawBody: $"{{\"error\":\"{Token} was refused\"}}");

        var result = await f.Service.UploadAsync(f.Submission.Id, Items((f.Align, false)), TeamId, Token, default);

        Assert.Equal(1, result.Failed);
        using var db = f.NewContext();
        var attempt = await db.SubmissionAttempts.SingleAsync();
        Assert.Equal(SubmissionStatus.Failed, attempt.Status);
        Assert.Contains("HTTP 422", attempt.ErrorSummary);
        Assert.NotNull(attempt.FinishedAt);

        var receipt = await db.SubmissionReceipts.SingleAsync();
        Assert.False(receipt.Success);
        Assert.Equal("""{"error":"[REDACTED] was refused"}""", receipt.Content);

        var failed = await db.SubmissionEvents.SingleAsync(e => e.Step == "upload.failed");
        Assert.Equal(SubmissionEventLevel.Error, failed.Level);
        Assert.Empty(db.Accessions);
        Assert.Equal(SubmissionStatus.Failed, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task An_upload_whose_visibility_could_not_be_set_keeps_its_accession_and_warns()
    {
        var f = Build();
        f.Gateway.Respond = (_, _) => throw new WorkflowHubGatewayException(
            WorkflowHubFailureKind.UploadedButPolicyUnset, null, "status 500: boom", 99, rawBody: """{"post":"answer"}""");

        var result = await f.Service.UploadAsync(f.Submission.Id, Items((f.Align, false)), TeamId, Token, default);

        Assert.Equal(1, result.PartiallyFailed);
        using var db = f.NewContext();
        var accession = await db.Accessions.SingleAsync();
        Assert.Equal("workflow/workflows/align", accession.Alias);
        Assert.Equal("99", accession.AccessionNumber);

        var receipts = (await db.SubmissionReceipts.ToListAsync()).OrderBy(r => r.ReceivedAt).ToList();
        Assert.Equal(
            new[] { (true, """{"post":"answer"}"""), (false, "status 500: boom") },
            receipts.Select(r => (r.Success, r.Content)).ToArray());
        Assert.Equal(SubmissionStatus.PartiallyFailed, (await db.SubmissionAttempts.SingleAsync()).Status);

        var warning = await db.SubmissionEvents.SingleAsync(e => e.Level == SubmissionEventLevel.Warning);
        Assert.Equal(
            "Uploaded workflow workflows/align as https://dev.workflowhub.eu/workflows/99, but setting it to private failed. It is public now. Change its visibility on WorkflowHub.",
            warning.Message);
        Assert.Equal(SubmissionStatus.PartiallyFailed, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
    }

    [Fact]
    public async Task A_failed_visibility_request_keeps_its_exact_body_and_the_raw_answer()
    {
        var f = Build();
        const string patchBody = """{"data":{"id":"99","type":"workflows","attributes":{"policy":{"access":"no_access"}}}}""";
        f.Gateway.Respond = (_, _) => throw new WorkflowHubGatewayException(
            WorkflowHubFailureKind.UploadedButPolicyUnset, null, "status 500: boom", 99,
            rawBody: """{"post":"answer"}""",
            policyRequest: patchBody,
            policyRawBody: $$"""{"errors":[{"detail":"server fault for {{Token}}"}]}""");

        var result = await f.Service.UploadAsync(f.Submission.Id, Items((f.Align, false)), TeamId, Token, default);

        Assert.Equal(1, result.PartiallyFailed);
        using var db = f.NewContext();

        var policy = Assert.Single(await db.SubmissionPayloads.ToListAsync(), p => p.ObjectKind == "workflowhub-policy");
        Assert.Equal(patchBody, policy.Content);

        var receipts = (await db.SubmissionReceipts.ToListAsync()).OrderBy(r => r.ReceivedAt).ToList();
        Assert.Equal(
            new[]
            {
                (true, """{"post":"answer"}"""),
                (false, """{"errors":[{"detail":"server fault for [REDACTED]"}]}""")
            },
            receipts.Select(r => (r.Success, r.Content)).ToArray());
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task The_submission_is_partially_failed_when_only_some_items_succeed()
    {
        var f = Build();
        f.Gateway.Respond = (n, makePublic) => n == 0 ? Uploaded(makePublic) : throw Rejected("no");

        var result = await f.Service.UploadAsync(
            f.Submission.Id, Items((f.Align, false), (f.Other, false)), TeamId, Token, default);

        Assert.Equal((1, 1), (result.Succeeded, result.Failed));
        using var db = f.NewContext();
        Assert.Equal(SubmissionStatus.PartiallyFailed, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
    }

    [Fact]
    public async Task An_item_that_gained_an_accession_during_the_run_is_skipped()
    {
        var f = Build();
        f.Gateway.Respond = (n, makePublic) =>
        {
            // Another request records the second item while the first one uploads.
            using var other = f.NewContext();
            other.Accessions.Add(new Accession
            {
                SubmissionId = f.Submission.Id, ObjectKind = "workflowhub-workflow",
                Alias = "workflow/workflows/other", AccessionNumber = "77"
            });
            other.SaveChanges();
            return Uploaded(makePublic);
        };

        var result = await f.Service.UploadAsync(
            f.Submission.Id, Items((f.Align, false), (f.Other, false)), TeamId, Token, default);

        Assert.Equal((1, 1), (result.Succeeded, result.Skipped));
        Assert.Single(f.Gateway.Uploads);
        using var db = f.NewContext();
        var skipped = await db.SubmissionEvents.SingleAsync(e => e.Step == "upload.skipped");
        Assert.Equal(SubmissionEventLevel.Info, skipped.Level);
        Assert.Equal(SubmissionStatus.Submitted, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
    }

    [Fact]
    public async Task An_upload_without_a_usable_answer_is_an_unknown_outcome_and_the_next_item_still_uploads()
    {
        var f = Build();
        f.Gateway.Respond = (n, makePublic) => n == 0
            ? throw new WorkflowHubGatewayException(
                WorkflowHubFailureKind.OutcomeUnknown, null, "WorkflowHub did not answer within 15 minutes.", null,
                rawBody: $"half an answer with {Token}")
            : Uploaded(makePublic);

        var result = await f.Service.UploadAsync(
            f.Submission.Id, Items((f.Align, false), (f.Other, false)), TeamId, Token, default);

        Assert.Equal((1, 0, 1), (result.Succeeded, result.Failed, result.Unknown));
        Assert.Equal(2, f.Gateway.Uploads.Count);

        using var db = f.NewContext();
        var attempt = await db.SubmissionAttempts.SingleAsync(a => a.AttemptNumber == 1);
        const string message =
            "WorkflowHub did not confirm the upload of workflow workflows/align. The entry may exist on WorkflowHub " +
            "and may be public. Check https://dev.workflowhub.eu/workflows before uploading this item again.";
        Assert.Equal(SubmissionStatus.Failed, attempt.Status);
        Assert.Equal(message, attempt.ErrorSummary);
        Assert.NotNull(attempt.FinishedAt);

        var unknown = await db.SubmissionEvents.SingleAsync(e => e.Step == "upload.outcome_unknown");
        Assert.Equal(SubmissionEventLevel.Error, unknown.Level);
        Assert.Equal(message, unknown.Message);

        var receipt = await db.SubmissionReceipts.SingleAsync(r => r.AttemptId == attempt.Id);
        Assert.False(receipt.Success);
        Assert.Equal("half an answer with [REDACTED]", receipt.Content);

        Assert.Equal("workflow/workflows/other", (await db.Accessions.SingleAsync()).Alias);
        Assert.Equal(SubmissionStatus.PartiallyFailed, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task An_unexpected_exception_is_an_unknown_outcome_logged_by_type_only_and_the_next_item_still_uploads()
    {
        var logger = new CapturingLogger<WorkflowHubUploadService>();
        var f = Build(logger: logger);
        f.Gateway.Respond = (n, makePublic) => n == 0
            ? throw new InvalidOperationException($"boom with {Token}")
            : Uploaded(makePublic);

        var result = await f.Service.UploadAsync(
            f.Submission.Id, Items((f.Align, false), (f.Other, false)), TeamId, Token, default);

        Assert.Equal((1, 0, 1), (result.Succeeded, result.Failed, result.Unknown));
        using var db = f.NewContext();
        var attempt = await db.SubmissionAttempts.SingleAsync(a => a.AttemptNumber == 1);
        Assert.Equal(SubmissionStatus.Failed, attempt.Status);
        Assert.StartsWith("WorkflowHub did not confirm the upload of workflow workflows/align.", attempt.ErrorSummary);
        Assert.Equal("upload.outcome_unknown", (await db.SubmissionEvents.SingleAsync(e => e.Level == SubmissionEventLevel.Error)).Step);
        Assert.Equal(
            "Unexpected failure: InvalidOperationException",
            (await db.SubmissionReceipts.SingleAsync(r => r.AttemptId == attempt.Id)).Content);

        var logged = Assert.Single(logger.Messages, m => m.Contains(nameof(InvalidOperationException)));
        Assert.Contains(f.Submission.Id.ToString(), logged);
        Assert.All(logger.Messages, m => Assert.DoesNotContain("boom", m));
        Assert.All(logger.Messages, m => Assert.DoesNotContain(Token, m));
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task The_status_keeps_what_an_earlier_run_uploaded_and_fails_only_without_any_accession()
    {
        var f = Build();
        f.Gateway.Respond = (n, makePublic) => n == 0 ? Uploaded(makePublic) : throw Rejected("no");

        await f.Service.UploadAsync(f.Submission.Id, Items((f.Align, false)), TeamId, Token, default);
        using (var db = f.NewContext())
            Assert.Equal(SubmissionStatus.Submitted, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);

        await f.Service.UploadAsync(f.Submission.Id, Items((f.Other, false)), TeamId, Token, default);
        using (var db = f.NewContext())
        {
            Assert.Equal(SubmissionStatus.PartiallyFailed, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
            Assert.Single(db.Accessions);
        }

        var none = Build();
        none.Gateway.Respond = (_, _) => throw Rejected("no");
        await none.Service.UploadAsync(none.Submission.Id, Items((none.Align, false)), TeamId, Token, default);
        using var check = none.NewContext();
        Assert.Equal(SubmissionStatus.Failed, (await check.Submissions.SingleAsync(s => s.Id == none.Submission.Id)).Status);
    }

    [Fact]
    public async Task Invalid_selections_are_refused_before_anything_is_sent()
    {
        var f = Build();
        using (var db = f.NewContext())
        {
            db.Accessions.Add(new Accession
            {
                SubmissionId = f.Submission.Id, ObjectKind = "workflowhub-workflow",
                Alias = "workflow/workflows/other", AccessionNumber = "77"
            });
            db.SaveChanges();
        }

        var cases = new (string Name, (Guid, bool)[] Items, string Expected)[]
        {
            ("empty", [], "Select at least one item"),
            ("not ready", Items((f.Align, false), (f.Blocked, false)), "workflows/blocked is not ready"),
            ("other submission", Items((f.Foreign, false)), "does not belong to this submission"),
            ("unknown id", [(Guid.NewGuid(), false)], "does not belong to this submission"),
            ("already uploaded", Items((f.Other, false)), "workflows/other was uploaded already"),
            ("listed twice", Items((f.Align, false), (f.Align, true)), "selected twice")
        };

        foreach (var (name, items, expected) in cases)
        {
            var result = await f.Service.UploadAsync(f.Submission.Id, items, TeamId, Token, default);
            Assert.True(result.Problem is not null && result.Problem.Contains(expected), $"{name}: {result.Problem}");
        }

        var unknown = await f.Service.UploadAsync(Guid.NewGuid(), Items((f.Align, false)), TeamId, Token, default);
        Assert.NotNull(unknown.Problem);

        Assert.Empty(f.Gateway.Uploads);
        using var check = f.NewContext();
        Assert.Empty(check.SubmissionAttempts);
        Assert.Equal(SubmissionStatus.PreviewReady, (await check.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
    }

    [Fact]
    public async Task Listing_teams_uses_the_target_instance_and_records_a_warning_when_there_are_none()
    {
        var f = Build(SubmissionTarget.Production);

        Assert.Empty(await f.Service.ListTeamsAsync(f.Submission.Id, Token, default));
        Assert.Equal(("https://workflowhub.eu", Token), Assert.Single(f.Gateway.TeamLists));
        using (var db = f.NewContext())
        {
            var warning = await db.SubmissionEvents.SingleAsync();
            Assert.Equal("upload.no_team", warning.Step);
            Assert.Equal(SubmissionEventLevel.Warning, warning.Level);
        }

        f.Gateway.Teams = [new WorkflowHubTeam(5, "Team Five")];
        Assert.Equal(5, Assert.Single(await f.Service.ListTeamsAsync(f.Submission.Id, Token, default)).Id);
        using var after = f.NewContext();
        Assert.Single(after.SubmissionEvents);
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public void The_no_team_page_explains_the_membership_requirement_and_links_to_the_teams()
    {
        var html = WorkflowHubResultPages.NoTeam("https://dev.workflowhub.eu/");

        Assert.Contains("<h1>Nothing was uploaded</h1>", html);
        Assert.Contains("Uploading to WorkflowHub requires membership in a WorkflowHub team", html);
        Assert.Contains(
            """<a href="https://dev.workflowhub.eu/projects" target="_blank" rel="noopener noreferrer" role="button">WorkflowHub teams</a>""",
            html);
        Assert.Contains("&lt;x&gt;", WorkflowHubResultPages.NoTeam("https://a/<x>"));
        Assert.DoesNotContain("Back to the submission", html);
        Assert.Contains(@"href=""/css/workflowhub.css""", html);

        var withBack = WorkflowHubResultPages.NoTeam(
            "https://dev.workflowhub.eu", "/workflowhub/submissions/1?t=a&b=\"<x>");
        Assert.Contains(
            """<a href="/workflowhub/submissions/1?t=a&amp;b=&quot;&lt;x&gt;" role="button" class="outline">Back to the submission</a>""",
            withBack);
    }
}
