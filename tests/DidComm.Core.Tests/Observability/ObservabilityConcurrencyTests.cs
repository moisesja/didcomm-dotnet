using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using DidComm.Diagnostics;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;

namespace DidComm.Tests.Observability;

/// <summary>
/// NFR-03 × NFR-05: the singleton <see cref="DidCommClient"/> instrumented with an active
/// <see cref="ActivityListener"/> must stay correct under concurrent pack/unpack — every
/// operation gets its OWN span with its OWN tags (no cross-operation tag bleed), every span is
/// a root (no span leaks across an await into a sibling operation's ambient context), and no
/// operation fails because of the listener.
/// </summary>
public sealed class ObservabilityConcurrencyTests
{
    private const string Alice = "did:example:nfr03-alice";
    private const string Bob = "did:example:nfr03-bob";
    private const string AliceKaKid = "did:example:nfr03-alice#ka-1";
    private const string BobKaKid = "did:example:nfr03-bob#ka-1";

    /// <summary>Distinctive per-operation type URIs so the process-global listener can be filtered to THIS test.</summary>
    private const string TypePrefix = "https://example.com/protocols/nfr03-concurrency/1.0/op-";

    [Fact]
    public async Task ConcurrentPackUnpack_UnderListener_EachOperationGetsItsOwnRootSpanWithItsOwnTags()
    {
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var resolver = new StaticResolver(
            (Alice, Doc(Alice, aliceKa.PublicJwk)),
            (Bob, Doc(Bob, bobKa.PublicJwk)));

        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DidCommDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        // One shared (singleton-registered, NFR-03) client per side.
        var alice = new DidCommClient(
            new DictionarySecretsLookup(new[] { aliceKa.PrivateJwk }), new NetDidKeyService(resolver), new DidCommOptions());
        var bob = new DidCommClient(
            new DictionarySecretsLookup(new[] { bobKa.PrivateJwk }), new NetDidKeyService(resolver), new DidCommOptions());

        const int operations = 12;
        await Task.WhenAll(Enumerable.Range(0, operations).Select(i => Task.Run(async () =>
        {
            var type = TypePrefix + i;
            var message = new MessageBuilder()
                .WithType(type)
                .WithFrom(Alice)
                .WithTo(Bob)
                .WithBody(JsonNode.Parse($"{{\"i\":{i}}}")!.AsObject())
                .Build();
            var packed = await alice.PackEncryptedAsync(
                message, new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice));
            var unpacked = await bob.UnpackAsync(packed.Message);
            unpacked.Message.Type.Should().Be(type);
        })));

        var ours = spans
            .Where(a => a.GetTagItem(DidCommDiagnostics.MessageTypeTag) is string t
                        && t.StartsWith(TypePrefix, StringComparison.Ordinal))
            .ToArray();

        for (var i = 0; i < operations; i++)
        {
            var type = TypePrefix + i;
            // Exactly one pack span and one unpack span per operation, each tagged with ITS OWN
            // message type — concurrent operations must not bleed tags into each other's spans.
            ours.Count(a => a.OperationName == DidCommDiagnostics.PackEncryptedActivity
                            && Equals(a.GetTagItem(DidCommDiagnostics.MessageTypeTag), type))
                .Should().Be(1, $"operation {i} must own exactly one pack span");
            ours.Count(a => a.OperationName == DidCommDiagnostics.UnpackActivity
                            && Equals(a.GetTagItem(DidCommDiagnostics.MessageTypeTag), type))
                .Should().Be(1, $"operation {i} must own exactly one unpack span");
        }

        foreach (var span in ours)
        {
            // No span leaked across an await into a sibling operation: with no ambient activity in
            // the test, every pack/unpack span must be a root. A leaked (undisposed) span would
            // reparent later spans under a foreign operation.
            span.Parent.Should().BeNull($"span '{span.OperationName}' must not be parented under a concurrent sibling's span");
            span.Status.Should().NotBe(ActivityStatusCode.Error);
            span.Duration.Should().BeGreaterThan(TimeSpan.Zero, "the span must have been properly stopped");
        }
    }

    private static DidDocument Doc(string did, Jwk keyAgreementKey)
    {
        var ka = new VerificationMethod
        {
            Id = keyAgreementKey.Kid!,
            Type = "JsonWebKey2020",
            Controller = new Did(did),
            PublicKeyJwk = new JsonWebKey
            {
                Kty = keyAgreementKey.Kty,
                Crv = keyAgreementKey.Crv,
                X = keyAgreementKey.X,
            },
        };
        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = new[] { ka },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(ka) },
        };
    }

    private sealed class StaticResolver : IDidResolver
    {
        private readonly Dictionary<string, DidDocument> _docs;

        public StaticResolver(params (string Did, DidDocument Doc)[] entries)
            => _docs = entries.ToDictionary(e => e.Did, e => e.Doc, StringComparer.Ordinal);

        public bool CanResolve(string did) => _docs.ContainsKey(did);

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(_docs.TryGetValue(did, out var doc)
                ? new DidResolutionResult { DidDocument = doc, ResolutionMetadata = new DidResolutionMetadata() }
                : DidResolutionResult.NotFound(did));
    }
}
