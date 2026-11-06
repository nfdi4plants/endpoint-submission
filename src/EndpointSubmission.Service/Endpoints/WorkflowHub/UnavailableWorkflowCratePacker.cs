namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>
/// Placeholder until the real packer is wired in. Every hook reports the same preparation
/// failure, so a deployment without a packer shows an explanation on the preview page.
/// </summary>
public sealed class UnavailableWorkflowCratePacker : IWorkflowCratePacker
{
    public Task<WorkflowPreparation> PrepareAsync(
        Uri dataHubInstance, int projectId, string commitSha, string token, CancellationToken ct)
        => Task.FromResult(new WorkflowPreparation(
            Items: [],
            LicenseSpdxId: null,
            LicensePresent: false,
            Error: "Crate preparation is not available in this deployment yet."));
}
