using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NetCrypto;
using Xunit;
using CapturingLogger = DidComm.Tests.Facade.AllRecipientKeysTests.CapturingLogger;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;
using StubServiceResolver = DidComm.Tests.Facade.AllRecipientKeysTests.StubServiceResolver;

namespace DidComm.Tests.Transports;

/// <summary>
/// #92 QA pass — <see cref="DidCommClient.SendAsync"/> under every combination the G2 suite samples:
/// <see cref="SendOptions.ReturnRoute"/> (null / none / all / thread) × a builder-set header (absent /
/// none / all / thread / malformed string / non-string) × the envelope (authcrypt, anoncrypt,
/// <c>ProtectSender</c>, <c>SignFrom</c> with and without a sender) — checking the header on the wire,
/// <c>created_time</c>, the request's return-route fields and that the caller's message is untouched;
/// then the route (override, direct, three routed shapes, the queue URI) × mode × sender for the
/// refusal and its order; then the <see cref="SendResult.Returned"/> edges (accepted envelope shapes,
/// expiry, cancellation, a discarded body on a send that asked for nothing). Every case scans the
/// facade's log for DIDs.
/// </summary>
public sealed class ReturnRouteClientQaTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string Carol = "did:example:carol";
    private const string Mallory = "did:example:mallory";
    private const string Mediator = "did:example:mediator";
    private const string BobInbox = "https://bob.example/inbox";
    private const string MediatorInbox = "https://mediator.example/inbox";
    private const string AppType = "https://example.com/protocols/qa/1.0/note";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    // ---- SendOptions.ReturnRoute × builder header × envelope ----

    private static readonly string?[] Modes = { null, "none", "all", "thread" };
    private static readonly string[] BuilderHeaders = { "absent", "none", "all", "thread", "malformed", "non-string" };
    private static readonly string[] Senders = { "authcrypt", "anoncrypt", "authcrypt+protect", "authcrypt+sign", "anoncrypt+sign" };

    public static TheoryData<string, string, string> HeaderPermutations()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var mode in Modes)
            foreach (var header in BuilderHeaders)
                foreach (var sender in Senders)
                    data.Add(mode ?? "null", header, sender);
        return data;
    }

    [Theory]
    [MemberData(nameof(HeaderPermutations))]
    public async Task Header_created_time_and_request_fields_follow_the_effective_header(string mode, string builderHeader, string sender)
    {
        var world = new World();
        var log = new CapturingLogger();
        var message = BuilderWith(builderHeader, authcrypt: sender.StartsWith("authcrypt", StringComparison.Ordinal));
        var before = JsonSerializer.Serialize(message, DidCommJson.Default);
        var requested = Mode(mode);
        var options = OptionsFor(sender) with
        {
            ReturnRoute = requested,
            ReturnRouteThread = requested == ReturnRouteMode.Thread ? "thr-qa" : null,
        };

        await world.Client(log).SendAsync(message, options);

        // What the header on the wire must be: the SendOptions value when set, the builder's otherwise.
        ReturnRouteMode? expected = requested ?? builderHeader switch
        {
            "absent" => null,
            "all" => ReturnRouteMode.All,
            "thread" => ReturnRouteMode.Thread,
            _ => ReturnRouteMode.None, // none, and both malformed shapes, which the receiver reads as none
        };
        var asks = expected is ReturnRouteMode.All or ReturnRouteMode.Thread;
        var authcrypt = options.From is not null;
        var label = $"{mode} × {builderHeader} × {sender}";

        var sent = (await world.DecryptSentAsync()).Message;
        var request = world.Router.Requests.Single();
        request.ReturnRoute.Should().Be(expected, label);
        request.LocalDid.Should().Be(authcrypt ? Alice : null, label);
        request.PeerDid.Should().Be(authcrypt ? Bob : null, label);
        request.MaxResponseBytes.Should().Be(asks && authcrypt ? world.Options.MaxReceiveBytes : null, label);
        sent.CreatedTime.Should().Be(asks ? Now.ToUnixTimeSeconds() : null, label);
        sent.Id.Should().Be(message.Id, label);

        var headers = sent.AdditionalHeaders ?? new Dictionary<string, JsonElement>();
        if (requested is { } r)
        {
            headers[ReturnRouteHeader.HeaderName].GetString().Should().Be(r.ToString().ToLowerInvariant(), label);
            headers.ContainsKey(ReturnRouteHeader.ThreadHeaderName).Should().Be(r == ReturnRouteMode.Thread, label);
            if (r == ReturnRouteMode.Thread)
                headers[ReturnRouteHeader.ThreadHeaderName].GetString().Should().Be("thr-qa", label);
        }
        else
        {
            // Untouched: byte-for-byte what the builder set, malformed values included.
            headers.ContainsKey(ReturnRouteHeader.HeaderName).Should().Be(builderHeader != "absent", label);
            if (builderHeader != "absent")
                headers[ReturnRouteHeader.HeaderName].GetRawText().Should().Be(message.AdditionalHeaders![ReturnRouteHeader.HeaderName].GetRawText(), label);
        }

        JsonSerializer.Serialize(message, DidCommJson.Default).Should().Be(before, "the caller's message is never modified ({0})", label);
        log.Entries.Should().NotContain(e => e.Message.Contains("did:", StringComparison.Ordinal), label);
    }

    // ---- route × mode × sender: the refusal and its order ----

    private static readonly string[] Routes = { "override", "direct uri", "DID URL routing key", "bare DID routing key", "DID as uri", "queue uri" };

    public static TheoryData<string, string, bool> RoutePermutations()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var route in Routes)
            foreach (var mode in Modes)
                foreach (var authcrypt in new[] { true, false })
                    data.Add(route, mode ?? "null", authcrypt);
        return data;
    }

    [Theory]
    [MemberData(nameof(RoutePermutations))]
    public async Task Route_mode_and_sender_decide_refusal_endpoint_and_identities(string route, string mode, bool authcrypt)
    {
        var service = route switch
        {
            "DID URL routing key" => Info(MediatorInbox, Mediator + "#m1"),
            "bare DID routing key" => Info(MediatorInbox, Mediator),
            "DID as uri" => Info(Mediator),
            "queue uri" => Info(QueueTransport.Uri),
            _ => Info(BobInbox),
        };
        var world = new World(service);
        var log = new CapturingLogger();
        var requested = Mode(mode);
        var asks = requested is ReturnRouteMode.All or ReturnRouteMode.Thread;
        var routed = route is "DID URL routing key" or "bare DID routing key" or "DID as uri";
        var options = new SendOptions(
            Recipients: new[] { Bob },
            From: authcrypt ? Alice : null,
            ServiceEndpointOverride: route == "override" ? new Uri(BobInbox) : null)
        {
            ReturnRoute = requested,
            ReturnRouteThread = requested == ReturnRouteMode.Thread ? "thr-qa" : null,
        };
        var label = $"{route} × {mode} × {(authcrypt ? "authcrypt" : "anoncrypt")}";

        var act = () => world.Client(log).SendAsync(AppMessage(authcrypt), options);

        if (route == "queue uri")
        {
            (await act.Should().ThrowAsync<TransportException>(label)).Which.Message.Should().Be(QueueTransport.RefusalMessage);
            world.Router.Requests.Should().BeEmpty(label);
        }
        else if (routed && asks)
        {
            (await act.Should().ThrowAsync<InvalidOperationException>(label)).Which.Message.Should().Be(DidCommClient.RoutedReturnRouteRefusal);
            world.Services.Resolved.Should().Equal(new[] { Bob }, label);
            world.Resolver.Requested.Should().OnlyContain(d => d == Bob, "no mediator, routing key or sender was resolved ({0})", label);
            world.AliceSecrets.FindPresentCalls.Should().Be(0, label);
            world.Router.Requests.Should().BeEmpty(label);
        }
        else
        {
            var result = await act();
            var request = world.Router.Requests.Single();
            var direct = !routed;
            request.Endpoint.Should().Be(new Uri(routed ? MediatorInbox : BobInbox), label);
            result.EndpointUsed.Should().Be(request.Endpoint, label);
            request.ReturnRoute.Should().Be(requested, label);
            request.LocalDid.Should().Be(direct && authcrypt ? Alice : null, label);
            request.PeerDid.Should().Be(direct && authcrypt ? Bob : null, label);
            request.MaxResponseBytes.Should().Be(direct && authcrypt && asks ? world.Options.MaxReceiveBytes : null, label);
            world.Services.Resolved.Count(d => d == Bob).Should().Be(route == "override" ? 0 : 1, "the service entry is read at most once ({0})", label);
            result.Returned.Should().BeNull(label);
        }
        log.Entries.Should().NotContain(e => e.Message.Contains("did:", StringComparison.Ordinal), label);
    }

    [Fact]
    public async Task The_queue_override_is_refused_before_the_two_DID_check_and_the_thread_shape_before_both()
    {
        // Pinned order (plan B2): shape checks → queue override → JC-4 → pack.
        var world = new World();
        var queue = new Uri(QueueTransport.Uri);

        var shape = () => world.Client().SendAsync(AppMessage(true), new SendOptions(Recipients: new[] { Bob, Carol }, From: Alice, ServiceEndpointOverride: queue)
        {
            ReturnRoute = ReturnRouteMode.Thread,
        });
        var queued = () => world.Client().SendAsync(AppMessage(true), new SendOptions(Recipients: new[] { Bob, Carol }, From: Alice, ServiceEndpointOverride: queue)
        {
            ReturnRoute = ReturnRouteMode.All,
        });
        var twoDids = () => world.Client().SendAsync(AppMessage(true), new SendOptions(Recipients: new[] { Bob, Carol }, From: Alice)
        {
            ReturnRoute = ReturnRouteMode.All,
        });

        await shape.Should().ThrowAsync<ArgumentException>();
        (await queued.Should().ThrowAsync<TransportException>()).Which.Message.Should().Be(QueueTransport.RefusalMessage);
        (await twoDids.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("exactly one DID");
        world.Resolver.Requested.Should().BeEmpty();
        world.Services.Resolved.Should().BeEmpty();
        world.Router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Entries_pinning_keys_of_one_DID_may_ask_for_a_return_route()
    {
        var world = new World();

        await world.Client().SendAsync(AppMessage(true), new SendOptions(Recipients: new[] { Bob, Bob + "#x1" }, From: Alice)
        {
            ReturnRoute = ReturnRouteMode.All,
        });

        var request = world.Router.Requests.Single();
        request.PeerDid.Should().Be(Bob);
        request.LocalDid.Should().Be(Alice);
        request.MaxResponseBytes.Should().Be(world.Options.MaxReceiveBytes);
    }

    // ---- Returned ----

    public static TheoryData<string> AcceptedReplyShapes() => new() { "authcrypt", "anoncrypt(authcrypt)", "authcrypt(sign by the peer)" };

    [Theory]
    [MemberData(nameof(AcceptedReplyShapes))]
    public async Task Returned_accepts_every_authcrypt_shape_from_the_addressed_peer(string shape)
    {
        var world = new World();
        world.Respond = async request => Returning(await world.PackReplyAsync(request, protect: shape == "anoncrypt(authcrypt)", signer: shape.Contains("sign", StringComparison.Ordinal) ? Bob : null));
        var message = AppMessage(true);

        var result = await world.Client().SendAsync(message, Direct() with { ReturnRoute = ReturnRouteMode.All });

        result.Returned.Should().NotBeNull(shape);
        result.Returned!.Message.Thid.Should().Be(message.Id);
        result.Returned.SenderKid.Should().StartWith(Bob + "#");
    }

    [Fact]
    public async Task Returned_authcrypt_by_the_peer_signed_by_another_DID_fails_unpack_as_TransportException_200()
    {
        // Unpack itself refuses the signer / skid mismatch (FR-CONSIST-05) before the identity rule runs.
        var world = new World();
        world.Respond = async request => Returning(await world.PackReplyAsync(request, protect: false, signer: Mallory));
        var log = new CapturingLogger();

        var act = () => world.Client(log).SendAsync(AppMessage(true), Direct() with { ReturnRoute = ReturnRouteMode.All });

        var thrown = (await act.Should().ThrowAsync<TransportException>()).Which;
        thrown.HttpStatusCode.Should().Be(200);
        thrown.InnerException.Should().BeOfType<ConsistencyException>();
        thrown.Message.Should().NotContain("did:");
        log.Entries.Should().NotContain(e => e.Message.Contains("did:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_expired_returned_message_fails_the_send_with_TransportException_200()
    {
        var world = new World();
        world.Respond = async request => Returning(await world.PackReplyAsync(request, protect: false, signer: null, expires: Now.AddMinutes(-1).ToUnixTimeSeconds()));

        var act = () => world.Client().SendAsync(AppMessage(true), Direct() with { ReturnRoute = ReturnRouteMode.All });

        var thrown = (await act.Should().ThrowAsync<TransportException>()).Which;
        thrown.HttpStatusCode.Should().Be(200);
        thrown.InnerException.Should().BeAssignableTo<DidCommException>();
        thrown.Message.Should().NotContain("did:");
    }

    [Fact]
    public async Task Caller_cancellation_while_the_returned_message_is_unpacked_propagates_as_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var world = new World();
        world.Respond = async request =>
        {
            var reply = Returning(await world.PackReplyAsync(request, protect: false, signer: null));
            cts.Cancel(); // the caller gives up after the response arrived, before it is unpacked
            return reply;
        };

        var act = () => world.Client().SendAsync(AppMessage(true), Direct() with { ReturnRoute = ReturnRouteMode.All }, cts.Token);

        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;
        thrown.Should().NotBeOfType<TransportException>();
    }

    public static TheoryData<string> SendsThatAskedForNothing() => new() { "header-less authcrypt", "none", "anoncrypt all" };

    [Theory]
    [MemberData(nameof(SendsThatAskedForNothing))]
    public async Task A_discarded_or_garbage_body_is_ignored_when_the_send_asked_for_nothing_it_can_verify(string @case)
    {
        var discarded = new World { Respond = _ => Task.FromResult(new TransportResult(true, 200) { ResponseDiscarded = true }) };
        var garbage = new World { Respond = _ => Task.FromResult(Returning("{")) };
        SendOptions Options() => @case switch
        {
            "header-less authcrypt" => Direct(),
            "none" => Direct() with { ReturnRoute = ReturnRouteMode.None },
            _ => new SendOptions(Recipients: new[] { Bob }, ServiceEndpointOverride: new Uri(BobInbox)) { ReturnRoute = ReturnRouteMode.All },
        };
        var authcrypt = @case != "anoncrypt all";

        var a = await discarded.Client().SendAsync(AppMessage(authcrypt), Options());
        var b = await garbage.Client().SendAsync(AppMessage(authcrypt), Options());

        a.Returned.Should().BeNull(@case);
        b.Returned.Should().BeNull(@case);
        a.Transport.ResponseDiscarded.Should().BeTrue("the transport's result is passed through unchanged");
    }

    // ---- helpers ----

    private static ReturnRouteMode? Mode(string? mode) => mode switch
    {
        null or "null" => null,
        "none" => ReturnRouteMode.None,
        "all" => ReturnRouteMode.All,
        "thread" => ReturnRouteMode.Thread,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static SendOptions OptionsFor(string sender) => sender switch
    {
        "authcrypt" => Direct(),
        "anoncrypt" => new SendOptions(Recipients: new[] { Bob }, ServiceEndpointOverride: new Uri(BobInbox)),
        "authcrypt+protect" => Direct() with { ProtectSender = true },
        "authcrypt+sign" => Direct() with { SignFrom = Alice },
        "anoncrypt+sign" => new SendOptions(Recipients: new[] { Bob }, SignFrom: Alice, ServiceEndpointOverride: new Uri(BobInbox)),
        _ => throw new ArgumentOutOfRangeException(nameof(sender)),
    };

    private static Message BuilderWith(string header, bool authcrypt)
    {
        var builder = AppBuilder(authcrypt);
        switch (header)
        {
            case "none":
                return builder.WithReturnRoute(ReturnRouteMode.None).Build();
            case "all":
                return builder.WithReturnRoute(ReturnRouteMode.All).Build();
            case "thread":
                return builder.WithReturnRoute(ReturnRouteMode.Thread, "thr-builder").Build();
            case "malformed":
            case "non-string":
                var message = builder.Build();
                message.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [ReturnRouteHeader.HeaderName] = header == "malformed" ? JsonSerializer.SerializeToElement("ALL") : JsonSerializer.SerializeToElement(1),
                };
                return message;
            default:
                return builder.Build();
        }
    }

    private static SendOptions Direct() => new(Recipients: new[] { Bob }, From: Alice, ServiceEndpointOverride: new Uri(BobInbox));

    private static MessageBuilder AppBuilder(bool authcrypt)
    {
        var builder = new MessageBuilder().WithType(AppType).WithTo(Bob).WithBody(new JsonObject { ["v"] = 1 });
        return authcrypt ? builder.WithFrom(Alice) : builder;
    }

    private static Message AppMessage(bool authcrypt) => AppBuilder(authcrypt).Build();

    private static DidCommServiceInfo Info(string uri, params string[] routing) => new(uri, routing, Array.Empty<string>());

    private static TransportResult Returning(string body) => new(true, 200)
    {
        ResponsePayload = Encoding.UTF8.GetBytes(body),
        ResponseMediaType = MediaTypes.Encrypted,
    };

    /// <summary>Alice (with a signing key), Bob, Carol, Mallory (with a signing key) and a mediator, behind one spy resolver.</summary>
    private sealed class World
    {
        public TestKeyMaterial AliceKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Alice + "#x1");
        public TestKeyMaterial AliceSigningKey { get; } = TestKeyMaterial.Generate(KeyType.Ed25519, Alice + "#s1");
        public TestKeyMaterial BobKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Bob + "#x1");
        public TestKeyMaterial BobSigningKey { get; } = TestKeyMaterial.Generate(KeyType.Ed25519, Bob + "#s1");
        public TestKeyMaterial CarolKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Carol + "#x1");
        public TestKeyMaterial MalloryKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Mallory + "#x1");
        public TestKeyMaterial MallorySigningKey { get; } = TestKeyMaterial.Generate(KeyType.Ed25519, Mallory + "#s1");
        public TestKeyMaterial MediatorKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Mediator + "#m1");

        public World(DidCommServiceInfo? bobService = null)
        {
            Resolver = new SpyDidResolver(Docs());
            AliceSecrets = new SpySecretsResolver(AliceKey.PrivateJwk, AliceSigningKey.PrivateJwk, CarolKey.PrivateJwk);
            Services = new StubServiceResolver(
                (Bob, new[] { bobService ?? Info(BobInbox) }),
                (Mediator, new[] { Info(MediatorInbox) }));
        }

        public SpyDidResolver Resolver { get; }

        public SpySecretsResolver AliceSecrets { get; }

        public StubServiceResolver Services { get; }

        public CapturingRouter Router { get; } = new();

        public Func<TransportRequest, Task<TransportResult>> Respond
        {
            get => Router.Respond;
            set => Router.Respond = value;
        }

        public DidCommOptions Options { get; } = new()
        {
            Clock = () => Now,
            OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false },
        };

        public DidCommClient Client(ILogger<DidCommClient>? logger = null)
            => new(AliceSecrets, new NetDidKeyService(Resolver), Services, Router, Options, new JoseCryptoProvider(), opaqueKeys: null, logger);

        /// <summary>Decrypt the one request sent, as Bob. A routed request is opened by the mediator first.</summary>
        public async Task<UnpackResult> DecryptSentAsync()
        {
            var bob = new DidCommClient(new SpySecretsResolver(BobKey.PrivateJwk), new NetDidKeyService(new SpyDidResolver(Docs())), new DidCommOptions { Clock = () => Now });
            return await bob.UnpackAsync(Encoding.UTF8.GetString(Router.Requests.Single().Payload.Span));
        }

        /// <summary>A reply to the request's message from Bob to Alice, optionally anoncrypt-wrapped or signed by <paramref name="signer"/>.</summary>
        public async Task<string> PackReplyAsync(TransportRequest request, bool protect, string? signer, long? expires = null)
        {
            var bobReader = new DidCommClient(new SpySecretsResolver(BobKey.PrivateJwk), new NetDidKeyService(new SpyDidResolver(Docs())), new DidCommOptions { Clock = () => Now });
            var thid = (await bobReader.UnpackAsync(Encoding.UTF8.GetString(request.Payload.Span))).Message.Id;
            var builder = new MessageBuilder().WithType(AppType + "-response").WithThid(thid).WithFrom(Bob).WithTo(Alice).WithBody(new JsonObject());
            if (expires is long e)
                builder = builder.WithExpiresTime(e);
            var peer = new DidCommClient(
                new SpySecretsResolver(BobKey.PrivateJwk, BobSigningKey.PrivateJwk, MallorySigningKey.PrivateJwk),
                new NetDidKeyService(new SpyDidResolver(Docs())),
                new DidCommOptions { Clock = () => Now });
            var packed = await peer.PackEncryptedAsync(builder.Build(), new PackEncryptedOptions(Recipients: new[] { Alice }, From: Bob, SignFrom: signer, ProtectSender: protect));
            return packed.Message;
        }

        private NetDid.Core.Model.DidDocument[] Docs() => new[]
        {
            LengthCap.DocWithKeys(Alice, new[] { AliceSigningKey.PublicJwk }, new[] { AliceKey.PublicJwk }),
            LengthCap.DocWithKeys(Bob, new[] { BobSigningKey.PublicJwk }, new[] { BobKey.PublicJwk }),
            LengthCap.Doc(Carol, keyAgreement: CarolKey.PublicJwk),
            LengthCap.DocWithKeys(Mallory, new[] { MallorySigningKey.PublicJwk }, new[] { MalloryKey.PublicJwk }),
            LengthCap.Doc(Mediator, keyAgreement: MediatorKey.PublicJwk),
        };
    }

    private sealed class CapturingRouter : ITransportRouter
    {
        public ConcurrentQueue<TransportRequest> Requests { get; } = new();

        public Func<TransportRequest, Task<TransportResult>> Respond { get; set; } = _ => Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            return Respond(request);
        }
    }
}
