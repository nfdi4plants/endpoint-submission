using System.Text.Json;
using System.Text.Json.Serialization;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Services;

namespace EndpointSubmission.Service.Endpoints.ENA;

/// <summary>
/// Owns the ENA-specific part of hook ingestion. The complete hook request, including its
/// short-lived PAT, is deliberately passed here so future ENA preparation may perform any
/// required authenticated DataHUB reads before the request ends.
/// </summary>
public class EnaSubmissionHandler(SubmissionIntakeService intake)
{
    public Task<IntakeResult> HandleAsync(HookRequest request, CancellationToken ct)
    {
        EnaPackagePayload payload;
        try
        {
            payload = request.PackagePayload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? new EnaPackagePayload()
                : request.PackagePayload.Deserialize<EnaPackagePayload>() ?? new EnaPackagePayload();
        }
        catch (JsonException ex)
        {
            return Task.FromResult<IntakeResult>(
                new IntakeResult.Rejected($"package_payload is not valid for endpoint 'ena': {ex.Message}"));
        }

        if (payload.ContractVersion != 1)
            return Task.FromResult<IntakeResult>(
                new IntakeResult.Rejected($"Unsupported package_payload contract_version {payload.ContractVersion}."));

        var target = ParseTarget(payload.Target);
        if (target is null)
            return Task.FromResult<IntakeResult>(
                new IntakeResult.Rejected($"Unknown target '{payload.Target}'. Expected 'sandbox' or 'production'."));

        return intake.IntakeAsync(request, "ena", target.Value, payload.ReleaseDate, ct);
    }

    private static SubmissionTarget? ParseTarget(string? target) => target?.Trim().ToLowerInvariant() switch
    {
        "production" => SubmissionTarget.Production,
        "sandbox" or null or "" => SubmissionTarget.Sandbox,
        _ => null
    };
}

public class EnaPackagePayload
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = "sandbox";

    [JsonPropertyName("checklist")]
    public string? Checklist { get; set; }

    [JsonPropertyName("release_date")]
    public DateOnly? ReleaseDate { get; set; }

    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; set; } = 1;
}
