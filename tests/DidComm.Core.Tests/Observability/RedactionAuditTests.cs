using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using DidComm.Diagnostics;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Tests.Envelopes;
using DidComm.Threading;
using DidComm.Transports;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Observability;

/// <summary>
/// NFR-04 redaction audit: drive every instrumented operation (pack plaintext/signed/encrypted,
/// unpack success and failure, send, resolve, dispatch) with a listener capturing ALL
/// "didcomm-dotnet" spans and a logger capturing ALL log output, then prove that no private-key
/// material and no plaintext body/attachment content appears anywhere in the emitted telemetry.
/// The positive assertions (the expected spans exist, with the expected NFR-05 attribute keys)
/// keep the audit from passing vacuously on a build that emits nothing.
/// </summary>
/// <remarks>
/// The listener is process-global for the source, so concurrently-running tests may add their
/// spans to the capture set. That is harmless in both directions: the negative assertions scan
/// for THIS test's sentinels and freshly-generated key material (which cannot appear in other
/// tests' spans), and the positive assertions filter by this test's distinctive message type.
/// </remarks>
public sealed class RedactionAuditTests
{
    private const string Alice = "did:example:audit-alice";
    private const string Bob = "did:example:audit-bob";
    private const string AliceKaKid = "did:example:audit-alice#ka-1";
    private const string AliceAuthKid = "did:example:audit-alice#auth-1";
    private const string BobKaKid = "did:example:audit-bob#ka-1";

    private const string ProbeType = "https://example.com/protocols/audit/1.0/redaction-probe";
    private const string BodySentinel = "REDACTION-SENTINEL-BODY-7f3a";
    private const string AttachmentSentinel = "REDACTION-SENTINEL-ATTACHMENT-2c91";

    [Fact]
    public async Task FullFlows_LeakNoSecretsOrPlaintext_IntoSpansOrLogs()
    {
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);

        // Every string that must never surface in telemetry: the sentinels planted in the
        // message, plus the actual private scalars of every key this test uses.
        var forbidden = new[]
        {
            BodySentinel,
            AttachmentSentinel,
            aliceKa.PrivateJwk.D!,
            aliceAuth.PrivateJwk.D!,
            bobKa.PrivateJwk.D!,
        };
        forbidden.Should().OnlyContain(s => !string.IsNullOrEmpty(s), "the audit is meaningless without real material to scan for");

        var resolver = new StaticResolver(
            (Alice, Doc(Alice, aliceKa.PublicJwk, aliceAuth.PublicJwk)),
            (Bob, Doc(Bob, bobKa.PublicJwk, authenticationKey: null)));

        var log = new CapturingLogSink();
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DidCommDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var alice = new DidCommClient(
            new DictionarySecretsLookup(new[] { aliceKa.PrivateJwk, aliceAuth.PrivateJwk }),
            new NetDidKeyService(resolver),
            new DidCommOptions(),
            log.For<DidCommClient>());
        var bob = new DidCommClient(
            new DictionarySecretsLookup(new[] { bobKa.PrivateJwk }),
            new NetDidKeyService(resolver),
            new DidCommOptions(),
            log.For<DidCommClient>());

