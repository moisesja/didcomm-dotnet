using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #96 engineer A: the real <see cref="MediatorClientChannel"/> of a recipient container
/// (<see cref="MediatorClientHarness"/>) against the #95 mediator (<see cref="MediatorTestServer"/>),
/// driven with raw Coordinate Mediation and Message Pickup requests through
/// <see cref="IMediatorExchange.ExchangeAsync"/>: over HTTP the answer is the response body
/// (<c>SendResult.Returned</c>), a 202 fails at once, a problem report fails with its code; over a
/// WebSocket — the mediator's DID naming a <c>ws://</c> endpoint, or an http DID with a <c>ws</c>
/// override — the answer arrives on the session socket and reaches the waiting request through
/// <c>UseInboundDispatch()</c> and the dispatcher's correlator. Every request reaches the mediator
/// with <c>created_time</c> stamped from the recipient's injected clock and <c>return_route: all</c>
/// (FR-RR-09), while the request the channel was handed is not modified.
/// </summary>
public sealed class MediatorClientChannelEndToEndTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Over_HTTP_the_answer_is_the_response_body_and_every_request_is_stamped_from_the_injected_clock()
    {
        var spy = new ReceivedSpy();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(spy));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var peer = await harness.NewPeerAsync();
        var channel = harness.Channel;

        var route = await channel.ResolveRouteAsync(mediator.Did, null, requireWebSocket: false, default);
        route.IsHttp.Should().BeTrue("the mediator's DID document names its http endpoint first");
        route.Endpoint.Should().Be(new Uri($"http://{mediator.Options.Host}{mediator.Options.Pattern}"));

        var mediateRequest = CoordinateMediation.CreateMediateRequest(peer.Did, mediator.Did);
        var grant = await channel.ExchangeAsync(route, mediateRequest, Specs.MediateRequest, mediator.Did, peer.Did, default).WaitAsync(Wait);
        grant.Type.Should().Be(CoordinateMediation.MediateGrantType);
        grant.Thid.Should().Be(mediateRequest.Id);
        CoordinateMediation.TryParseGrant(grant.DeserializeMessage(), out var routingDids).Should().BeTrue();
        routingDids.Should().Contain(mediator.Did);

        var statusRequest = MessagePickup.CreateStatusRequest(peer.Did, mediator.Did);
        var status = await channel.ExchangeAsync(route, statusRequest, Specs.StatusRequest, mediator.Did, peer.Did, default).WaitAsync(Wait);
        MessagePickup.TryParseStatus(status.DeserializeMessage(), out var parsed).Should().BeTrue();
        parsed!.MessageCount.Should().Be(0);

        await AssertStampedAsync(spy, mediator, mediateRequest, statusRequest);
        channel.PendingCount.Should().Be(0);
        mediator.LeakingLogLines().Should().BeEmpty("no line of either container names a DID or key id");
        harness.Logs.CategorizedRecords.Should().Contain(r => r.Category == typeof(MediatorClientChannel).FullName && r.Text.Contains(mediateRequest.Id, StringComparison.Ordinal),
            "the channel's Debug lines name our own request id");
    }

    [Fact]
    public async Task Over_HTTP_a_problem_report_in_the_response_fails_the_request_with_its_code()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var peer = await harness.NewPeerAsync();
        var route = await harness.Channel.ResolveRouteAsync(mediator.Did, null, false, default);
        var liveOn = MessagePickup.CreateLiveDeliveryChange(peer.Did, mediator.Did, liveDelivery: true);

        var act = () => harness.Channel.ExchangeAsync(route, liveOn, Specs.StatusRequest with { Name = "live-delivery-change" }, mediator.Did, peer.Did, default).WaitAsync(Wait);

        var thrown = (await act.Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.RequestId.Should().Be(liveOn.Id);
        thrown.ProblemCode.Should().Be(MessagePickup.LiveModeNotSupportedCode, "live delivery needs a connection that can carry pushes");
        thrown.Message.Should().NotContain(mediator.Did).And.NotContain(peer.Did);
    }

    [Fact]
    public async Task Over_HTTP_a_202_without_an_answer_fails_at_once_without_waiting_out_the_deadline()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var frozen = new FrozenTimeProvider();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.Time = frozen);
        var peer = await harness.NewPeerAsync();
        var route = await harness.Channel.ResolveRouteAsync(mediator.Did, null, false, default);
        // A message the mediator has no handler for: accepted with 202 and nothing in the body.
        var unanswered = new MessageBuilder().WithType("https://didcomm.org/basicmessage/2.0/message").WithFrom(peer.Did).WithTo(mediator.Did)
            .WithBody(new System.Text.Json.Nodes.JsonObject { ["content"] = "hi" }).Build();

        var act = () => harness.Channel.ExchangeAsync(route, unanswered, Specs.StatusRequest, mediator.Did, peer.Did, default).WaitAsync(Wait);

        var thrown = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        // The clients' clock never fires a timer, so this can only be the HTTP no-answer rule.
        thrown.Message.Should().Contain(unanswered.Id).And.Contain("no answer in the HTTP response").And.NotContain(mediator.Did).And.NotContain(peer.Did);
    }

    [Fact]
    public async Task Over_a_WebSocket_named_by_the_mediators_DID_the_answer_reaches_the_request_through_the_dispatcher()
    {
        var spy = new ReceivedSpy();
        var identity = await MediatorClientHarness.MintMediatorIdentityAsync("ws");
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Identity = identity;
            o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(spy);
        });
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseHttp = false;
            o.UseWebSocket = true;
        });
        var peer = await harness.NewPeerAsync();
        var channel = harness.Channel;

        var route = await channel.ResolveRouteAsync(mediator.Did, null, requireWebSocket: true, default);
        route.IsWebSocket.Should().BeTrue();
        route.Endpoint.Scheme.Should().Be("ws");

        await ExchangeOverTheSocketAsync(harness, route, peer.Did, spy);
    }

    [Fact]
    public async Task Over_a_WebSocket_override_of_an_http_DID_the_answer_reaches_the_request_through_the_dispatcher()
    {
        var spy = new ReceivedSpy();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(spy));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var peer = await harness.NewPeerAsync();

        var route = await harness.Channel.ResolveRouteAsync(mediator.Did, harness.WebSocketEndpoint, requireWebSocket: true, default);
        route.IsWebSocket.Should().BeTrue();

        await ExchangeOverTheSocketAsync(harness, route, peer.Did, spy);
    }

    [Fact]
    public async Task Without_inbound_dispatch_a_WebSocket_route_is_refused_and_nothing_reaches_the_mediator()
    {
        var spy = new ReceivedSpy();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(spy));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.UseInboundDispatch = false;
            o.WebSocket = w => w.ReceiveInbound = false;
        });

        var act = () => harness.Channel.ResolveRouteAsync(mediator.Did, harness.WebSocketEndpoint, requireWebSocket: false, default);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("UseInboundDispatch()");
        harness.Connects.Should().Be(0);
        spy.Messages.Should().BeEmpty();
    }

    // The WebSocket flow: a grant and a status over the session socket, both stamped.
    private static async Task ExchangeOverTheSocketAsync(MediatorClientHarness harness, MediatorRoute route, string peerDid, ReceivedSpy spy)
    {
        var mediator = harness.Mediator;
        var channel = harness.Channel;

        var mediateRequest = CoordinateMediation.CreateMediateRequest(peerDid, mediator.Did);
        var grant = await channel.ExchangeAsync(route, mediateRequest, Specs.MediateRequest, mediator.Did, peerDid, default).WaitAsync(Wait);
        grant.Type.Should().Be(CoordinateMediation.MediateGrantType);
        grant.Thid.Should().Be(mediateRequest.Id);
        grant.Verified.Should().BeTrue("it came through unpack, the replay gate and the dispatcher");

        var statusRequest = MessagePickup.CreateStatusRequest(peerDid, mediator.Did);
        var status = await channel.ExchangeAsync(route, statusRequest, Specs.StatusRequest, mediator.Did, peerDid, default).WaitAsync(Wait);
        status.Type.Should().Be(MessagePickup.StatusType);
        status.Thid.Should().Be(statusRequest.Id);

        harness.Connects.Should().Be(1, "both requests used the one session socket of the pair");
        await AssertStampedAsync(spy, mediator, mediateRequest, statusRequest);
        channel.PendingCount.Should().Be(0);
        mediator.LeakingLogLines().Should().BeEmpty("no line of either container names a DID or key id");
    }

    private static async Task AssertStampedAsync(ReceivedSpy spy, MediatorTestServer mediator, params Message[] requests)
    {
        var expected = mediator.Clock.Now.ToUnixTimeSeconds();
        foreach (var request in requests)
        {
            request.CreatedTime.Should().BeNull("the channel never stamps the request it is handed (SendAsync stamps a copy)");
            var received = await spy.WaitForAsync(request.Id, Wait);
            received.CreatedTime.Should().Be(expected, "FR-RR-09: stamped from the recipient's DidCommOptions.Clock");
            ReturnRouteHeader.Read(received).Mode.Should().Be(ReturnRouteMode.All);
        }
    }

    /// <summary>The specs as the facades define them.</summary>
    private static class Specs
    {
        public static readonly MediatorExchangeSpec MediateRequest = new("mediate-request",
            new[] { MessageTypeUri.Parse(CoordinateMediation.MediateGrantType), MessageTypeUri.Parse(CoordinateMediation.MediateDenyType) }, ExpectsDelivery: false);

        public static readonly MediatorExchangeSpec StatusRequest = new("status-request",
            new[] { MessageTypeUri.Parse(MessagePickup.StatusType) }, ExpectsDelivery: false);
    }

    /// <summary>Every message the mediator dispatched, by id.</summary>
    private sealed class ReceivedSpy : IProtocolObserver
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<Message>> _byId = new(StringComparer.Ordinal);

        public ConcurrentQueue<Message> Messages { get; } = new();

        public string? ProtocolUriFilter => null;

        public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
        {
            Messages.Enqueue(observation.Message);
            Slot(observation.Message.Id).TrySetResult(observation.Message);
            return Task.CompletedTask;
        }

        public Task<Message> WaitForAsync(string id, TimeSpan timeout) => Slot(id).Task.WaitAsync(timeout);

        private TaskCompletionSource<Message> Slot(string id)
            => _byId.GetOrAdd(id, _ => new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>A clock whose timers never fire: a deadline can never end a call.</summary>
    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Frozen();

        private sealed class Frozen : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
