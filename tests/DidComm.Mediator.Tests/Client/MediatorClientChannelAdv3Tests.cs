using System.Reflection;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
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
/// ADV-3 red-team pass on the correlation channel's bookkeeping (#96 design §4.1): every way an
/// exchange can end — an answer (HTTP or WebSocket), a timeout, the HTTP no-answer rule, a transport
/// failure (thrown or faulted), the caller's cancellation before the send, during it and while
/// waiting, a send that ignores its token, a problem report, a forged, unverified or mistyped
/// answer, the busy refusal, and the container's disposal at each step — must leave no pending
/// entry, no active count, no live registration and no timer behind. Driven by the manual clock and
/// rendezvous only (L-044).
/// </summary>
public sealed class MediatorClientChannelAdv3Tests : IAsyncLifetime
{
    private const int MaxPending = 8;

    private static readonly FieldInfo Active = typeof(MediatorClientChannel).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private enum Kind
    {
        HttpAnswer,
        HttpNoAnswer,
        HttpWrongType,
        HttpProblem,
        HttpForged,
        SendThrows,
        SendFaults,
        WsAnswer,
        WsProblem,
        WsTimeout,
        WsForgedThenTimeout,
        WsUnverifiedThenTimeout,
        WsWrongTypeThenTimeout,
        CancelledBefore,
        CancelledDuringSend,
        CancelledWhileWaiting,
        SendNeverReturns,
        SendHonoursItsToken,
    }

    private static readonly Kind[] Parking =
    {
        Kind.WsAnswer, Kind.WsProblem, Kind.WsTimeout, Kind.WsForgedThenTimeout, Kind.WsUnverifiedThenTimeout, Kind.WsWrongTypeThenTimeout,
        Kind.CancelledDuringSend, Kind.CancelledWhileWaiting, Kind.SendNeverReturns, Kind.SendHonoursItsToken,
    };