        // --- pack plaintext + signed + encrypted(signed), then unpack ---
        var message = NewProbeMessage();
        await alice.PackPlaintextAsync(message);
        await alice.PackSignedAsync(message, Alice);
        var packed = await alice.PackEncryptedAsync(
            message,
            new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, SignFrom: Alice));
        var unpacked = await bob.UnpackAsync(packed.Message);
        unpacked.Message.Body!.ToJsonString().Should().Contain(BodySentinel, "the probe must actually carry the sentinel");

        // --- unpack failure path (error span + failure breadcrumb log) ---
        await bob.Invoking(c => c.UnpackAsync("{\"not\":\"didcomm\"}"))
            .Should().ThrowAsync<DidComm.Exceptions.DidCommException>();

        // --- send over a fake transport (explicit endpoint → no routing dependency) ---
        var sender = new DidCommClient(
            new DictionarySecretsLookup(new[] { aliceKa.PrivateJwk, aliceAuth.PrivateJwk }),
            new NetDidKeyService(resolver),
            new NoServicesResolver(),
            new AcceptingRouter(),
            new DidCommOptions());
        await sender.SendAsync(
            NewProbeMessage(),
            new SendOptions(Recipients: new[] { Bob }, From: Alice)
            {
                ServiceEndpointOverride = new Uri("https://audit.example/inbox"),
            });

        // --- dispatch (no handler registered → NoHandler outcome span) ---
        using var dispatcher = new ProtocolDispatcher(
            new ProtocolHandlerRegistry(),
            new InMemoryThreadStateStore(),
            log.For<ProtocolDispatcher>());
        var outcome = await dispatcher.DispatchAsync(unpacked, bob, new DidCommOptions());
        outcome.Result.Should().Be(DispatchResult.NoHandler);

        // ---------- NEGATIVE: nothing secret anywhere in the telemetry ----------
        var capturedSpans = spans.ToArray();
        foreach (var span in capturedSpans)
        {
            foreach (var secret in forbidden)
            {
                span.DisplayName.Should().NotContain(secret);
                span.OperationName.Should().NotContain(secret);
                (span.StatusDescription ?? string.Empty).Should().NotContain(secret);
                foreach (var (key, value) in span.TagObjects)
                {
                    key.Should().NotContain(secret);
                    (value?.ToString() ?? string.Empty).Should().NotContain(
                        secret, $"span '{span.OperationName}' tag '{key}' must not leak secrets (NFR-04)");
                }
                foreach (var evt in span.Events)
                    evt.Name.Should().NotContain(secret);
            }
        }
        foreach (var entry in log.Entries)
        {
            foreach (var secret in forbidden)
            {
                entry.Message.Should().NotContain(secret, "log output must not leak secrets (NFR-04)");
            }
        }

        // ---------- POSITIVE: the audit saw the real instrumentation (NFR-05) ----------
        Activity SpanOf(string operation) =>
            capturedSpans.FirstOrDefault(a =>
                a.OperationName == operation
                && Equals(a.GetTagItem(DidCommDiagnostics.MessageTypeTag), ProbeType))
            ?? throw new Xunit.Sdk.XunitException($"expected a '{operation}' span tagged with the probe message type");

        SpanOf(DidCommDiagnostics.PackPlaintextActivity);
        SpanOf(DidCommDiagnostics.PackSignedActivity);

        var packSpan = SpanOf(DidCommDiagnostics.PackEncryptedActivity);
        packSpan.GetTagItem(DidCommDiagnostics.AlgTag).Should().Be(JoseAlgorithms.Ecdh1PuA256Kw);
        packSpan.GetTagItem(DidCommDiagnostics.EncTag).Should().Be(JoseAlgorithms.A256CbcHs512);
        packSpan.GetTagItem(DidCommDiagnostics.RecipientsTag).Should().Be(1);

        var unpackSpan = SpanOf(DidCommDiagnostics.UnpackActivity);
        unpackSpan.GetTagItem(DidCommDiagnostics.AlgTag).Should().Be(JoseAlgorithms.Ecdh1PuA256Kw);

        var sendSpan = SpanOf(DidCommDiagnostics.SendActivity);
        sendSpan.GetTagItem(DidCommDiagnostics.TransportSchemeTag).Should().Be("https");

        var handleSpan = SpanOf(DidCommDiagnostics.HandleActivity);
        handleSpan.GetTagItem(DidCommDiagnostics.DispatchResultTag).Should().Be(nameof(DispatchResult.NoHandler));

        capturedSpans.Should().Contain(
            a => a.OperationName == DidCommDiagnostics.ResolveActivity
                 && Equals(a.GetTagItem(DidCommDiagnostics.DidMethodTag), "example"),
            "DID resolution must emit a resolve span tagged with the method name only");

        capturedSpans.Should().Contain(
            a => a.OperationName == DidCommDiagnostics.UnpackActivity && a.Status == ActivityStatusCode.Error,
            "the failed unpack must surface as an error-status span");

        log.Entries.Should().Contain(
            e => e.Level == LogLevel.Debug && e.Message.Contains("Unpack failed"),
            "the failure breadcrumb (NFR-06) must fire — and it is covered by the negative scan above");
    }

    private static Message NewProbeMessage()
    {
        var attachment = new Attachment
        {
            Id = "audit-attachment",
            MediaType = "application/json",
            Data = new AttachmentData { Json = JsonNode.Parse($"{{\"secret\":\"{AttachmentSentinel}\"}}") },
        };
        return new MessageBuilder()
            .WithType(ProbeType)
            .WithFrom(Alice)
            .WithTo(Bob)
            .WithBody(JsonNode.Parse($"{{\"note\":\"{BodySentinel}\"}}")!.AsObject())
            .WithAttachment(attachment)
            .Build();
    }

    private static DidDocument Doc(string did, Jwk keyAgreementKey, Jwk? authenticationKey)
    {
        var vms = new List<VerificationMethod>();
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
        vms.Add(ka);

        VerificationMethod? auth = null;
        if (authenticationKey is not null)
        {
            auth = new VerificationMethod
            {
                Id = authenticationKey.Kid!,
                Type = "JsonWebKey2020",
                Controller = new Did(did),
                PublicKeyJwk = new JsonWebKey
                {
                    Kty = authenticationKey.Kty,
                    Crv = authenticationKey.Crv,
                    X = authenticationKey.X,
                },
            };
            vms.Add(auth);
        }

        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = vms,
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(ka) },
            Authentication = auth is null
                ? Array.Empty<VerificationRelationshipEntry>()
                : new[] { VerificationRelationshipEntry.FromEmbedded(auth) },
        };
    }

    /// <summary>One shared sink; per-category <see cref="ILogger{T}"/> views write into it.</summary>
    private sealed class CapturingLogSink
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger<T> For<T>() => new View<T>(this);

        private sealed class View<T> : ILogger<T>
        {
            private readonly CapturingLogSink _sink;
            public View(CapturingLogSink sink) => _sink = sink;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _sink.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

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
