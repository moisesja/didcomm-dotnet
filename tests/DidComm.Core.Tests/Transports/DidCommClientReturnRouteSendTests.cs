using System.Collections.Concurrent;
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
/// #92 G2 — the client side of the return-route extension in <see cref="DidCommClient.SendAsync"/>
/// (FR-RR-07, FR-RR-09): the <see cref="SendOptions.ReturnRoute"/> header on a snapshot, the
/// <c>created_time</c> stamping, the refusals and their order, the request's return-route fields,
/// and how an in-band response becomes <see cref="SendResult.Returned"/>. Alice sends to Bob through
/// a capturing router; Bob's own facade decrypts what was sent.
/// </summary>
public sealed class DidCommClientReturnRouteSendTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string Carol = "did:example:carol";
    private const string Mallory = "did:example:mallory";
    private const string Mediator = "did:example:mediator";
    private const string BobInbox = "https://bob.example/inbox";
    private const string MediatorInbox = "https://mediator.example/inbox";
    private const string AppType = "https://example.com/protocols/test/1.0/ping";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    // ---- the header on a snapshot ----

    [Fact]
    public async Task All_stamps_return_route_all_byte_exact_on_a_snapshot()
    {
        var world = new World();
        var message = AppMessage();

        await world.Client().SendAsync(message, Direct() with { ReturnRoute = ReturnRouteMode.All });

        var sent = await world.DecryptSentAsync();
        JsonSerializer.Serialize(sent.Message, DidCommJson.Default).Should().Contain("\"return_route\":\"all\"").And.NotContain("return_route_thread");
        sent.Message.Id.Should().Be(message.Id, "the snapshot is the same message");
        world.Router.Requests.Single().ReturnRoute.Should().Be(ReturnRouteMode.All);
    }

    [Fact]
    public async Task Callers_message_is_not_mutated()
    {
        var world = new World();
        var message = AppMessage();
        var before = JsonSerializer.Serialize(message, DidCommJson.Default);

        await world.Client().SendAsync(message, Direct() with { ReturnRoute = ReturnRouteMode.Thread, ReturnRouteThread = "thr-1" });

        message.AdditionalHeaders.Should().BeNull("the header was written on a copy");
        message.CreatedTime.Should().BeNull("the copy, not the caller's message, was stamped");
        JsonSerializer.Serialize(message, DidCommJson.Default).Should().Be(before);
        (await world.DecryptSentAsync()).Message.CreatedTime.Should().Be(Now.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task None_emits_explicit_none()
    {
        var world = new World();

        await world.Client().SendAsync(AppMessage(), Direct() with { ReturnRoute = ReturnRouteMode.None });

        var sent = await world.DecryptSentAsync();
        JsonSerializer.Serialize(sent.Message, DidCommJson.Default).Should().Contain("\"return_route\":\"none\"");
        sent.Message.CreatedTime.Should().BeNull("`none` is outside the FR-RR-08 scope");
        var request = world.Router.Requests.Single();
        request.ReturnRoute.Should().Be(ReturnRouteMode.None);
        request.MaxResponseBytes.Should().BeNull("`none` asks for nothing back");
        request.LocalDid.Should().Be(Alice, "a direct authcrypt send still carries its identities (the downgrade needs them)");
        request.PeerDid.Should().Be(Bob);
    }

    [Fact]
    public async Task Null_preserves_a_builder_header()
    {
        var world = new World();
        var message = AppBuilder().WithReturnRoute(ReturnRouteMode.Thread, "thr-builder").Build();

        await world.Client().SendAsync(message, Direct());

        ReturnRouteHeader.Read((await world.DecryptSentAsync()).Message).Should().Be(new ReturnRouteHeader(ReturnRouteMode.Thread, "thr-builder"));
        world.Router.Requests.Single().ReturnRoute.Should().Be(ReturnRouteMode.Thread);
    }

    [Fact]
    public async Task NonNull_overrides_a_builder_header_and_drops_a_stale_thread()
    {
        var world = new World();
        var message = AppBuilder().WithReturnRoute(ReturnRouteMode.Thread, "thr-builder").Build();

        await world.Client().SendAsync(message, Direct() with { ReturnRoute = ReturnRouteMode.All });

        var json = JsonSerializer.Serialize((await world.DecryptSentAsync()).Message, DidCommJson.Default);
        json.Should().Contain("\"return_route\":\"all\"").And.NotContain("return_route_thread").And.NotContain("thr-builder");
        ReturnRouteHeader.Read(message).Should().Be(new ReturnRouteHeader(ReturnRouteMode.Thread, "thr-builder"), "the caller's header is untouched");
    }

    [Fact]
    public async Task Thread_emits_thread_and_return_route_thread()
    {
        var world = new World();

        await world.Client().SendAsync(AppMessage(), Direct() with { ReturnRoute = ReturnRouteMode.Thread, ReturnRouteThread = "thr-42" });

        var json = JsonSerializer.Serialize((await world.DecryptSentAsync()).Message, DidCommJson.Default);
        json.Should().Contain("\"return_route\":\"thread\"").And.Contain("\"return_route_thread\":\"thr-42\"");
        var request = world.Router.Requests.Single();
        request.ReturnRoute.Should().Be(ReturnRouteMode.Thread);
        request.MaxResponseBytes.Should().Be(new DidCommOptions().MaxReceiveBytes);
    }

    public static TheoryData<string, ReturnRouteMode?, string?> BadThreadShapes() => new()
    {
        { "thread without a value", ReturnRouteMode.Thread, null },
        { "thread with whitespace", ReturnRouteMode.Thread, "   " },
        { "thread with a reserved character", ReturnRouteMode.Thread, "thr/1" },
        { "thread of 2049 characters", ReturnRouteMode.Thread, new string('t', ReturnRouteHeader.MaxThreadLength + 1) },
        { "a thread with all", ReturnRouteMode.All, "thr-1" },
        { "a thread with none", ReturnRouteMode.None, "thr-1" },
        { "a thread without a mode", null, "thr-1" },
    };

    [Theory]
    [MemberData(nameof(BadThreadShapes))]
    public async Task Thread_shape_is_refused_before_any_resolution(string label, ReturnRouteMode? mode, string? thread)
    {
        var world = new World();

        var act = () => world.Client().SendAsync(AppMessage(), new SendOptions(Recipients: new[] { Bob }, From: Alice)
        {
            ReturnRoute = mode,
            ReturnRouteThread = thread,
        });

        var thrown = (await act.Should().ThrowAsync<ArgumentException>(label)).Which;
        thrown.ParamName.Should().Be("options");
        if (!string.IsNullOrWhiteSpace(thread))
            thrown.Message.Should().NotContain(thread.Length > 64 ? thread[..64] : thread, "the value is never echoed");
        world.Resolver.Requested.Should().BeEmpty("shape checks run before any I/O (L-008)");
        world.Services.Resolved.Should().BeEmpty();
        world.Router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_undefined_ReturnRoute_value_is_ArgumentOutOfRangeException_before_any_resolution()
    {
        var world = new World();

        var act = () => world.Client().SendAsync(AppMessage(), Direct() with { ReturnRoute = (ReturnRouteMode)42 });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        world.Resolver.Requested.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ReturnRouteMode.All, false)]
    [InlineData(ReturnRouteMode.Thread, false)]
    [InlineData(ReturnRouteMode.All, true)]
    public async Task All_with_two_recipient_DIDs_is_refused_before_any_resolution(ReturnRouteMode mode, bool fromBuilder)
    {
        // JC-4: a return route binds one peer, so recipients naming two DIDs are refused before I/O.
        var world = new World();
        var builder = AppBuilder().WithTo(Bob, Carol);
        var options = new SendOptions(Recipients: new[] { Bob, Carol }, From: Alice, ServiceEndpointOverride: new Uri(BobInbox));
        if (fromBuilder)
            builder = builder.WithReturnRoute(mode, mode == ReturnRouteMode.Thread ? "thr-1" : null);
        else
            options = options with { ReturnRoute = mode, ReturnRouteThread = mode == ReturnRouteMode.Thread ? "thr-1" : null };

        var act = () => world.Client().SendAsync(builder.Build(), options);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.Message.Should().Contain("exactly one DID").And.NotContain("did:");
        world.Resolver.Requested.Should().BeEmpty();
        world.Router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_DIDs_without_a_return_route_still_send_with_an_override()
    {
        var world = new World();

        await world.Client().SendAsync(AppBuilder().WithTo(Bob, Carol).Build(),
            new SendOptions(Recipients: new[] { Bob, Carol }, From: Alice, ServiceEndpointOverride: new Uri(BobInbox)));

        var request = world.Router.Requests.Single();
        request.LocalDid.Should().BeNull("two recipient DIDs bind no peer (JC-18)");
        request.PeerDid.Should().BeNull();
    }

    // ---- FR-RR-09 created_time ----

    public static TheoryData<string> StampedCases() => new()
    {
        "SendOptions all",
        "builder all",
        "SendOptions thread",
        "https://didcomm.org/coordinate-mediation/2.0/mediate-request",
        "https://didcomm.org/coordinate-mediation/3.0/recipient-update",
        "https://didcomm.org/messagepickup/3.0/delivery-request",
        "https://didcomm.org/message-pickup/4.0/messages-received",
    };

    [Theory]
    [MemberData(nameof(StampedCases))]
    public async Task Created_time_is_stamped_from_the_injected_clock(string @case)
    {
        var world = new World();
        var options = Direct();
        Message message;
        switch (@case)
        {
            case "SendOptions all":
                message = AppMessage();
                options = options with { ReturnRoute = ReturnRouteMode.All };
                break;
            case "builder all":
                message = AppBuilder().WithReturnRoute(ReturnRouteMode.All).Build();
                break;
            case "SendOptions thread":
                message = AppMessage();
                options = options with { ReturnRoute = ReturnRouteMode.Thread, ReturnRouteThread = "thr-1" };
                break;
            default:
                // A mediator-service type is in the FR-RR-08 scope without any header.
                message = new MessageBuilder().WithType(@case).WithFrom(Alice).WithTo(Bob).WithBody(new JsonObject()).Build();
                break;
        }

        await world.Client().SendAsync(message, options);

        (await world.DecryptSentAsync()).Message.CreatedTime.Should().Be(Now.ToUnixTimeSeconds(), @case);
        message.CreatedTime.Should().BeNull("the caller's message is never stamped");
    }

    [Fact]
    public async Task Ordinary_application_message_gets_no_created_time()
    {
        var world = new World();

        await world.Client().SendAsync(AppMessage(), Direct());

        (await world.DecryptSentAsync()).Message.CreatedTime.Should().BeNull("outside the FR-RR-08 scope nothing is added");
    }

    [Fact]
    public async Task A_10_minute_old_caller_created_time_is_sent_unchanged()
    {
        var world = new World();
        var old = Now.AddMinutes(-10).ToUnixTimeSeconds();
        var message = AppBuilder().WithCreatedTime(old).Build();

        await world.Client().SendAsync(message, Direct() with { ReturnRoute = ReturnRouteMode.All });

        (await world.DecryptSentAsync()).Message.CreatedTime.Should().Be(old, "a caller's value is never refreshed (FR-RR-09)");
    }

    // ---- the refusal for routed recipients (FR-RR-07) ----

    public static TheoryData<string> RoutedCases() => new()
    {
        "DID URL routing key",
        "bare DID routing key",
        "mediator DID as the uri",
        "builder-set all",
    };

    [Theory]
    [MemberData(nameof(RoutedCases))]
    public async Task Routed_recipient_with_All_is_refused_after_one_service_read_before_any_mediator_sender_or_secret_lookup_and_before_packing(string @case)
    {
        var service = @case switch
        {
            "DID URL routing key" => Info(MediatorInbox, Mediator + "#m1"),
            "bare DID routing key" or "builder-set all" => Info(MediatorInbox, Mediator),
            _ => Info(Mediator),
        };
        var world = new World(service);
        var message = @case == "builder-set all" ? AppBuilder().WithReturnRoute(ReturnRouteMode.All).Build() : AppMessage();
        var options = new SendOptions(Recipients: new[] { Bob }, From: Alice)
        {
            ReturnRoute = @case == "builder-set all" ? null : ReturnRouteMode.All,
        };

        var act = () => world.Client().SendAsync(message, options);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>(@case)).Which;
        thrown.Message.Should().Be(DidCommClient.RoutedReturnRouteRefusal).And.NotContain("did:");
        world.Services.Resolved.Should().Equal(new[] { Bob }, "the service entry is read once, and the mediator's never");
        world.Resolver.Requested.Should().OnlyContain(d => d == Bob, "only the recipient's own keys were resolved: no mediator, routing key or sender");
        world.AliceSecrets.FindPresentCalls.Should().Be(0, "no sender key was looked up, so nothing was packed");
        world.AliceSecrets.Requested.Should().BeEmpty();
        world.Router.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_recipient_with_All_is_allowed_with_the_Forward_default(bool pinned)
    {
        var world = new World();
        var recipient = pinned ? Bob + "#x1" : Bob;

        var result = await world.Client().SendAsync(AppMessage(), new SendOptions(Recipients: new[] { recipient }, From: Alice)
        {
            ReturnRoute = ReturnRouteMode.All,
        });

        result.EndpointUsed.Should().Be(new Uri(BobInbox));
        world.Services.Resolved.Should().Equal(Bob);
        var request = world.Router.Requests.Single();
        request.ReturnRoute.Should().Be(ReturnRouteMode.All);
        request.MaxResponseBytes.Should().Be(new DidCommOptions().MaxReceiveBytes);
        request.LocalDid.Should().Be(Alice);
        request.PeerDid.Should().Be(Bob, "the peer is the DID subject even when the entry pins a key (L-034)");
        result.Returned.Should().BeNull("the router returned no payload");
    }

    [Fact]
    public async Task Override_with_All_reads_no_service_entry()
    {
        // The service entry would be routed; the override bypasses it, so no refusal applies.
        var world = new World(Info(MediatorInbox, Mediator));

        await world.Client().SendAsync(AppMessage(), Direct() with { ReturnRoute = ReturnRouteMode.All });

        world.Services.Resolved.Should().BeEmpty();
        var request = world.Router.Requests.Single();
        request.Endpoint.Should().Be(new Uri(BobInbox));
        request.LocalDid.Should().Be(Alice);
        request.PeerDid.Should().Be(Bob);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queue_endpoint_with_All_still_throws_TransportException_before_any_router_call(bool viaOverride)
    {
        var world = new World(Info(QueueTransport.Uri));
        var options = viaOverride
            ? new SendOptions(Recipients: new[] { Bob }, From: Alice, ServiceEndpointOverride: new Uri(QueueTransport.Uri))
            : new SendOptions(Recipients: new[] { Bob }, From: Alice);

        var act = () => world.Client().SendAsync(AppMessage(), options with { ReturnRoute = ReturnRouteMode.All });

        var thrown = (await act.Should().ThrowAsync<TransportException>()).Which;
        thrown.Message.Should().Be(QueueTransport.RefusalMessage);
        thrown.Scheme.Should().Be("didcomm");
        world.Services.Resolved.Should().HaveCount(viaOverride ? 0 : 1);
        world.Router.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("bare DID routing key")]
    [InlineData("mediator DID as the uri")]
    public async Task A_send_without_ReturnRoute_reads_the_service_entry_exactly_once(string @case)
    {
        // L-039 regression: the route is expanded from the one read made before packing.
        var world = new World(@case == "bare DID routing key" ? Info(MediatorInbox, Mediator) : Info(Mediator));

        var result = await world.Client().SendAsync(AppMessage(), new SendOptions(Recipients: new[] { Bob }, From: Alice));

        world.Services.Resolved.Count(d => d == Bob).Should().Be(1);
        world.Services.Resolved.Should().Equal(@case == "bare DID routing key" ? new[] { Bob } : new[] { Bob, Mediator });
        result.EndpointUsed.Should().Be(new Uri(MediatorInbox));
    }

    // ---- request identities (JC-5, JC-18) ----

    [Fact]
    public async Task Header_less_direct_authcrypt_send_carries_identities_but_no_response_cap()
    {
        var world = new World();

        await world.Client().SendAsync(AppMessage(), new SendOptions(Recipients: new[] { Bob }, From: Alice));

        var request = world.Router.Requests.Single();
        request.ReturnRoute.Should().BeNull();
        request.LocalDid.Should().Be(Alice);
        request.PeerDid.Should().Be(Bob);
        request.MaxResponseBytes.Should().BeNull("nothing was asked for, so no body is read (JC-3)");
    }

    [Fact]
    public async Task Anoncrypt_All_carries_no_LocalDid()
    {
        // JC-5: stamped and sent, but no identity to bind a session or verify a reply against.
        var world = new World();
        var message = new MessageBuilder().WithType(AppType).WithTo(Bob).WithBody(new JsonObject()).Build();

        var result = await world.Client().SendAsync(message, new SendOptions(Recipients: new[] { Bob }) { ReturnRoute = ReturnRouteMode.All });

        var request = world.Router.Requests.Single();
        request.ReturnRoute.Should().Be(ReturnRouteMode.All);
        request.LocalDid.Should().BeNull();
        request.PeerDid.Should().BeNull();
        request.MaxResponseBytes.Should().BeNull("an anoncrypt send can never verify a returned message, so none is read");
        result.Returned.Should().BeNull();
        (await world.DecryptSentAsync()).Message.CreatedTime.Should().Be(Now.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Routed_header_less_send_carries_no_identities()
    {
        var world = new World(Info(MediatorInbox, Mediator));

        await world.Client().SendAsync(AppMessage(), new SendOptions(Recipients: new[] { Bob }, From: Alice));

        var request = world.Router.Requests.Single();
        request.ReturnRoute.Should().BeNull();
        request.LocalDid.Should().BeNull("a routed send never touches a session socket (JC-18)");
        request.PeerDid.Should().BeNull();
        request.MaxResponseBytes.Should().BeNull();
    }

    // ---- the in-band response ----

    [Fact]
    public async Task ResponseDiscarded_on_an_All_send_throws_TransportException_status_200_naming_no_DID()
    {
        var world = new World { Respond = _ => Task.FromResult(new TransportResult(true, 200) { ResponseDiscarded = true }) };

        var act = () => world.Client().SendAsync(AppMessage(), Direct() with { ReturnRoute = ReturnRouteMode.All });

        var thrown = (await act.Should().ThrowAsync<TransportException>()).Which;
        thrown.HttpStatusCode.Should().Be(200);
        thrown.Scheme.Should().Be("https");
        thrown.Message.Should().Contain("MaxReceiveBytes").And.NotContain("did:").And.NotContain("bob.example");
        world.Router.Requests.Should().ContainSingle("the request itself was delivered");
    }

    [Fact]
    public async Task A_reply_from_the_addressed_peer_to_the_sender_is_surfaced_in_Returned()
    {
        var world = new World();
        world.Respond = async request => Returning(await world.PackReplyAsync(request, from: Bob, fromKey: world.BobKey, to: Alice));
        var message = AppMessage();

        var result = await world.Client().SendAsync(message, Direct() with { ReturnRoute = ReturnRouteMode.All });

        result.Returned.Should().NotBeNull();
        result.Returned!.Message.Thid.Should().Be(message.Id);
        result.Returned.Authenticated.Should().BeTrue();
        result.Returned.SenderKid.Should().StartWith(Bob + "#");
        result.Transport.ResponsePayload.Should().NotBeNull("the raw bytes stay on the transport result");
    }

    [Theory]
    [InlineData("anoncrypt from the peer")]
    [InlineData("authcrypt by Mallory")]
    [InlineData("authcrypt by the peer to Carol")]
    [InlineData("signed-only by the peer")]
    public async Task A_reply_failing_the_identity_rule_is_logged_by_the_request_id_and_dropped(string @case)
    {
        var world = new World();
        world.Respond = async request => Returning(@case switch
        {
            "anoncrypt from the peer" => await world.PackReplyAsync(request, from: null, fromKey: null, to: Alice),
            "authcrypt by Mallory" => await world.PackReplyAsync(request, from: Mallory, fromKey: world.MalloryKey, to: Alice),
            "authcrypt by the peer to Carol" => await world.PackReplyAsync(request, from: Bob, fromKey: world.BobKey, to: Carol),
            _ => await world.SignReplyAsync(request),
        });
        var log = new CapturingLogger();
        var message = AppMessage();

        var result = await world.Client(log).SendAsync(message, Direct() with { ReturnRoute = ReturnRouteMode.All });

        result.Returned.Should().BeNull(@case);
        var line = log.Entries.Should().ContainSingle(e => e.EventId == 6).Which;
        line.Level.Should().Be(LogLevel.Warning);
        line.Message.Should().Contain(message.Id);
        line.Message.Should().NotContain("did:", "no DID or key id from the returned bytes is logged");
    }

    [Fact]
    public async Task Garbage_returned_under_All_is_TransportException_200_with_a_typed_inner_exception()
    {
        var world = new World { Respond = _ => Task.FromResult(Returning("{")) };

        var act = () => world.Client().SendAsync(AppMessage(), Direct() with { ReturnRoute = ReturnRouteMode.All });

        var thrown = (await act.Should().ThrowAsync<TransportException>()).Which;
        thrown.HttpStatusCode.Should().Be(200);
        thrown.InnerException.Should().BeAssignableTo<DidCommException>();
        thrown.Message.Should().NotContain("did:");
    }

    [Fact]
    public async Task Payload_on_a_send_without_ReturnRoute_is_ignored_and_never_unpacked()
    {
        // JC-3: an unsolicited body costs no unpack and no resolution. Garbage would throw if it were
        // unpacked; a valid reply would be resolved against. Neither happens.
        var plain = new World();
        await plain.Client().SendAsync(AppMessage(), Direct());

        var garbage = new World { Respond = _ => Task.FromResult(Returning("{")) };
        var garbageResult = await garbage.Client().SendAsync(AppMessage(), Direct());

        var valid = new World();
        valid.Respond = async request => Returning(await valid.PackReplyAsync(request, from: Bob, fromKey: valid.BobKey, to: Alice));
        var validResult = await valid.Client().SendAsync(AppMessage(), Direct());

        garbageResult.Returned.Should().BeNull();
        validResult.Returned.Should().BeNull();
        garbage.Resolver.Requested.Should().Equal(plain.Resolver.Requested, "no extra resolution for an ignored body");
        valid.Resolver.Requested.Should().Equal(plain.Resolver.Requested);
        valid.AliceSecrets.Requested.Should().Equal(plain.AliceSecrets.Requested, "no decryption attempt");
    }

    // ---- helpers ----

    private static SendOptions Direct() => new(Recipients: new[] { Bob }, From: Alice, ServiceEndpointOverride: new Uri(BobInbox));

    private static MessageBuilder AppBuilder() => new MessageBuilder()
        .WithType(AppType)
        .WithFrom(Alice)
        .WithTo(Bob)
        .WithBody(new JsonObject { ["v"] = 1 });

    private static Message AppMessage() => AppBuilder().Build();

    private static DidCommServiceInfo Info(string uri, params string[] routing) => new(uri, routing, Array.Empty<string>());

    private static TransportResult Returning(string body) => new(true, 200)
    {
        ResponsePayload = Encoding.UTF8.GetBytes(body),
        ResponseMediaType = MediaTypes.Encrypted,
    };

    /// <summary>Alice (sender, also holding Carol's key), Bob (recipient), Carol, Mallory and a mediator, published through one spy resolver.</summary>
    private sealed class World
    {
        public TestKeyMaterial AliceKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Alice + "#x1");
        public TestKeyMaterial BobKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Bob + "#x1");
        public TestKeyMaterial BobSigningKey { get; } = TestKeyMaterial.Generate(KeyType.Ed25519, Bob + "#s1");
        public TestKeyMaterial CarolKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Carol + "#x1");
        public TestKeyMaterial MalloryKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Mallory + "#x1");
        public TestKeyMaterial MediatorKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Mediator + "#m1");

        public World(DidCommServiceInfo? bobService = null)
        {
            Resolver = new SpyDidResolver(Docs());
            AliceSecrets = new SpySecretsResolver(AliceKey.PrivateJwk, CarolKey.PrivateJwk);
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

        // Resolving DNS for the example hosts is not what these tests are about.
        public DidCommOptions Options { get; } = new()
        {
            Clock = () => Now,
            OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false },
        };

        public DidCommClient Client(ILogger<DidCommClient>? logger = null)
            => new(AliceSecrets, new NetDidKeyService(Resolver), Services, Router, Options, new JoseCryptoProvider(), opaqueKeys: null, logger);

        /// <summary>Decrypt the one request sent, as Bob (his own resolver, so the spy only sees Alice's calls).</summary>
        public Task<UnpackResult> DecryptSentAsync()
        {
            var bob = new DidCommClient(new SpySecretsResolver(BobKey.PrivateJwk), new NetDidKeyService(new SpyDidResolver(Docs())), new DidCommOptions { Clock = () => Now });
            return bob.UnpackAsync(Encoding.UTF8.GetString(Router.Requests.Single().Payload.Span));
        }

        /// <summary>A reply to the request's message, packed by a peer that is not Alice's client.</summary>
        public async Task<string> PackReplyAsync(TransportRequest request, string? from, TestKeyMaterial? fromKey, string to)
        {
            var thid = await SentIdAsync(request);
            var builder = new MessageBuilder().WithType(AppType + "-response").WithThid(thid).WithTo(to).WithBody(new JsonObject());
            if (from is not null)
                builder = builder.WithFrom(from);
            var held = fromKey is null ? Array.Empty<Jwk>() : new[] { fromKey.PrivateJwk };
            var peer = new DidCommClient(new SpySecretsResolver(held), new NetDidKeyService(new SpyDidResolver(Docs())), new DidCommOptions { Clock = () => Now });
            var packed = await peer.PackEncryptedAsync(builder.Build(), new PackEncryptedOptions(Recipients: new[] { to }, From: from));
            return packed.Message;
        }

        /// <summary>A signed-only reply by Bob.</summary>
        public async Task<string> SignReplyAsync(TransportRequest request)
        {
            var thid = await SentIdAsync(request);
            var peer = new DidCommClient(new SpySecretsResolver(BobSigningKey.PrivateJwk), new NetDidKeyService(new SpyDidResolver(Docs())), new DidCommOptions { Clock = () => Now });
            var reply = new MessageBuilder().WithType(AppType + "-response").WithThid(thid).WithFrom(Bob).WithTo(Alice).WithBody(new JsonObject()).Build();
            return await peer.PackSignedAsync(reply, Bob);
        }

        private async Task<string> SentIdAsync(TransportRequest request)
        {
            var bob = new DidCommClient(new SpySecretsResolver(BobKey.PrivateJwk), new NetDidKeyService(new SpyDidResolver(Docs())), new DidCommOptions { Clock = () => Now });
            return (await bob.UnpackAsync(Encoding.UTF8.GetString(request.Payload.Span))).Message.Id;
        }

        private NetDid.Core.Model.DidDocument[] Docs() => new[]
        {
            LengthCap.Doc(Alice, keyAgreement: AliceKey.PublicJwk),
            LengthCap.DocWithKeys(Bob, new[] { BobSigningKey.PublicJwk }, new[] { BobKey.PublicJwk }),
            LengthCap.Doc(Carol, keyAgreement: CarolKey.PublicJwk),
            LengthCap.Doc(Mallory, keyAgreement: MalloryKey.PublicJwk),
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
