using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Endpoints.ENA;
using EndpointSubmission.Service.Endpoints.WorkflowHub;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Pages;
using EndpointSubmission.Service.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointSubmission.Tests;

public class SubmissionIntakeServiceTests
{
    private const string RoCrateUrl =
        "https://gitdev.nfdi4plants.org/api/v4/projects/2/packages/generic/isa_arc_json/0.0.1/arc-ro-crate-metadata.json";

    /// <summary>
    /// Records what it was asked for, so tests can assert the token was actually used during the
    /// hook and not deferred. Failures are raised as the DataHubClient library's own error types,
    /// because that is what the intake path maps onto its result cases.
    /// </summary>
    private sealed class FakeDataHub : IArcMetadataReader
    {
        public ArcSubmitter Submitter { get; set; } = new(7, "kschneider", "kschneider@example.org");
        public string? Commit { get; set; } = "0123456789abcdef0123456789abcdef01234567";
        public string RoCrate { get; set; } = """{"@context":"https://w3id.org/ro/crate/1.1/context"}""";

        /// <summary>Thrown by every method when set, standing in for a DataHUB failure.</summary>
        public Exception? Fail { get; set; }

        public List<string> TokensSeen { get; } = [];
        public List<RoCrateLocation> LocationsRequested { get; } = [];

        public Task<ArcSubmitter> GetSubmitterAsync(Uri instance, string token, CancellationToken ct)
        { TokensSeen.Add(token); return Fail is null ? Task.FromResult(Submitter) : Task.FromException<ArcSubmitter>(Fail); }

        public Task<string?> ResolveHeadCommitAsync(Uri instance, int projectId, string token, CancellationToken ct)
        { TokensSeen.Add(token); return Fail is null ? Task.FromResult(Commit) : Task.FromException<string?>(Fail); }

        public Task<string> FetchRoCrateAsync(RoCrateLocation location, string token, CancellationToken ct)
        {
            TokensSeen.Add(token);
            LocationsRequested.Add(location);
            return Fail is null ? Task.FromResult(RoCrate) : Task.FromException<string>(Fail);
        }
    }

    /// <summary>
    /// Stands in for the crate packer. Records the token and instance it was handed, so tests can
    /// assert the PAT reached preparation during the hook and nowhere else.
    /// </summary>
    private sealed class FakeCratePacker : IWorkflowCratePacker
    {
        public WorkflowPreparation Result { get; set; } = new([], null, true, null);

        /// <summary>Thrown instead of returning <see cref="Result"/> when set.</summary>
        public Exception? Fail { get; set; }

        /// <summary>Awaited inside the call before it answers, to hold a preparation open.</summary>
        public Func<CancellationToken, Task>? Gate { get; set; }

        public List<string> TokensSeen { get; } = [];
        public List<Uri> InstancesSeen { get; } = [];

        public async Task<WorkflowPreparation> PrepareAsync(
            Uri dataHubInstance, int projectId, string commitSha, string token, CancellationToken ct)
        {
            TokensSeen.Add(token);
            InstancesSeen.Add(dataHubInstance);
            if (Gate is not null) await Gate(ct);
            return Fail is null ? Result : throw Fail;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + exception);
    }

