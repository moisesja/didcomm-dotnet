using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// ADV-3 (#95): a break-it pass on the REVISED receiver side of rewrapping — layers addressed to the
/// mediator itself are now opened inline, on the sender's request, before any reservation (AD-1);
/// the earliest <c>expires_time</c> of every opened layer bounds the relayed payload (AD-2); a layer
/// that cannot be opened is the sender's mistake, never <c>Faulted</c> (AD-3). Real anoncrypt,
/// authcrypt and JWS layers; the received (outermost) layer is synthetic, decrypted by the
/// mediator's key.
/// </summary>
public sealed class ForwardPeelAdv3Tests
{
    private const string Mediator = "did:example:m";
    private const string Bob = "did:example:bob";
    private const string Payload = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
    private static readonly TimeSpan Flush = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Cap = TimeSpan.FromHours(1);
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    // ── Schedule across layers ─────────────────────────────────────────────────────────────

    // ADV-3 DEFECT 1 (P3, fixed): ForwardHandler's remarks and PRD FR-ROUTE-06 say "a hold asked for
    // by an outer layer is kept: the relayed forward's not-before is the latest of its layers'". Only
    // the received layer's and the innermost forward's delay_milli were read: a self-addressed middle
    // layer's hold was silently dropped. Every opened layer's hold is now drawn and the latest kept.
    [Fact]
    public async Task ADV3_D1_A_hold_asked_for_by_a_middle_layer_is_kept()
    {
        await using var f = Fixture.Create();
        var received = await f.ThreeLayersAsync(middleDelayMs: 600_000);

        await f.HandleAsync(received);

        var result = f.Sink.Results.Should().ContainSingle().Which;
        result.NotBefore.Should().Be(T0.AddMinutes(10), "the middle layer, addressed to this mediator, asked for a 10-minute hold");
    }

    [Fact]
    public async Task Holds_of_several_layers_are_never_added_so_a_chain_is_held_at_most_the_cap()
    {
        // The received layer and the innermost forward each ask for twice the cap: each is drawn
        // under the cap and the later of the two is kept — never their sum (quota occupation stays
        // bounded by the cap, FR-PICK-10).
        await using var f = Fixture.Create();
        var inner = Forward(Bob);
        inner.AdditionalHeaders = Delay((long)(2 * Cap.TotalMilliseconds));
        var outer = ForwardMessage.Create(Mediator, Mediator, new[] { await f.AnoncryptAsync(inner) });
        outer.AdditionalHeaders = Delay((long)(2 * Cap.TotalMilliseconds));

        await f.HandleAsync(AsDecryptedByMediator(outer, Fixture.MediatorKid));

        var result = f.Sink.Results.Should().ContainSingle().Which;
        result.NotBefore.Should().Be(T0 + Cap);
    }

    [Fact]
    public async Task The_earliest_expiry_of_any_layer_bounds_the_relayed_payload_a_middle_layer_included()
    {
        await using var f = Fixture.Create();
        var middleExpiry = T0.AddMinutes(2).ToUnixTimeSeconds();
        var received = await f.ThreeLayersAsync(middleExpires: middleExpiry, outerExpires: T0.AddMinutes(10).ToUnixTimeSeconds(), innerExpires: T0.AddMinutes(5).ToUnixTimeSeconds());

        await f.HandleAsync(received);

        f.Sink.Results.Should().ContainSingle().Which.ExpiresTime.Should().Be(middleExpiry);
    }

