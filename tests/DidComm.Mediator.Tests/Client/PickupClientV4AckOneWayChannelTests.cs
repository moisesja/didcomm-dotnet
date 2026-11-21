using System.Threading.Channels;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PickupClient.AckAsync"/> under Message Pickup 4.0 over the real
/// <see cref="MediatorClientChannel"/> on the manual clock (#100, PR #143 review round 2, OD-6):
/// the transport is handed each <c>messages-received</c> without a <c>return_route</c> (4.0 L35)
/// and nothing else, no <c>status-request</c> before or after; nothing is registered for an answer,
/// so a status the mediator sends anyway answers nothing; the call returns <c>null</c> when the last
/// send completes. A send that does not complete within
/// <see cref="MediatorClientOptions.RequestTimeout"/> is a <see cref="TimeoutException"/>, and a
/// transport failure passes through. Answers are real authcrypted messages, unpacked as the
/// recipient and handed to the channel as the dispatcher hands them over.
/// </summary>
public sealed class PickupClientV4AckOneWayChannelTests : IAsyncLifetime
{
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private string M => _w.Mediator.Did;

    private string L => _w.Local.Did;

    [Fact]
    public async Task Over_a_WebSocket_the_transport_gets_each_acknowledgement_without_a_return_route_and_nothing_else()
    {
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, o => o.MessagePickupVersion = MessagePickupVersion.V4, time: time);
        var started = time.GetUtcNow();

        var status = await PickupTest.Within(new PickupClient(channel, logger: null).AckAsync(M, L, Ids(150), serviceEndpointOverride: Wss));
        var chunk1 = await NextAsync(sent);
        var chunk2 = await NextAsync(sent);

        status.Should().BeNull("nothing answers a 4.0 acknowledgement, and the call does not ask");
        new[] { chunk1.Type, chunk2.Type }.Should().OnlyContain(t => t == MessagePickup40.MessagesReceivedType);
        sent.Reader.TryRead(out _).Should().BeFalse("no status-request before or after the acknowledgements");
        var calls = send.Calls.ToArray();
        calls.Select(c => c.Message).Should().Equal(chunk1, chunk2);
        calls.Select(c => c.Options.ReturnRoute).Should().Equal(new ReturnRouteMode?[] { null, null }, "4.0 L35: no return_route");
        calls.Should().OnlyContain(c => c.Options.ServiceEndpointOverride == Wss && c.Options.From == L);
        time.GetUtcNow().Should().Be(started, "nothing waited on the clock");
        time.ScheduledTimers.Should().Be(0);
        channel.PendingCount.Should().Be(0);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(Status40(chunk2.Id, count: 0))).Should().BeFalse("no acknowledgement waits for an answer");
    }

    [Fact]
    public async Task Over_HTTP_a_status_in_the_response_is_ignored_and_the_call_returns_null()
    {
        // A 4.0 mediator sends nothing back (L294); one that does is not read.
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        send.ReturnWith(async message => await _w.ReceiveAsync(Status40(message.Id, count: 9)));
        await using var channel = Create(_w, send, o => o.MessagePickupVersion = MessagePickupVersion.V4, time: time);

        var status = await PickupTest.Within(new PickupClient(channel, logger: null).AckAsync(M, L, Ids(1), serviceEndpointOverride: Https));

        status.Should().BeNull();
        send.Calls.Select(c => (c.Message.Type, c.Options.ReturnRoute)).Should().Equal(
            new[] { (MessagePickup40.MessagesReceivedType, (ReturnRouteMode?)null) });
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_send_that_does_not_complete_within_the_RequestTimeout_is_a_TimeoutException_and_the_later_chunks_are_not_sent()
    {
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        send.Respond = (_, _, _) => new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await using var channel = Create(_w, send, o => o.MessagePickupVersion = MessagePickupVersion.V4, time: time);
        var timeout = channel.Options.RequestTimeout;
        var started = time.GetUtcNow();
        var mark = time.Mark();

        var acking = Observed(new PickupClient(channel, logger: null).AckAsync(M, L, Ids(150), serviceEndpointOverride: Wss));
        await time.WaitForTimerAsync(timeout, mark, PickupTest.Timeout);
        await send.FirstCall.WaitAsync(PickupTest.Timeout);
        time.Advance(timeout - TimeSpan.FromTicks(1)).Should().Be(0);
        acking.IsCompleted.Should().BeFalse();
        time.Advance(TimeSpan.FromTicks(1)).Should().Be(1, "the send's deadline");
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        var first = send.Calls.Should().ContainSingle("the call stops at the message that was not sent").Subject.Message;
        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(first.Id).And.Contain("messages-received").And.NotContain(M).And.NotContain(L);
        (time.GetUtcNow() - started).Should().Be(timeout);
        channel.PendingCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_send_that_fails_passes_its_TransportException_through(bool webSocket)
    {
        var send = new FakeSend();
        var failure = new TransportException("the socket closed");
        send.Respond = (_, _, _) => Task.FromException<SendResult>(failure);
        await using var channel = Create(_w, send, o => o.MessagePickupVersion = MessagePickupVersion.V4, time: new ManualTimeProvider());

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(new PickupClient(channel, logger: null)
            .AckAsync(M, L, Ids(150), serviceEndpointOverride: webSocket ? Wss : Https)));

        thrown.Should().BeSameAs(failure);
        send.Count.Should().Be(1);
        channel.PendingCount.Should().Be(0);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private Message Status40(string thid, int count) => MessagePickup40.CreateStatus(M, L, new PickupStatus40(count, LiveDelivery: false), thid);

    private static string[] Ids(int count) => Enumerable.Range(0, count).Select(i => $"q{i}").ToArray();

    // A send that takes every request (nothing returned: a WebSocket send) and hands it to the test in order.
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
