using AwesomeAssertions;
using DidComm.InteropTests.Mediator;
using DidComm.InteropTests.Mediator.Client;
using DidComm.Mediator;
using DidComm.Protocols.Routing;
using Xunit;

namespace DidComm.InteropTests.Adv98.D5;

/// <summary>
/// #98 red team, D5 attack 3: Mallory captured Alice's inner authcrypted message for Bob's recipient
/// DID and posts it again inside her own forward, to a re-wrapping mediator. Forwards are outside
/// replay admission (documented), and in rewrap mode each copy reaches Bob in a fresh layer (other
/// bytes), so the only thing between the replay and Bob's application is the pickup client's
/// duplicate filter, keyed on (authenticated sender, message id). Bob's application must see Alice's
/// message once, attributed to Alice, and both copies must be acknowledged.
/// </summary>
public sealed class D5CapturedInnerRewrapReplayTests
{
    [Fact]
    public async Task Alices_captured_inner_in_Mallorys_forward_is_yielded_once_as_Alices_and_both_copies_are_acknowledged()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var mallory = await harness.NewAliceAsync();

        var note = Qa2.NoteWithId(Guid.NewGuid().ToString(), alice.Did, recipient.Did, "alice, once");
        var inner = await alice.AuthcryptAsync(note, recipient.Did);
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, inner);
        // Mallory's own forward (her bytes, her anoncrypt) around Alice's captured inner.
        await Qa2.PostForwardAsync(mediator, mallory, recipient.Did, inner);
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10);
        var again = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10);

        batch.Items.Should().ContainSingle("the copy in Mallory's layer is the same (sender, id) and is filtered");
        var item = batch.Items[0];
        item.Unpacked.Should().NotBeNull();
        item.Unpacked!.Message.Id.Should().Be(note.Id);
        item.Unpacked.Authenticated.Should().BeTrue();
        item.Unpacked.SenderKid.Should().StartWith(alice.Did + "#", "the identity is Alice's, never Mallory's who re-posted it");
        again.Items.Should().BeEmpty();
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0, "both copies were acknowledged");
    }
}