    [Fact]
    public async Task A_payload_whose_middle_layer_expires_while_queued_is_discarded_on_the_pump()
    {
        // Inline the middle layer is still valid; the sink is held until the clock passes its expiry.
        await using var f = Fixture.Create();
        var received = await f.ThreeLayersAsync(middleExpires: T0.AddMinutes(2).ToUnixTimeSeconds());
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await f.HandleAsync(received, acknowledged.Task, flush: false);
        f.Now = T0.AddMinutes(3);
        acknowledged.SetResult();
        await f.Handler.FlushAsync(Flush);

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedExpired] = 1 });
    }

    // ── Work per request ───────────────────────────────────────────────────────────────────

    // ADV-3 DEFECT 2 (P3): the CHANGELOG says opening a forward addressed to the mediator "costs one
    // decryption per layer". A layer is opened with the mediator's full unpack, so every layer
    // also costs every DID resolution its sender chose — an authcrypt sender, a signer (sync over
    // async on the request thread), a from_prior issuer — inline, before the 202. One request with
    // 8 layers makes the mediator resolve 8 DIDs of a stranger's choosing (16 resolutions: each DID
    // for its authcrypt key and again for its signature), 8× what the outer unpack alone can be made
    // to resolve. A rewrapping mediator's layer is always a plain anoncrypt
    // (ForwardProcessor.RewrapToNextAsync), so nothing legitimate needs a resolution.
    [Fact(Skip = "ADV-3 DEFECT 2: opening self-addressed layers resolves every sender-chosen DID of every layer inline (8 stranger DIDs for 8 authcrypt+signed layers), not one decryption per layer")]
    public async Task ADV3_D2_Opening_layers_addressed_to_the_mediator_resolves_no_DID_the_sender_chose()
    {
        await using var f = Fixture.Create();
        var received = await f.AuthenticatedChainAsync(ForwardHandler.MaxSelfAddressedLayers);
        f.Resolver.Clear();

        await f.HandleAsync(received);

        f.Resolver.Requested.Where(d => d.StartsWith("did:example:stranger", StringComparison.Ordinal))
            .Should().BeEmpty("a layer addressed to the mediator costs one decryption, not a resolution of DIDs the sender picked");
    }

    [Fact]
    public async Task ADV3_D2_today_one_request_makes_the_mediator_resolve_a_stranger_DID_per_opened_layer_inline()
    {
        await using var f = Fixture.Create();
        var received = await f.AuthenticatedChainAsync(ForwardHandler.MaxSelfAddressedLayers);
        f.Resolver.Clear();

        await f.HandleAsync(received, flush: false);

        // Counted when HandleAsync returned, i.e. on the sender's request, before any acknowledgement.
        var calls = f.Resolver.Requested.Where(d => d.StartsWith("did:example:stranger", StringComparison.Ordinal)).ToArray();
        calls.Distinct().Should().HaveCount(ForwardHandler.MaxSelfAddressedLayers);
        calls.Length.Should().BeGreaterThanOrEqualTo(2 * ForwardHandler.MaxSelfAddressedLayers, "each layer resolves its stranger for the authcrypt key and again for the signature");
        await f.Handler.FlushAsync(Flush);
        f.Sink.Count.Should().Be(1, "the chain is legal by the handler's rules and is relayed");
    }

    [Fact]
    public async Task An_opened_layer_can_be_larger_than_the_layer_it_came_out_of_but_never_over_the_receive_limit()
    {
        // ForwardHandler's remarks: "each [unpack] of a payload smaller than the last". A data.json
        // attachment is re-serialized with the default encoder, which writes '<' as < (six
        // bytes for one), and a JWE ignores members it does not know: the layer the mediator opens
        // is ~6x the attachment it was carried in. The receive limit still bounds every layer.
        await using var f = Fixture.Create();
        var packed = System.Text.Json.Nodes.JsonNode.Parse(await f.AnoncryptAsync(Forward(Bob)))!.AsObject();
        packed["pad"] = new string('<', 100_000);
        var outer = ForwardMessage.Create(Mediator, Mediator, new[] { packed.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) });
        var attachmentBytes = Encoding.UTF8.GetByteCount(outer.Attachments![0].Data.Json!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var openedBytes = Encoding.UTF8.GetByteCount(outer.Attachments![0].Data.Json!.ToJsonString());

        await f.HandleAsync(AsDecryptedByMediator(outer, Fixture.MediatorKid));

        f.Sink.Count.Should().Be(1, "the padded layer opened and the forward inside was relayed");
        openedBytes.Should().BeGreaterThan(5 * attachmentBytes);
        openedBytes.Should().BeLessThan(f.Options.MaxReceiveBytes);

        // Past the limit the layer is refused (Dropped-Malformed), never opened.
        packed["pad"] = new string('<', (int)(f.Options.MaxReceiveBytes / 6) + 1);
        await f.HandleAsync(AsDecryptedByMediator(ForwardMessage.Create(Mediator, Mediator, new[] { packed.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) }), Fixture.MediatorKid));
        f.Sink.Count.Should().Be(1);
        f.Outcomes[ForwardOutcomes.DroppedMalformed].Should().Be(1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(ForwardHandler.MaxSelfAddressedLayers)]
    public async Task Whatever_the_depth_one_request_asks_the_registry_once(int layers)
    {
        await using var f = Fixture.Create();

        await f.HandleAsync(await f.NestedAsync(layers));

        f.Registry.Calls.Should().Be(1);
        f.Registry.Requested.Should().Equal(Bob);
        f.Sink.Count.Should().Be(1);
    }

    // ── Byte budget ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_self_addressed_forward_is_charged_for_the_received_and_the_opened_forward_and_one_byte_over_is_dropped_inline()
    {
        await using var f = Fixture.Create();
        var received = await f.NestedAsync(1);
        var outerBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(received.Message, DidComm.Json.DidCommJson.Default));
        var opened = await f.Client.UnpackAsync(received.Message.Attachments![0].Data.Json!.ToJsonString());
        InboundMessageSnapshot.TryGetFor(opened.Message, out var snapshot).Should().BeTrue();
        long charge = outerBytes + snapshot.Utf8ByteCount;

        // Exactly at the budget: admitted, and both layers are charged while it waits for its 202.
        var exact = f.NewHandler(charge);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (await exact.HandleAsync(received.Message, f.Context(received, acknowledged.Task), CancellationToken.None)).Should().BeNull();
        exact.Outstanding.Should().Be(1);
        exact.OutstandingBytes.Should().Be(charge, "the received forward and the forward opened out of it are both held");
        acknowledged.SetResult();
        await exact.FlushAsync(Flush);
        f.Sink.Count.Should().Be(1);
        exact.OutstandingBytes.Should().Be(0);

        // One byte less: refused inline as saturated, nothing reserved.
        var under = f.NewHandler(charge - 1);
        (await under.HandleAsync(received.Message, f.Context(received, Task.CompletedTask), CancellationToken.None)).Should().BeNull();
        await under.FlushAsync(Flush);
        f.Sink.Count.Should().Be(1, "nothing more reached the sink");
        under.Outstanding.Should().Be(0);
        f.Outcomes[ForwardOutcomes.DroppedSaturated].Should().Be(1);
    }

    // ── Acknowledgement rule ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_layer_opened_inline_is_never_relayed_when_the_request_is_aborted_before_its_202()
    {
        await using var f = Fixture.Create();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await f.HandleAsync(await f.NestedAsync(3), acknowledged.Task, flush: false);
        f.Handler.Outstanding.Should().Be(1, "opened and reserved inline");
        acknowledged.SetCanceled();
        await f.Handler.FlushAsync(Flush);

        f.Sink.Count.Should().Be(0);
        f.Handler.Outstanding.Should().Be(0);
        f.Handler.OutstandingBytes.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnacknowledged] = 1 });
    }

    [Fact]
    public async Task A_request_cancelled_while_a_layer_is_being_opened_leaves_as_cancellation_with_nothing_reserved()
    {
        await using var f = Fixture.Create();
        using var cts = new CancellationTokenSource();
        var received = await f.NestedAsync(2);
        f.Secrets.OnFindPresent = cts.Cancel; // the request is aborted while the first inner layer is being decrypted

        var context = f.Context(received, Task.CompletedTask);
        await f.Handler.Awaiting(h => h.HandleAsync(received.Message, context, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        await f.Handler.FlushAsync(Flush);

        f.Sink.Count.Should().Be(0);
        f.Handler.Outstanding.Should().Be(0);
        f.Registry.Calls.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnacknowledged] = 1 });
    }

    // ── No Faulted a stranger controls; no exception text in the logs ──────────────────────

    public enum ResolverFault
    {
        InvalidOperation,
        TaskCanceledWithoutTheRequestToken,
        HttpRequest,
        NotFound,
    }

    [Theory]
    [InlineData(ResolverFault.InvalidOperation)]
    [InlineData(ResolverFault.TaskCanceledWithoutTheRequestToken)]
    [InlineData(ResolverFault.HttpRequest)]
    [InlineData(ResolverFault.NotFound)]
    public async Task A_layer_whose_sender_DID_resolution_fails_in_any_way_is_a_malformed_drop_with_type_names_only(ResolverFault fault)
    {
        await using var f = Fixture.Create();
        const string marker = "did:example:leak\r\n[CRITICAL] forged line";
        f.Resolver.Fail = fault switch
        {
            ResolverFault.InvalidOperation => new InvalidOperationException(marker),
            ResolverFault.TaskCanceledWithoutTheRequestToken => new TaskCanceledException(marker),
            ResolverFault.HttpRequest => new System.Net.Http.HttpRequestException(marker),
            _ => null,
        };
        f.Resolver.FailFor = fault == ResolverFault.NotFound ? null : "did:example:stranger-1";
        f.Resolver.Forget = fault == ResolverFault.NotFound ? "did:example:stranger-1" : null;
        var received = await f.AuthenticatedChainAsync(1);

        await f.HandleAsync(received);

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 1 }, "{0}", fault);
        ForwardHandlerTests.AssertLogsCarryNoExceptionObjectAndNone(f.Logger, "did:example:leak", "forged line", "stranger", Bob);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────

    private static Message Forward(string next) => ForwardMessage.Create(Mediator, next, new[] { Payload });

    private static Dictionary<string, JsonElement> Delay(long millis) => new(StringComparer.Ordinal)
    {
        ["delay_milli"] = JsonDocument.Parse(millis.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
    };

    /// <summary>A resolver that counts every DID it is asked for, can forget one, and can throw for one.</summary>
    private sealed class CountingResolver : IDidResolver
    {
        private readonly Dictionary<string, DidDocument> _docs;
        private ConcurrentQueue<string> _requested = new();

        public CountingResolver(IEnumerable<DidDocument> docs) => _docs = docs.ToDictionary(d => d.Id.Value, StringComparer.Ordinal);

        public IReadOnlyCollection<string> Requested => _requested;
        public Exception? Fail { get; set; }
        public string? FailFor { get; set; }
        public string? Forget { get; set; }

        public void Clear() => _requested = new ConcurrentQueue<string>();

        public bool CanResolve(string did) => true;

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            _requested.Enqueue(did);
            if (Fail is { } fail && string.Equals(did, FailFor, StringComparison.Ordinal))
                return Task.FromException<DidResolutionResult>(fail);
            if (string.Equals(did, Forget, StringComparison.Ordinal) || !_docs.TryGetValue(did, out var doc))
                return Task.FromResult(DidResolutionResult.NotFound(did));
            return Task.FromResult(new DidResolutionResult { DidDocument = doc, ResolutionMetadata = new DidResolutionMetadata() });
        }
    }

    /// <summary>The mediator's secrets, with a hook that runs when a held key is looked up (a decryption is under way).</summary>
    private sealed class HookedSecrets : DidComm.Secrets.ISecretsResolver
    {
        private readonly SpySecretsResolver _inner;
        public HookedSecrets(params DataProofsDotnet.Jose.Jwk[] keys) => _inner = new SpySecretsResolver(keys);
        public Action? OnFindPresent { get; set; }
        public Task<DataProofsDotnet.Jose.Jwk?> FindAsync(string kid, CancellationToken ct = default) => _inner.FindAsync(kid, ct);
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
        {
            OnFindPresent?.Invoke();
            return _inner.FindPresentAsync(kids, ct);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string MediatorKid = Mediator + "#ka-1";

        private readonly TestMeterFactory _meters = new();
        private readonly List<ForwardHandler> _handlers = new();
        private DidCommClient _stranger = null!;
        private ForwardProcessor _processor = null!;
        private ForwardMetrics _metrics = null!;

        private Fixture() { }

        public DateTimeOffset Now { get; set; } = T0;
        public DidCommClient Client { get; private set; } = null!;
        public DidCommOptions Options { get; private set; } = null!;
        public ForwardHandler Handler { get; private set; } = null!;
        public ForwardHandlerTests.CountingRegistry Registry { get; private set; } = null!;
        public ForwardHandlerTests.CapturingSink Sink { get; } = new();
        public ForwardHandlerTests.CapturingLogger Logger { get; } = new();
        public ForwardOutcomeRecorder Outcomes { get; private set; } = null!;
        public CountingResolver Resolver { get; private set; } = null!;
        public HookedSecrets Secrets { get; private set; } = null!;

        public static Fixture Create(long byteBudget = ForwardHandler.DefaultByteBudget)
        {
            var f = new Fixture();
            f.Options = new DidCommOptions { Clock = () => f.Now };
            var mediatorKey = TestKeyMaterial.Generate(KeyType.X25519, MediatorKid);
            var docs = new List<DidDocument> { LengthCap.Doc(Mediator, keyAgreement: mediatorKey.PublicJwk) };
            var strangerSecrets = new List<DataProofsDotnet.Jose.Jwk>();
            for (var i = 1; i <= ForwardHandler.MaxSelfAddressedLayers; i++)
            {
                // One stranger DID per layer, with a keyAgreement key (authcrypt) and an
                // authentication key (the signature inside it).
                var sender = TestKeyMaterial.Generate(KeyType.X25519, $"did:example:stranger-{i}#ka-1");
                var signer = TestKeyMaterial.Generate(KeyType.Ed25519, $"did:example:stranger-{i}#auth-1");
                docs.Add(LengthCap.Doc($"did:example:stranger-{i}", authentication: signer.PublicJwk, keyAgreement: sender.PublicJwk));
                strangerSecrets.Add(sender.PrivateJwk);
                strangerSecrets.Add(signer.PrivateJwk);
            }

            f.Resolver = new CountingResolver(docs);
            var keyService = new NetDidKeyService(f.Resolver);
            f.Secrets = new HookedSecrets(mediatorKey.PrivateJwk);
            f.Client = new DidCommClient(f.Secrets, keyService, f.Options);
            f._stranger = new DidCommClient(new SpySecretsResolver(strangerSecrets.ToArray()), new NetDidKeyService(new SpyDidResolver(docs.ToArray())), f.Options);

            f._processor = new ForwardProcessor(f.Client, keyService, new ForwardProcessorOptions { MaxDelay = Cap }, new StaticRecipientRoutingKeyProvider(null), f.Options);
            var recipients = new InMemoryForwardRecipientRegistry();
            recipients.Register(Bob);
            f.Registry = new ForwardHandlerTests.CountingRegistry(recipients);
            f.Outcomes = ForwardOutcomeRecorder.For(f._meters);
            f._metrics = new ForwardMetrics(f._meters);
            f.Handler = f.NewHandler(byteBudget);
            return f;
        }

        /// <summary>Another handler over the same mediator, registry, sink, logger and outcome counter.</summary>
        public ForwardHandler NewHandler(long byteBudget)
        {
            var handler = new ForwardHandler(_processor, Sink, Registry, Logger, ForwardHandler.DefaultCapacity, shutdownGrace: TimeSpan.FromSeconds(2), byteBudget, _metrics);
            _handlers.Add(handler);
            return handler;
        }

        public async Task<string> AnoncryptAsync(Message message)
            => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { Mediator }))).Message;

        /// <summary>Received (synthetic, self-addressed) → middle (self-addressed) → inner forward for Bob.</summary>
        public async Task<UnpackResult> ThreeLayersAsync(long? middleDelayMs = null, long? middleExpires = null, long? outerExpires = null, long? innerExpires = null)
        {
            var inner = ForwardMessage.Create(Mediator, Bob, new[] { Payload }, expiresTimeEpochSeconds: innerExpires);
            var middle = ForwardMessage.Create(Mediator, Mediator, new[] { await AnoncryptAsync(inner) }, expiresTimeEpochSeconds: middleExpires);
            if (middleDelayMs is { } ms)
                middle.AdditionalHeaders = Delay(ms);
            var outer = ForwardMessage.Create(Mediator, Mediator, new[] { await AnoncryptAsync(middle) }, expiresTimeEpochSeconds: outerExpires);
            return AsDecryptedByMediator(outer, MediatorKid);
        }

        /// <summary><paramref name="layers"/> self-addressed anoncrypt layers around a forward for Bob.</summary>
        public async Task<UnpackResult> NestedAsync(int layers)
        {
            var packed = await AnoncryptAsync(Forward(Bob));
            for (var i = 1; i < layers; i++)
                packed = await AnoncryptAsync(ForwardMessage.Create(Mediator, Mediator, new[] { packed }));
            return AsDecryptedByMediator(ForwardMessage.Create(Mediator, Mediator, new[] { packed }), MediatorKid);
        }

        /// <summary>
        /// <paramref name="layers"/> layers opened by the mediator, each authcrypted to it and signed
        /// by its own stranger DID — the innermost carries the forward for Bob, the others are
        /// self-addressed — inside a synthetic received layer.
        /// </summary>
        public async Task<UnpackResult> AuthenticatedChainAsync(int layers)
        {
            string packed = Payload;
            for (var i = layers; i >= 1; i--)
            {
                var next = i == layers ? Bob : Mediator;
                var forward = ForwardMessage.Create(Mediator, next, new[] { packed });
                forward.From = $"did:example:stranger-{i}";
                packed = (await _stranger.PackEncryptedAsync(forward, new PackEncryptedOptions(
                    Recipients: new[] { Mediator },
                    From: $"did:example:stranger-{i}",
                    SignFrom: $"did:example:stranger-{i}"))).Message;
            }
            return AsDecryptedByMediator(ForwardMessage.Create(Mediator, Mediator, new[] { packed }), MediatorKid);
        }

        public ProtocolContext Context(UnpackResult received, Task acknowledged)
            => new(received, new DidComm.Threading.ThreadState(received.Message.Id), Client: null, Options, new DidComm.Threading.InMemoryThreadStateStore())
            {
                Acknowledged = acknowledged,
            };

        public Task HandleAsync(UnpackResult received, bool flush = true) => HandleAsync(received, Task.CompletedTask, flush);

        public async Task HandleAsync(UnpackResult received, Task acknowledged, bool flush = true)
        {
            (await Handler.HandleAsync(received.Message, Context(received, acknowledged), CancellationToken.None)).Should().BeNull();
            if (flush)
                await Handler.FlushAsync(Flush);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var handler in _handlers)
                await handler.DisposeAsync();
            Outcomes.Dispose();
            _meters.Dispose();
        }
    }
}
