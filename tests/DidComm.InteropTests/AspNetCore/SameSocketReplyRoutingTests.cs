using DidComm.AspNetCore;
using DidComm.Facade;
using DidComm.Messages;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// Unit tests for <see cref="DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply"/> —
/// the gate the registry-aware <c>MapDidCommWebSocket</c> overload applies before writing a
/// handler-produced reply onto the inbound WebSocket. The handler owns <c>from</c>/<c>to</c>
/// selection; this gate only enforces what the socket dictates: the reply MUST be addressed to
/// the inbound peer.
/// </summary>
public sealed class SameSocketReplyRoutingTests
{
    private static UnpackResult InboundFrom(string? from, params string[] to) =>
        new(
            Message: new MessageBuilder()
                .WithType("https://didcomm.org/test/1.0/m")
                .WithFrom(from ?? string.Empty)
                .WithTo(to)
                .Build(),
            Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
            Encrypted: false,
            Authenticated: false,
            NonRepudiation: false,
            AnonymousSender: false,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: null,
            SenderKid: null,
            RecipientKid: null,
            AllRecipientKids: Array.Empty<string>(),
            FromPrior: null);

    private static Message Reply(string? from, params string[] to) =>
        new MessageBuilder()
            .WithType("https://didcomm.org/test/1.0/reply")
            .WithFrom(from ?? string.Empty)
            .WithTo(to)
            .Build();

    [Fact]
    public void Allows_reply_addressed_to_inbound_peer_and_returns_handler_chosen_from()
    {
        // Inbound: alice → bob. Handler reply: bob → alice. The socket reaches alice (the peer);
        // reply.From is the handler's choice (bob) and is honored.
        var inbound = InboundFrom("did:peer:alice", "did:peer:bob");
        var reply = Reply("did:peer:bob", "did:peer:alice");

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, reply, out var from, out var peerDid, out var reason);

        ok.Should().BeTrue();
        from.Should().Be("did:peer:bob");        // trusts handler's reply.From
        peerDid.Should().Be("did:peer:alice");   // single recipient = the inbound peer
        reason.Should().BeNull();
    }

    [Fact]
    public void Allows_reply_when_handler_chose_a_different_local_identity_for_from()
    {
        // Multi-DID agent: inbound was addressed to bob, but the handler legitimately replies as
        // bob-alt (e.g. a per-thread persona). The gate enforces only that the inbound peer is
        // in reply.to — it doesn't second-guess the handler's chosen sender identity.
        var inbound = InboundFrom("did:peer:alice", "did:peer:bob");
        var reply = Reply("did:peer:bob-alt", "did:peer:alice");

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, reply, out var from, out var peerDid, out _);

        ok.Should().BeTrue();
        from.Should().Be("did:peer:bob-alt");
        peerDid.Should().Be("did:peer:alice");
    }

    [Fact]
    public void Drops_when_reply_From_is_missing()
    {
        var inbound = InboundFrom("did:peer:alice", "did:peer:bob");
        var reply = Reply(from: null, "did:peer:alice");

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, reply, out var from, out var peerDid, out var reason);

        ok.Should().BeFalse();
        from.Should().BeNull();
        peerDid.Should().BeNull();
        reason.Should().Contain("has no 'from'");
    }

    [Fact]
    public void Drops_when_inbound_From_is_missing()
    {
        // Anoncrypt inbound (no 'from') has no addressable peer on the same socket.
        var inbound = InboundFrom(from: null, "did:peer:bob");
        var reply = Reply("did:peer:bob", "did:peer:carol");

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, reply, out var from, out var peerDid, out var reason);

        ok.Should().BeFalse();
        from.Should().BeNull();
        peerDid.Should().BeNull();
        reason.Should().Contain("inbound envelope has no 'from'");
    }

    [Fact]
    public void Drops_when_reply_To_does_not_include_the_inbound_peer()
    {
        // The handler addressed the reply to carol, not to the inbound peer alice. Writing it
        // onto alice's socket would deliver ciphertext alice cannot decrypt — drop.
        var inbound = InboundFrom("did:peer:alice", "did:peer:bob");
        var reply = Reply("did:peer:bob", "did:peer:carol");

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, reply, out var from, out var peerDid, out var reason);

        ok.Should().BeFalse();
        from.Should().BeNull();
        peerDid.Should().BeNull();
        reason.Should().Contain("did:peer:alice");
        reason.Should().Contain("does not include the inbound peer");
    }

    [Fact]
    public void Drops_when_reply_To_is_empty()
    {
        var inbound = InboundFrom("did:peer:alice", "did:peer:bob");
        var reply = Reply("did:peer:bob"); // empty 'to'

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, reply, out var from, out var peerDid, out var reason);

        ok.Should().BeFalse();
        from.Should().BeNull();
        peerDid.Should().BeNull();
        reason.Should().Contain("does not include the inbound peer");
    }

    [Fact]
    public void Allows_reply_when_peer_is_one_of_multiple_recipients_on_reply_to()
    {
        // Fan-out reply addressed to alice + carol — alice is the inbound peer, so same-socket
        // delivery to alice is fine (carol would need an out-of-band send).
        var inbound = InboundFrom("did:peer:alice", "did:peer:bob");
        var reply = Reply("did:peer:bob", "did:peer:carol", "did:peer:alice");

        var ok = DidCommEndpointRouteBuilderExtensions.TryRouteSameSocketReply(
            inbound, reply, out var from, out var peerDid, out _);

        ok.Should().BeTrue();
        from.Should().Be("did:peer:bob");
        peerDid.Should().Be("did:peer:alice");
    }
}
