using System.Security.Cryptography;

namespace EndpointSubmission.Service.Services;

/// <summary>
/// Mints and verifies the token in a submission's status link.
///
/// The token is shown once, inside follow_me, and only its SHA-256 is stored — a database
/// leak must not hand out access to submission records. Hashing is a bare SHA-256 rather
/// than a password KDF on purpose: the input is 256 bits of CSPRNG output, so there is no
/// guessable plaintext for a slow hash to protect.
/// </summary>
public class StatusTokenService
{
    public record MintedToken(string Token, string Hash);

    public MintedToken Mint()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Base64UrlEncode(bytes);
        return new MintedToken(token, Hash(token));
    }

    public string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public bool Verify(string? token, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expectedHash))
            return false;

        var actual = System.Text.Encoding.ASCII.GetBytes(Hash(token));
        var expected = System.Text.Encoding.ASCII.GetBytes(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Compares a submitted email against the recorded one. Case- and whitespace-insensitive,
    /// because users retype these, but fixed-time so the gate cannot be used as an oracle for
    /// who submitted a given ARC.
    /// </summary>
    public static bool EmailMatches(string? expected, string? provided)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(provided))
            return false;

        // Compare digests, not the strings themselves: FixedTimeEquals short-circuits on
        // differing lengths, which would leak the length of the recorded address.
        var a = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected.Trim().ToLowerInvariant()));
        var b = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provided.Trim().ToLowerInvariant()));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
