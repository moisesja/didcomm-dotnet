using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), the bounds a forward meets on its way through the pipeline: the largest admissible
/// payload (<c>MessagePickupQueue.MaxAdmissiblePayloadBytes</c>) is queued and actually deliverable by
/// every delivery mode, one byte more is dropped (<c>Dropped-TooLarge</c>); a full per-grant quota
/// (messages or bytes, counting leased and live-pushed records) drops with <c>Dropped-Quota</c> and still
/// answers 202; and the forward handler's own backlog (1 024 forwards, 64 MiB) behind a slow sink drops
/// the next forward with <c>Dropped-Saturated</c>, 202, and loses none of those it admitted.
/// </summary>
public sealed class MediatorBoundsQaTests
{
    public enum Mode
    {
        Http,
        WebSocket,
        Live,
    }

    [Theory]
    [InlineData(Mode.Http)]
    [InlineData(Mode.WebSocket)]
    [InlineData(Mode.Live)]
    public async Task A_payload_of_exactly_MaxAdmissiblePayloadBytes_is_queued_and_delivered_and_one_byte_more_is_dropped(Mode mode)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;
        max.Should().BeGreaterThan(400_000, "a little over half of the 1 MiB delivery budget");

        WebSocket? socket = null;
        if (mode != Mode.Http)
        {
            socket = await mediator.ConnectAsync();
            if (mode == Mode.Live)
                await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
        }

