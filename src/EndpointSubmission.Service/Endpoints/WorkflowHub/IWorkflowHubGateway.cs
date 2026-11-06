namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>
/// Everything this service asks of a WorkflowHub instance, in its own terms. The OAuth access
/// token is a method argument of one call and is never kept.
/// </summary>
public interface IWorkflowHubGateway
{
    /// <summary>
    /// Trades the authorization code of the OAuth callback for an access token. The client is
    /// public, so the PKCE verifier takes the place of a client secret.
    /// </summary>
    Task<string> ExchangeCodeAsync(
        string baseUrl, string clientId, string code, string redirectUri, string codeVerifier, CancellationToken ct);

    /// <summary>The teams the token's owner belongs to.</summary>
    Task<IReadOnlyList<WorkflowHubTeam>> ListTeamsAsync(string baseUrl, string accessToken, CancellationToken ct);

    /// <summary>Uploads one crate to a team and applies the chosen visibility.</summary>
    Task<WorkflowHubUpload> UploadAsync(
        string baseUrl, string accessToken, byte[] crate, int teamId, bool makePublic, CancellationToken ct);

    /// <summary>Ends an access token (RFC 7009).</summary>
    Task RevokeAsync(string baseUrl, string clientId, string accessToken, CancellationToken ct);
}

public sealed record WorkflowHubTeam(int Id, string Title);

/// <param name="RawResponse">WorkflowHub's answer to the crate POST, unparsed, kept as the receipt.</param>
/// <param name="PolicyRequest">The exact JSON body of the visibility PATCH that was sent.</param>
/// <param name="PolicyRawResponse">WorkflowHub's answer to that PATCH, unparsed, kept as the receipt.</param>
public sealed record WorkflowHubUpload(
    int WorkflowId, string Url, int Version, bool IsPublic,
    string RawResponse, string PolicyRequest, string PolicyRawResponse);

public enum WorkflowHubFailureKind
{
    /// <summary>WorkflowHub answered with an error status.</summary>
    Rejected,
    /// <summary>The request did not complete, or the response could not be read.</summary>
    Transport,
    /// <summary>The entry exists, but applying its visibility failed.</summary>
    UploadedButPolicyUnset,
    /// <summary>
    /// The crate POST may have created an entry, but no usable answer arrived. The entry may exist
    /// and may be public.
    /// </summary>
    OutcomeUnknown
}

/// <summary>
/// A WorkflowHub failure. <see cref="Detail"/> comes from WorkflowHub's answer and can be long.
/// The message is fixed text, so logging the exception cannot leak a request value.
/// </summary>
public sealed class WorkflowHubGatewayException(
    WorkflowHubFailureKind kind, int? status, string detail, int? workflowId, string rawBody = "",
    string policyRequest = "", string policyRawBody = "")
    : Exception($"WorkflowHub request failed ({kind}).")
{
    public WorkflowHubFailureKind Kind { get; } = kind;

    /// <summary>The HTTP status for <see cref="WorkflowHubFailureKind.Rejected"/>.</summary>
    public int? Status { get; } = status;

    public string Detail { get; } = detail;

    /// <summary>The entry that was created, for <see cref="WorkflowHubFailureKind.UploadedButPolicyUnset"/>.</summary>
    public int? WorkflowId { get; } = workflowId;

    /// <summary>The response body the failure came from, for the audit trail. Empty when there is none.</summary>
    public string RawBody { get; } = rawBody;

    /// <summary>
    /// The exact JSON body of the visibility PATCH that failed, for
    /// <see cref="WorkflowHubFailureKind.UploadedButPolicyUnset"/>. Empty otherwise.
    /// </summary>
    public string PolicyRequest { get; } = policyRequest;

    /// <summary>
    /// The raw body WorkflowHub answered that PATCH with, for the audit trail. Empty when no response arrived.
    /// </summary>
    public string PolicyRawBody { get; } = policyRawBody;
}
