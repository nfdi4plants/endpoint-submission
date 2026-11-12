using WfHubClient;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>
/// <see cref="IWorkflowHubGateway"/> over the WfHubClient library. This class keeps the library's
/// F# types, such as lists and <c>WorkflowHubException</c>, out of the rest of the service.
///
/// The library calls take no cancellation token, so cancellation abandons the wait and lets the
/// running request finish in the background.
/// </summary>
public sealed class WfHubGateway : IWorkflowHubGateway
{
    public Task<string> ExchangeCodeAsync(
        string baseUrl, string clientId, string code, string redirectUri, string codeVerifier, CancellationToken ct)
        => Run(() => Client.exchangeAuthorizationCode(baseUrl, clientId, code, redirectUri, codeVerifier), ct);

    public async Task<IReadOnlyList<WorkflowHubTeam>> ListTeamsAsync(
        string baseUrl, string accessToken, CancellationToken ct)
    {
        var teams = await Run(() => Client.listTeams(baseUrl, accessToken), ct);
        return teams.Select(team => new WorkflowHubTeam(team.Id, team.Title)).ToList();
    }

    public async Task<WorkflowHubUpload> UploadAsync(
        string baseUrl, string accessToken, byte[] crate, int teamId, bool makePublic, CancellationToken ct)
    {
        var result = await Run(
            () => Client.uploadCrate(
                baseUrl, accessToken, crate, teamId, makePublic ? Visibility.Public : Visibility.Private),
            ct);
        return ToUpload(result);
    }

    public async Task<WorkflowHubUpload> CreateVersionAsync(
        string baseUrl, string accessToken, int workflowId, byte[] crate, CancellationToken ct)
        => ToUpload(await Run(() => Client.createVersion(baseUrl, accessToken, workflowId, crate), ct));

    private static WorkflowHubUpload ToUpload(UploadResult result)
        => new(
            result.WorkflowId, result.Url, result.Version, result.Applied.IsPublic,
            result.RawResponse, result.PolicyRequest, result.PolicyRawResponse);

    public Task RevokeAsync(string baseUrl, string clientId, string accessToken, CancellationToken ct)
        => Run(() => Client.revokeToken(baseUrl, clientId, accessToken), ct);

    private static async Task<T> Run<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call().WaitAsync(ct);
        }
        catch (WorkflowHubException ex)
        {
            throw Map(ex);
        }
    }

    private static async Task Run(Func<Task> call, CancellationToken ct)
    {
        try
        {
            await call().WaitAsync(ct);
        }
        catch (WorkflowHubException ex)
        {
            throw Map(ex);
        }
    }

    internal static WorkflowHubGatewayException Map(WorkflowHubException ex) => ex.Error switch
    {
        UploadError.Rejected rejected =>
            new(WorkflowHubFailureKind.Rejected, rejected.status, rejected.detail, null, ex.RawBody),
        UploadError.Transport transport =>
            new(WorkflowHubFailureKind.Transport, null, transport.message, null, ex.RawBody),
        UploadError.UploadedButPolicyUnset unset =>
            new(WorkflowHubFailureKind.UploadedButPolicyUnset, null, unset.detail, unset.workflowId, ex.RawBody,
                ex.PolicyRequest, ex.PolicyRawBody),
        UploadError.OutcomeUnknown unknown =>
            new(WorkflowHubFailureKind.OutcomeUnknown, null, unknown.detail, null, ex.RawBody),
        _ => new(WorkflowHubFailureKind.Transport, null, "WorkflowHub answered in an unknown way.", null, ex.RawBody)
    };
}
