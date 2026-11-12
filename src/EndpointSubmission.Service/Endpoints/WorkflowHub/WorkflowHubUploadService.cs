using System.Globalization;
using System.Text.Json;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <param name="Problem">
/// Set when the request was refused before anything was sent. The text is safe to show the user.
/// </param>
/// <param name="Skipped">Items that already had an accession by the time their turn came.</param>
/// <param name="Unknown">Items whose upload got no usable answer. The entry may exist on WorkflowHub.</param>
public sealed record WorkflowHubUploadResult(
    string? Problem, int Succeeded, int PartiallyFailed, int Failed, int Skipped, int Unknown)
{
    public static WorkflowHubUploadResult Refused(string problem) => new(problem, 0, 0, 0, 0, 0);
}

/// <summary>
/// Uploads prepared WorkflowHub crates with the OAuth access token of one request and records what happened:
/// an attempt, the payload descriptions, the receipts, the accession and the events per item, and
/// the submission status per run.
///
/// The access token is a method argument. It is never kept in a field, stored, logged or put in an event.
/// </summary>
public class WorkflowHubUploadService(
    SubmissionDbContext db,
    IWorkflowHubGateway gateway,
    IOptions<WorkflowHubOptions> options,
    ILogger<WorkflowHubUploadService> logger)
{
    private const string Redacted = "[REDACTED]";
    private const int MaxDetailLength = 500;

    /// <summary>
    /// The start of the error summary of an attempt with an unknown outcome. The preview page finds
    /// these attempts by this text, because no status value tells them apart from a clean failure.
    /// </summary>
    internal const string UnknownOutcomeLead = "WorkflowHub did not confirm the upload of ";

    private enum ItemOutcome
    {
        Succeeded,
        PartiallyFailed,
        Failed,
        Unknown
    }

    /// <summary>
    /// The teams the token's owner belongs to. An empty answer records the warning event
    /// "upload.no_team" on the submission, because nothing can be uploaded then.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowHubTeam>> ListTeamsAsync(
        Guid submissionId, string token, CancellationToken ct)
    {
        var target = await db.Submissions.AsNoTracking()
            .Where(s => s.Id == submissionId && s.Endpoint == "workflowhub")
            .Select(s => s.Target)
            .FirstAsync(ct);

        var teams = await gateway.ListTeamsAsync(
            WorkflowHubPreviewHandlers.InstanceBase(target, options.Value), token, ct);

        if (teams.Count == 0)
        {
            db.SubmissionEvents.Add(new SubmissionEvent
            {
                SubmissionId = submissionId,
                Step = "upload.no_team",
                Level = SubmissionEventLevel.Warning,
                Message = "Nothing was uploaded because the WorkflowHub account is not a member of any team."
            });
            await db.SaveChangesAsync(ct);
        }

        return teams;
    }

    /// <summary>
    /// Whether at least one selected item will be uploaded as a new entry, and so needs a team.
    /// A new version uses the entry's own teams. The answer comes from the same lookup the upload
    /// uses. An item that cannot be found counts as a new entry, because the upload refuses it later.
    /// </summary>
    public async Task<bool> NeedsTeamAsync(
        Guid submissionId, IReadOnlyList<WorkflowHubSelectedItem> items, CancellationToken ct)
    {
        var submission = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(
            s => s.Id == submissionId && s.Endpoint == "workflowhub", ct);
        if (submission is null) return true;

        var ids = items.Select(i => i.ArtifactId).ToList();
        var rows = await db.PreparedArtifacts.AsNoTracking()
            .Where(a => a.SubmissionId == submissionId && ids.Contains(a.Id))
            .Select(a => new { a.Id, a.ItemKind, a.ItemIdentifier })
            .ToListAsync(ct);
        var earlierEntries = await WorkflowHubEarlierEntries.FindAsync(db, submission, ct);

        return items.Any(item =>
            rows.FirstOrDefault(r => r.Id == item.ArtifactId) is not { } row
            || EntryToUpdate(item, $"{row.ItemKind}/{row.ItemIdentifier}", earlierEntries) is null);
    }

    /// <summary>
    /// The earlier entry an item goes to as a new version, or null when it goes up as a new entry.
    /// That is the case when the user chose a new entry and when no earlier entry exists.
    /// </summary>
    private static WorkflowHubEarlierEntry? EntryToUpdate(
        WorkflowHubSelectedItem item, string alias, IReadOnlyDictionary<string, WorkflowHubEarlierEntry> earlierEntries)
        => !item.AsNewEntry && earlierEntries.TryGetValue(alias, out var entry) ? entry : null;

    /// <summary>
    /// Uploads the selected items. Every item must be a ready artifact of this submission with content
    /// and no accession yet, or the whole request is refused and nothing is sent.
    ///
    /// An item with an earlier entry, found by <see cref="WorkflowHubEarlierEntries"/> at this moment, goes up
    /// as a new version of that entry unless the user chose a new entry. The entry id never comes from
    /// the caller. Every other item becomes a new entry in <paramref name="teamId"/> with its chosen
    /// visibility. The team can be null when every item is a new version. A new entry without a team fails
    /// that item before anything is sent.
    ///
    /// <paramref name="ct"/> covers the checks. The upload loop that follows runs to its end
    /// whether or not the caller is still there. The user asked for the upload and an uploaded entry
    /// cannot be taken back, so the rows that record it must not be lost to a closed tab. The client
    /// limits each call, 15 minutes for a crate POST and 100 seconds for the rest. A POST without a
    /// usable answer is recorded as an unknown outcome, and a failure inside one item never stops
    /// the next item.
    /// </summary>
    public async Task<WorkflowHubUploadResult> UploadAsync(
        Guid submissionId,
        IReadOnlyList<WorkflowHubSelectedItem> items,
        int? teamId,
        string token,
        CancellationToken ct)
    {
        var submission = await db.Submissions.FirstOrDefaultAsync(
            s => s.Id == submissionId && s.Endpoint == "workflowhub", ct);
        if (submission is null) return WorkflowHubUploadResult.Refused("The submission was not found.");

        var problem = await ValidateSelection(submissionId, items, ct);
        if (problem is not null) return WorkflowHubUploadResult.Refused(problem);

        var earlierEntries = await WorkflowHubEarlierEntries.FindAsync(db, submission, ct);

        var baseUrl = WorkflowHubPreviewHandlers.InstanceBase(submission.Target, options.Value);
        var work = CancellationToken.None;
        var outcomes = new List<ItemOutcome>();
        var skipped = 0;

        foreach (var item in items)
        {
            var (artifactId, makePublic) = (item.ArtifactId, item.MakePublic);
            var artifact = await db.PreparedArtifacts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == artifactId && a.SubmissionId == submissionId, work);
            if (artifact?.Content is not { } crate)
            {
                // ValidateSelection just ran, so this means the row changed in between.
                outcomes.Add(ItemOutcome.Failed);
                await AddEvent(submissionId, "upload.failed", SubmissionEventLevel.Error,
                    "A selected item is no longer available, so it was not uploaded.", work);
                continue;
            }

            var alias = $"{artifact.ItemKind}/{artifact.ItemIdentifier}";
            var label = $"{artifact.ItemKind} {artifact.ItemIdentifier}";

            if (await db.Accessions.AnyAsync(a => a.SubmissionId == submissionId && a.Alias == alias, work))
            {
                skipped++;
                await AddEvent(submissionId, "upload.skipped", SubmissionEventLevel.Info,
                    $"Skipped {label} because it is already on WorkflowHub.", work);
                continue;
            }

            var entryToUpdate = EntryToUpdate(item, alias, earlierEntries);
            if (entryToUpdate is null && teamId is null)
            {
                // The callback only skips the team when every item is a new version, so this means the
                // earlier entries changed in between.
                outcomes.Add(ItemOutcome.Failed);
                await AddEvent(submissionId, "upload.failed", SubmissionEventLevel.Error,
                    $"{label} needs a new WorkflowHub entry, which needs a team, and no team was chosen. It was not uploaded.", work);
                continue;
            }

            var attemptNumber =
                (await db.SubmissionAttempts.Where(a => a.SubmissionId == submissionId)
                    .MaxAsync(a => (int?)a.AttemptNumber, work) ?? 0) + 1;

            if (entryToUpdate is not null)
            {
                outcomes.Add(await UploadVersionAsync(
                    submissionId, attemptNumber, artifact, crate, label, alias, baseUrl, entryToUpdate, token, work));
                continue;
            }

            // A new entry always has a team here.
            var team = teamId.GetValueOrDefault();

            // The attempt and the description of the request are stored before the call, so a crash in
            // the middle of an upload leaves an attempt that still says Submitting. The zip bytes stay
            // in the PreparedArtifacts row. The path and field names come from the same constants
            // that Client.postCrate sends with.
            var attempt = new SubmissionAttempt { SubmissionId = submissionId, AttemptNumber = attemptNumber };
            db.SubmissionAttempts.Add(attempt);
            db.SubmissionPayloads.Add(new SubmissionPayload
            {
                AttemptId = attempt.Id,
                ObjectKind = "workflowhub-crate",
                Content = JsonSerializer.Serialize(new
                {
                    method = "POST",
                    url = baseUrl + WfHubClient.WorkflowHubApi.WorkflowsPath,
                    artifactId = artifact.Id,
                    itemKind = artifact.ItemKind,
                    itemIdentifier = artifact.ItemIdentifier,
                    sha256 = artifact.ContentSha256,
                    sizeBytes = artifact.SizeBytes,
                    requestedVisibility = makePublic ? "public" : "private",
                    fields = new object[]
                    {
                        new
                        {
                            name = WfHubClient.WorkflowHubApi.CrateFieldName,
                            fileName = WfHubClient.WorkflowHubApi.CrateFileName,
                            contentType = WfHubClient.WorkflowHubApi.CrateContentType
                        },
                        new
                        {
                            name = WfHubClient.WorkflowHubApi.TeamFieldName,
                            value = team.ToString(CultureInfo.InvariantCulture)
                        }
                    }
                })
            });
            await db.SaveChangesAsync(work);

            ItemOutcome outcome;
            try
            {
                var upload = await gateway.UploadAsync(baseUrl, token, crate, team, makePublic, work);

                db.SubmissionPayloads.Add(new SubmissionPayload
                {
                    AttemptId = attempt.Id,
                    ObjectKind = "workflowhub-policy",
                    Content = Redact(upload.PolicyRequest, token)
                });
                AddReceipt(attempt, true, upload.RawResponse, token);
                AddReceipt(attempt, true, upload.PolicyRawResponse, token);
                db.Accessions.Add(new Accession
                {
                    SubmissionId = submissionId,
                    ObjectKind = "workflowhub-" + artifact.ItemKind,
                    Alias = alias,
                    AccessionNumber = upload.WorkflowId.ToString(CultureInfo.InvariantCulture),
                    SecondaryAccession = upload.Version.ToString(CultureInfo.InvariantCulture)
                });
                attempt.Status = SubmissionStatus.Submitted;
                db.SubmissionEvents.Add(new SubmissionEvent
                {
                    SubmissionId = submissionId,
                    Step = "upload.succeeded",
                    Message = $"Uploaded {label} to WorkflowHub as {Redact(upload.Url, token)} ({(upload.IsPublic ? "public" : "private")})."
                });
                outcome = ItemOutcome.Succeeded;
            }
            catch (WorkflowHubGatewayException ex) when (ex.Kind == WorkflowHubFailureKind.UploadedButPolicyUnset)
            {
                var detail = Redact(ex.Detail, token);

                // The visibility PATCH that failed is kept as exactly as the one that succeeds: the body
                // that was sent and the raw answer. Without an answer, the receipt holds the detail.
                if (!string.IsNullOrEmpty(ex.PolicyRequest))
                {
                    db.SubmissionPayloads.Add(new SubmissionPayload
                    {
                        AttemptId = attempt.Id,
                        ObjectKind = "workflowhub-policy",
                        Content = Redact(ex.PolicyRequest, token)
                    });
                }

                AddReceipt(attempt, true, ex.RawBody, token);
                AddReceipt(attempt, false, string.IsNullOrEmpty(ex.PolicyRawBody) ? detail : ex.PolicyRawBody, token);
                if (ex.WorkflowId is { } workflowId)
                {
                    db.Accessions.Add(new Accession
                    {
                        SubmissionId = submissionId,
                        ObjectKind = "workflowhub-" + artifact.ItemKind,
                        Alias = alias,
                        AccessionNumber = workflowId.ToString(CultureInfo.InvariantCulture)
                    });
                }

                var url = ex.WorkflowId is { } id ? $"{baseUrl}/workflows/{id}" : baseUrl;
                attempt.Status = SubmissionStatus.PartiallyFailed;
                attempt.ErrorSummary = Truncate("The entry was created, but setting its visibility failed: " + detail);
                db.SubmissionEvents.Add(new SubmissionEvent
                {
                    SubmissionId = submissionId,
                    Step = "upload.policy_unset",
                    Level = SubmissionEventLevel.Warning,
                    Message = makePublic
                        ? $"Uploaded {label} as {url}, but applying the public visibility failed. Check its visibility on WorkflowHub."
                        : $"Uploaded {label} as {url}, but setting it to private failed. It is public now. Change its visibility on WorkflowHub."
                });
                outcome = ItemOutcome.PartiallyFailed;
            }
            catch (WorkflowHubGatewayException ex) when (ex.Kind == WorkflowHubFailureKind.OutcomeUnknown)
            {
                RecordUnknownOutcome(attempt, label, baseUrl,
                    string.IsNullOrEmpty(ex.RawBody) ? ex.Detail : ex.RawBody, token);
                outcome = ItemOutcome.Unknown;
            }
            catch (WorkflowHubGatewayException ex)
            {
                var detail = Redact(ex.Detail, token);
                var summary = Truncate(ex.Kind == WorkflowHubFailureKind.Rejected
                    ? $"WorkflowHub rejected the upload (HTTP {ex.Status}): {detail}"
                    : $"WorkflowHub could not be reached: {detail}");

                AddReceipt(attempt, false, string.IsNullOrEmpty(ex.RawBody) ? ex.Detail : ex.RawBody, token);
                attempt.Status = SubmissionStatus.Failed;
                attempt.ErrorSummary = summary;
                db.SubmissionEvents.Add(new SubmissionEvent
                {
                    SubmissionId = submissionId,
                    Step = "upload.failed",
                    Level = SubmissionEventLevel.Error,
                    Message = $"Uploading {label} to WorkflowHub failed. {summary}"
                });
                outcome = ItemOutcome.Failed;
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the crate may have reached WorkflowHub. Only the type name is
                // logged and stored, because the message may quote request details.
                logger.LogWarning("Upload for submission {SubmissionId} failed with {ExceptionType}",
                    submissionId, ex.GetType().Name);
                RecordUnknownOutcome(attempt, label, baseUrl, "Unexpected failure: " + ex.GetType().Name, token);
                outcome = ItemOutcome.Unknown;
            }

            attempt.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(work);
            outcomes.Add(outcome);
        }

        if (outcomes.Count > 0)
        {
            // An entry on WorkflowHub from any run counts, so a failed second run does not
            // wipe out what the first one uploaded.
            var hasAccession = await db.Accessions.AnyAsync(a => a.SubmissionId == submissionId, work);
            var runHadFailure = outcomes.Any(o => o != ItemOutcome.Succeeded);
            submission.Status = !hasAccession ? SubmissionStatus.Failed
                : runHadFailure ? SubmissionStatus.PartiallyFailed
                : SubmissionStatus.Submitted;
            submission.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(work);
        }

        logger.LogInformation("WorkflowHub upload for submission {SubmissionId} finished with {Count} attempted items",
            submissionId, outcomes.Count);

        return new WorkflowHubUploadResult(
            Problem: null,
            Succeeded: outcomes.Count(o => o == ItemOutcome.Succeeded),
            PartiallyFailed: outcomes.Count(o => o == ItemOutcome.PartiallyFailed),
            Failed: outcomes.Count(o => o == ItemOutcome.Failed),
            Skipped: skipped,
            Unknown: outcomes.Count(o => o == ItemOutcome.Unknown));
    }

    /// <summary>
    /// The first reason the selection cannot be uploaded, as text for the user, or null. The
    /// authorize route runs this before it sends the user to WorkflowHub, and
    /// <see cref="UploadAsync"/> runs it again after the callback.
    /// </summary>
    public async Task<string?> ValidateSelection(
        Guid submissionId, IReadOnlyList<WorkflowHubSelectedItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return "Select at least one item to upload.";

        var ids = items.Select(i => i.ArtifactId).ToList();
        if (ids.Distinct().Count() != ids.Count) return "An item was selected twice.";

        var rows = await db.PreparedArtifacts.AsNoTracking()
            .Where(a => a.SubmissionId == submissionId && ids.Contains(a.Id))
            .Select(a => new { a.Id, a.ItemKind, a.ItemIdentifier, a.Status, HasContent = a.Content != null })
            .ToListAsync(ct);
        var uploaded = (await db.Accessions.AsNoTracking()
            .Where(a => a.SubmissionId == submissionId)
            .Select(a => a.Alias)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            var row = rows.FirstOrDefault(r => r.Id == id);
            if (row is null) return "A selected item does not belong to this submission.";

            var label = $"{row.ItemKind} {row.ItemIdentifier}";
            if (row.Status != PreparedArtifactStatus.Ready || !row.HasContent)
                return $"{label} is not ready for upload.";
            if (uploaded.Contains($"{row.ItemKind}/{row.ItemIdentifier}"))
                return $"{label} was uploaded already.";
        }

        return null;
    }

    /// <summary>
    /// Uploads one crate as a new version of an existing entry and records the attempt. The entry id
    /// comes from the lookup. The client reads the entry's own teams at upload time, so the stored
    /// request description says so instead of listing team ids. The client sends no visibility
    /// request for a version, so there is no policy payload, and a failure leaves the item selectable.
    /// </summary>
    private async Task<ItemOutcome> UploadVersionAsync(
        Guid submissionId,
        int attemptNumber,
        PreparedArtifact artifact,
        byte[] crate,
        string label,
        string alias,
        string baseUrl,
        WorkflowHubEarlierEntry entry,
        string token,
        CancellationToken work)
    {
        var attempt = new SubmissionAttempt { SubmissionId = submissionId, AttemptNumber = attemptNumber };
        db.SubmissionAttempts.Add(attempt);
        db.SubmissionPayloads.Add(new SubmissionPayload
        {
            AttemptId = attempt.Id,
            ObjectKind = "workflowhub-crate",
            Content = JsonSerializer.Serialize(new
            {
                method = "POST",
                url = baseUrl + WfHubClient.WorkflowHubApi.createVersionPath(entry.EntryId),
                artifactId = artifact.Id,
                itemKind = artifact.ItemKind,
                itemIdentifier = artifact.ItemIdentifier,
                sha256 = artifact.ContentSha256,
                sizeBytes = artifact.SizeBytes,
                entryId = entry.EntryId,
                fields = new object[]
                {
                    new
                    {
                        name = WfHubClient.WorkflowHubApi.CrateFieldName,
                        fileName = WfHubClient.WorkflowHubApi.CrateFileName,
                        contentType = WfHubClient.WorkflowHubApi.CrateContentType
                    }
                },
                teams = "The team ids sent with the crate are the entry's own. The client reads them from the entry at upload time, so the service does not know them before the call."
            })
        });
        await db.SaveChangesAsync(work);

        var entryText = entry.EntryId.ToString(CultureInfo.InvariantCulture);
        ItemOutcome outcome;
        try
        {
            var upload = await gateway.CreateVersionAsync(baseUrl, token, entry.EntryId, crate, work);

            if (upload.WorkflowId != entry.EntryId)
            {
                // WorkflowHub named another entry than the one asked for, so nothing is recorded as uploaded.
                RecordUnknownOutcome(attempt, label, baseUrl, upload.RawResponse, token, entry);
                outcome = ItemOutcome.Unknown;
            }
            else
            {
                AddReceipt(attempt, true, upload.RawResponse, token);
                db.Accessions.Add(new Accession
                {
                    SubmissionId = submissionId,
                    ObjectKind = "workflowhub-" + artifact.ItemKind,
                    Alias = alias,
                    AccessionNumber = entryText,
                    SecondaryAccession = upload.Version.ToString(CultureInfo.InvariantCulture)
                });
                attempt.Status = SubmissionStatus.Submitted;
                db.SubmissionEvents.Add(new SubmissionEvent
                {
                    SubmissionId = submissionId,
                    Step = "upload.version_succeeded",
                    Message = $"Uploaded {label} as version {upload.Version.ToString(CultureInfo.InvariantCulture)} of entry {entryText}."
                });
                outcome = ItemOutcome.Succeeded;
            }
        }
        catch (WorkflowHubGatewayException ex)
            when (ex.Kind is WorkflowHubFailureKind.OutcomeUnknown or WorkflowHubFailureKind.UploadedButPolicyUnset)
        {
            // A version has no visibility step, so the second kind should not occur. If it does, the
            // crate reached WorkflowHub, and the result is as unclear as after a lost answer.
            RecordUnknownOutcome(attempt, label, baseUrl,
                string.IsNullOrEmpty(ex.RawBody) ? ex.Detail : ex.RawBody, token, entry);
            outcome = ItemOutcome.Unknown;
        }
        catch (WorkflowHubGatewayException ex)
        {
            // WorkflowHub refused, for example because the person cannot read or edit the entry or the
            // entry is gone, or the request never left. Either way no version was made.
            var detail = Redact(ex.Detail, token);
            var summary = Truncate(ex.Kind == WorkflowHubFailureKind.Rejected
                ? $"WorkflowHub rejected the new version of entry {entryText} (HTTP {ex.Status}): {detail}"
                : $"WorkflowHub could not be reached to add a version to entry {entryText}: {detail}");

            // The advice fits the cause. Only a refusal about the entry can be answered with another entry or other rights.
            var advice = ex.Kind == WorkflowHubFailureKind.Transport
                ? "Nothing was uploaded for this item. Try again later."
                : ex.Status == 401
                    ? "WorkflowHub did not accept the login. Start the upload again."
                    : $"You can choose \"Upload as a new entry\" for this item, or get edit rights on entry {entryText} and try the new version again.";

            AddReceipt(attempt, false, string.IsNullOrEmpty(ex.RawBody) ? ex.Detail : ex.RawBody, token);
            attempt.Status = SubmissionStatus.Failed;
            attempt.ErrorSummary = summary;
            db.SubmissionEvents.Add(new SubmissionEvent
            {
                SubmissionId = submissionId,
                Step = "upload.failed",
                Level = SubmissionEventLevel.Error,
                Message = $"Uploading {label} as a new version of entry {entryText} failed. {summary} {advice}"
            });
            outcome = ItemOutcome.Failed;
        }
        catch (Exception ex)
        {
            // Same rule as for a new entry: only the type name is logged and stored.
            logger.LogWarning("Upload for submission {SubmissionId} failed with {ExceptionType}",
                submissionId, ex.GetType().Name);
            RecordUnknownOutcome(attempt, label, baseUrl, "Unexpected failure: " + ex.GetType().Name, token, entry);
            outcome = ItemOutcome.Unknown;
        }

        attempt.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(work);
        return outcome;
    }

    private void AddReceipt(SubmissionAttempt attempt, bool success, string content, string token)
        => db.SubmissionReceipts.Add(new SubmissionReceipt
        {
            AttemptId = attempt.Id,
            Success = success,
            Content = Redact(content, token)
        });

    /// <summary>
    /// Records an upload whose result is not known. The entry may exist on WorkflowHub and may be
    /// public, and no accession is stored because nothing identifies the entry. For a new version,
    /// <paramref name="versionOf"/> names the entry, and the text says a version may have been created.
    /// Both texts start with <see cref="UnknownOutcomeLead"/>, which is how the preview page finds them.
    /// </summary>
    private void RecordUnknownOutcome(
        SubmissionAttempt attempt, string label, string baseUrl, string receipt, string token,
        WorkflowHubEarlierEntry? versionOf = null)
    {
        var message = versionOf is { } entry
            ? $"{UnknownOutcomeLead}{label}. A new version of entry {entry.EntryId.ToString(CultureInfo.InvariantCulture)} may have been created. " +
              $"Check {baseUrl}/workflows/{entry.EntryId.ToString(CultureInfo.InvariantCulture)} before uploading this item again."
            : $"{UnknownOutcomeLead}{label}. The entry may exist on WorkflowHub " +
              $"and may be public. Check {baseUrl}/workflows before uploading this item again.";

        AddReceipt(attempt, false, receipt, token);
        attempt.Status = SubmissionStatus.Failed;
        attempt.ErrorSummary = message;
        db.SubmissionEvents.Add(new SubmissionEvent
        {
            SubmissionId = attempt.SubmissionId,
            Step = "upload.outcome_unknown",
            Level = SubmissionEventLevel.Error,
            Message = message
        });
    }

    private async Task AddEvent(
        Guid submissionId, string step, SubmissionEventLevel level, string message, CancellationToken ct)
    {
        db.SubmissionEvents.Add(new SubmissionEvent
        {
            SubmissionId = submissionId,
            Step = step,
            Level = level,
            Message = message
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Defense in depth. WorkflowHub's answers do not echo the token, and this strips it if one ever does.</summary>
    private static string Redact(string text, string token)
        => string.IsNullOrEmpty(token) ? text : text.Replace(token, Redacted, StringComparison.Ordinal);

    private static string Truncate(string text)
        => text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "...";
}
