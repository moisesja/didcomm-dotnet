using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.TrustPing;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #105 — two hosts whose clocks are 30 seconds apart (the #98 clock-skew case), end to end: a
/// recipient application (a real <see cref="DidCommClient"/> in its own container, its clock offset
/// from the mediator's) against the TestServer mediator with <c>AddCoordinateMediation()</c> and
/// <c>AddMessagePickup()</c>, over HTTP and over WebSocket, with the client ahead and behind. Every
/// exchange is in the FR-RR-08 scope and stamped by its sender's own clock (FR-RR-09): the trust
/// ping with <c>return_route: all</c>, enrolment (<c>mediate-request</c>, <c>recipient-update</c>)
/// and pickup (<c>status-request</c>, <c>delivery-request</c>, <c>messages-received</c>, and over
/// WebSocket live mode and a live push). The default <see cref="ReplayProtectionOptions.ClockSkew"/>
/// (60 s) admits them on both sides; the negative controls show where <c>ClockSkew = 0</c> — the rule
/// before #105 — drops them, so these tests would catch the old behaviour.
/// </summary>
public sealed class ReplayClockSkewTwoHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(30)]
    [InlineData(-30)]
    public async Task Over_HTTP_a_client_30_seconds_off_the_mediators_clock_completes_ping_enrolment_and_pickup(int clientAheadSeconds)
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        await using var client = ReturnRouteClientHarness.Create(mediator.Host, clockOffset: TimeSpan.FromSeconds(clientAheadSeconds), holders: mediator.Recipient);

        await RunOverHttpAsync(mediator, client, clientAheadSeconds);

        mediator.Host.DropLines("").Should().BeEmpty("nothing was refused on either side");
        mediator.Host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-30)]
    public async Task Over_WebSocket_a_client_30_seconds_off_the_mediators_clock_completes_ping_enrolment_pickup_and_live_delivery(int clientAheadSeconds)
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        await using var client = WebSocketClient(mediator, clientAheadSeconds);

        await RunOverWebSocketAsync(mediator, client, clientAheadSeconds);

        mediator.Host.DropLines("").Should().BeEmpty("nothing was refused on either side");
        mediator.Host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- negative controls: ClockSkew = 0 is the rule before #105 ----------------------------------

    [Fact]
    public async Task With_no_replay_skew_on_the_mediator_a_client_30_seconds_ahead_is_dropped_over_HTTP()
    {
        await using var mediator = await PickupEndpointHost.StartAsync(replay: o => o.ClockSkew = TimeSpan.Zero);
        var recipient = mediator.Recipient;
        await using var client = ReturnRouteClientHarness.Create(mediator.Host, clockOffset: TimeSpan.FromSeconds(30), holders: recipient);
        var ping = TrustPing.CreatePing(recipient.Did, mediator.Mediator);

        var result = await client.Client.SendAsync(ping, client.ToServer(recipient) with { ReturnRoute = ReturnRouteMode.All });

        result.Transport.HttpStatusCode.Should().Be(202, "the mediator refused the request as future-dated and answered like any drop");
        result.Returned.Should().BeNull();
        ServerDrops(mediator, ping.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.FutureDated));
        mediator.Host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_replay_skew_on_the_mediator_a_client_30_seconds_ahead_is_dropped_over_WebSocket()
    {
        await using var mediator = await PickupEndpointHost.StartAsync(replay: o => o.ClockSkew = TimeSpan.Zero);
        var recipient = mediator.Recipient;
        await using var client = WebSocketClient(mediator, clientAheadSeconds: 30);
        var request = CoordinateMediation.CreateMediateRequest(recipient.Did, mediator.Mediator);

        await client.Client.SendAsync(request, client.ToServer(recipient) with { ReturnRoute = ReturnRouteMode.All });

        (await ReturnRouteTestHost.WaitUntilAsync(() => ServerDrops(mediator, request.Id).Count == 1, Timeout)).Should().BeTrue();
        ServerDrops(mediator, request.Id).Single().Should().Contain(nameof(ReplayAdmission.FutureDated));
        client.Recorder.Count.Should().Be(0, "nothing was granted, so nothing came back");
        (await mediator.Mediation.FindGrantByRecipientDidAsync(recipient.Did, default)).Should().BeNull();
        mediator.Host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_replay_skew_on_the_client_the_answers_of_a_mediator_30_seconds_ahead_are_dropped_over_WebSocket()
    {
        // The client side of the rule (#92's inbound consumer): the mediator stamps its answers with
        // its own clock, 30 s ahead of the client's. Its ping-response carries no return_route and is
        // no mediator-service type, so it is outside FR-RR-08 and arrives either way.
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await using var client = WebSocketClient(mediator, clientAheadSeconds: -30, clientReplay: o => o.ClockSkew = TimeSpan.Zero);
        var options = client.ToServer(recipient) with { ReturnRoute = ReturnRouteMode.All };
        var ping = TrustPing.CreatePing(recipient.Did, mediator.Mediator);
        var request = CoordinateMediation.CreateMediateRequest(recipient.Did, mediator.Mediator);

        await client.Client.SendAsync(ping, options);
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Seen.Any(r => r.Message.Thid == ping.Id), Timeout)).Should().BeTrue();
        await client.Client.SendAsync(request, options);

        (await ReturnRouteTestHost.WaitUntilAsync(() => mediator.Host.Logs.Entries.Any(IsClientFutureDatedDrop), Timeout)).Should().BeTrue();
        (await mediator.Mediation.FindGrantByRecipientDidAsync(recipient.Did, default)).Should().BeNull("no recipient was registered");
        (await mediator.Mediation.GetGrantAsync(recipient.Did, default)).Should().NotBeNull("the mediator admitted the request and granted mediation");
        client.Recorder.Seen.Should().NotContain(r => r.Message.Thid == request.Id, "the client refused the grant as future-dated");
        ServerDrops(mediator, request.Id).Should().BeEmpty();
        mediator.Host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Over_HTTP_the_answers_of_a_mediator_30_seconds_ahead_reach_a_client_with_no_skew_because_Returned_is_not_replay_admitted()
    {
        // SendResult.Returned proves identity, not freshness (FR-RR-07, #92): the in-band HTTP answer
        // is never run through FR-RR-08, so no replay skew decides it.
        await using var mediator = await PickupEndpointHost.StartAsync(replay: o => o.ClockSkew = TimeSpan.Zero);
        await using var client = ReturnRouteClientHarness.Create(
            mediator.Host,
            configureDidComm: b => b.Services.AddOptions<ReplayProtectionOptions>().Configure(o => o.ClockSkew = TimeSpan.Zero),
            clockOffset: TimeSpan.FromSeconds(-30),
            holders: mediator.Recipient);

        await RunOverHttpAsync(mediator, client, clientAheadSeconds: -30);

        mediator.Host.DropLines("").Should().BeEmpty();
        mediator.Host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- the flows ------------------------------------------------------------------------------

    private static async Task RunOverHttpAsync(PickupEndpointHost mediator, ReturnRouteClientHarness client, int clientAheadSeconds)
    {
        var recipient = mediator.Recipient;
        var options = client.ToServer(recipient) with { ReturnRoute = ReturnRouteMode.All };

        async Task<Message> AskAsync(Message request)
        {
            var result = await client.Client.SendAsync(request, options);
            (await SentStampAsync(mediator, result)).Should().Be(ClientNow(mediator, clientAheadSeconds), "the client stamps from its own clock (FR-RR-09)");
            result.Transport.HttpStatusCode.Should().Be(200, "the mediator admitted {0}", request.Type);
            var returned = result.Returned;
            returned.Should().NotBeNull("the client got the mediator's answer to {0}", request.Type);
            returned!.SenderKid.Should().StartWith(mediator.Mediator + "#");
            returned.Message.Thid.Should().Be(request.Id);
            return returned.Message;
        }

        await ExchangeAsync(mediator, clientAheadSeconds, AskAsync);
    }

    private static async Task RunOverWebSocketAsync(PickupEndpointHost mediator, ReturnRouteWebSocketClientHarness client, int clientAheadSeconds)
    {
        var recipient = mediator.Recipient;
        var options = client.ToServer(recipient) with { ReturnRoute = ReturnRouteMode.All };

        async Task<Message> AskAsync(Message request)
        {
            var result = await client.Client.SendAsync(request, options);
            (await SentStampAsync(mediator, result)).Should().Be(ClientNow(mediator, clientAheadSeconds), "the client stamps from its own clock (FR-RR-09)");
            (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Seen.Any(r => r.Message.Thid == request.Id), Timeout))
                .Should().BeTrue("the client admitted the mediator's answer to {0} on the session socket", request.Type);
            var answer = client.Recorder.Seen.Single(r => r.Message.Thid == request.Id).Message;
            answer.From.Should().Be(mediator.Mediator);
            return answer;
        }

        await ExchangeAsync(mediator, clientAheadSeconds, AskAsync);

        // Live mode: the mediator pushes an arrival at once, stamped with its clock, answering no request.
        var live = MessagePickup.CreateLiveDeliveryChange(recipient.Did, mediator.Mediator, true);
        new PickupAnswer(await AskAsync(live), Array.Empty<byte>()).Status(live.Id).LiveDelivery.Should().BeTrue();
        var pushed = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.PushedLive);
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Seen.Any(r => r.Message.Type == MessagePickup.DeliveryType && r.Message.Thid is null), Timeout))
            .Should().BeTrue("the client admitted the live push");
        var push = client.Recorder.Seen.Single(r => r.Message.Type == MessagePickup.DeliveryType && r.Message.Thid is null).Message;
        push.CreatedTime.Should().Be(ServerNow(mediator), "the mediator stamps its push with its own clock");
        var delivered = new PickupAnswer(push, Array.Empty<byte>()).Delivery(thid: null);
        await mediator.ShouldBeRelayedAsync(recipient, delivered.Single(), pushed);
        var ack = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { pushed.Id! });
        new PickupAnswer(await AskAsync(ack), Array.Empty<byte>()).Status(ack.Id).MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(0);
    }

    /// <summary>Ping, enrolment and the fetch → acknowledge loop, through <paramref name="askAsync"/> (send one request, return the mediator's answer).</summary>
    private static async Task ExchangeAsync(PickupEndpointHost mediator, int clientAheadSeconds, Func<Message, Task<Message>> askAsync)
    {
        var recipient = mediator.Recipient;

        var ping = TrustPing.CreatePing(recipient.Did, mediator.Mediator);
        (await askAsync(ping)).Type.Should().Be(TrustPing.ResponseType);

        var grant = await askAsync(CoordinateMediation.CreateMediateRequest(recipient.Did, mediator.Mediator));
        ShouldBeStampedByTheMediator(mediator, grant);
        CoordinateMediation.TryParseGrant(grant, out _).Should().BeTrue();

        var update = await askAsync(CoordinateMediation.CreateRecipientUpdate(recipient.Did, mediator.Mediator,
            new[] { new RecipientUpdate(recipient.Did, RecipientUpdateAction.Add) }));
        ShouldBeStampedByTheMediator(mediator, update);
        CoordinateMediation.TryParseRecipientUpdateResponse(update, out var results).Should().BeTrue();
        results!.Should().ContainSingle().Which.Result.Should().Be(RecipientUpdateOutcome.Success);

        var queued = await mediator.QueueAsync(recipient);

        var statusRequest = MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator);
        var status = await askAsync(statusRequest);
        ShouldBeStampedByTheMediator(mediator, status);
        new PickupAnswer(status, Array.Empty<byte>()).Status(statusRequest.Id).MessageCount.Should().Be(1);

        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var delivery = await askAsync(fetch);
        ShouldBeStampedByTheMediator(mediator, delivery);
        var delivered = new PickupAnswer(delivery, Array.Empty<byte>()).Delivery(fetch.Id);
        await mediator.ShouldBeRelayedAsync(recipient, delivered.Single(), queued);

        var ack = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { queued.Id! });
        var after = await askAsync(ack);
        ShouldBeStampedByTheMediator(mediator, after);
        new PickupAnswer(after, Array.Empty<byte>()).Status(ack.Id).MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(0);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static ReturnRouteWebSocketClientHarness WebSocketClient(
        PickupEndpointHost mediator, int clientAheadSeconds, Action<ReplayProtectionOptions>? clientReplay = null)
        => ReturnRouteWebSocketClientHarness.Create(
            mediator.Host,
            configureDidComm: b =>
            {
                // Records the mediator's Coordinate Mediation answers on the client (pickup, trust
                // ping and the test protocol already have client-side recorders).
                b.AddProtocol<CoordinateMediationRecordingHandler>();
                if (clientReplay is not null)
                    b.Services.AddOptions<ReplayProtectionOptions>().Configure(clientReplay);
            },
            clockOffset: TimeSpan.FromSeconds(clientAheadSeconds),
            holders: mediator.Recipient);

    private static long ServerNow(PickupEndpointHost mediator) => mediator.Clock.Now.ToUnixTimeSeconds();

    private static long ClientNow(PickupEndpointHost mediator, int clientAheadSeconds) => ServerNow(mediator) + clientAheadSeconds;

    /// <summary>The <c>created_time</c> of the copy the client actually sent, read by unpacking it as the mediator.</summary>
    private static async Task<long?> SentStampAsync(PickupEndpointHost mediator, SendResult result)
    {
        var server = mediator.Host.App.Services.GetRequiredService<DidCommClient>();
        return (await server.UnpackAsync(result.Packed.Message)).Message.CreatedTime;
    }

    private static void ShouldBeStampedByTheMediator(PickupEndpointHost mediator, Message answer)
        => answer.CreatedTime.Should().Be(ServerNow(mediator), "the mediator stamps {0} with its own clock (FR-RR-09)", answer.Type);

    private static IReadOnlyList<string> ServerDrops(PickupEndpointHost mediator, string messageId)
        => mediator.Host.DropLines(messageId).Where(l => l.Contains("Inbound " + messageId + " dropped", StringComparison.Ordinal)).ToList();

    private static bool IsClientFutureDatedDrop(string line)
        => line.Contains("Pushed message ", StringComparison.Ordinal)
           && line.Contains("dropped: replay admission " + nameof(ReplayAdmission.FutureDated), StringComparison.Ordinal);
}