    /// <summary>
    /// Held (passes): 3 200 exchanges in waves of eight (the pending bound), every way of ending
    /// mixed at random, with a busy probe whenever the map is full: each ends with the exception its
    /// kind calls for, and afterwards the pending map, the active count and the clock's timers are
    /// all empty.
    /// </summary>
    [Fact]
    public async Task Thousands_of_exchanges_ending_every_way_leave_no_pending_entry_active_count_or_timer()
    {
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        var plans = new System.Collections.Concurrent.ConcurrentDictionary<string, Func<SendOptions, CancellationToken, Task<SendResult>>>(StringComparer.Ordinal);
        send.Respond = (message, options, ct) => plans.TryRemove(message.Id, out var plan) ? plan(options, ct) : throw new InvalidOperationException("unplanned");
        var channel = Create(_w, send, o => o.MaxPendingRequests = MaxPending, time: time);
        var random = new Random(9603);
        var kinds = Enum.GetValues<Kind>();
        var busyProbes = 0;
        try
        {
            for (var wave = 0; wave < 400; wave++)
            {
                var ops = new List<(Kind Kind, Message Request, Task<InboundMessageSnapshot> Exchange, CancellationTokenSource Cancel)>();
                var allPark = wave % 10 == 0;
                for (var i = 0; i < MaxPending; i++)
                {
                    var kind = allPark ? Parking[random.Next(Parking.Length)] : kinds[random.Next(kinds.Length)];
                    var request = _w.Request();
                    var cancel = new CancellationTokenSource();
                    if (kind == Kind.CancelledBefore)
                        cancel.Cancel(); // refused before anything is sent: no send planned
                    else
                        plans[request.Id] = Plan(kind, request);
                    var route = kind <= Kind.SendFaults ? HttpRoute : WebSocketRoute;
                    var exchange = Start(channel, route, request, cancel.Token);
                    ops.Add((kind, request, exchange, cancel));
                }

                if (allPark)
                {
                    channel.PendingCount.Should().Be(MaxPending);
                    var probe = _w.Request();
                    await FluentActions.Awaiting(() => channel.ExchangeAsync(WebSocketRoute, probe, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default))
                        .Should().ThrowAsync<InvalidOperationException>().Where(e => e.Message.Contains("already waiting"));
                    plans.TryRemove(probe.Id, out _).Should().BeFalse("the busy refusal sends nothing");
                    busyProbes++;
                }

                // What arrives on the connection, and the callers who give up.
                foreach (var (kind, request, _, cancel) in ops)
                {
                    switch (kind)
                    {
                        case Kind.WsAnswer:
                            channel.AsCorrelator().OnInbound(Answer(_w.Status(request.Id))).Should().BeTrue();
                            break;
                        case Kind.WsProblem:
                            channel.AsCorrelator().OnInbound(Answer(_w.Problem(request.Id))).Should().BeTrue();
                            break;
                        case Kind.WsForgedThenTimeout:
                            channel.AsCorrelator().OnInbound(CraftedSnapshot.Verified(_w.Status(request.Id), senderKid: _w.Mallory.Privates[0].Kid, recipientKid: _w.Local.Privates[0].Kid)).Should().BeFalse();
                            break;
                        case Kind.WsUnverifiedThenTimeout:
                            channel.AsCorrelator().OnInbound(InboundMessageSnapshot.CreateFallback(Result(_w.Status(request.Id)))).Should().BeFalse();
                            break;
                        case Kind.WsWrongTypeThenTimeout:
                            channel.AsCorrelator().OnInbound(Answer(_w.Grant(request.Id))).Should().BeFalse();
                            break;
                        case Kind.CancelledDuringSend:
                        case Kind.CancelledWhileWaiting:
                            cancel.Cancel();
                            break;
                    }
                }
                time.Advance(channel.Options.RequestTimeout);

                foreach (var (kind, _, exchange, cancel) in ops)
                {
                    await Expect(kind, exchange);
                    cancel.Dispose();
                }
                channel.PendingCount.Should().Be(0, "wave {0}", wave);
            }

            busyProbes.Should().Be(40);
            channel.PendingCount.Should().Be(0);
            ((int)Active.GetValue(channel)!).Should().Be(0, "every exchange gave its active count back");
            time.ScheduledTimers.Should().Be(0, "every request deadline was disposed");
            plans.Should().BeEmpty("every planned send was made exactly once");
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }

    /// <summary>
    /// Held (passes): the container's disposal at each step of an exchange — finding the endpoint, the
    /// send, the wait for a WebSocket answer, and after it — ends the call with
    /// <see cref="ObjectDisposedException"/>, and leaves nothing pending, active, registered or timed.
    /// </summary>
    [Theory]
    [InlineData("resolving")]
    [InlineData("sending")]
    [InlineData("waiting")]
    [InlineData("live")]
    [InlineData("after")]
    public async Task Disposal_at_each_step_leaves_nothing_behind(string step)
    {
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        var parkedResolve = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = Create(_w, send, time: time, resolveEndpoint: (_, _) =>
        {
            resolving.TrySetResult();
            return parkedResolve.Task; // ignores its token
        });
        send.Respond = step == "sending"
            ? (_, _, _) => new TaskCompletionSource<SendResult>().Task // never, ignores its token
            : (_, o, _) => Task.FromResult(FakeSend.Accepted(o.ServiceEndpointOverride!));

        Task call;
        LiveRegistration? registration = null;
        switch (step)
        {
            case "resolving":
                call = channel.ResolveRouteAsync(_w.Mediator.Did, null, requireWebSocket: false, default);
                await resolving.Task.WaitAsync(PickupTest.Timeout);
                break;
            case "sending":
                call = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
                await send.FirstCall.WaitAsync(PickupTest.Timeout);
                break;
            case "waiting":
                call = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);
                await send.FirstCall.WaitAsync(PickupTest.Timeout);
                break;
            case "live":
                registration = channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
                call = Task.CompletedTask;
                break;
            default:
                call = Task.CompletedTask;
                break;
        }

        await channel.DisposeAsync();
        if (step == "after")
            call = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, default);

