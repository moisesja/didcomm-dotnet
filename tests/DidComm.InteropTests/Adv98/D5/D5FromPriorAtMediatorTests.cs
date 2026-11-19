using System.Net;
using System.Text;
using AwesomeAssertions;
using DataProofsDotnet.Jose;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Mediator.Coordination;
using DidComm.Protocols.Rotation;
using DidComm.Samples.Shared;
using Xunit;

namespace DidComm.InteropTests.Adv98.D5;

/// <summary>
/// #98 red team, D5 attack 5 (AGENTS.md §4 at the mediator): a <c>from_prior</c> rotation claim
/// carried by a Coordinate Mediation request. Bob holds a grant and a registered recipient B.
/// - post-sign tamper: Bob's genuine rotation JWT (Bob → Bob2) with <c>sub</c> rewritten to Mallory,
///   sent by Mallory (so <c>sub == from</c> holds and only the signature is wrong);
/// - self-consistent forgery 1: <c>iss</c> = Bob, signed by Mallory's key, <c>kid</c> = Mallory's;
/// - self-consistent forgery 2: <c>iss</c> = Bob, <c>kid</c> = Bob's own key id, signed by Mallory's key.
/// And the genuine rotation: does any of Bob's mediator state follow Bob2?
/// </summary>
public sealed class D5FromPriorAtMediatorTests
{
    public enum Forgery { PostSignSubTamper, ForeignKidUnderIss, BobsKidMallorysKey }

    [Theory]
    [InlineData(Forgery.PostSignSubTamper)]
    [InlineData(Forgery.ForeignKidUnderIss)]
    [InlineData(Forgery.BobsKidMallorysKey)]
    public async Task A_forged_rotation_from_Bob_is_refused_and_Mallory_gains_no_grant_and_Bob_keeps_his(Forgery forgery)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bobIdentity = await MediatorCast.MintAsync();
        var bob = mediator.Own(TestPeer.Create(bobIdentity));
        var b = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, b.Did);
        var malloryIdentity = await MediatorCast.MintAsync();
        var mallory = mediator.Own(TestPeer.Create(malloryIdentity));
        var bob2 = await mediator.NewPeerAsync();
        var iat = mediator.Clock.Now.ToUnixTimeSeconds();

        var jwt = forgery switch
        {
            Forgery.PostSignSubTamper => RewriteSub(
                await FromPriorBuilder.BuildAsync(new FromPriorClaims(bob2.Did, bob.Did, iat), Ed25519(bobIdentity)), mallory.Did),
            Forgery.ForeignKidUnderIss => await FromPriorBuilder.BuildAsync(
                new FromPriorClaims(mallory.Did, bob.Did, iat), Ed25519(malloryIdentity)),
            Forgery.BobsKidMallorysKey => await FromPriorBuilder.BuildAsync(
                new FromPriorClaims(mallory.Did, bob.Did, iat), WithKid(Ed25519(malloryIdentity), Ed25519(bobIdentity).Kid!)),
            _ => throw new ArgumentOutOfRangeException(nameof(forgery)),
        };
        var request = mediator.Prepare(CoordinateMediation.CreateMediateRequest(mallory.Did, mediator.Did));
        request.FromPrior = jwt;

        var response = await mediator.PostAsync(await mediator.AuthcryptAsync(mallory, request));

        response.Status.Should().Be(HttpStatusCode.BadRequest, "an unverifiable from_prior fails the unpack (FR-ROT-01/02)");
        (await mediator.Mediation.GetGrantAsync(mallory.Did, CancellationToken.None)).Should().BeNull();
        (await mediator.Mediation.GetGrantAsync(bob.Did, CancellationToken.None)).Should().NotBeNull();
        (await mediator.Mediation.ListRecipientsAsync(bob.Did, 0, 10, CancellationToken.None)).Dids.Should().Equal(b.Did);
        (await mediator.Mediation.FindGrantByRecipientDidAsync(b.Did, CancellationToken.None))!.PeerDid.Should().Be(bob.Did);
    }

    [Fact]
    public async Task A_genuine_rotation_from_Bob_to_Bob2_is_accepted_and_carries_none_of_Bobs_grant_registrations_or_queue()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bobIdentity = await MediatorCast.MintAsync();
        var bob = mediator.Own(TestPeer.Create(bobIdentity));
        var b = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, b.Did);
        var alice = await mediator.NewSenderAsync();
        var (forward, _) = await alice.ForwardAsync(mediator.Did, b.Did, await alice.AuthcryptAsync(alice.Note(b.Did), b.Did));
        (await mediator.PostAsync(forward)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        var bob2 = await mediator.NewPeerAsync();
        var jwt = await FromPriorBuilder.BuildAsync(
            new FromPriorClaims(bob2.Did, bob.Did, mediator.Clock.Now.ToUnixTimeSeconds()), Ed25519(bobIdentity));

        // Bob2's first request carries the rotation; nothing about it names B.
        var request = mediator.Prepare(CoordinateMediation.CreateMediateRequest(bob2.Did, mediator.Did));
        request.FromPrior = jwt;
        var response = await mediator.PostAsync(await mediator.AuthcryptAsync(bob2, request));
        var bob2Fetch = await mediator.FetchAsync(bob2);
        var bobFetch = await mediator.FetchAsync(bob);

        response.Status.Should().Be(HttpStatusCode.OK, "a verified rotation is an ordinary authcrypted request from Bob2");
        (await mediator.Mediation.GetGrantAsync(bob2.Did, CancellationToken.None)).Should().NotBeNull("Bob2 enrols as a new peer (open enrolment)");
        (await mediator.Mediation.ListRecipientsAsync(bob2.Did, 0, 10, CancellationToken.None)).Dids.Should().BeEmpty("registrations do not follow a rotation");
        bob2Fetch.Should().BeEmpty("Bob's queue does not follow a rotation");
        (await mediator.Mediation.GetGrantAsync(bob.Did, CancellationToken.None)).Should().NotBeNull("the prior DID keeps its grant");
        (await mediator.Mediation.FindGrantByRecipientDidAsync(b.Did, CancellationToken.None))!.PeerDid.Should().Be(bob.Did);
        bobFetch.Should().ContainSingle("B's message stays Bob's");
    }

    private static Jwk Ed25519(PeerIdentity identity) => identity.Privates.Single(k => k.Crv == "Ed25519");

    private static Jwk WithKid(Jwk key, string kid) => new() { Kty = key.Kty, Crv = key.Crv, X = key.X, D = key.D, Kid = kid };

    /// <summary>Rewrite the payload's <c>sub</c>, keeping the header and the (now wrong) signature.</summary>
    private static string RewriteSub(string jwt, string sub)
    {
        var parts = jwt.Split('.');
        var payload = System.Text.Json.Nodes.JsonNode.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(parts[1]))!.AsObject();
        payload["sub"] = sub;
        var rewritten = System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return string.Join('.', parts[0], rewritten, parts[2]);
    }
}
