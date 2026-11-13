using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #95 acceptance on time, with the clock injected through <c>DidCommOptions.Clock</c> (no sleeps):
/// a forward that expired before relay is dropped (<c>Dropped-Expired</c>) and nothing is queued; a
/// queued payload whose forward expiry passes is no longer counted or delivered and the purge removes
/// it (FR-ROUTE-09); <c>delay_milli</c> holds a message until it is due, and the hold is capped at
/// <c>MediatorOptions.MaxForwardDelay</c> (FR-ROUTE-10, owner decision); a record older than the
/// mailbox age limit is never delivered and is removed (FR-PICK-10, owner decision).
/// </summary>
public sealed class MediatorExpiryDelayAndAgeTests
{
    // ---- expiry -------------------------------------------------------------------------------

    [Fact]
    public async Task A_forward_that_expired_before_relay_is_dropped_as_Dropped_Expired_and_nothing_is_queued()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var t0 = mediator.Clock.Now;
        var (packed, forward) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did),
            f => f.ExpiresTime = t0.AddSeconds(60).ToUnixTimeSeconds());

        // At the dispatcher: the unpack clock is before the expiry (UnpackAsync accepts it), the
        // dispatch runs on a clock past it — the handler's own FR-ROUTE-09 check drops it.
        var client = mediator.App.Services.GetRequiredService<DidCommClient>();
        var dispatcher = mediator.App.Services.GetRequiredService<ProtocolDispatcher>();
        var unpacked = await client.UnpackAsync(packed);
        var late = new DidCommOptions { Clock = () => t0.AddSeconds(120) };
        (await dispatcher.DispatchAsync(unpacked, client, late)).Result.Should().Be(DispatchResult.NoReply, "the drop is silent");
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedExpired);
        mediator.Store.MessageCount.Should().Be(0);
        mediator.Logs.Entries.Should().Contain(e => e.Contains(forward.Id) && e.Contains("expired"));

        // Over HTTP the forward's own expires_time is judged first, at unpack (FR-API-05): the uniform 400.
        mediator.Clock.Advance(TimeSpan.FromSeconds(120));
        var response = await mediator.PostAsync(packed);
        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.Body.Should().BeEmpty();
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedExpired);
        mediator.Store.MessageCount.Should().Be(0);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_queued_message_whose_forward_expiry_passes_is_purged_and_no_longer_counted_or_delivered()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var t0 = mediator.Clock.Now;
        await PostForwardAsync(mediator, alice, bob, f => f.ExpiresTime = t0.AddSeconds(60).ToUnixTimeSeconds());
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);

        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        mediator.Store.MessageCount.Should().Be(1, "nothing removes a record on its own between purges");
        await mediator.Hosted.PurgeOnceAsync(CancellationToken.None);
        mediator.Store.MessageCount.Should().Be(0);
        mediator.Measurements.Sum(DidComm.Mediator.Pipeline.MediatorMetrics.QueuePurgedInstrument).Should().Be(1);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
        (await mediator.FetchAsync(bob)).Should().BeEmpty();
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_queued_message_past_its_forward_expiry_is_not_counted_even_before_a_purge()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var t0 = mediator.Clock.Now;
        await PostForwardAsync(mediator, alice, bob, f => f.ExpiresTime = t0.AddSeconds(60).ToUnixTimeSeconds());

        mediator.Clock.Advance(TimeSpan.FromSeconds(60));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1, "at its expiry instant the message is still deliverable");
        mediator.Clock.Advance(TimeSpan.FromSeconds(1));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
        (await mediator.FetchAsync(bob)).Should().BeEmpty();
    }

    // ---- delay and its cap --------------------------------------------------------------------

    [Theory]
    [InlineData(500L, 500L)]
    [InlineData(3_599_999L, 3_599_999L)]
    [InlineData(3_600_000L, 3_600_000L)]
    [InlineData(3_600_001L, 3_600_000L)]
    [InlineData(long.MaxValue, 3_600_000L)]
    public async Task A_delayed_forward_is_not_deliverable_before_its_delay_which_is_capped_at_MaxForwardDelay(long delayMilli, long dueAfterMilli)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        mediator.App.Services.GetRequiredService<ForwardProcessorOptions>().MaxDelay.Should().Be(TimeSpan.FromHours(1), "MaxForwardDelay's default (1 h) reaches the processor");
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var note = await PostForwardAsync(mediator, alice, bob, Delay(delayMilli));
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);

        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0, "a message that is not due is not counted");
        (await mediator.FetchAsync(bob)).Should().BeEmpty();

        mediator.Clock.Advance(TimeSpan.FromMilliseconds(dueAfterMilli - 1));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0, "one millisecond before it is due");
        (await mediator.FetchAsync(bob)).Should().BeEmpty();

        mediator.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- mailbox age limit --------------------------------------------------------------------

    [Fact]
    public async Task A_record_older_than_the_mailbox_age_limit_is_never_delivered_and_the_purge_removes_it()
    {
        var limit = TimeSpan.FromHours(2);
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxMessageAge = limit);
        var bob = await mediator.NewPeerAsync();
        var carol = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        await mediator.EnrolAsync(carol);
        var alice = await mediator.NewSenderAsync();
        await PostForwardAsync(mediator, alice, bob);
        await PostForwardAsync(mediator, alice, carol);
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);

        // One tick before the limit, counted from admission on the mediator's clock: still there.
        mediator.Clock.Advance(limit - TimeSpan.FromTicks(1));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);

        // At the limit: removed by the purge (Carol's never asked; Bob's not swept by his last status).
        mediator.Clock.Advance(TimeSpan.FromTicks(1));
        mediator.Store.MessageCount.Should().Be(2);
        await mediator.Hosted.PurgeOnceAsync(CancellationToken.None);
        mediator.Store.MessageCount.Should().Be(0);
        mediator.Measurements.Sum(DidComm.Mediator.Pipeline.MediatorMetrics.QueuePurgedInstrument).Should().Be(2);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
        (await mediator.FetchAsync(carol)).Should().BeEmpty();
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_record_reaching_the_age_limit_is_not_counted_or_delivered_even_before_a_purge()
    {
        var limit = TimeSpan.FromHours(2);
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxMessageAge = limit);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        await PostForwardAsync(mediator, alice, bob);

        mediator.Clock.Advance(limit);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
        (await mediator.FetchAsync(bob)).Should().BeEmpty();
    }

    [Fact]
    public async Task With_the_age_limit_off_a_record_waits_for_its_recipient_however_long()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxMessageAge = null);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var note = await PostForwardAsync(mediator, alice, bob);

        mediator.Clock.Advance(TimeSpan.FromDays(400));
        await mediator.Hosted.PurgeOnceAsync(CancellationToken.None);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static Action<Message> Delay(long millis)
        => forward => (forward.AdditionalHeaders ??= new Dictionary<string, JsonElement>())["delay_milli"] = JsonSerializer.SerializeToElement(millis);

    /// <summary>Alice's note for <paramref name="to"/> in a hand-built forward (shaped by <paramref name="shape"/>), posted and relayed; returns the note.</summary>
    private static async Task<Message> PostForwardAsync(MediatorTestServer mediator, MediatorSender alice, TestPeer to, Action<Message>? shape = null)
    {
        var note = alice.Note(to.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, to.Did, await alice.AuthcryptAsync(note, to.Did), shape);
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        return note;
    }
}
