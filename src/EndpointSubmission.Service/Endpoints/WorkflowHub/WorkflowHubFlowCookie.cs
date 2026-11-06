using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>One item the user ticked on the preview page, with the visibility chosen for it.</summary>
public sealed record WorkflowHubSelectedItem(Guid ArtifactId, bool MakePublic);

/// <summary>
/// What the service remembers between sending the user to WorkflowHub and receiving them back.
/// It holds no WorkflowHub token and no PAT.
/// </summary>
/// <param name="StatusToken">
/// The secret of the preview page. The callback needs it to send the user back to that page. It is
/// neither a WorkflowHub nor a DataHUB credential, and it only lives inside this encrypted cookie
/// for as long as the login takes.
/// </param>
/// <param name="TeamId">The team the user chose before this round, or null when the account has one team or none yet.</param>
/// <param name="State">The OAuth state value the callback must return unchanged.</param>
/// <param name="CodeVerifier">The PKCE verifier that belongs to the challenge sent to WorkflowHub.</param>
public sealed record WorkflowHubPendingFlow(
    Guid SubmissionId,
    string StatusToken,
    IReadOnlyList<WorkflowHubSelectedItem> Items,
    int? TeamId,
    string State,
    string CodeVerifier,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Keeps a <see cref="WorkflowHubPendingFlow"/> in an HttpOnly cookie protected with Data Protection.
/// The key ring lives in memory (see Program.cs), so a restart invalidates pending logins and no
/// key is written to disk.
/// </summary>
public sealed class WorkflowHubFlowCookie
{
    public const string CookieName = "wfh_oauth";
    public const string Purpose = "EndpointSubmission.WorkflowHub.OAuth.v1";

    /// <summary>The browser sends the cookie to the WorkflowHub routes only.</summary>
    public const string CookiePath = "/workflowhub";

    private readonly IDataProtector _protector;
    private readonly WorkflowHubOptions _options;

    public WorkflowHubFlowCookie(IDataProtectionProvider provider, IOptions<WorkflowHubOptions> options)
    {
        _protector = provider.CreateProtector(Purpose);
        _options = options.Value;
    }

    public DateTimeOffset NewExpiry() => DateTimeOffset.UtcNow.AddMinutes(_options.OAuthFlowMinutes);

    public void Write(HttpContext http, WorkflowHubPendingFlow flow)
    {
        var options = Options(http.Request);
        options.MaxAge = TimeSpan.FromMinutes(_options.OAuthFlowMinutes);
        http.Response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(flow)), options);
    }

    /// <summary>The pending flow, or null when the cookie is missing, damaged, not ours or expired.</summary>
    public WorkflowHubPendingFlow? Read(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var raw) || string.IsNullOrEmpty(raw))
            return null;

        try
        {
            var flow = JsonSerializer.Deserialize<WorkflowHubPendingFlow>(_protector.Unprotect(raw));
            return flow is not null && flow.ExpiresAt > DateTimeOffset.UtcNow ? flow : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Expires the cookie. The path must match the one it was written with.</summary>
    public void Delete(HttpContext http)
        => http.Response.Cookies.Delete(CookieName, Options(http.Request));

    private static CookieOptions Options(HttpRequest request) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = CookiePath,
        Secure = request.IsHttps
    };
}