    private static (EnaSubmissionHandler Service, SubmissionDbContext Db, FakeDataHub Hub) Build(
        bool allowProduction = false)
    {
        var db = new SubmissionDbContext(new DbContextOptionsBuilder<SubmissionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var options = Options.Create(new EndpointSubmissionOptions
        {
            AllowedDataHubHosts = ["gitdev.nfdi4plants.org"],
            AllowProductionTarget = allowProduction,
            PublicBaseUrl = "https://submit.example.org",
            HookMaxAgeSeconds = 300,
            HookSharedSecret = "secret"
        });

        var hub = new FakeDataHub();
        var intake = new SubmissionIntakeService(
            db, hub, new DataHubUrlGuard(options), new StatusTokenService(), options,
            NullLogger<SubmissionIntakeService>.Instance);

        var service = new EnaSubmissionHandler(intake);

        return (service, db, hub);
    }

    private static (WorkflowHubSubmissionHandler Service, SubmissionDbContext Db, FakeDataHub Hub, FakeCratePacker Packer)
        BuildWorkflowHub(ILogger<WorkflowHubSubmissionHandler>? logger = null, WorkflowHubOptions? workflowHubOptions = null)
    {
        var db = new SubmissionDbContext(new DbContextOptionsBuilder<SubmissionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var options = Options.Create(new EndpointSubmissionOptions
        {
            AllowedDataHubHosts = ["gitdev.nfdi4plants.org"],
            PublicBaseUrl = "https://submit.example.org",
            HookMaxAgeSeconds = 300,
            HookSharedSecret = "secret"
        });
        var hub = new FakeDataHub();
        var intake = new SubmissionIntakeService(
            db, hub, new DataHubUrlGuard(options), new StatusTokenService(), options,
            NullLogger<SubmissionIntakeService>.Instance);

        var packer = new FakeCratePacker();
        var handler = new WorkflowHubSubmissionHandler(
            intake, db, packer, new KeyedAsyncLock(), Options.Create(workflowHubOptions ?? new WorkflowHubOptions()),
            logger ?? NullLogger<WorkflowHubSubmissionHandler>.Instance);

        return (handler, db, hub, packer);
    }

    private static HookRequest Hook(object? packagePayload = null, long? iat = null, string? roCrateUrl = RoCrateUrl) =>
        new()
        {
            IssuedAt = iat ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            PackageEndpoint = "https://submit.example.org/api/v1/hooks/ena",
            PackagePayload = JsonSerializer.SerializeToElement(packagePayload ?? new { }),
            ProjectId = 2,
            ProjectName = "group/my-arc",
            GitLabToken = "glpat-short-lived",
            RoCrateUrl = roCrateUrl
        };

    [Fact]
    public async Task Accepts_a_valid_hook_and_returns_a_follow_me_url()
    {
        var (service, db, hub) = Build();

        var result = Assert.IsType<IntakeResult.Accepted>(
            await service.HandleAsync(Hook(), default));

        Assert.StartsWith("https://submit.example.org/ena/submissions/", result.FollowMe);
        Assert.Contains("?t=", result.FollowMe);

        var stored = await db.Submissions.SingleAsync();
        Assert.Equal(2, stored.ProjectId);
        Assert.Equal("group/my-arc", stored.ProjectName);
        Assert.Equal("gitdev.nfdi4plants.org", stored.DataHubHost);
        Assert.Equal(SubmissionTarget.Sandbox, stored.Target);
        Assert.Equal(SubmissionStatus.Created, stored.Status);
        Assert.Equal(hub.RoCrate, stored.RoCrateJson);
    }

    [Fact]
    public async Task WorkflowHub_uses_the_same_submission_model_with_its_own_route()
    {
        var (service, db, hub, _) = BuildWorkflowHub();

        var result = Assert.IsType<IntakeResult.Accepted>(
            await service.HandleAsync(Hook(new { target = "sandbox", contract_version = 1 }), default));

        Assert.StartsWith("https://submit.example.org/workflowhub/submissions/", result.FollowMe);
        var stored = await db.Submissions.SingleAsync();
        Assert.Equal("workflowhub", stored.Endpoint);
        Assert.Equal(hub.RoCrate, stored.RoCrateJson);
        Assert.Equal(3, hub.TokensSeen.Count);
    }

    [Fact]
    public async Task Uses_the_gitlab_token_during_the_hook_and_never_stores_it()
    {
        var (service, db, hub) = Build();

        await service.HandleAsync(Hook(), default);

        // All three reads happened while the token was still alive.
        Assert.Equal(3, hub.TokensSeen.Count);
        Assert.All(hub.TokensSeen, t => Assert.Equal("glpat-short-lived", t));

        // And nothing about it survived into the record. Checked across every string column
        // rather than a named few, so a future field cannot quietly start carrying it.
        var stored = await db.Submissions.Include(s => s.Events).SingleAsync();
        var strings = typeof(Submission).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (string?)p.GetValue(stored))
            .Concat(stored.Events.Select(e => e.Message));

        Assert.All(strings, s => Assert.DoesNotContain("glpat-short-lived", s ?? ""));
    }

    [Fact]
    public async Task Records_the_clicker_as_the_audit_trail()
    {
        // Authorization is "the clicker is sufficient", so this is the only attestation
        // that the submission was sanctioned.
        var (service, db, _) = Build();
        await service.HandleAsync(Hook(), default);

        var stored = await db.Submissions.SingleAsync();
        Assert.Equal("kschneider", stored.SubmitterGitLabUser);
        Assert.Equal(7, stored.SubmitterGitLabUserId);
        Assert.Equal("kschneider@example.org", stored.SubmitterEmail);
    }

    [Fact]
    public async Task Stores_the_package_payload_verbatim()
    {
        var (service, db, _) = Build();
        await service.HandleAsync(Hook(new { target = "sandbox", checklist = "ERC000011", contract_version = 1 }), default);

        var stored = await db.Submissions.SingleAsync();
        Assert.Contains("ERC000011", stored.PackagePayloadJson);
    }

    [Fact]
    public async Task Rejects_a_stale_hook()
    {
        var (service, _, _) = Build();
        var stale = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();

        var result = await service.HandleAsync(Hook(iat: stale), default);

        Assert.IsType<IntakeResult.Unauthorized>(result);
    }

    private static SubmissionIntakeService BuildIntake()
    {
        var db = new SubmissionDbContext(new DbContextOptionsBuilder<SubmissionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var options = Options.Create(new EndpointSubmissionOptions
        {
            AllowedDataHubHosts = ["gitdev.nfdi4plants.org"],
            PublicBaseUrl = "https://submit.example.org",
            HookMaxAgeSeconds = 300,
            HookSharedSecret = "secret"
        });
        return new SubmissionIntakeService(
            db, new FakeDataHub(), new DataHubUrlGuard(options), new StatusTokenService(), options,
            NullLogger<SubmissionIntakeService>.Instance);
    }

    [Fact]
    public async Task Measures_freshness_from_when_the_hook_was_received_when_the_caller_says_so()
    {
        // The hook arrived ten minutes ago and has waited since, for example behind a project lock.
        // Its iat is 30 seconds older than its arrival, which is fresh, although it is more than
        // HookMaxAgeSeconds older than now.
        var intake = BuildIntake();
        var receivedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var request = Hook(iat: receivedAt.AddSeconds(-30).ToUnixTimeSeconds());

        var result = await intake.IntakeAsync(
            request, "ena", SubmissionTarget.Sandbox, releaseDate: null, default, receivedAt);

        Assert.IsType<IntakeResult.Accepted>(result);
    }

    [Fact]
    public async Task Rejects_an_iat_that_was_already_stale_when_the_hook_was_received()
    {
        var intake = BuildIntake();
        var receivedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var request = Hook(iat: receivedAt.AddSeconds(-400).ToUnixTimeSeconds());

        var result = await intake.IntakeAsync(
            request, "ena", SubmissionTarget.Sandbox, releaseDate: null, default, receivedAt);

        Assert.IsType<IntakeResult.Unauthorized>(result);
    }

    [Fact]
    public async Task Rejects_a_ro_crate_url_off_the_allowlist()
    {
        var (service, _, hub) = Build();

        var result = await service.HandleAsync(Hook(roCrateUrl: "https://attacker.example/x.json"), default);

        Assert.IsType<IntakeResult.Rejected>(result);
        // The guard must run before anything is fetched, or the SSRF has already happened.
        Assert.Empty(hub.TokensSeen);
    }

    [Fact]
    public async Task Refuses_production_when_the_deployment_forbids_it()
    {
        var (service, _, _) = Build(allowProduction: false);

        var result = Assert.IsType<IntakeResult.Rejected>(
            await service.HandleAsync(Hook(new { target = "production", contract_version = 1 }), default));

        Assert.Contains("not configured to submit to production", result.Reason);
    }

    [Fact]
    public async Task Allows_production_only_when_both_opt_ins_agree()
    {
        var (service, db, _) = Build(allowProduction: true);

        Assert.IsType<IntakeResult.Accepted>(
            await service.HandleAsync(Hook(new { target = "production", contract_version = 1 }), default));

        Assert.Equal(SubmissionTarget.Production, (await db.Submissions.SingleAsync()).Target);
    }

    [Fact]
    public async Task Defaults_to_sandbox_when_the_payload_says_nothing()
    {
        var (service, db, _) = Build(allowProduction: true);

        await service.HandleAsync(Hook(new { }), default);

        Assert.Equal(SubmissionTarget.Sandbox, (await db.Submissions.SingleAsync()).Target);
    }

    [Fact]
    public async Task Rejects_an_unknown_target_rather_than_defaulting()
    {
        var (service, _, _) = Build();

        var result = Assert.IsType<IntakeResult.Rejected>(
            await service.HandleAsync(Hook(new { target = "prod" }), default));

        Assert.Contains("Unknown target", result.Reason);
    }

    [Fact]
    public async Task Rejects_an_unsupported_contract_version()
    {
        var (service, _, _) = Build();

        var result = Assert.IsType<IntakeResult.Rejected>(
            await service.HandleAsync(Hook(new { contract_version = 99 }), default));

        Assert.Contains("contract_version", result.Reason);
    }

    [Fact]
    public async Task Re_clicking_the_badge_reuses_the_submission_and_reissues_the_link()
    {
        var (service, db, _) = Build();

        var first = Assert.IsType<IntakeResult.Accepted>(await service.HandleAsync(Hook(), default));
        var second = Assert.IsType<IntakeResult.Accepted>(await service.HandleAsync(Hook(), default));

        Assert.Equal(1, await db.Submissions.CountAsync());
        Assert.Equal(first.Submission.Id, second.Submission.Id);

        // A fresh token each time — this is also the recovery path for a lost status link.
        Assert.NotEqual(first.FollowMe, second.FollowMe);
    }

    [Fact]
    public async Task Reports_upstream_failures_distinctly_from_bad_requests()
    {
        var (service, _, hub) = Build();
        hub.Commit = null;

        Assert.IsType<IntakeResult.Upstream>(await service.HandleAsync(Hook(), default));
    }

    [Fact]
    public async Task Reports_a_rejected_token_as_unauthorized()
    {
        var (service, _, hub) = Build();
        hub.Fail = new DataHubClient.UnauthorizedError(401, "401 Unauthorized");

        Assert.IsType<IntakeResult.Unauthorized>(await service.HandleAsync(Hook(), default));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Reports_other_datahub_errors_as_upstream(int status)
    {
        // A healthy credential against an unwell DataHUB is not the caller's fault, so it must
        // not come back as 401 and invite them to re-authenticate.
        var (service, _, hub) = Build();
        hub.Fail = new DataHubClient.DataHubError(status, $"HTTP {status}", "");

        var result = Assert.IsType<IntakeResult.Upstream>(await service.HandleAsync(Hook(), default));
        Assert.Contains(status.ToString(), result.Reason);
    }

    [Fact]
    public async Task Does_not_leak_datahub_response_bodies_to_the_caller()
    {
        // Error bodies can carry private ARC content; they belong in the log, not the response.
        var (service, _, hub) = Build();
        hub.Fail = new DataHubClient.DataHubError(500, "HTTP 500", "secret ARC detail");

        var result = Assert.IsType<IntakeResult.Upstream>(await service.HandleAsync(Hook(), default));
        Assert.DoesNotContain("secret ARC detail", result.Reason);
    }

    [Fact]
    public async Task Fetches_the_ro_crate_by_package_components_not_by_the_supplied_url()
    {
        // The library rebuilds the request URL from these parts, so what gets fetched is
        // something we constructed rather than the caller's string.
        var (service, _, hub) = Build();

        await service.HandleAsync(Hook(), default);

        var location = Assert.Single(hub.LocationsRequested);
        Assert.Equal(2, location.ProjectId);
        Assert.Equal("isa_arc_json", location.PackageName);
        Assert.Equal("0.0.1", location.Version);
        Assert.Equal("arc-ro-crate-metadata.json", location.FileName);
        Assert.Equal("gitdev.nfdi4plants.org", location.Instance.Host);
    }

    [Fact]
    public async Task Rejects_a_ro_crate_url_whose_project_disagrees_with_the_hook()
    {
        // The two halves of the payload disagreeing about which ARC is being submitted is
        // never legitimate.
        var (service, _, hub) = Build();
        var mismatched = Hook(roCrateUrl:
            "https://gitdev.nfdi4plants.org/api/v4/projects/99/packages/generic/isa_arc_json/0.0.1/arc-ro-crate-metadata.json");

        var result = Assert.IsType<IntakeResult.Rejected>(await service.HandleAsync(mismatched, default));

        Assert.Contains("project 99", result.Reason);
        Assert.Empty(hub.TokensSeen);
    }

    private const string Pat = "glpat-short-lived";

    private static WorkflowPreparation MixedPreparation() => new(
        [
            new("workflow", "workflows/align", "Align reads", "Aligns reads.",
                PreparedWorkflowItemStatus.Uploadable, [], Encoding.UTF8.GetBytes("PK crate bytes"),
                ["data/reads.fastq", "data/table.csv"]),
            new("run", "runs/r1", "Run one", null,
                PreparedWorkflowItemStatus.NotReady, ["The run has no outputs."], null, []),
            new("run", "runs/r2", null, null,
                PreparedWorkflowItemStatus.PackFailed, ["Zip assembly failed."], null, [])
        ],
        LicenseSpdxId: "MIT", LicensePresent: true, Error: null);

    private static Task<IntakeResult> WorkflowHubHook(WorkflowHubSubmissionHandler service)
        => service.HandleAsync(Hook(new { target = "sandbox", contract_version = 1 }), default);

    /// <summary>Every string column of the submission, its events and its prepared artifacts.</summary>
    private static IEnumerable<string?> StoredStrings(Submission stored)
        => typeof(Submission).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (string?)p.GetValue(stored))
            .Concat(stored.Events.Select(e => e.Message))
            .Concat(stored.PreparedArtifacts.SelectMany(a => typeof(PreparedArtifact).GetProperties()
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => (string?)p.GetValue(a))));

    [Fact]
    public async Task WorkflowHub_hook_prepares_one_artifact_per_item_with_the_hook_token()
    {
        var (service, db, _, packer) = BuildWorkflowHub();
        packer.Result = MixedPreparation();

        Assert.IsType<IntakeResult.Accepted>(await WorkflowHubHook(service));

        var artifacts = await db.PreparedArtifacts.ToListAsync();
        Assert.Equal(3, artifacts.Count);

        var ready = Assert.Single(artifacts, a => a.ItemIdentifier == "workflows/align");
        var crate = Encoding.UTF8.GetBytes("PK crate bytes");
        Assert.Equal("workflow", ready.ItemKind);
        Assert.Equal(PreparedArtifactStatus.Ready, ready.Status);
        Assert.Equal(crate, ready.Content);
        Assert.Equal(crate.Length, ready.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(crate)).ToLowerInvariant(), ready.ContentSha256);
        Assert.Equal(
            """{"licenseSpdxId":"MIT","licensePresent":true,"lfsPointerFiles":["data/reads.fastq","data/table.csv"]}""",
            ready.MetadataJson);

        var notReady = Assert.Single(artifacts, a => a.ItemIdentifier == "runs/r1");
        Assert.Equal("""{"licenseSpdxId":"MIT","licensePresent":true,"lfsPointerFiles":[]}""", notReady.MetadataJson);
        Assert.Equal("run", notReady.ItemKind);
        Assert.Equal(PreparedArtifactStatus.NotReady, notReady.Status);
        Assert.Equal("""["The run has no outputs."]""", notReady.FailureReasonsJson);

        var failed = Assert.Single(artifacts, a => a.ItemIdentifier == "runs/r2");
        Assert.Equal(PreparedArtifactStatus.PreparationFailed, failed.Status);

        Assert.All(new[] { notReady, failed }, a =>
        {
            Assert.Null(a.Content);
            Assert.Null(a.SizeBytes);
            Assert.Null(a.ContentSha256);
        });

        var submission = await db.Submissions.Include(s => s.Events).SingleAsync();
        Assert.Equal(SubmissionStatus.PreviewReady, submission.Status);
        var completed = Assert.Single(submission.Events, e => e.Step == "preparation.completed");
        Assert.Contains("1 ready, 1 not ready, 1 failed", completed.Message);

        Assert.Equal(Pat, Assert.Single(packer.TokensSeen));
        Assert.Equal(new Uri("https://gitdev.nfdi4plants.org"), Assert.Single(packer.InstancesSeen));
    }

