using DidComm.Consistency;
using DidComm.Exceptions;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Consistency;

/// <summary>
/// Direct unit coverage for <see cref="AddressingConsistency.CheckResolverAuthorization"/>
/// (FR-CONSIST-06). The full pipeline integration through <c>EnvelopeReader.Unpack</c> is
/// covered by the interop round-trip tests, which exercise every legal envelope shape with a
/// real resolver-backed predicate.
/// </summary>
public sealed class ResolverAuthorizationTests
{
    [Fact]
    public void Predicate_Authorized_DoesNotThrow()
    {
        Action act = () => AddressingConsistency.CheckResolverAuthorization(
            "did:example:alice",
            "did:example:alice#key-1",
            "authentication",
            (_, _, _) => true);

        act.Should().NotThrow();
    }

    [Fact]
    public void Predicate_Unauthorized_ThrowsConsistency()
    {
        Action act = () => AddressingConsistency.CheckResolverAuthorization(
            "did:example:alice",
            "did:example:alice#stolen",
            "authentication",
            (_, _, _) => false);

        act.Should().Throw<ConsistencyException>()
            .Where(e => e.Message.Contains("FR-CONSIST-06"));
    }

    [Fact]
    public void Predicate_Null_ShortCircuitsToAuthorized()
    {
        Action act = () => AddressingConsistency.CheckResolverAuthorization(
            "did:example:alice",
            "did:example:alice#any",
            "authentication",
            null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Predicate_PassesAssertedTriple_Exactly()
    {
        (string did, string kid, string rel)? observed = null;

        AddressingConsistency.CheckResolverAuthorization(
            "did:example:alice",
            "did:example:alice#key-2",
            "keyAgreement",
            (d, k, r) =>
            {
                observed = (d, k, r);
                return true;
            });

        observed.Should().Be(("did:example:alice", "did:example:alice#key-2", "keyAgreement"));
    }
}
