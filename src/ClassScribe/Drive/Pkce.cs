using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ClassScribe.Drive;

/// <summary>
/// The random values of one Google sign-in: the PKCE verifier and its S256 challenge (RFC 7636; Google's guide for desktop
/// apps: "a high-entropy cryptographic random string" of 43 to 128 characters, and "the Base64URL (with no padding) encoded
/// SHA256 hash of the code verifier"), and the state that ties Google's answer to this sign-in. None of them is ever logged.
/// </summary>
public static class Pkce
{
    /// <summary>How many random bytes make a verifier or a state: 32, which base64url writes as 43 characters.</summary>
    private const int RandomBytes = 32;

    /// <summary>A new verifier: 32 random bytes in base64url, 43 characters of the unreserved alphabet RFC 7636 allows.</summary>
    public static string NewVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));

    /// <summary>A new state: 32 random bytes in base64url.</summary>
    public static string NewState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));

    /// <summary>The S256 challenge of <paramref name="verifier"/>: the base64url SHA-256 of its ASCII bytes, without padding.</summary>
    public static string ChallengeFor(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// True when <paramref name="given"/>, the state a request to the listener carries, is <paramref name="expected"/>, the
    /// sign-in's own, compared in constant time for states of the same length.
    /// </summary>
    public static bool SameState(string? given, string expected) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
}
