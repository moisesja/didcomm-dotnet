using System.Threading.Channels;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PickupClient.AckAsync"/> and the next calls over the real <see cref="MediatorClientChannel"/>
/// and its matcher (#98 F-1, PR #130 review P2 and round 2): answers are real authcrypted messages
/// unpacked as the recipient and handed to the channel the way the dispatcher hands them over
/// (<see cref="IInboundCorrelator.OnInbound"/>), on the manual clock. On a WebSocket the client stops
/// waiting for a <c>messages-received</c>'s status half of
/// <see cref="MediatorClientOptions.RequestTimeout"/> after it was sent and goes on, so the given-up
/// status can still arrive later; the call returns the status that answered the last message, or
/// <c>null</c>, and never asks for one of its own (#100 OD-6). DIDComm v2.1 threading: a reply
/// carries the <c>thid</c> of the message it answers, and a message without that <c>thid</c> is not
/// part of the thread. So an answer arriving on its own completes a request only when its
/// <c>thid</c> names it; one without <c>thid</c> completes none, in the same call or a later one.
/// The same holds over HTTP: a thread-less reply in the response to a request completes no request.
/// A message that could not be sent is an exception, never <c>null</c>.
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

        status.Should().NotBeNull();
        status!.MessageCount.Should().Be(0, "chunk 2's own status, not chunk 1's");
        lateThreaded.Should().BeFalse("chunk 1 is no longer waiting");
        lateUnthreaded.Should().BeFalse("a status without thid may be chunk 1's late answer, so chunk 2 does not take it");
        own.Should().BeTrue();
        first.Type.Should().Be(MessagePickup.MessagesReceivedType);
        second.Type.Should().Be(MessagePickup.MessagesReceivedType);
        sent.Reader.TryRead(out _).Should().BeFalse("chunk 2 was answered: nothing more is sent");
    }

    [Fact]
    public async Task Over_a_WebSocket_when_every_chunk_is_given_up_the_call_returns_null_without_asking_and_a_late_status_answers_nothing()
    {
        // Both chunks given up (t=15, t=30): the call returns null at t=30 and sends nothing more.
        // Chunk 1's late status (no thid, 50 still queued) arrives at t=35 and answers nothing.
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, time: time);
        var started = time.GetUtcNow();
        var mark = time.Mark();

        var acking = Observed(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: Wss));
        var first = await NextAsync(sent);
        await time.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        mark = time.Mark();
        time.Advance(Half).Should().Be(1);
        var second = await NextAsync(sent);
        await time.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        time.Advance(Half).Should().Be(1, "only the wait for chunk 2's status comes due at t=30");
        var status = await PickupTest.Within(acking);
        time.Advance(TimeSpan.FromSeconds(5)).Should().Be(0);
        var lateUnthreaded = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(thid: null, count: 50)));
        var lateThreaded = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(second.Id, count: 0)));

        status.Should().BeNull("the last chunk got no status, and the call does not ask for one");
        new[] { first.Type, second.Type }.Should().OnlyContain(t => t == MessagePickup.MessagesReceivedType);
        sent.Reader.TryRead(out _).Should().BeFalse("no status-request follows");
        (time.GetUtcNow() - started).Should().Be(Budget + TimeSpan.FromSeconds(5), "half a RequestTimeout per chunk, then the 5 s the test moved");
        lateUnthreaded.Should().BeFalse("a status without thid belongs to no request's thread");
        lateThreaded.Should().BeFalse("chunk 2 is no longer waiting");
        channel.PendingCount.Should().Be(0);
        time.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task Over_a_WebSocket_a_status_that_answered_the_first_chunk_is_not_returned_when_the_last_chunk_is_given_up()
    {
        // OD-6: chunk 1 is answered (50 still queued), chunk 2 gets nothing; the call returns null,
        // not chunk 1's count, which predates chunk 2.
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, time: time);

        var acking = Observed(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: Wss));
        var first = await NextAsync(sent);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(first.Id, count: 50))).Should().BeTrue();
        var mark = time.Mark();
        var second = await NextAsync(sent);
        await time.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        time.Advance(Half).Should().Be(1, "chunk 2's wait for its status");
        var status = await PickupTest.Within(acking);

        status.Should().BeNull("only the answer to the last message is returned");
        second.Type.Should().Be(MessagePickup.MessagesReceivedType);
        sent.Reader.TryRead(out _).Should().BeFalse("no status-request follows");
        channel.PendingCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Over_a_WebSocket_a_late_status_without_thid_does_not_answer_the_next_call_on_the_same_channel(bool nextIsAnAcknowledgement)
    {
        // The review's case (PR #130, round 2): AckAsync gives up on chunk 1 at t=15, chunk 2 is
        // answered, the call returns; at t=16 the next call starts (GetStatusAsync, or another
        // AckAsync); at t=20 chunk 1's late status (no thid, 50 still queued) arrives. Without a thid
        // it is not part of any request's thread, so the next call does not take it.
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, time: time);
        var pickup = new PickupClient(channel, logger: null);
        var mark = time.Mark();

        var acking = Observed(pickup.AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: Wss));
        await NextAsync(sent);
        await time.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        time.Advance(Half).Should().Be(1, "only the wait for chunk 1's status comes due at t=15");
        var second = await NextAsync(sent);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(second.Id, count: 0))).Should().BeTrue();
        var acknowledged = await PickupTest.Within(acking);
        time.Advance(TimeSpan.FromSeconds(1)).Should().Be(0);
        async Task<PickupStatus?> NextCallAsync() => nextIsAnAcknowledgement
            ? await pickup.AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(1), serviceEndpointOverride: Wss)
            : await pickup.GetStatusAsync(_w.Mediator.Did, _w.Local.Did, serviceEndpointOverride: Wss);
        var next = Observed(NextCallAsync());
        var ask = await NextAsync(sent);
        time.Advance(TimeSpan.FromSeconds(4)).Should().Be(0);
        var late = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(thid: null, count: 50)));
        var own = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(ask.Id, count: 0)));
        var status = await PickupTest.Within(next);

        status.Should().NotBeNull();
        status!.MessageCount.Should().Be(0, "the next call's own status, not chunk 1's late one");
        acknowledged.Should().NotBeNull("chunk 2, the last, was answered");
        acknowledged!.MessageCount.Should().Be(0);
        late.Should().BeFalse("a status without thid is not part of the next call's thread (DIDComm v2.1 threading)");
        own.Should().BeTrue();
    }

    [Fact]
    public async Task Over_a_WebSocket_a_status_without_thid_is_not_taken_by_the_only_waiting_request_and_the_same_status_with_its_thid_is()
    {
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, time: new ManualTimeProvider());
        var request = _w.Request(MessagePickup.StatusRequestType);

        var exchange = channel.ExchangeAsync(WebSocketRoute, request, DeliveryReader.StatusRequestSpec, _w.Mediator.Did, _w.Local.Did, CancellationToken.None).Observed();
        await NextAsync(sent);
        var unthreaded = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(thid: null, count: 9)));
        var pendingAfter = !exchange.IsCompleted;
        var threaded = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id, count: 1)));

        unthreaded.Should().BeFalse("an answer without thid belongs to no request's thread, on any path");
        pendingAfter.Should().BeTrue();
        threaded.Should().BeTrue();
        (await PickupTest.Within(exchange)).Thid.Should().Be(request.Id);
    }

    [Fact]
    public async Task Over_a_WebSocket_a_mediator_that_answers_each_chunk_in_time_with_its_thid_gets_one_request_per_chunk()
    {
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, time: time);
        var left = 150;
        send.Respond = async (request, options, _) =>
        {
            await sent.Writer.WriteAsync(request);
            left -= request.Body![PickupBodies.MessageIdList]!.AsArray().Count;
            channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id, count: left)));
            return FakeSend.Accepted(options.ServiceEndpointOverride!);
        };

        var status = await PickupTest.Within(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: Wss));

        status.Should().NotBeNull();
        status!.MessageCount.Should().Be(0);
        send.Calls.Select(c => c.Message.Type).Should().Equal(MessagePickup.MessagesReceivedType, MessagePickup.MessagesReceivedType);
        time.ScheduledTimers.Should().Be(0, "no deadline is left behind");
    }

    [Fact]
    public async Task Over_a_WebSocket_a_mediator_whose_statuses_leave_out_thid_is_treated_as_one_that_sends_none_and_the_call_returns_null()
    {
        // The Message Pickup 3.0 example's shape (a status without thid), answered at once: it is not
        // taken, so the chunk is given up at half the RequestTimeout and the call returns null.
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, time: time);
        var taken = new List<bool>();
        send.Respond = async (request, options, _) =>
        {
            await sent.Writer.WriteAsync(request);
            taken.Add(channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(thid: null, count: 0))));
            return FakeSend.Accepted(options.ServiceEndpointOverride!);
        };
        var started = time.GetUtcNow();
        var mark = time.Mark();

        var acking = Observed(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(1), serviceEndpointOverride: Wss));
        await NextAsync(sent);
        await time.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        time.Advance(Half).Should().Be(1);
        var status = await PickupTest.Within(acking);

        status.Should().BeNull();
        taken.Should().Equal(false);
        sent.Reader.TryRead(out _).Should().BeFalse("no status-request follows");
        (time.GetUtcNow() - started).Should().Be(Half);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Over_HTTP_the_response_answers_the_last_chunk_only_with_its_thid_and_nothing_else_is_sent(bool withThid)
    {
        // Over HTTP nothing is ever given up. A status in the response answers the request only when
        // its thid names it (PR #130 review, round 2 follow-up); without one the response carried no
        // answer, which a messages-received allows: the call returns null and asks for nothing.
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        await using var channel = Create(_w, send, time: time);
        var left = 150;
        send.Respond = async (request, options, _) =>
        {
            left -= request.Body![PickupBodies.MessageIdList]!.AsArray().Count;
            return FakeSend.Accepted(options.ServiceEndpointOverride!, await _w.ReceiveAsync(_w.Status(thid: withThid ? request.Id : null, count: left)), status: 200);
        };

        var status = await PickupTest.Within(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: Https));

        if (withThid)
        {
            status.Should().NotBeNull();
            status!.MessageCount.Should().Be(0, "the status answering the last chunk");
        }
        else
        {
            status.Should().BeNull("a status without thid answers no request");
        }
        send.Calls.Select(c => c.Message.Type).Should().Equal(MessagePickup.MessagesReceivedType, MessagePickup.MessagesReceivedType);
        time.ScheduledTimers.Should().Be(0, "nothing waited on the clock");
    }

    [Fact]
    public async Task Over_HTTP_a_status_that_answered_the_first_chunk_is_not_returned_when_the_response_to_the_last_carries_none()
    {
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        await using var channel = Create(_w, send, time: time);
        var chunk = 0;
        send.Respond = async (request, options, _) => ++chunk == 1
            ? FakeSend.Accepted(options.ServiceEndpointOverride!, await _w.ReceiveAsync(_w.Status(request.Id, count: 50)), status: 200)
            : FakeSend.Accepted(options.ServiceEndpointOverride!);

        var status = await PickupTest.Within(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: Https));

        status.Should().BeNull("only the answer to the last message is returned");
        send.Calls.Select(c => c.Message.Type).Should().Equal(MessagePickup.MessagesReceivedType, MessagePickup.MessagesReceivedType);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_message_whose_send_fails_ends_the_call_with_the_TransportException_never_null(bool webSocket)
    {
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        await using var channel = Create(_w, send, time: time);
        var failure = new DidComm.Exceptions.TransportException("the socket closed");
        send.Respond = (_, _, _) => Task.FromException<SendResult>(failure);

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(new PickupClient(channel, logger: null)
            .AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(150), serviceEndpointOverride: webSocket ? Wss : Https)));

        thrown.Should().BeSameAs(failure);
        send.Count.Should().Be(1, "the call stops at the message that failed");
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Over_a_WebSocket_a_message_whose_send_does_not_complete_is_a_TimeoutException_at_the_RequestTimeout_never_null()
    {
        // The wait for the status starts only once the message is sent: a send that hangs is not
        // given up at half the RequestTimeout as a missing status, it times out as a request.
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        await using var channel = Create(_w, send, time: time);
        send.Respond = (_, _, _) => new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var started = time.GetUtcNow();
        var mark = time.Mark();

        var acking = Observed(new PickupClient(channel, logger: null).AckAsync(_w.Mediator.Did, _w.Local.Did, Ids(1), serviceEndpointOverride: Wss));
        await time.WaitForTimerAsync(Budget, mark, PickupTest.Timeout);
        await send.FirstCall.WaitAsync(PickupTest.Timeout);
        time.Advance(Half).Should().Be(0, "no wait for a status runs while the message is being sent");
        acking.IsCompleted.Should().BeFalse();
        time.Advance(Half).Should().Be(1, "the request's own deadline");
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(send.Calls.Single().Message.Id);
        (time.GetUtcNow() - started).Should().Be(Budget);
        channel.PendingCount.Should().Be(0);
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
    private static Task<PickupStatus?> Observed(Task<PickupStatus?> call)
    {
        call.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return call;
    }
}
