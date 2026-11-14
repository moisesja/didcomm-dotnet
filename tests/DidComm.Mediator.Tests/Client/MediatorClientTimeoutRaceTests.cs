using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The request deadline of #96 design §4.1 and the terminal CAS an answer competes on (L-044: every
/// interleaving is driven by a rendezvous or the manual clock, never by luck): the winner holds and
/// a late answer is dropped; the deadline is a timer on the injected clock whose token is cancelled
/// inside a <c>try</c>, so a throwing callback host code registered on the send's token cannot
/// escape onto a timer thread or into the caller's <c>Cancel()</c> (#92 follow-up 1); the caller's
/// cancellation is a cancellation, not a timeout.
/// </summary>
public sealed class MediatorClientTimeoutRaceTests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();
    private MediatorClientChannel _channel = null!;

    public async Task InitializeAsync()
    {
        _w = await ClientWorld.CreateAsync();
        _channel = Create(_w, _send, time: _time);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _w.DisposeAsync();
    }

    [Fact]
    public async Task When_the_deadline_wins_the_call_times_out_naming_the_request_and_the_late_answer_is_dropped()
    {
        var (request, exchange) = Start();

        _time.Advance(_channel.Options.RequestTimeout - TimeSpan.FromTicks(1));
        exchange.IsCompleted.Should().BeFalse("one tick early");
        _time.Advance(TimeSpan.FromTicks(1));

        var thrown = (await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<TimeoutException>()).Which;
        thrown.Message.Should().Contain(request.Id).And.Contain("status-request");
        foreach (var identifier in _w.Identifiers)
            thrown.Message.Should().NotContain(identifier);
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeFalse("late");
        _channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task When_the_answer_wins_a_deadline_that_fires_afterwards_changes_nothing()
    {
        var (request, exchange) = Start();
        var answer = await _w.SnapshotAsync(_w.Status(request.Id));

        _channel.AsCorrelator().OnInbound(answer).Should().BeTrue();
        _time.Advance(_channel.Options.RequestTimeout);

        (await exchange).Should().BeSameAs(answer);
    }

    [Fact]
    public async Task An_answer_that_arrives_while_the_send_is_still_running_holds_when_the_deadline_then_fires()
    {
        var sendGate = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _send.Respond = (_, _, _) => sendGate.Task; // ignores its token
        var (request, exchange) = Start();
        var answer = await _w.SnapshotAsync(_w.Status(request.Id));

        _channel.AsCorrelator().OnInbound(answer).Should().BeTrue("registered before the send, so a WebSocket answer can beat it");
        _time.Advance(_channel.Options.RequestTimeout);

        (await exchange).Should().BeSameAs(answer);
        sendGate.SetResult(FakeSend.Accepted(Wss));
    }

    [Fact]
    public async Task A_send_that_never_returns_is_abandoned_at_the_deadline()
    {
        _send.Respond = (_, _, _) => new TaskCompletionSource<SendResult>().Task; // never, ignores its token
        var (_, exchange) = Start();

        _time.Advance(_channel.Options.RequestTimeout);

        await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task A_send_abandoned_at_the_deadline_that_fails_later_is_observed_not_reported_unobserved()
    {
        var marker = "late-send-fault-" + Guid.NewGuid().ToString("N");
        var unobserved = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            if (e.Exception.ToString().Contains(marker, StringComparison.Ordinal))
                unobserved.Enqueue(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            await AbandonThenFaultTheSendAsync(marker);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            unobserved.Should().BeEmpty("the abandoned send's fault was observed");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }

    // In its own method, so nothing of the abandoned send stays reachable from the test.
    private async Task AbandonThenFaultTheSendAsync(string marker)
    {
        var send = new TaskCompletionSource<SendResult>();
        _send.Respond = (_, _, _) => send.Task; // ignores its token
        var (_, exchange) = Start();
        _time.Advance(_channel.Options.RequestTimeout);
        await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<TimeoutException>();
        _send.Respond = (_, o, _) => Task.FromResult(FakeSend.Accepted(o.ServiceEndpointOverride!));
        send.SetException(new InvalidOperationException(marker));
    }

    [Fact]
    public async Task A_throwing_callback_registered_on_the_sends_token_cannot_escape_the_timer_or_fault_the_call()
    {
        CancellationToken handed = default;
        _send.Respond = (_, _, ct) =>
        {
            handed = ct;
            ct.Register(() => throw new InvalidOperationException("host callback"));
            return Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => FakeSend.Accepted(Wss), TaskScheduler.Default);
        };
        var (_, exchange) = Start();

        var advance = () => _time.Advance(_channel.Options.RequestTimeout);

        advance.Should().NotThrow("the timer cancels inside a try");
        handed.IsCancellationRequested.Should().BeTrue();
        await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task A_throwing_callback_registered_on_the_sends_token_cannot_escape_into_the_callers_cancel()
    {
        using var cts = new CancellationTokenSource();
        _send.Respond = (_, _, ct) =>
        {
            ct.Register(() => throw new InvalidOperationException("host callback"));
            return Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => FakeSend.Accepted(Wss), TaskScheduler.Default);
        };
        var (_, exchange) = Start(cts.Token);

        var cancel = () => cts.Cancel();

        cancel.Should().NotThrow("the caller's token is not linked to the token host code sees");
        (await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task The_deadline_is_a_timer_on_the_injected_clock_and_is_released_when_the_call_ends()
    {
        var (request, exchange) = Start();

        _time.ScheduledTimers.Should().Be(1, "one deadline timer, on the injected TimeProvider (never CancelAfter)");
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeTrue();
        await exchange;

        _time.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task Cancelling_the_call_is_a_cancellation_and_a_later_answer_is_dropped()
    {
        using var cts = new CancellationTokenSource();
        var (request, exchange) = Start(cts.Token);

        await cts.CancelAsync();

        (await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(cts.Token);
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeFalse();
    }

    [Fact]
    public async Task An_answer_that_won_before_the_caller_cancelled_holds()
    {
        using var cts = new CancellationTokenSource();
        var (request, exchange) = Start(cts.Token);
        var answer = await _w.SnapshotAsync(_w.Status(request.Id));

        _channel.AsCorrelator().OnInbound(answer).Should().BeTrue();
        await cts.CancelAsync();

        (await exchange).Should().BeSameAs(answer);
    }

    [Fact]
    public async Task When_the_deadline_and_the_callers_cancellation_are_both_set_the_cancellation_wins()
    {
        using var cts = new CancellationTokenSource();
        // The rendezvous: the deadline firing cancels the caller's token too, synchronously, before
        // the exchange can observe either — both are set when it decides.
        _send.Respond = (_, _, ct) =>
        {
            ct.Register(() => cts.Cancel());
            return new TaskCompletionSource<SendResult>().Task;
        };
        var (_, exchange) = Start(cts.Token);

        _time.Advance(_channel.Options.RequestTimeout);

        cts.IsCancellationRequested.Should().BeTrue();
        (await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task A_caller_token_cancelled_before_the_call_sends_nothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _send.Count.Should().Be(0);
        _channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_duplicate_answer_after_the_first_is_dropped_and_the_first_holds()
    {
        var (request, exchange) = Start();
        var first = await _w.SnapshotAsync(_w.Status(request.Id, 1));
        var second = await _w.SnapshotAsync(_w.Status(request.Id, 2));

        _channel.AsCorrelator().OnInbound(first).Should().BeTrue();
        _channel.AsCorrelator().OnInbound(second).Should().BeFalse();

        (await exchange).Should().BeSameAs(first);
    }

    [Fact]
    public async Task Concurrent_answers_to_concurrent_requests_each_complete_their_own_request()
    {
        const int count = 32;
        var started = Enumerable.Range(0, count).Select(_ => Start()).ToArray();
        var answers = await Task.WhenAll(started.Select(s => _w.SnapshotAsync(_w.Status(s.Request.Id))));
        using var go = new ManualResetEventSlim();

        // Every answer offered from its own thread at once, against every request waiting.
        var offers = answers.Select(a => Task.Run(() =>
        {
            go.Wait();
            return _channel.AsCorrelator().OnInbound(a);
        })).ToArray();
        go.Set();

        (await Task.WhenAll(offers)).Should().OnlyContain(claimed => claimed);
        for (var i = 0; i < count; i++)
            (await started[i].Exchange).Should().BeSameAs(answers[i]);
        _channel.PendingCount.Should().Be(0);
    }

    // ---- each step has its own RequestTimeout (MediatorClientOptions.RequestTimeout, QA Q1-2) ----

    [Fact]
    public async Task Finding_the_endpoint_and_the_request_each_get_a_whole_RequestTimeout()
    {
        var (resolving, resolverCalled, channel) = SlowResolver();
        var mediation = new MediationClient(channel, logger: null);
        try
        {
            var call = mediation.RequestMediationAsync(_w.Mediator.Did, _w.Local.Did);
            await resolverCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _time.Advance(TimeSpan.FromSeconds(20));
            resolving.SetResult(Wss);
            await _send.FirstCall.WaitAsync(TimeSpan.FromSeconds(10));
            await _time.WaitForScheduledTimersAsync(1, TimeSpan.FromSeconds(10));

            _time.Advance(TimeSpan.FromSeconds(20));
            call.IsCompleted.Should().BeFalse("the request has had 20 s of its own 30 s; the 20 s spent finding the endpoint were the endpoint's");
            _time.Advance(TimeSpan.FromSeconds(10));

            var thrown = (await FluentActions.Awaiting(() => call).Should().ThrowAsync<TimeoutException>()).Which;
            thrown.Message.Should().Contain(_send.Calls.Single().Message.Id, "a request that ran out names its id");
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }

    [Fact]
    public async Task Running_out_while_finding_the_endpoint_names_no_request_and_sends_nothing()
    {
        var (_, resolverCalled, channel) = SlowResolver();
        var mediation = new MediationClient(channel, logger: null);
        try
        {
            var call = mediation.RequestMediationAsync(_w.Mediator.Did, _w.Local.Did);
            await resolverCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _time.Advance(channel.Options.RequestTimeout);

            var thrown = (await FluentActions.Awaiting(() => call).Should().ThrowAsync<TimeoutException>()).Which;
            thrown.Message.Should().Contain("Finding the mediator's endpoint").And.Contain("RequestTimeout").And.Contain("nothing was sent");
            foreach (var identifier in _w.Identifiers)
                thrown.Message.Should().NotContain(identifier);
            _send.Count.Should().Be(0);
            channel.PendingCount.Should().Be(0);
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }

    // A channel (Coordinate Mediation pinned, so one request per call) whose endpoint resolution
    // waits for the test: the resolver's result and a rendezvous for its call.
    private (TaskCompletionSource<Uri> Resolving, TaskCompletionSource Called, MediatorClientChannel Channel) SlowResolver()
    {
        var resolving = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = Create(_w, _send, c => c.CoordinateMediationVersion = CoordinateMediationVersion.V3, time: _time,
            resolveEndpoint: (_, _) =>
            {
                called.TrySetResult();
                return resolving.Task;
            });
        return (resolving, called, channel);
    }

    private (Message Request, Task<InboundMessageSnapshot> Exchange) Start(CancellationToken ct = default)
    {
        var request = _w.Request();
        var exchange = _channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, ct);
        exchange.IsCompleted.Should().BeFalse();
        return (request, exchange);
    }
}
