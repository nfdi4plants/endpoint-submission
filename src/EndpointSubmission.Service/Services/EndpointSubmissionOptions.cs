namespace EndpointSubmission.Service.Services;

public class EndpointSubmissionOptions
{
    public const string SectionName = "EndpointSubmission";

    /// <summary>
    /// DataHUB hosts whose URLs this service is willing to fetch. The hook body contains a
    /// caller-supplied <c>ro_crate_url</c> that we retrieve while holding a GitLab token;
    /// without this list that is a server-side request forgery primitive. Empty means
    /// "fetch nothing", which is the correct failure mode for a misconfigured deployment.
    /// </summary>
    public string[] AllowedDataHubHosts { get; set; } = [];

    /// <summary>
    /// Master switch for production submissions. Production also requires the package
    /// payload to ask for it — two independent opt-ins, because the action is irreversible
    /// and a single misconfiguration should not be able to cause it.
    /// </summary>
    public bool AllowProductionTarget { get; set; } = false;

    /// <summary>Public origin used to build follow_me URLs, e.g. "https://submit.nfdi4plants.org".</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// How stale an <c>iat</c> may be before the hook is rejected. Bounds replay of a
    /// captured hook body.
    /// </summary>
    public int HookMaxAgeSeconds { get; set; } = 300;

    /// <summary>
    /// Shared secret the caller must present in X-Hook-Secret outside Development. Unset means
    /// the hook endpoint refuses to serve. Development uses a DI-provided bypass authenticator.
    /// See <see cref="Authentication.SharedSecretHookAuthenticator"/> for what this control does
    /// and does not protect.
    /// </summary>
    public string? HookSharedSecret { get; set; }
}
