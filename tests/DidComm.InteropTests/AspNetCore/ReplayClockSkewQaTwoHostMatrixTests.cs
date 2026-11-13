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
using Xunit.Abstractions;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #105 QA pass (client side), kept as a regression matrix: two hosts whose clocks are apart, end to end, over HTTP and WebSocket,
/// in both directions. A real recipient client (its own container, clock offset from the mediator's)
/// runs ping (<c>return_route: all</c>), enrolment, the pickup loop and, over WebSocket, live mode
/// and a live push against the TestServer mediator (Coordinate Mediation 3.0 + Message Pickup 3.0).
/// Every step's actual outcome is recorded and compared with the documented rule:
/// the receiving side admits a message stamped ahead of its clock by at most its own
/// <c>ReplayProtectionOptions.ClockSkew</c> (default 60 s) and behind by at most <c>Window</c>
/// (300 s); an HTTP <c>SendResult.Returned</c> is never replay-admitted; the ping-response is out of
/// the FR-RR-08 scope. Run against <c>main</c> (where the guard read <c>ExpiresClockSkew</c>, 0), the
/// default-skew cases at ±30, ±59 and ±60 seconds failed; with <c>ClockSkew = 0</c> on both hosts this
/// build gives <c>main</c>'s answers.
/// </summary>
public sealed class ReplayClockSkewQaTwoHostMatrixTests
{
    private const int Window = 300;
    private const int DefaultSkew = 60;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly ITestOutputHelper _output;

    public ReplayClockSkewQaTwoHostMatrixTests(ITestOutputHelper output) => _output = output;

    public enum Wire
    {
        Http,
        WebSocket,
    }

    public static TheoryData<Wire, int> DefaultSkewCases()
    {
        var data = new TheoryData<Wire, int>();
        foreach (var wire in new[] { Wire.Http, Wire.WebSocket })
        {
            foreach (var ahead in new[] { 0, 30, -30, 59, -59, 60, -60, 61, -61, 90, -90, -300, -301 })
                data.Add(wire, ahead);
        }
        return data;
    }

    /// <summary>Default options on both hosts; <paramref name="clientAheadSeconds"/> &gt; 0 is the client's clock ahead of the mediator's.</summary>
    [Theory]
    [MemberData(nameof(DefaultSkewCases))]
    public async Task Default_skews_two_hosts_follow_the_documented_rule(Wire wire, int clientAheadSeconds)
        => await RunAndCompareAsync(wire, clientAheadSeconds, serverSkew: null, clientSkew: null, expectedServerSkew: DefaultSkew, expectedClientSkew: DefaultSkew);

    /// <summary>ClockSkew = 0 on both hosts: the outcome vector main produces with its defaults (ExpiresClockSkew = 0).</summary>
    [Theory]
    [InlineData(Wire.Http, 30)]
    [InlineData(Wire.Http, -30)]
    [InlineData(Wire.WebSocket, 30)]
    [InlineData(Wire.WebSocket, -30)]
    [InlineData(Wire.WebSocket, 1)]
    [InlineData(Wire.WebSocket, -1)]
    public async Task Zero_skew_on_both_hosts_reproduces_the_strict_rule(Wire wire, int clientAheadSeconds)
        => await RunAndCompareAsync(wire, clientAheadSeconds, serverSkew: 0, clientSkew: 0, expectedServerSkew: 0, expectedClientSkew: 0);

    /// <summary>A widened receiver skew admits the larger offsets; the other side's setting is irrelevant to that direction.</summary>
    [Theory]
    [InlineData(Wire.Http, 90, 120, 0)]
    [InlineData(Wire.WebSocket, 90, 120, 0)]
    [InlineData(Wire.WebSocket, -90, 0, 120)]
    [InlineData(Wire.WebSocket, -90, 120, 60)]
    [InlineData(Wire.Http, -90, 0, 0)]
    public async Task Widened_receiver_skew_admits_the_larger_offset(Wire wire, int clientAheadSeconds, int serverSkew, int clientSkew)
    {
        await RunAndCompareAsync(wire, clientAheadSeconds, serverSkew, clientSkew, serverSkew, clientSkew);
    }

    // ---- the runner -------------------------------------------------------------------------------

