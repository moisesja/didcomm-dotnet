using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// The FR-RR-03 identity rule (<see cref="ReturnRouteEligibility"/>) and addressing rule
/// (<see cref="ReturnRouteAddressing"/>). Migrated from the InteropTests' same-socket reply gate
/// tests: both identities come from verified envelope key ids, never from plaintext ordering. Reasons
/// are enum values — no DID is ever embedded in a reason (JC-6).
/// </summary>
public sealed class ReturnRouteRulesTests
{
    private static UnpackResult Inbound(
        string? from = "did:example:alice",
        string[]? to = null,
        bool encrypted = true,
        bool authenticated = true,
        string? senderKid = "did:example:alice#key-agreement-1",
        string? signerKid = null,
        string? recipientKid = "did:example:bob#key-agreement-1",
        string type = "https://didcomm.org/test/1.0/m") =>
        new(
            Message: new MessageBuilder()
                .WithType(type)
                .WithFrom(from ?? string.Empty)
                .WithTo(to ?? new[] { "did:example:bob" })
                .Build(),
            Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
            Encrypted: encrypted,
            Authenticated: authenticated,
            NonRepudiation: signerKid is not null,
            AnonymousSender: encrypted && !authenticated,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: signerKid,
            SenderKid: senderKid,
            RecipientKid: recipientKid,
            AllRecipientKids: recipientKid is null ? Array.Empty<string>() : new[] { recipientKid },
            FromPrior: null);

    private static Message Reply(string? from = "did:example:bob", params string[] to) =>
        new MessageBuilder()
            .WithType("https://didcomm.org/test/1.0/reply")
            .WithFrom(from ?? string.Empty)
            .WithTo(to)
            .Build();

    [Fact]
    public void Allows_authcrypt_inbound_and_returns_key_bound_DID_subjects()
    {
        var ok = ReturnRouteEligibility.TryGetPeer(Inbound(), out var peer, out var local, out var reason);

        ok.Should().BeTrue();
        peer.Should().Be("did:example:alice");
        local.Should().Be("did:example:bob");
        reason.Should().Be(ReturnRouteIneligibility.None);
        ReturnRouteAddressing.Check(Reply(to: "did:example:alice"), peer, local).Should().Be(ReturnRouteSendRefusal.None);
    }

    [Theory]
    [InlineData(false, false)] // plaintext
    [InlineData(true, false)]  // anoncrypt
    [InlineData(false, true)]  // signed-only
    public void Rejects_any_inbound_that_is_not_both_encrypted_and_authenticated(bool encrypted, bool authenticated)
    {
        var inbound = Inbound(
            encrypted: encrypted,
            authenticated: authenticated,
            senderKid: authenticated && encrypted ? "did:example:alice#key-1" : null,
            signerKid: authenticated ? "did:example:alice#signing-1" : null,
            recipientKid: encrypted ? "did:example:bob#key-1" : null);

        var ok = ReturnRouteEligibility.TryGetPeer(inbound, out var peer, out var local, out var reason);

        ok.Should().BeFalse();
        peer.Should().BeEmpty();
        local.Should().BeEmpty();
        reason.Should().Be(ReturnRouteIneligibility.NotEncryptedAndAuthenticated);
    }

