using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-1 (#96): the channel's correlation, bound and lifetime under permutations — a long mixed run
/// (success over each transport, timeout, cancellation, transport failure, HTTP no-answer, problem
/// report) that must leave no slot, timer or active count behind; the thread-less fallback across
/// local DIDs and mediators, ambiguity and its resolution, wrong types; late answers; the caller
/// cancelling mid-send; disposal in each phase; the Discover Features query's own timeout; the
/// request deadline's documented scope; the dispatcher's no-handler level. Every interleaving is a
/// rendezvous or the manual clock (L-044).
/// </summary>
public sealed class MediatorClientChannelQa1Tests : IAsyncLifetime
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

    // ---- the bound, over a long mixed run ----------------------------------------------------

    [Fact]
    public async Task Ten_thousand_mixed_operations_leave_no_slot_timer_or_active_count_behind()
    {
        // A thread-less grant bound to the HTTP request whose response carried it: one real envelope,
        // reused (a snapshot is immutable).
        var httpAnswer = await _w.ReceiveAsync(_w.Grant(thid: null));
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < 10_000; i++)
        {
            var kind = i % 7;
            var route = kind is 1 or 5 ? HttpRoute : WebSocketRoute;
            using var cts = new CancellationTokenSource();
            _send.Respond = kind switch
            {
                // 0: answered over the WebSocket before the send returned.
                0 => (message, options, _) =>
                {
                    _channel.AsCorrelator().OnInbound(Crafted(_w.Grant(message.Id))).Should().BeTrue();
                    return Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!));
                },
                // 1: answered in the HTTP response, without a thread.
                1 => (_, options, _) => Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!, httpAnswer, 200)),
                // 2: timeout; 3: caller cancels; both accepted with nothing back.
                2 or 3 => (_, options, _) => Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!)),
                // 4: the transport fails.
                4 => (_, _, _) => Task.FromException<SendResult>(new TransportException("down")),
                // 5: HTTP 202, no answer.
                5 => (_, options, _) => Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!)),
                // 6: a problem report naming the request in pthid.
                _ => (message, options, _) =>
                {
                    _channel.AsCorrelator().OnInbound(Crafted(_w.Problem(message.Id))).Should().BeTrue();
                    return Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!));
                },
            };

            var request = _w.Request(CoordinateMediation.MediateRequestType);
            var exchange = _channel.ExchangeAsync(route, request, ClientSpecs.MediateRequest, _w.Mediator.Did, _w.Local.Did, cts.Token);
            if (kind == 2)
                _time.Advance(_channel.Options.RequestTimeout);
            if (kind == 3)
                cts.Cancel();
            try
            {
                await exchange;
                outcomes.AddOrUpdate("answered", 1, (_, n) => n + 1);
            }
            catch (Exception ex)
            {
                outcomes.AddOrUpdate(ex.GetType().Name, 1, (_, n) => n + 1);
            }
        }

        outcomes["answered"].Should().Be(2858);
        outcomes[nameof(TimeoutException)].Should().Be(1429 + 1428, "deadline (kind 2) and HTTP no-answer (kind 5)");
        outcomes[nameof(OperationCanceledException)].Should().Be(1429);
        outcomes[nameof(TransportException)].Should().Be(1428);
        outcomes[nameof(MediationProtocolException)].Should().Be(1428);
        _channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0, "every deadline timer was released");

        // Idle again: an unbound answer is not even looked up.
        var lookups = _channel.Lookups;
        _channel.AsCorrelator().OnInbound(Crafted(_w.Grant("x"))).Should().BeFalse();
        _channel.Lookups.Should().Be(lookups);

        // Every slot is free: exactly MaxPendingRequests may wait at once, then disposal fails them all.
        _send.Respond = (_, options, _) => Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!));
        var waiting = Enumerable.Range(0, _channel.Options.MaxPendingRequests)
            .Select(_ => _channel.ExchangeAsync(WebSocketRoute, _w.Request(CoordinateMediation.MediateRequestType), ClientSpecs.MediateRequest, _w.Mediator.Did, _w.Local.Did, default))
            .ToArray();
        _channel.PendingCount.Should().Be(_channel.Options.MaxPendingRequests);
        var sends = _send.Count;
        await FluentActions.Awaiting(() => _channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default))
            .Should().ThrowAsync<InvalidOperationException>();
        _send.Count.Should().Be(sends, "refused at the bound before anything was sent");

        await _channel.DisposeAsync();
        foreach (var w in waiting)
            await FluentActions.Awaiting(() => w).Should().ThrowAsync<ObjectDisposedException>();
        _channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0);
    }

    // ---- the thread-less fallback (§4.2 row 5, unbound) ------------------------------------

    [Fact]
    public async Task Without_a_thread_two_local_DIDs_at_one_mediator_each_take_only_their_own_answer()
    {
        var (_, forLocal) = Start(_w.Local.Did);
        var (_, forOther) = Start(_w.OtherLocal.Did);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(thid: null, to: _w.OtherLocal.Did), to: _w.OtherLocal.Did)).Should().BeTrue();
        (await forOther.WaitAsync(TimeSpan.FromSeconds(10))).To.Should().Equal(_w.OtherLocal.Did);
        forLocal.IsCompleted.Should().BeFalse("not ambiguous: the pair differs");

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(thid: null))).Should().BeTrue();
        (await forLocal.WaitAsync(TimeSpan.FromSeconds(10))).To.Should().Equal(_w.Local.Did);
    }

    [Fact]
    public async Task Without_a_thread_one_local_DID_at_two_mediators_takes_each_answer_from_its_own_mediator()
    {
        // Mallory plays a second mediator here.
        var (_, atMediator) = Start(_w.Local.Did);
        var (_, atSecond) = Start(_w.Local.Did, _w.Mallory.Did);

        var fromSecond = _w.Answer(CoordinateMediation.MediateGrantType, thid: null, from: _w.Mallory.Did, body: new JsonObject { ["routing_did"] = new JsonArray(_w.Mallory.Did) });
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(fromSecond)).Should().BeTrue();
        (await atSecond.WaitAsync(TimeSpan.FromSeconds(10))).From.Should().Be(_w.Mallory.Did);
        atMediator.IsCompleted.Should().BeFalse();

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(thid: null))).Should().BeTrue();
        (await atMediator.WaitAsync(TimeSpan.FromSeconds(10))).From.Should().Be(_w.Mediator.Did);
    }

    [Fact]
    public async Task An_ambiguous_threadless_answer_is_dropped_and_once_one_request_is_answered_by_thread_the_next_threadless_one_takes_the_other()
    {
        var (first, firstExchange) = Start(_w.Local.Did);
        var (_, secondExchange) = Start(_w.Local.Did);

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(thid: null))).Should().BeFalse("two candidates: ambiguous");
        firstExchange.IsCompleted.Should().BeFalse();
        secondExchange.IsCompleted.Should().BeFalse();

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(first.Id))).Should().BeTrue();
        await firstExchange.WaitAsync(TimeSpan.FromSeconds(10));

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(thid: null))).Should().BeTrue("one candidate left");
        await secondExchange.WaitAsync(TimeSpan.FromSeconds(10));
        _channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_threadless_answer_of_a_type_no_waiting_request_expects_is_dropped_and_the_request_keeps_waiting()
    {
        var (_, exchange) = Start(_w.Local.Did);

        var wrongType = _w.Answer(CoordinateMediation.RecipientUpdateResponseType, thid: null, body: new JsonObject { ["updated"] = new JsonArray() });
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(wrongType)).Should().BeFalse();
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(thid: null))).Should().BeFalse();

        exchange.IsCompleted.Should().BeFalse();
        _time.Advance(_channel.Options.RequestTimeout);
        await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task A_threadless_disclose_completes_the_only_waiting_Discover_Features_query()
    {
        var query = FeatureNegotiation.CreateQuery(_w.Local.Did, _w.Mediator.Did);
        var exchange = _channel.ExchangeAsync(WebSocketRoute, query, FeatureNegotiation.QuerySpec, _w.Mediator.Did, _w.Local.Did, default);
        var disclose = _w.Answer(DiscoverFeatures.DiscloseType, thid: null, body: new JsonObject { ["disclosures"] = new JsonArray() });

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(disclose)).Should().BeTrue();

        (await exchange.WaitAsync(TimeSpan.FromSeconds(10))).Type.Should().Be(DiscoverFeatures.DiscloseType);
    }

    // ---- late answers, cancellation, disposal ------------------------------------------------

    [Fact]
    public async Task A_late_answer_after_the_timeout_is_dropped_quietly_and_the_channel_is_idle_again()
    {
        var (request, exchange) = Start(_w.Local.Did);
        _time.Advance(_channel.Options.RequestTimeout);
        await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<TimeoutException>();

        var lookups = _channel.Lookups;
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(request.Id))).Should().BeFalse();
        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(thid: null))).Should().BeFalse();
        _channel.Lookups.Should().Be(lookups, "idle: no request waits, nothing is looked up");
        _channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task The_caller_cancelling_mid_send_over_HTTP_is_a_cancellation_and_the_answer_that_comes_back_afterwards_is_dropped()
    {
        var gate = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken handed = default;
        _send.Respond = (_, _, token) =>
        {
            handed = token;
            return gate.Task; // ignores its token
        };
        using var cts = new CancellationTokenSource();
        var request = _w.Request(CoordinateMediation.MediateRequestType);
        var exchange = _channel.ExchangeAsync(HttpRoute, request, ClientSpecs.MediateRequest, _w.Mediator.Did, _w.Local.Did, cts.Token);
        await _send.FirstCall;

        cts.Cancel();

        await FluentActions.Awaiting(() => exchange).Should().ThrowAsync<OperationCanceledException>();
        handed.IsCancellationRequested.Should().BeTrue("the send's token follows the caller's");
        _channel.PendingCount.Should().Be(0);
        gate.SetResult(FakeSend.Accepted(Https, await _w.ReceiveAsync(_w.Grant(request.Id)), 200));
        _channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task Disposal_fails_a_request_in_its_send_and_one_waiting_for_its_answer_and_refuses_new_ones_without_sending()
    {
        var gate = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _send.Respond = (_, _, _) => gate.Task;
        var inSend = _channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
        await _send.FirstCall;
        _send.Respond = (_, options, _) => Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!));
        var waiting = _channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
        _channel.PendingCount.Should().Be(2);

        _channel.Dispose();

        await FluentActions.Awaiting(() => inSend).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<ObjectDisposedException>();
        var sends = _send.Count;
        await FluentActions.Awaiting(() => _channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default))
            .Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => _channel.ResolveRouteAsync(_w.Mediator.Did, Wss, false, default)).Should().ThrowAsync<ObjectDisposedException>();
        _send.Count.Should().Be(sends);
        _channel.PendingCount.Should().Be(0);
        gate.SetResult(FakeSend.Accepted(Wss));
    }

    // ---- the Discover Features query's own timeout ------------------------------------------

    [Fact]
    public async Task Over_a_WebSocket_an_unanswered_Discover_Features_query_times_out_naming_the_query_and_nothing_else_is_sent()
    {
        var channel = Create(_w, _send, time: _time, resolveEndpoint: (_, _) => Task.FromResult(Wss));
        var mediation = new MediationClient(channel, logger: null);

        var call = mediation.RequestMediationAsync(_w.Mediator.Did, _w.Local.Did);
        await _send.FirstCall;
        await _time.WaitForScheduledTimersAsync(1, TimeSpan.FromSeconds(10));
        _time.Advance(channel.Options.RequestTimeout);

        var thrown = (await FluentActions.Awaiting(() => call).Should().ThrowAsync<TimeoutException>()).Which;
        var query = _send.Calls.Should().ContainSingle("no version is guessed").Subject;
        query.Message.Type.Should().Be(DiscoverFeatures.QueriesType);
        query.Options.ReturnRoute.Should().Be(Transports.ReturnRoute.ReturnRouteMode.All);
        thrown.Message.Should().Contain(query.Message.Id);
        foreach (var identifier in _w.Identifiers)
            thrown.Message.Should().NotContain(identifier);
        channel.Versions.TryGet(_w.Mediator.Did, out _).Should().BeFalse();
        await channel.DisposeAsync();
    }

    // ---- unobserved task exceptions ----------------------------------------------------------

    /// <summary>
    /// Q1-3. Over HTTP, a response without an answer fails the request through
    /// <c>PendingRequest.TryFail(noAnswer)</c> — which faults the pending request's
    /// <c>TaskCompletionSource</c> with the <see cref="TimeoutException"/> — and then throws the
    /// same exception directly (<c>MediatorClientChannel.ExchangeCoreAsync</c>), never awaiting or
    /// observing the faulted task. Every HTTP no-answer (a 202, an identity-refused or
    /// non-correlating response, FR-RR-04/R-7) therefore leaves a faulted task that the finalizer
    /// reports through <see cref="TaskScheduler.UnobservedTaskException"/>, which hosts log as an
    /// error and which this repository's own "without unobserved exceptions" tests catch (they fail
    /// in a full interop run: <c>InboundDispatchDiTests.Disposing_the_container_ends_session_loops_without_unobserved_exceptions</c>
    /// saw "Mediator request '…' (mediate-request) got no answer in the HTTP response").
    /// </summary>
    [Fact]
    public async Task Q1_3_An_HTTP_response_without_an_answer_leaves_no_unobserved_task_exception()
    {
        var ids = new ConcurrentBag<string>();
        var seen = new ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            foreach (var ex in e.Exception.InnerExceptions)
                seen.Enqueue(ex);
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            for (var i = 0; i < 20; i++)
                ids.Add(await RunHttpNoAnswerAsync());
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            seen.Where(ex => ids.Any(id => ex.Message.Contains(id, StringComparison.Ordinal)))
                .Should().BeEmpty("the failure was thrown to the caller; nothing may be left for the finalizer to report");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    /// <summary>
    /// Q1-3 (second path). <c>Dispose</c> fails every waiting request with
    /// <c>PendingRequest.TryFail(ObjectDisposedException)</c>. A request still in its send at that
    /// moment never awaits its completion task: the shutdown cancels the send, and
    /// <c>Unanswered</c> throws a fresh <see cref="ObjectDisposedException"/>, so the one stored in the
    /// faulted completion task is never observed and is reported by the finalizer.
    /// </summary>
    [Fact]
    public async Task Q1_3_Disposal_during_a_send_leaves_no_unobserved_task_exception()
    {
        var seen = new ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            foreach (var ex in e.Exception.InnerExceptions)
                seen.Enqueue(ex);
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            for (var i = 0; i < 20; i++)
                await RunDisposedDuringSendAsync();
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // Only exceptions that were stored and never thrown (no stack trace) are this channel's
            // leak; a thrown one that another test running in parallel left unawaited is not.
            seen.OfType<ObjectDisposedException>().Where(ex => ex.ObjectName == nameof(MediatorClientChannel) && ex.StackTrace is null)
                .Should().BeEmpty("the caller saw the disposal; nothing may be left for the finalizer to report");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<string> RunHttpNoAnswerAsync()
    {
        _send.Respond = (_, options, _) => Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!));
        var request = _w.Request();
        try
        {
            await _channel.ExchangeAsync(HttpRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
        }
        catch (TimeoutException)
        {
        }
        return request.Id;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task RunDisposedDuringSendAsync()
    {
        var send = new FakeSend { Respond = (_, _, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<SendResult>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default) };
        var channel = Create(_w, send, time: _time);
        var exchange = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
        await send.FirstCall;
        channel.Dispose();
        try
        {
            await exchange;
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ---- the request deadline's documented scope ---------------------------------------------

    // Q1-2 (retired by the orchestrator: doc fixed). The documented contract is now "each step —
    // finding the endpoint, the version query, the request — has its own RequestTimeout"; the code
    // always behaved that way. Pinned by MediatorClientTimeoutRaceTests
    // (Finding_the_endpoint_and_the_request_each_get_a_whole_RequestTimeout,
    // Running_out_while_finding_the_endpoint_names_no_request_and_sends_nothing) and
    // MediationClientTimeoutTests.

    // ---- the dispatcher's no-handler level (R-3) ----------------------------------------------

    [Theory]
    [InlineData("claimed", LogLevel.Debug)]
    [InlineData("late answer", LogLevel.Information)]
    [InlineData("not an answer type", LogLevel.Information)]
    [InlineData("answer for another local DID", LogLevel.Information)]
    public async Task The_no_handler_line_drops_to_Debug_only_when_the_channel_claimed_the_message(string because, LogLevel expected)
    {
        var logs = new ClientLogCapture();
        var secrets = new InMemorySecretsResolver();
        var collection = new ServiceCollection();
        collection.AddLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        collection.AddSingleton<IDidCommTransport>(new AcceptingWebSocketTransport());
        collection.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.Configure(o => o.OutboundEndpointPolicy.ResolveDnsNames = false);
            b.UseInboundDispatch();
            b.AddMediatorClient();
        });
        await using var sp = collection.BuildServiceProvider();
        var client = sp.GetRequiredService<DidCommClient>();
        async Task<PeerIdentity> Mint()
        {
            var peer = await PeerIdentityFactory.CreateAsync(sp.GetRequiredService<IDidManager>(), sp.GetRequiredService<IKeyGenerator>(), sp.GetRequiredService<ICryptoProvider>());
            foreach (var jwk in peer.Privates)
                secrets.Add(jwk);
            return peer;
        }
        var mediator = await Mint();
        var local = await Mint();
        var other = await Mint();
        var channel = sp.GetRequiredService<MediatorClientChannel>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();

        // One request waits (except for the late-answer case, which has none).
        var request = MessagePickup.CreateStatusRequest(local.Did, mediator.Did);
        var route = new MediatorRoute(new Uri("wss://mediator.example/ws"), false, true);
        Task<InboundMessageSnapshot>? exchange = null;
        using var cts = new CancellationTokenSource();
        if (because != "late answer")
            exchange = channel.ExchangeAsync(route, request, ClientSpecs.StatusRequest, mediator.Did, local.Did, cts.Token);

        Message answer = because switch
        {
            "not an answer type" => new MessageBuilder().WithType("https://didcomm.org/basicmessage/2.0/message").WithFrom(mediator.Did).WithTo(local.Did).WithBody(new JsonObject { ["content"] = "hi" }).Build(),
            "answer for another local DID" => MessagePickup.CreateStatus(mediator.Did, other.Did, new PickupStatus(0), thid: request.Id),
            _ => MessagePickup.CreateStatus(mediator.Did, local.Did, new PickupStatus(0), thid: request.Id),
        };
        var to = because == "answer for another local DID" ? other.Did : local.Did;
        var packed = await client.PackEncryptedAsync(answer, new PackEncryptedOptions(Recipients: new[] { to }, From: mediator.Did));
        var unpacked = await client.UnpackAsync(packed.Message);

        var outcome = await dispatcher.DispatchAsync(unpacked, client, sp.GetRequiredService<IOptions<DidCommOptions>>().Value);

        outcome.Result.Should().Be(DispatchResult.NoHandler);
        logs.Entries.Where(e => e.Formatted.Contains("No protocol handler registered", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Level.Should().Be(expected, because);
        if (exchange is not null)
        {
            if (because == "claimed")
            {
                (await exchange.WaitAsync(TimeSpan.FromSeconds(10))).Thid.Should().Be(request.Id);
                return;
            }
            exchange.IsCompleted.Should().BeFalse("an unclaimed message completes nothing");
            cts.Cancel();
            try
            {
                await exchange;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>A wss transport that accepts every request (the answer is dispatched by the test).</summary>
    private sealed class AcceptingWebSocketTransport : IDidCommTransport
    {
        public string Scheme => "wss";

        public bool CanHandle(Uri endpoint) => string.Equals(endpoint.Scheme, "wss", StringComparison.OrdinalIgnoreCase);

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
            => Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: null));
    }

    private (Message Request, Task<InboundMessageSnapshot> Exchange) Start(string localDid, string? mediatorDid = null)
    {
        var request = _w.Request(CoordinateMediation.MediateRequestType, from: localDid, to: mediatorDid ?? _w.Mediator.Did);
        return (request, _channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.MediateRequest, mediatorDid ?? _w.Mediator.Did, localDid, default));
    }

    // A verified snapshot from the mediator to the local DID, as unpack registers it, without the cost
    // of real cryptography (the matcher reads only the trust fields).
    private InboundMessageSnapshot Crafted(Message message)
        => CraftedSnapshot.Verified(message, senderKid: _w.Mediator.Did + "#key-x", recipientKid: _w.Local.Did + "#key-y");
}
