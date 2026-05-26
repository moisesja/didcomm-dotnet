using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Empty;
using DidComm.Protocols.TrustPing;
using DidComm.Threading;
using FluentAssertions;
using Xunit;

// L-014: alias the TrustPing static API class to dodge namespace shadowing.
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.Tests.Protocols.Dispatch;

public sealed class ProtocolDispatcherTests
{
    // Build a synthetic UnpackResult around a plaintext message — these tests exercise the
    // dispatcher's control flow, not the envelope crypto. Stack=[] is fine because dispatcher
    // never inspects the envelope shape; it only reads Message.Type / Thid / Id.
    private static UnpackResult Unpack(Message msg) => new(
        Message: msg,
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

    private sealed class CapturingHandler : IProtocolHandler
    {
        public Message? ReplyToReturn { get; set; }
        public int CallCount { get; private set; }
        public string ProtocolUri => "https://didcomm.org/test/1.0";
        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(ReplyToReturn);
        }
    }

    private static Message Msg(string type, string id = "msg-1") =>
        new() { Id = id, Type = type, From = "did:peer:alice", To = new[] { "did:peer:bob" } };

    [Fact]
    public async Task No_handler_returns_NoHandler_outcome()
    {
        var dispatcher = new ProtocolDispatcher(new ProtocolHandlerRegistry(), new InMemoryThreadStateStore());
        var result = await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/x/1.0/msg")), client: null!, new DidCommOptions());
        result.Result.Should().Be(DispatchResult.NoHandler);
        result.Reply.Should().BeNull();
        result.Handler.Should().BeNull();
    }

    [Fact]
    public async Task Handler_null_reply_returns_NoReply()
    {
        var reg = new ProtocolHandlerRegistry();
        var handler = new CapturingHandler { ReplyToReturn = null };
        reg.Register(handler);
        var dispatcher = new ProtocolDispatcher(reg, new InMemoryThreadStateStore());

        var result = await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/test/1.0/m")), client: null!, new DidCommOptions());
        result.Result.Should().Be(DispatchResult.NoReply);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Handler_reply_returns_ReplyProduced()
    {
        var reg = new ProtocolHandlerRegistry();
        var reply = new MessageBuilder().WithType("https://didcomm.org/test/1.0/reply").Build();
        var handler = new CapturingHandler { ReplyToReturn = reply };
        reg.Register(handler);
        var dispatcher = new ProtocolDispatcher(reg, new InMemoryThreadStateStore());

        var result = await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/test/1.0/m")), client: null!, new DidCommOptions());
        result.Result.Should().Be(DispatchResult.ReplyProduced);
        result.Reply.Should().BeSameAs(reply);
        result.Handler.Should().BeSameAs(handler);
    }

    [Fact]
    public async Task Handler_returning_unsafe_reply_throws_InvalidOperationException()
    {
        // FR-THR-04 rule 2: a pure ACK that also requests an ACK is a handler bug; the
        // dispatcher refuses to surface it to the transport layer.
        var reg = new ProtocolHandlerRegistry();
        var badReply = Message.Empty().WithFrom("did:peer:bob").WithTo("did:peer:alice")
            .WithAck("some-id").WithPleaseAck().Build();
        reg.Register(new CapturingHandler { ReplyToReturn = badReply });
        var dispatcher = new ProtocolDispatcher(reg, new InMemoryThreadStateStore());

        Func<Task> act = async () => await dispatcher.DispatchAsync(
            Unpack(Msg("https://didcomm.org/test/1.0/m")), client: null!, new DidCommOptions());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*FR-THR-04*");
    }

    [Fact]
    public async Task Inbound_pure_ack_that_requests_ack_is_dropped_without_calling_handler()
    {
        // FR-THR-04 rule 3 defensive enforcement of a peer's rule-2 violation.
        var reg = new ProtocolHandlerRegistry();
        var handler = new CapturingHandler { ReplyToReturn = null };
        reg.Register(handler);
        var dispatcher = new ProtocolDispatcher(reg, new InMemoryThreadStateStore());

        var loopTrap = Message.Empty().WithFrom("did:peer:alice").WithTo("did:peer:bob")
            .WithAck("prev-id").WithPleaseAck().Build();

        var result = await dispatcher.DispatchAsync(Unpack(loopTrap), client: null!, new DidCommOptions());
        result.Result.Should().Be(DispatchResult.DroppedAsAckLoop);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ThreadState_is_per_thid_and_passed_to_handler()
    {
        var reg = new ProtocolHandlerRegistry();
        DidComm.Threading.ThreadState? observed = null;
        reg.Register(new DelegateHandler("https://didcomm.org/test/1.0", (m, ctx) => { observed = ctx.Thread; return null; }));
        var store = new InMemoryThreadStateStore();
        var dispatcher = new ProtocolDispatcher(reg, store);

        var msg = Msg("https://didcomm.org/test/1.0/m", id: "id-x");
        msg.Thid = "thread-7";
        await dispatcher.DispatchAsync(Unpack(msg), client: null!, new DidCommOptions());

        observed.Should().NotBeNull();
        observed!.Thid.Should().Be("thread-7");
        store.Get("thread-7").Should().BeSameAs(observed);
    }

    [Fact]
    public async Task TrustPing_round_trips_through_dispatcher()
    {
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new TrustPingHandler());
        var dispatcher = new ProtocolDispatcher(reg, new InMemoryThreadStateStore());

        var ping = TrustPingApi.CreatePing(from: "did:peer:alice", to: "did:peer:bob");
        var outcome = await dispatcher.DispatchAsync(Unpack(ping), client: null!, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Reply!.Type.Should().Be(TrustPingApi.ResponseType);
        outcome.Reply.Thid.Should().Be(ping.Id);
        outcome.Reply.From.Should().Be("did:peer:bob");
        outcome.Reply.To.Should().Equal("did:peer:alice");
    }

    [Fact]
    public async Task Empty_handler_round_trips_with_no_reply()
    {
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new EmptyHandler());
        var dispatcher = new ProtocolDispatcher(reg, new InMemoryThreadStateStore());

        var empty = Message.Empty().WithFrom("did:peer:alice").WithTo("did:peer:bob").WithAck("prev").Build();
        var outcome = await dispatcher.DispatchAsync(Unpack(empty), client: null!, new DidCommOptions());
        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Handler.Should().BeOfType<EmptyHandler>();
    }

    private sealed class DelegateHandler(string protocolUri, Func<Message, ProtocolContext, Message?> impl) : IProtocolHandler
    {
        public string ProtocolUri { get; } = protocolUri;
        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
            => Task.FromResult(impl(message, context));
    }
}
