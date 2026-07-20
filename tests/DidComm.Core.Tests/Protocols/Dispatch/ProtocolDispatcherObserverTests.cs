using System.Collections.Concurrent;
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
/// FR-PROTO-12 — the read-only inbound observer seam. Observers are delivered off the dispatch
/// path via a bounded background queue, so these tests <see cref="ProtocolDispatcher.FlushObserversAsync"/>
/// before asserting. Covers: the notification contract (every outcome, once per matching message),
/// the isolation guarantees (defensive clone, never gates or clobbers the outcome, one hung observer
/// can't starve another), least-privilege filtering, and the #50 report-problem use case.
/// </summary>
public sealed class ProtocolDispatcherObserverTests
{
    // Generous: only paid on the failure path. The observer pumps run on Task.Run continuations, which
    // can be scheduled seconds late under parallel-test thread-pool contention on constrained CI
    // runners (e.g. 2-core Windows). A tight timeout there is a flake, not a real failure.
    private static readonly TimeSpan Flush = TimeSpan.FromSeconds(30);

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
        public int CallCount { get; private set; }
        public string ProtocolUri => "https://didcomm.org/test/1.0";
        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(ReplyToReturn);
        }
    }

    private sealed class RecordingObserver : IProtocolObserver
    {
        private readonly ConcurrentQueue<InboundObservation> _observations = new();
        public string? ProtocolUriFilter { get; init; }
        public Exception? ThrowOnObserve { get; init; }
        public Action<InboundObservation>? OnObserve { get; init; }
        public IReadOnlyList<InboundObservation> Observations => _observations.ToArray();
        public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            _observations.Enqueue(observation);
            OnObserve?.Invoke(observation);
            if (ThrowOnObserve is not null)
                throw ThrowOnObserve;
            return Task.CompletedTask;
        }
    }

    /// <summary>An observer whose callback blocks until released — models a slow/hung observer.</summary>
    private sealed class BlockingObserver : IProtocolObserver
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ProtocolUriFilter => null;
        public Task Started => _started.Task;
        public void Release() => _gate.TrySetResult();
        public async Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            _started.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
        }
    }

    /// <summary>An observer that signals a Task the first time it observes — for deterministic waits
    /// that don't poll (robust under parallel-test thread-pool contention).</summary>
    private sealed class SignalingObserver : IProtocolObserver
    {
        private readonly TaskCompletionSource _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ProtocolUriFilter => null;
        public Task Observed => _observed.Task;
        public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            _observed.TrySetResult();
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
        await using var dispatcher = Dispatcher(reg, observer);

        var msg = Msg("https://didcomm.org/test/1.0/m");
        msg.Body = new JsonObject { ["k"] = "v" };

        var outcome = await dispatcher.DispatchAsync(Unpack(msg, authenticated: true), client: null, new DidCommOptions());
        await dispatcher.FlushObserversAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        observer.Observations.Should().HaveCount(1);
        var seen = observer.Observations[0];
        seen.Message.Should().NotBeSameAs(msg, "observers must get a defensive clone, never the live instance");
        seen.Message.Body.Should().NotBeSameAs(msg.Body);
        seen.Message.Id.Should().Be(msg.Id);
        seen.Message.Body!["k"]!.GetValue<string>().Should().Be("v");
        seen.Authenticated.Should().BeTrue("envelope-auth metadata must flow through for trust decisions");
    }

    [Fact]
    public async Task Observer_is_notified_on_NoHandler()
    {
        var observer = new RecordingObserver();
        await using var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), observer);

        var outcome = await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/x/1.0/msg")), client: null, new DidCommOptions());
        await dispatcher.FlushObserversAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoHandler);
        observer.Observations.Should().HaveCount(1, "an initiator with no handler for a PIURI must still observe its traffic");
    }

    [Fact]
    public async Task Observer_is_notified_on_ack_loop_drop()
    {
        var observer = new RecordingObserver();
        await using var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), observer);

        var msg = Msg("https://didcomm.org/empty/1.0/empty");
        msg.Ack = new[] { "prior-msg" };
        msg.PleaseAck = new[] { "" };

        var outcome = await dispatcher.DispatchAsync(Unpack(msg), client: null, new DidCommOptions());
        await dispatcher.FlushObserversAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.DroppedAsAckLoop);
        observer.Observations.Should().HaveCount(1);
    }

    [Fact]
    public async Task Observer_exception_is_isolated_and_other_observers_still_run()
    {
        var reg = new ProtocolHandlerRegistry();
        var handler = new CapturingHandler { ReplyToReturn = new MessageBuilder().WithType("https://didcomm.org/test/1.0/reply").Build() };
        reg.Register(handler);
        var throwing = new RecordingObserver { ThrowOnObserve = new InvalidOperationException("observer bug") };
        var second = new RecordingObserver();
        await using var dispatcher = Dispatcher(reg, throwing, second);

        var outcome = await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/test/1.0/m")), client: null, new DidCommOptions());
        await dispatcher.FlushObserversAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced, "an observer bug must never affect the dispatch outcome");
        handler.CallCount.Should().Be(1);
        throwing.Observations.Should().HaveCount(1);
        second.Observations.Should().HaveCount(1, "one observer throwing must not starve the others (separate queues)");
    }

    [Fact]
    public async Task A_never_completing_observer_neither_blocks_dispatch_nor_starves_another_observer()
    {
        var hung = new BlockingObserver();
        var healthy = new SignalingObserver();
        await using var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), hung, healthy);

        // Dispatch returns immediately even though `hung` will never complete its observation.
        var dispatch = dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/x/1.0/m")), client: null, new DidCommOptions());
        var outcome = await dispatch.WaitAsync(TimeSpan.FromSeconds(30));
        outcome.Result.Should().Be(DispatchResult.NoHandler, "a hung observer must not gate the dispatch outcome");

        // The healthy observer drains on its own pump despite `hung` being stuck on its own — wait on
        // its signal (not the global flush, whose hung barrier never completes; not a poll, which can
        // flake under parallel-test thread-pool contention). Both observers run on independent pumps,
        // so await each signal rather than assuming an ordering between them.
        await healthy.Observed.WaitAsync(TimeSpan.FromSeconds(30));
        await hung.Started.WaitAsync(TimeSpan.FromSeconds(30)); // it started (on its own pump) and is now blocked

        hung.Release();
    }

    /// <summary>Observer whose <see cref="ProtocolUriFilter"/> getter throws — must not break dispatch
    /// or construction; the observer is disabled and sees nothing.</summary>
    private sealed class ThrowingFilterObserver : IProtocolObserver
    {
        public string? ProtocolUriFilter => throw new InvalidOperationException("hostile filter getter");
        public int Delivered;
        public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            Interlocked.Increment(ref Delivered);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_throwing_ProtocolUriFilter_getter_disables_that_observer_without_breaking_construction_or_dispatch()
    {
        var hostile = new ThrowingFilterObserver();
        var healthy = new RecordingObserver();
        // Construction reads the filter once, guarded — a throwing getter must not break the ctor.
        await using var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), hostile, healthy);

        var outcome = await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/x/1.0/m")), client: null, new DidCommOptions());
        await dispatcher.FlushObserversAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoHandler, "a hostile filter getter must not affect the outcome");
        hostile.Delivered.Should().Be(0, "an observer whose filter getter threw is disabled and sees nothing");
        healthy.Observations.Should().HaveCount(1, "the healthy observer is unaffected");
    }

    [Fact]
    public async Task Dispatch_cancellation_cannot_clobber_the_computed_outcome()
    {
        var observer = new RecordingObserver();
        await using var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), observer);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already-cancelled token supplied to dispatch

        // Observers run on a background pump with their own token, decoupled from the dispatch ct,
        // so a cancelled dispatch token can neither throw out of DispatchAsync via the observer path
        // nor discard the computed outcome.
        var outcome = await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/x/1.0/m")), client: null, new DidCommOptions(), cts.Token);
        await dispatcher.FlushObserversAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoHandler);
        observer.Observations.Should().HaveCount(1);
    }

    [Fact]
    public async Task Mutating_observer_cannot_reach_the_pipeline_message_or_another_observers_view()
    {
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new CapturingHandler());
        var mutating = new RecordingObserver
        {
            OnObserve = obs =>
            {
                obs.Message.Type = "https://didcomm.org/evil/1.0/tampered";
                obs.Message.Body!["k"] = "TAMPERED";
            },
        };
        var second = new RecordingObserver();
        await using var dispatcher = Dispatcher(reg, mutating, second);

        var msg = Msg("https://didcomm.org/test/1.0/m");
        msg.Body = new JsonObject { ["k"] = "v" };

        await dispatcher.DispatchAsync(Unpack(msg), client: null, new DidCommOptions());
        await dispatcher.FlushObserversAsync(Flush);

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
        await using var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), filtered, unfiltered);
        var options = new DidCommOptions();

        await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/trust-ping/2.0/ping", "m1")), client: null, options);
        await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/discover-features/2.1/disclose", "m2")), client: null, options);
        await dispatcher.DispatchAsync(Unpack(Msg("https://didcomm.org/discover-features/3.0/disclose", "m3")), client: null, options);
        await dispatcher.FlushObserversAsync(Flush);

        filtered.Observations.Should().HaveCount(1, "only the matching 2.x disclose reaches a 2.0-filtered observer");
        filtered.Observations[0].Message.Id.Should().Be("m2");
        unfiltered.Observations.Should().HaveCount(3, "a null filter observes everything");
    }

    [Fact]
    public async Task A_malformed_message_type_cannot_break_dispatch_or_starve_later_observers()
    {
        var filtered = new RecordingObserver { ProtocolUriFilter = "https://didcomm.org/discover-features/2.0" };
        var unfiltered = new RecordingObserver();
        await using var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), filtered, unfiltered);

        var msg = new Message { Id = "m1", Type = "not-a-valid-mturi", From = "did:peer:alice", To = new[] { "did:peer:bob" } };
        var act = async () => await dispatcher.DispatchAsync(Unpack(msg), client: null, new DidCommOptions());

        var outcome = await act.Should().NotThrowAsync();
        await dispatcher.FlushObserversAsync(Flush);
        outcome.Which.Result.Should().Be(DispatchResult.NoHandler);
        filtered.Observations.Should().BeEmpty("a malformed type matches no PIURI filter");
        unfiltered.Observations.Should().HaveCount(1, "a null-filter observer still sees every inbound");
    }

    [Fact]
    public async Task Crafted_double_slash_type_on_the_ack_drop_path_does_not_break_dispatch_or_a_filtered_observer()
    {
        var filtered = new RecordingObserver { ProtocolUriFilter = "https://didcomm.org/discover-features/2.0" };
        var unfiltered = new RecordingObserver();
        await using var dispatcher = Dispatcher(new ProtocolHandlerRegistry(), filtered, unfiltered);

        var msg = new Message
        {
            Id = "x",
            Type = "https://didcomm.org//foo/1.0/bar",
            Ack = new[] { "x" },
            PleaseAck = new[] { "" },
        };

        var act = async () => await dispatcher.DispatchAsync(Unpack(msg), client: null, new DidCommOptions());

        var outcome = await act.Should().NotThrowAsync();
        await dispatcher.FlushObserversAsync(Flush);
        outcome.Which.Result.Should().Be(DispatchResult.DroppedAsAckLoop, "the computed outcome must survive observer notification");
        filtered.Observations.Should().BeEmpty("a malformed type matches no PIURI filter (and must not throw)");
        unfiltered.Observations.Should().HaveCount(1, "a null-filter observer still observes the dropped message");
    }

    [Fact]
    public async Task Inbound_report_problem_is_observable_while_the_builtin_handler_still_runs()
    {
        // The #50 use case: a higher-level state machine wants to learn its thread failed, WITHOUT
        // replacing ProblemReportHandler (which owns the PIURI and the cascade budget).
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new ProblemReportHandler(Options.Create(new ProblemReportOptions())));
        var observer = new RecordingObserver { ProtocolUriFilter = ProblemReportApi.ProtocolUri };
        await using var dispatcher = Dispatcher(reg, observer);

        var report = ProblemReportApi.Create(
            from: "did:peer:bob",
            to: "did:peer:alice",
            code: "e.p.xfer.cant-use-endpoint",
            pthid: "failing-thread-1");

        var outcome = await dispatcher.DispatchAsync(Unpack(report, authenticated: true), client: null, new DidCommOptions());
        await dispatcher.FlushObserversAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply, "the built-in handler still consumes the report (cascade bookkeeping intact)");
        observer.Observations.Should().HaveCount(1);
        observer.Observations[0].Message.Pthid.Should().Be("failing-thread-1");
        observer.Observations[0].Message.Body!["code"]!.GetValue<string>().Should().Be("e.p.xfer.cant-use-endpoint");
    }
}