    private async Task RunAndCompareAsync(Wire wire, int clientAheadSeconds, int? serverSkew, int? clientSkew, int expectedServerSkew, int expectedClientSkew)
    {
        await using var mediator = await PickupEndpointHost.StartAsync(replay: serverSkew is int s ? o => SetReplaySkew(o, s) : null);
        var actual = wire == Wire.Http
            ? await RunOverHttpAsync(mediator, clientAheadSeconds, clientSkew)
            : await RunOverWebSocketAsync(mediator, clientAheadSeconds, clientSkew);
        var expected = actual.Keys.ToDictionary(step => step, step => Expected(wire, clientAheadSeconds, expectedServerSkew, expectedClientSkew, step));

        var actualLine = string.Join(", ", actual.Select(kv => kv.Key + "=" + kv.Value));
        var expectedLine = string.Join(", ", expected.Select(kv => kv.Key + "=" + kv.Value));
        _output.WriteLine($"{wire} clientAhead={clientAheadSeconds} serverSkew={serverSkew?.ToString() ?? "default"} clientSkew={clientSkew?.ToString() ?? "default"}");
        _output.WriteLine("actual:   " + actualLine);
        _output.WriteLine("expected: " + expectedLine);

        actualLine.Should().Be(expectedLine);
        mediator.Host.LeakingLogLines().Should().BeEmpty();
    }

    /// <summary>The documented rule for one step.</summary>
    private static string Expected(Wire wire, int clientAhead, int serverSkew, int clientSkew, string step)
    {
        // The client stamps with its clock; the mediator judges it.
        string? serverRefusal = clientAhead > serverSkew ? "server:FutureDated"
            : -clientAhead > Window ? "server:TooOld"
            : null;
        // The mediator stamps with its clock; over WebSocket the client judges an in-scope answer.
        string? clientRefusal = wire == Wire.Http ? null
            : -clientAhead > clientSkew ? "client:FutureDated"
            : clientAhead > Window ? "client:TooOld"
            : null;

        return step switch
        {
            "ping" => serverRefusal ?? "ok", // the ping-response is outside FR-RR-08 on the client
            "queue" => serverRefusal is null ? nameof(PickupDeliveryOutcome.Queued) : nameof(PickupDeliveryOutcome.NotRegistered),
            "live-push" => serverRefusal is not null ? nameof(PickupDeliveryOutcome.NotRegistered) : clientRefusal ?? "ok",
            _ => serverRefusal ?? clientRefusal ?? "ok",
        };
    }

    private async Task<Dictionary<string, string>> RunOverHttpAsync(PickupEndpointHost mediator, int clientAheadSeconds, int? clientSkew)
    {
        await using var client = ReturnRouteClientHarness.Create(
            mediator.Host,
            configureDidComm: clientSkew is int s ? b => b.Services.AddOptions<ReplayProtectionOptions>().Configure(o => SetReplaySkew(o, s)) : null,
            clockOffset: TimeSpan.FromSeconds(clientAheadSeconds),
            holders: mediator.Recipient);
        var options = client.ToServer(mediator.Recipient) with { ReturnRoute = ReturnRouteMode.All };

        async Task<string> AskAsync(Message request)
        {
            var result = await client.Client.SendAsync(request, options);
            if (result.Returned is { } returned)
            {
                returned.Message.Thid.Should().Be(request.Id);
                returned.SenderKid.Should().StartWith(mediator.Mediator + "#");
                return "ok";
            }
            result.Transport.HttpStatusCode.Should().Be(202);
            (await ReturnRouteTestHost.WaitUntilAsync(() => ServerDrops(mediator, request.Id).Count > 0, Timeout)).Should().BeTrue("a 202 without a body is a replay drop here");
            return "server:" + Reason(ServerDrops(mediator, request.Id)[0]);
        }

        return await ExchangeAsync(mediator, AskAsync, live: null);
    }

