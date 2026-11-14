using AwesomeAssertions;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// ADV-3 (#95): who can make the mediator count <c>Faulted</c>. AD-3 settled that a stranger must
/// not be able to raise the fault count at will — <c>Faulted</c> is for the mediator's own
/// collaborators — and fixed it for layers addressed to the mediator itself. Through the real
/// <c>MapDidCommMediator</c> endpoint.
/// </summary>
public sealed class MediatorFaultedAdv3Tests
{
    private const string Undeliverable = "did:example:adv3-registered-but-unresolvable";

    // ADV-3 DEFECT 4 (P3; the outcome counter is new in #95; fixed): in RewrapMode.ReanoncryptToNext
    // the pump resolves `next` to rewrap for it. Under the default IMediationPolicy any authenticated
    // peer is granted mediation and may register any bare DID, so a stranger registered one with no
    // usable DID document and sent forwards to it: each ended as `Faulted`, the outcome AD-3 reserved
    // for the mediator's own collaborators. A next hop that cannot be resolved now ends
    // `Dropped-Unknown`: the recipient cannot be reached.
    [Fact]
    public async Task ADV3_D4_A_strangers_own_unresolvable_recipient_is_never_counted_as_a_mediator_fault()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext);
        await SendToSelfRegisteredUnresolvableAsync(mediator, count: 3);

        mediator.Outcomes.Should().NotContain(ForwardOutcomes.Faulted);
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedUnknown, ForwardOutcomes.DroppedUnknown, ForwardOutcomes.DroppedUnknown);
        mediator.Logs.Records.Should().Contain(r => r.Level == LogLevel.Warning && r.Text.Contains("next hop cannot be reached: DidResolutionException", StringComparison.Ordinal), "the line names the exception type only");
        mediator.LeakingLogLines(Undeliverable).Should().BeEmpty();
    }

    private static async Task SendToSelfRegisteredUnresolvableAsync(MediatorTestServer mediator, int count)
    {
        // Mallory: any DID that can authcrypt; the default policy grants her and lets her register.
        var mallory = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(mallory, Undeliverable);
        var sender = await mediator.NewSenderAsync();
        for (var i = 0; i < count; i++)
        {
            var (packed, _) = await sender.ForwardAsync(mediator.Did, Undeliverable, await sender.AuthcryptAsync(sender.Note(mallory.Did), mallory.Did));
            await Qa.PostForwardAsync(mediator, packed);
        }
        await mediator.FlushAsync();
    }
}
