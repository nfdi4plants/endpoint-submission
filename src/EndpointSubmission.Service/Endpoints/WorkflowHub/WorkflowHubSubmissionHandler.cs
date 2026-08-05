using System.Text.Json;
using System.Text.Json.Serialization;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Services;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>
/// Owns WorkflowHub's hook payload and preparation path. It receives the original request so
/// endpoint-specific preparation can use the PAT during this POST without ever persisting it.
/// </summary>
public class WorkflowHubSubmissionHandler(SubmissionIntakeService intake)
{
    public Task<IntakeResult> HandleAsync(HookRequest request, CancellationToken ct)
    {
        WorkflowHubPackagePayload payload;
        try
        {
            payload = request.PackagePayload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? new WorkflowHubPackagePayload()
                : request.PackagePayload.Deserialize<WorkflowHubPackagePayload>() ?? new WorkflowHubPackagePayload();
        }
        catch (JsonException ex)
        {
            return Task.FromResult<IntakeResult>(
                new IntakeResult.Rejected($"package_payload is not valid for endpoint 'workflowhub': {ex.Message}"));
        }

        if (payload.ContractVersion != 1)
            return Task.FromResult<IntakeResult>(
                new IntakeResult.Rejected($"Unsupported package_payload contract_version {payload.ContractVersion}."));

        var target = ParseTarget(payload.Target);
        if (target is null)
            return Task.FromResult<IntakeResult>(
                new IntakeResult.Rejected($"Unknown target '{payload.Target}'. Expected 'sandbox' or 'production'."));

        return intake.IntakeAsync(request, "workflowhub", target.Value, releaseDate: null, ct);
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
