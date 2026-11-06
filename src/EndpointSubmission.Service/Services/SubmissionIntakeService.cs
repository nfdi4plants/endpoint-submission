using System.Text.Json;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointSubmission.Service.Services;

public abstract record IntakeResult
{
    public sealed record Accepted(Submission Submission, string FollowMe) : IntakeResult;
    public sealed record Rejected(string Reason) : IntakeResult;
    public sealed record Unauthorized(string Reason) : IntakeResult;
    public sealed record Upstream(string Reason) : IntakeResult;
}

/// <summary>
/// Turns a badge click into a persisted submission.
///
/// Everything that needs the caller's GitLab token happens here, synchronously, because the
/// token is short-lived and the user may not open follow_me for some time. After this method
/// returns, the token is gone and the service holds no credential of any kind.
/// </summary>
public class SubmissionIntakeService(
    SubmissionDbContext db,
    IArcMetadataReader dataHub,
    DataHubUrlGuard urlGuard,
    StatusTokenService tokens,
    IOptions<EndpointSubmissionOptions> options,
    ILogger<SubmissionIntakeService> logger)
{
    private readonly EndpointSubmissionOptions _options = options.Value;

    /// <param name="receivedAt">
    /// When the hook request arrived. A caller that waits before intake, such as for a per-project
    /// lock, passes it so the wait does not count against the freshness window. It defaults to now.
    /// </param>
    public async Task<IntakeResult> IntakeAsync(
        HookRequest request,
        string endpoint,
        SubmissionTarget target,
        DateOnly? releaseDate,
        CancellationToken ct,
        DateTimeOffset? receivedAt = null)
    {
        // --- Freshness ---------------------------------------------------------
        var age = (receivedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - request.IssuedAt;
        if (request.IssuedAt <= 0 || age > _options.HookMaxAgeSeconds || age < -60)
            return new IntakeResult.Unauthorized("Hook payload is stale or not yet valid (iat).");

        if (string.IsNullOrWhiteSpace(request.GitLabToken))
            return new IntakeResult.Rejected("gitlab_token is required.");
        if (string.IsNullOrWhiteSpace(request.ProjectName))
            return new IntakeResult.Rejected("project_name is required.");
        if (request.ProjectId <= 0)
            return new IntakeResult.Rejected("project_id is required.");

        // --- SSRF guard: do this before any outbound request -------------------
        if (!urlGuard.TryResolve(request.RoCrateUrl, request.ProjectId, out var roCrate, out var urlError))
            return new IntakeResult.Rejected(urlError!);

        // Two independent opt-ins for the irreversible option. The payload asking for
        // production is not enough; the deployment must also be configured to permit it.
        if (target == SubmissionTarget.Production && !_options.AllowProductionTarget)
            return new IntakeResult.Rejected("This deployment is not configured to submit to production.");

        // --- Token-dependent work, all of it, right now ------------------------
        // The token is short-lived and the user may not open follow_me for some time, so every
        // DataHUB read happens here, inside the hook request, and the token is dropped after.
        var location = roCrate!;
        var token = request.GitLabToken!;

        ArcSubmitter user;
        string? commitSha;
        string roCrateJson;
        try
        {
            user = await dataHub.GetSubmitterAsync(location.Instance, token, ct);
            commitSha = await dataHub.ResolveHeadCommitAsync(location.Instance, request.ProjectId, token, ct);

            roCrateJson = await dataHub.FetchRoCrateAsync(location, token, ct);
        }
        catch (DataHubClient.UnauthorizedError)
        {
            // Distinct from the failures below: the credential is the problem, not the DataHUB.
            return new IntakeResult.Unauthorized("The supplied gitlab_token was rejected by the DataHUB.");
        }
        catch (DataHubClient.DataHubError ex)
        {
            // Body may carry private ARC detail, so it is logged rather than returned.
            logger.LogWarning("DataHUB read failed for project {ProjectId} with status {Status}",
                request.ProjectId, ex.StatusCode);
            return new IntakeResult.Upstream($"The DataHUB could not be read (HTTP {ex.StatusCode}).");
        }

        if (string.IsNullOrWhiteSpace(commitSha))
            return new IntakeResult.Upstream("Could not resolve the ARC's current commit from the DataHUB.");

        // --- Persist -----------------------------------------------------------
        var existing = await db.Submissions.FirstOrDefaultAsync(
            s => s.DataHubHost == location.Instance.Host
              && s.ProjectId == request.ProjectId
              && s.CommitSha == commitSha
              && s.Endpoint == endpoint
              && s.Target == target,
            ct);

        var minted = tokens.Mint();

        if (existing is not null)
        {
            // Re-click of the same badge. Rotate the status token rather than creating a
            // second submission: the clicker has just proven DataHUB access, and this
            // doubles as the recovery path for a lost status link.
            existing.StatusTokenHash = minted.Hash;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            db.SubmissionEvents.Add(new SubmissionEvent
            {
                SubmissionId = existing.Id,
                Step = "hook.received",
                Message = $"Badge re-clicked by {user.Username}; status link reissued."
            });
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Re-click for submission {SubmissionId}", existing.Id);
            return new IntakeResult.Accepted(existing, BuildFollowMe(existing.Id, endpoint, minted.Token));
        }

        var submission = new Submission
        {
            ProjectId = request.ProjectId,
            ProjectName = request.ProjectName!,
            DataHubHost = location.Instance.Host,
            CommitSha = commitSha!,
            RoCrateUrl = request.RoCrateUrl!,
            RoCrateJson = roCrateJson,
            PackagePayloadJson = request.PackagePayload.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : request.PackagePayload.GetRawText(),
            Endpoint = endpoint,
            Target = target,
            SubmitterGitLabUser = user.Username,
            SubmitterGitLabUserId = user.UserId,
            SubmitterEmail = user.Email ?? string.Empty,
            StatusTokenHash = minted.Hash,
            ReleaseDate = releaseDate,
            Status = SubmissionStatus.Created
        };

        submission.Events.Add(new SubmissionEvent
        {
            Step = "hook.received",
            Message = $"Submission opened by {user.Username} for {request.ProjectName} at {commitSha![..Math.Min(8, commitSha.Length)]} " +
                      $"(target: {target.ToString().ToLowerInvariant()})."
        });

        db.Submissions.Add(submission);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Possibly lost a race against a concurrent click, in which case the unique
            // index did its job and the other request's submission is the one to return.
            db.ChangeTracker.Clear();

            var winner = await db.Submissions.FirstOrDefaultAsync(
                s => s.DataHubHost == location.Instance.Host
                  && s.ProjectId == request.ProjectId && s.CommitSha == commitSha
                  && s.Endpoint == endpoint && s.Target == target, ct);

            if (winner is null) throw; // Not the race; a real failure.

            winner.StatusTokenHash = minted.Hash;
            winner.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return new IntakeResult.Accepted(winner, BuildFollowMe(winner.Id, endpoint, minted.Token));
        }

        logger.LogInformation("Opened submission {SubmissionId} for project {ProjectId}", submission.Id, submission.ProjectId);
        return new IntakeResult.Accepted(submission, BuildFollowMe(submission.Id, endpoint, minted.Token));
    }

    private string BuildFollowMe(Guid id, string endpoint, string token)
    {
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        return $"{baseUrl}/{endpoint}/submissions/{id}?t={token}";
    }
}
