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

    private static WorkflowHubSelectedItem[] Items(params (PreparedArtifact Artifact, bool MakePublic)[] selected)
        => selected.Select(s => new WorkflowHubSelectedItem(s.Artifact.Id, s.MakePublic)).ToArray();

    private static WorkflowHubSelectedItem Item(PreparedArtifact artifact, bool asNewEntry = false, bool makePublic = false)
        => new(artifact.Id, makePublic, asNewEntry);

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

        var cases = new (string Name, WorkflowHubSelectedItem[] Items, string Expected)[]
        {
            ("empty", [], "Select at least one item"),
            ("not ready", Items((f.Align, false), (f.Blocked, false)), "workflows/blocked is not ready"),
            ("other submission", Items((f.Foreign, false)), "does not belong to this submission"),
            ("unknown id", [new WorkflowHubSelectedItem(Guid.NewGuid(), false)], "does not belong to this submission"),
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

    private const string AlignAlias = "workflow/workflows/align";
    private const string OtherAlias = "workflow/workflows/other";

    /// <summary>Adds another submission of the same ARC: same host, project, endpoint and target as the fixture's.</summary>
    private static Submission AddSubmission(Fixture f, Action<Submission>? change = null)
    {
        var submission = NewSubmission(f.Submission.Target);
        change?.Invoke(submission);
        using var db = f.NewContext();
        db.Submissions.Add(submission);
        db.SaveChanges();
        return submission;
    }

    private static void AddAccession(
        Fixture f, Submission submission, string alias, string number, string? version = null,
        DateTimeOffset? createdAt = null, string objectKind = "workflowhub-workflow", Guid? id = null)
    {
        using var db = f.NewContext();
        db.Accessions.Add(new Accession
        {
            Id = id ?? Guid.NewGuid(),
            SubmissionId = submission.Id,
            ObjectKind = objectKind,
            Alias = alias,
            AccessionNumber = number,
            SecondaryAccession = version,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddHours(-1)
        });
        db.SaveChanges();
    }

    private static async Task<IReadOnlyDictionary<string, WorkflowHubEarlierEntry>> Lookup(Fixture f)
    {
        using var db = f.NewContext();
        var submission = await db.Submissions.AsNoTracking().SingleAsync(s => s.Id == f.Submission.Id);
        return await WorkflowHubEarlierEntries.FindAsync(db, submission, default);
    }

    private static WorkflowHubUpload Version(int entryId, int version, string raw = "{}")
        => new(entryId, $"https://dev.workflowhub.eu/workflows/{entryId}?version={version}", version, false, raw, "", "");

    [Fact]
    public async Task The_lookup_finds_the_record_of_another_submission_with_the_same_host_project_endpoint_and_target()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");

        var entries = await Lookup(f);

        var entry = Assert.Single(entries);
        Assert.Equal(AlignAlias, entry.Key);
        Assert.Equal(new WorkflowHubEarlierEntry(41, 2), entry.Value);
    }

    [Fact]
    public async Task The_lookup_leaves_the_version_open_when_the_record_has_none()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41");

        Assert.Equal(new WorkflowHubEarlierEntry(41, null), (await Lookup(f))[AlignAlias]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_lookup_picks_the_newest_of_several_records(bool newestFirst)
    {
        var f = Build();
        var older = AddSubmission(f);
        var newer = AddSubmission(f);
        var now = DateTimeOffset.UtcNow;
        Action[] adds =
        [
            () => AddAccession(f, older, AlignAlias, "41", "1", createdAt: now.AddHours(-3)),
            () => AddAccession(f, newer, AlignAlias, "55", "3", createdAt: now.AddHours(-2)),
            () => AddAccession(f, older, AlignAlias, "47", "2", createdAt: now.AddHours(-4))
        ];
        foreach (var add in newestFirst ? adds.Reverse() : adds) add();

        Assert.Equal(new WorkflowHubEarlierEntry(55, 3), (await Lookup(f))[AlignAlias]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_lookup_breaks_a_tie_in_creation_time_by_the_larger_record_id(bool largerFirst)
    {
        var f = Build();
        var earlier = AddSubmission(f);
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        var small = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var large = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Action[] adds =
        [
            () => AddAccession(f, earlier, AlignAlias, "41", createdAt: at, id: small),
            () => AddAccession(f, earlier, AlignAlias, "55", createdAt: at, id: large)
        ];
        foreach (var add in largerFirst ? adds.Reverse() : adds) add();

        Assert.Equal(55, (await Lookup(f))[AlignAlias].EntryId);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("host")]
    [InlineData("target")]
    [InlineData("endpoint")]
    [InlineData("alias")]
    [InlineData("object kind")]
    [InlineData("same submission")]
    public async Task The_lookup_ignores_a_newer_record_that_does_not_belong_to_the_item(string difference)
    {
        var f = Build();
        // The valid record is older, so a wrong match would win.
        AddAccession(f, AddSubmission(f), AlignAlias, "99", "1", createdAt: DateTimeOffset.UtcNow.AddHours(-2));

        var otherTarget = f.Submission.Target == SubmissionTarget.Sandbox ? SubmissionTarget.Production : SubmissionTarget.Sandbox;
        var owner = difference switch
        {
            "project" => AddSubmission(f, s => s.ProjectId = 3),
            "host" => AddSubmission(f, s => s.DataHubHost = "other.example.org"),
            "target" => AddSubmission(f, s => s.Target = otherTarget),
            "endpoint" => AddSubmission(f, s => s.Endpoint = "ena"),
            "same submission" => f.Submission,
            _ => AddSubmission(f)
        };
        AddAccession(f, owner, difference == "alias" ? OtherAlias : AlignAlias, "11", "5",
            createdAt: DateTimeOffset.UtcNow.AddHours(-1),
            objectKind: difference == "object kind" ? "workflowhub-run" : "workflowhub-workflow");

        var entries = await Lookup(f);

        Assert.Equal(new WorkflowHubEarlierEntry(99, 1), entries[AlignAlias]);
        if (difference == "alias") Assert.Equal(11, entries[OtherAlias].EntryId);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("4 1")]
    [InlineData(" 41")]
    [InlineData("4.1")]
    [InlineData("")]
    public async Task The_lookup_skips_a_newer_record_whose_accession_number_is_not_a_positive_integer(string number)
    {
        var f = Build();
        var earlier = AddSubmission(f);
        AddAccession(f, earlier, AlignAlias, "41", "2", createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        AddAccession(f, earlier, AlignAlias, number, "9", createdAt: DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(new WorkflowHubEarlierEntry(41, 2), (await Lookup(f))[AlignAlias]);
    }

    [Fact]
    public async Task A_selection_with_a_new_entry_needs_a_team_and_a_version_only_selection_does_not()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");

        Task<bool> Needs(params WorkflowHubSelectedItem[] items) => f.Service.NeedsTeamAsync(f.Submission.Id, items, default);

        Assert.False(await Needs(Item(f.Align)));
        Assert.True(await Needs(Item(f.Align, asNewEntry: true)));
        Assert.True(await Needs(Item(f.Align), Item(f.Other)));
        Assert.True(await Needs(new WorkflowHubSelectedItem(Guid.NewGuid(), false)));
        Assert.True(await f.Service.NeedsTeamAsync(Guid.NewGuid(), [Item(f.Align)], default));
    }

    [Fact]
    public async Task A_new_version_goes_to_the_entry_from_the_lookup_and_is_recorded_without_the_new_entry_call()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");
        f.Gateway.RespondVersion = (_, id) => Version(id, 3, """{"version":"raw"}""");

        // The visibility is for a new entry only. The version ignores it, and so does the missing team.
        var result = await f.Service.UploadAsync(
            f.Submission.Id, [Item(f.Align, makePublic: true)], null, Token, default);

        Assert.Null(result.Problem);
        Assert.Equal(1, result.Succeeded);
        Assert.Empty(f.Gateway.Uploads);
        var call = Assert.Single(f.Gateway.Versions);
        Assert.Equal(("https://dev.workflowhub.eu", Token, 41), (call.BaseUrl, call.Token, call.WorkflowId));
        Assert.Equal(f.Align.Content, call.Crate);

        using var db = f.NewContext();
        var attempt = await db.SubmissionAttempts.SingleAsync();
        Assert.Equal(SubmissionStatus.Submitted, attempt.Status);
        Assert.NotNull(attempt.FinishedAt);

        var payload = await db.SubmissionPayloads.SingleAsync();
        Assert.Equal("workflowhub-crate", payload.ObjectKind);
        using (var json = JsonDocument.Parse(payload.Content))
        {
            var root = json.RootElement;
            Assert.Equal("POST", root.GetProperty("method").GetString());
            Assert.Equal("https://dev.workflowhub.eu/workflows/41/create_version", root.GetProperty("url").GetString());
            Assert.Equal(f.Align.Id, root.GetProperty("artifactId").GetGuid());
            Assert.Equal("workflow", root.GetProperty("itemKind").GetString());
            Assert.Equal("workflows/align", root.GetProperty("itemIdentifier").GetString());
            Assert.Equal("sha-workflows/align", root.GetProperty("sha256").GetString());
            Assert.Equal(3, root.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(41, root.GetProperty("entryId").GetInt32());
            Assert.False(root.TryGetProperty("requestedVisibility", out _));
            Assert.Contains("entry's own", root.GetProperty("teams").GetString());
            Assert.Contains("at upload time", root.GetProperty("teams").GetString());
            var field = Assert.Single(root.GetProperty("fields").EnumerateArray());
            Assert.Equal("ro_crate", field.GetProperty("name").GetString());
        }

        var receipt = await db.SubmissionReceipts.SingleAsync();
        Assert.True(receipt.Success);
        Assert.Equal("""{"version":"raw"}""", receipt.Content);

        var accession = await db.Accessions.SingleAsync(a => a.SubmissionId == f.Submission.Id);
        Assert.Equal(("workflowhub-workflow", AlignAlias, "41", "3"),
            (accession.ObjectKind, accession.Alias, accession.AccessionNumber, accession.SecondaryAccession));

        var uploaded = await db.SubmissionEvents.SingleAsync();
        Assert.Equal("upload.version_succeeded", uploaded.Step);
        Assert.Equal(SubmissionEventLevel.Info, uploaded.Level);
        Assert.Equal("Uploaded workflow workflows/align as version 3 of entry 41.", uploaded.Message);

        Assert.Equal(SubmissionStatus.Submitted, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task The_new_entry_choice_uploads_a_new_entry_although_an_earlier_entry_exists()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");
        f.Gateway.Respond = (_, makePublic) =>
            new WorkflowHubUpload(99, "https://dev.workflowhub.eu/workflows/99?version=1", 1, makePublic, "{}", "{}", "{}");

        var result = await f.Service.UploadAsync(
            f.Submission.Id, [Item(f.Align, asNewEntry: true, makePublic: true)], TeamId, Token, default);

        Assert.Equal(1, result.Succeeded);
        Assert.Empty(f.Gateway.Versions);
        var upload = Assert.Single(f.Gateway.Uploads);
        Assert.Equal((TeamId, true), (upload.TeamId, upload.MakePublic));
        using var db = f.NewContext();
        Assert.Equal("99", (await db.Accessions.SingleAsync(a => a.SubmissionId == f.Submission.Id)).AccessionNumber);
        Assert.Equal("upload.succeeded", (await db.SubmissionEvents.SingleAsync()).Step);
    }

    [Fact]
    public async Task The_entry_of_a_new_version_is_the_newest_stored_one_and_no_item_field_can_name_another()
    {
        var f = Build();
        var earlier = AddSubmission(f);
        AddAccession(f, earlier, AlignAlias, "41", "1", createdAt: DateTimeOffset.UtcNow.AddHours(-3));
        AddAccession(f, earlier, AlignAlias, "55", "4", createdAt: DateTimeOffset.UtcNow.AddHours(-2));

        await f.Service.UploadAsync(f.Submission.Id, [Item(f.Align)], TeamId, Token, default);

        Assert.Equal(55, Assert.Single(f.Gateway.Versions).WorkflowId);
        // The selection carries the item, the visibility and the choice. It has no place for an entry id.
        Assert.Equal(
            new[] { "ArtifactId", "AsNewEntry", "MakePublic" },
            typeof(WorkflowHubSelectedItem).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    private const string EntryRefusalAdvice =
        "You can choose \"Upload as a new entry\" for this item, or get edit rights on entry 41 and try the new version again.";
    private const string TransportAdvice = "Nothing was uploaded for this item. Try again later.";
    private const string LoginAdvice = "WorkflowHub did not accept the login. Start the upload again.";

    [Theory]
    [InlineData(WorkflowHubFailureKind.Rejected, 403, "WorkflowHub rejected the new version of entry 41 (HTTP 403): not allowed to edit", EntryRefusalAdvice)]
    [InlineData(WorkflowHubFailureKind.Rejected, 404, "WorkflowHub rejected the new version of entry 41 (HTTP 404): not allowed to edit", EntryRefusalAdvice)]
    [InlineData(WorkflowHubFailureKind.Rejected, 401, "WorkflowHub rejected the new version of entry 41 (HTTP 401): not allowed to edit", LoginAdvice)]
    [InlineData(WorkflowHubFailureKind.Transport, null, "WorkflowHub could not be reached to add a version to entry 41: not allowed to edit", TransportAdvice)]
    public async Task A_refused_new_version_fails_its_attempt_names_the_entry_and_the_next_item_still_uploads(
        WorkflowHubFailureKind kind, int? status, string summary, string advice)
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");
        f.Gateway.RespondVersion = (_, _) => throw new WorkflowHubGatewayException(
            kind, status, "not allowed to edit", null, rawBody: $"{{\"errors\":[\"{Token} was refused\"]}}");

        var result = await f.Service.UploadAsync(
            f.Submission.Id, [Item(f.Align), Item(f.Other)], TeamId, Token, default);

        Assert.Equal((1, 1, 0), (result.Succeeded, result.Failed, result.Unknown));
        Assert.Equal(f.Other.Content, Assert.Single(f.Gateway.Uploads).Crate);

        using var db = f.NewContext();
        var attempt = await db.SubmissionAttempts.SingleAsync(a => a.AttemptNumber == 1);
        Assert.Equal(SubmissionStatus.Failed, attempt.Status);
        Assert.Equal(summary, attempt.ErrorSummary);
        Assert.NotNull(attempt.FinishedAt);

        var receipt = await db.SubmissionReceipts.SingleAsync(r => r.AttemptId == attempt.Id);
        Assert.False(receipt.Success);
        Assert.Equal("""{"errors":["[REDACTED] was refused"]}""", receipt.Content);

        var failed = await db.SubmissionEvents.SingleAsync(e => e.Step == "upload.failed");
        Assert.Equal(SubmissionEventLevel.Error, failed.Level);
        Assert.Equal(
            "Uploading workflow workflows/align as a new version of entry 41 failed. " + summary + " " + advice,
            failed.Message);

        var accession = await db.Accessions.SingleAsync(a => a.SubmissionId == f.Submission.Id);
        Assert.Equal(OtherAlias, accession.Alias);
        Assert.Equal(SubmissionStatus.PartiallyFailed, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task A_new_version_answered_with_another_entry_is_an_unknown_outcome_and_stores_no_accession()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");
        f.Gateway.RespondVersion = (_, _) => Version(99, 3, $"{{\"id\":99,\"note\":\"{Token}\"}}");

        var result = await f.Service.UploadAsync(f.Submission.Id, [Item(f.Align)], null, Token, default);

        Assert.Equal((0, 0, 1), (result.Succeeded, result.Failed, result.Unknown));
        Assert.Equal(41, Assert.Single(f.Gateway.Versions).WorkflowId);

        using var db = f.NewContext();
        var attempt = await db.SubmissionAttempts.SingleAsync();
        const string message =
            "WorkflowHub did not confirm the upload of workflow workflows/align. A new version of entry 41 may have been created. " +
            "Check https://dev.workflowhub.eu/workflows/41 before uploading this item again.";
        Assert.Equal(SubmissionStatus.Failed, attempt.Status);
        Assert.Equal(message, attempt.ErrorSummary);
        Assert.NotNull(attempt.FinishedAt);

        var unknown = await db.SubmissionEvents.SingleAsync();
        Assert.Equal("upload.outcome_unknown", unknown.Step);
        Assert.Equal(SubmissionEventLevel.Error, unknown.Level);
        Assert.StartsWith(WorkflowHubUploadService.UnknownOutcomeLead, unknown.Message);
        Assert.Equal(message, unknown.Message);

        var receipt = await db.SubmissionReceipts.SingleAsync(r => r.AttemptId == attempt.Id);
        Assert.False(receipt.Success);
        Assert.Equal("""{"id":99,"note":"[REDACTED]"}""", receipt.Content);

        Assert.Empty(db.Accessions.Where(a => a.SubmissionId == f.Submission.Id));
        Assert.Equal(SubmissionStatus.Failed, (await db.Submissions.SingleAsync(s => s.Id == f.Submission.Id)).Status);
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task A_new_version_whose_answer_echoes_the_token_stores_it_nowhere()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");
        f.Gateway.RespondVersion = (_, id) => Version(id, 3, $"{{\"version\":3,\"echo\":\"{Token}\"}}");

        var result = await f.Service.UploadAsync(f.Submission.Id, [Item(f.Align)], null, Token, default);

        Assert.Equal(1, result.Succeeded);
        using var db = f.NewContext();
        var attempt = await db.SubmissionAttempts.SingleAsync();
        Assert.Equal(SubmissionStatus.Submitted, attempt.Status);

        var receipt = await db.SubmissionReceipts.SingleAsync(r => r.AttemptId == attempt.Id);
        Assert.True(receipt.Success);
        Assert.Equal("""{"version":3,"echo":"[REDACTED]"}""", receipt.Content);
        Assert.DoesNotContain(Token, receipt.Content);

        var uploaded = await db.SubmissionEvents.SingleAsync();
        Assert.Equal("upload.version_succeeded", uploaded.Step);
        Assert.DoesNotContain(Token, uploaded.Message);

        var payloads = await db.SubmissionPayloads.Where(p => p.AttemptId == attempt.Id).ToListAsync();
        Assert.NotEmpty(payloads);
        Assert.All(payloads, p => Assert.DoesNotContain(Token, p.Content));
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task A_new_version_without_a_usable_answer_is_an_unknown_outcome_worded_for_a_version()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");
        f.Gateway.RespondVersion = (_, _) => throw new WorkflowHubGatewayException(
            WorkflowHubFailureKind.OutcomeUnknown, null, "WorkflowHub did not answer within 15 minutes.", null,
            rawBody: $"half an answer with {Token}");

        var result = await f.Service.UploadAsync(
            f.Submission.Id, [Item(f.Align), Item(f.Other)], TeamId, Token, default);

        Assert.Equal((1, 0, 1), (result.Succeeded, result.Failed, result.Unknown));
        using var db = f.NewContext();
        var attempt = await db.SubmissionAttempts.SingleAsync(a => a.AttemptNumber == 1);
        const string message =
            "WorkflowHub did not confirm the upload of workflow workflows/align. A new version of entry 41 may have been created. " +
            "Check https://dev.workflowhub.eu/workflows/41 before uploading this item again.";
        Assert.StartsWith(WorkflowHubUploadService.UnknownOutcomeLead, message);
        Assert.Equal(SubmissionStatus.Failed, attempt.Status);
        Assert.Equal(message, attempt.ErrorSummary);

        var unknown = await db.SubmissionEvents.SingleAsync(e => e.Step == "upload.outcome_unknown");
        Assert.Equal(SubmissionEventLevel.Error, unknown.Level);
        Assert.Equal(message, unknown.Message);

        var receipt = await db.SubmissionReceipts.SingleAsync(r => r.AttemptId == attempt.Id);
        Assert.False(receipt.Success);
        Assert.Equal("half an answer with [REDACTED]", receipt.Content);

        Assert.Equal(OtherAlias, (await db.Accessions.SingleAsync(a => a.SubmissionId == f.Submission.Id)).Alias);
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task An_unexpected_exception_during_a_new_version_is_an_unknown_outcome_logged_by_type_only()
    {
        var logger = new CapturingLogger<WorkflowHubUploadService>();
        var f = Build(logger: logger);
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");
        f.Gateway.RespondVersion = (_, _) => throw new InvalidOperationException($"boom with {Token}");

        var result = await f.Service.UploadAsync(f.Submission.Id, [Item(f.Align)], null, Token, default);

        Assert.Equal(1, result.Unknown);
        using var db = f.NewContext();
        var attempt = await db.SubmissionAttempts.SingleAsync();
        Assert.StartsWith(WorkflowHubUploadService.UnknownOutcomeLead, attempt.ErrorSummary);
        Assert.Contains("A new version of entry 41 may have been created.", attempt.ErrorSummary);
        Assert.Equal(
            "Unexpected failure: InvalidOperationException",
            (await db.SubmissionReceipts.SingleAsync(r => r.AttemptId == attempt.Id)).Content);
        Assert.Empty(db.Accessions.Where(a => a.SubmissionId == f.Submission.Id));

        Assert.All(logger.Messages, m => Assert.DoesNotContain("boom", m));
        Assert.All(logger.Messages, m => Assert.DoesNotContain(Token, m));
        await AssertTokenNotStored(f.Options);
    }

    [Fact]
    public async Task A_visibility_failure_reported_for_a_new_version_is_treated_as_an_unclear_result()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");
        f.Gateway.RespondVersion = (_, _) => throw new WorkflowHubGatewayException(
            WorkflowHubFailureKind.UploadedButPolicyUnset, null, $"status 500: boom {Token}", 41, rawBody: "");

        var result = await f.Service.UploadAsync(f.Submission.Id, [Item(f.Align)], null, Token, default);

        Assert.Equal(1, result.Unknown);
        using var db = f.NewContext();
        Assert.Equal(SubmissionStatus.Failed, (await db.SubmissionAttempts.SingleAsync()).Status);
        Assert.Equal("status 500: boom [REDACTED]", (await db.SubmissionReceipts.SingleAsync()).Content);
        Assert.Empty(db.Accessions.Where(a => a.SubmissionId == f.Submission.Id));
        await AssertTokenNotStored(f.Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_item_already_uploaded_from_this_submission_is_refused_whatever_the_choice(bool asNewEntry)
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");
        AddAccession(f, f.Submission, AlignAlias, "77");

        var result = await f.Service.UploadAsync(f.Submission.Id, [Item(f.Align, asNewEntry)], TeamId, Token, default);

        Assert.Contains("workflows/align was uploaded already", result.Problem);
        Assert.Equal(result.Problem, await f.Service.ValidateSelection(f.Submission.Id, [Item(f.Align, asNewEntry)], default));
        Assert.Empty(f.Gateway.Uploads);
        Assert.Empty(f.Gateway.Versions);
        using var db = f.NewContext();
        Assert.Empty(db.SubmissionAttempts);
    }

    [Fact]
    public async Task An_item_that_gained_an_accession_during_a_run_of_new_versions_is_skipped()
    {
        var f = Build();
        var earlier = AddSubmission(f);
        AddAccession(f, earlier, AlignAlias, "41", "2");
        AddAccession(f, earlier, OtherAlias, "42", "2");
        f.Gateway.RespondVersion = (_, id) =>
        {
            // Another request records the second item while the first one uploads.
            AddAccession(f, f.Submission, OtherAlias, "77");
            return Version(id, 3);
        };

        var result = await f.Service.UploadAsync(f.Submission.Id, [Item(f.Align), Item(f.Other)], null, Token, default);

        Assert.Equal((1, 1), (result.Succeeded, result.Skipped));
        Assert.Equal(41, Assert.Single(f.Gateway.Versions).WorkflowId);
        Assert.Empty(f.Gateway.Uploads);
        using var db = f.NewContext();
        Assert.Single(db.SubmissionEvents, e => e.Step == "upload.skipped");
    }

    [Fact]
    public async Task A_new_entry_without_a_team_fails_that_item_before_anything_is_sent_and_versions_still_upload()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2");

        var result = await f.Service.UploadAsync(f.Submission.Id, [Item(f.Align), Item(f.Other)], null, Token, default);

        Assert.Equal((1, 1), (result.Succeeded, result.Failed));
        Assert.Empty(f.Gateway.Uploads);
        Assert.Equal(41, Assert.Single(f.Gateway.Versions).WorkflowId);
        using var db = f.NewContext();
        Assert.Equal(1, await db.SubmissionAttempts.CountAsync());
        var failed = await db.SubmissionEvents.SingleAsync(e => e.Step == "upload.failed");
        Assert.Equal(SubmissionEventLevel.Error, failed.Level);
        Assert.Contains("workflow workflows/other needs a new WorkflowHub entry, which needs a team", failed.Message);
    }

    [Fact]
    public async Task A_later_submission_after_a_new_entry_choice_resolves_to_the_newest_entry()
    {
        var f = Build();
        AddAccession(f, AddSubmission(f), AlignAlias, "41", "2", createdAt: DateTimeOffset.UtcNow.AddDays(-1));
        f.Gateway.Respond = (_, makePublic) =>
            new WorkflowHubUpload(99, "https://dev.workflowhub.eu/workflows/99?version=1", 1, makePublic, "{}", "{}", "{}");

        await f.Service.UploadAsync(f.Submission.Id, [Item(f.Align, asNewEntry: true)], TeamId, Token, default);

        var later = AddSubmission(f);
        using var db = f.NewContext();
        var entries = await WorkflowHubEarlierEntries.FindAsync(db, later, default);
        Assert.Equal(new WorkflowHubEarlierEntry(99, 1), entries[AlignAlias]);
    }
}
