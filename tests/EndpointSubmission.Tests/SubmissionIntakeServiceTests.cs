using System.Text.Json;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Endpoints.ENA;
using EndpointSubmission.Service.Endpoints.WorkflowHub;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Services;
using Microsoft.EntityFrameworkCore;
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

    private static (WorkflowHubSubmissionHandler Service, SubmissionDbContext Db, FakeDataHub Hub) BuildWorkflowHub()
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

        return (new WorkflowHubSubmissionHandler(intake), db, hub);
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
        var (service, db, hub) = BuildWorkflowHub();

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
}
