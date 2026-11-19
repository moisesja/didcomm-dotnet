using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.Mediator;
using DidComm.Mediator.Coordination;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Method.Peer;
using Xunit;

namespace DidComm.InteropTests.Adv98.D1;

/// <summary>
/// #98 adversarial pass, dimension D1, end to end over the mediator's HTTP endpoint (TestServer):
/// Bob registers his did:peer:4 by its long form, and Mallory then asks to register the short form
/// of the same DID (did:peer spec, numalgo 4). Whatever the mediator answers Mallory, a forward
/// addressed to Bob's long form is queued for Bob and only for Bob.
/// </summary>
public sealed class D1PeerDid4ForwardDiversionTests
{
    [Fact]
    public async Task Control_a_forward_to_the_long_form_reaches_bob()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var mallory = await mediator.NewPeerAsync();
        var (bobsLong, bobsShort) = await MintPeer4Async(mediator);
        await mediator.EnrolAsync(bob, bobsLong);
        // Mallory's attempt on the short form: its answer is not this test's subject.
        (await mediator.RequestMediationAsync(mallory)).Should().Equal(mediator.Did);
        await mediator.AskAsync(mallory, CoordinateMediation.CreateRecipientUpdate(mallory.Did, mediator.Did, new[] { new RecipientUpdate(bobsShort, RecipientUpdateAction.Add) }));
        var alice = await mediator.NewSenderAsync();

        var forBob = await alice.AuthcryptAsync(alice.Note(bob.Did, "for Bob, by his long form"), bob.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bobsLong, forBob);
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Outcomes.Count >= 1, "the forward's outcome is recorded");

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        (await mediator.FetchAsync(bob)).Should().ContainSingle();
        (await mediator.FetchAsync(mallory)).Should().BeEmpty();
    }

    private static async Task<(string Long, string Short)> MintPeer4Async(MediatorTestServer mediator)
    {
        var services = mediator.App.Services;
        var keys = services.GetRequiredService<IKeyGenerator>();
        var kx = keys.Generate(KeyType.X25519);
        var auth = keys.Generate(KeyType.Ed25519);
        var input = new DidDocument
        {
            VerificationMethod =
            [
                new VerificationMethod { Id = "#key-1", Type = "Multikey", PublicKeyMultibase = kx.MultibasePublicKey },
                new VerificationMethod { Id = "#key-2", Type = "Multikey", PublicKeyMultibase = auth.MultibasePublicKey },
            ],
            KeyAgreement = [VerificationRelationshipEntry.FromReference("#key-1")],
            Authentication = [VerificationRelationshipEntry.FromReference("#key-2")],
        };
        var created = await services.GetRequiredService<IDidManager>()
            .CreateAsync(new DidPeerCreateOptions { Numalgo = PeerNumalgo.Four, InputDocument = input });
        var longForm = created.Did.Value!;
        return (longForm, longForm[..longForm.IndexOf(':', "did:peer:4".Length)]);
    }
}