    private async Task<Dictionary<string, string>> RunOverWebSocketAsync(PickupEndpointHost mediator, int clientAheadSeconds, int? clientSkew)
    {
        await using var client = ReturnRouteWebSocketClientHarness.Create(
            mediator.Host,
            configureDidComm: b =>
            {
                b.AddProtocol<CoordinateMediationRecordingHandler>();
                if (clientSkew is int s)
                    b.Services.AddOptions<ReplayProtectionOptions>().Configure(o => SetReplaySkew(o, s));
            },
            clockOffset: TimeSpan.FromSeconds(clientAheadSeconds),
            holders: mediator.Recipient);
        var options = client.ToServer(mediator.Recipient) with { ReturnRoute = ReturnRouteMode.All };

        async Task<string> AskAsync(Message request)
        {
            var before = ClientReplayDrops(mediator).Count;
            await client.Client.SendAsync(request, options);
            var settled = await ReturnRouteTestHost.WaitUntilAsync(
                () => client.Recorder.Seen.Any(r => r.Message.Thid == request.Id)
                      || ServerDrops(mediator, request.Id).Count > 0
                      || ClientReplayDrops(mediator).Count > before,
                Timeout);
            if (!settled)
                return "timeout";
            if (client.Recorder.Seen.SingleOrDefault(r => r.Message.Thid == request.Id) is { } seen)
            {
                seen.Message.From.Should().Be(mediator.Mediator);
                return "ok";
            }
            if (ServerDrops(mediator, request.Id) is { Count: > 0 } serverLines)
                return "server:" + Reason(serverLines[0]);
            return "client:" + Reason(ClientReplayDrops(mediator)[before]);
        }

        async Task<string> LivePushAsync()
        {
            var before = ClientReplayDrops(mediator).Count;
            var relayed = await mediator.PackFromAliceAsync(mediator.Recipient);
            var result = await mediator.Queue.DeliverAsync(mediator.Recipient.Did, relayed.Payload);
            if (result.Outcome != PickupDeliveryOutcome.PushedLive)
                return result.Outcome.ToString();
            var settled = await ReturnRouteTestHost.WaitUntilAsync(
                () => client.Recorder.Seen.Any(r => r.Message.Type == MessagePickup.DeliveryType && r.Message.Thid is null)
                      || ClientReplayDrops(mediator).Count > before,
                Timeout);
            if (!settled)
                return "timeout";
            if (client.Recorder.Seen.Any(r => r.Message.Type == MessagePickup.DeliveryType && r.Message.Thid is null))
                return "ok";
            return "client:" + Reason(ClientReplayDrops(mediator)[before]);
        }

        return await ExchangeAsync(mediator, AskAsync, LivePushAsync);
    }

    /// <summary>Ping, enrolment, queue, status, fetch, acknowledge, and (WebSocket) live mode + a live push. Each step's outcome, in order.</summary>
    private static async Task<Dictionary<string, string>> ExchangeAsync(PickupEndpointHost mediator, Func<Message, Task<string>> askAsync, Func<Task<string>>? live)
    {
        var recipient = mediator.Recipient;
        var outcomes = new Dictionary<string, string>();

        outcomes["ping"] = await askAsync(TrustPing.CreatePing(recipient.Did, mediator.Mediator));
        outcomes["mediate-request"] = await askAsync(CoordinateMediation.CreateMediateRequest(recipient.Did, mediator.Mediator));
        outcomes["recipient-update"] = await askAsync(CoordinateMediation.CreateRecipientUpdate(recipient.Did, mediator.Mediator,
            new[] { new RecipientUpdate(recipient.Did, RecipientUpdateAction.Add) }));

        var relayed = await mediator.PackFromAliceAsync(recipient);
        var queued = await mediator.Queue.DeliverAsync(recipient.Did, relayed.Payload);
        outcomes["queue"] = queued.Outcome.ToString();

        outcomes["status-request"] = await askAsync(MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator));
        outcomes["delivery-request"] = await askAsync(MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10));
        outcomes["messages-received"] = await askAsync(MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { queued.MessageId ?? "none" }));
        if (queued.Outcome == PickupDeliveryOutcome.Queued)
            mediator.Store.MessageCount.Should().Be(0, "whatever the client saw, the mediator admitted and executed the acknowledgement");

        if (live is not null)
        {
            outcomes["live-mode"] = await askAsync(MessagePickup.CreateLiveDeliveryChange(recipient.Did, mediator.Mediator, true));
            outcomes["live-push"] = await live();
        }
        return outcomes;
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static void SetReplaySkew(ReplayProtectionOptions options, int seconds)
        => options.ClockSkew = TimeSpan.FromSeconds(seconds);

    private static IReadOnlyList<string> ServerDrops(PickupEndpointHost mediator, string messageId)
        => mediator.Host.Logs.Entries.Where(l => l.Contains("Inbound " + messageId + " dropped: replay admission", StringComparison.Ordinal)).ToList();

    private static IReadOnlyList<string> ClientReplayDrops(PickupEndpointHost mediator)
        => mediator.Host.Logs.Entries.Where(l => l.StartsWith("Pushed message ", StringComparison.Ordinal)
                                                 && l.Contains("dropped: replay admission", StringComparison.Ordinal)).ToList();

    private static string Reason(string dropLine)
    {
        const string Marker = "replay admission ";
        var start = dropLine.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
        var end = dropLine.IndexOf(' ', start);
        return dropLine[start..(end < 0 ? dropLine.Length : end)];
    }
}
