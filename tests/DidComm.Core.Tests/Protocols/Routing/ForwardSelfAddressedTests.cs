using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NetCrypto;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// #95 Q-2: the receiver side of rewrapping (FR-ROUTE-06; DIDComm v2.1 §Routing "Rewrapping": "the
/// outer envelope is a forward message where to is the receiver itself"). A forward whose
/// <c>next</c> is the DID that decrypted it is not looked up in the registry: <see cref="ForwardHandler"/>
/// opens its single attachment as it is, with the mediator's own client, and screens the forward
/// inside like an arriving one — recursively, at most <see cref="ForwardHandler.MaxSelfAddressedLayers"/>
/// layers. Real anoncrypt layers to a mediator that holds keys for two DIDs; the received (outermost)
/// layer is synthetic, decrypted by the mediator's first key. One outcome per received forward.
/// </summary>
public sealed class ForwardSelfAddressedTests
{
    private const string Mediator = "did:example:m";
    private const string OtherMediatorDid = "did:example:m2";
    private const string Bob = "did:example:bob";
    private const string Carol = "did:example:carol";
    private const string Stranger = "did:example:stranger";
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
    private static readonly TimeSpan Flush = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    // ── One layer ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_forward_addressed_to_the_mediator_itself_is_opened_and_the_forward_inside_relayed()
    {
        await using var f = await Fixture.CreateAsync();
        var received = f.SelfAddressed(await f.AnoncryptAsync(Forward(Bob)));

        await f.HandleAsync(received);

        var result = f.Sink.Results.Should().ContainSingle().Which;
        result.NextHop.Should().Be(Bob);
        SamePayload(result.OnwardPacked).Should().BeTrue("the forward inside is relayed under the mediator's own mode (pass-through): its attachment, untransformed");
        f.Sink.Contexts.Single().Received.Message.Id.Should().Be(received.Message.Id, "the sink is handed the received forward's context");
        f.Registry.Requested.Should().Equal(new[] { Bob }, "the mediator's own DID is never looked up; the forward inside is");
        f.Outcomes.Total.Should().Be(0, "the forward reached the sink, which counts it");
        f.Handler.Outstanding.Should().Be(0);
    }

