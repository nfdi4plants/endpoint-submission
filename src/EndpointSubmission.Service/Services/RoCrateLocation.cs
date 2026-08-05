namespace EndpointSubmission.Service.Services;

/// <summary>
/// A DataHUB generic-package file, decomposed from the <c>ro_crate_url</c> in the hook body.
///
/// The hook hands us a URL string; the DataHubClient library fetches by component
/// (<c>projectId</c>, package name, version, file name) and rebuilds the URL itself. That is
/// the safer direction: the address actually requested is one we constructed from validated
/// parts, not one the caller supplied. The host allowlist still applies — this narrows what a
/// permitted host can be asked for.
/// </summary>
/// <param name="Instance">DataHUB root, without the <c>/api/v4</c> suffix.</param>
public record RoCrateLocation(
    Uri Instance,
    int ProjectId,
    string PackageName,
    string Version,
    string FileName);
