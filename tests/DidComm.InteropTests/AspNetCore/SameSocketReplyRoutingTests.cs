using DidComm.AspNetCore;
using DidComm.Facade;
using DidComm.Messages;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// Security-boundary tests for the registry-aware WebSocket reply gate. The route must derive
/// both identities from verified envelope key ids and never from plaintext ordering alone.
/// </summary>
public sealed class SameSocketReplyRoutingTests
{
    private static UnpackResult Inbound(
        string? from = "did:example:alice",
        string[]? to = null,
        bool encrypted = true,
        bool authenticated = true,
        string? senderKid = "did:example:alice#key-agreement-1",
        string? signerKid = null,
        string? recipientKid = "did:example:bob#key-agreement-1") =>
        new(
            Message: new MessageBuilder()
                .WithType("https://didcomm.org/test/1.0/m")
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
    public void Allows_authcrypt_reply_and_returns_key_bound_DID_subjects()
    {
        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            Inbound(), Reply(to: "did:example:alice"), out var from, out var peerDid, out var reason);

        ok.Should().BeTrue();
        from.Should().Be("did:example:bob");
        peerDid.Should().Be("did:example:alice");
        reason.Should().BeNull();
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

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, Reply(to: "did:example:alice"), out var from, out var peerDid, out var reason);

        ok.Should().BeFalse();
        from.Should().BeNull();
        peerDid.Should().BeNull();
        reason.Should().Contain("encrypted and authenticated");
    }

    [Fact]
    public void Allows_encrypted_authenticated_reply_bound_by_signer_kid_when_sender_kid_is_absent()
    {
        var inbound = Inbound(senderKid: null, signerKid: "did:example:alice#signing-1");

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, Reply(to: "did:example:alice"), out _, out var peerDid, out _);

        ok.Should().BeTrue();
        peerDid.Should().Be("did:example:alice");
    }

    [Theory]
    [InlineData(null, null, "did:example:bob#key-1", "sender or signer key id")]
    [InlineData("not-a-did", null, "did:example:bob#key-1", "sender kid")]
    [InlineData(null, "not-a-did", "did:example:bob#key-1", "signer kid")]
    [InlineData("did:example:alice#key-1", null, "not-a-did", "recipient key id")]
    public void Rejects_missing_or_malformed_cryptographic_identity_metadata(
        string? senderKid, string? signerKid, string? recipientKid, string expectedReason)
    {
        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            Inbound(senderKid: senderKid, signerKid: signerKid, recipientKid: recipientKid),
            Reply(to: "did:example:alice"),
            out _, out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain(expectedReason);
    }

    [Fact]
    public void Rejects_when_sender_and_signer_key_ids_name_different_peers()
    {
        var inbound = Inbound(
            senderKid: "did:example:alice#key-1",
            signerKid: "did:example:mallory#signing-1");

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, Reply(to: "did:example:alice"), out _, out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("different DID subjects");
    }

    [Fact]
    public void Handler_mutation_of_plaintext_from_cannot_change_the_key_bound_peer_route()
    {
        var inbound = Inbound();
        inbound.Message.From = "did:example:mallory"; // models a handler retaining/mutating the live message

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound,
            Reply(to: "did:example:alice"),
            out var from, out var peerDid, out var reason);

        ok.Should().BeTrue();
        from.Should().Be("did:example:bob");
        peerDid.Should().Be("did:example:alice");
        reason.Should().BeNull();
    }

    [Fact]
    public void Rejects_cross_tenant_reply_from_even_when_that_tenant_is_first_in_plaintext_to()
    {
        var inbound = Inbound(to: new[] { "did:example:tenant-a", "did:example:bob" });
        var attackerSelectedReply = Reply("did:example:tenant-a", "did:example:alice");

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, attackerSelectedReply, out _, out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("local decrypting key DID subject");
    }

    [Theory]
    [InlineData()]
    [InlineData("did:example:carol")]
    [InlineData("did:example:alice", "did:example:carol")]
    public void Rejects_reply_unless_to_is_exactly_the_authenticated_peer(params string[] recipients)
    {
        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            Inbound(), Reply(to: recipients), out _, out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("exactly the authenticated inbound peer");
    }

    [Fact]
    public void Compares_DID_URLs_by_subject_and_returns_bare_key_bound_DIDs()
    {
        var inbound = Inbound(
            from: "did:example:alice/path?version=2",
            senderKid: "did:example:alice#key-1",
            recipientKid: "did:example:bob#key-1");
        var reply = Reply("did:example:bob/agent?tenant=blue", "did:example:alice/inbox?version=2");

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, reply, out var from, out var peerDid, out var reason);

        ok.Should().BeTrue();
        from.Should().Be("did:example:bob");
        peerDid.Should().Be("did:example:alice");
        reason.Should().BeNull();
    }

    [Fact]
    public void Rejects_reply_without_from()
    {
        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            Inbound(), Reply(from: null, "did:example:alice"), out _, out _, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("reply.from");
    }
}
