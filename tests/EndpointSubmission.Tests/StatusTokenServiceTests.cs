using EndpointSubmission.Service.Services;

namespace EndpointSubmission.Tests;

public class StatusTokenServiceTests
{
    private readonly StatusTokenService _tokens = new();

    [Fact]
    public void Minted_tokens_are_unique()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
            Assert.True(seen.Add(_tokens.Mint().Token));
    }

    [Fact]
    public void Minted_token_is_url_safe_and_long_enough_to_be_unguessable()
    {
        var token = _tokens.Mint().Token;

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.True(token.Length >= 42, $"token was only {token.Length} chars");
    }

    [Fact]
    public void Hash_is_stable_and_does_not_reveal_the_token()
    {
        var minted = _tokens.Mint();

        Assert.Equal(minted.Hash, _tokens.Hash(minted.Token));
        Assert.DoesNotContain(minted.Token, minted.Hash);
        Assert.Equal(64, minted.Hash.Length); // SHA-256, hex
    }

    [Theory]
    [InlineData("user@example.org", "user@example.org")]
    [InlineData("User@Example.ORG", "user@example.org")]
    [InlineData("  user@example.org  ", "user@example.org")]
    public void Email_gate_accepts_equivalent_addresses(string expected, string provided)
        => Assert.True(StatusTokenService.EmailMatches(expected, provided));

    [Theory]
    [InlineData("user@example.org", "other@example.org")]
    [InlineData("user@example.org", "")]
    [InlineData("user@example.org", null)]
    [InlineData("", "user@example.org")]
    [InlineData(null, null)]
    public void Email_gate_rejects_everything_else(string? expected, string? provided)
        => Assert.False(StatusTokenService.EmailMatches(expected, provided));
}
