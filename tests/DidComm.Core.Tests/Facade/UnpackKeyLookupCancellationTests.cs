using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using Microsoft.IdentityModel.Tokens;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Facade;

/// <summary>
/// The sender's key (authcrypt) and the signer's key (signed) are looked up with the caller's
/// cancellation token: an unpack whose lookup is still pending ends when that token is cancelled,
/// with an <see cref="OperationCanceledException"/> for it. Checked through both shapes of key
/// service: one that supplies same-document bindings (<see cref="IDidKeyBindingService"/>) and one
/// that only lists keys (<see cref="IDidKeyService"/>).
/// </summary>
public sealed class UnpackKeyLookupCancellationTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";

    /// <summary>
    /// Real time is only a guard here: with a correct unpack the outcome is decided as soon as the
    /// token is cancelled.
    /// </summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    public enum Envelope
    {
        Authcrypt,
        Signed,
    }

    [Theory]
    [InlineData(Envelope.Authcrypt, true)]
    [InlineData(Envelope.Authcrypt, false)]
    [InlineData(Envelope.Signed, true)]
    [InlineData(Envelope.Signed, false)]
    public async Task A_cancelled_token_ends_an_unpack_while_the_key_lookup_is_pending(Envelope envelope, bool bindingService)
    {
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, Alice + "#ka-1");
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, Alice + "#auth-1");
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, Bob + "#ka-1");
        var aliceDoc = Doc(Alice, (aliceKa.PublicJwk, VerificationRelationship.KeyAgreement), (aliceAuth.PublicJwk, VerificationRelationship.Authentication));
        var bobDoc = Doc(Bob, (bobKa.PublicJwk, VerificationRelationship.KeyAgreement));
        var packed = await PackAsync(envelope, aliceKa, aliceAuth, aliceDoc, bobDoc);

        // Alice's document is the one the unpack looks up; it is not answered until released.
        var resolver = new Resolver(pendingDid: Alice, aliceDoc, bobDoc);
        var netDid = new NetDidKeyService(resolver);
        IDidKeyService keyService = bindingService ? netDid : new ListingOnlyKeyService(netDid);
        var bob = new DidCommClient(new DictionarySecretsLookup(new[] { bobKa.PrivateJwk }), keyService, new DidCommOptions());

        using var cts = new CancellationTokenSource();
        var unpack = Task.Run(() => bob.UnpackAsync(packed, cts.Token));
        try
        {
            await resolver.LookupStarted.WaitAsync(Guard);
            unpack.IsCompleted.Should().BeFalse("the lookup has not been answered");

            cts.Cancel();
            var thrown = await Record.ExceptionAsync(() => unpack.WaitAsync(Guard));

            thrown.Should().BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should().Be(cts.Token);
        }
        finally
        {
            resolver.Release();
        }
    }

    private static async Task<string> PackAsync(Envelope envelope, TestKeyMaterial aliceKa, TestKeyMaterial aliceAuth, DidDocument aliceDoc, DidDocument bobDoc)
    {
        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithFrom(Alice)
            .WithTo(Bob)
            .WithBody(JsonNode.Parse("""{"v":1}""")!.AsObject())
            .Build();
        var alice = new DidCommClient(
            new DictionarySecretsLookup(new[] { aliceKa.PrivateJwk, aliceAuth.PrivateJwk }),
            new NetDidKeyService(new Resolver(pendingDid: null, aliceDoc, bobDoc)),
            new DidCommOptions());
        return envelope == Envelope.Authcrypt
            ? (await alice.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message
            : await alice.PackSignedAsync(message, Alice);
    }

    private static DidDocument Doc(string did, params (Jwk PublicJwk, VerificationRelationship Relationship)[] keys)
    {
        var methods = new List<VerificationMethod>();
        var authentication = new List<VerificationRelationshipEntry>();
        var keyAgreement = new List<VerificationRelationshipEntry>();
        foreach (var (publicJwk, relationship) in keys)
        {
            var method = new VerificationMethod
            {
                Id = publicJwk.Kid!,
                Type = "JsonWebKey2020",
                Controller = new Did(did),
                PublicKeyJwk = new JsonWebKey { Kty = publicJwk.Kty, Crv = publicJwk.Crv, X = publicJwk.X, Y = publicJwk.Y },
            };
            methods.Add(method);
            (relationship == VerificationRelationship.Authentication ? authentication : keyAgreement)
                .Add(VerificationRelationshipEntry.FromEmbedded(method));
        }

        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = methods.ToArray(),
            Authentication = authentication.ToArray(),
            KeyAgreement = keyAgreement.ToArray(),
        };
    }

    /// <summary>
    /// Serves fixed documents. A resolution of <c>pendingDid</c> is not answered until
    /// <see cref="Release"/>, and ends early only when its own token is cancelled.
    /// </summary>
    private sealed class Resolver(string? pendingDid, params DidDocument[] documents) : IDidResolver
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when a resolution of the pending DID has started.</summary>
        public Task LookupStarted => _started.Task;

        public void Release() => _release.TrySetResult();

        public bool CanResolve(string did) => true;

        public async Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            if (did == pendingDid)
            {
                _started.TrySetResult();
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            var document = documents.FirstOrDefault(d => d.Id.ToString() == did);
            return document is null
                ? DidResolutionResult.NotFound(did)
                : new DidResolutionResult { DidDocument = document, ResolutionMetadata = new DidResolutionMetadata() };
        }
    }

    /// <summary>A key service that lists keys only: it does not offer <see cref="IDidKeyBindingService"/>.</summary>
    private sealed class ListingOnlyKeyService(NetDidKeyService inner) : IDidKeyService
    {
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.GetVerificationMethodsAsync(did, relationship, ct);

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

        public void RejectUnsupportedMethod(string did) => inner.RejectUnsupportedMethod(did);
    }
}
