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
/// pipeline has — queued, delivered live, dropped unknown, expired, over quota, too large,
/// unacknowledged, a revoked grant — at every level, Trace included: no DID, no key id and no queue
/// record id (FR-ROUTE-11, FR-PICK-09, #95 observability). That the HTTP transport's own line never
/// carries an endpoint URI (QA-2 defect 4, Q-9) is pinned by HttpTransportAcceptedLogTests.
/// </summary>
public sealed class MediatorLogsQaTests
{
    [Fact]
    public async Task No_log_line_at_any_level_carries_a_DID_a_key_id_or_a_queue_record_id()
    {
        var (mediator, sensitive, f) = await RunBusyScenarioAsync();
        await using (f)
            Qa.LinesWith(mediator.Logs, sensitive).Should().BeEmpty();
    }

    private static async Task<(MediatorTestServer Mediator, List<string> Sensitive, QaPushFixture Fixture)> RunBusyScenarioAsync()
    {
        var f = await QaPushFixture.StartAsync(m => m.Pickup.MaxQueueMessages = 4);
        var mediator = f.Mediator;
        var sensitive = new List<string> { mediator.Did, f.Bob.Did, f.B.Did, f.Alice.Did };
        sensitive.AddRange(mediator.Kids);

        // Queued with no live session (nothing is pushed), then picked up and acknowledged.
        await Qa.SendNoteAsync(mediator, f.Alice, f.B, content: "queued");
        await Qa.SendNoteAsync(mediator, f.Alice, f.B, content: "queued too");
        await mediator.PushesIdleAsync();
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
        mediator.PushOutcomes.Should().Contain(DidComm.Mediator.Pipeline.PushOutcomes.PushedLive);
        return (mediator, sensitive, f);
    }
}
