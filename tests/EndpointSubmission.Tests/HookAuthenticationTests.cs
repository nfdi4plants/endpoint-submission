using EndpointSubmission.Service.Authentication;
using EndpointSubmission.Service.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace EndpointSubmission.Tests;

public class HookAuthenticationTests
{
    [Fact]
    public void DevelopmentAuthenticator_AllowsRequestWithoutSecret()
    {
        IHookAuthenticator authenticator = new DevelopmentHookAuthenticator();

        Assert.True(authenticator.IsConfigured);
        Assert.True(authenticator.IsAuthentic(new DefaultHttpContext().Request));
    }

    [Fact]
    public void SharedSecretAuthenticator_AllowsMatchingSecret()
    {
        var authenticator = SharedSecretAuthenticator("expected-secret");
        var request = new DefaultHttpContext().Request;
        request.Headers[SharedSecretHookAuthenticator.HeaderName] = "expected-secret";

        Assert.True(authenticator.IsConfigured);
        Assert.True(authenticator.IsAuthentic(request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-secret")]
    public void SharedSecretAuthenticator_RejectsMissingOrIncorrectSecret(string? provided)
    {
        var authenticator = SharedSecretAuthenticator("expected-secret");
        var request = new DefaultHttpContext().Request;
        if (provided is not null)
        {
            request.Headers[SharedSecretHookAuthenticator.HeaderName] = provided;
        }

        Assert.False(authenticator.IsAuthentic(request));
    }

    [Fact]
    public void SharedSecretAuthenticator_IsNotConfiguredWithoutSecret()
    {
        var authenticator = SharedSecretAuthenticator(null);

        Assert.False(authenticator.IsConfigured);
        Assert.False(authenticator.IsAuthentic(new DefaultHttpContext().Request));
    }

    private static SharedSecretHookAuthenticator SharedSecretAuthenticator(string? secret) =>
        new(Options.Create(new EndpointSubmissionOptions { HookSharedSecret = secret }));
}
