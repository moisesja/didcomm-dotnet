using System.Threading.Channels;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PickupClient.AckAsync"/> over the real <see cref="MediatorClientChannel"/> and its
/// matcher (#98 F-1, PR #130 review P2): answers are real authcrypted messages unpacked as the
/// recipient and handed to the channel the way the dispatcher hands them over
/// (<see cref="IInboundCorrelator.OnInbound"/>), on the manual clock. On a WebSocket the client
/// stops waiting for a <c>messages-received</c>'s status after half of
/// <see cref="MediatorClientOptions.RequestTimeout"/> and goes on (to the next chunk, or to a
/// <c>status-request</c>). A <c>status</c> without <c>thid</c> that arrives after that may be the
/// late answer to the request it gave up on, so every later request of the call takes only a status
/// whose <c>thid</c> names it (DIDComm threading: a reply carries <c>thid</c>). Before anything is
/// given up, and over HTTP, where the response is the answer, a status without <c>thid</c> is taken
/// as before.
/// </summary>
public sealed class PickupClientAckThreadingTests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().RequestTimeout;
    private static readonly TimeSpan Half = Budget / 2;

    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    [Fact]
    public async Task Over_a_WebSocket_a_late_status_without_thid_does_not_answer_the_chunk_sent_after_one_given_up()
    {
        // The review's case: chunk 1 at t=0, given up at t=15, chunk 2 sent; chunk 1's status (no
        // thid, 50 still queued) arrives at t=20. It must not be taken as chunk 2's answer.
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, time: time);
        var mark = time.Mark();

        var acking = Observed(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: Wss));
        var first = await NextAsync(sent);
        await time.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        time.Advance(Half).Should().Be(1, "only the wait for chunk 1's status comes due at t=15");
        var second = await NextAsync(sent);
        time.Advance(TimeSpan.FromSeconds(5)).Should().Be(0);
        var lateThreaded = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(first.Id, count: 50)));
        var lateUnthreaded = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(thid: null, count: 50)));
        var own = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(second.Id, count: 0)));
        var status = await PickupTest.Within(acking);

        status.MessageCount.Should().Be(0, "chunk 2's own status, not chunk 1's");
        lateThreaded.Should().BeFalse("chunk 1 is no longer waiting");
        lateUnthreaded.Should().BeFalse("a status without thid may be chunk 1's late answer, so chunk 2 does not take it");
        own.Should().BeTrue();
        first.Type.Should().Be(MessagePickup.MessagesReceivedType);
        second.Type.Should().Be(MessagePickup.MessagesReceivedType);
        sent.Reader.TryRead(out _).Should().BeFalse("chunk 2 was answered: nothing more is sent");
    }

    [Fact]
    public async Task Over_a_WebSocket_a_late_status_without_thid_does_not_answer_the_status_request_that_follows_a_chunk_given_up()
    {
        // Both chunks given up (t=15, t=30); the status-request goes out at t=30 with the 15 s left of
        // chunk 2's budget; chunk 1's late status (no thid, 50 still queued) arrives at t=35.
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, time: time);
        var mark = time.Mark();

        var acking = Observed(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: Wss));
        await NextAsync(sent);
        await time.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        mark = time.Mark();
        time.Advance(Half).Should().Be(1);
        await NextAsync(sent);
        await time.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        mark = time.Mark();
        time.Advance(Half).Should().Be(1, "only the wait for chunk 2's status comes due at t=30");
        var ask = await NextAsync(sent);
        await time.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        time.Advance(TimeSpan.FromSeconds(5)).Should().Be(0);
        var lateUnthreaded = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(thid: null, count: 50)));
        var own = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(ask.Id, count: 0)));
        var status = await PickupTest.Within(acking);

        status.MessageCount.Should().Be(0, "the status-request's own status, not a chunk's late one");
        ask.Type.Should().Be(MessagePickup.StatusRequestType);
        lateUnthreaded.Should().BeFalse("a status without thid may be the late answer to a chunk given up on");
        own.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Over_a_WebSocket_a_mediator_that_answers_each_chunk_in_time_gets_one_request_per_chunk_with_or_without_thid(bool threaded)
    {
        // Nothing is given up, so a status without thid (the Message Pickup 3.0 example's shape) is
        // still the only waiting request's answer, as before.
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, time: time);
        var left = 150;
        send.Respond = async (request, options, _) =>
        {
            await sent.Writer.WriteAsync(request);
            left -= request.Body![PickupBodies.MessageIdList]!.AsArray().Count;
            channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(threaded ? request.Id : null, count: left)));
            return FakeSend.Accepted(options.ServiceEndpointOverride!);
        };

        var status = await PickupTest.Within(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: Wss));

        status.MessageCount.Should().Be(0);
        send.Calls.Select(c => c.Message.Type).Should().Equal(MessagePickup.MessagesReceivedType, MessagePickup.MessagesReceivedType);
        time.ScheduledTimers.Should().Be(0, "no deadline is left behind");
    }

    [Fact]
    public async Task Over_HTTP_an_empty_response_is_followed_by_a_status_request_whose_response_is_its_answer_with_or_without_thid()
    {
        // Unchanged over HTTP: nothing is ever given up; the response to a request is its answer.
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        await using var channel = Create(_w, send, time: time);
        send.Respond = async (request, options, _) => request.Type == MessagePickup.StatusRequestType
            ? FakeSend.Accepted(options.ServiceEndpointOverride!, await _w.ReceiveAsync(_w.Status(thid: null, count: 0)), status: 200)
            : FakeSend.Accepted(options.ServiceEndpointOverride!);

        var status = await PickupTest.Within(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: Https));

        status.MessageCount.Should().Be(0);
        send.Calls.Select(c => c.Message.Type).Should().Equal(
            MessagePickup.MessagesReceivedType, MessagePickup.MessagesReceivedType, MessagePickup.StatusRequestType);
        time.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task A_request_that_takes_only_a_status_naming_it_still_counts_so_a_status_without_thid_stays_ambiguous_for_another_waiting_request()
    {
        // The flag never changes what another request (a live stream's, say) is offered: with two
        // requests of the pair waiting, a status without thid answers neither, as without the flag.
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, time: time);
        var threaded = _w.Request(MessagePickup.StatusRequestType);
        var plain = _w.Request(MessagePickup.StatusRequestType);

        var first = channel.ExchangeAsync(WebSocketRoute, threaded, DeliveryReader.StatusRequestThreadedSpec, _w.Mediator.Did, _w.Local.Did, CancellationToken.None).Observed();
        var second = channel.ExchangeAsync(WebSocketRoute, plain, DeliveryReader.StatusRequestSpec, _w.Mediator.Did, _w.Local.Did, CancellationToken.None).Observed();
        await NextAsync(sent);
        await NextAsync(sent);
        var unthreaded = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(thid: null, count: 9)));
        var answeredFirst = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(threaded.Id, count: 1)));
        var answeredSecond = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(plain.Id, count: 2)));

        unthreaded.Should().BeFalse("two requests of the pair wait for a status: it is ambiguous, flag or not");
        answeredFirst.Should().BeTrue();
        answeredSecond.Should().BeTrue();
        (await PickupTest.Within(first)).Thid.Should().Be(threaded.Id);
        (await PickupTest.Within(second)).Thid.Should().Be(plain.Id);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string[] Ids(int count) => Enumerable.Range(0, count).Select(i => $"q{i}").ToArray();

    // A send that takes every request (nothing returned: a WebSocket send, or an HTTP 202) and hands
    // it to the test in order.
    private static (FakeSend Send, Channel<Message> Sent) Recorded()
    {
        var sent = Channel.CreateUnbounded<Message>();
        var send = new FakeSend();
        send.Respond = async (request, options, _) =>
        {
            await sent.Writer.WriteAsync(request);
            return FakeSend.Accepted(options.ServiceEndpointOverride!);
        };
        return (send, sent);
    }

    private static async Task<Message> NextAsync(Channel<Message> sent)
        => await sent.Reader.ReadAsync().AsTask().WaitAsync(PickupTest.Timeout);

    // A call a failing test may leave running when the channel is disposed: its fault is observed.
    private static Task<PickupStatus> Observed(Task<PickupStatus> call)
    {
        call.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return call;
    }
}
