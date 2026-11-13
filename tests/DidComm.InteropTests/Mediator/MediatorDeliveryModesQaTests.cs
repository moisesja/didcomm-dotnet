using System.Net;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), delivery modes end to end: live push over the mediator's WebSocket, the queue, and
/// direct push to a host-registered endpoint — the status codes that count as delivered, the ones that
/// do not, the per-push deadline on an injected <see cref="TimeProvider"/>, schemes and the transport's
/// <c>AllowedSchemes</c>, the D6 order (live first, then queue, then push), a session that closes
/// before or during a push, two live sessions, the per-grant push bound, and FR-ROUTE-09 for a
/// message whose expiry passes while its push waits.
/// </summary>
public sealed class MediatorDeliveryModesQaTests
{
    // ---- direct push: what counts as delivered ---------------------------------------------------

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(299)]
    public async Task A_direct_push_answered_with_any_2xx_removes_the_queued_copy(int status)
    {
        await using var f = await QaPushFixture.StartAsync();
        f.Inbox.Status = status;

        var note = await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B);
        await f.Mediator.PushesIdleAsync();

        var received = f.Inbox.Received.Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(f.B, received.Body, f.Alice)).Message.Id.Should().Be(note.Id);
        f.Mediator.PushOutcomes.Should().Equal(PushOutcomes.Pushed);
        f.Mediator.Store.MessageCount.Should().Be(0);
        (await f.Mediator.StatusAsync(f.Bob)).MessageCount.Should().Be(0);
        f.ShouldNotLeak();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task A_direct_push_answered_with_4xx_or_5xx_leaves_the_message_queued_once_tried(int status)
    {
        await using var f = await QaPushFixture.StartAsync();
        f.Inbox.Status = status;

        var note = await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B);
        await f.Mediator.PushesIdleAsync();

        f.Inbox.Entered.Should().Be(1, "one attempt per message, no retry");
        f.Mediator.PushOutcomes.Should().Equal(PushOutcomes.Failed);
        f.Mediator.Store.MessageCount.Should().Be(1);
        var delivered = (await f.Mediator.FetchAsync(f.Bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(f.B, delivered.Payload, f.Alice)).Message.Id.Should().Be(note.Id);
        f.ShouldNotLeak();
    }

    [Fact]
    public async Task A_direct_push_that_outlasts_PushTimeout_on_the_injected_TimeProvider_is_abandoned_and_the_message_stays_queued()
    {
        var time = new QaManualTimeProvider();
        await using var f = await QaPushFixture.StartAsync(
            m => m.PushTimeout = TimeSpan.FromSeconds(7),
            services => services.AddSingleton<TimeProvider>(time));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Inbox.Gate = gate;

        var note = await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B);
        await MediatorTestServer.WaitUntilAsync(() => f.Inbox.Entered == 1, "the push is parked in Bob's endpoint");

        time.Advance(TimeSpan.FromSeconds(6));
        f.Mediator.PushOutcomes.Should().BeEmpty("one second before the deadline the push still runs");
        time.Advance(TimeSpan.FromSeconds(1));
        await f.Mediator.PushesIdleAsync();
        gate.SetResult();

        f.Mediator.PushOutcomes.Should().Equal(PushOutcomes.Failed);
        f.Mediator.Store.MessageCount.Should().Be(1, "an abandoned push never loses the accepted message");
        var delivered = (await f.Mediator.FetchAsync(f.Bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(f.B, delivered.Payload, f.Alice)).Message.Id.Should().Be(note.Id);
        f.Mediator.Logs.Entries.Should().Contain(e => e.Contains("Direct push") && e.Contains("Timeout"));
        f.ShouldNotLeak();
    }

    [Theory]
    [InlineData("ws://inbox.test/bob")]
    [InlineData("wss://inbox.test/bob")]
    [InlineData("ftp://inbox.test/bob")]
    [InlineData("didcomm:transport/queue")]
    public async Task An_endpoint_that_is_not_http_or_https_is_never_pushed_to(string endpoint)
    {
        await using var f = await QaPushFixture.StartAsync(map: false);
        f.Directory.Map(f.Bob.Did, new Uri(endpoint, UriKind.RelativeOrAbsolute));

        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B);
        await f.Mediator.PushesIdleAsync();

        f.Mediator.PushOutcomes.Should().Equal(PushOutcomes.SkippedScheme);
        f.Inbox.Entered.Should().Be(0);
        (await f.Mediator.FetchAsync(f.Bob)).Should().ContainSingle();
        f.ShouldNotLeak(endpoint);
    }

    [Fact]
    public async Task With_the_transport_limited_to_https_an_http_endpoint_is_refused_and_an_https_one_is_pushed()
    {
        await using var f = await QaPushFixture.StartAsync(post: services =>
            services.Configure<HttpTransportOptions>(o => o.AllowedSchemes = new[] { "https" }));

        // http: the transport refuses the scheme; the message stays queued.
        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, content: "over http");
        await f.Mediator.PushesIdleAsync();
        f.Mediator.PushOutcomes.Should().Equal(PushOutcomes.Failed);
        f.Inbox.Entered.Should().Be(0, "the http endpoint was never contacted");
        f.Mediator.Store.MessageCount.Should().Be(1);

        // https: pushed (the test network answers for the host whatever the scheme).
        f.Directory.Map(f.Bob.Did, new Uri($"https://{InboxServer.Host}/bob-inbox"));
        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, content: "over https");
        await f.Mediator.PushesIdleAsync();
        f.Mediator.PushOutcomes.Should().Equal(PushOutcomes.Failed, PushOutcomes.Pushed);
        f.Inbox.Received.Should().ContainSingle();
        f.Mediator.Store.MessageCount.Should().Be(1, "only the https push removed its copy");
        f.ShouldNotLeak();
    }

    [Fact]
    public async Task An_endpoint_outside_the_outbound_guard_is_refused_and_the_message_stays_queued()
    {
        await using var f = await QaPushFixture.StartAsync(map: false);
        f.Directory.Map(f.Bob.Did, new Uri("http://127.0.0.1/bob-inbox"));

        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B);
        await f.Mediator.PushesIdleAsync();

        f.Mediator.PushOutcomes.Should().Equal(PushOutcomes.Failed);
        f.Inbox.Entered.Should().Be(0);
        (await f.Mediator.FetchAsync(f.Bob)).Should().ContainSingle();
        f.ShouldNotLeak("127.0.0.1");
    }

    [Fact]
    public async Task One_grant_holds_at_most_16_push_jobs_and_the_17th_waits_in_the_queue()
    {
        await using var f = await QaPushFixture.StartAsync(m => m.PushConcurrency = 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Inbox.Gate = gate;

        for (var i = 0; i < 17; i++)
            await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, flush: false, content: $"m{i}");
        await f.Mediator.FlushAsync();

        f.Mediator.Outcome(ForwardOutcomes.Queued).Should().Be(17);
        f.Mediator.PushOutcome(PushOutcomes.SkippedSaturated).Should().Be(1);
        gate.SetResult();
        await f.Mediator.PushesIdleAsync();
        f.Mediator.PushOutcome(PushOutcomes.Pushed).Should().Be(16);
        f.Inbox.Received.Should().HaveCount(16);
        f.Mediator.Store.MessageCount.Should().Be(1);
        (await f.Mediator.FetchAsync(f.Bob)).Should().ContainSingle();
        f.ShouldNotLeak();
    }

    // ---- FR-ROUTE-09 / FR-PICK-10 before a direct push --------------------------------------------

    [Fact]
    public async Task A_message_whose_forward_expires_while_its_push_job_waits_is_never_pushed()
    {
        // One push worker, parked on Bob's endpoint by the first message; the second message's job
        // waits behind it. Its forward expires before the worker reaches it.
        await using var f = await QaPushFixture.StartAsync(m => m.PushConcurrency = 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Inbox.Gate = gate;
        var t0 = f.Mediator.Clock.Now;

        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, content: "first");
        await MediatorTestServer.WaitUntilAsync(() => f.Inbox.Entered == 1, "the only push worker is parked");
        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, Qa.Expires(t0.AddSeconds(60)), content: "expiring");
        f.Mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);

        f.Mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        (await f.Mediator.StatusAsync(f.Bob)).MessageCount.Should().Be(1, "the expired message is no longer counted (only the parked first one is)");
        gate.SetResult();
        await f.Mediator.PushesIdleAsync();

        var contents = new List<string>();
        foreach (var (_, body) in f.Inbox.Received)
            contents.Add((await MediatorRecipient.UnpackFromSenderAsync(f.B, body, f.Alice)).Message.Body!["content"]!.GetValue<string>());
        contents.Should().Equal(new[] { "first" }, "a message whose forward expired is never pushed (FR-ROUTE-09)");
    }

    [Fact]
    public async Task A_message_that_ages_out_while_its_push_job_waits_is_never_pushed()
    {
        await using var f = await QaPushFixture.StartAsync(m =>
        {
            m.PushConcurrency = 1;
            m.MaxForwardDelay = TimeSpan.Zero;
            m.Pickup.MaxMessageAge = TimeSpan.FromMinutes(1);
        });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Inbox.Gate = gate;

        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, content: "first");
        await MediatorTestServer.WaitUntilAsync(() => f.Inbox.Entered == 1, "the only push worker is parked");
        f.Mediator.Clock.Advance(TimeSpan.FromSeconds(30));
        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, content: "second");

        // The first ages out at +60 s, the second at +90 s: at +90 s both are past the limit.
        f.Mediator.Clock.Advance(TimeSpan.FromSeconds(60));
        (await f.Mediator.StatusAsync(f.Bob)).MessageCount.Should().Be(0);
        gate.SetResult();
        await f.Mediator.PushesIdleAsync();

        f.Inbox.Received.Should().ContainSingle("only the push already on the wire completes; the waiting job's message had aged out");
    }

    // ---- D6: live first, then the queue, then the push ---------------------------------------------

    [Fact]
    public async Task A_live_session_takes_the_message_first_and_nothing_is_pushed_directly()
    {
        // L-020 (PR #109 review R1-2): the forward is now only stored by the forward pump (Queued) and
        // the push workers push it live (Pushed-Live) before they would try the directory; the old
        // Delivered-Live forward outcome and the empty push counter described the in-pump live push.
        await using var f = await QaPushFixture.StartAsync();
        using var socket = await f.Mediator.ConnectAsync();
        await GoLiveAsync(f.Mediator, socket, f.Bob);

        var note = await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B);
        var pushed = (await f.Mediator.ReceiveOverSocketAsync(socket, f.Bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(f.B, pushed.Payload, f.Alice)).Message.Id.Should().Be(note.Id);
        await f.Mediator.PushesIdleAsync();

        f.Mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        f.Mediator.PushOutcomes.Should().Equal(new[] { PushOutcomes.PushedLive }, "the live session is tried first, and a message it took is never pushed directly");
        f.Inbox.Entered.Should().Be(0);
        f.Directory.Calls.Should().Be(0);
        (await AckOverSocketAsync(f.Mediator, socket, f.Bob, pushed.Id)).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        f.ShouldNotLeak(pushed.Id);
    }

    [Fact]
    public async Task After_the_live_session_closes_the_next_message_is_queued_and_pushed_directly()
    {
        await using var f = await QaPushFixture.StartAsync();
        var socket = await f.Mediator.ConnectAsync();
        await GoLiveAsync(f.Mediator, socket, f.Bob);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        socket.Dispose();
        await WaitForNoLiveSessionAsync(f.Mediator, f.Bob.Did);

        var note = await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B);
        await f.Mediator.PushesIdleAsync();

        f.Mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        f.Mediator.PushOutcomes.Should().Equal(PushOutcomes.Pushed);
        (await MediatorRecipient.UnpackFromSenderAsync(f.B, f.Inbox.Received.Single().Body, f.Alice)).Message.Id.Should().Be(note.Id);
        f.Mediator.Store.MessageCount.Should().Be(0);
        f.ShouldNotLeak();
    }

    [Fact]
    public async Task A_live_session_aborted_around_the_push_never_loses_the_message()
    {
        // Whichever side wins (the push is written before the server notices the abort, or the
        // session is gone first), the message ends with one outcome and can be fetched on a new
        // connection: a live lease ends with its session (L-044: assert the invariant, not the race).
        // L-020 (PR #109 review R1-2): the forward is always stored (Queued) and at most one push job
        // follows (none when the session was already gone), so the race is now on the push counter.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var socket = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, socket, bob);

        var note = alice.Note(bob.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));
        var post = mediator.PostAsync(packed);
        socket.Abort();
        socket.Dispose();
        (await post).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.PushOutcomes.Should().HaveCountLessThanOrEqualTo(1, "one outcome for the push job, when the session was still there to get one");
        IReadOnlyList<DeliveredMessage> fetched = Array.Empty<DeliveredMessage>();
        (await ReturnRouteTestHost.WaitUntilAsync(() =>
        {
            fetched = mediator.FetchAsync(bob).GetAwaiter().GetResult();
            return fetched.Count > 0;
        }, MediatorTestServer.Timeout)).Should().BeTrue("the message is fetchable once the aborted session's lease has ended");
        var delivered = fetched.Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        (await mediator.AcknowledgeAsync(bob, delivered.Id)).MessageCount.Should().Be(0);
        mediator.LeakingLogLines(delivered.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task With_two_live_sessions_each_message_goes_to_exactly_one_the_newest_and_then_the_older_once_the_newest_closes()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var older = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, older, bob);
        var newer = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, newer, bob);

        var first = await Qa.SendNoteAsync(mediator, alice, bob, content: "first");
        var onNewer = (await mediator.ReceiveOverSocketAsync(newer, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, onNewer.Payload, alice)).Message.Id.Should().Be(first.Id);
        // The older socket's next frame is the answer to its own request: nothing was pushed to it.
        (await AckOverSocketAsync(mediator, older, bob, onNewer.Id)).MessageCount.Should().Be(0);

        await newer.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        newer.Dispose();
        await WaitForLiveSessionCountAsync(mediator, bob.Did, 1);

        var second = await Qa.SendNoteAsync(mediator, alice, bob, content: "second");
        var onOlder = (await mediator.ReceiveOverSocketAsync(older, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, onOlder.Payload, alice)).Message.Id.Should().Be(second.Id);
        (await AckOverSocketAsync(mediator, older, bob, onOlder.Id)).MessageCount.Should().Be(0);

        // L-020 (PR #109 review R1-2): stored by the forward pump, pushed live by the push workers.
        await mediator.PushesIdleAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);
        mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedLive, PushOutcomes.PushedLive);
        await older.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        mediator.LeakingLogLines(onNewer.Id, onOlder.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_session_that_stops_reading_takes_what_its_push_bound_allows_and_the_rest_is_queued_never_both()
    {
        // Invariant for every interleaving: each message is pushed live (one frame on the socket) or
        // stays queued (fetchable, not leased to the socket), never both, never neither.
        // L-020 (PR #109 review R1-2): every forward is now counted Queued when stored; whether it was
        // pushed live is the push counter's Pushed-Live (one push job per message, all eight fit the
        // grant's share of the push backlog).
        const int Sent = 8;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Sessions.MaxPendingPushes = 2);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, socket, bob);

        var sent = new List<string>();
        for (var i = 0; i < Sent; i++)
            sent.Add((await Qa.SendNoteAsync(mediator, alice, bob, flush: false, content: $"m{i}")).Id);
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();
        mediator.Outcome(ForwardOutcomes.Queued).Should().Be(Sent);
        mediator.PushOutcomes.Should().HaveCount(Sent);
        var live = (int)mediator.PushOutcome(PushOutcomes.PushedLive);
        var queued = Sent - live;
        live.Should().BeGreaterThan(0);

        var received = new List<DeliveredMessage>();
        for (var i = 0; i < live; i++)
            received.AddRange((await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null));
        received.Should().HaveCount(live);
        (await AckOverSocketAsync(mediator, socket, bob, received.Select(m => m.Id).ToArray())).MessageCount.Should().Be(queued);

        var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var answer = await mediator.AskOverSocketAsync(socket, bob, request);
        var fetched = queued == 0 ? Array.Empty<DeliveredMessage>() : answer.Delivery(request.Id);
        fetched.Should().HaveCount(queued);
        var all = received.Concat(fetched).ToList();
        all.Select(m => m.Id).Should().OnlyHaveUniqueItems();
        var ids = new List<string>();
        foreach (var m in all)
            ids.Add((await MediatorRecipient.UnpackFromSenderAsync(bob, m.Payload, alice)).Message.Id);
        ids.Should().BeEquivalentTo(sent);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Fact]
    public async Task A_live_push_left_unacknowledged_on_a_session_that_closes_is_fetchable_from_the_other_session()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var older = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, older, bob);
        var newer = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, newer, bob);

        var note = await Qa.SendNoteAsync(mediator, alice, bob);
        var pushed = (await mediator.ReceiveOverSocketAsync(newer, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        await newer.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        newer.Dispose();
        await WaitForLiveSessionCountAsync(mediator, bob.Did, 1);

        IReadOnlyList<DeliveredMessage> got = Array.Empty<DeliveredMessage>();
        (await ReturnRouteTestHost.WaitUntilAsync(() =>
        {
            var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
            var answer = mediator.AskOverSocketAsync(older, bob, request).GetAwaiter().GetResult();
            got = answer.Message.Type == MessagePickup.DeliveryType ? answer.Delivery(request.Id) : Array.Empty<DeliveredMessage>();
            return got.Count > 0;
        }, MediatorTestServer.Timeout)).Should().BeTrue("the closed session's lease ended");
        var again = got.Should().ContainSingle().Subject;
        again.Id.Should().Be(pushed.Id, "the same record, delivered again (at least once)");
        (await MediatorRecipient.UnpackFromSenderAsync(bob, again.Payload, alice)).Message.Id.Should().Be(note.Id);
        (await AckOverSocketAsync(mediator, older, bob, again.Id)).MessageCount.Should().Be(0);
        await older.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    internal static async Task GoLiveAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer)
    {
        var live = MessagePickup.CreateLiveDeliveryChange(peer.Did, mediator.Did, true);
        (await mediator.AskOverSocketAsync(socket, peer, live)).Status(live.Id).LiveDelivery.Should().BeTrue();
    }

    internal static async Task<PickupStatus> AckOverSocketAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, params string[] ids)
    {
        var ack = MessagePickup.CreateMessagesReceived(peer.Did, mediator.Did, ids);
        return (await mediator.AskOverSocketAsync(socket, peer, ack)).Status(ack.Id);
    }

    internal static Task WaitForNoLiveSessionAsync(MediatorTestServer mediator, string peerDid) => WaitForLiveSessionCountAsync(mediator, peerDid, 0);

    internal static Task WaitForLiveSessionCountAsync(MediatorTestServer mediator, string peerDid, int count)
    {
        var registry = mediator.App.Services.GetRequiredService<DidComm.Transports.ReturnRoute.IReturnRouteSessionRegistry>();
        return MediatorTestServer.WaitUntilAsync(() => (registry.Find(peerDid)?.Count(s => s.IsLive) ?? 0) == count, "the server saw the socket close");
    }
}

