using System.Text.Json;
using System.Text.Json.Serialization;

namespace EndpointSubmission.Service.Models;

/// <summary>
/// The body AaaS POSTs when a user clicks a validation-package badge.
/// Documented in docs/hook-contract.md; the ARC specification defines the concept of a
/// (CQC) Hook Endpoint but not its parameters, so this shape is ours.
/// </summary>
public class HookRequest
{
    /// <summary>Issued-at, unix seconds. Checked for freshness to bound replay.</summary>
    [JsonPropertyName("iat")]
    public long IssuedAt { get; set; }

    [JsonPropertyName("package_endpoint")]
    public string? PackageEndpoint { get; set; }

    /// <summary>
    /// Package-author-defined and opaque to AaaS. This is the configuration channel for
    /// endpoint-specific options; see <see cref="EnaPackagePayload"/>.
    /// </summary>
    [JsonPropertyName("package_payload")]
    public JsonElement PackagePayload { get; set; }

    [JsonPropertyName("project_id")]
    public int ProjectId { get; set; }

    /// <summary>GitLab path_with_namespace.</summary>
    [JsonPropertyName("project_name")]
    public string? ProjectName { get; set; }

    /// <summary>
    /// Short-lived. Every use of this token must happen inside the hook request — by the
    /// time the user opens follow_me it may already be dead. Never persisted.
    /// </summary>
    [JsonPropertyName("gitlab_token")]
    public string? GitLabToken { get; set; }

    /// <summary>
    /// Caller-supplied URL that this service will fetch. Checked against a host allowlist
    /// before any request is made — otherwise this is an SSRF primitive holding a token.
    /// </summary>
    [JsonPropertyName("ro_crate_url")]
    public string? RoCrateUrl { get; set; }
}

/// <summary>Response to the hook: where to send the user's browser.</summary>
public class HookResponse
{
    [JsonPropertyName("follow_me")]
    public string FollowMe { get; set; } = string.Empty;

    public HookResponse() { }
    public HookResponse(string followMe) => FollowMe = followMe;
}
