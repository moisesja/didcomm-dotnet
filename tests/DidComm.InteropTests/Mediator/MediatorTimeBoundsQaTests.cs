using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), time through the real endpoints with the clock injected (<c>DidCommOptions.Clock</c>;
/// peers stamp <c>created_time</c> from the server's clock): the <c>delay_milli</c> cap at and around
/// its bound, negative and non-integer hints, the exact not-before instant, delayed messages and live
/// push; the mailbox age limit one tick before, at and after the limit for <c>status</c>,
/// <c>delivery-request</c>, live push and the hosted purge (once, and through the loop on a manual
/// <see cref="TimeProvider"/>); and FR-ROUTE-09 expiry at every stage a forward passes through.
/// </summary>
public sealed class MediatorTimeBoundsQaTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(2);

    // ---- delay_milli and its cap -------------------------------------------------------------------

    [Theory]
    [InlineData(500L, 500L)]
    [InlineData(1_999L, 1_999L)]
    [InlineData(2_000L, 2_000L)]
    [InlineData(2_001L, 2_000L)]
    [InlineData(int.MaxValue + 1L, 2_000L)]
    [InlineData(long.MaxValue, 2_000L)]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    public async Task A_delay_is_held_until_exactly_its_not_before_and_capped_at_a_custom_MaxForwardDelay(long delayMilli, long dueAfterMilli)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.MaxForwardDelay = Cap);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();

        var note = await Qa.SendNoteAsync(mediator, alice, bob, Qa.Delay(delayMilli));
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);

        if (dueAfterMilli > 0)
        {
            mediator.Clock.Advance(TimeSpan.FromMilliseconds(dueAfterMilli - 1));
            (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0, "one millisecond before it is due");
            (await mediator.FetchAsync(bob)).Should().BeEmpty();
            mediator.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1, "exactly at its not-before instant");
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        mediator.LeakingLogLines(delivered.Id).Should().BeEmpty();
    }

    [Theory]
    [InlineData(-500L, 500L)]
    [InlineData(-1L, 1L)]
    [InlineData(-2_001L, 2_000L)]
    [InlineData(-1_000_000_000L, 2_000L)]
    [InlineData(long.MinValue, 2_000L)]
    public async Task A_negative_delay_is_due_no_later_than_its_capped_magnitude(long delayMilli, long latestDueMilli)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.MaxForwardDelay = Cap);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();

        await Qa.SendNoteAsync(mediator, alice, bob, Qa.Delay(delayMilli));
        mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued }, "no forward is refused because of its delay");

        mediator.Clock.Advance(TimeSpan.FromMilliseconds(latestDueMilli));
        (await mediator.FetchAsync(bob)).Should().ContainSingle("a random delay never exceeds min(|n|, cap)");
    }

    [Theory]
    [InlineData("500.5")]
    [InlineData("5e2")]
    [InlineData("\"500\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[500]")]
    [InlineData("1e400")]
    [InlineData("99999999999999999999999")]
    public async Task A_delay_that_is_not_an_integer_is_ignored_and_the_message_is_due_at_once(string json)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.MaxForwardDelay = Cap);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();

        JsonElement value;
        try
        {
            value = JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return; // not representable on the wire through this builder
        }
        var note = alice.Note(bob.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did), Qa.DelayRaw(value));
        var response = await mediator.PostAsync(packed);
        response.Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        (await mediator.FetchAsync(bob)).Should().ContainSingle("a hint the mediator cannot read as a whole number of milliseconds does not delay anything");
    }

    [Fact]
    public async Task A_delayed_message_is_not_pushed_live_and_a_live_recipient_fetches_it_once_due()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.MaxForwardDelay = Cap);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);

        var note = await Qa.SendNoteAsync(mediator, alice, bob, Qa.Delay(500));
        mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued }, "a message that is not due is never pushed live");

        // The socket's next frame answers this status request: nothing was pushed before it.
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).MessageCount.Should().Be(0);

        mediator.Clock.Advance(TimeSpan.FromMilliseconds(500));
        // As built (PRD FR-PICK-09): once due it waits for a delivery-request; it is not pushed then either.
        var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var delivered = (await mediator.AskOverSocketAsync(socket, bob, request)).Delivery(request.Id).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        (await MediatorDeliveryModesQaTests.AckOverSocketAsync(mediator, socket, bob, delivered.Id)).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Fact]
    public async Task A_delayed_message_is_never_pushed_before_it_is_due_and_goes_with_the_grants_next_push_once_due()
    {
        // L-020 (PR #109 review round 2, D-1): a push takes what the store hands out now, so a delayed
        // message is never pushed before it is due (FR-ROUTE-10) and, once due, goes with the grant's
        // next push like any queued message; it used to stay pickup-only for good. Over the live
        // session here (its form for a push to the recipient's own endpoint left #95, #110).
        await using var f = await QaPushFixture.StartAsync(m => m.MaxForwardDelay = Cap);
        using var socket = await f.Mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(f.Mediator, socket, f.Bob);

        var note = await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, Qa.Delay(1));
        await f.Mediator.PushesIdleAsync();
        f.Mediator.PushOutcomes.Should().BeEmpty("one millisecond of delay is enough to skip the push");
        f.Mediator.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var undelayed = await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, content: "undelayed");

        var pushed = new List<DeliveredMessage>();
        for (var i = 0; i < 2; i++)
            pushed.Add((await f.Mediator.ReceiveOverSocketAsync(socket, f.Bob)).Delivery(thid: null).Should().ContainSingle().Subject);
        var ids = new List<string>();
        foreach (var m in pushed)
            ids.Add((await MediatorRecipient.UnpackFromSenderAsync(f.B, m.Payload, f.Alice)).Message.Id);
        ids.Should().Equal(note.Id, undelayed.Id);
        await f.Mediator.PushesIdleAsync();
        f.Mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedLive, PushOutcomes.PushedLive);
        (await f.Mediator.FetchAsync(f.Bob)).Should().BeEmpty("both are leased to the live session");
        (await MediatorDeliveryModesQaTests.AckOverSocketAsync(f.Mediator, socket, f.Bob, pushed.Select(m => m.Id).ToArray())).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- mailbox age limit -------------------------------------------------------------------------

    [Fact]
    public async Task A_message_leased_by_a_delivery_request_ages_out_at_the_limit_and_its_late_acknowledgement_changes_nothing()
    {
        var limit = TimeSpan.FromHours(2);
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxMessageAge = limit);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        await Qa.SendNoteAsync(mediator, alice, bob);

        mediator.Clock.Advance(limit - TimeSpan.FromTicks(1));
        var leased = (await mediator.FetchAsync(bob)).Should().ContainSingle("one tick before the limit it is delivered").Subject;
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1, "leased, still counted");

        mediator.Clock.Advance(TimeSpan.FromTicks(1));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0, "at the limit");
        (await mediator.AcknowledgeAsync(bob, leased.Id)).MessageCount.Should().Be(0);
        mediator.Clock.Advance(TimeSpan.FromMinutes(5));
        (await mediator.FetchAsync(bob)).Should().BeEmpty("after the limit, and after its lease would have ended");
        await mediator.Hosted.PurgeOnceAsync(CancellationToken.None);
        mediator.Store.MessageCount.Should().Be(0);
        mediator.LeakingLogLines(leased.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_push_left_unacknowledged_ages_out_at_the_limit()
    {
        var limit = TimeSpan.FromHours(2);
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxMessageAge = limit);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);

        await Qa.SendNoteAsync(mediator, alice, bob);
        var pushed = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        // L-020 (PR #109 review R1-2): the forward pump now only stores (Queued); the push workers push it live (Pushed-Live).
        await mediator.PushesIdleAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedLive);

        mediator.Clock.Advance(limit - TimeSpan.FromTicks(1));
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).MessageCount.Should().Be(1);
        mediator.Clock.Advance(TimeSpan.FromTicks(1));
        var after = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, after)).Status(after.Id).MessageCount.Should().Be(0, "the live lease does not keep an aged-out record alive");
        (await MediatorDeliveryModesQaTests.AckOverSocketAsync(mediator, socket, bob, pushed.Id)).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await mediator.FetchAsync(bob)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_hosted_purge_loop_on_a_manual_TimeProvider_removes_aged_out_records_and_only_those()
    {
        var time = new QaManualTimeProvider();
        var limit = TimeSpan.FromHours(2);
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m =>
            {
                m.Pickup.MaxMessageAge = limit;
                m.PurgeInterval = TimeSpan.FromMinutes(1);
            };
            o.ConfigureServices = services => services.AddSingleton<TimeProvider>(time);
        });
        await time.WaitForScheduledTimersAsync(1, MediatorTestServer.Timeout);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        // Admitted at t0, t0 + 1 tick and t0 + 30 min on the mediator's clock.
        await Qa.SendNoteAsync(mediator, alice, bob, content: "oldest");
        mediator.Clock.Advance(TimeSpan.FromTicks(1));
        await Qa.SendNoteAsync(mediator, alice, bob, content: "old");
        mediator.Clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1));
        await Qa.SendNoteAsync(mediator, alice, bob, content: "young");
        mediator.Store.MessageCount.Should().Be(3);

        // At t0 + limit: "oldest" is at the limit, "old" one tick before it. One loop round.
        mediator.Clock.Advance(limit - TimeSpan.FromMinutes(30));
        time.Advance(TimeSpan.FromMinutes(1));
        await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 2, "the loop's round removed the record at the limit");
        mediator.Measurements.Sum(DidComm.Mediator.Pipeline.MediatorMetrics.QueuePurgedInstrument).Should().Be(1, "one tick before the limit is kept");

        // One tick later, the next round removes "old" only.
        mediator.Clock.Advance(TimeSpan.FromTicks(1));
        time.Advance(TimeSpan.FromMinutes(1));
        await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 1, "the next round removed the record now at the limit");
        mediator.Measurements.Sum(DidComm.Mediator.Pipeline.MediatorMetrics.QueuePurgedInstrument).Should().Be(2);
        var left = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, left.Payload, alice)).Message.Body!["content"]!.GetValue<string>().Should().Be("young");
        mediator.Logs.Entries.Should().Contain(e => e.StartsWith("Mediator purge:", StringComparison.Ordinal));
        mediator.LeakingLogLines(left.Id).Should().BeEmpty();
    }

    // ---- FR-ROUTE-09 at every stage ----------------------------------------------------------------

    [Fact]
    public async Task A_forward_that_expires_while_its_202_is_held_is_dropped_as_expired_and_nothing_is_queued()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var t0 = mediator.Clock.Now;
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did), Qa.Expires(t0.AddSeconds(10)));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;

        var post = Task.Run(() => mediator.PostAsync(packed));
        await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 1, "admitted while the 202 is held");
        mediator.Clock.Advance(TimeSpan.FromSeconds(11));
        gate.SetResult();
        (await post).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedExpired);
        mediator.Store.MessageCount.Should().Be(0);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_forward_that_expires_while_queued_behind_a_slow_sink_is_dropped_as_expired()
    {
        var store = new QaParkingStore();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IMessageStore>(store));
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var t0 = mediator.Clock.Now;
        var parked = store.ParkNextEnqueue();

        await Qa.SendNoteAsync(mediator, alice, bob, flush: false, content: "first");
        await parked.Arrived.WaitAsync(MediatorTestServer.Timeout);
        await Qa.SendNoteAsync(mediator, alice, bob, Qa.Expires(t0.AddSeconds(10)), flush: false, content: "expiring");
        mediator.Handler.Outstanding.Should().Be(2);
        mediator.Clock.Advance(TimeSpan.FromSeconds(11));
        parked.Release();
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.DroppedExpired);
        store.Inner.MessageCount.Should().Be(1);
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Body!["content"]!.GetValue<string>().Should().Be("first");
    }

    [Fact]
    public async Task A_message_that_expires_while_leased_is_not_counted_not_delivered_again_and_purged()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var t0 = mediator.Clock.Now;
        await Qa.SendNoteAsync(mediator, alice, bob, Qa.Expires(t0.AddSeconds(10)));

        var leased = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        mediator.Clock.Advance(TimeSpan.FromSeconds(10));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1, "at its expires_time it is still held");
        mediator.Clock.Advance(TimeSpan.FromSeconds(1));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
        mediator.Clock.Advance(TimeSpan.FromMinutes(5));
        (await mediator.FetchAsync(bob)).Should().BeEmpty("its lease ended after it expired; it is not handed out again");
        (await mediator.AcknowledgeAsync(bob, leased.Id)).MessageCount.Should().Be(0);
        await mediator.Hosted.PurgeOnceAsync(CancellationToken.None);
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task ExpiresClockSkew_is_applied_the_same_way_at_unpack_in_the_handler_and_in_the_queue()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o =>
            o.PostConfigureServices = s => s.Configure<DidComm.Facade.DidCommOptions>(d => d.ExpiresClockSkew = TimeSpan.FromSeconds(30)));
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var t0 = mediator.Clock.Now;

        // Ten seconds past its expires_time, within the 30 s skew: accepted, relayed and queued.
        await Qa.SendNoteAsync(mediator, alice, bob, Qa.Expires(t0.AddSeconds(-10)));
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);

        // Expired once now - skew > expires: at +20 s it is still held, at +21 s it is not.
        mediator.Clock.Advance(TimeSpan.FromSeconds(20));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);
        mediator.Clock.Advance(TimeSpan.FromSeconds(1));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);

        // Beyond the skew at unpack: the uniform 400.
        var (late, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did), Qa.Expires(mediator.Clock.Now.AddSeconds(-31)));
        (await mediator.PostAsync(late)).Status.Should().Be(HttpStatusCode.BadRequest);
    }
}
