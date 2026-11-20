using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using DidComm.Diagnostics;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Threading;
using DidComm.Transports;
using AwesomeAssertions;
using Microsoft.IdentityModel.Tokens;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;

namespace DidComm.Tests.Observability;

/// <summary>
/// The message-type tag of every span that carries one (pack plaintext, pack signed, pack encrypted,
/// send, unpack, handle). A stranger chooses the type of what it sends, and a host that copies a
/// span's tags into its log scopes writes the tag on every line logged inside the span; the type
/// grammar lets a type hold line breaks and terminal escapes and be as long as the receive limit.
/// So the tag holds the type as it is only when it is at most 256 URI characters; otherwise a
/// placeholder that does not echo it.
/// </summary>
public sealed class MessageTypeSpanTagTests
{
    private const string Alice = "did:example:typetag-alice";
    private const string Bob = "did:example:typetag-bob";

    private const string Clean = "https://didcomm.org/basicmessage/2.0/message";

    // A valid Message Type URI: the protocol name may hold anything but '/', so a line feed, a
    // forged log line and a clear-screen escape fit inside it.
    private const string WithControlCharacters =
        "https://didcomm.org/x\nFORGED: info: Forged.Category[0] rotate keys now\u001b[2J/1.0/ping";

    private static readonly string OverLong = "https://didcomm.org/" + new string('a', 300) + "/1.0/ping";

    public static TheoryData<string> Spans => new()
    {
        DidCommDiagnostics.PackPlaintextActivity,
        DidCommDiagnostics.PackSignedActivity,
        DidCommDiagnostics.PackEncryptedActivity,
        DidCommDiagnostics.SendActivity,
        DidCommDiagnostics.UnpackActivity,
        DidCommDiagnostics.HandleActivity,
    };

    [Theory]
    [MemberData(nameof(Spans))]
    public async Task A_type_with_characters_outside_a_URI_is_tagged_as_a_placeholder(string span)
    {
        var tag = await TagOfAsync(span, WithControlCharacters);

        tag.Should().Be("(type with characters outside a URI)");
    }

    [Theory]
    [MemberData(nameof(Spans))]
    public async Task A_type_over_256_characters_is_tagged_by_its_length(string span)
    {
        var tag = await TagOfAsync(span, OverLong);

        tag.Should().Be($"(type of {OverLong.Length} characters)");
    }

    [Theory]
    [MemberData(nameof(Spans))]
    public async Task A_type_of_URI_characters_within_the_bound_is_tagged_as_it_is(string span)
    {
        var tag = await TagOfAsync(span, Clean);

        tag.Should().Be(Clean);
    }

    /// <summary>
    /// Run the operation behind <paramref name="span"/> on a message of type <paramref name="type"/>
    /// under a root activity of this test, and return the message-type tag of the one span of that
    /// name in the root's trace (the listener is process-wide; the trace id keeps other tests out).
    /// </summary>
    private static async Task<object?> TagOfAsync(string span, string type)
    {
        var world = World.Create();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DidCommDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        ActivityTraceId trace;
        using (var root = new Activity("message-type-span-tag-test").Start())
        {
            trace = root.TraceId;
            await world.RunAsync(span, type);
        }

        var spans = stopped.Where(a => a.TraceId == trace && a.OperationName == span).ToList();
        spans.Should().ContainSingle("the operation emits one '{0}' span", span);
        return spans[0].GetTagItem(DidCommDiagnostics.MessageTypeTag);
    }

    /// <summary>Alice (a sender with signing and key-agreement keys) and Bob (a recipient), resolvable to each other.</summary>
    private sealed class World
    {
        private readonly DidCommClient _alice;
        private readonly DidCommClient _bob;
        private readonly DidCommClient _sender;

        private World(DidCommClient alice, DidCommClient bob, DidCommClient sender)
        {
            _alice = alice;
            _bob = bob;
            _sender = sender;
        }

