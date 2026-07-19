using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.ProblemReport;
using DidComm.Threading;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

// L-014: alias the ProblemReport static API class to dodge namespace shadowing.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Tests.Protocols.Dispatch;

/// <summary>
/// FR-PROTO-12 — the read-only inbound observer seam. Covers the notification contract
/// (every completed outcome, exactly once), the isolation guarantees (defensive clone,
/// swallowed exceptions), least-privilege filtering, and the #50 use case: observing an
/// inbound <c>report-problem</c> without replacing the built-in handler.
/// </summary>
public sealed class ProtocolDispatcherObserverTests
{
    private static UnpackResult Unpack(Message msg, bool authenticated = false) => new(
        Message: msg,
        Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
        Encrypted: authenticated,
        Authenticated: authenticated,
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
        public Message? SeenMessage { get; private set; }
        public int CallCount { get; private set; }
        public string ProtocolUri => "https://didcomm.org/test/1.0";
        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            CallCount++;
            SeenMessage = message;
            return Task.FromResult(ReplyToReturn);
        }
    }

    private sealed class RecordingObserver : IProtocolObserver
    {
        private readonly List<InboundObservation> _observations = new();
        public string? ProtocolUriFilter { get; init; }
        public Exception? ThrowOnObserve { get; init; }
        public Action<InboundObservation>? OnObserve { get; init; }
        public IReadOnlyList<InboundObservation> Observations => _observations;
        public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            _observations.Add(observation);
            OnObserve?.Invoke(observation);
            if (ThrowOnObserve is not null)
                throw ThrowOnObserve;
            return Task.CompletedTask;
        }
    }

    private static Message Msg(string type, string id = "msg-1") =>
        new() { Id = id, Type = type, From = "did:peer:alice", To = new[] { "did:peer:bob" } };

    private static ProtocolDispatcher Dispatcher(ProtocolHandlerRegistry registry, params IProtocolObserver[] observers)
        => new(registry, new InMemoryThreadStateStore(), logger: null, traceOptions: null, observers: observers);

    [Fact]
    public async Task Observer_is_notified_after_a_handled_dispatch_with_a_faithful_clone()
    {
        var reg = new ProtocolHandlerRegistry();
        var handler = new CapturingHandler { ReplyToReturn = new MessageBuilder().WithType("https://didcomm.org/test/1.0/reply").Build() };
        reg.Register(handler);
        var observer = new RecordingObserver();
        var dispatcher = Dispatcher(reg, observer);

        var msg = Msg("https://didcomm.org/test/1.0/m");
        msg.Body = new JsonObject { ["k"] = "v" };

        var outcome = await dispatcher.DispatchAsync(Unpack(msg, authenticated: true), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        observer.Observations.Should().HaveCount(1);
        var seen = observer.Observations[0];
        seen.Message.Should().NotBeSameAs(msg, "observers must get a defensive clone, never the live instance");
        seen.Message.Body.Should().NotBeSameAs(msg.Body);
        seen.Message.Id.Should().Be(msg.Id);
        seen.Message.Type.Should().Be(msg.Type);
        seen.Message.Body!["k"]!.GetValue<string>().Should().Be("v");
        seen.Authenticated.Should().BeTrue("envelope-auth metadata must flow through for trust decisions");
    }

    [Fact]
    public async Task Observer_is_notified_on_NoHandler()
    {
        var observer = new RecordingObserver();
        var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), observer);

        var outcome = await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/x/1.0/msg")), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.NoHandler);
        observer.Observations.Should().HaveCount(1, "an initiator with no handler for a PIURI must still observe its traffic");
    }

    [Fact]
    public async Task Observer_is_notified_on_ack_loop_drop()
    {
        var observer = new RecordingObserver();
        var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), observer);

        // FR-THR-04 rule 2 violation by the peer: a pure ACK that also requests an ACK is
        // dropped without invoking any handler — but the observation side channel still fires.
        var msg = Msg("https://didcomm.org/empty/1.0/empty");
        msg.Ack = new[] { "prior-msg" };
        msg.PleaseAck = new[] { "" };

        var outcome = await dispatcher.DispatchAsync(Unpack(msg), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.DroppedAsAckLoop);
        observer.Observations.Should().HaveCount(1);
    }

    [Fact]
    public async Task Observer_exception_is_swallowed_and_later_observers_still_run()
    {
        var reg = new ProtocolHandlerRegistry();
        var handler = new CapturingHandler { ReplyToReturn = new MessageBuilder().WithType("https://didcomm.org/test/1.0/reply").Build() };
        reg.Register(handler);
        var throwing = new RecordingObserver { ThrowOnObserve = new InvalidOperationException("observer bug") };
        var second = new RecordingObserver();
        var dispatcher = Dispatcher(reg, throwing, second);

        var outcome = await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/test/1.0/m")), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.ReplyProduced, "an observer bug must never affect the dispatch outcome");
        handler.CallCount.Should().Be(1);
        throwing.Observations.Should().HaveCount(1);
        second.Observations.Should().HaveCount(1, "one observer throwing must not starve the others");
    }

    [Fact]
    public async Task Mutating_observer_cannot_reach_the_pipeline_message_or_another_observers_view()
    {
        var reg = new ProtocolHandlerRegistry();
        var handler = new CapturingHandler();
        reg.Register(handler);
        var mutating = new RecordingObserver
        {
            OnObserve = obs =>
            {
                obs.Message.Type = "https://didcomm.org/evil/1.0/tampered";
                obs.Message.Body!["k"] = "TAMPERED";
            },
        };
        var second = new RecordingObserver();
        var dispatcher = Dispatcher(reg, mutating, second);

        var msg = Msg("https://didcomm.org/test/1.0/m");
        msg.Body = new JsonObject { ["k"] = "v" };

        await dispatcher.DispatchAsync(Unpack(msg), client: null, new DidCommOptions());

        msg.Type.Should().Be("https://didcomm.org/test/1.0/m", "the live pipeline message must be untouchable");
        msg.Body!["k"]!.GetValue<string>().Should().Be("v");
        second.Observations[0].Message.Body!["k"]!.GetValue<string>().Should().Be("v",
            "each observer gets its own clone, so one observer's mutation is invisible to the next");
    }

    [Fact]
    public async Task Filtered_observer_sees_only_its_protocol_family()
    {
        var filtered = new RecordingObserver { ProtocolUriFilter = "https://didcomm.org/discover-features/2.0" };
        var unfiltered = new RecordingObserver();
        var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), filtered, unfiltered);
        var options = new DidCommOptions();

        await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/trust-ping/2.0/ping", "m1")), client: null, options);
        filtered.Observations.Should().BeEmpty("a different protocol must not reach a filtered observer");

        await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/discover-features/2.1/disclose", "m2")), client: null, options);
        filtered.Observations.Should().HaveCount(1, "a 2.0 filter observes the whole 2.x family (minor-tolerant per FR-PROTO-02)");

        await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/discover-features/3.0/disclose", "m3")), client: null, options);
        filtered.Observations.Should().HaveCount(1, "a different major is a different protocol");

        unfiltered.Observations.Should().HaveCount(3, "a null filter observes everything");
    }

    [Fact]
    public async Task A_malformed_message_type_cannot_break_dispatch_or_starve_later_observers()
    {
        // A filtered observer's match check runs on Message.Type; a pathological type must be
        // swallowed inside the per-observer guard, not propagate out of DispatchAsync and clobber
        // the already-computed outcome — and must not starve a later unfiltered observer.
        var filtered = new RecordingObserver { ProtocolUriFilter = "https://didcomm.org/discover-features/2.0" };
        var unfiltered = new RecordingObserver();
        var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), filtered, unfiltered);

        var msg = new Message { Id = "m1", Type = "not-a-valid-mturi", From = "did:peer:alice", To = new[] { "did:peer:bob" } };
        var act = async () => await dispatcher.DispatchAsync(Unpack(msg), client: null, new DidCommOptions());

        var outcome = await act.Should().NotThrowAsync();
        outcome.Which.Result.Should().Be(DispatchResult.NoHandler);
        filtered.Observations.Should().BeEmpty("a malformed type matches no PIURI filter");
        unfiltered.Observations.Should().HaveCount(1, "a null-filter observer still sees every inbound");
    }

    [Fact]
    public async Task Crafted_double_slash_type_on_the_ack_drop_path_does_not_break_dispatch_or_a_filtered_observer()
    {
        // The adversarial exploit chain: a crafted `type` with a doubled slash parses as an MTURI
        // but its derived PIURI does not, so ObserverMatches' PIURI parse would throw. Delivered
        // as a pure-ACK-that-requests-an-ACK, dispatch early-returns DroppedAsAckLoop (skipping
        // handler resolution) and then notifies observers — with a filtered observer registered
        // (as AddBuiltInProtocols does via DiscoverFeaturesClient), the throw must NOT escape and
        // clobber the outcome.
        var filtered = new RecordingObserver { ProtocolUriFilter = "https://didcomm.org/discover-features/2.0" };
        var unfiltered = new RecordingObserver();
        var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), filtered, unfiltered);

        var msg = new Message
        {
            Id = "x",
            Type = "https://didcomm.org//foo/1.0/bar",
            Ack = new[] { "x" },
            PleaseAck = new[] { "" },
        };

        var act = async () => await dispatcher.DispatchAsync(Unpack(msg), client: null, new DidCommOptions());

        var outcome = await act.Should().NotThrowAsync();
        outcome.Which.Result.Should().Be(DispatchResult.DroppedAsAckLoop, "the computed outcome must survive observer notification");
        filtered.Observations.Should().BeEmpty("a malformed type matches no PIURI filter (and must not throw)");
        unfiltered.Observations.Should().HaveCount(1, "a null-filter observer still observes the dropped message");
    }

    [Fact]
    public async Task Inbound_report_problem_is_observable_while_the_builtin_handler_still_runs()
    {
        // The #50 use case: a higher-level state machine wants to learn that its thread failed,
        // WITHOUT replacing ProblemReportHandler (which owns the PIURI and the cascade budget).
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new ProblemReportHandler(Options.Create(new ProblemReportOptions())));
        var observer = new RecordingObserver { ProtocolUriFilter = ProblemReportApi.ProtocolUri };
        var dispatcher = Dispatcher(reg, observer);

        var report = ProblemReportApi.Create(
            from: "did:peer:bob",
            to: "did:peer:alice",
            code: "e.p.xfer.cant-use-endpoint",
            pthid: "failing-thread-1");

        var outcome = await dispatcher.DispatchAsync(Unpack(report, authenticated: true), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.NoReply, "the built-in handler still consumes the report (cascade bookkeeping intact)");
        observer.Observations.Should().HaveCount(1);
        observer.Observations[0].Message.Pthid.Should().Be("failing-thread-1");
        observer.Observations[0].Message.Body!["code"]!.GetValue<string>().Should().Be("e.p.xfer.cant-use-endpoint");
    }
}
