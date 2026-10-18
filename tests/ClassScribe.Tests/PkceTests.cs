using System.Text.RegularExpressions;
using ClassScribe.Drive;

namespace ClassScribe.Tests;

/// <summary>
/// PKCE as Google and RFC 7636 describe it: a verifier of 43 to 128 characters from the unreserved alphabet, new for every
/// sign-in; its challenge, the base64url SHA-256 of the verifier without padding; and the state, compared whole.
/// </summary>
[TestFixture]
public class PkceTests
{
    /// <summary>The characters RFC 7636 allows in a verifier: letters, digits, and - . _ ~.</summary>
    private static readonly Regex Unreserved = new("^[A-Za-z0-9._~-]+$");

    /// <summary>A verifier is 43 characters of the unreserved alphabet, from 32 random bytes, and new each time.</summary>
    [Test]
    public void A_verifier_is_43_unreserved_characters_and_new_each_time()
    {
        var verifiers = Enumerable.Range(0, 50).Select(_ => Pkce.NewVerifier()).ToList();

        Assert.That(verifiers, Has.All.Length.EqualTo(43));
        Assert.That(verifiers, Has.All.Matches<string>(verifier => Unreserved.IsMatch(verifier)));
        Assert.That(verifiers, Is.Unique);
    }

    /// <summary>The challenge of the verifier in RFC 7636's appendix B is the value the RFC gives, as the probe on this laptop found.</summary>
    [Test]
    public void The_challenge_of_the_RFC_7636_vector_is_its_published_value()
    {
        Assert.That(Pkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"), Is.EqualTo("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
    }

    /// <summary>A challenge is 43 characters of base64url, without padding.</summary>
    [Test]
    public void A_challenge_is_base64url_without_padding()
    {
        var challenge = Pkce.ChallengeFor(Pkce.NewVerifier());

        Assert.That(challenge, Has.Length.EqualTo(43));
        Assert.That(challenge, Does.Not.Contain("=").And.Not.Contain("+").And.Not.Contain("/"));
    }

    /// <summary>The state is 43 URL-safe characters from 32 random bytes, and new each time.</summary>
    [Test]
    public void The_state_is_url_safe_and_new_each_time()
    {
        var states = Enumerable.Range(0, 50).Select(_ => Pkce.NewState()).ToList();

        Assert.That(states, Has.All.Length.EqualTo(43).And.All.Matches<string>(state => Unreserved.IsMatch(state)));
        Assert.That(states, Is.Unique);
    }

    /// <summary>A state matches only when it is the very same: one character off, a part of it, more than it, or none at all does not.</summary>
    [Test]
    public void A_state_matches_only_when_it_is_the_same()
    {
        var state = Pkce.NewState();

        Assert.That(Pkce.SameState(state, state), Is.True);
        Assert.That(Pkce.SameState(new string([.. state]), state), Is.True, "compared by value");
        Assert.That(Pkce.SameState(state[..^1] + (state[^1] == 'A' ? 'B' : 'A'), state), Is.False);
        Assert.That(Pkce.SameState(state[..20], state), Is.False);
        Assert.That(Pkce.SameState(state + "x", state), Is.False);
        Assert.That(Pkce.SameState("", state), Is.False);
        Assert.That(Pkce.SameState(null, state), Is.False);
    }
}
