using System.Net;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Protocols.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #96 acceptance over HTTP, against the #95 mediator on a TestServer: Bob enrols, a DID whose
/// service is <c>DidCommServiceBuilder.ForMediation(grant)</c> is registered, Alice sends to it with
/// <c>SendAsync</c> (one forward for the mediator), and Bob's <see cref="PickupClient"/> fetches,
/// unpacks and acknowledges — after which the mediator's status is zero. Also: a message Bob cannot
/// open is returned as a failure, never acknowledged and still counted; repeats (the same message
/// relayed twice, and a redelivery after a lost acknowledgement and the lease) are acknowledged and
/// not returned again; live delivery over HTTP is refused with no request reaching the mediator; and
/// every request carries <c>created_time</c> from the client's clock (FR-RR-09).
/// </summary>
public sealed class PickupClientHttpEndToEndTests
{
    [Fact]
    public async Task Enrol_send_fetch_and_acknowledge_then_the_status_is_zero()
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var first = await PickupE2E.SendAsync(mediator, alice, recipient, "one");
        var second = await PickupE2E.SendAsync(mediator, alice, recipient, "two");
        var pickup = harness.Pickup;

        (await pickup.GetStatusAsync(mediator.Did, bob.Did)).MessageCount.Should().Be(2);
        var batch = await pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);

        batch.Drained.Should().BeFalse();
        batch.Items.Should().HaveCount(2);
        PickupE2E.ShouldBeAlicesNote(batch.Items[0], alice, recipient, first);
        PickupE2E.ShouldBeAlicesNote(batch.Items[1], alice, recipient, second);
        batch.Items[0].Unpacked!.Message.Body!["content"]!.GetValue<string>().Should().Be("one");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0, "the fetch acknowledged what it returned (FR-PICK-11)");
        mediator.Store.MessageCount.Should().Be(0);

        var drained = await pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
        drained.Drained.Should().BeTrue();
        drained.Status!.MessageCount.Should().Be(0);
        (await pickup.GetStatusAsync(mediator.Did, bob.Did)).MessageCount.Should().Be(0);

        // The client's five requests, and the test's own status check (hand-rolled, also stamped).
        await observer.WaitForAsync(6);
        observer.Seen.Where(s => s.From == bob.Did).Select(s => s.Type).Should().Equal(
            MessagePickup.StatusRequestType, MessagePickup.DeliveryRequestType, MessagePickup.MessagesReceivedType,
            MessagePickup.StatusRequestType, MessagePickup.DeliveryRequestType, MessagePickup.StatusRequestType);
        PickupE2E.ShouldAllBeStampedWith(observer, bob.Did, mediator.Clock.Now);
        mediator.LeakingLogLines(recipient.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_limit_takes_that_many_and_a_loop_until_drained_takes_everything()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var notes = new List<string>();
        for (var i = 0; i < 5; i++)
            notes.Add((await PickupE2E.SendAsync(mediator, alice, recipient, $"n{i}")).Id);

        var seen = new List<string>();
        PickupBatch batch;
        var rounds = 0;
        do
        {
            batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 2);
            batch.Items.Count.Should().BeLessThanOrEqualTo(2);
            seen.AddRange(batch.Items.Select(i => i.Unpacked!.Message.Id));
        }
        while (!batch.Drained && ++rounds < 10);

        seen.Should().Equal(notes, "oldest first, each once");
        rounds.Should().Be(3);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_message_Bob_cannot_open_is_a_failure_never_acknowledged_and_still_counted()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var good = await PickupE2E.SendAsync(mediator, alice, recipient, "good");
        await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);
        var later = await PickupE2E.SendAsync(mediator, alice, recipient, "later");

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);

        batch.Items.Should().HaveCount(3);
        PickupE2E.ShouldBeAlicesNote(batch.Items[0], alice, recipient, good);
        batch.Items[1].Unpacked.Should().BeNull();
        batch.Items[1].Error.Should().NotBeNull();
        PickupE2E.ShouldBeAlicesNote(batch.Items[2], alice, recipient, later);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1, "the failure was not acknowledged: the mediator still holds it");

        // Within the lease the failure is not delivered again; discarding it is the application's call.
        (await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10)).Drained.Should().BeTrue();
        (await harness.Pickup.AckAsync(mediator.Did, bob.Did, new[] { batch.Items[1].Id })).MessageCount.Should().Be(0);
        mediator.LeakingLogLines(recipient.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_comes_back_after_the_lease_and_is_reported_again()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);

        var first = (await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10)).Items.Should().ContainSingle().Subject;
        first.Error.Should().NotBeNull();
        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        var again = (await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10)).Items.Should().ContainSingle().Subject;

        again.Id.Should().Be(first.Id);
        again.Error.Should().NotBeNull("a failure is never remembered as handled");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task The_same_message_relayed_twice_is_returned_once_and_both_copies_are_acknowledged()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = alice.Note(recipient.Did);
        var inner = await alice.AuthcryptAsync(note, recipient.Did);
        for (var copy = 0; copy < 2; copy++)
        {
            // Two forwards around the same authcrypted message (a sender or relay retrying).
            var (packed, _) = await alice.ForwardAsync(mediator.Did, recipient.Did, inner);
            (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        }
        await mediator.FlushAsync();
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(2);

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10);

        batch.Items.Should().ContainSingle();
        PickupE2E.ShouldBeAlicesNote(batch.Items[0], alice, recipient, note);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0, "the repeat was acknowledged too");
    }

    [Fact]
    public async Task A_message_redelivered_after_a_lost_acknowledgement_and_the_lease_is_acknowledged_and_not_returned_again()
    {
        var store = new AckLosingStore(new InMemoryMessageStore(new MessagePickupOptions()));
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = services => services.AddSingleton<IMessageStore>(store));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient);

        store.LoseNextAcknowledgement();
        var first = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10);
        PickupE2E.ShouldBeAlicesNote(first.Items.Should().ContainSingle().Subject, alice, recipient, note);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1, "the acknowledgement was lost");

        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        var second = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10);

        second.Items.Should().BeEmpty("the client remembers the message it returned (DedupeCapacity)");
        second.Drained.Should().BeFalse("the mediator did deliver something: the repeat");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0, "the repeat was acknowledged");
    }

    [Fact]
    public async Task The_recipient_filter_returns_only_that_DIDs_messages()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var routing = await mediator.RequestMediationAsync(bob);
        var grant = new DidComm.Mediator.Coordination.MediationGrant(mediator.Did, routing);
        var b1 = await PickupE2E.NewRecipientForAsync(mediator, harness, grant);
        var b2 = await PickupE2E.NewRecipientForAsync(mediator, harness, grant);
        await mediator.AddRecipientsAsync(bob, new[] { b1.Did, b2.Did });
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, b1, "for b1");
        var forB2 = await PickupE2E.SendAsync(mediator, alice, b2, "for b2");

        (await harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, recipientDid: b2.Did)).MessageCount.Should().Be(1);
        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, recipientDid: b2.Did);

        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, b2, forB2);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1, "b1's message is left alone");
    }

    [Fact]
    public async Task Live_delivery_over_HTTP_is_refused_with_no_request_reaching_the_mediator()
    {
        var posts = 0;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigurePipeline = app => app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            Interlocked.Increment(ref posts);
            await next(context);
        }));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var before = Volatile.Read(ref posts);

        await FluentActions.Awaiting(() => harness.Pickup.SetLiveDeliveryAsync(mediator.Did, bob.Did, true)).Should().ThrowAsync<InvalidOperationException>();
        await using (var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did).GetAsyncEnumerator())
            await FluentActions.Awaiting(() => PickupE2E.Within(stream.MoveNextAsync())).Should().ThrowAsync<InvalidOperationException>();

        Volatile.Read(ref posts).Should().Be(before, "the route is refused before anything is sent");
    }
}