    [Fact]
    public async Task WorkflowHub_hook_never_stores_the_token_in_the_prepared_artifacts()
    {
        var (service, db, _, packer) = BuildWorkflowHub();
        packer.Result = MixedPreparation();

        await WorkflowHubHook(service);

        // Every string column of every stored entity, plus the crate bytes.
        var stored = await db.Submissions.Include(s => s.Events).Include(s => s.PreparedArtifacts).SingleAsync();
        Assert.NotEmpty(stored.PreparedArtifacts);
        Assert.All(StoredStrings(stored), s => Assert.DoesNotContain(Pat, s ?? ""));

        var needle = Encoding.UTF8.GetBytes(Pat);
        Assert.All(stored.PreparedArtifacts.Where(a => a.Content is not null),
            a => Assert.Equal(-1, a.Content.AsSpan().IndexOf(needle)));
    }

    [Fact]
    public async Task WorkflowHub_re_click_after_a_successful_preparation_does_not_prepare_again()
    {
        var (service, db, _, packer) = BuildWorkflowHub();
        packer.Result = MixedPreparation();

        await WorkflowHubHook(service);
        Assert.IsType<IntakeResult.Accepted>(await WorkflowHubHook(service));

        Assert.Single(packer.TokensSeen);
        Assert.Equal(3, await db.PreparedArtifacts.CountAsync());
    }