        var over = Qa.JsonPayloadOfSize(max + 1);
        await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, bob.Did, over)).Packed);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedTooLarge);
        mediator.Store.MessageCount.Should().Be(0);

        var exact = Qa.JsonPayloadOfSize(max);
        await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, bob.Did, exact)).Packed);
        await mediator.FlushAsync();

        DeliveredMessage delivered;
        switch (mode)
        {
            case Mode.Http:
                mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedTooLarge, ForwardOutcomes.Queued);
                delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle("the largest admissible message fits one delivery").Subject;
                break;
            case Mode.WebSocket:
                mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedTooLarge, ForwardOutcomes.Queued);
                var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
                var answer = await mediator.AskOverSocketAsync(socket!, bob, request);
                answer.Packed.Length.Should().BeLessThanOrEqualTo(1024 * 1024);
                delivered = answer.Delivery(request.Id).Should().ContainSingle().Subject;
                break;
            default:
                var push = await mediator.ReceiveOverSocketAsync(socket!, bob);
                push.Packed.Length.Should().BeLessThanOrEqualTo(1024 * 1024);
                delivered = push.Delivery(thid: null).Should().ContainSingle().Subject;
                mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedTooLarge, ForwardOutcomes.DeliveredLive);
                break;
        }
        Encoding.UTF8.GetString(delivered.Payload.Span).Should().Be(exact, "the payload is relayed byte for byte");
        if (socket is not null)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
            socket.Dispose();
        }
    }

    [Fact]
    public async Task A_full_message_quota_drops_with_Dropped_Quota_and_202_counting_leased_and_live_pushed_records()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxQueueMessages = 3);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var carol = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(carol);
        var alice = await mediator.NewSenderAsync();

        await Qa.SendNoteAsync(mediator, alice, bob, content: "leased");
        var leased = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        using var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
        await Qa.SendNoteAsync(mediator, alice, bob, content: "live");
        var pushed = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        await Qa.SendNoteAsync(mediator, alice, bob, Qa.Delay(60_000), content: "not yet due");
        await Qa.SendNoteAsync(mediator, alice, bob, content: "over quota");
        await Qa.SendNoteAsync(mediator, alice, carol, content: "another grant");

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.DeliveredLive, ForwardOutcomes.Queued, ForwardOutcomes.DroppedQuota, ForwardOutcomes.Queued);
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).MessageCount.Should().Be(2, "the not-yet-due message holds quota without being reported");

        // Acknowledging frees room: the next message is accepted (and pushed live).
        (await MediatorDeliveryModesQaTests.AckOverSocketAsync(mediator, socket, bob, leased.Id)).MessageCount.Should().Be(1);
        await Qa.SendNoteAsync(mediator, alice, bob, content: "room again");
        mediator.Outcomes.Last().Should().Be(ForwardOutcomes.DeliveredLive);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        mediator.LeakingLogLines(leased.Id, pushed.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task A_full_byte_quota_drops_with_Dropped_Quota_and_202()
    {
        const int Size = 10_000;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxQueueBytes = 2 * Size + 1);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var payload = Qa.JsonPayloadOfSize(Size);

        for (var i = 0; i < 3; i++)
            await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, bob.Did, payload)).Packed);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued, ForwardOutcomes.DroppedQuota);
        var status = await mediator.StatusAsync(bob);
        status.MessageCount.Should().Be(2);
        status.TotalBytes.Should().Be(2 * Size, "the recipient sees its quota in total_bytes");
    }

    [Fact]
    public async Task Behind_a_slow_sink_the_handler_admits_1024_forwards_drops_the_next_as_saturated_and_relays_every_admitted_one()
    {
        Qa.RaiseThreadPoolFloor();
        // A host's own store enforces its own quotas: build it with room for every admitted forward.
        var store = new QaParkingStore(new InMemoryMessageStore(new MessagePickupOptions { MaxQueueMessages = 2_000, MaxStoreMessages = 2_000 }));
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IMessageStore>(store));
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        // Anoncrypted forwards are outside replay admission: the same bytes can be posted again.
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        var parked = store.ParkNextEnqueue();

        await Qa.PostForwardAsync(mediator, packed);
        await parked.Arrived.WaitAsync(MediatorTestServer.Timeout);
        for (var batch = 0; batch < 1023; batch += 31)
        {
            var count = Math.Min(31, 1023 - batch);
            await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(() => Qa.PostForwardAsync(mediator, packed))));
        }
        mediator.Handler.Outstanding.Should().Be(1024);
        await Qa.PostForwardAsync(mediator, packed);
        mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.DroppedSaturated }, "the 1025th is dropped on the request, still 202");

        parked.Release();
        await mediator.FlushAsync();
        mediator.Outcome(ForwardOutcomes.Queued).Should().Be(1024);
        mediator.Outcome(ForwardOutcomes.DroppedSaturated).Should().Be(1);
        store.Inner.MessageCount.Should().Be(1024);
        mediator.Handler.Outstanding.Should().Be(0);
    }

    [Fact]
    public async Task Behind_a_slow_sink_the_handler_byte_budget_of_64_MiB_drops_the_forward_that_would_exceed_it()
    {
        Qa.RaiseThreadPoolFloor();
        const int Payload = 6 * 1024 * 1024;
        var store = new QaParkingStore();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.MaxReceiveBytes = 9 * 1024 * 1024;
            o.ConfigureServices = s => s.AddSingleton<IMessageStore>(store);
        });
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (small, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        var (big, _) = await alice.ForwardAsync(mediator.Did, bob.Did, Qa.JsonPayloadOfSize(Payload));
        var parked = store.ParkNextEnqueue();

        const long Budget = 64L * 1024 * 1024;
        await Qa.PostForwardAsync(mediator, small);
        await parked.Arrived.WaitAsync(MediatorTestServer.Timeout);
        var smallBytes = mediator.Handler.OutstandingBytes;
        await Qa.PostForwardAsync(mediator, big);
        var bigBytes = mediator.Handler.OutstandingBytes - smallBytes;
        bigBytes.Should().BeGreaterThan(Payload, "a forward is charged its plaintext size");
        var admitted = 1;
        while (mediator.Outcome(ForwardOutcomes.DroppedSaturated) == 0)
        {
            await Qa.PostForwardAsync(mediator, big);
            if (mediator.Outcome(ForwardOutcomes.DroppedSaturated) == 0)
                admitted++;
            admitted.Should().BeLessThan(20, "the byte budget stops admission long before this");
        }
        admitted.Should().Be((int)((Budget - smallBytes) / bigBytes), "exactly as many as fit in 64 MiB beside the one in the sink");
        mediator.Handler.OutstandingBytes.Should().Be(smallBytes + admitted * bigBytes);
        mediator.Handler.Outstanding.Should().Be(admitted + 1, "far below the 1 024-item bound: the bytes decided");

        parked.Release();
        await mediator.FlushAsync();
        mediator.Handler.OutstandingBytes.Should().Be(0);
        mediator.Outcome(ForwardOutcomes.DroppedSaturated).Should().Be(1);
        mediator.Outcomes.Should().HaveCount(admitted + 2, "every admitted forward ends with exactly one outcome");
    }

    [Fact]
    public async Task A_forward_over_MaxReceiveBytes_is_refused_413_before_anything_is_admitted()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.MaxReceiveBytes = 128 * 1024);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, Qa.JsonPayloadOfSize(128 * 1024));

        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().BeEmpty();
        mediator.Handler.Outstanding.Should().Be(0);
    }
}
