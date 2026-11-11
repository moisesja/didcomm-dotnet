using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using DidComm.AspNetCore;
using DidComm.Consistency;
using DidComm.Facade;
using DidComm.InteropTests.Transports;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #91 F12 — the two internal session types in isolation, with real did:peer packing: the HTTP
/// session's single slot (one message, claimed before packing, released on a failed pack, closed
/// and taken atomically — PA-2), the FR-RR-03 addressing rule and the snapshot, and the WebSocket
/// session's bounded writer queue, single writer, downgrade, eviction close and fault handling
/// against a fake socket whose sends can be parked.
/// </summary>
public sealed class ReturnRouteSessionTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly InMemorySecretsResolver _serverSecrets = new();
    private readonly CapturingLoggerProvider _logs = new();
    private string _serverDid = string.Empty;
    private TestPeer _alice = null!;
    private TestPeer _carol = null!;
    private TestPeer _mallory = null!;
    private GatedKeyService _keys = null!;
    private DidCommClient _client = null!;

    public async Task InitializeAsync()
    {
        var (server, alice, carol, mallory) = await ReturnRouteTestHost.CreateIdentitiesAsync(_serverSecrets);
        _serverDid = server.Did;
        _alice = alice;
        _carol = carol;
        _mallory = mallory;
        _keys = new GatedKeyService(alice.Services.GetRequiredService<IDidKeyService>());
        _client = new DidCommClient(_serverSecrets, _keys, new DidCommOptions());
    }

    public async Task DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _carol.DisposeAsync();
        await _mallory.DisposeAsync();
    }

    // ---- HTTP session -------------------------------------------------------------------------

    [Fact]
    public async Task Http_second_send_returns_false_and_the_response_is_exactly_the_first_message()
    {
        var session = NewHttpSession();
        var first = ToAlice("first");
        var second = ToAlice("second");

        (await session.TrySendAsync(first, default)).Should().BeTrue();
        (await session.TrySendAsync(second, default)).Should().BeFalse("HTTP returns at most one message per request (FR-RR-04)");
        session.HasResponse.Should().BeTrue();

        var body = session.TakeResponseAndClose();
        body.Should().NotBeNull();
        var unpacked = await _alice.Client.UnpackAsync(Encoding.UTF8.GetString(body!));
        unpacked.Message.Id.Should().Be(first.Id);
        unpacked.Authenticated.Should().BeTrue();
        unpacked.SenderKid.Should().StartWith(_serverDid + "#");
        session.TakeResponseAndClose().Should().BeNull("taking the response is one-shot");
    }

    [Fact]
    public async Task Http_send_after_close_returns_false_and_Closed_is_cancelled()
    {
        var session = NewHttpSession();
        session.Mode.Should().Be(ReturnRouteMode.All);
        session.Transport.Should().Be(ReturnRouteTransport.Http);
        session.IsLive.Should().BeFalse();
        session.Thread.Should().BeNull();

        session.TakeResponseAndClose().Should().BeNull("nothing was sent");

        session.Closed.IsCancellationRequested.Should().BeTrue();
        session.Mode.Should().Be(ReturnRouteMode.None);
        (await session.TrySendAsync(ToAlice("late"), default)).Should().BeFalse();
        _keys.Calls.Should().Be(0, "a closed session costs no crypto");
    }

    [Fact]
    public async Task Http_addressing_rule_refuses_anything_but_local_to_exactly_the_peer()
    {
        // FR-RR-03: from the local DID to exactly [peer], compared as DID subjects.
        (await NewHttpSession().TrySendAsync(Message(_serverDid, _carol.Did), default)).Should().BeFalse("wrong to");
        (await NewHttpSession().TrySendAsync(Message(_serverDid, _alice.Did, _carol.Did), default)).Should().BeFalse("two recipients");
        (await NewHttpSession().TrySendAsync(Message(_carol.Did, _alice.Did), default)).Should().BeFalse("wrong from");
        (await NewHttpSession().TrySendAsync(Message(null, _alice.Did), default)).Should().BeFalse("missing from");
        _keys.Calls.Should().Be(0, "a refused message is never packed");

        _logs.Entries.Should().Contain(e => e.Contains(nameof(ReturnRouteSendRefusal.ToNotExactlyPeer)));
        _logs.Entries.Should().Contain(e => e.Contains(nameof(ReturnRouteSendRefusal.FromNotLocalDid)));
        _logs.Entries.Should().Contain(e => e.Contains(nameof(ReturnRouteSendRefusal.NoFrom)));

        // A DID URL of the peer's own subject in `to` is the same peer (L-034; `to` may not carry a
        // fragment, FR-MSG-07). NetDidKeyService refuses any DID URL in a message's to/from at pack and
        // unpack time, so both ends here check the method on the DID subject instead — the property
        // under test is the session's subject comparison, not that resolver's strictness. (The
        // addressing rule's DID-URL `from` acceptance is pinned by ReturnRouteRulesTests; a DID-URL
        // `from` cannot survive the receiver's FR-CONSIST-06 resolver check with NetDid.)
        _keys.CheckMethodOnSubject = true;
        var session = NewHttpSession();
        (await session.TrySendAsync(Message(_serverDid, _alice.Did + "?src=test"), default))
            .Should().BeTrue("a DID URL of the session peer's subject names the same peer");
        var aliceLenient = new DidCommClient(
            _alice.Secrets,
            new GatedKeyService(_alice.Services.GetRequiredService<IDidKeyService>()) { CheckMethodOnSubject = true },
            new DidCommOptions());
        var unpacked = await aliceLenient.UnpackAsync(Encoding.UTF8.GetString(session.TakeResponseAndClose()!));
        unpacked.SenderKid.Should().StartWith(_serverDid + "#", "it is authcrypted from the local DID");
        unpacked.RecipientKid.Should().StartWith(_alice.Did + "#", "it is encrypted to the peer's own keys");

        NoLeaks();
    }

    [Fact]
    public async Task Http_the_message_is_snapshotted_before_the_pack_so_a_racing_mutation_cannot_redirect_it()
    {
        var session = NewHttpSession();
        var reply = ToAlice("snapshot");
        _keys.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var send = session.TrySendAsync(reply, default).AsTask();
        await _keys.Entered.Task.WaitAsync(Timeout);

        // The caller keeps mutating its instance while the pack is parked.
        reply.To = new List<string> { _mallory.Did };
        reply.From = _carol.Did;
        _keys.Gate.SetResult();

        (await send.WaitAsync(Timeout)).Should().BeTrue();
        var unpacked = await _alice.Client.UnpackAsync(Encoding.UTF8.GetString(session.TakeResponseAndClose()!));
        unpacked.Message.To.Should().Equal(_alice.Did);
        unpacked.Message.From.Should().Be(_serverDid);
    }

    [Fact]
    public async Task Http_a_pack_failure_returns_false_and_logs_the_exception_type_only()
    {
        // The recipient DID cannot be resolved (no such method), so the pack throws inside TrySendAsync.
        const string unresolvable = "did:example:nobody";
        var session = new HttpReturnRouteSession(_client, unresolvable, _serverDid, _logs.CreateLogger("test"));

        (await session.TrySendAsync(Message(_serverDid, unresolvable), default)).Should().BeFalse();

        session.HasResponse.Should().BeFalse();
        _logs.Entries.Should().Contain(e => e.Contains("pack failed with") && e.Contains("Exception"));
        _logs.Entries.Should().NotContain(e => e.Contains(unresolvable), "the log carries the exception type name, never a DID");
        NoLeaks();
    }

    [Fact]
    public async Task Http_a_failed_pack_releases_the_slot_for_the_next_sender()
    {
        var session = NewHttpSession();
        _keys.FailNext = true;

        (await session.TrySendAsync(ToAlice("fails"), default)).Should().BeFalse("the pack failed");
        session.HasResponse.Should().BeFalse();

        var reply = ToAlice("reply");
        (await session.TrySendAsync(reply, default)).Should().BeTrue("the failed sender released its claim, so the handler's reply can still use the slot");
        var unpacked = await _alice.Client.UnpackAsync(Encoding.UTF8.GetString(session.TakeResponseAndClose()!));
        unpacked.Message.Id.Should().Be(reply.Id);
    }

    [Fact]
    public async Task Http_only_one_concurrent_sender_packs()
    {
        var session = NewHttpSession();
        var winner = ToAlice("winner");
        _keys.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = session.TrySendAsync(winner, default).AsTask();
        await _keys.Entered.Task.WaitAsync(Timeout);
        var callsWhileParked = _keys.Calls;

        // The slot is claimed by the parked sender: the second answers false at once, without packing.
        (await session.TrySendAsync(ToAlice("loser"), default).AsTask().WaitAsync(TimeSpan.FromSeconds(1))).Should().BeFalse();
        _keys.Calls.Should().Be(callsWhileParked, "the losing sender never reached the pack");

        _keys.Gate.SetResult();
        (await first.WaitAsync(Timeout)).Should().BeTrue();
        var unpacked = await _alice.Client.UnpackAsync(Encoding.UTF8.GetString(session.TakeResponseAndClose()!));
        unpacked.Message.Id.Should().Be(winner.Id);
    }

    [Fact]
    public async Task Http_a_send_still_packing_when_the_endpoint_takes_the_response_returns_false()
    {
        // PA-2: a handler's background task holds context.Session and is still packing when the
        // endpoint's last step closes the session and takes the (empty) response. Its pack completes
        // afterwards: it must report false, and its bytes must never surface.
        var session = NewHttpSession();
        _keys.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var background = session.TrySendAsync(ToAlice("late-push"), default).AsTask();
        await _keys.Entered.Task.WaitAsync(Timeout);

        session.TakeResponseAndClose().Should().BeNull("the push had not completed when the response was taken");
        session.Closed.IsCancellationRequested.Should().BeTrue();

        _keys.Gate.SetResult();
        (await background.WaitAsync(Timeout)).Should().BeFalse("a pack that completes after the close is never reported as sent");
        session.HasResponse.Should().BeFalse();
        session.TakeResponseAndClose().Should().BeNull("the late bytes were discarded, not stored");
    }

    [Fact]
    public async Task Http_cancellation_on_the_callers_token_propagates_and_releases_the_slot()
    {
        var session = NewHttpSession();
        _keys.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var cancelled = session.TrySendAsync(ToAlice("cancelled"), cts.Token).AsTask();
        await _keys.Entered.Task.WaitAsync(Timeout);
        cts.Cancel();

        var awaitCancelled = async () => await cancelled.WaitAsync(Timeout);
        await awaitCancelled.Should().ThrowAsync<OperationCanceledException>("cancellation on the caller's token propagates");
        _keys.Gate.SetResult();
        _keys.Gate = null;
        (await session.TrySendAsync(ToAlice("after"), default)).Should().BeTrue("the cancelled sender released its claim");
    }

    // ---- WebSocket session --------------------------------------------------------------------

    [Fact]
    public async Task Ws_a_full_queue_answers_false_without_blocking_and_one_writer_sends()
    {
        var socket = new FakeWebSocket { SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection, maxPendingPushes: 2);
        session.Start();

        (await session.TrySendAsync(ToAlice("p1"), default)).Should().BeTrue();
        await socket.SendEntered.Task.WaitAsync(Timeout); // the writer took p1 and is parked inside SendAsync

        (await session.TrySendAsync(ToAlice("p2"), default)).Should().BeTrue();
        (await session.TrySendAsync(ToAlice("p3"), default)).Should().BeTrue();
        (await session.TrySendAsync(ToAlice("p4"), default).AsTask().WaitAsync(TimeSpan.FromSeconds(1)))
            .Should().BeFalse("the queue holds MaxPendingPushes packed messages; one more answers false at once");

        socket.SendGate.SetResult();
        (await ReturnRouteTestHost.WaitUntilAsync(() => socket.Frames.Count == 3, Timeout)).Should().BeTrue();
        socket.MaxConcurrentSends.Should().Be(1, "the writer task is the only sender");
        var types = new List<string>();
        foreach (var frame in socket.Frames)
            types.Add((await _alice.Client.UnpackAsync(Encoding.UTF8.GetString(frame))).Message.Type);
        types.Should().Equal(TestType("p1"), TestType("p2"), TestType("p3"));
        socket.MessageTypes.Should().AllSatisfy(t => t.Should().Be(WebSocketMessageType.Binary));

        (await session.StopAsync()).Should().BeTrue();
        socket.CloseOutputs.Should().BeEmpty("only an eviction sends a close frame from the writer");
    }

    [Fact]
    public async Task Ws_eviction_discards_queued_pushes_sends_one_normal_close_and_arms_the_backstop()
    {
        var socket = new FakeWebSocket { SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection, maxPendingPushes: 4, closeGrace: TimeSpan.FromMilliseconds(200));
        session.Start();

        (await session.TrySendAsync(ToAlice("in-flight"), default)).Should().BeTrue();
        await socket.SendEntered.Task.WaitAsync(Timeout);
        (await session.TrySendAsync(ToAlice("queued-1"), default)).Should().BeTrue();
        (await session.TrySendAsync(ToAlice("queued-2"), default)).Should().BeTrue();

        session.RequestClose(SessionCloseReason.Evicted);

        session.Closed.IsCancellationRequested.Should().BeTrue();
        session.Mode.Should().Be(ReturnRouteMode.None);
        session.CloseReason.Should().Be(SessionCloseReason.Evicted);
        (await session.TrySendAsync(ToAlice("after-close"), default)).Should().BeFalse();

        socket.SendGate.SetResult();
        (await session.StopAsync()).Should().BeTrue();

        socket.Frames.Should().HaveCount(1, "only the frame already being written goes out; queued pushes are discarded");
        socket.CloseOutputs.Should().ContainSingle().Which.Should().Be((WebSocketCloseStatus.NormalClosure, WebSocketReturnRouteSession.EvictedDescription));
        session.CloseReason.Should().Be(SessionCloseReason.Evicted, "StopAsync does not overwrite the first reason");

        // JC-21 backstop: the fake peer never answers the close frame, so the connection is cancelled after the grace.
        (await ReturnRouteTestHost.WaitUntilAsync(() => connection.IsCancellationRequested, Timeout)).Should().BeTrue();
    }

    [Fact]
    public async Task Ws_a_downgraded_session_refuses_without_packing_and_all_resumes()
    {
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(new FakeWebSocket(), connection);
        session.Start();

        session.ApplyHeader(ReturnRouteHeader.None);
        session.Mode.Should().Be(ReturnRouteMode.None);
        (await session.TrySendAsync(ToAlice("refused"), default)).Should().BeFalse();
        _keys.Calls.Should().Be(0, "a downgraded session costs no crypto");

        session.ApplyHeader(new ReturnRouteHeader(ReturnRouteMode.Thread, "thread-1"));
        session.Mode.Should().Be(ReturnRouteMode.None, "thread is treated as none for the session (JC-20)");
        session.Thread.Should().Be("thread-1", "the thread is surfaced");
        (await session.TrySendAsync(ToAlice("still-refused"), default)).Should().BeFalse();
        _keys.Calls.Should().Be(0);

        session.ApplyHeader(new ReturnRouteHeader(ReturnRouteMode.All, null));
        session.Mode.Should().Be(ReturnRouteMode.All);
        session.Thread.Should().BeNull();
        (await session.TrySendAsync(ToAlice("resumed"), default)).Should().BeTrue();

        (await session.StopAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Ws_a_closed_session_never_reopens()
    {
        // PA-3: once closed, a later `all` from the same pair changes nothing.
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(new FakeWebSocket(), connection);
        session.Start();

        session.RequestClose(SessionCloseReason.Unregistered);
        session.ApplyHeader(new ReturnRouteHeader(ReturnRouteMode.All, null));

        session.Mode.Should().Be(ReturnRouteMode.None);
        session.IsClosed.Should().BeTrue();
        (await session.TrySendAsync(ToAlice("ignored"), default)).Should().BeFalse();
        _keys.Calls.Should().Be(0);
        (await session.StopAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Ws_a_writer_fault_closes_the_session_and_cancels_the_connection()
    {
        var socket = new FakeWebSocket { SendFault = new WebSocketException("simulated broken socket") };
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection);
        session.Start();

        (await session.TrySendAsync(ToAlice("doomed"), default)).Should().BeTrue("queued; the fault happens in the writer");

        (await ReturnRouteTestHost.WaitUntilAsync(() => connection.IsCancellationRequested, Timeout)).Should().BeTrue();
        session.IsClosed.Should().BeTrue();
        session.Closed.IsCancellationRequested.Should().BeTrue();
        _logs.Entries.Should().Contain(e => e.Contains("writer failed with WebSocketException"));
        _logs.Entries.Should().NotContain(e => e.Contains("simulated broken socket"), "exception text is never logged");
        (await session.StopAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Ws_StopAsync_cancels_a_send_stuck_on_a_peer_that_is_not_reading()
    {
        var socket = new FakeWebSocket { SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection, closeGrace: TimeSpan.FromMilliseconds(200));
        session.Start();

        (await session.TrySendAsync(ToAlice("stuck"), default)).Should().BeTrue();
        await socket.SendEntered.Task.WaitAsync(Timeout);

        (await session.StopAsync().WaitAsync(Timeout)).Should().BeTrue("the stuck send is cancelled through the connection token, so the writer stops");
        connection.IsCancellationRequested.Should().BeTrue();
        socket.Frames.Should().BeEmpty();
    }

    [Fact]
    public async Task Ws_eviction_while_the_writer_is_stuck_in_a_send_still_ends_the_connection_within_the_grace()
    {
        // QA #91: the JC-21 backstop used to be armed only after the eviction close frame was sent,
        // and the writer sends it only after its current send completes. A peer that stops reading
        // keeps that send pending forever, so the evicted connection never ended (FR-RR-03: eviction
        // closes the socket). The backstop is now armed at eviction.
        var socket = new FakeWebSocket { SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var connection = new CancellationTokenSource();
        var grace = TimeSpan.FromMilliseconds(200);
        var session = NewWebSocketSession(socket, connection, closeGrace: grace);
        session.Start();

        (await session.TrySendAsync(ToAlice("stuck"), default)).Should().BeTrue();
        await socket.SendEntered.Task.WaitAsync(Timeout); // the gate is never released: the peer does not read

        session.RequestClose(SessionCloseReason.Evicted);

        (await ReturnRouteTestHost.WaitUntilAsync(() => connection.IsCancellationRequested, grace + TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the eviction backstop ends the connection even though the writer never reached the close frame");
        (await ReturnRouteTestHost.WaitUntilAsync(() => session.WriterStopped, Timeout)).Should().BeTrue("the cancelled send releases the writer");
        (await session.StopAsync().WaitAsync(Timeout)).Should().BeTrue();
        socket.Frames.Should().BeEmpty();
    }

    [Fact]
    public async Task Ws_no_send_starts_after_a_downgrade_was_applied_even_while_the_writer_drains_its_queue()
    {
        // FR-RR-07 / judgment call 3: "the receiver pushes nothing after it processes the None". The
        // invariant must hold for every interleaving of the writer draining queued pushes and the
        // receive loop applying `none`: once ApplyHeader(None) has returned, no SendAsync may begin.
        // QA #91 regression: the writer read the mode and then started the send without excluding
        // ApplyHeader, so a downgrade landing between the two let one more frame out.
        // Each round parks the writer in a send, queues a full batch, then releases the writer while a
        // second thread follows its progress and applies `none` right after the k-th frame, a few
        // spins later each round, so the downgrade sweeps the writer's check-then-send step.
        const int Rounds = 60;
        const int Queued = 8;
        var random = new Random(91);
        var landedMidDrain = 0;
        for (var round = 0; round < Rounds; round++)
        {
            var downgraded = 0;
            var socket = new FakeWebSocket
            {
                SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                AtSendEntry = () => Volatile.Read(ref downgraded) == 1,
            };
            using var connection = new CancellationTokenSource();
            var session = NewWebSocketSession(socket, connection, maxPendingPushes: Queued);
            session.Start();

            (await session.TrySendAsync(ToAlice("parked"), default)).Should().BeTrue();
            await socket.SendEntered.Task.WaitAsync(Timeout);
            for (var i = 0; i < Queued; i++)
                (await session.TrySendAsync(ToAlice("queued-" + i), default)).Should().BeTrue();

            var k = 1 + (round % 4);
            var spin = random.Next(0, 64);
            var ready = new ManualResetEventSlim();
            var downgrader = Task.Factory.StartNew(() =>
            {
                ready.Set();
                var deadline = DateTime.UtcNow + Timeout;
                while (socket.Frames.Count < k && DateTime.UtcNow < deadline)
                    Thread.SpinWait(1);
                Thread.SpinWait(spin);
                session.ApplyHeader(ReturnRouteHeader.None);
                Volatile.Write(ref downgraded, 1);
            }, TaskCreationOptions.LongRunning);
            ready.Wait(Timeout).Should().BeTrue();

            socket.SendGate.SetResult();
            await downgrader.WaitAsync(Timeout);
            (await session.StopAsync().WaitAsync(Timeout)).Should().BeTrue();

            socket.SendsAfterFlag.Should().Be(0, "round {0}: a send began after ApplyHeader(None) returned", round);
            if (socket.Frames.Count < Queued + 1)
                landedMidDrain++;
        }

        landedMidDrain.Should().BeGreaterThan(Rounds / 2, "the downgrade must overlap the drain (L-044): some queued pushes are discarded");
    }

    [Fact]
    public async Task Ws_StopAsync_reports_false_when_the_writer_ignores_cancellation()
    {
        // A socket whose send never observes the token: StopAsync gives up after the grace twice and
        // answers false, so the receive loop aborts the socket instead of closing it over a send.
        var socket = new FakeWebSocket
        {
            SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true,
        };
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(socket, connection, closeGrace: TimeSpan.FromMilliseconds(100));
        session.Start();

        (await session.TrySendAsync(ToAlice("wedged"), default)).Should().BeTrue();
        await socket.SendEntered.Task.WaitAsync(Timeout);

        (await session.StopAsync().WaitAsync(Timeout)).Should().BeFalse("the writer could not be stopped");
        connection.IsCancellationRequested.Should().BeTrue("the connection was cancelled before giving up");
        session.WriterStopped.Should().BeFalse();
        _logs.Entries.Should().Contain(e => e.Contains("did not stop within the close grace"));

        socket.SendGate.SetResult(); // let the wedged send finish so the writer task ends
        (await ReturnRouteTestHost.WaitUntilAsync(() => session.WriterStopped, Timeout)).Should().BeTrue();
    }

    [Fact]
    public async Task Ws_a_session_that_was_never_started_stops_at_once()
    {
        using var connection = new CancellationTokenSource();
        var session = NewWebSocketSession(new FakeWebSocket(), connection);

        (await session.StopAsync().WaitAsync(Timeout)).Should().BeTrue();
        session.IsClosed.Should().BeTrue();
        connection.IsCancellationRequested.Should().BeFalse();
    }

    // ---- helpers ------------------------------------------------------------------------------

    private HttpReturnRouteSession NewHttpSession()
        => new(_client, _alice.Did, _serverDid, _logs.CreateLogger("test"));

    private WebSocketReturnRouteSession NewWebSocketSession(
        FakeWebSocket socket, CancellationTokenSource connection, int maxPendingPushes = 16, TimeSpan? closeGrace = null)
        => new(socket, _client, _alice.Did, _serverDid, maxPendingPushes, connection, _logs.CreateLogger("test"), closeGrace ?? TimeSpan.FromSeconds(2));

    private static string TestType(string name) => "https://didcomm.org/test-rr/1.0/" + name;

    private Message ToAlice(string name) => Message(_serverDid, _alice.Did, name: name);

    private static Message Message(string? from, string to, string? to2 = null, string name = "m")
    {
        var builder = new MessageBuilder().WithType(TestType(name));
        if (from is not null)
            builder.WithFrom(from);
        if (to2 is null)
            builder.WithTo(to);
        else
            builder.WithTo(to, to2);
        return builder.Build();
    }

    private void NoLeaks()
    {
        var sensitive = new[] { _serverDid, _alice.Did, _carol.Did, _mallory.Did };
        _logs.Entries.Should().NotContain(e => sensitive.Any(d => e.Contains(d, StringComparison.Ordinal)), "no log line may carry a DID (JC-6)");
    }

    /// <summary>A key service whose resolution can be parked on a gate, counted, or failed once.</summary>
    private sealed class GatedKeyService : IDidKeyService
    {
        private readonly IDidKeyService _inner;
        private int _calls;

        public GatedKeyService(IDidKeyService inner) => _inner = inner;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? Gate { get; set; }

        public bool FailNext { get; set; }

        /// <summary>Check the DID method on the DID subject of a DID URL instead of refusing the URL.</summary>
        public bool CheckMethodOnSubject { get; set; }

        public async Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            if (Gate is { } gate)
                await gate.Task.WaitAsync(ct);
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("simulated resolution fault");
            }
            return await _inner.GetVerificationMethodsAsync(did, relationship, ct);
        }

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => _inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

        public void RejectUnsupportedMethod(string did)
            => _inner.RejectUnsupportedMethod(CheckMethodOnSubject ? DidSubject.DidSubjectOf(did) ?? did : did);
    }

    /// <summary>A socket that records frames and close-outputs; its sends can be parked or faulted.</summary>
    private sealed class FakeWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private int _concurrentSends;
        private int _maxConcurrentSends;

        public ConcurrentQueue<byte[]> Frames { get; } = new();

        public ConcurrentQueue<WebSocketMessageType> MessageTypes { get; } = new();

        public ConcurrentQueue<(WebSocketCloseStatus Status, string? Description)> CloseOutputs { get; } = new();

        public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? SendGate { get; init; }

        public Exception? SendFault { get; init; }

        /// <summary>Park on <see cref="SendGate"/> without observing the send's cancellation token.</summary>
        public bool IgnoreCancellation { get; init; }

        /// <summary>Evaluated on entry to every send; a <c>true</c> answer is counted in <see cref="SendsAfterFlag"/>.</summary>
        public Func<bool>? AtSendEntry { get; init; }

        public int SendsAfterFlag => Volatile.Read(ref _sendsAfterFlag);

        private int _sendsAfterFlag;

        public int MaxConcurrentSends => Volatile.Read(ref _maxConcurrentSends);

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseOutputs.Enqueue((closeStatus, statusDescription));
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException("the session never receives");

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (AtSendEntry?.Invoke() == true)
                Interlocked.Increment(ref _sendsAfterFlag);
            var concurrent = Interlocked.Increment(ref _concurrentSends);
            int seen;
            while ((seen = Volatile.Read(ref _maxConcurrentSends)) < concurrent
                   && Interlocked.CompareExchange(ref _maxConcurrentSends, concurrent, seen) != seen)
            {
            }
            try
            {
                endOfMessage.Should().BeTrue("one push is one whole WebSocket message");
                SendEntered.TrySetResult();
                if (SendFault is not null)
                    throw SendFault;
                if (SendGate is { } gate)
                    await (IgnoreCancellation ? gate.Task : gate.Task.WaitAsync(cancellationToken));
                Frames.Enqueue(buffer.ToArray());
                MessageTypes.Enqueue(messageType);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentSends);
            }
        }
    }
}
