using EndpointSubmission.Service.Services;
using Microsoft.Extensions.Options;

namespace EndpointSubmission.Service.Authentication;

public interface IHookAuthenticator
{
    bool IsConfigured { get; }

    bool IsAuthentic(HttpRequest request);
}

/// <summary>
/// Authenticates the caller of a hook endpoint via a shared secret header.
///
/// Note what this control is and is not. The load-bearing authenticator is the
/// <c>gitlab_token</c> in the payload: no submission can be created without one the DataHUB
/// accepts. This header is defence in depth on top of that — it keeps unauthenticated
/// callers from consuming resources, probing the host allowlist, or creating records at all.
///
/// Outside Development, the service refuses to serve the hook when no secret is configured
/// rather than falling open, because this endpoint leads to minting permanent public identifiers.
///
/// Caveat worth remembering: the <c>iat</c> freshness check in SubmissionIntakeService only
/// deters replay to the extent the envelope is authenticated. If AaaS turns out to sign its
/// payloads (the <c>iat</c> claim hints at a JWT lineage), verifying that signature here
/// would strengthen both controls at once. Replacing the body of <see cref="IsAuthentic"/>
/// is sufficient; the call site does not change.
/// </summary>
public sealed class SharedSecretHookAuthenticator(IOptions<EndpointSubmissionOptions> options)
    : IHookAuthenticator
{
    public const string HeaderName = "X-Hook-Secret";

    private readonly EndpointSubmissionOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.HookSharedSecret);

    public bool IsAuthentic(HttpRequest request)
    {
        if (!IsConfigured) return false;

        if (!request.Headers.TryGetValue(HeaderName, out var provided)) return false;

        var expected = System.Text.Encoding.UTF8.GetBytes(_options.HookSharedSecret!);
        var actual = System.Text.Encoding.UTF8.GetBytes(provided.ToString());

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Security.Cryptography.SHA256.HashData(expected),
            System.Security.Cryptography.SHA256.HashData(actual));
    }
}

/// <summary>
/// Allows local development hooks without coordinating a shared secret. This implementation
/// must only be registered when the ASP.NET Core environment is Development.
/// </summary>
public sealed class DevelopmentHookAuthenticator : IHookAuthenticator
{
    public bool IsConfigured => true;

    public bool IsAuthentic(HttpRequest request) => true;
}
