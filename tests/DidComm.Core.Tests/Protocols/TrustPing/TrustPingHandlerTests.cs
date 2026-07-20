using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.TrustPing;
using DidComm.Threading;
using FluentAssertions;
using Xunit;

// L-014: alias the static API class.
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.Tests.Protocols.TrustPing;

public sealed class TrustPingHandlerTests
{
    private static ProtocolContext Ctx(Message m, string? recipientKid = null)
    {
        var unpacked = new UnpackResult(
            m, Array.Empty<DidComm.Jose.EnvelopeKind>(),
            recipientKid is not null, false, false, false, null, null, null, null, null, recipientKid,
            Array.Empty<string>(), null);
        return new ProtocolContext(unpacked, new DidComm.Threading.ThreadState(m.Thid ?? m.Id), Client: null, new DidCommOptions(), new InMemoryThreadStateStore());
    }

    [Fact]
    public async Task Replies_to_ping_with_default_response_requested_true()
    {
        var handler = new TrustPingHandler();
        var ping = TrustPingApi.CreatePing(from: "did:peer:alice", to: "did:peer:bob");
        var reply = await handler.HandleAsync(ping, Ctx(ping), CancellationToken.None);
        reply.Should().NotBeNull();
        reply!.Type.Should().Be(TrustPingApi.ResponseType);
        reply.Thid.Should().Be(ping.Id);
    }

    [Fact]
    public async Task Suppresses_reply_when_response_requested_false()
    {
        var handler = new TrustPingHandler();
        var ping = TrustPingApi.CreatePing(from: "did:peer:alice", to: "did:peer:bob", responseRequested: false);
        var reply = await handler.HandleAsync(ping, Ctx(ping), CancellationToken.None);
        reply.Should().BeNull();
    }

    [Fact]
    public async Task Does_not_reply_when_message_is_a_ping_response()
    {
        // A ping-response is the terminal leaf; never reply to it (would create a ping-pong loop).
        var handler = new TrustPingHandler();
        var pingResp = new MessageBuilder().WithType(TrustPingApi.ResponseType)
            .WithFrom("did:peer:bob").WithTo("did:peer:alice").WithThid("ping-id-1").Build();
        var reply = await handler.HandleAsync(pingResp, Ctx(pingResp), CancellationToken.None);
        reply.Should().BeNull();
    }

    [Fact]
    public async Task Reply_from_prefers_actual_decrypting_DID_over_first_plaintext_recipient()
    {
        var ping = new MessageBuilder()
            .WithType(TrustPingApi.PingType)
            .WithFrom("did:example:alice")
            .WithTo("did:example:other-tenant", "did:example:bob")
            .Build();

        var reply = await new TrustPingHandler().HandleAsync(
            ping,
            Ctx(ping, recipientKid: "did:example:bob#key-agreement-1"),
            CancellationToken.None);

        reply.Should().NotBeNull();
        reply!.From.Should().Be("did:example:bob");
        reply.To.Should().Equal("did:example:alice");
    }

    [Fact]
    public void ProtocolUri_matches_spec()
    {
        new TrustPingHandler().ProtocolUri.Should().Be("https://didcomm.org/trust-ping/2.0");
    }
}
