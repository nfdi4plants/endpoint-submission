namespace EndpointSubmission.Service.Models;

/// <summary>
/// Which instance of the endpoint repository a submission targets.
/// Sandbox is the default everywhere; production must be opted into twice
/// (service configuration AND package payload), because submission mints
/// permanent public identifiers that cannot be withdrawn.
/// </summary>
public enum SubmissionTarget
{
    Sandbox = 0,
    Production = 1
}

public enum SubmissionStatus
{
    /// <summary>Hook received, ARC metadata captured, nothing submitted yet.</summary>
    Created = 0,
    /// <summary>Submission artifact resolved and renderable.</summary>
    PreviewReady = 1,
    /// <summary>Handed to the endpoint repository, awaiting its verdict.</summary>
    Submitting = 2,
    /// <summary>Accepted in full.</summary>
    Submitted = 3,
    /// <summary>Some objects accepted, others rejected. Not recoverable by retry alone.</summary>
    PartiallyFailed = 4,
    /// <summary>Nothing was accepted.</summary>
    Failed = 5
}

/// <summary>
/// One ARC, one intent to publish. Re-clicking the badge does not create a second
/// submission; it produces another <see cref="SubmissionAttempt"/> against this one.
/// </summary>
public class Submission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // --- ARC identity, captured during the hook request -----------------------

    public int ProjectId { get; set; }

    /// <summary>GitLab path_with_namespace, e.g. "group/subgroup/my-arc".</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Host of the DataHUB instance this ARC lives on. Recorded because one
    /// deployment may serve several instances, and because it is the value
    /// checked against the allowlist.
    /// </summary>
    public string DataHubHost { get; set; } = string.Empty;

    /// <summary>
    /// Commit the submission describes. Resolved via the GitLab API during the hook request,
    /// since the AaaS payload does not carry one — tracked in plans/0001 under
    /// "commit_sha is not in the AaaS payload".
    /// </summary>
    public string CommitSha { get; set; } = string.Empty;

    public string RoCrateUrl { get; set; } = string.Empty;

    // --- Trigger provenance ---------------------------------------------------

    /// <summary>Verbatim package_payload as received, for audit. Parsed form is not stored.</summary>
    public string PackagePayloadJson { get; set; } = "{}";

    public string Endpoint { get; set; } = "ena";

    public SubmissionTarget Target { get; set; } = SubmissionTarget.Sandbox;

    // --- Who ------------------------------------------------------------------
    // Authorization is "the clicker is sufficient", so nothing else attests that this
    // submission was sanctioned. These fields ARE the audit trail; treat them as such.

    public string SubmitterGitLabUser { get; set; } = string.Empty;
    public long SubmitterGitLabUserId { get; set; }
    public string SubmitterEmail { get; set; } = string.Empty;

    // --- Status page access ---------------------------------------------------

    /// <summary>
    /// SHA-256 of the status link token. The token itself is returned once, in follow_me,
    /// and never persisted — a database leak must not grant access to submission records.
    /// </summary>
    public string StatusTokenHash { get; set; } = string.Empty;

    // --- Submission policy ----------------------------------------------------

    /// <summary>Hold/release date. The only input this service collects that the ARC cannot supply.</summary>
    public DateOnly? ReleaseDate { get; set; }

    public SubmissionStatus Status { get; set; } = SubmissionStatus.Created;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<SubmissionAttempt> Attempts { get; set; } = [];
    public List<Accession> Accessions { get; set; } = [];
    public List<SubmissionEvent> Events { get; set; } = [];
}