    [Fact]
    public void Signer_only_binding_without_a_sender_kid_is_not_eligible()
    {
        // Inverted from the pre-#91 gate (L-020): FR-RR-03 fixes the peer to DidSubjectOf(SenderKid)
        // — an authcrypt layer — so a signer-only binding no longer opens a session.
        var inbound = Inbound(senderKid: null, signerKid: "did:example:alice#signing-1");

        var ok = ReturnRouteEligibility.TryGetPeer(inbound, out _, out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Be(ReturnRouteIneligibility.NoSenderKid);
    }

    [Theory]
    [InlineData(null, null, "did:example:bob#key-1", "NoSenderKid")]
    [InlineData("", null, "did:example:bob#key-1", "NoSenderKid")]
    [InlineData("not-a-did", null, "did:example:bob#key-1", "SenderKidNotDid")]
    [InlineData("did:example:alice#key-1", "not-a-did", "did:example:bob#key-1", "SignerKidNotDid")]
    [InlineData("did:example:alice#key-1", null, "not-a-did", "RecipientKidNotDid")]
    [InlineData("did:example:alice#key-1", null, null, "RecipientKidNotDid")]
    public void Rejects_missing_or_malformed_cryptographic_identity_metadata(
        string? senderKid, string? signerKid, string? recipientKid, string expected)
    {
        // The reason enum is internal, so the theory names it as a string (a public test method
        // cannot take an internal parameter type).
        var ok = ReturnRouteEligibility.TryGetPeer(
            Inbound(senderKid: senderKid, signerKid: signerKid, recipientKid: recipientKid),
            out _, out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Be(Enum.Parse<ReturnRouteIneligibility>(expected));
    }

    [Fact]
    public void Rejects_when_sender_and_signer_key_ids_name_different_peers()
    {
        var inbound = Inbound(senderKid: "did:example:alice#key-1", signerKid: "did:example:mallory#signing-1");

        var ok = ReturnRouteEligibility.TryGetPeer(inbound, out _, out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Be(ReturnRouteIneligibility.SignerMismatch);
    }

    [Fact]
    public void Accepts_a_signer_kid_naming_the_same_subject_as_the_sender_kid()
    {
        var inbound = Inbound(senderKid: "did:example:alice#key-1", signerKid: "did:example:alice#signing-1");

        ReturnRouteEligibility.TryGetPeer(inbound, out var peer, out _, out var reason).Should().BeTrue();
        peer.Should().Be("did:example:alice");
        reason.Should().Be(ReturnRouteIneligibility.None);
    }

    [Fact]
    public void Handler_mutation_of_plaintext_from_cannot_change_the_key_bound_peer()
    {
        var inbound = Inbound();
        inbound.Message.From = "did:example:mallory"; // models a handler retaining/mutating the live message

        var ok = ReturnRouteEligibility.TryGetPeer(inbound, out var peer, out var local, out _);

        ok.Should().BeTrue();
        peer.Should().Be("did:example:alice");
        local.Should().Be("did:example:bob");
    }

    [Fact]
    public void Forward_typed_authcrypt_inbound_is_ineligible()
    {
        var inbound = Inbound(type: ForwardConstants.ForwardTypeUri);

        var ok = ReturnRouteEligibility.TryGetPeer(inbound, out _, out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Be(ReturnRouteIneligibility.Forward);
    }

    [Fact]
    public void Rejects_cross_tenant_reply_from_even_when_that_tenant_is_first_in_plaintext_to()
    {
        var inbound = Inbound(to: new[] { "did:example:tenant-a", "did:example:bob" });
        ReturnRouteEligibility.TryGetPeer(inbound, out var peer, out var local, out _).Should().BeTrue();
        local.Should().Be("did:example:bob");

        var attackerSelectedReply = Reply("did:example:tenant-a", "did:example:alice");

        ReturnRouteAddressing.Check(attackerSelectedReply, peer, local).Should().Be(ReturnRouteSendRefusal.FromNotLocalDid);
    }

    [Theory]
    [InlineData()]
    [InlineData("did:example:carol")]
    [InlineData("did:example:alice", "did:example:carol")]
    [InlineData("did:example:alice", "did:example:alice")]
    public void Rejects_reply_unless_to_is_exactly_the_authenticated_peer(params string[] recipients)
    {
        ReturnRouteAddressing.Check(Reply(to: recipients), "did:example:alice", "did:example:bob")
            .Should().Be(ReturnRouteSendRefusal.ToNotExactlyPeer);
    }

    [Fact]
    public void Compares_DID_URLs_by_subject_and_returns_bare_key_bound_DIDs()
    {
        var inbound = Inbound(
            from: "did:example:alice/path?version=2",
            senderKid: "did:example:alice#key-1",
            recipientKid: "did:example:bob#key-1");
        ReturnRouteEligibility.TryGetPeer(inbound, out var peer, out var local, out _).Should().BeTrue();
        peer.Should().Be("did:example:alice");
        local.Should().Be("did:example:bob");

        var reply = Reply("did:example:bob/agent?tenant=blue", "did:example:alice/inbox?version=2");

        ReturnRouteAddressing.Check(reply, peer, local).Should().Be(ReturnRouteSendRefusal.None);
    }

    [Fact]
    public void Rejects_reply_without_from()
    {
        ReturnRouteAddressing.Check(Reply(from: null, "did:example:alice"), "did:example:alice", "did:example:bob")
            .Should().Be(ReturnRouteSendRefusal.NoFrom);
    }

    [Fact]
    public void Rules_throw_only_for_null_input()
    {
        var a = () => ReturnRouteEligibility.TryGetPeer(null!, out _, out _, out _);
        a.Should().Throw<ArgumentNullException>();
        var b = () => ReturnRouteAddressing.Check(null!, "did:example:alice", "did:example:bob");
        b.Should().Throw<ArgumentNullException>();
    }
}
