using DidComm.Consistency;
using DidComm.Tests.Facade;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Consistency;

/// <summary>
/// Pins the backstop bound in the <c>DidSubjectOf</c> comparison primitive (FR-API-09, #80). The
/// receive path refuses over-long wire identifiers first, but this primitive also parses values from
/// resolved DID documents, so it declines to hand NetDid anything longer than the key-id bound and
/// reports it as "not a DID". At exactly the bound it parses normally, so nothing the wire checks
/// accept is refused here.
/// </summary>
public sealed class DidSubjectLengthBoundTests
{
    private const string Alice = "did:example:alice";

    [Fact]
    public void DidSubjectOf_parses_a_DID_URL_of_exactly_the_key_id_cap()
    {
        DidSubject.DidSubjectOf(LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap)).Should().Be(Alice);
    }

    [Fact]
    public void DidSubjectOf_parses_a_bare_DID_of_exactly_the_key_id_cap()
    {
        var did = LengthCap.DidOfLength(LengthCap.KeyIdCap);

        DidSubject.DidSubjectOf(did).Should().Be(did);
    }

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(1_000_000)]
    public void DidSubjectOf_treats_a_longer_DID_URL_as_not_a_DID(int length)
    {
        var kid = LengthCap.KidOfLength(Alice, length);

        DidSubject.DidSubjectOf(kid).Should().BeNull();
        DidSubject.SameDidSubject(kid, Alice).Should().BeFalse("an over-long value can never match a DID subject");
        DidSubject.SameDidSubject(Alice, kid).Should().BeFalse();
    }

    [Fact]
    public void DidSubjectOf_treats_a_longer_bare_DID_as_not_a_DID()
    {
        DidSubject.DidSubjectOf(LengthCap.DidOfLength(LengthCap.KeyIdCap + 1)).Should().BeNull();
    }
}
