using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Tests.Coordination;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Method.Peer;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Mediator.Tests.Coordination.HandlerMessages;

namespace DidComm.Mediator.Tests.Adv98.D1;

/// <summary>
/// #98 adversarial pass, dimension D1 (authorization and identifiers): a did:peer:4 has two legal
/// spellings, the long form <c>did:peer:4{hash}:{encoded-document}</c> and the short form
/// <c>did:peer:4{hash}</c> (did:peer spec, numalgo 4). The long form is a DID only when the hash is
/// the hash of the encoded document. These tests pin the two facts a recipient registration of a
/// did:peer:4 relies on: a long form whose hash does not match its document never takes Bob's
/// DID from him, and the default resolver answers the long form (naming the short form as an
/// alias) and nothing for the short form.
/// </summary>
public sealed class D1PeerDid4SpellingsTests(ITestOutputHelper output)
{
    private const RecipientUpdateOutcome Success = RecipientUpdateOutcome.Success;

    /// <summary>
    /// Mallory goes first with two long forms that carry Bob's hash: (1) post-sign tampering, Bob's
    /// real long form with one character of the encoded document changed; (2) a self-consistent
    /// forgery, Bob's hash followed by Mallory's own encoded document. Neither is Bob's DID, so
    /// neither may take Bob's long or short form from him. Any change that treats the two spellings
    /// as one registration must check the hash before it maps a long form onto its short form.
    /// </summary>
    [Fact]
    public async Task A_long_form_whose_hash_does_not_match_its_document_never_takes_bobs_did()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        var (bobsLong, bobsShort) = await MintPeer4Async(fx);
        var (mallorysLong, mallorysShort) = await MintPeer4Async(fx);
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.EnrolAsync(mallory.Did, mediator.Did);

        var encodedStart = bobsLong.IndexOf(':', "did:peer:4".Length) + 1;
        var flip = encodedStart + 12;
        var tampered = bobsLong[..flip] + (bobsLong[flip] == 'A' ? 'B' : 'A') + bobsLong[(flip + 1)..];
        var forged = bobsShort + mallorysLong[mallorysShort.Length..]; // did:peer:4{bob's hash}:{mallory's document}
        tampered.Should().NotBe(bobsLong);
        forged.Should().StartWith(bobsShort + ":").And.NotBe(bobsLong);

        var resolver = fx.Host.Services.GetRequiredService<IDidResolver>();
        var tamperedResolves = (await resolver.ResolveAsync(tampered)).DidDocument is not null;
        var forgedResolves = (await resolver.ResolveAsync(forged)).DidDocument is not null;
        output.WriteLine($"tampered long form resolves: {tamperedResolves}; forged long form resolves: {forgedResolves}");

        var mallorys = await fx.UpdateAsync(mallory.Did, mediator.Did, Add(tampered), Add(forged));
        var bobs = await fx.UpdateAsync(bob.Did, mediator.Did, Add(bobsLong));
        var longOwner = await fx.Memory.FindGrantByRecipientDidAsync(bobsLong, default);
        var shortOwner = await fx.Memory.FindGrantByRecipientDidAsync(bobsShort, default);
        output.WriteLine($"Mallory: tampered -> {mallorys[0].Result}, forged -> {mallorys[1].Result}; Bob's long -> {bobs[0].Result}; long owner: {Who(longOwner, bob.Did, mallory.Did)}; short owner: {Who(shortOwner, bob.Did, mallory.Did)}");

        tamperedResolves.Should().BeFalse("NetDid checks the hash of a long form");
        forgedResolves.Should().BeFalse("NetDid checks the hash of a long form");
        bobs[0].Result.Should().Be(Success, "nothing Mallory registered is Bob's DID");
        longOwner!.PeerDid.Should().Be(bob.Did);
        shortOwner?.PeerDid.Should().NotBe(mallory.Did);
    }

    /// <summary>
    /// NetDid resolves the long form (with the short form in <c>alsoKnownAs</c>) and resolves
    /// nothing for the short form, so a sender using this library's default resolver can only ever
    /// address a did:peer:4 recipient by its long form.
    /// </summary>
    [Fact]
    public async Task The_default_resolver_answers_the_long_form_only()
    {
        await using var fx = HandlerFixture.Create();
        var (longForm, shortForm) = await MintPeer4Async(fx);
        var resolver = fx.Host.Services.GetRequiredService<IDidResolver>();

        var viaLong = await resolver.ResolveAsync(longForm);
        var viaShort = await resolver.ResolveAsync(shortForm);
        output.WriteLine($"long: document {(viaLong.DidDocument is null ? "none" : "found")}, alsoKnownAs = [{string.Join(", ", viaLong.DidDocument?.AlsoKnownAs ?? [])}]");
        output.WriteLine($"short: document {(viaShort.DidDocument is null ? "none" : "found")}, error = {viaShort.ResolutionMetadata.Error}");

        viaLong.DidDocument.Should().NotBeNull();
        viaLong.DidDocument!.AlsoKnownAs.Should().Contain(shortForm);
        viaShort.DidDocument.Should().BeNull();
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static async Task<(string Long, string Short)> MintPeer4Async(HandlerFixture fx)
    {
        var keys = fx.Host.Services.GetRequiredService<IKeyGenerator>();
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
        var created = await fx.Host.Services.GetRequiredService<IDidManager>()
            .CreateAsync(new DidPeerCreateOptions { Numalgo = PeerNumalgo.Four, InputDocument = input });
        var longForm = created.Did.Value!;
        var shortForm = longForm[..longForm.IndexOf(':', "did:peer:4".Length)];
        DidComm.Mediator.Coordination.BareDid.IsBareDid(longForm).Should().BeTrue("the long form is a bare DID to the mediator");
        DidComm.Mediator.Coordination.BareDid.IsBareDid(shortForm).Should().BeTrue("the short form is a bare DID to the mediator");
        return (longForm, shortForm);
    }

    private static string Who(MediationGrant? grant, string bob, string mallory)
        => grant is null ? "(none)" : grant.PeerDid == bob ? "Bob" : grant.PeerDid == mallory ? "Mallory" : "someone else";
}
