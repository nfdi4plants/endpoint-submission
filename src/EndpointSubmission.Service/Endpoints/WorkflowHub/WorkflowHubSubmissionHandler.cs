using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>
/// Owns WorkflowHub's hook payload and preparation path. It receives the original request so
/// endpoint-specific preparation can use the PAT during this POST without ever persisting it.
/// </summary>
public class WorkflowHubSubmissionHandler(
    SubmissionIntakeService intake,
    SubmissionDbContext db,
    IWorkflowCratePacker packer,
    KeyedAsyncLock locks,
    IOptions<WorkflowHubOptions> options,
    ILogger<WorkflowHubSubmissionHandler> logger)
{
    private const string Redacted = "[REDACTED]";
    private const string PreparationFailedText = "Preparing the WorkflowHub crates failed.";
    private const string PreparationTimedOutText = "Preparing the WorkflowHub crates took too long.";
    private const string SaveFailedText = "Saving the prepared WorkflowHub crates failed.";
    private const string NothingFoundText =
        "Prepared 0 WorkflowHub items: no workflows or runs were found in this commit.";

    private readonly WorkflowHubOptions _options = options.Value;

    public async Task<IntakeResult> HandleAsync(HookRequest request, CancellationToken ct)
    {
        // Taken before the lock below. A second click can wait there through a long preparation,
        // and the intake freshness check must measure from when the click arrived.
        var receivedAt = DateTimeOffset.UtcNow;

        WorkflowHubPackagePayload payload;
        try
        {
            payload = request.PackagePayload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? new WorkflowHubPackagePayload()
                : request.PackagePayload.Deserialize<WorkflowHubPackagePayload>() ?? new WorkflowHubPackagePayload();
        }
        catch (JsonException ex)
        {
            return new IntakeResult.Rejected($"package_payload is not valid for endpoint 'workflowhub': {ex.Message}");
        }

        if (payload.ContractVersion != 1)
            return new IntakeResult.Rejected($"Unsupported package_payload contract_version {payload.ContractVersion}.");

        var target = ParseTarget(payload.Target);
        if (target is null)
            return new IntakeResult.Rejected($"Unknown target '{payload.Target}'. Expected 'sandbox' or 'production'.");

        // Preparation holds this request open for minutes. A second click for the same project
        // waits here, so its intake issues the link the user ends up with and it finds the
        // prepared rows instead of running a second checkout.
        using var projectLock = await locks.AcquireAsync(LockKey(request), ct);

        var result = await intake.IntakeAsync(
            request, "workflowhub", target.Value, releaseDate: null, ct, receivedAt);
        if (result is IntakeResult.Accepted accepted)
            await PrepareAsync(accepted.Submission, request.GitLabToken!, ct);

        // A failed preparation still answers with follow_me. The preview page explains it.
        return result;
    }

    private static string LockKey(HookRequest request)
    {
        var host = Uri.TryCreate(request.RoCrateUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : request.RoCrateUrl ?? "";
        return host.ToLowerInvariant() + "|" + request.ProjectId;
    }

    private async Task PrepareAsync(Submission submission, string token, CancellationToken ct)
    {
        // A re-click after a failed preparation prepares again. After a successful one the rows
        // exist (or the completed event does, when the commit has no items), and after an upload
        // attempt the prepared content is what was sent.
        if (await db.PreparedArtifacts.AnyAsync(a => a.SubmissionId == submission.Id, ct)
            || await db.SubmissionEvents.AnyAsync(
                e => e.SubmissionId == submission.Id && e.Step == "preparation.completed", ct)
            || await db.SubmissionAttempts.AnyAsync(a => a.SubmissionId == submission.Id, ct))
            return;

        List<PreparedArtifact>? artifacts = null;
        string? failure = null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.PreparationTimeoutSeconds));

        try
        {
            var preparation = await packer.PrepareAsync(
                new Uri("https://" + submission.DataHubHost),
                submission.ProjectId,
                submission.CommitSha,
                token,
                timeout.Token);

            if (preparation.Error is not null)
            {
                failure = Redact(preparation.Error, token);
                logger.LogWarning("Preparation for submission {SubmissionId} reported: {Reason}", submission.Id, failure);
            }
            else
            {
                artifacts = ToArtifacts(submission.Id, preparation, token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // The caller went away, so there is nobody to show the page to.
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("Preparation timed out for submission {SubmissionId}", submission.Id);
            failure = PreparationTimedOutText;
        }
        catch (Exception ex)
        {
            // The type name is enough to debug with. The message may quote request details.
            logger.LogWarning("Preparation failed for submission {SubmissionId} with {ExceptionType}",
                submission.Id, ex.GetType().Name);
            failure = PreparationFailedText;
        }

        if (artifacts is null)
        {
            db.SubmissionEvents.Add(new SubmissionEvent
            {
                SubmissionId = submission.Id,
                Step = "preparation.failed",
                Level = SubmissionEventLevel.Error,
                Message = failure!
            });
        }
        else
        {
            db.PreparedArtifacts.AddRange(artifacts);
            submission.Status = SubmissionStatus.PreviewReady;
            submission.UpdatedAt = DateTimeOffset.UtcNow;

            var ready = artifacts.Count(a => a.Status == PreparedArtifactStatus.Ready);
            var notReady = artifacts.Count(a => a.Status == PreparedArtifactStatus.NotReady);
            var failed = artifacts.Count(a => a.Status == PreparedArtifactStatus.PreparationFailed);
            db.SubmissionEvents.Add(new SubmissionEvent
            {
                SubmissionId = submission.Id,
                Step = "preparation.completed",
                Message = artifacts.Count == 0
                    ? NothingFoundText
                    : $"Prepared {artifacts.Count} WorkflowHub items: " +
                      $"{ready} ready, {notReady} not ready, {failed} failed to pack."
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Normally the project lock prevents this. If another request saved the rows first,
            // its result stands. The exception text may quote row values, so only its type is logged.
            var submissionId = submission.Id;
            logger.LogWarning("Saving the prepared crates for submission {SubmissionId} failed with {ExceptionType}",
                submissionId, ex.GetType().Name);
            db.ChangeTracker.Clear();

            if (await db.PreparedArtifacts.AnyAsync(a => a.SubmissionId == submissionId, ct))
                return;

            db.SubmissionEvents.Add(new SubmissionEvent
            {
                SubmissionId = submissionId,
                Step = "preparation.failed",
                Level = SubmissionEventLevel.Error,
                Message = SaveFailedText
            });
            await db.SaveChangesAsync(ct);
        }
    }

    private static List<PreparedArtifact> ToArtifacts(Guid submissionId, WorkflowPreparation preparation, string token)
    {
        return preparation.Items.Select(item =>
        {
            var metadata = JsonSerializer.Serialize(new
            {
                licenseSpdxId = Redact(preparation.LicenseSpdxId, token),
                licensePresent = preparation.LicensePresent,
                lfsPointerFiles = item.LfsPointerFiles.Select(f => Redact(f, token)).ToList()
            });

            var artifact = new PreparedArtifact
            {
                SubmissionId = submissionId,
                // Redact before truncating, so a cut can never leave part of the token behind.
                ItemKind = Truncate(Redact(item.Kind, token), 64),
                ItemIdentifier = Truncate(Redact(item.Identifier, token), 256),
                Title = Truncate(Redact(item.Title, token), 1024),
                Description = Redact(item.Description, token),
                FailureReasonsJson = JsonSerializer.Serialize(item.Failures.Select(f => Redact(f, token)).ToList()),
                MetadataJson = metadata,
                Status = item.Status switch
                {
                    PreparedWorkflowItemStatus.Uploadable => PreparedArtifactStatus.Ready,
                    PreparedWorkflowItemStatus.NotReady => PreparedArtifactStatus.NotReady,
                    _ => PreparedArtifactStatus.PreparationFailed
                }
            };

            if (artifact.Status == PreparedArtifactStatus.Ready)
            {
                var crate = item.Crate ?? throw new InvalidOperationException("An uploadable item has no crate.");
                artifact.Content = crate;
                artifact.SizeBytes = crate.LongLength;
                artifact.ContentSha256 = Convert.ToHexString(SHA256.HashData(crate)).ToLowerInvariant();
            }

            return artifact;
        }).ToList();
    }

    /// <summary>Defense in depth. Packers return credential-free text, and this strips the token if one slips through.</summary>
    [return: NotNullIfNotNull(nameof(message))]
    private static string? Redact(string? message, string token)
        => string.IsNullOrEmpty(token) || message is null
            ? message
            : message.Replace(token, Redacted, StringComparison.Ordinal);

    /// <summary>Cuts to the column's maximum length. PostgreSQL rejects longer values.</summary>
    [return: NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength) return value;
        return char.IsHighSurrogate(value[maxLength - 1]) ? value[..(maxLength - 1)] : value[..maxLength];
    }

    private static SubmissionTarget? ParseTarget(string? target) => target?.Trim().ToLowerInvariant() switch
    {
        "production" => SubmissionTarget.Production,
        "sandbox" or null or "" => SubmissionTarget.Sandbox,
        _ => null
    };
}

public class WorkflowHubPackagePayload
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = "sandbox";

    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; set; } = 1;
}
