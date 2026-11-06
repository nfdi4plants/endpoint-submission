namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>
/// Builds the WorkflowHub crates for one ARC commit. The hook calls it during the POST because
/// it needs the GitLab token, which is passed as an argument and never kept.
/// </summary>
public interface IWorkflowCratePacker
{
    Task<WorkflowPreparation> PrepareAsync(
        Uri dataHubInstance, int projectId, string commitSha, string token, CancellationToken ct);
}

/// <param name="Items">One entry per workflow and run found in the ARC.</param>
/// <param name="LicenseSpdxId">SPDX id of the ARC license, or null when it was not recognized.</param>
/// <param name="LicensePresent">False when the ARC has no LICENSE file.</param>
/// <param name="Error">
/// A preparation-level failure the user may see, for example a failed checkout. Implementations
/// return it without credentials.
/// </param>
public sealed record WorkflowPreparation(
    IReadOnlyList<PreparedWorkflowItem> Items,
    string? LicenseSpdxId,
    bool LicensePresent,
    string? Error);

/// <param name="Kind">"workflow" or "run".</param>
/// <param name="Failures">Reasons the item is not uploadable. Empty for Uploadable items.</param>
/// <param name="Crate">The zip to upload. Set for Uploadable items only.</param>
public sealed record PreparedWorkflowItem(
    string Kind,
    string Identifier,
    string? Title,
    string? Description,
    PreparedWorkflowItemStatus Status,
    IReadOnlyList<string> Failures,
    byte[]? Crate);

public enum PreparedWorkflowItemStatus
{
    Uploadable,
    NotReady,
    PackFailed
}
