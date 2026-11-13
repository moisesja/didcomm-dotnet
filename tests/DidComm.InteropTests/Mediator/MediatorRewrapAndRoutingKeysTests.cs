using System.Text;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Jose;
using DidComm.Mediator;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #95 acceptance, "rewrap mode and per-grant extra routing keys produce onions the recipient
/// unwraps": with <c>RewrapMode.ReanoncryptToNext</c> the mediator anoncrypts a fresh forward to the
/// recipient (FR-ROUTE-06), and with a host <see cref="IRecipientRoutingKeyProvider"/> it adds one
/// forward layer per extra key (FR-ROUTE-05, JC-7); in both cases Bob peels the forward addressed to
/// himself (the technique of <c>AliceMediatorBobRoundTripTests</c>) and finds Alice's message. The
/// two together are refused at registration.
/// </summary>
public sealed class MediatorRewrapAndRoutingKeysTests
{
    [Fact]
    public async Task With_ReanoncryptToNext_Bob_peels_the_fresh_forward_and_finds_Alices_message()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext);
        var bob = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob)).Should().Equal(mediator.Did);
        var b = await mediator.NewRecipientAsync();
        await mediator.AddRecipientsAsync(bob, new[] { b.Did });
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(b.Did);

        (await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;

        await ShouldPeelToAlicesNoteAsync(b, delivered.Payload, alice, note.Id);
        mediator.LeakingLogLines(b.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_per_recipient_extra_routing_key_adds_a_layer_that_Bob_peels()
    {
        // The provider answers for B with B's own X25519 key, whose secret Bob holds.
        var provider = new FixedRoutingKeys();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
            o.ConfigureServices = services => services.AddSingleton<IRecipientRoutingKeyProvider>(provider));
        var bob = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob)).Should().Equal(mediator.Did);
        var bIdentity = await MediatorCast.MintAsync(new DidCommServiceSpec(mediator.Did, Accept: new[] { "didcomm/v2" }));
        var b = mediator.Own(TestPeer.Create(bIdentity));
        var x25519 = bIdentity.Privates.Single(k => k.Crv == "X25519");
        provider.For(b.Did, new Jwk { Kty = x25519.Kty, Crv = x25519.Crv, X = x25519.X, Kid = x25519.Kid });
        await mediator.AddRecipientsAsync(bob, new[] { b.Did });
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(b.Did);

        (await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        provider.Asked.Should().Contain(b.Did, "the processor asks the host's provider for the recipient's extra keys (pass-through)");
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;

        await ShouldPeelToAlicesNoteAsync(b, delivered.Payload, alice, note.Id);
        mediator.LeakingLogLines(b.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task ReanoncryptToNext_together_with_a_routing_key_provider_is_refused_at_registration()
    {
        var act = () => MediatorTestServer.CreateAsync(o =>
        {
            o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext;
            o.ConfigureServices = services => services.AddSingleton<IRecipientRoutingKeyProvider>(new FixedRoutingKeys());
        });

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be(nameof(MediatorOptions.RewrapMode));
    }

    /// <summary>
    /// The payload is a forward anoncrypted to B (not Alice's message itself); Bob peels it — its
    /// <c>next</c> is B — and the inner payload is Alice's authcrypted message.
    /// </summary>
    private static async Task ShouldPeelToAlicesNoteAsync(TestPeer b, ReadOnlyMemory<byte> payload, MediatorSender alice, string noteId)
    {
        var packed = Encoding.UTF8.GetString(payload.Span);
        var outer = await b.Client.UnpackAsync(packed);
        outer.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri, "the recipient gets one more forward layer to peel");
        outer.Encrypted.Should().BeTrue();
        outer.SenderKid.Should().BeNull("the added layer is anoncrypted");

        var peeler = new ForwardProcessor(b.Client, b.Services.GetRequiredService<IDidKeyService>(), new ForwardProcessorOptions());
        var peeled = await peeler.ProcessAsync(packed);
        peeled.NextHop.Should().Be(b.Did);
        (await MediatorRecipient.UnpackFromSenderAsync(b, peeled.OnwardPacked, alice)).Message.Id.Should().Be(noteId);
    }

    /// <summary>A host provider of per-recipient extra routing keys; records who it was asked about.</summary>
    private sealed class FixedRoutingKeys : IRecipientRoutingKeyProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<Jwk>> _keys = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _asked = new();

        public IReadOnlyCollection<string> Asked => _asked;

        public void For(string recipientDid, params Jwk[] keys) => _keys[recipientDid] = keys;

        public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct)
        {
            _asked.Enqueue(recipientDid);
            return ValueTask.FromResult(_keys.GetValueOrDefault(recipientDid) ?? Array.Empty<Jwk>());
        }
    }
}
