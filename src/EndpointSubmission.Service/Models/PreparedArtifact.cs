namespace EndpointSubmission.Service.Models;

public enum PreparedArtifactStatus
{
    /// <summary>Prepared and safe to hand to the endpoint repository.</summary>
    Ready = 0,
    /// <summary>The ARC does not meet the endpoint's requirements. The reasons are listed.</summary>
    NotReady = 1,
    /// <summary>The item met the requirements but building its artifact failed.</summary>
    PreparationFailed = 2
}

/// <summary>
/// One item the hook prepared from the ARC for an endpoint, for example a WorkflowHub crate.
/// The table is endpoint-neutral so another endpoint can store its prepared documents here.
///
/// The hook request builds these rows while the short-lived PAT is valid, so later pages and
/// uploads never need the PAT. <see cref="Content"/> can be large. Keep it out of list queries:
/// pages project the metadata columns only and load the bytes for a single item when they send it.
/// </summary>
public class PreparedArtifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubmissionId { get; set; }
    public Submission? Submission { get; set; }

    /// <summary>Endpoint-defined kind. WorkflowHub uses "workflow" and "run".</summary>
    public string ItemKind { get; set; } = string.Empty;

    /// <summary>Stable identifier of the item inside the ARC, unique per kind within a submission.</summary>
    public string ItemIdentifier { get; set; } = string.Empty;

    public string? Title { get; set; }
    public string? Description { get; set; }

    public PreparedArtifactStatus Status { get; set; }

    /// <summary>JSON array of strings. Empty for Ready items.</summary>
    public string FailureReasonsJson { get; set; } = "[]";

    /// <summary>
    /// Endpoint-specific details. WorkflowHub stores <c>licenseSpdxId</c> (string or null), <c>licensePresent</c>
    /// (bool) and <c>lfsPointerFiles</c> (array of the zip paths of the LFS pointer files in the crate).
    /// Rows written before <c>lfsPointerFiles</c> existed do not have that key.
    /// </summary>
    public string MetadataJson { get; set; } = "{}";

    public long? SizeBytes { get; set; }

    /// <summary>The prepared file. Only Ready items carry content.</summary>
    public byte[]? Content { get; set; }

    /// <summary>Lowercase hex SHA-256 of <see cref="Content"/>.</summary>
    public string? ContentSha256 { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
