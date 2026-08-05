using System.Threading.Tasks;
using Microsoft.FSharp.Control;
using Microsoft.FSharp.Core;
using DHC = DataHubClient;

namespace EndpointSubmission.Service.Services;

/// <summary>The DataHUB identity behind the token that arrived in the hook body.</summary>
public record ArcSubmitter(long UserId, string Username, string? Email);

/// <summary>
/// Everything this service reads from a DataHUB, expressed in its own terms.
///
/// A port over <see cref="DHC.DataHubClient"/> rather than direct use, for two reasons: it keeps
/// F# types (<c>FSharpAsync</c>, <c>FSharpOption</c>) out of the rest of the service, and it keeps
/// the intake path testable without a transport.
///
/// Every method takes the short-lived token from the hook body, and every call must complete while
/// that hook request is still being handled — by the time the user opens follow_me the token may be
/// long dead, so nothing may be deferred to page-load time.
/// </summary>
public interface IArcMetadataReader
{
    /// <summary>
    /// Resolves the identity the token authenticates as. This is the only DataHUB call that
    /// returns an email address, and under "the clicker is sufficient" it is the whole audit trail.
    /// </summary>
    Task<ArcSubmitter> GetSubmitterAsync(Uri instance, string token, CancellationToken ct);

    /// <summary>
    /// Resolves the project's default-branch HEAD.
    ///
    /// A stopgap: the AaaS payload carries no commit, so this is "HEAD when the badge was clicked",
    /// not "the commit the validation package ran against" — those differ whenever the ARC moves
    /// between CI finishing and the click. Tracked in plans/0001 under "commit_sha is not in the
    /// AaaS payload".
    /// </summary>
    Task<string?> ResolveHeadCommitAsync(Uri instance, int projectId, string token, CancellationToken ct);

    /// <summary>Downloads the ARC's RO-Crate metadata from the DataHUB's generic package registry.</summary>
    Task<string> FetchRoCrateAsync(RoCrateLocation location, string token, CancellationToken ct);
}

/// <summary>
/// <see cref="IArcMetadataReader"/> over the DataHubClient library.
///
/// Failures surface as the library's own exception hierarchy — <c>UnauthorizedError</c>,
/// <c>NotFoundError</c>, <c>RateLimitedError</c>, <c>ServerError</c> — which the intake path maps
/// onto its result cases. That is deliberately not flattened to nulls here: "the token was
/// rejected" and "the DataHUB is unwell" need to reach the caller differently.
/// </summary>
public class ArcMetadataReader : IArcMetadataReader
{
    public async Task<ArcSubmitter> GetSubmitterAsync(Uri instance, string token, CancellationToken ct)
    {
        var user = await Run(Client(instance, token).Users.GetCurrentAsync(), ct);
        return new ArcSubmitter(user.Id, user.Username, OptionModule.ToObj(user.Email));
    }

    public async Task<string?> ResolveHeadCommitAsync(Uri instance, int projectId, string token, CancellationToken ct)
    {
        var commits = await Run(
            Client(instance, token).Repository.ListCommitsAsync(
                projectId,
                FSharpOption<string>.None,
                FSharpOption<string>.None),
            ct);

        return commits.Length == 0 ? null : commits[0].Id;
    }

    public Task<string> FetchRoCrateAsync(RoCrateLocation location, string token, CancellationToken ct)
        => Run(
            Client(location.Instance, token).Packages.DownloadGenericFileAsync(
                location.ProjectId,
                location.PackageName,
                location.Version,
                location.FileName),
            ct);

    /// <summary>
    /// The token arrives per request and is never stored, so the client is per call rather than
    /// injected. AaaS sends a personal access token, which the library sends as PRIVATE-TOKEN —
    /// note that OAuth and CI job tokens need different headers and would silently 401 here.
    /// </summary>
    private static DHC.DataHubClient Client(Uri instance, string token) =>
        new(instance.GetLeftPart(UriPartial.Authority),
            DHC.Authentication.PersonalAccessToken(token));

    /// <summary>Bridges the library's F# <c>Async</c> to a cancellable <c>Task</c>.</summary>
    private static Task<T> Run<T>(FSharpAsync<T> computation, CancellationToken ct) =>
        FSharpAsync.StartAsTask(
            computation,
            FSharpOption<TaskCreationOptions>.None,
            FSharpOption<CancellationToken>.Some(ct));
}
