using Microsoft.Extensions.Options;

namespace EndpointSubmission.Service.Services;

/// <summary>
/// Validates the caller-supplied <c>ro_crate_url</c> and decomposes it into the generic-package
/// coordinates the DataHubClient library fetches by.
///
/// Two jobs, both security-relevant. The URL arrives in the hook body and is retrieved while
/// holding a GitLab token, so fetching it unchecked would let any caller aim an authenticated
/// request at an arbitrary host — including internal addresses only reachable from inside the
/// deployment. Hence the explicit host allowlist, HTTPS only. Decomposing rather than passing the
/// string through then means the address actually requested is rebuilt from validated parts.
/// </summary>
public class DataHubUrlGuard(IOptions<EndpointSubmissionOptions> options)
{
    private readonly EndpointSubmissionOptions _options = options.Value;

    /// <summary>
    /// Validates <paramref name="rawUrl"/> and resolves it to a <see cref="RoCrateLocation"/>.
    /// </summary>
    /// <param name="expectedProjectId">
    /// The <c>project_id</c> from the hook body. The URL carries its own project id; a mismatch
    /// means the two halves of the payload disagree about which ARC is being submitted, which is
    /// never legitimate.
    /// </param>
    public bool TryResolve(string? rawUrl, int expectedProjectId, out RoCrateLocation? location, out string? error)
    {
        location = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "ro_crate_url is required.";
            return false;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsed))
        {
            error = "ro_crate_url is not an absolute URL.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            // A token travels with this request; plaintext is not an option, and allowing http
            // would also open up redirect-to-internal tricks.
            error = "ro_crate_url must use https.";
            return false;
        }

        var allowed = _options.AllowedDataHubHosts
            .Any(h => string.Equals(h, parsed.Host, StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            // Deliberately does not echo the configured allowlist back to the caller.
            error = $"Host '{parsed.Host}' is not an allowed DataHUB host.";
            return false;
        }

        // /api/v4/projects/{projectId}/packages/generic/{packageName}/{version}/{fileName}
        var segments = parsed.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (segments.Length != 9
            || segments[0] != "api"
            || segments[1] != "v4"
            || segments[2] != "projects"
            || segments[4] != "packages"
            || segments[5] != "generic")
        {
            error = "ro_crate_url is not a DataHUB generic package file URL "
                  + "(expected /api/v4/projects/{id}/packages/generic/{name}/{version}/{file}).";
            return false;
        }

        if (!int.TryParse(segments[3], out var projectId))
        {
            error = "ro_crate_url does not contain a numeric project id.";
            return false;
        }

        if (projectId != expectedProjectId)
        {
            error = $"ro_crate_url is for project {projectId}, but the hook declared project {expectedProjectId}.";
            return false;
        }

        location = new RoCrateLocation(
            Instance: new Uri(parsed.GetLeftPart(UriPartial.Authority)),
            ProjectId: projectId,
            PackageName: segments[6],
            Version: segments[7],
            FileName: segments[8]);

        return true;
    }
}
