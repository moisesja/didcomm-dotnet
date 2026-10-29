using DidComm.Consistency;
using DidComm.Tests.Facade;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Consistency;

/// <summary>
/// Pins the shared key-id predicate (FR-API-09, #80): a wire key id is at most 4096 characters in
/// all, and at most 2048 in its DID part, meaning everything before its first <c>/</c>, <c>;</c>,
/// <c>?</c> or <c>#</c>. DID syntax allows none of the four inside a DID, so the DID part is exact,
/// and a value with none of them is all DID part.
/// </summary>
public sealed class IdentifierLimitsTests
{
    [Theory]
    [InlineData("#")]
    [InlineData("/")]
    [InlineData(";")]
    [InlineData("?")]
    public void DID_part_of_exactly_the_DID_cap_is_within_the_limits_whichever_delimiter_ends_it(string delimiter)
    {
        IdentifierLimits.IsWithinKeyIdLimits(LengthCap.KidWithDidPart(LengthCap.DidCap, delimiter + "key-1"))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("#")]
    [InlineData("/")]
    [InlineData(";")]
    [InlineData("?")]
    public void DID_part_one_over_the_DID_cap_is_outside_the_limits_whichever_delimiter_ends_it(string delimiter)
    {
        IdentifierLimits.IsWithinKeyIdLimits(LengthCap.KidWithDidPart(LengthCap.DidCap + 1, delimiter + "key-1"))
            .Should().BeFalse();
    }

    [Fact]
    public void Value_with_no_delimiter_is_all_DID_part()
    {
        IdentifierLimits.IsWithinKeyIdLimits(LengthCap.KidWithDidPart(LengthCap.DidCap, suffix: "")).Should().BeTrue();
        IdentifierLimits.IsWithinKeyIdLimits(LengthCap.KidWithDidPart(LengthCap.DidCap + 1, suffix: "")).Should().BeFalse();
        IdentifierLimits.IsWithinKeyIdLimits(new string('x', LengthCap.DidCap + 1))
            .Should().BeFalse("a value that is not a DID at all is still held to the DID bound when nothing delimits it");
    }

    [Fact]
    public void Only_the_first_delimiter_ends_the_DID_part()
    {
        // A short DID, then a fragment that itself contains a long run with no further delimiter.
        var kid = LengthCap.KidOfLength("did:example:alice", LengthCap.KeyIdCap);

        IdentifierLimits.IsWithinKeyIdLimits(kid).Should().BeTrue();
        IdentifierLimits.IsWithinKeyIdLimits("did:example:alice/" + new string('k', LengthCap.DidCap + 10) + "#k")
            .Should().BeTrue("the path is not part of the DID");
    }

    [Fact]
    public void Total_length_is_bounded_at_the_key_id_cap()
    {
        IdentifierLimits.IsWithinKeyIdLimits(LengthCap.KidOfLength("did:example:alice", LengthCap.KeyIdCap)).Should().BeTrue();
        IdentifierLimits.IsWithinKeyIdLimits(LengthCap.KidOfLength("did:example:alice", LengthCap.KeyIdCap + 1)).Should().BeFalse();
    }

    [Fact]
    public void Message_names_the_bound_that_was_broken_and_never_quotes_the_value()
    {
        var tooLong = LengthCap.KidOfLength("did:example:alice", LengthCap.KeyIdCap + 1);
        var longDidPart = LengthCap.KidWithDidPart(LengthCap.DidCap + 1);
        // Both bounds broken: the total-length text wins.
        var both = LengthCap.KidWithDidPart(LengthCap.KeyIdCap + 1);

        IdentifierLimits.KeyIdLimitMessage("JWS signer 'kid'", tooLong)
            .Should().Be("JWS signer 'kid' exceeds the maximum length of 4096 characters (FR-API-09).");
        IdentifierLimits.KeyIdLimitMessage("JWS signer 'kid'", longDidPart)
            .Should().Be("JWS signer 'kid' has a DID part longer than the maximum of 2048 characters (FR-API-09).");
        IdentifierLimits.KeyIdLimitMessage("JWS signer 'kid'", both).Should().Contain("maximum length of 4096");
    }
}
