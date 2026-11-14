using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Protocols.Routing;
using DidComm.Transports;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-2 (#96) against the real #95 mediator: fetch bounds (<c>limit</c> 1 and 100, the mediator's
/// own batch cap), a fetch loop until drained with more undecryptable messages than <c>limit</c>
/// in front of the good ones, within and beyond the mediator's 60 s lease (it ends, and reaches what
/// is behind); <see cref="PickupClient.AckAsync"/> in chunks of 100 that the mediator accepts, with
/// unknown ids; and re-wrapped (double-packaged) deliveries of hostile shapes a sender can build —
/// eight self-addressed layers (opened), nine (refused), an authcrypt layer, a layer with two
/// attachments, a layer naming someone else and one naming another DID of ours (all failures, left
/// queued, still counted) — plus a replay through a re-wrapping mediator (returned once).
/// </summary>
public sealed class PickupClientFetchAndRewrapQa2Tests
{
    [Fact]
    public async Task Limit_one_takes_one_per_fetch_and_the_loop_ends_drained()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var sent = new List<string>();
        for (var i = 0; i < 3; i++)
            sent.Add((await PickupE2E.SendAsync(mediator, alice, recipient, $"n{i}")).Id);

        var got = new List<string>();
        PickupBatch batch;
        var fetches = 0;
        do
        {
            batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 1);
            fetches++;
            batch.Items.Count.Should().BeLessThanOrEqualTo(1);
            got.AddRange(batch.Items.Select(i => i.Unpacked!.Message.Id));
        }
        while (!batch.Drained);

        got.Should().Equal(sent);
        fetches.Should().Be(4);
        batch.Status!.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Limit_100_is_capped_by_the_mediators_batch_and_a_mediator_allowing_100_delivers_100_then_the_rest()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m =>
        {
            m.Pickup.MaxDeliveryBatch = 100;
            m.Pickup.MaxQueueMessages = 200;
            m.Pickup.MaxDeliveryBytes = 4 * 1024 * 1024;
        });
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.RequestTimeout = TimeSpan.FromMinutes(2));
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var sent = new List<string>();
        for (var i = 0; i < 101; i++)
        {
            var note = alice.Note(recipient.Did, $"n{i}");
            sent.Add(note.Id);
            await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await alice.AuthcryptAsync(note, recipient.Did));
        }

        var first = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 100);
        first.Items.Should().HaveCount(100).And.OnlyContain(i => i.Unpacked != null);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1, "the 100 were acknowledged in one messages-received");
        var second = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 100);
        second.Items.Should().ContainSingle();
        (await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 100)).Drained.Should().BeTrue();
        first.Items.Concat(second.Items).Select(i => i.Unpacked!.Message.Id).Should().Equal(sent);
    }

    [Fact]
    public async Task A_loop_until_drained_gets_past_more_failures_than_its_limit_within_and_beyond_the_lease_and_always_ends()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        for (var i = 0; i < 5; i++)
            await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);
        var good1 = await PickupE2E.SendAsync(mediator, alice, recipient, "behind 1");
        var good2 = await PickupE2E.SendAsync(mediator, alice, recipient, "behind 2");

        var (got, failures, fetches) = await DrainAsync(harness, mediator, bob, limit: 2);
        got.Should().Equal(good1.Id, good2.Id);
        failures.Should().HaveCount(5);
        fetches.Should().BeLessThanOrEqualTo(6, "the loop ends");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(5);

        // Beyond the lease: the failures are deliverable again; a new good message is behind them.
        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        var good3 = await PickupE2E.SendAsync(mediator, alice, recipient, "behind 3");
        (got, failures, fetches) = await DrainAsync(harness, mediator, bob, limit: 2);
        got.Should().Equal(good3.Id);
        failures.Should().HaveCount(5, "every failure is reported again after its lease");
        fetches.Should().BeLessThanOrEqualTo(5);

        // A caller that gives up on them discards them by id.
        (await harness.Pickup.AckAsync(mediator.Did, bob.Did, failures)).MessageCount.Should().Be(0);
    }

    private static async Task<(List<string> Got, List<string> Failures, int Fetches)> DrainAsync(MediatorClientHarness harness, MediatorTestServer mediator, TestPeer bob, int limit)
    {
        var got = new List<string>();
        var failures = new List<string>();
        var fetches = 0;
        PickupBatch batch;
        do
        {
            batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit);
            fetches++;
            fetches.Should().BeLessThan(20, "a loop until drained must end");
            foreach (var item in batch.Items)
            {
                if (item.Unpacked is { } unpacked)
                    got.Add(unpacked.Message.Id);
                else
                    failures.Add(item.Id);
            }
        }
        while (!batch.Drained);
        return (got, failures, fetches);
    }

    [Fact]
    public async Task AckAsync_sends_chunks_of_100_the_mediator_accepts_and_unknown_ids_are_harmless()
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);
        var failed = (await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10)).Items.Should().ContainSingle().Subject;
        failed.Error.Should().NotBeNull();

        // 250 ids: 249 unknown (well-formed queue ids nobody holds) and the real one last.
        var ids = Enumerable.Range(0, 249).Select(i => Guid.NewGuid().ToString("N")).Append(failed.Id).ToArray();
        var status = await harness.Pickup.AckAsync(mediator.Did, bob.Did, ids);

        status.MessageCount.Should().Be(0, "the real id was in the last chunk");
        await observer.WaitForAsync(bob.Did, 4);
        observer.KindsFrom(bob.Did).Count(k => k == "messages-received").Should().Be(3, "250 ids are three messages of at most 100");
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Sender_built_self_addressed_layers_open_up_to_eight_and_every_hostile_shape_is_a_failure_left_queued()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var grant = new MediationGrant(mediator.Did, await mediator.RequestMediationAsync(bob));
        var b1 = await PickupE2E.NewRecipientForAsync(mediator, harness, grant);
        var b2 = await PickupE2E.NewRecipientForAsync(mediator, harness, grant);
        await mediator.AddRecipientsAsync(bob, new[] { b1.Did, b2.Did });
        var alice = await harness.NewAliceAsync();
        var carol = await harness.NewAliceAsync();
        var note = alice.Note(b1.Did, "deep inside");
        var inner = await alice.AuthcryptAsync(note, b1.Did);

        // 1. Eight layers a sender built exactly as a re-wrapping mediator would: opened.
        await Qa2.PostForwardAsync(mediator, alice, b1.Did, await Qa2.SelfAddressedLayersAsync(alice.Client, b1.Did, inner, 8));
        // 2. Nine: one beyond the bound.
        await Qa2.PostForwardAsync(mediator, alice, b1.Did, await Qa2.SelfAddressedLayersAsync(alice.Client, b1.Did, inner, 9));
        // 3. A self-addressed layer authcrypted by Alice.
        await Qa2.PostForwardAsync(mediator, alice, b1.Did, await Qa2.LayerAsync(alice.Client, b1.Did, b1.Did, inner, from: alice.Did));
        // 4. A self-addressed layer with two attachments.
        await Qa2.PostForwardAsync(mediator, alice, b1.Did, await Qa2.SelfAddressedLayersAsync(alice.Client, b1.Did, inner, 1, extraAttachment: true));
        // 5. A layer for b1 naming Carol as next (relay work, not a delivery for us).
        await Qa2.PostForwardAsync(mediator, alice, b1.Did, await Qa2.LayerAsync(alice.Client, b1.Did, carol.Did, inner));
        // 6. A layer decrypted by b1 naming b2, another DID this agent holds.
        await Qa2.PostForwardAsync(mediator, alice, b1.Did, await Qa2.LayerAsync(alice.Client, b1.Did, b2.Did, inner));

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10);

        batch.Items.Should().HaveCount(6);
        batch.Items[0].Error.Should().BeNull();
        batch.Items[0].Unpacked!.Message.Id.Should().Be(note.Id);
        batch.Items[0].Unpacked!.Authenticated.Should().BeTrue("the innermost message is Alice's authcrypt, not the anonymous layers");
        batch.Items.Skip(1).Should().OnlyContain(i => i.Unpacked == null && i.Error != null);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(5, "every hostile shape is left queued and counted");
        mediator.LeakingLogLines(b1.Did, b2.Did, carol.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task Through_a_rewrapping_mediator_a_replayed_anonymous_message_is_returned_once()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = Qa2.NoteWithId(Guid.NewGuid().ToString(), null, recipient.Did, "anonymous, replayed");
        var inner = await Qa2.AnoncryptAsync(alice.Client, note, recipient.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, recipient.Did, inner);
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(2, "each copy was re-wrapped afresh and stored");

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10);

        batch.Items.Should().ContainSingle().Which.Unpacked!.Message.Id.Should().Be(note.Id);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
    }
}