        if (step != "live")
            await FluentActions.Awaiting(() => call.WaitAsync(PickupTest.Timeout)).Should().ThrowAsync<ObjectDisposedException>();
        else
        {
            await FluentActions.Awaiting(() => registration!.Pushes.Completion.WaitAsync(PickupTest.Timeout))
                .Should().ThrowAsync<ObjectDisposedException>("the stream's reader observes the disposal");
            registration!.Dispose(); // the stream's own disposal afterwards gives nothing back twice
        }
        channel.PendingCount.Should().Be(0);
        channel.LiveStreamCount.Should().Be(0);
        ((int)Active.GetValue(channel)!).Should().Be(0);
        time.ScheduledTimers.Should().Be(0);
        parkedResolve.TrySetCanceled();
    }

    /// <summary>
    /// Held (passes): a thousand live registrations opened and closed (pushes buffered, refused when
    /// full, and to a stream that has ended), a second registration for the same pair refused, and
    /// the bound of 16 reached and given back: the counts return to zero.
    /// </summary>
    [Fact]
    public async Task Live_registrations_churned_a_thousand_times_return_every_count()
    {
        var channel = Create(_w, new FakeSend(), time: new ManualTimeProvider());
        try
        {
            var push = Answer(_w.Delivery(thid: null));
            for (var i = 0; i < 1000; i++)
            {
                var registration = channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
                FluentActions.Invoking(() => channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did)).Should().Throw<InvalidOperationException>();
                for (var p = 0; p < LiveRegistration.PushCapacity + 2; p++)
                    channel.AsCorrelator().OnInbound(push);
                registration.Dispose();
                channel.AsCorrelator().OnInbound(push).Should().BeFalse("a push after the stream ended reaches nothing");
                registration.Dispose();
            }

            var many = Enumerable.Range(0, MediatorClientChannel.MaxLiveStreams)
                .Select(i => channel.RegisterLiveStream(_w.Mediator.Did, $"did:example:local-{i}")).ToArray();
            FluentActions.Invoking(() => channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did)).Should().Throw<InvalidOperationException>();
            foreach (var registration in many)
                registration.Dispose();

            channel.LiveStreamCount.Should().Be(0);
            ((int)Active.GetValue(channel)!).Should().Be(0);
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }

    /// <summary>
    /// ADV3-7 (defect, Low; fixed in fix round 3: the delivery budget is linked to the channel's shutdown). Property broken — <see cref="MediatorClientChannel.Dispose"/>:
    /// "Stop: pending requests fail with <see cref="ObjectDisposedException"/> and live streams end";
    /// <see cref="PickupClient.FetchAsync"/>: "<see cref="ObjectDisposedException"/>: The container
    /// was disposed"; <see cref="GuardedDeadline"/>: the token handed to host code is cancelled by
    /// "the caller's token and the channel's shutdown token". Attack (shutdown, not remote): the
    /// delivery budget is a <see cref="GuardedDeadline"/> on the caller's token only — the unpacker
    /// is not given the channel's shutdown token — so disposing the container while a fetch (or a
    /// live stream) is opening a delivery cancels nothing: the host's DID resolutions keep running on
    /// an uncancelled token against a disposed container, and the call waits out the whole budget
    /// (20 s) before it returns — normally, with its acknowledgement failing quietly — instead of
    /// ending with <see cref="ObjectDisposedException"/>. A live stream yields the delivery's items
    /// after the disposal in the same way.
    /// </summary>
    [Fact]
    public async Task ADV3_7_disposing_the_container_while_a_delivery_is_opened_cancels_nothing_and_the_fetch_waits_out_the_budget()
    {
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = Create(_w, send, time: time, unpack: async (_, ct) =>
        {
            entered.TrySetResult(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // a DID lookup that honours its token
            throw new InvalidOperationException("unreachable");
        });
        send.ReturnWith(request => Task.FromResult<UnpackResult?>(Returned(DidComm.Mediator.Pickup.MessagePickup.CreateDelivery(
            _w.Mediator.Did, _w.Local.Did, new[] { new DidComm.Mediator.Pickup.DeliveredMessage("q1", System.Text.Encoding.UTF8.GetBytes("packed")) }, thid: request.Id))));
        var pickup = new PickupClient(channel, logger: null);

        var fetch = pickup.FetchAsync(_w.Mediator.Did, _w.Local.Did, 10, serviceEndpointOverride: Https);
        _ = fetch.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var token = await entered.Task.WaitAsync(PickupTest.Timeout);
        try
        {
            await channel.DisposeAsync();

            token.IsCancellationRequested.Should().BeTrue("the container is gone: the host code opening its delivery must be told to stop");
            await FluentActions.Awaiting(() => fetch.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<ObjectDisposedException>(
                "a fetch running when its container is disposed ends with ObjectDisposedException, not after its budget");
        }
        finally
        {
            time.Advance(channel.Options.UnpackTimeout);
            await Record.ExceptionAsync(() => fetch.WaitAsync(PickupTest.Timeout));
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private Task<InboundMessageSnapshot> Start(MediatorClientChannel channel, MediatorRoute route, Message request, CancellationToken ct)
    {
        var task = channel.ExchangeAsync(route, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, ct);
        // Observed here: a test that fails part-way must not leave an unobserved fault for the
        // parallel unobserved-exception tests to count.
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private Func<SendOptions, CancellationToken, Task<SendResult>> Plan(Kind kind, Message request)
        => kind switch
        {
            Kind.HttpAnswer => (o, _) => Task.FromResult(FakeSend.Accepted(o.ServiceEndpointOverride!, Returned(_w.Status(request.Id)), 200)),
            Kind.HttpNoAnswer => (o, _) => Task.FromResult(FakeSend.Accepted(o.ServiceEndpointOverride!)),
            Kind.HttpWrongType => (o, _) => Task.FromResult(FakeSend.Accepted(o.ServiceEndpointOverride!, Returned(_w.Grant(request.Id)), 200)),
            Kind.HttpProblem => (o, _) => Task.FromResult(FakeSend.Accepted(o.ServiceEndpointOverride!, Returned(_w.Problem(request.Id)), 200)),
            Kind.HttpForged => (o, _) => Task.FromResult(FakeSend.Accepted(o.ServiceEndpointOverride!, Returned(_w.Status(request.Id), _w.Mallory.Privates[0].Kid), 200)),
            Kind.SendThrows => (_, _) => throw new TransportException("refused at once", httpStatusCode: null, scheme: "https"),
            Kind.SendFaults => (_, _) => Task.FromException<SendResult>(new TransportException("failed later", httpStatusCode: 502, scheme: "https")),
            Kind.CancelledDuringSend or Kind.SendNeverReturns => (_, _) => new TaskCompletionSource<SendResult>().Task,
            Kind.SendHonoursItsToken => async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            },
            _ => (o, _) => Task.FromResult(FakeSend.Accepted(o.ServiceEndpointOverride!)),
        };

    private static async Task Expect(Kind kind, Task<InboundMessageSnapshot> exchange)
    {
        switch (kind)
        {
            case Kind.HttpAnswer:
            case Kind.WsAnswer:
                (await exchange.WaitAsync(PickupTest.Timeout)).Type.Should().Be(DidComm.Mediator.Pickup.MessagePickup.StatusType);
                break;
            case Kind.HttpProblem:
            case Kind.WsProblem:
                await FluentActions.Awaiting(() => exchange.WaitAsync(PickupTest.Timeout)).Should().ThrowAsync<MediationProtocolException>();
                break;
            case Kind.SendThrows:
            case Kind.SendFaults:
                await FluentActions.Awaiting(() => exchange.WaitAsync(PickupTest.Timeout)).Should().ThrowAsync<TransportException>();
                break;
            case Kind.CancelledBefore:
            case Kind.CancelledDuringSend:
            case Kind.CancelledWhileWaiting:
                await FluentActions.Awaiting(() => exchange.WaitAsync(PickupTest.Timeout)).Should().ThrowAsync<OperationCanceledException>();
                break;
            default:
                await FluentActions.Awaiting(() => exchange.WaitAsync(PickupTest.Timeout)).Should().ThrowAsync<TimeoutException>();
                break;
        }
    }

    // A verified answer from the mediator to the local DID (crafted: no crypto, the same registration unpack makes).
    private InboundMessageSnapshot Answer(Message message, string? senderKid = null)
        => CraftedSnapshot.Verified(message, senderKid: senderKid ?? _w.Mediator.Privates[0].Kid, recipientKid: _w.Local.Privates[0].Kid);

    // What SendResult.Returned carries: an unpack result whose message has a verified snapshot.
    private UnpackResult Returned(Message message, string? senderKid = null)
    {
        Answer(message, senderKid);
        return Result(message, senderKid ?? _w.Mediator.Privates[0].Kid);
    }

    private UnpackResult Result(Message message, string? senderKid = null)
        => new(
            Message: message,
            Stack: new[] { EnvelopeKind.Encrypted, EnvelopeKind.Plaintext },
            Encrypted: true,
            Authenticated: true,
            NonRepudiation: false,
            AnonymousSender: false,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: null,
            SenderKid: senderKid ?? _w.Mediator.Privates[0].Kid,
            RecipientKid: _w.Local.Privates[0].Kid,
            AllRecipientKids: new[] { _w.Local.Privates[0].Kid! },
            FromPrior: null);
}
