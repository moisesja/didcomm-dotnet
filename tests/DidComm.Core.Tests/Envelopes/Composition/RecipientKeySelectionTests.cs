using DidComm.Composition;
using DidComm.Exceptions;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Envelopes.Composition;

/// <summary>
/// Unit tests for <see cref="RecipientKeySelection"/>, the recipient-key selection shared by the
/// facade, the mediator-endpoint expander and the mediator rewrap (FR-ENC-19). The facade-level
/// behaviour, with real envelopes, is in <c>Facade/AllRecipientKeysTests</c>.
/// </summary>
public sealed class RecipientKeySelectionTests
{
    // ---- Addressable ----

    private const string A = "did:example:a";

    [Fact]
    public void Addressable_keeps_document_order_and_drops_keys_without_a_kid()
    {
        var keys = new[] { Key(A + "#1"), Key(null), Key(A + "#2"), Key(""), Key(A + "#3", "P-256") };

        RecipientKeySelection.Addressable(A, keys).Select(k => k.Kid).Should().Equal(A + "#1", A + "#2", A + "#3");
    }

    [Fact]
    public void Addressable_dedupes_by_kid_ordinally_and_the_first_wins()
    {
        var first = Key(A + "#1");
        var duplicate = Key(A + "#1", "P-256");
        var differentCase = Key(A + "#K");
        var keys = new[] { first, Key(A + "#k"), duplicate, differentCase };

        var addressable = RecipientKeySelection.Addressable(A, keys);

        addressable.Should().Equal(first, keys[1], differentCase);
    }

    [Fact]
    public void Addressable_keeps_keys_on_unsupported_curves_for_negotiation_to_ignore()
    {
        RecipientKeySelection.Addressable(A, new[] { Key(A + "#1", "secp256k1") }).Should().ContainSingle();
    }

    [Theory]
    [InlineData("did:example:b#1")]       // another DID
    [InlineData("did:example:ab#1")]      // a DID that merely starts with this one
    [InlineData("did:example:#1")]        // a prefix of this DID
    [InlineData("did:example:A#1")]       // DIDs compare ordinally
    [InlineData("#1")]                    // relative: names no DID at all
    [InlineData("a#1")]                   // not a DID
    public void Addressable_drops_a_key_whose_kid_names_another_DID(string foreignKid)
    {
        var own = Key(A + "#2");

        RecipientKeySelection.Addressable(A, new[] { Key(foreignKid), own }).Should().Equal(own);
    }

    [Theory]
    [InlineData(A + "#1")]
    [InlineData(A + "?versionId=1#1")]
    [InlineData(A + "/path#1")]
    [InlineData(A + ";p=1#1")]
    public void Addressable_keeps_a_kid_whose_DID_part_is_exactly_the_DID(string kid)
    {
        RecipientKeySelection.Addressable(A, new[] { Key(kid) }).Should().ContainSingle();
    }

    // ---- PickCommonCurve ----

    [Theory]
    [InlineData(new[] { "P-521", "P-384", "P-256", "X25519" }, "X25519")]
    [InlineData(new[] { "P-521", "P-384", "P-256" }, "P-256")]
    [InlineData(new[] { "P-521", "P-384" }, "P-384")]
    [InlineData(new[] { "P-521" }, "P-521")]
    [InlineData(new[] { "secp256k1", "Ed25519" }, null)]
    public void PickCommonCurve_prefers_X25519_then_the_NIST_curves_by_size(string[] curves, string? expected)
    {
        var set = curves.Select((crv, i) => Key($"a#{i}", crv)).ToArray();

        RecipientKeySelection.PickCommonCurve(new[] { set }, Array.Empty<Jwk>(), requireSender: false).Should().Be(expected);
    }

    [Fact]
    public void PickCommonCurve_needs_the_curve_in_every_recipient_set()
    {
        var bob = new[] { Key("b#x"), Key("b#p", "P-256") };
        var carol = new[] { Key("c#p", "P-256") };

        RecipientKeySelection.PickCommonCurve(new[] { bob, carol }, Array.Empty<Jwk>(), requireSender: false).Should().Be("P-256");
    }

