using System.ComponentModel.DataAnnotations;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

public class WorkflowHubOptions
{
    public const string SectionName = "WorkflowHub";

    /// <summary>
    /// The time limit of the ARC checkout in the hook request. Packing starts afterwards and runs
    /// to its end, so the hook can answer somewhat later than this limit. A checkout that runs out
    /// of time is recorded as a failed preparation, and the hook still answers with the preview link.
    /// </summary>
    [Range(1, 3600)]
    public int PreparationTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// How long a user may take on WorkflowHub between "Continue to WorkflowHub" and the return to
    /// the callback. The login cookie expires after this time.
    /// </summary>
    [Range(1, 60)]
    public int OAuthFlowMinutes { get; set; } = 10;

    /// <summary>
    /// DataHUB hosts whose hooks go to production when they name no target. Hooks that name a target
    /// keep it. The production target still needs EndpointSubmission:AllowProductionTarget.
    /// </summary>
    public List<string> ProductionDataHubHosts { get; set; } = [];

    /// <summary>The WorkflowHub test instance, used by submissions with target "sandbox".</summary>
    public WorkflowHubInstanceOptions Sandbox { get; set; } = new() { BaseUrl = "https://dev.workflowhub.eu" };

    /// <summary>The live WorkflowHub, used by submissions with target "production".</summary>
    public WorkflowHubInstanceOptions Production { get; set; } = new() { BaseUrl = "https://workflowhub.eu" };
}

public class WorkflowHubInstanceOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The id of the OAuth application registered on this instance as a non-confidential client with
    /// the redirect URI {PublicBaseUrl}/workflowhub/oauth/callback and the scopes "read write".
    /// Empty turns the upload login off. The service holds no client secret.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;
}
