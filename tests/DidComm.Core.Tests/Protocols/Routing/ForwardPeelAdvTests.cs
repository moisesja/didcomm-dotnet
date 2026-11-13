using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// ADV-1 (#95): adversarial pass over the receiver side of rewrapping — a forward whose <c>next</c>
/// is the DID that decrypted it is opened on <see cref="ForwardHandler"/>'s pump and the forward
/// inside relayed (FR-ROUTE-06). Who can make the mediator open what, what each hostile layer ends
/// in, what the pump owes a stranger, and what the logs carry. Real anoncrypt / JWS layers; the
/// received (outermost) layer is synthetic, decrypted by the mediator's first key.
/// </summary>
public sealed class ForwardPeelAdvTests
{
    private const string Mediator = "did:example:m";
    private const string OtherMediatorDid = "did:example:m2";
    private const string Bob = "did:example:bob";
    private const string Tarpit = "did:example:tarpit";
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
    private static readonly TimeSpan Flush = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    // ── Identity: what the peel lets a stranger reach ─────────────────────────────────────

    [Fact]
    public async Task A_mediator_service_message_inside_a_self_addressed_forward_is_never_dispatched_or_looked_up()
    {
        // Forwards are outside FR-RR-08 replay admission, so a captured pickup / coordinate-mediation
        // request re-wrapped in a self-addressed forward must not be executed by the peel: what is
        // inside and is not a forward is dropped, and nothing about it reaches the registry.
        await using var f = await Fixture.CreateAsync();
        foreach (var type in new[]
        {
            "https://didcomm.org/messagepickup/3.0/delivery-request",
            "https://didcomm.org/messagepickup/3.0/messages-received",
            "https://didcomm.org/coordinate-mediation/3.0/recipient-update",
            "https://didcomm.org/coordinate-mediation/3.0/mediate-request",
        })
        {
            var request = new MessageBuilder().WithType(type).WithFrom(Bob).WithTo(Mediator)
                .WithBody(new JsonObject { ["recipient_did"] = Bob, ["limit"] = 10 }).Build();
            await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(request)));
        }

        f.Sink.Count.Should().Be(0);
        f.Registry.Requested.Should().BeEmpty("the peel never treats a mediator-service message as a forward");
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 4 });
    }

    [Fact]
    public async Task An_inner_forward_naming_the_received_layers_DID_but_decrypted_by_another_held_DID_is_looked_up_not_peeled()
    {
        // Layer 1: next = M, decrypted by M. Layer 2: next = M again, but encrypted to M2's key. Only
        // the DID that decrypted a layer counts as "itself": layer 2 names M while M2 decrypted it, so
        // M is looked up (never registered) and the chain ends as unknown — the peel never hops to a
        // DID the layer was not addressed to.
        await using var f = await Fixture.CreateAsync();
        var layer3 = await f.AnoncryptAsync(Forward(Bob));
        var layer2 = await f.AnoncryptAsync(ForwardMessage.Create(OtherMediatorDid, Mediator, new[] { layer3 }), to: OtherMediatorDid);

        await f.HandleAsync(f.SelfAddressed(layer2));

        f.Sink.Count.Should().Be(0);
        f.Registry.Requested.Should().Equal(Mediator);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnknown] = 1 });
    }

    [Theory]
    [InlineData("DID:example:m")]
    [InlineData("did:example:M")]
    [InlineData("did:example:%6D")]
    [InlineData("did:example:m:")]
    [InlineData(" did:example:m")]
    public async Task A_next_that_only_resembles_the_decrypting_DID_is_never_peeled(string lookalike)
    {
        // The inline self-address check compares DID subjects ordinally: case, percent-encoding and
        // padding variants are other values (or no DID), looked up like any next, never opened.
        await using var f = await Fixture.CreateAsync();

        await f.HandleAsync(f.SelfAddressed(await f.AnoncryptAsync(Forward(Bob)), next: lookalike));

        f.Sink.Count.Should().Be(0);
        f.Registry.Requested.Should().NotContain(Bob, "the layer was never opened");
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedUnknown] = 1 });
    }

    // ── Hostile layers: every shape ends in one typed drop ────────────────────────────────

    public enum Shape
    {
        LinksOnly,
        Base64Whitespace,
        Base64NotBase64,
        Base64NonUtf8,
        Base64DeepJsonArray,
        JsonNotAnEnvelope,
        JsonEmptyObject,
        JweHeaderNotJson,
        JwsOverLongKid,
        TwoAttachments,
        NoAttachmentData,
        InnerNextUnpairedSurrogate,
        InnerNextOverCap,
        InnerNextEmpty,
        InnerNextNumber,
        InnerTwoAttachments,
        InnerPlaintext,
        InnerSignedOnly,
        InnerDelayExtreme,
        InnerExpiresMinValue,
    }

    [Theory]
    [InlineData(Shape.LinksOnly, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.Base64Whitespace, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.Base64NotBase64, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.Base64NonUtf8, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.Base64DeepJsonArray, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.JsonNotAnEnvelope, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.JsonEmptyObject, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.JweHeaderNotJson, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.JwsOverLongKid, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.TwoAttachments, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.NoAttachmentData, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.InnerNextUnpairedSurrogate, null)]
    [InlineData(Shape.InnerNextOverCap, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.InnerNextEmpty, null)]
    [InlineData(Shape.InnerNextNumber, null)]
    [InlineData(Shape.InnerTwoAttachments, ForwardOutcomes.DroppedMalformed)]
    [InlineData(Shape.InnerPlaintext, ForwardOutcomes.DroppedNotEncrypted)]
    [InlineData(Shape.InnerSignedOnly, ForwardOutcomes.DroppedNotEncrypted)]
    [InlineData(Shape.InnerDelayExtreme, "sink")]
    [InlineData(Shape.InnerExpiresMinValue, ForwardOutcomes.DroppedExpired)]
    public async Task Each_hostile_shape_of_a_self_addressed_layer_ends_in_one_typed_outcome_never_a_fault(Shape shape, string? expected)
    {
        await using var f = await Fixture.CreateAsync();
        var received = await f.HostileAsync(shape);

        // HandleAsync never throws and always answers null; the pump turns the layer into one outcome.
        await f.HandleAsync(received);

        f.Handler.Outstanding.Should().Be(0);
        f.Handler.OutstandingBytes.Should().Be(0);
        f.Outcomes.Counts.Keys.Should().NotContain(ForwardOutcomes.Faulted, "a hostile layer is the sender's mistake, never the mediator's fault ({0})", shape);
        f.Logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
        if (expected == "sink")
        {
            f.Sink.Count.Should().Be(1);
            f.Outcomes.Total.Should().Be(0);
        }
        else if (expected is null)
        {
            // Malformed or unknown are both a sender-caused drop; which one depends on where the
            // value is refused (the JSON reader, the forward parser or the DID parser).
            f.Sink.Count.Should().Be(0);
            f.Outcomes.Total.Should().Be(1);
            f.Outcomes.Counts.Keys.Single().Should().BeOneOf(ForwardOutcomes.DroppedMalformed, ForwardOutcomes.DroppedUnknown);
        }
        else
        {
            f.Sink.Count.Should().Be(0);
            f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [expected] = 1 });
        }
        ForwardHandlerTests.AssertLogsCarryNoExceptionObjectAndNone(f.Logger, Bob, Fixture.MediatorKid, Tarpit, new string('k', 64));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(ForwardHandler.MaxSelfAddressedLayers)]
    public async Task A_nesting_malformed_at_layer_k_ends_as_malformed_and_never_reaches_the_registry(int k)
    {
        // k self-addressed layers, the k-th carrying an attachment that is not an envelope.
        await using var f = await Fixture.CreateAsync();
        string packed = "{\"not\":\"an envelope\"}";
        for (var i = 1; i < k; i++)
            packed = await f.AnoncryptAsync(ForwardMessage.Create(Mediator, Mediator, new[] { packed }));

        await f.HandleAsync(f.SelfAddressed(packed));

        f.Sink.Count.Should().Be(0);
        f.Registry.Requested.Should().BeEmpty();
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 1 });
    }

    [Fact]
    public async Task Sixteen_self_addressed_layers_cost_at_most_the_bound_in_decryptions()
    {
        // A nesting made to burn decryptions meets the bound: the mediator opens at most
        // MaxSelfAddressedLayers layers (the received one included, which was opened by the
        // endpoint), so a 16-deep onion costs the pump 8 unpacks, not 16.
        await using var f = await Fixture.CreateAsync();
        var received = await f.NestedAsync(16, Forward(Bob));
        var before = f.Secrets.FindPresentCalls;

        await f.HandleAsync(received);

        (f.Secrets.FindPresentCalls - before).Should().BeLessThanOrEqualTo(ForwardHandler.MaxSelfAddressedLayers);
        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedMalformed] = 1 });
    }

    // ── DEFECT 1: the peel resolves DIDs on the shared pump ───────────────────────────────

    // ADV-1 DEFECT 1 (P2, fixed by #95 AD-1): the layer used to be opened on ForwardHandler's single
    // pump, so a stranger's inner layer naming a slowly-resolving DID parked every relay and, once
    // the queue was full, Bob's forwards were dropped as saturated. Layers addressed to the mediator
    // are now opened inline, on the sender's own request and before any reservation: the stranger's
    // request is what waits for the DID it chose.
    [Fact]
    public async Task ADV1_D1_A_self_addressed_forward_whose_inner_layer_resolves_a_slow_DID_does_not_hold_up_other_recipients_forwards()
    {
        RaiseThreadPoolFloor();
        await using var f = await Fixture.CreateAsync(capacity: 2);
        var poisoned = f.SelfAddressed(await f.SignedByTarpitAsync(Forward(Bob)));

        // The stranger's forward: anoncrypt to the mediator, no grant, no registered recipient
        // needed — opening the inner layer resolves the signer before anything else is checked.
        // Its own request thread (as the endpoint gives every request one): the signer lookup is
        // sync-over-async, so the call blocks the thread that makes it.
        var strangers = Task.Run(() => f.HandleAsync(poisoned, flush: false));
        (await Task.WhenAny(f.Resolver.Entered.Task, Task.Delay(Rendezvous))).Should().BeSameAs(f.Resolver.Entered.Task, "the stranger's DID is being resolved");

        // Alice's forwards for Bob arrive while that resolution is parked.
        var forBob1 = AsDecryptedByMediator(Forward(Bob));
        var forBob2 = AsDecryptedByMediator(Forward(Bob));
        await f.HandleAsync(forBob1, flush: false);
        await f.HandleAsync(forBob2, flush: false);

        try
        {
            f.Outcomes.Counts.Should().NotContainKey(ForwardOutcomes.DroppedSaturated, "Bob's mail must not be dropped because a stranger's onion is being resolved");
            (await PollAsync(() => f.Sink.Count >= 2, TimeSpan.FromSeconds(5))).Should().BeTrue("Bob's forwards are relayed while the stranger's DID resolution is still pending");
            strangers.IsCompleted.Should().BeFalse("the stranger's own request pays for opening what it sent");
            f.Handler.Outstanding.Should().Be(0, "nothing is reserved for a layer still being opened");
        }
        finally
        {
            f.Resolver.Gate.TrySetResult();
        }

        await strangers.WaitAsync(Flush);
        await f.Handler.FlushAsync(Flush);
        f.Outcomes.Counts.Should().Equal(new Dictionary<string, long> { [ForwardOutcomes.DroppedNotEncrypted] = 1 }, "once resolved, the signed-only inner layer is refused by the posture rule");
    }

    // ── DEFECT 4: a stranger can make the peel count Faulted ──────────────────────────────

    [Fact]
    public async Task ADV1_D4_An_inner_layer_signed_by_a_DID_that_does_not_resolve_is_a_sender_caused_drop_not_a_fault()
    {
        await using var f = await Fixture.CreateAsync();

        await f.HandleAsync(f.SelfAddressed(await f.SignedByGhostAsync(Forward(Bob))));

        f.Sink.Count.Should().Be(0);
        f.Outcomes.Counts.Keys.Should().NotContain(ForwardOutcomes.Faulted);
        f.Outcomes.Total.Should().Be(1);
    }

    // ── DEFECT 2: an outer layer's expiry is lost once the layer is opened ────────────────

    [Fact]
    public async Task ADV1_D2_The_expiry_of_an_opened_layer_still_bounds_the_relayed_payload()
    {
        await using var f = await Fixture.CreateAsync();
        var outerExpiry = T0.AddMinutes(1).ToUnixTimeSeconds();
        var outer = ForwardMessage.Create(Mediator, Mediator, new[] { await f.AnoncryptAsync(Forward(Bob)) }, expiresTimeEpochSeconds: outerExpiry);

        await f.HandleAsync(AsDecryptedByMediator(outer, Fixture.MediatorKid));

        var result = f.Sink.Results.Should().ContainSingle().Which;
        result.ExpiresTime.Should().NotBeNull("the opened layer asked for an expiry");
        result.ExpiresTime!.Value.Should().BeLessThanOrEqualTo(outerExpiry, "the relayed payload must not outlive the forward that enclosed it");
    }

    // ── DEFECT 3: the forward handler's log lines carry the id unbounded ──────────────────

    [Fact]
    public async Task ADV1_D3_A_forward_handler_drop_line_never_carries_more_than_a_bounded_id()
    {
        await using var f = await Fixture.CreateAsync();
        // A peel drop (the new lines) and an inline drop (the existing ones), each with a long id.
        var peeled = ForwardMessage.Create(Mediator, Mediator, new[] { "{\"not\":\"an envelope\"}" });
        peeled.Id = new string('a', 200_000);
        var unknown = Forward("did:example:nobody");
        unknown.Id = new string('b', 200_000);

        await f.HandleAsync(AsDecryptedByMediator(peeled, Fixture.MediatorKid));
        await f.HandleAsync(AsDecryptedByMediator(unknown, Fixture.MediatorKid));

        f.Logger.Warnings.Should().HaveCount(2);
        f.Logger.Entries.Should().OnlyContain(e => e.Formatted.Length < 4_096, "a stranger-chosen id is bounded before it is written (MediatorLog.LogId keeps 128 characters)");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static Message Forward(string next, long? expires = null) => ForwardMessage.Create(Mediator, next, new[] { Inner }, expiresTimeEpochSeconds: expires);

    /// <summary>
    /// The signer lookup is sync-over-async (the JWS verifier's contract), so a parked resolution
    /// holds a thread-pool thread: raise the floor so the rest of the test never waits for injection.
    /// </summary>
    private static void RaiseThreadPoolFloor(int minimum = 64)
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        if (worker < minimum || io < minimum)
            ThreadPool.SetMinThreads(Math.Max(worker, minimum), Math.Max(io, minimum));
    }

    private static async Task<bool> PollAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>A DID resolver that parks every resolution of <see cref="Tarpit"/> until <see cref="Gate"/> opens (a did:webvh / did:web host that answers slowly).</summary>
    private sealed class GatedDidResolver : IDidResolver
    {
        private readonly Dictionary<string, DidDocument> _docs;
        private readonly ConcurrentQueue<string> _requested = new();

        public GatedDidResolver(params DidDocument[] docs)
            => _docs = docs.ToDictionary(d => d.Id.Value, StringComparer.Ordinal);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<string> Requested => _requested;

        public bool CanResolve(string did) => true;

        public async Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            _requested.Enqueue(did);
            if (string.Equals(did, Tarpit, StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            return _docs.TryGetValue(did, out var doc)
                ? new DidResolutionResult { DidDocument = doc, ResolutionMetadata = new DidResolutionMetadata() }
                : DidResolutionResult.NotFound(did);
        }
    }

    /// <summary>The mediator (keys for two DIDs), a registry with Bob, a capturing sink, and a sender that can sign as the tarpit DID.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public const string MediatorKid = Mediator + "#ka-1";
        public const string TarpitKid = Tarpit + "#auth-1";
        public const string GhostDid = "did:example:ghost";

        private readonly TestMeterFactory _meters;
        private DateTimeOffset _now = T0;

        private Fixture(TestMeterFactory meters) => _meters = meters;

        public DidCommClient Client { get; private set; } = null!;
        public DidCommClient TarpitSigner { get; private set; } = null!;
        public DidCommOptions Options { get; private set; } = null!;
        public ForwardHandler Handler { get; private set; } = null!;
        public ForwardHandlerTests.CountingRegistry Registry { get; private set; } = null!;
        public ForwardHandlerTests.CapturingSink Sink { get; } = new();
        public ForwardHandlerTests.CapturingLogger Logger { get; } = new();
        public ForwardOutcomeRecorder Outcomes { get; private set; } = null!;
        public GatedDidResolver Resolver { get; private set; } = null!;
        public SpySecretsResolver Secrets { get; private set; } = null!;

        public static Task<Fixture> CreateAsync(int capacity = ForwardHandler.DefaultCapacity)
        {
            var f = new Fixture(new TestMeterFactory());
            f.Options = new DidCommOptions { Clock = () => f._now };
            var key1 = TestKeyMaterial.Generate(KeyType.X25519, MediatorKid);
            var key2 = TestKeyMaterial.Generate(KeyType.X25519, OtherMediatorDid + "#ka-1");
            var tarpitAuth = TestKeyMaterial.Generate(KeyType.Ed25519, TarpitKid);
            var docs = new[]
            {
                LengthCap.Doc(Mediator, keyAgreement: key1.PublicJwk),
                LengthCap.Doc(OtherMediatorDid, keyAgreement: key2.PublicJwk),
            };
            f.Resolver = new GatedDidResolver(docs.Append(LengthCap.Doc(Tarpit, authentication: tarpitAuth.PublicJwk)).ToArray());
            var keyService = new NetDidKeyService(f.Resolver);
            f.Secrets = new SpySecretsResolver(key1.PrivateJwk, key2.PrivateJwk);
            f.Client = new DidCommClient(f.Secrets, keyService, f.Options);

            // The stranger's own client: it resolves its own DID without delay.
            var ghostAuth = TestKeyMaterial.Generate(KeyType.Ed25519, GhostDid + "#auth-1");
            var signerDocs = new SpyDidResolver(docs
                .Append(LengthCap.Doc(Tarpit, authentication: tarpitAuth.PublicJwk))
                .Append(LengthCap.Doc(GhostDid, authentication: ghostAuth.PublicJwk)).ToArray());
            f.TarpitSigner = new DidCommClient(new SpySecretsResolver(tarpitAuth.PrivateJwk, ghostAuth.PrivateJwk), new NetDidKeyService(signerDocs), f.Options);

            var processor = new ForwardProcessor(f.Client, keyService, new ForwardProcessorOptions(), new StaticRecipientRoutingKeyProvider(null), f.Options);
            var recipients = new InMemoryForwardRecipientRegistry();
            recipients.Register(Bob);
            f.Registry = new ForwardHandlerTests.CountingRegistry(recipients);
            f.Outcomes = ForwardOutcomeRecorder.For(f._meters);
            f.Handler = new ForwardHandler(processor, f.Sink, f.Registry, f.Logger, capacity, shutdownGrace: TimeSpan.FromSeconds(2), ForwardHandler.DefaultByteBudget, new ForwardMetrics(f._meters));
            return Task.FromResult(f);
        }

        public async Task<string> AnoncryptAsync(Message message, string to = Mediator)
            => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }))).Message;

        /// <summary>A JWS of <paramref name="message"/> signed by the tarpit DID (whose resolution the mediator's resolver parks).</summary>
        public Task<string> SignedByTarpitAsync(Message message) => TarpitSigner.PackSignedAsync(message, Tarpit);

        /// <summary>A JWS signed by a DID only the stranger's own resolver knows (the mediator's does not resolve it).</summary>
        public Task<string> SignedByGhostAsync(Message message) => TarpitSigner.PackSignedAsync(message, GhostDid);

        public UnpackResult SelfAddressed(string packedInside, string next = Mediator)
            => AsDecryptedByMediator(ForwardMessage.Create(Mediator, next, new[] { packedInside }), MediatorKid);

        public async Task<UnpackResult> NestedAsync(int layers, Message innermost)
        {
            var packed = await AnoncryptAsync(innermost);
            for (var i = 1; i < layers; i++)
                packed = await AnoncryptAsync(ForwardMessage.Create(Mediator, Mediator, new[] { packed }));
            return SelfAddressed(packed);
        }

        public async Task<UnpackResult> HostileAsync(Shape shape)
        {
            static Message WithData(Message forward, AttachmentData data)
            {
                forward.Attachments![0].Data = data;
                return forward;
            }

            Message Outer(Action<Message> shapeIt)
            {
                var outer = ForwardMessage.Create(Mediator, Mediator, new[] { Inner });
                shapeIt(outer);
                return outer;
            }

            async Task<UnpackResult> InnerForward(Action<Message> shapeIt)
            {
                var inner = Forward(Bob);
                shapeIt(inner);
                return SelfAddressed(await AnoncryptAsync(inner));
            }

            static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            switch (shape)
            {
                case Shape.LinksOnly:
                    return AsDecryptedByMediator(Outer(o => WithData(o, new AttachmentData { Links = new[] { "https://evil.example/x" }, Hash = "abc" })), MediatorKid);
                case Shape.Base64Whitespace:
                    return AsDecryptedByMediator(Outer(o => WithData(o, new AttachmentData { Base64 = "   \t\n  " })), MediatorKid);
                case Shape.Base64NotBase64:
                    return AsDecryptedByMediator(Outer(o => WithData(o, new AttachmentData { Base64 = "!!!*" })), MediatorKid);
                case Shape.Base64NonUtf8:
                    return AsDecryptedByMediator(Outer(o => WithData(o, new AttachmentData { Base64 = B64(new byte[] { 0xFF, 0xFE, 0xC0, 0x80, 0xED, 0xA0, 0x80 }) })), MediatorKid);
                case Shape.Base64DeepJsonArray:
                    return AsDecryptedByMediator(Outer(o => WithData(o, new AttachmentData { Base64 = B64(Encoding.UTF8.GetBytes(new string('[', 5000) + new string(']', 5000))) })), MediatorKid);
                case Shape.JsonNotAnEnvelope:
                    return AsDecryptedByMediator(Outer(o => WithData(o, new AttachmentData { Json = new JsonObject { ["a"] = 1 } })), MediatorKid);
                case Shape.JsonEmptyObject:
                    return AsDecryptedByMediator(Outer(o => WithData(o, new AttachmentData { Json = new JsonObject() })), MediatorKid);
                case Shape.JweHeaderNotJson:
                    return SelfAddressed("""{"protected":"bm90IGpzb24","recipients":[{"header":{"kid":"did:example:m#ka-1"},"encrypted_key":"AA"}],"iv":"AA","ciphertext":"AA","tag":"AA"}""");
                case Shape.JwsOverLongKid:
                    return SelfAddressed("{\"payload\":\"e30\",\"signatures\":[{\"protected\":\"eyJhbGciOiJFZERTQSJ9\",\"header\":{\"kid\":\"" + LengthCap.KidOfLength(Tarpit, LengthCap.KeyIdCap + 1) + "\"},\"signature\":\"AA\"}]}");
                case Shape.TwoAttachments:
                    return AsDecryptedByMediator(ForwardMessage.Create(Mediator, Mediator, new[] { Inner, Inner }), MediatorKid);
                case Shape.NoAttachmentData:
                    return AsDecryptedByMediator(Outer(o => WithData(o, new AttachmentData())), MediatorKid);
                case Shape.InnerNextUnpairedSurrogate:
                    return await InnerForward(m => m.Body!["next"] = Bob + "\uD800");
                case Shape.InnerNextOverCap:
                    return await InnerForward(m => m.Body!["next"] = LengthCap.KidOfLength(Bob, LengthCap.KeyIdCap + 1));
                case Shape.InnerNextEmpty:
                    return await InnerForward(m => m.Body!["next"] = "");
                case Shape.InnerNextNumber:
                    return await InnerForward(m => m.Body!["next"] = 42);
                case Shape.InnerTwoAttachments:
                    return SelfAddressed(await AnoncryptAsync(ForwardMessage.Create(Mediator, Bob, new[] { Inner, Inner })));
                case Shape.InnerPlaintext:
                    return SelfAddressed(await Client.PackPlaintextAsync(Forward(Bob)));
                case Shape.InnerSignedOnly:
                    return await SignedOnlyWithoutTarpitAsync();
                case Shape.InnerDelayExtreme:
                    return await InnerForward(m => m.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["delay_milli"] = JsonDocument.Parse(long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
                    });
                case Shape.InnerExpiresMinValue:
                    return await InnerForward(m => m.ExpiresTime = long.MinValue);
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }

        // A signed-only inner whose signer resolves at once: the gate is opened first, so the pump
        // is not parked and the posture rule is what refuses it.
        private async Task<UnpackResult> SignedOnlyWithoutTarpitAsync()
        {
            Resolver.Gate.TrySetResult();
            return SelfAddressed(await TarpitSigner.PackSignedAsync(Forward(Bob), Tarpit));
        }

        public async Task HandleAsync(UnpackResult received, bool flush = true)
        {
            var context = new ProtocolContext(received, new DidComm.Threading.ThreadState(received.Message.Id), Client: null, Options, new DidComm.Threading.InMemoryThreadStateStore())
            {
                Acknowledged = Task.CompletedTask,
            };
            (await Handler.HandleAsync(received.Message, context, CancellationToken.None)).Should().BeNull();
            if (flush)
                await Handler.FlushAsync(Flush);
        }

        public async ValueTask DisposeAsync()
        {
            Resolver.Gate.TrySetResult();
            await Handler.DisposeAsync();
            Outcomes.Dispose();
            _meters.Dispose();
        }
    }
}