    [Fact]
    public void PickCommonCurve_honours_the_sender_only_when_required()
    {
        var bob = new[] { Key("b#x"), Key("b#p", "P-256") };
        var sender = new[] { Key("a#p", "P-256") };

        RecipientKeySelection.PickCommonCurve(new[] { bob }, sender, requireSender: true).Should().Be("P-256");
        RecipientKeySelection.PickCommonCurve(new[] { bob }, sender, requireSender: false).Should().Be("X25519");
        RecipientKeySelection.PickCommonCurve(new[] { bob }, Array.Empty<Jwk>(), requireSender: true).Should().BeNull();
    }

    [Fact]
    public void PickCommonCurve_returns_null_for_an_empty_recipient_set()
    {
        RecipientKeySelection.PickCommonCurve(new[] { new[] { Key("b#x") }, Array.Empty<Jwk>() }, Array.Empty<Jwk>(), requireSender: false)
            .Should().BeNull();
    }

    // ---- OnCurve ----

    [Fact]
    public void OnCurve_takes_the_keys_on_the_curve_in_order()
    {
        var keys = new[] { Key("b#p1", "P-256"), Key("b#x1"), Key("b#p2", "P-256"), Key("b#x2") };

        var onCurve = RecipientKeySelection.OnCurve(keys, "P-256", out var overCap);

        onCurve.Select(k => k.Kid).Should().Equal("b#p1", "b#p2");
        overCap.Should().Be(0);
    }

    [Fact]
    public void OnCurve_caps_at_MaxKeysPerDid_and_counts_the_rest()
    {
        var keys = Enumerable.Range(0, RecipientKeySelection.MaxKeysPerDid + 3)
            .Select(i => Key($"b#{i}"))
            .Append(Key("b#p", "P-256"))
            .ToArray();

        var onCurve = RecipientKeySelection.OnCurve(keys, "X25519", out var overCap);

        RecipientKeySelection.MaxKeysPerDid.Should().Be(64);
        onCurve.Should().Equal(keys.Take(64));
        overCap.Should().Be(3, "keys on other curves are not counted as over the cap");
    }

    // ---- AllOnPreferredCurve ----

    private const string M = "did:example:m";

    [Fact]
    public void AllOnPreferredCurve_takes_every_addressable_key_on_the_best_curve()
    {
        var keys = new[] { Key(M + "#p", "P-256"), Key(null), Key(M + "#x1"), Key(M + "#x1"), Key("did:example:other#x"), Key(M + "#x2") };

        RecipientKeySelection.AllOnPreferredCurve(M, keys, "mediator").Select(k => k.Kid)
            .Should().Equal(M + "#x1", M + "#x2");
    }

    [Fact]
    public void AllOnPreferredCurve_refuses_a_DID_with_no_kid_and_one_with_no_supported_curve()
    {
        var noKid = () => RecipientKeySelection.AllOnPreferredCurve(M, new[] { Key(null), Key("") }, "mediator");
        var noCurve = () => RecipientKeySelection.AllOnPreferredCurve(M, new[] { Key(M + "#k", "secp256k1") }, "mediator");

        noKid.Should().Throw<DidResolutionException>().Which.Message.Should().Contain("mediator has no keyAgreement key with a key id of this DID");
        noCurve.Should().Throw<DidResolutionException>().Which.Message.Should().Contain("supported curve");
    }

    [Fact]
    public void AllOnPreferredCurve_refuses_a_DID_whose_keys_all_name_another_DID()
    {
        var act = () => RecipientKeySelection.AllOnPreferredCurve(M, new[] { Key("did:example:other#k") }, "mediator");

        act.Should().Throw<DidResolutionException>().Which.Message.Should().Contain("no keyAgreement key with a key id of this DID");
    }

    [Fact]
    public void AllOnPreferredCurve_is_capped()
    {
        var keys = Enumerable.Range(0, 100).Select(i => Key($"{M}#{i}")).ToArray();

        RecipientKeySelection.AllOnPreferredCurve(M, keys, "mediator").Should().HaveCount(RecipientKeySelection.MaxKeysPerDid);
    }

    private static Jwk Key(string? kid, string crv = "X25519") =>
        new() { Kty = crv is "X25519" or "Ed25519" ? "OKP" : "EC", Crv = crv, X = "stub", Kid = kid };
}
