using DidComm.Consistency;
using DidComm.Exceptions;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Consistency;

public sealed class AddressingConsistencyTests
{
    [Theory]
    [InlineData("did:example:alice", "did:example:alice#key-1")]
    [InlineData("did:example:alice", "did:example:alice?foo=bar#key-1")]
    [InlineData("did:example:alice?foo=bar", "did:example:alice#key-1")]
    [InlineData("did:example:alice/path", "did:example:alice#key-2")]
    public void Authcrypt_from_matches_skid_via_did_subject(string from, string skid)
    {
        // Must not throw.
        AddressingConsistency.CheckAuthcryptFromMatchesSkid(from, skid);
    }

    [Fact]
    public void Authcrypt_from_mismatched_skid_throws()
    {
        Action act = () => AddressingConsistency.CheckAuthcryptFromMatchesSkid(
            "did:example:alice", "did:example:carol#key-1");

        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-01*");
    }

    [Fact]
    public void Authcrypt_with_null_from_short_circuits()
    {
        // Anoncrypt-style: no 'from'. Check is a no-op.
        AddressingConsistency.CheckAuthcryptFromMatchesSkid(from: null, "did:example:alice#k");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Authenticated_decrypt_without_surfaced_skid_fails_closed(string? skid)
    {
        // Issue #52 — if the JOSE layer's IsAuthenticated ⟺ non-empty-skid invariant ever
        // regressed, the guard must reject rather than let the from↔skid binding no-op.
        Action act = () => AddressingConsistency.CheckAuthcryptSkidSurfaced(skid);
        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-01*");
    }

    [Fact]
    public void Authenticated_decrypt_with_surfaced_skid_passes()
    {
        AddressingConsistency.CheckAuthcryptSkidSurfaced("did:example:alice#key-x25519-1");
    }

    [Fact]
    public void Recipient_kid_membership_succeeds_when_subject_matches_any_to_entry()
    {
        var to = new[] { "did:example:alice", "did:example:bob?service=agent" };
        AddressingConsistency.CheckRecipientKidInTo(to, "did:example:bob#key-x25519-1");
    }

    [Fact]
    public void Recipient_kid_not_in_to_throws()
    {
        var to = new[] { "did:example:alice", "did:example:bob" };
        Action act = () => AddressingConsistency.CheckRecipientKidInTo(to, "did:example:carol#k");
        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-02*");
    }

    [Fact]
    public void Recipient_kid_null_or_unparseable_throws()
    {
        var to = new[] { "did:example:alice" };
        Action act = () => AddressingConsistency.CheckRecipientKidInTo(to, "not-a-did#k");
        act.Should().Throw<ConsistencyException>();
    }

    [Fact]
    public void Signed_from_matches_signer_kid()
    {
        AddressingConsistency.CheckSignedFromMatchesSignerKid(
            "did:example:alice", "did:example:alice#key-2");
    }

    [Fact]
    public void Signed_from_mismatched_signer_kid_throws()
    {
        Action act = () => AddressingConsistency.CheckSignedFromMatchesSignerKid(
            "did:example:alice", "did:example:mallory#key-2");
        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-03*");
    }

    [Fact]
    public void Is_recipient_in_to_returns_true_on_match_and_false_on_absence()
    {
        AddressingConsistency
            .IsRecipientInTo(new[] { "did:example:alice", "did:example:bob" }, "did:example:bob")
            .Should().BeTrue();

        AddressingConsistency
            .IsRecipientInTo(new[] { "did:example:alice" }, "did:example:bob")
            .Should().BeFalse();
    }

    [Fact]
    public void Authcrypt_inner_signer_mismatch_throws()
    {
        Action act = () => AddressingConsistency.CheckAuthcryptInnerSignerMatchesSkid(
            "did:example:bob#sig", "did:example:alice#enc");
        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-05*");
    }

    [Fact]
    public void Authcrypt_inner_signer_match_passes()
    {
        AddressingConsistency.CheckAuthcryptInnerSignerMatchesSkid(
            "did:example:alice#sig", "did:example:alice#enc");
    }

    [Fact]
    public async Task Resolver_authorization_with_null_resolver_short_circuits()
    {
        await AddressingConsistency.CheckResolverAuthorizationAsync(
            "did:example:alice", "did:example:alice#k", "keyAgreement", resolverCheck: null);
    }

    [Fact]
    public async Task Resolver_authorization_throws_when_resolver_says_no()
    {
        Func<Task> act = () => AddressingConsistency.CheckResolverAuthorizationAsync(
            "did:example:alice", "did:example:alice#k", "keyAgreement",
            resolverCheck: (_, _, _, _) => Task.FromResult(false));
        (await act.Should().ThrowAsync<ConsistencyException>()).WithMessage("*FR-CONSIST-06*");
    }
}