    [Fact]
    public async Task WorkflowHub_re_click_after_a_failed_preparation_prepares_again()
    {
        var (service, db, _, packer) = BuildWorkflowHub();
        packer.Fail = new InvalidOperationException("boom");

        await WorkflowHubHook(service);
        Assert.Empty(db.PreparedArtifacts);

        packer.Fail = null;
        packer.Result = MixedPreparation();
        await WorkflowHubHook(service);

        Assert.Equal(2, packer.TokensSeen.Count);
        Assert.Equal(3, await db.PreparedArtifacts.CountAsync());
        Assert.Equal(SubmissionStatus.PreviewReady, (await db.Submissions.SingleAsync()).Status);
    }

    [Fact]
    public async Task WorkflowHub_packer_error_is_recorded_without_artifacts_and_the_hook_still_answers()
    {
        var (service, db, _, packer) = BuildWorkflowHub();
        packer.Result = new WorkflowPreparation([], null, false, "The ARC could not be loaded.");

        Assert.IsType<IntakeResult.Accepted>(await WorkflowHubHook(service));

        Assert.Empty(db.PreparedArtifacts);
        var submission = await db.Submissions.Include(s => s.Events).SingleAsync();
        Assert.Equal(SubmissionStatus.Created, submission.Status);
        var failed = Assert.Single(submission.Events, e => e.Step == "preparation.failed");
        Assert.Equal(SubmissionEventLevel.Error, failed.Level);
        Assert.Equal("The ARC could not be loaded.", failed.Message);
    }

