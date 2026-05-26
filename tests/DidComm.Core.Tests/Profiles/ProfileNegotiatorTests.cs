using DidComm.Profiles;
using FluentAssertions;
using Xunit;

// Alias dodges the shadowing of the `DidComm.Profiles.Profiles` static class by the test
// namespace `DidComm.Tests.Profiles` (the resolver prefers the local namespace first).
using ProfilesConst = DidComm.Profiles.Profiles;

namespace DidComm.Tests.Profiles;

public sealed class ProfileNegotiatorTests
{
    [Fact]
    public void Returns_v2_when_advertised()
    {
        ProfileNegotiator.Choose(new[] { ProfilesConst.DidCommV2 }).Should().Be(ProfilesConst.DidCommV2);
    }

    [Fact]
    public void Returns_v2_when_v2_appears_among_multiple()
    {
        ProfileNegotiator.Choose(new[] { "didcomm/aip1", "didcomm/v2", "didcomm/v3" })
            .Should().Be(ProfilesConst.DidCommV2);
    }

    [Fact]
    public void Case_and_whitespace_are_ignored()
    {
        ProfileNegotiator.Choose(new[] { "  DIDComm/V2  " }).Should().Be(ProfilesConst.DidCommV2);
    }

    [Fact]
    public void Returns_null_when_no_overlap()
    {
        // FR-PROF-02: caller MAY emit a problem-report; negotiator just signals "no match".
        ProfileNegotiator.Choose(new[] { "didcomm/aip1", "didcomm/v3" }).Should().BeNull();
    }

    [Fact]
    public void Null_accept_implies_v2_is_acceptable()
    {
        // Absent `accept` ⇒ peer makes no claim; pick our default.
        ProfileNegotiator.Choose(null).Should().Be(ProfilesConst.DidCommV2);
    }

    [Fact]
    public void Empty_accept_returns_null()
    {
        ProfileNegotiator.Choose(Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void Empty_entries_in_accept_are_skipped()
    {
        ProfileNegotiator.Choose(new[] { string.Empty, "didcomm/v2" }).Should().Be(ProfilesConst.DidCommV2);
    }

    [Fact]
    public void IsSupported_recognises_v2()
    {
        ProfileNegotiator.IsSupported("didcomm/v2").Should().BeTrue();
        ProfileNegotiator.IsSupported("DIDCOMM/V2").Should().BeTrue();
    }

    [Fact]
    public void IsSupported_returns_false_for_unsupported_profile()
    {
        ProfileNegotiator.IsSupported("didcomm/aip1").Should().BeFalse();
        ProfileNegotiator.IsSupported(string.Empty).Should().BeFalse();
    }
}
