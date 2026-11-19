using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Mediator;
using DidComm.Protocols.Routing;
using Xunit;

namespace DidComm.InteropTests.Adv98.D5;

/// <summary>
/// #98 red team, D5 attacks 1, 2 and AGENTS.md §4 at the receiving side of a re-wrapping mediator
/// (<see cref="RewrapMode.ReanoncryptToNext"/>). Forwards are anonymous, so anyone can build one: the
/// question is whether a swapped or forged layer can make Bob's agent accept a message that was
/// not sent to him, or stop Carol from getting hers.
/// - self-consistent forgery: Mallory's own, well-formed forward naming Bob around Carol's JWE;
/// - post-sign tamper: one byte of the layer the mediator re-wrapped for Bob changed in transit;
/// - a forged inner layer, decrypted with Bob's key B1, whose <c>next</c> names Bob's other DID B2.
/// </summary>
public sealed class D5RewrapSwapTests
{
    private static Task<MediatorTestServer> RewrapMediatorAsync()
        => MediatorTestServer.StartAsync(o => o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext);

    [Fact]
    public async Task A_forged_forward_naming_Bob_around_Carols_JWE_fails_for_Bob_and_Carol_still_gets_hers()
    {
        await using var mediator = await RewrapMediatorAsync();
        var bobAgent = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bobAgent)).Should().Equal(mediator.Did);
        var b = await mediator.NewRecipientAsync();
        await mediator.AddRecipientsAsync(bobAgent, new[] { b.Did });
        var carolAgent = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(carolAgent)).Should().Equal(mediator.Did);
        var c = await mediator.NewRecipientAsync();
        await mediator.AddRecipientsAsync(carolAgent, new[] { c.Did });
        var alice = await mediator.NewSenderAsync();
        var mallory = await mediator.NewSenderAsync();

        var carolsNote = alice.Note(c.Did, "for carol only");
        var forCarol = await alice.AuthcryptAsync(carolsNote, c.Did);
        var (genuine, _) = await alice.ForwardAsync(mediator.Did, c.Did, forCarol);
        // Mallory captured Carol's JWE and re-addresses it: a well-formed forward with next = B.
        var (swapped, _) = await mallory.ForwardAsync(mediator.Did, b.Did, forCarol);

        (await mediator.PostAsync(swapped)).Status.Should().Be(HttpStatusCode.Accepted);
        (await mediator.PostAsync(genuine)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);

        var toBob = (await mediator.FetchAsync(bobAgent)).Should().ContainSingle().Subject;
        var toCarol = (await mediator.FetchAsync(carolAgent)).Should().ContainSingle().Subject;

        var bobOpen = await Record.ExceptionAsync(() => b.Client.UnpackDeliveredAsync(Text(toBob), CancellationToken.None));
        DeliveredUnpack carolOpen = default;
        var carolError = await Record.ExceptionAsync(async () => carolOpen = await c.Client.UnpackDeliveredAsync(Text(toCarol), CancellationToken.None));

        bobOpen.Should().BeAssignableTo<DidCommException>("Bob holds no key for Carol's JWE, so the swapped delivery never becomes a message");
        carolError.Should().BeNull();
        carolOpen.LayersOpened.Should().Be(1);
        carolOpen.Result.Message.Id.Should().Be(carolsNote.Id);
        carolOpen.Result.Authenticated.Should().BeTrue();
        carolOpen.Result.SenderKid.Should().StartWith(alice.Did + "#");
    }

    [Fact]
    public async Task A_byte_changed_in_the_layer_rewrapped_for_Bob_fails_and_the_untouched_copy_opens()
    {
        await using var mediator = await RewrapMediatorAsync();
        var bobAgent = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bobAgent)).Should().Equal(mediator.Did);
        var b = await mediator.NewRecipientAsync();
        await mediator.AddRecipientsAsync(bobAgent, new[] { b.Did });
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(b.Did);
        var (forward, _) = await alice.ForwardAsync(mediator.Did, b.Did, await alice.AuthcryptAsync(note, b.Did));

        (await mediator.PostAsync(forward)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        var delivered = (await mediator.FetchAsync(bobAgent)).Should().ContainSingle().Subject;
        var genuine = Text(delivered);

        var tamperedCiphertext = await Record.ExceptionAsync(() => b.Client.UnpackDeliveredAsync(Tamper(genuine, "ciphertext"), CancellationToken.None));
        var tamperedTag = await Record.ExceptionAsync(() => b.Client.UnpackDeliveredAsync(Tamper(genuine, "tag"), CancellationToken.None));
        var tamperedProtected = await Record.ExceptionAsync(() => b.Client.UnpackDeliveredAsync(Tamper(genuine, "protected"), CancellationToken.None));
        var control = await b.Client.UnpackDeliveredAsync(genuine, CancellationToken.None);

        tamperedCiphertext.Should().BeAssignableTo<DidCommException>();
        tamperedTag.Should().BeAssignableTo<DidCommException>();
        tamperedProtected.Should().BeAssignableTo<DidCommException>();
        control.LayersOpened.Should().Be(1);
        control.Result.Message.Id.Should().Be(note.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_layer_Bob_decrypts_with_B1_but_naming_B2_as_next_is_refused_and_the_same_shape_naming_B1_opens(bool nextIsB2)
    {
        await using var mediator = await RewrapMediatorAsync();
        var bobAgent = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bobAgent)).Should().Equal(mediator.Did);
        var b1 = await mediator.NewRecipientAsync();
        var b2 = await mediator.NewRecipientAsync();
        await mediator.AddRecipientsAsync(bobAgent, new[] { b1.Did, b2.Did });
        var alice = await mediator.NewSenderAsync();
        var mallory = await mediator.NewSenderAsync();

        // Alice's genuine message to B1. Mallory wraps it in a forward anoncrypted to B1 whose next
        // is B2 (the forgery) or B1 (the control), and routes that through the mediator to B1.
        var note = alice.Note(b1.Did);
        var forB1 = await alice.AuthcryptAsync(note, b1.Did);
        var (forgedLayer, _) = await mallory.ForwardAsync(b1.Did, nextIsB2 ? b2.Did : b1.Did, forB1);
        var (outer, _) = await mallory.ForwardAsync(mediator.Did, b1.Did, forgedLayer);

        (await mediator.PostAsync(outer)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        var delivered = (await mediator.FetchAsync(bobAgent)).Should().ContainSingle().Subject;

        DeliveredUnpack opened = default;
        var error = await Record.ExceptionAsync(async () => opened = await b1.Client.UnpackDeliveredAsync(Text(delivered), CancellationToken.None));

        if (nextIsB2)
        {
            error.Should().BeOfType<ConsistencyException>("a forward decrypted with B1's key that names B2 is not a layer re-wrapped for B1");
        }
        else
        {
            error.Should().BeNull();
            opened.LayersOpened.Should().Be(2, "the mediator's layer and Mallory's self-addressed layer");
            opened.Result.Message.Id.Should().Be(note.Id);
            opened.Result.SenderKid.Should().StartWith(alice.Did + "#");
        }
    }

    /// <summary>
    /// D5 attack 4 over the endpoint: a forward whose chain carries <paramref name="selfLayers"/>
    /// forwards addressed to the mediator itself (the received one included) around the forward to B.
    /// Up to <see cref="ForwardHandler.MaxSelfAddressedLayers"/> are opened and B gets Alice's message
    /// in one re-wrapped layer; one more is <c>Dropped-Malformed</c> and nothing is queued.
    /// </summary>
    [Theory]
    [InlineData(ForwardHandler.MaxSelfAddressedLayers)]
    [InlineData(ForwardHandler.MaxSelfAddressedLayers + 1)]
    public async Task Self_addressed_layers_are_opened_up_to_the_cap_and_one_more_is_dropped_as_malformed(int selfLayers)
    {
        await using var mediator = await RewrapMediatorAsync();
        var bobAgent = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bobAgent)).Should().Equal(mediator.Did);
        var b = await mediator.NewRecipientAsync();
        await mediator.AddRecipientsAsync(bobAgent, new[] { b.Did });
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(b.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, b.Did, await alice.AuthcryptAsync(note, b.Did));
        for (var i = 0; i < selfLayers; i++)
            (packed, _) = await alice.ForwardAsync(mediator.Did, mediator.Did, packed);

        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        var delivered = await mediator.FetchAsync(bobAgent);

        if (selfLayers <= ForwardHandler.MaxSelfAddressedLayers)
        {
            mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
            var opened = await b.Client.UnpackDeliveredAsync(Text(delivered.Should().ContainSingle().Subject), CancellationToken.None);
            opened.LayersOpened.Should().Be(1, "the mediator re-wraps the relayed payload once, not once per layer it opened");
            opened.Result.Message.Id.Should().Be(note.Id);
        }
        else
        {
            mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedMalformed);
            delivered.Should().BeEmpty();
        }
    }

    private static string Text(DidComm.Mediator.Pickup.DeliveredMessage delivered) => Encoding.UTF8.GetString(delivered.Payload.Span);

    /// <summary>Change one base64url character of the named top-level JWE member.</summary>
    private static string Tamper(string jwe, string member)
    {
        var node = JsonNode.Parse(jwe)!.AsObject();
        var value = node[member]!.GetValue<string>();
        var at = value.Length / 2;
        var flipped = value[at] == 'A' ? 'B' : 'A';
        node[member] = string.Concat(value.AsSpan(0, at), flipped.ToString(), value.AsSpan(at + 1));
        return node.ToJsonString();
    }
}
