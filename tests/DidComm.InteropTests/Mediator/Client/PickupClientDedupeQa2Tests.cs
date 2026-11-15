using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-2 (#96): duplicate deliveries against the real #95 mediator. Delivery is at-least-once, and
/// the clients' duplicate filter must suppress true repeats (a replayed forward stored twice, in a
/// fetch and in a live stream; an anonymous inner message replayed; a message fetched whose
/// acknowledgement was lost and that then comes to a live stream) while never suppressing a message
/// the application has not received: the same message id from two different senders (authcrypt and
/// anoncrypt) is delivered twice. Also a message encrypted to two of the recipient's own DIDs at once
/// (one JWE, two queue entries), unfiltered and under a <c>recipient_did</c> filter.
/// </summary>
public sealed class PickupClientDedupeQa2Tests
{
    [Fact]
    public async Task The_same_message_id_from_two_senders_is_delivered_twice_authcrypt_and_anoncrypt()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var carol = await harness.NewAliceAsync();

        // Authcrypt: the same id, two senders.
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await alice.AuthcryptAsync(Qa2.NoteWithId("qa2-same-id", alice.Did, recipient.Did, "from alice"), recipient.Did));
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await carol.AuthcryptAsync(Qa2.NoteWithId("qa2-same-id", carol.Did, recipient.Did, "from carol"), recipient.Did));
        // Anoncrypt: the same id, two different messages without a sender.
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await Qa2.AnoncryptAsync(alice.Client, Qa2.NoteWithId("qa2-anon-id", null, recipient.Did, "anonymous one"), recipient.Did));
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await Qa2.AnoncryptAsync(carol.Client, Qa2.NoteWithId("qa2-anon-id", null, recipient.Did, "anonymous two"), recipient.Did));

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10);

        batch.Items.Should().HaveCount(4).And.OnlyContain(i => i.Unpacked != null);
        batch.Items.Select(i => i.Unpacked!.Message.Body!["content"]!.GetValue<string>())
            .Should().Equal("from alice", "from carol", "anonymous one", "anonymous two");
        batch.Items[2].Unpacked!.AnonymousSender.Should().BeTrue();
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_forward_replayed_to_a_live_stream_is_yielded_once_and_both_copies_are_acknowledged(bool anonymous)
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await observer.WaitForAsync(bob.Did, 2);

        var note = Qa2.NoteWithId(Guid.NewGuid().ToString(), anonymous ? null : alice.Did, recipient.Did, "replayed");
        var inner = anonymous ? await Qa2.AnoncryptAsync(alice.Client, note, recipient.Did) : await alice.AuthcryptAsync(note, recipient.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, recipient.Did, inner);
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted, "a forward is exempt from the replay rule: the replay is stored too");
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        (await PickupE2E.Within(first)).Should().BeTrue();
        stream.Current.Unpacked!.Message.Id.Should().Be(note.Id);
        var next = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "the repeat was acknowledged with the original");
        next.IsCompleted.Should().BeFalse("the repeat is not yielded");
        await PickupE2E.StopAsync(stream, next, cts);
    }

    [Fact]
    public async Task A_message_fetched_whose_acknowledgement_was_lost_is_acknowledged_and_not_yielded_by_a_later_live_stream()
    {
        var store = new Qa2Store(new InMemoryMessageStore(new MessagePickupOptions()));
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IMessageStore>(store));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "once");
        var later = await PickupE2E.SendAsync(mediator, alice, recipient, "later");

        store.LoseNext();
        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 1);
        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        Qa2.MessageCount(mediator).Should().Be(2, "the acknowledgement was lost");

        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var item = await PickupE2E.NextAsync(stream);
        PickupE2E.ShouldBeAlicesNote(item, alice, recipient, later);
        var next = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "the repeat and the new one were acknowledged");
        store.AckedIds.Count(id => id == batch.Items[0].Id).Should().Be(2, "the repeat was acknowledged again, by the stream");
        await PickupE2E.StopAsync(stream, next, cts);
    }

    // One JWE authcrypted to both DIDs (as a sender that encrypts once for several recipients does),
    // relayed to each in its own forward (next = that DID): two queue entries, the same bytes.
    private static async Task PostToBothAsync(MediatorTestServer mediator, MediatorSender alice, DidComm.Messages.Message note, string first, string second)
    {
        var inner = (await alice.Client.PackEncryptedAsync(note, new DidComm.Facade.PackEncryptedOptions(Recipients: new[] { first, second }, From: alice.Did, Forward: false))).Message;
        await Qa2.PostForwardAsync(mediator, alice, first, inner);
        await Qa2.PostForwardAsync(mediator, alice, second, inner);
    }

    /// <summary>
    /// <b>Q2-1</b>, as decided by the owner on the L-063 stop (#88, option 1-A): the <c>recipientDid</c>
    /// filter passes a message only when one of the filter DID's own keys opened it; the envelope's
    /// recipient labels are not evidence (ADV1-2, ADV1-3). One message Alice encrypts to two DIDs Bob
    /// holds, b1 and b2 (one JWE, relayed in one forward per DID: two queue entries), opens with b1's
    /// key, the first held key the JWE lists. So under b2's filter b2's copy is a failure "for another
    /// of your DIDs" and stays queued; the message is accepted under b1's filter (b1's copy) or
    /// without a filter; and the host discards b2's copy with <c>AckAsync</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Q2_1_A_message_encrypted_to_two_of_our_DIDs_fails_under_the_second_DIDs_filter_is_accepted_under_the_first_or_unfiltered_and_is_discarded_with_AckAsync(bool unfiltered)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var grant = new MediationGrant(mediator.Did, await mediator.RequestMediationAsync(bob));
        var b1 = await PickupE2E.NewRecipientForAsync(mediator, harness, grant);
        var b2 = await PickupE2E.NewRecipientForAsync(mediator, harness, grant);
        await mediator.AddRecipientsAsync(bob, new[] { b1.Did, b2.Did });
        var alice = await harness.NewAliceAsync();

        var note = alice.Note(b1.Did, "to both");
        note.To = new List<string> { b1.Did, b2.Did };
        await PostToBothAsync(mediator, alice, note, b1.Did, b2.Did);
        (await mediator.StatusAsync(bob, b2.Did)).MessageCount.Should().Be(1, "the mediator queued a copy for b2");

        // Under b2's filter: b1's key opened b2's copy, so it is reported as another DID's and kept.
        var forB2 = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, recipientDid: b2.Did);
        var failed = forB2.Items.Should().ContainSingle().Subject;
        var error = failed.Error.Should().BeOfType<MediationProtocolException>().Subject;
        error.Message.Should().Contain("another of your DIDs").And.Contain("AckAsync").And.Contain(error.RequestId!);
        error.Message.Should().NotContain(b1.Did).And.NotContain(b2.Did).And.NotContain(bob.Did);
        (await mediator.StatusAsync(bob, b2.Did)).MessageCount.Should().Be(1, "a failure is not acknowledged: b2's copy stays queued");

        // Under b1's filter, or without one, the message is accepted (b1's copy; b2's is still leased
        // to the failed fetch, so the mediator passes over it).
        var taken = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, recipientDid: unfiltered ? null : b1.Did);
        var item = taken.Items.Should().ContainSingle().Subject;
        item.Error.Should().BeNull();
        item.Unpacked!.Message.Id.Should().Be(note.Id);
        item.Unpacked.RecipientKid.Should().StartWith(b1.Did + "#", "b1's key opened it");
        (await mediator.StatusAsync(bob, b1.Did)).MessageCount.Should().Be(0, "b1's copy was acknowledged");

        // The host gives up on b2's copy.
        await harness.Pickup.AckAsync(mediator.Did, bob.Did, new[] { failed.Id });
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0, "AckAsync discarded b2's copy");
    }

    /// <summary>
    /// Observation for Q2-1 without a filter: both copies decrypt with b1's key, so they have the same
    /// duplicate-filter key; the application sees the message once (reported as decrypted for b1) and
    /// b2's copy is acknowledged as a repeat. Pinned as current behaviour (the filter's per-DID scope is
    /// the decrypting key, not the DID the mediator queued the copy for).
    /// </summary>
    [Fact]
    public async Task Without_a_filter_a_message_encrypted_to_two_of_our_DIDs_is_returned_once_and_both_copies_are_acknowledged()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var grant = new MediationGrant(mediator.Did, await mediator.RequestMediationAsync(bob));
        var b1 = await PickupE2E.NewRecipientForAsync(mediator, harness, grant);
        var b2 = await PickupE2E.NewRecipientForAsync(mediator, harness, grant);
        await mediator.AddRecipientsAsync(bob, new[] { b1.Did, b2.Did });
        var alice = await harness.NewAliceAsync();
        var note = alice.Note(b1.Did, "to both");
        note.To = new List<string> { b1.Did, b2.Did };
        await PostToBothAsync(mediator, alice, note, b1.Did, b2.Did);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(2);

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10);

        batch.Items.Should().ContainSingle().Which.Unpacked!.RecipientKid.Should().StartWith(b1.Did + "#");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
    }
}
