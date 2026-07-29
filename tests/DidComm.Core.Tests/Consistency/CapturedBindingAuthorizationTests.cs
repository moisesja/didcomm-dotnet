using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Resolution;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Consistency;

/// <summary>
/// Direct unit coverage for <see cref="AddressingConsistency.CheckCapturedBindingAuthorized"/> —
/// the same-document form of FR-CONSIST-06 (#56): authorization decided against the binding
/// captured from the resolution that fed the crypto, never a re-resolution.
/// </summary>
public sealed class CapturedBindingAuthorizationTests
{
    private static ResolvedKeyBinding Binding(string kid, string did, string? controller)
        => new(kid, did, controller, VerificationRelationship.KeyAgreement, new Jwk
        {
            Kty = "OKP",
            Crv = "X25519",
            X = "avH0O2Y4tqLAq8y9zpianr8ajii5m4F_mICrzNlatXs",
            Kid = kid,
        });

    [Fact]
    public void SubjectAndControllerMatch_Authorized()
    {
        var binding = Binding("did:example:alice#k", "did:example:alice", "did:example:alice");

        var act = () => AddressingConsistency.CheckCapturedBindingAuthorized("did:example:alice", binding);

        act.Should().NotThrow();
    }

    [Fact]
    public void ControllerOmitted_FallsBackToIdSubjectRule()
    {
        var binding = Binding("did:example:alice#k", "did:example:alice", controller: null);

        var act = () => AddressingConsistency.CheckCapturedBindingAuthorized("did:example:alice", binding);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertedDidMayBeADidUrl_ComparedAsSubjects()
    {
        var binding = Binding("did:example:alice#k", "did:example:alice", "did:example:alice");

        var act = () => AddressingConsistency.CheckCapturedBindingAuthorized("did:example:alice?service=x", binding);

        act.Should().NotThrow();
    }

    [Fact]
    public void CrossDidSubject_Rejected()
    {
        // Embedded VM whose id belongs to eve, listed under alice's relationship: the captured
        // binding's subject is eve, so an asserted 'from' of alice must fail.
        var binding = Binding("did:example:eve#k", "did:example:eve", "did:example:eve");

        var act = () => AddressingConsistency.CheckCapturedBindingAuthorized("did:example:alice", binding);

        act.Should().Throw<ConsistencyException>().WithMessage("*belongs to*");
    }

    [Fact]
    public void CrossDidController_Rejected()
    {
        var binding = Binding("did:example:alice#k", "did:example:alice", "did:example:eve");

        var act = () => AddressingConsistency.CheckCapturedBindingAuthorized("did:example:alice", binding);

        act.Should().Throw<ConsistencyException>().WithMessage("*controlled by*");
    }

    [Fact]
    public void UnparseableAssertedDid_Rejected()
    {
        var binding = Binding("did:example:alice#k", "did:example:alice", null);

        var act = () => AddressingConsistency.CheckCapturedBindingAuthorized("not-a-did", binding);

        act.Should().Throw<ConsistencyException>();
    }
}