        public static World Create()
        {
            var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, Alice + "#ka-1");
            var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, Alice + "#auth-1");
            var bobKa = TestKeyMaterial.Generate(KeyType.X25519, Bob + "#ka-1");
            var resolver = new StaticResolver(
                (Alice, Doc(Alice, aliceKa.PublicJwk, aliceAuth.PublicJwk)),
                (Bob, Doc(Bob, bobKa.PublicJwk, authenticationKey: null)));
            var aliceSecrets = new DictionarySecretsLookup(new[] { aliceKa.PrivateJwk, aliceAuth.PrivateJwk });

            return new World(
                new DidCommClient(aliceSecrets, new NetDidKeyService(resolver), new DidCommOptions()),
                new DidCommClient(new DictionarySecretsLookup(new[] { bobKa.PrivateJwk }), new NetDidKeyService(resolver), new DidCommOptions()),
                new DidCommClient(aliceSecrets, new NetDidKeyService(resolver), new NoServicesResolver(), new AcceptingRouter(), new DidCommOptions()));
        }

        public async Task RunAsync(string span, string type)
        {
            switch (span)
            {
                case DidCommDiagnostics.PackPlaintextActivity:
                    await _alice.PackPlaintextAsync(Message(type));
                    break;
                case DidCommDiagnostics.PackSignedActivity:
                    await _alice.PackSignedAsync(Message(type), Alice);
                    break;
                case DidCommDiagnostics.PackEncryptedActivity:
                    await _alice.PackEncryptedAsync(Message(type), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, Forward: false));
                    break;
                case DidCommDiagnostics.SendActivity:
                    await _sender.SendAsync(Message(type), new SendOptions(Recipients: new[] { Bob }, From: Alice)
                    {
                        ServiceEndpointOverride = new Uri("https://typetag.example/inbox"),
                    });
                    break;
                case DidCommDiagnostics.UnpackActivity:
                    await _bob.UnpackAsync(await PackedForBobAsync(type));
                    break;
                case DidCommDiagnostics.HandleActivity:
                    var received = await _bob.UnpackAsync(await PackedForBobAsync(type));
                    using (var dispatcher = new ProtocolDispatcher(new ProtocolHandlerRegistry(), new InMemoryThreadStateStore()))
                    {
                        var outcome = await dispatcher.DispatchAsync(received, _bob, new DidCommOptions());
                        outcome.Result.Should().Be(DispatchResult.NoHandler);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(span), span, null);
            }
        }

        // What a stranger sends Bob: an authcrypt from Alice.
        private async Task<string> PackedForBobAsync(string type)
            => (await _alice.PackEncryptedAsync(Message(type), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, Forward: false))).Message;

        private static Message Message(string type)
            => new MessageBuilder().WithType(type).WithFrom(Alice).WithTo(Bob).WithBody(new JsonObject()).Build();
    }

    private static DidDocument Doc(string did, Jwk keyAgreementKey, Jwk? authenticationKey)
    {
        var ka = Method(did, keyAgreementKey);
        var auth = authenticationKey is null ? null : Method(did, authenticationKey);
        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = auth is null ? new[] { ka } : new[] { ka, auth },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(ka) },
            Authentication = auth is null
                ? Array.Empty<VerificationRelationshipEntry>()
                : new[] { VerificationRelationshipEntry.FromEmbedded(auth) },
        };
    }

    private static VerificationMethod Method(string did, Jwk key) => new()
    {
        Id = key.Kid!,
        Type = "JsonWebKey2020",
        Controller = new Did(did),
        PublicKeyJwk = new JsonWebKey { Kty = key.Kty, Crv = key.Crv, X = key.X },
    };

    private sealed class NoServicesResolver : IServiceEndpointResolver
    {
        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(Array.Empty<DidCommServiceInfo>());
    }

    private sealed class AcceptingRouter : ITransportRouter
    {
        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
            => Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
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