    [Theory]
    [InlineData("#ka-1")]
    [InlineData("#some-other-key")]
    [InlineData("/path")]
    [InlineData("?service=x")]
    public async Task A_next_that_is_a_DID_URL_of_the_decrypting_DID_counts_as_the_mediator_itself(string suffix)
    {
        await using var f = await Fixture.CreateAsync();

        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(Forward(Bob)), next: Mediator + suffix));

        f.Sink.Results.Should().ContainSingle().Which.NextHop.Should().Be(Bob);
        f.Registry.Requested.Should().Equal(Bob);
    }

    [Fact]
    public async Task A_next_naming_another_DID_of_the_mediator_is_looked_up_and_dropped_as_unknown()
    {
        // Decided and documented: only the DID whose key decrypted the forward is "the receiver
        // itself". A conformant rewrapper encrypts to the keys of the DID it names, so the two always
        // agree; a forward where they differ is treated like any other next.
        await using var f = await Fixture.CreateAsync();

        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(Forward(Bob)), next: OtherMediatorDid));

        f.Sink.Count.Should().Be(0);
        f.Registry.Requested.Should().Equal(OtherMediatorDid);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnknown] = 1 });
    }

    [Fact]
    public async Task A_layer_decrypted_by_the_mediators_other_DID_is_its_own_receiver_too()
    {
        await using var f = await Fixture.CreateAsync();
        var inside = ForwardMessage.Create(OtherMediatorDid, Bob, new[] { Inner });
        var received = f.SelfAddressed(await f.AnoncryptAsync(inside, to: OtherMediatorDid), next: OtherMediatorDid, decryptedBy: OtherMediatorDid + "#ka-1");

        await f.HandleAsync(received);

        f.Sink.Results.Should().ContainSingle().Which.NextHop.Should().Be(Bob);
    }

    // ── Nested layers and the bound ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(ForwardHandler.MaxSelfAddressedLayers)]
    public async Task Nested_layers_addressed_to_the_mediator_are_opened_up_to_the_bound(int layers)
    {
        await using var f = await Fixture.CreateAsync();

        await f.HandleAsync(await f.NestedAsync(layers, Forward(Bob)));

        f.Sink.Results.Should().ContainSingle().Which.NextHop.Should().Be(Bob);
        SamePayload(f.Sink.Results.Single().OnwardPacked).Should().BeTrue();
        f.Outcomes.Total.Should().Be(0);
    }

    [Fact]
    public async Task One_layer_beyond_the_bound_is_dropped_as_malformed()
    {
        await using var f = await Fixture.CreateAsync();

        await f.HandleAsync(await f.NestedAsync(ForwardHandler.MaxSelfAddressedLayers + 1, Forward(Bob)));

        f.Sink.Count.Should().Be(0);
        f.Registry.Requested.Should().BeEmpty("the chain never reached a forward for someone else");
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 1 });
        f.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("layers addressed to this mediator itself");
    }

    // ── What is inside ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_message_inside_that_is_not_a_forward_is_dropped_as_malformed()
    {
        await using var f = await Fixture.CreateAsync();
        var note = new MessageBuilder().WithType("https://example.org/note/1.0/note").WithTo(Mediator).WithBody(new JsonObject { ["text"] = "hi" }).Build();

        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(note)));

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 1 });
        f.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("not a forward");
    }

    [Fact]
    public async Task A_message_inside_that_the_mediator_cannot_decrypt_is_dropped_as_malformed()
    {
        await using var f = await Fixture.CreateAsync();

        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(Forward(Bob), to: Stranger)));

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 1 });
        f.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("could not be opened");
    }

    [Fact]
    public async Task A_payload_inside_that_is_not_an_envelope_is_dropped_as_malformed()
    {
        await using var f = await Fixture.CreateAsync();

        await f.HandleAsync(f.SelfAddressed("""{"not":"an envelope"}"""));

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 1 });
    }

    [Fact]
    public async Task A_plaintext_forward_inside_is_dropped_as_not_encrypted()
    {
        await using var f = await Fixture.CreateAsync();
        var plaintext = await f.Client.PackPlaintextAsync(Forward(Bob));

        await f.HandleAsync(f.SelfAddressed(plaintext));

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedNotEncrypted] = 1 });
    }

    [Fact]
    public async Task A_forward_inside_that_has_expired_when_it_arrives_is_dropped_as_expired()
    {
        // #95 AD-1: layers are opened at receipt, on the request; the inner forward is judged then.
        await using var f = await Fixture.CreateAsync();
        var inner = await f.AnoncryptAsync(Forward(Bob, expires: T0.AddSeconds(10).ToUnixTimeSeconds()));
        f.Now = T0.AddSeconds(20);

        await f.HandleAsync(f.SelfAddressed(inner));

        f.Sink.Count.Should().Be(0);
        f.Handler.Outstanding.Should().Be(0, "dropped before anything was reserved");
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedExpired] = 1 });
        f.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("the forward inside it has expired");
    }

    [Fact]
    public async Task A_forward_inside_that_expires_while_waiting_for_the_acknowledgement_is_dropped_as_expired()
    {
        await using var f = await Fixture.CreateAsync();
        var inner = await f.AnoncryptAsync(Forward(Bob, expires: T0.AddSeconds(10).ToUnixTimeSeconds()));
        var gate = new TaskCompletionSource();

        await f.HandleAsync(f.SelfAddressed(inner), gate.Task, flush: false);
        f.Now = T0.AddSeconds(20);   // the inner forward expires while the outer waits for its acknowledgement
        gate.SetResult();
        await f.Handler.FlushAsync(Flush);

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedExpired] = 1 });
        f.Logger.Entries.Should().ContainSingle(e => e.Formatted.Contains("expired while queued"));
    }

    [Fact]
    public async Task A_forward_inside_for_an_unregistered_next_is_dropped_as_unknown()
    {
        await using var f = await Fixture.CreateAsync();

        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(Forward(Carol))));

        f.Sink.Count.Should().Be(0);
        f.Registry.Requested.Should().Equal(Carol);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnknown] = 1 });
    }

    [Fact]
    public async Task A_forward_inside_whose_next_is_not_a_DID_is_dropped_as_unknown()
    {
        await using var f = await Fixture.CreateAsync();

        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(Forward("not-a-did"))));

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnknown] = 1 });
    }

    [Fact]
    public async Task Two_attachments_on_the_layer_addressed_to_the_mediator_are_dropped_as_malformed()
    {
        await using var f = await Fixture.CreateAsync();
        var jwe = await f.AnoncryptAsync(Forward(Bob));
        var received = AsDecryptedByMediator(ForwardMessage.Create(Mediator, Mediator, new[] { jwe, jwe }), Fixture.MediatorKid);

        await f.HandleAsync(received);

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 1 });
    }

    [Fact]
    public async Task A_layer_that_expired_while_queued_is_not_opened()
    {
        await using var f = await Fixture.CreateAsync();
        var gate = new TaskCompletionSource();
        var outer = ForwardMessage.Create(Mediator, Mediator, new[] { await f.AnoncryptAsync(Forward(Bob)) }, expiresTimeEpochSeconds: T0.AddSeconds(10).ToUnixTimeSeconds());

        await f.HandleAsync(AsDecryptedByMediator(outer, Fixture.MediatorKid), gate.Task, flush: false);
        f.Now = T0.AddSeconds(20);
        gate.SetResult();
        await f.Handler.FlushAsync(Flush);

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedExpired] = 1 });
    }

    // ── Schedule ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_forward_inside_has_its_own_delay_drawn_at_receipt_under_the_cap()
    {
        // #95 AD-1: the layer is opened at receipt (one clock reading per received forward), so a
        // late acknowledgement never moves the inner forward's not-before.
        await using var f = await Fixture.CreateAsync(maxDelay: TimeSpan.FromHours(1));
        var gate = new TaskCompletionSource();

        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(WithDelay(Forward(Bob), long.MaxValue))), gate.Task, flush: false);
        f.Now = T0.AddMinutes(5);
        gate.SetResult();
        await f.Handler.FlushAsync(Flush);

        var result = f.Sink.Results.Should().ContainSingle().Which;
        result.Delay.Should().Be(TimeSpan.FromHours(1), "capped");
        result.NotBefore.Should().Be(T0.AddHours(1), "drawn at receipt, when the layer was opened");
    }

    [Fact]
    public async Task Opening_happens_on_the_request_before_anything_is_reserved()
    {
        // #95 AD-1: the sender's own request opens what it sent; the pump never sees a layer
        // addressed to the mediator, and a drop while opening reserves nothing.
        await using var f = await Fixture.CreateAsync();
        var gate = new TaskCompletionSource();

        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(Forward(Bob))), gate.Task, flush: false);

        f.Registry.Requested.Should().Equal(new[] { Bob }, "the forward inside was opened and looked up on the request");
        f.Handler.Outstanding.Should().Be(1, "the forward found inside is reserved, waiting for the acknowledgement");
        gate.SetResult();
        await f.Handler.FlushAsync(Flush);
        f.Sink.Results.Should().ContainSingle().Which.NextHop.Should().Be(Bob);

        await f.HandleAsync(f.SelfAddressed("""{"not":"an envelope"}"""), new TaskCompletionSource().Task, flush: false);
        f.Handler.Outstanding.Should().Be(0, "a layer that cannot be opened is dropped before a reservation");
    }

    [Fact]
    public async Task A_hold_asked_for_by_the_outer_layer_is_kept_when_it_ends_later()
    {
        await using var f = await Fixture.CreateAsync(maxDelay: TimeSpan.FromHours(1));
        var outer = WithDelay(ForwardMessage.Create(Mediator, Mediator, new[] { await f.AnoncryptAsync(WithDelay(Forward(Bob), 500)) }), 3_600_000);

        await f.HandleAsync(AsDecryptedByMediator(outer, Fixture.MediatorKid));

        var result = f.Sink.Results.Should().ContainSingle().Which;
        result.NotBefore.Should().Be(T0.AddHours(1), "the later of the layers' not-befores");
    }

    // ── Logs ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_line_on_any_path_carries_a_DID_a_kid_or_an_exception_object()
    {
        await using var f = await Fixture.CreateAsync();
        var note = new MessageBuilder().WithType("https://example.org/note/1.0/note").WithTo(Mediator).WithBody(new JsonObject()).Build();

        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(note)));
        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(Forward(Bob), to: Stranger)));
        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(Forward(Carol))));
        await f.HandleAsync(f.SelfAddressed(await f.Client.PackPlaintextAsync(Forward(Bob))));
        await f.HandleAsync(await f.NestedAsync(ForwardHandler.MaxSelfAddressedLayers + 1, Forward(Bob)));

        f.Logger.Entries.Should().HaveCountGreaterThanOrEqualTo(5);
        ForwardHandlerTests.AssertLogsCarryNoExceptionObjectAndNone(f.Logger, "did:", Fixture.MediatorKid, Carol, Stranger);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static Message Forward(string next, long? expires = null) => ForwardMessage.Create(Mediator, next, new[] { Inner }, expiresTimeEpochSeconds: expires);

    /// <summary>The relayed bytes are the innermost attachment (a JSON attachment comes back as the same JSON document, re-serialized).</summary>
    private static bool SamePayload(byte[] onward) => JsonNode.DeepEquals(JsonNode.Parse(onward), JsonNode.Parse(Inner));

    private static Message WithDelay(Message forward, long millis)
    {
        forward.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse(millis.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
        };
        return forward;
    }

    /// <summary>A mediator holding keys for two DIDs, its processor and handler, a registry with Bob, and a capturing sink.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public const string MediatorKid = Mediator + "#ka-1";

        private Fixture(DidCommClient client, DidCommOptions options, ForwardHandler handler, ForwardHandlerTests.CountingRegistry registry,
            ForwardHandlerTests.CapturingSink sink, ForwardHandlerTests.CapturingLogger logger, TestMeterFactory meters, ForwardOutcomeRecorder outcomes, Func<DateTimeOffset> now, Action<DateTimeOffset> setNow)
        {
            Client = client;
            Options = options;
            Handler = handler;
            Registry = registry;
            Sink = sink;
            Logger = logger;
            _meters = meters;
            Outcomes = outcomes;
            _now = now;
            _setNow = setNow;
        }

        private readonly TestMeterFactory _meters;
        private readonly Func<DateTimeOffset> _now;
        private readonly Action<DateTimeOffset> _setNow;

        public DidCommClient Client { get; }
        public DidCommOptions Options { get; }
        public ForwardHandler Handler { get; }
        public ForwardHandlerTests.CountingRegistry Registry { get; }
        public ForwardHandlerTests.CapturingSink Sink { get; }
        public ForwardHandlerTests.CapturingLogger Logger { get; }
        public ForwardOutcomeRecorder Outcomes { get; }

        public DateTimeOffset Now
        {
            get => _now();
            set => _setNow(value);
        }

        public static Task<Fixture> CreateAsync(TimeSpan? maxDelay = null)
        {
            var now = T0;
            var options = new DidCommOptions { Clock = () => now };
            var key1 = TestKeyMaterial.Generate(KeyType.X25519, MediatorKid);
            var key2 = TestKeyMaterial.Generate(KeyType.X25519, OtherMediatorDid + "#ka-1");
            var strangerKey = TestKeyMaterial.Generate(KeyType.X25519, Stranger + "#ka-1");
            var resolver = new SpyDidResolver(
                LengthCap.Doc(Mediator, keyAgreement: key1.PublicJwk),
                LengthCap.Doc(OtherMediatorDid, keyAgreement: key2.PublicJwk),
                LengthCap.Doc(Stranger, keyAgreement: strangerKey.PublicJwk));
            var keyService = new NetDidKeyService(resolver);
            var client = new DidCommClient(new SpySecretsResolver(key1.PrivateJwk, key2.PrivateJwk), keyService, options);
            var processorOptions = new ForwardProcessorOptions { MaxDelay = maxDelay };
            var processor = new ForwardProcessor(client, keyService, processorOptions, new StaticRecipientRoutingKeyProvider(null), options);
            var recipients = new InMemoryForwardRecipientRegistry();
            recipients.Register(Bob);
            var registry = new ForwardHandlerTests.CountingRegistry(recipients);
            var sink = new ForwardHandlerTests.CapturingSink();
            var logger = new ForwardHandlerTests.CapturingLogger();
            var meters = new TestMeterFactory();
            var outcomes = ForwardOutcomeRecorder.For(meters);
            var handler = new ForwardHandler(processor, sink, registry, logger, ForwardHandler.DefaultCapacity, shutdownGrace: null, ForwardHandler.DefaultByteBudget, new ForwardMetrics(meters));
            return Task.FromResult(new Fixture(client, options, handler, registry, sink, logger, meters, outcomes, () => now, value => now = value));
        }

        /// <summary>Anoncrypt <paramref name="message"/> to <paramref name="to"/> (the mediator by default): the packed bytes a layer carries.</summary>
        public async Task<string> AnoncryptAsync(Message message, string to = Mediator)
            => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }))).Message;

        /// <summary>The received layer: a forward addressed to the mediator itself, as decrypted by <paramref name="decryptedBy"/>.</summary>
        public UnpackResult SelfAddressed(string packedInside, string next = Mediator, string decryptedBy = MediatorKid)
            => AsDecryptedByMediator(ForwardMessage.Create(Mediator, next, new[] { packedInside }), decryptedBy);

        /// <summary><paramref name="layers"/> layers addressed to the mediator (the received one included) around <paramref name="innermost"/>.</summary>
        public async Task<UnpackResult> NestedAsync(int layers, Message innermost)
        {
            var packed = await AnoncryptAsync(innermost);
            for (var i = 1; i < layers; i++)
                packed = await AnoncryptAsync(ForwardMessage.Create(Mediator, Mediator, new[] { packed }));
            return SelfAddressed(packed);
        }

        public async Task HandleAsync(UnpackResult received, Task? acknowledged = null, bool flush = true)
        {
            var context = new ProtocolContext(received, new DidComm.Threading.ThreadState(received.Message.Id), Client: null, Options, new DidComm.Threading.InMemoryThreadStateStore())
            {
                Acknowledged = acknowledged ?? Task.CompletedTask,
            };
            (await Handler.HandleAsync(received.Message, context, CancellationToken.None)).Should().BeNull();
            if (flush)
                await Handler.FlushAsync(Flush);
        }

        public async ValueTask DisposeAsync()
        {
            await Handler.DisposeAsync();
            Outcomes.Dispose();
            _meters.Dispose();
        }
    }
}