/// <summary>A mediator with <c>DirectPush</c> on and a directory mapping Bob to an inbox, Bob enrolled with recipient DID B, and Alice.</summary>
internal sealed class QaPushFixture : IAsyncDisposable
{
    public required MediatorTestServer Mediator { get; init; }
    public required InboxServer Inbox { get; init; }
    public required TestEndpointDirectory Directory { get; init; }
    public required TestPeer Bob { get; init; }
    public required TestPeer B { get; init; }
    public required MediatorSender Alice { get; init; }

    public static async Task<QaPushFixture> StartAsync(
        Action<MediatorOptions>? mediator = null,
        Action<IServiceCollection>? services = null,
        Action<IServiceCollection>? post = null,
        bool map = true)
    {
        var network = new MediatorNetwork();
        var inbox = await InboxServer.StartAsync(network);
        var directory = new TestEndpointDirectory();
        var server = await MediatorTestServer.StartAsync(o =>
        {
            o.Network = network;
            o.Mediator = m =>
            {
                m.DirectPush = true;
                mediator?.Invoke(m);
            };
            o.ConfigureServices = s =>
            {
                s.AddSingleton<IRecipientEndpointDirectory>(directory);
                services?.Invoke(s);
            };
            o.PostConfigureServices = post;
        });
        var bob = await server.NewPeerAsync();
        (await server.RequestMediationAsync(bob)).Should().Equal(server.Did);
        var b = await server.NewRecipientAsync();
        await server.AddRecipientsAsync(bob, new[] { b.Did });
        var alice = await server.NewSenderAsync();
        if (map)
            directory.Map(bob.Did, inbox.Endpoint("/bob-inbox"));
        return new QaPushFixture { Mediator = server, Inbox = inbox, Directory = directory, Bob = bob, B = b, Alice = alice };
    }

    /// <summary>No DID or key id in any log line, and no push endpoint in the mediator's own lines.</summary>
    public void ShouldNotLeak(params string[] extra)
    {
        Mediator.LeakingLogLines(extra.Append(B.Did).ToArray()).Should().BeEmpty();
        Mediator.Logs.Entries.Where(e => e.Contains("push", StringComparison.OrdinalIgnoreCase))
            .Should().NotContain(e => e.Contains(InboxServer.Host, StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        await Mediator.DisposeAsync();
        await Inbox.DisposeAsync();
    }
}