    [Fact]
    public async Task WorkflowHub_redacts_the_token_from_a_packer_error()
    {
        var (service, db, _, packer) = BuildWorkflowHub();
        packer.Result = new WorkflowPreparation([], null, false, $"Checkout of https://oauth2:{Pat}@git failed.");

        await WorkflowHubHook(service);

        var failed = await db.SubmissionEvents.SingleAsync(e => e.Step == "preparation.failed");
        Assert.Equal("Checkout of https://oauth2:[REDACTED]@git failed.", failed.Message);
    }

    [Fact]
    public async Task WorkflowHub_exception_text_never_reaches_the_store_or_the_log()
    {
        var logger = new ListLogger<WorkflowHubSubmissionHandler>();
        var (service, db, _, packer) = BuildWorkflowHub(logger);
        packer.Fail = new InvalidOperationException($"git clone https://oauth2:{Pat}@gitdev failed");

        Assert.IsType<IntakeResult.Accepted>(await WorkflowHubHook(service));

        Assert.Empty(db.PreparedArtifacts);
        var submission = await db.Submissions.Include(s => s.Events).SingleAsync();
        Assert.Equal(SubmissionStatus.Created, submission.Status);
        var failed = Assert.Single(submission.Events, e => e.Step == "preparation.failed");
        Assert.Equal(SubmissionEventLevel.Error, failed.Level);
        Assert.Equal("Preparing the WorkflowHub crates failed.", failed.Message);

        var strings = typeof(Submission).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (string?)p.GetValue(submission))
            .Concat(submission.Events.Select(e => e.Message))
            .Concat(logger.Messages);
        Assert.All(strings, s => Assert.DoesNotContain(Pat, s ?? ""));
        Assert.Contains(logger.Messages, m => m.Contains(nameof(InvalidOperationException)));
    }

    [Fact]
    public async Task WorkflowHub_redacts_the_token_from_every_string_the_packer_returns()
    {
        var (service, db, _, packer) = BuildWorkflowHub();
        packer.Result = new WorkflowPreparation(
            [
                new($"run-{Pat}", $"runs/{Pat}", $"Title {Pat}", $"Description {Pat}",
                    PreparedWorkflowItemStatus.NotReady, [$"Reason {Pat}"], null, [$"data/{Pat}.fastq"])
            ],
            LicenseSpdxId: $"MIT-{Pat}", LicensePresent: true, Error: null);

        await WorkflowHubHook(service);

        var stored = await db.Submissions.Include(s => s.Events).Include(s => s.PreparedArtifacts).SingleAsync();
        var artifact = Assert.Single(stored.PreparedArtifacts);
        Assert.Equal("Title [REDACTED]", artifact.Title);
        Assert.Equal("""["Reason [REDACTED]"]""", artifact.FailureReasonsJson);
        Assert.Contains("""["data/[REDACTED].fastq"]""", artifact.MetadataJson);
        Assert.All(StoredStrings(stored), s => Assert.DoesNotContain(Pat, s ?? ""));
    }

    [Fact]
    public async Task WorkflowHub_truncates_packer_strings_to_the_column_lengths()
    {
        var (service, db, _, packer) = BuildWorkflowHub();
        packer.Result = new WorkflowPreparation(
            [
                new(new string('k', 100), new string('i', 300), new string('t', 2000), null,
                    PreparedWorkflowItemStatus.NotReady, [], null, [])
            ],
            LicenseSpdxId: null, LicensePresent: true, Error: null);

        await WorkflowHubHook(service);

        var artifact = await db.PreparedArtifacts.SingleAsync();
        Assert.Equal(64, artifact.ItemKind.Length);
        Assert.Equal(256, artifact.ItemIdentifier.Length);
        Assert.Equal(1024, artifact.Title!.Length);
    }

    [Fact]
    public async Task WorkflowHub_zero_items_is_a_completed_preparation_and_a_re_click_does_not_prepare_again()
    {
        var (service, db, _, packer) = BuildWorkflowHub();

        await WorkflowHubHook(service);
        Assert.IsType<IntakeResult.Accepted>(await WorkflowHubHook(service));

        Assert.Single(packer.TokensSeen);
        Assert.Empty(db.PreparedArtifacts);
        var submission = await db.Submissions.Include(s => s.Events).SingleAsync();
        Assert.Equal(SubmissionStatus.PreviewReady, submission.Status);
        var completed = Assert.Single(submission.Events, e => e.Step == "preparation.completed");
        Assert.Equal("Prepared 0 WorkflowHub items: no workflows or runs were found in this commit.", completed.Message);
    }

    [Fact]
    public async Task WorkflowHub_preparation_that_takes_too_long_is_recorded_as_a_failure()
    {
        var (service, db, _, packer) = BuildWorkflowHub(
            workflowHubOptions: new WorkflowHubOptions { PreparationTimeoutSeconds = 1 });
        packer.Gate = ct => Task.Delay(Timeout.Infinite, ct);

        Assert.IsType<IntakeResult.Accepted>(await WorkflowHubHook(service));

        Assert.Empty(db.PreparedArtifacts);
        var failed = await db.SubmissionEvents.SingleAsync(e => e.Step == "preparation.failed");
        Assert.Equal(SubmissionEventLevel.Error, failed.Level);
        Assert.Equal("Preparing the WorkflowHub crates took too long.", failed.Message);
    }

    [Fact]
    public async Task WorkflowHub_caller_cancellation_propagates_and_records_no_preparation_event()
    {
        var (service, db, _, packer) = BuildWorkflowHub();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        packer.Gate = ct =>
        {
            entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        };
        using var cts = new CancellationTokenSource();

        var call = service.HandleAsync(Hook(new { target = "sandbox", contract_version = 1 }), cts.Token);
        await entered.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.DoesNotContain(await db.SubmissionEvents.ToListAsync(), e => e.Step.StartsWith("preparation."));
        Assert.Empty(db.PreparedArtifacts);
    }

    [Fact]
    public async Task WorkflowHub_second_click_waits_for_the_running_preparation_and_returns_the_working_link()
    {
        var (service, db, _, packer) = BuildWorkflowHub();
        packer.Result = MixedPreparation();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        packer.Gate = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var first = WorkflowHubHook(service);
        await entered.Task;
        var second = WorkflowHubHook(service);

        // The second click queues on the project lock, so it neither prepares nor finishes.
        await Task.Delay(200);
        Assert.False(second.IsCompleted);
        Assert.Single(packer.TokensSeen);

        release.SetResult();
        Assert.IsType<IntakeResult.Accepted>(await first);
        var secondResult = Assert.IsType<IntakeResult.Accepted>(await second);

        Assert.Single(packer.TokensSeen);
        Assert.Equal(1, await db.Submissions.CountAsync());
        Assert.Equal(3, await db.PreparedArtifacts.CountAsync());

        var token = secondResult.FollowMe[(secondResult.FollowMe.IndexOf("?t=", StringComparison.Ordinal) + 3)..];
        var access = new SubmissionPageAccess(db, new StatusTokenService());
        Assert.NotNull(await access.FindAsync(secondResult.Submission.Id, token, "workflowhub", default));
    }

    [Fact]
    public async Task KeyedAsyncLock_makes_one_key_wait_without_blocking_another_key()
    {
        var locks = new KeyedAsyncLock();
        using var held = await locks.AcquireAsync("a", default);

        var sameKey = locks.AcquireAsync("a", default);
        using var otherKey = await locks.AcquireAsync("b", default).WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Delay(100);
        Assert.False(sameKey.IsCompleted);

        held.Dispose();
        using var acquired = await sameKey.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
