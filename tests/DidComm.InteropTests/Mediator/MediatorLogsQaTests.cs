using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), what the mediator writes to its logs over a busy run that touches every outcome the
/// pipeline has — queued, delivered live, pushed directly, push failed, dropped unknown, expired, over
/// quota, too large, unacknowledged, a revoked grant — at every level, Trace included: no DID, no key
/// id, no queue record id and no push endpoint (FR-ROUTE-11, FR-PICK-09, #95 observability).
/// </summary>
public sealed class MediatorLogsQaTests
{
    private const string EndpointToken = "push-endpoint-capability-7f3a9c";

    [Fact]
    public async Task No_log_line_at_any_level_carries_a_DID_a_key_id_or_a_queue_record_id()
    {
        var (mediator, sensitive, f) = await RunBusyScenarioAsync();
        await using (f)
            Qa.LinesWith(mediator.Logs, sensitive).Should().BeEmpty();
    }

    [Fact]
    public async Task The_mediator_packages_own_lines_never_carry_the_push_endpoint()
    {
        // Everything except the HTTP transport's Debug line and the framework's IHttpClientFactory
        // request logging (see DEFECT 4 below).
        var (mediator, _, f) = await RunBusyScenarioAsync();
        await using (f)
        {
            mediator.Logs.Entries.Where(e => !IsHttpTransportLine(e))
                .Should().NotContain(e => e.Contains(EndpointToken, StringComparison.Ordinal));
        }
    }

    // QA-2 DEFECT 4 (fixed, #95 Q-9): the direct-push endpoint (a host secret, possibly a capability
    // URL) reached the logs through the library's HttpDidCommTransport Debug line, which wrote the
    // full URI; it now writes the scheme and the status only. Scoped to the library's own lines: the
    // framework's IHttpClientFactory request logging (categories System.Net.Http.HttpClient.*, e.g.
    // "Start processing HTTP request POST {uri}" at Information) also prints the URI. That is the
    // framework's line, not removed by the library; the operations guidance tells hosts to filter
    // those categories.
    [Fact]
    public async Task No_log_line_at_any_level_carries_the_push_endpoint()
    {
        var (mediator, _, f) = await RunBusyScenarioAsync();
        await using (f)
        {
            mediator.Logs.CategorizedRecords
                .Where(r => !r.Category.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal))
                .Select(r => r.Text)
                .Should().NotContain(t => t.Contains(EndpointToken, StringComparison.Ordinal));
            mediator.Logs.CategorizedRecords.Should().Contain(r => r.Text.StartsWith("HttpDidCommTransport POST", StringComparison.Ordinal),
                "the transport's own line is written (Debug) and is in scope of the check");
        }
    }

    private static bool IsHttpTransportLine(string line)
        => line.StartsWith("HttpDidCommTransport POST", StringComparison.Ordinal)
           || line.StartsWith("Start processing HTTP request", StringComparison.Ordinal)
           || line.StartsWith("Sending HTTP request", StringComparison.Ordinal)
           || line.StartsWith("Received HTTP response", StringComparison.Ordinal)
           || line.StartsWith("End processing HTTP request", StringComparison.Ordinal);

    private static async Task<(MediatorTestServer Mediator, List<string> Sensitive, QaPushFixture Fixture)> RunBusyScenarioAsync()
    {
        var f = await QaPushFixture.StartAsync(m => m.Pickup.MaxQueueMessages = 4, map: false);
        var mediator = f.Mediator;
        f.Directory.Map(f.Bob.Did, f.Inbox.Endpoint("/" + EndpointToken));
        var sensitive = new List<string> { mediator.Did, f.Bob.Did, f.B.Did, f.Alice.Did };
        sensitive.AddRange(mediator.Kids);

        // Pushed directly (2xx), then a failed push (500).
        await Qa.SendNoteAsync(mediator, f.Alice, f.B, content: "pushed");
        await mediator.PushesIdleAsync();
        f.Inbox.Status = 500;
        await Qa.SendNoteAsync(mediator, f.Alice, f.B, content: "push fails");
        await mediator.PushesIdleAsync();

        // Picked up and acknowledged; leased and left.
        foreach (var m in await mediator.FetchAsync(f.Bob))
        {
            sensitive.Add(m.Id);
            await mediator.AcknowledgeAsync(f.Bob, m.Id);
        }

        // Live, unacknowledged; then the socket closes.
        var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, f.Bob);
        await Qa.SendNoteAsync(mediator, f.Alice, f.B, content: "live");
        var pushed = (await mediator.ReceiveOverSocketAsync(socket, f.Bob)).Delivery(thid: null).Single();
        sensitive.Add(pushed.Id);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        socket.Dispose();

        // Unknown next, expired, too large, over quota, unacknowledged.
        var stranger = await mediator.NewPeerAsync();
        sensitive.Add(stranger.Did);
        await Qa.SendNoteAsync(mediator, f.Alice, stranger);
        var t0 = mediator.Clock.Now;
        await Qa.SendNoteAsync(mediator, f.Alice, f.B, Qa.Expires(t0.AddSeconds(5)), content: "expires");
        mediator.Clock.Advance(TimeSpan.FromSeconds(6));
        await mediator.Hosted.PurgeOnceAsync(CancellationToken.None);
        await Qa.PostForwardAsync(mediator, (await f.Alice.ForwardAsync(mediator.Did, f.B.Did, Qa.JsonPayloadOfSize(mediator.Queue.MaxAdmissiblePayloadBytes + 1))).Packed);
        for (var i = 0; i < 5; i++)
            await Qa.SendNoteAsync(mediator, f.Alice, f.B, flush: false, content: $"fill {i}");
        await mediator.FlushAsync();
        mediator.Controls.AbortNextPost = true;
        var (aborted, _) = await f.Alice.ForwardAsync(mediator.Did, f.B.Did, await f.Alice.AuthcryptAsync(f.Alice.Note(f.B.Did), f.B.Did));
        try
        {
            await mediator.PostAsync(aborted);
        }
        catch (Exception)
        {
            // Aborted before the 202.
        }
        await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 0, "the aborted forward was released");
        await mediator.PushesIdleAsync();

        // Fetch what is left, then revoke and purge.
        foreach (var m in await mediator.FetchAsync(f.Bob))
            sensitive.Add(m.Id);
        await mediator.Queue.RevokeAndPurgeAsync(f.Bob.Did);

        mediator.Outcomes.Should().Contain(new[]
        {
            ForwardOutcomes.Queued, ForwardOutcomes.DroppedUnknown, ForwardOutcomes.DroppedTooLarge,
            ForwardOutcomes.DroppedQuota, ForwardOutcomes.DroppedUnacknowledged,
        });
        // L-020 (PR #109 review R1-2): the live delivery is now a push outcome (Pushed-Live), not a forward outcome.
        mediator.PushOutcomes.Should().Contain(new[] { DidComm.Mediator.Pipeline.PushOutcomes.PushedLive, DidComm.Mediator.Pipeline.PushOutcomes.Pushed, DidComm.Mediator.Pipeline.PushOutcomes.Failed });
        return (mediator, sensitive, f);
    }
}
