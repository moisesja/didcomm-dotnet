using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Facade;

/// <summary>
/// #63: <c>InboundMessageSnapshot.RegisterVerified</c> stores into a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> with <c>Add</c>, which throws on a duplicate key,
/// and the unpack call site has no guard. That fail-fast is deliberate — <c>AddOrUpdate</c> was
/// considered and rejected, because silent overwrite would let a later, weaker registration replace
/// verified trust metadata. What keeps it safe is that there is exactly ONE registration per unpack;
/// a second call site would turn a remote message into an unpack fault. These tests pin that
/// invariant directly instead of leaving it to be re-derived by inspection.
/// <para>
/// The count is taken by filtering the weak table on a per-test marker embedded in the plaintext, so
/// it is exact under xUnit's parallel execution and — unlike "did unpack throw?" — also catches a
/// second registration made against some OTHER message instance (an inner/derived message), which
/// <c>Add</c> itself would accept silently.
/// </para>
/// </summary>
public sealed class SnapshotRegistrationTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string AliceAuthKid = "did:example:alice#auth-1";
    private const string AliceKaKid = "did:example:alice#ka-1";
    private const string BobKaKid = "did:example:bob#ka-1";
    private const string MessageType = "https://example.com/protocols/test/1.0/ping";

    private static readonly FieldInfo VerifiedSnapshotsField =
        typeof(InboundMessageSnapshot).GetField("VerifiedSnapshots", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException(
            "InboundMessageSnapshot.VerifiedSnapshots not found — if the registration table was renamed " +
            "or restructured, update this test so it keeps counting registrations per unpack (#63).");

    // ---------------------------------------------------------------------------------------
    // One registration per unpack, for every envelope shape the reader can terminate on.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Plaintext_unpack_registers_exactly_one_snapshot()
    {
        var marker = NewMarker();
        var sender = Client(Resolver());
        var packed = await sender.PackPlaintextAsync(Msg(marker, from: null));

        await AssertExactlyOneRegistration(Client(Resolver()), packed, marker);
    }

    [Fact]
    public async Task Signed_unpack_registers_exactly_one_snapshot()
    {
        var marker = NewMarker();
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var resolver = Resolver(Doc(Alice, Auth(aliceAuth.PublicJwk)));
        var packed = await Client(resolver, aliceAuth.PrivateJwk).PackSignedAsync(Msg(marker), Alice);

        await AssertExactlyOneRegistration(Client(resolver), packed, marker);
    }

    [Fact]
    public async Task Anoncrypt_unpack_registers_exactly_one_snapshot()
    {
        var marker = NewMarker();
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var resolver = Resolver(Doc(Bob, Ka(bobKa.PublicJwk)));
        var packed = (await Client(resolver).PackEncryptedAsync(
            Msg(marker, from: null),
            new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        await AssertExactlyOneRegistration(Client(resolver, bobKa.PrivateJwk), packed, marker);
    }

    [Fact]
    public async Task Authcrypt_unpack_registers_exactly_one_snapshot()
    {
        var marker = NewMarker();
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var resolver = Resolver(Doc(Alice, Ka(aliceKa.PublicJwk)), Doc(Bob, Ka(bobKa.PublicJwk)));
        var packed = (await Client(resolver, aliceKa.PrivateJwk).PackEncryptedAsync(
            Msg(marker),
            new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;

        await AssertExactlyOneRegistration(Client(resolver, bobKa.PrivateJwk), packed, marker);
    }

    [Fact]
    public async Task Signed_then_authcrypt_unpack_registers_exactly_one_snapshot()
    {
        // The deepest legal composition (anoncrypt? authcrypt? sign? plaintext) — the reader unwraps
        // three layers before reaching plaintext, and must still register exactly once.
        var marker = NewMarker();
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var resolver = Resolver(
            Doc(Alice, Auth(aliceAuth.PublicJwk), Ka(aliceKa.PublicJwk)),
            Doc(Bob, Ka(bobKa.PublicJwk)));

        var packed = (await Client(resolver, aliceAuth.PrivateJwk, aliceKa.PrivateJwk).PackEncryptedAsync(
            Msg(marker),
            new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, SignFrom: Alice))).Message;

        var result = await AssertExactlyOneRegistration(Client(resolver, bobKa.PrivateJwk), packed, marker);
        result.Authenticated.Should().BeTrue();
        result.NonRepudiation.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Envelope-shaped CONTENT must not produce a second registration. The reader terminates at
    // the outer plaintext; body/attachment/extension-header payloads that look like nested DIDComm
    // are inert data. Each probe carries the marker inside the inner payload, so a registration
    // for the inner content would be counted.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("inner-jwm-in-body")]
    [InlineData("jws-shaped-body")]
    [InlineData("jwe-shaped-body")]
    [InlineData("attachment-borne-jwm")]
    [InlineData("extension-header-jwm")]
    public async Task Envelope_shaped_payloads_register_only_the_outer_message(string probe)
    {
        var marker = NewMarker();
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var resolver = Resolver(Doc(Alice, Auth(aliceAuth.PublicJwk)));

        var outer = Msg(marker);
        var innerJwm = new JsonObject
        {
            ["id"] = $"inner-{marker}",
            ["type"] = MessageType,
            ["body"] = new JsonObject { ["marker"] = marker },
        };
        var innerJwmJson = innerJwm.ToJsonString();
        switch (probe)
        {
            case "inner-jwm-in-body":
                outer.Body = innerJwm.DeepClone().AsObject();
                break;
            case "jws-shaped-body":
                outer.Body = new JsonObject
                {
                    ["payload"] = Base64Url(innerJwmJson),
                    ["signatures"] = new JsonArray(new JsonObject { ["protected"] = Base64Url("""{"alg":"EdDSA"}"""), ["signature"] = "AA" }),
                };
                break;
            case "jwe-shaped-body":
                outer.Body = new JsonObject
                {
                    ["protected"] = Base64Url("""{"alg":"ECDH-ES+A256KW","enc":"A256CBC-HS512"}"""),
                    ["recipients"] = new JsonArray(new JsonObject { ["header"] = new JsonObject { ["kid"] = BobKaKid }, ["encrypted_key"] = "AA" }),
                    ["iv"] = "AA",
                    ["ciphertext"] = Base64Url(innerJwmJson),
                    ["tag"] = "AA",
                };
                break;
            case "attachment-borne-jwm":
                outer.Attachments = new[]
                {
                    new Attachment
                    {
                        Id = "a1",
                        MediaType = "application/didcomm-plain+json",
                        Data = new AttachmentData { Json = innerJwm.DeepClone() },
                    },
                };
                break;
            case "extension-header-jwm":
                outer.AdditionalHeaders = new Dictionary<string, JsonElement>
                {
                    ["nested"] = JsonDocument.Parse(innerJwmJson).RootElement.Clone(),
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(probe), probe, "unknown probe");
        }

        var packed = await Client(resolver, aliceAuth.PrivateJwk).PackSignedAsync(outer, Alice);

        var result = await AssertExactlyOneRegistration(Client(resolver), packed, marker);
        result.Message.Id.Should().Be(outer.Id, "the registration belongs to the outer message, not the embedded payload");
    }

    // ---------------------------------------------------------------------------------------
    // Repeated unpacks of BYTE-IDENTICAL input: N unpacks ⇒ N distinct messages ⇒ N registrations.
    // This is the shape that would break first if anything ever cached or pooled Message instances,
    // and — because the marker is shared across all N — it counts registrations globally rather than
    // per instance.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Repeated_identical_unpacks_each_register_exactly_once()
    {
        const int sequential = 50;
        var marker = NewMarker();
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var resolver = Resolver(Doc(Alice, Auth(aliceAuth.PublicJwk)));
        var packed = await Client(resolver, aliceAuth.PrivateJwk).PackSignedAsync(Msg(marker), Alice);
        var recipient = Client(resolver);

        var results = new List<UnpackResult>(sequential);
        for (var i = 0; i < sequential; i++)
            results.Add(await recipient.UnpackAsync(packed));

        AssertOneRegistrationPerResult(results, marker);
    }

    [Fact]
    public async Task Concurrent_identical_unpacks_each_register_exactly_once()
    {
        const int concurrency = 64;
        var marker = NewMarker();
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var resolver = Resolver(Doc(Alice, Auth(aliceAuth.PublicJwk)));
        var packed = await Client(resolver, aliceAuth.PrivateJwk).PackSignedAsync(Msg(marker), Alice);
        var recipient = Client(resolver);

        var results = await Task.WhenAll(Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => recipient.UnpackAsync(packed))));

        AssertOneRegistrationPerResult(results, marker);
    }

    // ---------------------------------------------------------------------------------------
    // assertions
    // ---------------------------------------------------------------------------------------

    private static async Task<UnpackResult> AssertExactlyOneRegistration(
        DidCommClient recipient, string packed, string marker)
    {
        // A second registration against this same instance would already have thrown inside
        // UnpackAsync (ConditionalWeakTable.Add rejects a duplicate key) — deliberately fail-fast.
        var result = await recipient.UnpackAsync(packed);

        InboundMessageSnapshot.TryGetFor(result.Message, out var snapshot).Should().BeTrue(
            "every unpack must leave the returned message covered by a verified snapshot");
        SnapshotsMentioning(marker).Should().ContainSingle(
            "one unpack must produce exactly one registration (#63)")
            .Which.Should().BeSameAs(snapshot);
        snapshot.Id.Should().Be(result.Message.Id);
        JsonSerializer.Serialize(snapshot.DeserializeMessage(), DidCommJson.Default)
            .Should().Be(
                JsonSerializer.Serialize(result.Message, DidCommJson.Default),
                "the registered snapshot must carry the plaintext of the message that was returned");
        return result;
    }

    private static void AssertOneRegistrationPerResult(IReadOnlyCollection<UnpackResult> results, string marker)
    {
        results.Select(r => r.Message).Distinct(ReferenceEqualityComparer.Instance).Should().HaveCount(
            results.Count, "each unpack must deserialize its own Message — a shared/pooled instance would " +
                           "make the second registration a duplicate-key fault");
        results.Should().OnlyContain(r => IsCoveredBySnapshot(r.Message));
        SnapshotsMentioning(marker).Should().HaveCount(
            results.Count, "N unpacks of the same bytes must produce exactly N registrations (#63)");
        GC.KeepAlive(results); // keys must stay reachable until the weak table has been counted
    }

    private static bool IsCoveredBySnapshot(Message message) =>
        InboundMessageSnapshot.TryGetFor(message, out _);

    /// <summary>
    /// Live registrations whose verified plaintext carries <paramref name="marker"/>. Filtering on a
    /// per-test marker makes the count exact while other test classes register concurrently.
    /// </summary>
    private static IReadOnlyList<InboundMessageSnapshot> SnapshotsMentioning(string marker)
    {
        var table = (IEnumerable<KeyValuePair<Message, InboundMessageSnapshot>>)VerifiedSnapshotsField.GetValue(null)!;
        return table
            .Select(entry => entry.Value)
            .Where(s => s.PlaintextJson.Contains(marker, StringComparison.Ordinal))
            .ToArray();
    }

    // ---------------------------------------------------------------------------------------
    // harness
    // ---------------------------------------------------------------------------------------

    private static string NewMarker() => $"m{Guid.NewGuid():N}";

    private static string Base64Url(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A message whose id and body both carry the test's marker, so any registration
    /// derived from it is attributable to this test.</summary>
    private static Message Msg(string marker, string? from = Alice)
    {
        var builder = new MessageBuilder()
            .WithId($"outer-{marker}")
            .WithType(MessageType)
            .WithTo(Bob)
            .WithBody(new JsonObject { ["marker"] = marker });
        if (from is not null)
            builder = builder.WithFrom(from);
        return builder.Build();
    }

    private static DidCommClient Client(IDidResolver resolver, params Jwk[] privateJwks)
        => new(new DictionarySecretsLookup(privateJwks), new NetDidKeyService(resolver), new DidCommOptions());

    private static StaticResolver Resolver(params DidDocument[] documents) => new(documents);

    private sealed record VmSpec(Jwk PublicJwk, VerificationRelationship Relationship);

    private static VmSpec Auth(Jwk publicJwk) => new(publicJwk, VerificationRelationship.Authentication);

    private static VmSpec Ka(Jwk publicJwk) => new(publicJwk, VerificationRelationship.KeyAgreement);

    private static DidDocument Doc(string did, params VmSpec[] methods)
    {
        var vms = new List<VerificationMethod>();
        var auth = new List<VerificationRelationshipEntry>();
        var ka = new List<VerificationRelationshipEntry>();
        foreach (var spec in methods)
        {
            var vm = new VerificationMethod
            {
                Id = spec.PublicJwk.Kid!,
                Type = "JsonWebKey2020",
                Controller = new Did(did),
                PublicKeyJwk = new JsonWebKey
                {
                    Kty = spec.PublicJwk.Kty,
                    Crv = spec.PublicJwk.Crv,
                    X = spec.PublicJwk.X,
                    Y = spec.PublicJwk.Y,
                },
            };
            vms.Add(vm);
            (spec.Relationship == VerificationRelationship.Authentication ? auth : ka)
                .Add(VerificationRelationshipEntry.FromEmbedded(vm));
        }

        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = vms.ToArray(),
            Authentication = auth.ToArray(),
            KeyAgreement = ka.ToArray(),
        };
    }

    /// <summary>Serves a fixed set of documents by DID; every resolution returns the same version.</summary>
    private sealed class StaticResolver : IDidResolver
    {
        private readonly Dictionary<string, DidDocument> _documents;

        public StaticResolver(IEnumerable<DidDocument> documents)
            => _documents = documents.ToDictionary(d => d.Id.ToString(), StringComparer.Ordinal);

        public bool CanResolve(string did) => _documents.ContainsKey(did);

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(_documents.TryGetValue(did, out var doc)
                ? new DidResolutionResult { DidDocument = doc, ResolutionMetadata = new DidResolutionMetadata() }
                : DidResolutionResult.NotFound(did));
    }
}
