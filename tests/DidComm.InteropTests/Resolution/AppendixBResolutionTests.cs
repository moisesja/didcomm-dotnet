using DidComm.Resolution;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests.Resolution;

/// <summary>
/// End-to-end resolution tests against the vendored DIDComm v2.1 Appendix B DID Documents.
/// Goes through <see cref="NetDidKeyService"/> against a <see cref="FixtureDidResolver"/> so
/// the JWK materialisation path (fragment dereferencing, JsonWebKey2020 handling, curve
/// filtering) is exercised on real spec data.
/// </summary>
public sealed class AppendixBResolutionTests
{
    private static readonly Lazy<FixtureDidResolver> Resolver = new(() =>
        FixtureDidResolver.LoadFromDirectory(Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec")));

    private static NetDidKeyService NewKeyService() => new(Resolver.Value);

    [Fact]
    public async Task Alice_AuthenticationKeys_ReturnsThreeSigningKeys()
    {
        var keys = await NewKeyService().GetVerificationMethodsAsync(
            "did:example:alice", VerificationRelationship.Authentication);

        keys.Select(k => k.Kid).Should().BeEquivalentTo(new[]
        {
            "did:example:alice#key-1",
            "did:example:alice#key-2",
            "did:example:alice#key-3",
        });

        keys.Single(k => k.Kid == "did:example:alice#key-1").Crv.Should().Be("Ed25519");
        keys.Single(k => k.Kid == "did:example:alice#key-2").Crv.Should().Be("P-256");
        keys.Single(k => k.Kid == "did:example:alice#key-3").Crv.Should().Be("secp256k1");
    }

    [Fact]
    public async Task Alice_KeyAgreementKeys_ReturnsX25519P256P521()
    {
        var keys = await NewKeyService().GetVerificationMethodsAsync(
            "did:example:alice", VerificationRelationship.KeyAgreement);

        keys.Select(k => k.Kid).Should().Contain(new[]
        {
            "did:example:alice#key-x25519-1",
            "did:example:alice#key-p256-1",
            "did:example:alice#key-p521-1",
        });
        keys.Should().OnlyContain(k => k.Crv == "X25519" || k.Crv == "P-256" || k.Crv == "P-521");
    }

    [Fact]
    public async Task Bob_KeyAgreementKeys_AllCurvesPresent()
    {
        var keys = await NewKeyService().GetVerificationMethodsAsync(
            "did:example:bob", VerificationRelationship.KeyAgreement);

        keys.Should().HaveCount(9);
        keys.Select(k => k.Crv).Distinct().Should().BeEquivalentTo(new[] { "X25519", "P-256", "P-384", "P-521" });
    }

    [Fact]
    public async Task Bob_HasNoAuthenticationKeys()
    {
        var keys = await NewKeyService().GetVerificationMethodsAsync(
            "did:example:bob", VerificationRelationship.Authentication);

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task IsKeyAuthorized_FollowsRelationshipBoundary()
    {
        var sut = NewKeyService();

        // Alice's key-1 is an authentication key, not a key-agreement key.
        (await sut.IsKeyAuthorizedAsync("did:example:alice", "did:example:alice#key-1", VerificationRelationship.Authentication))
            .Should().BeTrue();
        (await sut.IsKeyAuthorizedAsync("did:example:alice", "did:example:alice#key-1", VerificationRelationship.KeyAgreement))
            .Should().BeFalse();
    }
}
