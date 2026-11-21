using System.Threading.Channels;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #100, PR #143 review P2-2, over the real <see cref="MediatorClientChannel"/> on the manual clock:
/// <see cref="PickupClient.AckAsync"/> under Message Pickup 4.0 over a WebSocket hands the transport
/// a <c>status-request</c> with <c>return_route: all</c> first (the header that opens, or reuses,
/// the connection that carries this agent's answers), then each <c>messages-received</c> without a
/// <c>return_route</c> (4.0 L35), then the confirming <c>status-request</c>, again with
/// <c>return_route: all</c>. Answers are real authcrypted messages, unpacked as the recipient and
/// handed to the channel as the dispatcher hands them over. The first <c>status-request</c> has the
/// channel's own deadline, one <see cref="MediatorClientOptions.RequestTimeout"/>, and a timeout or a
/// problem report there fails the call before anything is acknowledged.
/// </summary>
public sealed class PickupClientV4AckSessionChannelTests : IAsyncLifetime
{
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private string M => _w.Mediator.Did;

    private string L => _w.Local.Did;

    [Fact]
    public async Task The_transport_is_asked_for_a_return_route_on_the_first_status_request_on_none_of_the_acknowledgements_and_on_the_status_request_after_them()
    {
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, o => o.MessagePickupVersion = MessagePickupVersion.V4, time: time);

        var acking = Observed(new PickupClient(channel, logger: null).AckAsync(M, L, Ids(150), serviceEndpointOverride: Wss));
        var first = await NextAsync(sent);
        first.Type.Should().Be(MessagePickup40.StatusRequestType);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(Status40(first.Id, count: 150))).Should().BeTrue("the first status-request waits for its answer");
        var chunk1 = await NextAsync(sent);
        var chunk2 = await NextAsync(sent);
        var last = await NextAsync(sent);
        last.Type.Should().Be(MessagePickup40.StatusRequestType);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(Status40(last.Id, count: 0))).Should().BeTrue();
        var status = await PickupTest.Within(acking);

        status.MessageCount.Should().Be(0, "the status after the acknowledgements, not the first one (150)");
        new[] { chunk1.Type, chunk2.Type }.Should().OnlyContain(t => t == MessagePickup40.MessagesReceivedType);
        var calls = send.Calls.ToArray();
        calls.Select(c => c.Message).Should().Equal(first, chunk1, chunk2, last);
        calls.Select(c => c.Options.ReturnRoute).Should().Equal(ReturnRouteMode.All, null, null, ReturnRouteMode.All);
        calls.Should().OnlyContain(c => c.Options.ServiceEndpointOverride == Wss && c.Options.From == L, "one route, from the local DID: what the transport keys its connection on");
        sent.Reader.TryRead(out _).Should().BeFalse();
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_first_status_request_that_gets_no_answer_times_out_at_its_own_RequestTimeout_naming_it_and_nothing_is_acknowledged()
    {
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, o => o.MessagePickupVersion = MessagePickupVersion.V4, time: time);
        var mark = time.Mark();

        var acking = Observed(new PickupClient(channel, logger: null).AckAsync(M, L, Ids(1), serviceEndpointOverride: Wss));
        var first = await NextAsync(sent);
        await time.WaitForTimerAsync(channel.Options.RequestTimeout, mark, PickupTest.Timeout);
        time.Advance(channel.Options.RequestTimeout - TimeSpan.FromSeconds(1)).Should().Be(0, "nothing ends it before its deadline");
        acking.IsCompleted.Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(1)).Should().Be(1, "the first status-request's deadline, one RequestTimeout");
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        first.Type.Should().Be(MessagePickup40.StatusRequestType);
        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(first.Id).And.Contain("status-request").And.NotContain(M).And.NotContain(L);
        send.Count.Should().Be(1, "nothing was acknowledged");
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_first_status_request_answered_with_a_4_0_problem_report_fails_the_call_and_nothing_is_acknowledged()
    {
        var time = new ManualTimeProvider();
        var (send, sent) = Recorded();
        await using var channel = Create(_w, send, o => o.MessagePickupVersion = MessagePickupVersion.V4, time: time);

        var acking = Observed(new PickupClient(channel, logger: null).AckAsync(M, L, Ids(1), serviceEndpointOverride: Wss));
        var first = await NextAsync(sent);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(MessagePickup40.CreateLiveModeNotSupported(M, L, first.Id))).Should().BeTrue();
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        var protocol = thrown.Should().BeOfType<MediationProtocolException>().Subject;
        protocol.RequestId.Should().Be(first.Id);
        protocol.ProblemCode.Should().Be("e.m.live-mode-not-supported");
        send.Count.Should().Be(1, "nothing was acknowledged");
    }

    [Fact]
    public async Task Over_HTTP_under_4_0_the_acknowledgement_is_the_first_message()
    {
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        // The 4.0 acknowledgement is answered 202 with nothing in it (L294), the status-request 200 with its status.
        send.ReturnWith(async message => message.Type == MessagePickup40.StatusRequestType ? await _w.ReceiveAsync(Status40(message.Id, count: 0)) : null);
        await using var channel = Create(_w, send, o => o.MessagePickupVersion = MessagePickupVersion.V4, time: time);

        var status = await PickupTest.Within(new PickupClient(channel, logger: null).AckAsync(M, L, Ids(1), serviceEndpointOverride: Https));

        status.MessageCount.Should().Be(0);
        send.Calls.Select(c => (c.Message.Type, c.Options.ReturnRoute)).Should().Equal(
            (MessagePickup40.MessagesReceivedType, (ReturnRouteMode?)null), (MessagePickup40.StatusRequestType, ReturnRouteMode.All));
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
    private static Task<PickupStatus> Observed(Task<PickupStatus> call)
    {
        call.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return call;
    }
}
