using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Threading;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Threading;

/// <summary>
/// DIDComm v2.1 threading (threading.md L9, L15, L21): a message <c>id</c> "MUST be compared
/// case-insensitively", and <c>thid</c> / <c>pthid</c> obey the same constraints (PRD FR-MSG-04). So
/// thread ids that differ only in letter case name one thread: in the store, and through the
/// dispatcher that keys thread state by <c>thid ?? id</c>. Genuinely different ids stay apart.
/// </summary>
public sealed class ThreadIdCaseInsensitivityTests
{
    private const string Lower = "4f1c2b7e-thread-abc";
    private const string Upper = "4F1C2B7E-THREAD-ABC";
    private const string Mixed = "4f1C2b7E-Thread-aBc";

    // ---- the store --------------------------------------------------------------------------

    [Fact]
    public void Thread_ids_that_differ_only_in_case_share_one_state_whose_Thid_keeps_the_first_casing()
    {
        var store = new InMemoryThreadStateStore();

        var first = store.GetOrCreate(Lower);
        first.AcceptLang = new[] { "fr" };
        var second = store.GetOrCreate(Upper);
        var read = store.Get(Mixed);

        second.Should().BeSameAs(first);
        read.Should().BeSameAs(first);
        second.AcceptLang.Should().Equal("fr");
        second.Thid.Should().Be(Lower, "the state keeps the casing of the call that created it");
        store.Count.Should().Be(1);
    }

    [Fact]
    public void Remove_with_another_casing_ends_the_thread()
    {
        var store = new InMemoryThreadStateStore();
        store.GetOrCreate(Lower).AcceptLang = new[] { "fr" };

        var removed = store.Remove(Upper);

        removed.Should().BeTrue();
        store.Get(Lower).Should().BeNull();
        store.Count.Should().Be(0);
    }

    [Fact]
    public void Thread_ids_that_differ_in_more_than_case_stay_apart()
    {
        // Control: only letter case is ignored, nothing else (a different character, a suffix).
        var store = new InMemoryThreadStateStore();

        var a = store.GetOrCreate(Lower);
        var b = store.GetOrCreate(Lower[..^1] + "d");
        var c = store.GetOrCreate(Lower + "0");

        b.Should().NotBeSameAs(a);
        c.Should().NotBeSameAs(a);
        store.Count.Should().Be(3);
    }

    // ---- through the dispatcher -------------------------------------------------------------

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

    private static Message Msg(string id, string? thid) =>
        new() { Id = id, Thid = thid, Type = "https://didcomm.org/test/1.0/m", From = "did:peer:alice", To = new[] { "did:peer:bob" } };

    private sealed class RecordingHandler(Message? reply = null) : IProtocolHandler
    {
        public List<DidComm.Threading.ThreadState> Seen { get; } = new();
        public string ProtocolUri => "https://didcomm.org/test/1.0";
        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            Seen.Add(context.Thread);
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task The_dispatcher_hands_messages_whose_thids_differ_only_in_case_the_same_thread_state()
    {
        var registry = new ProtocolHandlerRegistry();
        var handler = new RecordingHandler();
        registry.Register(handler);
        var dispatcher = new ProtocolDispatcher(registry, new InMemoryThreadStateStore());

        // The first message declares no thid, so its id is the thread id (threading.md L15); the
        // replies on the thread carry that id with other letter casings.
        await dispatcher.DispatchAsync(Unpack(Msg(Lower, thid: null)), client: null, new DidCommOptions());
        await dispatcher.DispatchAsync(Unpack(Msg("in-2", thid: Upper)), client: null, new DidCommOptions());
        await dispatcher.DispatchAsync(Unpack(Msg("in-3", thid: Mixed)), client: null, new DidCommOptions());

        handler.Seen.Should().HaveCount(3);
        handler.Seen[1].Should().BeSameAs(handler.Seen[0]);
        handler.Seen[2].Should().BeSameAs(handler.Seen[0]);
        handler.Seen[0].Thid.Should().Be(Lower);
    }

    [Fact]
    public async Task The_dispatcher_keeps_threads_that_differ_in_more_than_case_apart()
    {
        // Control for the test above.
        var registry = new ProtocolHandlerRegistry();
        var handler = new RecordingHandler();
        registry.Register(handler);
        var dispatcher = new ProtocolDispatcher(registry, new InMemoryThreadStateStore());

        await dispatcher.DispatchAsync(Unpack(Msg("in-1", thid: Lower)), client: null, new DidCommOptions());
        await dispatcher.DispatchAsync(Unpack(Msg("in-2", thid: Lower + "0")), client: null, new DidCommOptions());

        handler.Seen.Should().HaveCount(2);
        handler.Seen[1].Should().NotBeSameAs(handler.Seen[0]);
    }

    [Fact]
    public async Task A_pure_ack_answering_our_ack_request_on_an_upper_cased_thid_is_consumed_as_the_loop_rule_requires()
    {
        // FR-THR-04 rule 3: the dispatcher records its ACK request on the thread; the peer's pure ACK
        // on the same thread, written in other letter case, is the answer and is consumed.
        var registry = new ProtocolHandlerRegistry();
        var handler = new RecordingHandler(new MessageBuilder().WithType("https://didcomm.org/test/1.0/reply").WithPleaseAck().Build());
        registry.Register(handler);
        var store = new InMemoryThreadStateStore();
        var dispatcher = new ProtocolDispatcher(registry, store);
        await dispatcher.DispatchAsync(Unpack(Msg("in-1", thid: Lower)), client: null, new DidCommOptions());
        var pureAck = Message.Empty().WithFrom("did:peer:alice").WithTo("did:peer:bob").WithAck("reply-1").WithThid(Upper).Build();

        var outcome = await dispatcher.DispatchAsync(Unpack(pureAck), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.DroppedAsAckLoop);
        handler.Seen.Should().ContainSingle("the answering pure ACK must not reach a handler");
        store.Get(Lower)!.AckRequested.Should().BeFalse("the satisfied ACK request is cleared");
    }

    [Fact]
    public async Task A_pure_ack_on_another_thread_is_not_taken_for_the_answer()
    {
        // Control for the test above: the pure ACK names a thread that differs in more than case.
        var registry = new ProtocolHandlerRegistry();
        var handler = new RecordingHandler(new MessageBuilder().WithType("https://didcomm.org/test/1.0/reply").WithPleaseAck().Build());
        registry.Register(handler);
        var store = new InMemoryThreadStateStore();
        var dispatcher = new ProtocolDispatcher(registry, store);
        await dispatcher.DispatchAsync(Unpack(Msg("in-1", thid: Lower)), client: null, new DidCommOptions());
        var pureAck = Message.Empty().WithFrom("did:peer:alice").WithTo("did:peer:bob").WithAck("reply-1").WithThid(Upper + "0").Build();

        var outcome = await dispatcher.DispatchAsync(Unpack(pureAck), client: null, new DidCommOptions());

        outcome.Result.Should().NotBe(DispatchResult.DroppedAsAckLoop);
        store.Get(Lower)!.AckRequested.Should().BeTrue("the ACK request on our thread is still open");
    }
}
