using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Mediator;
using DidComm.InteropTests.Resolution;
using DidComm.InteropTests.Transports;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.Stomp;
using DidComm.Transports.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetDid.Core.Model;
using NetDid.Core.Serialization;
using Xunit;

namespace DidComm.InteropTests.Routing;

/// <summary>
/// #98 (owner answer "A1, B in", 2026-11-19): bytes that are not a DIDComm message (UTF-8 JSON with
/// an object at the top level) are refused wherever this library would pass them on without packing
/// them itself: a mediator's forward intake, <see cref="DidCommClient.SendPackedAsync"/>, and the
/// WebSocket transport (UTF-8 only: a DIDComm message goes out as a text frame, and a receiver fails
/// the whole connection on a text frame that is not UTF-8, RFC 6455 §5.6 and §8.1). The relay test
/// is the reproduction that started this: a pass-through relay with a WebSocket next hop.
/// </summary>
public sealed class NonDidCommPayloadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string Mediator1 = "did:example:mediator1";
    private const string Marker = "ZZZMARKERZZZ";

    // Invalid UTF-8 (0xFF) inside a JSON string: a lossy decode would turn it into a valid JSON object.
    private static readonly byte[] JsonWithInvalidUtf8 = { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}' };

    // Not UTF-8 and not JSON.
    private static readonly byte[] NotUtf8 = { 0x7B, 0xFF, 0xFE, 0x7D };

    // ---- the mediator: AddMediator() + MapDidCommMediator ------------------------------------

    [Fact]
    public async Task The_mediator_refuses_a_forward_carrying_bytes_that_are_not_UTF8_at_intake_with_a_202_and_nothing_held()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did),
            f => f.Attachments![0].Data = new AttachmentData { Base64 = Base64Url.Encode(JsonWithInvalidUtf8) });

        // Hold the 202: a forward admitted to the queue would now hold its reservation, waiting for the
        // acknowledgement. One refused at intake is already counted, and holds nothing.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;
        var post = mediator.PostAsync(packed);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Outcome(ForwardOutcomes.DroppedMalformed) == 1, "the forward is refused at intake, while its 202 is still held");
        var outstanding = mediator.Handler.Outstanding;
        var outstandingBytes = mediator.Handler.OutstandingBytes;
        gate.SetResult();
        var response = await post;
        await mediator.FlushAsync();

        outstanding.Should().Be(0, "nothing was reserved in the forward queue");
        outstandingBytes.Should().Be(0, "nothing was charged to the forward byte budget");
        response.Status.Should().Be(HttpStatusCode.Accepted, "a malformed forward gets the transport's 202 (owner choice A1; the spec leaves the answer open)");
        response.Body.Should().BeEmpty();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedMalformed);
        mediator.Store.MessageCount.Should().Be(0, "nothing is stored");
        mediator.Store.TotalBytes.Should().Be(0);
        mediator.Logs.Entries.Should().ContainSingle(line => line.Contains("is not a DIDComm message", StringComparison.Ordinal));
        mediator.LeakingLogLines().Should().BeEmpty("no DID or key id in any line");
    }

    // ---- the relay-break reproduction: a pass-through relay with a WebSocket next hop ---------

    [Fact]
    public async Task A_pass_through_relay_refuses_a_forward_carrying_bytes_that_are_not_UTF8_and_its_WebSocket_next_hop_keeps_serving()
    {
        // Before #98 the relay passed the bytes on unread. Sent as a text frame they make the next hop
        // fail the connection (RFC 6455 §8.1) and lose what follows; sent as binary they are still not
        // a DIDComm message. Now the relay refuses them at intake: nothing is written, the onward
        // connection stays up, and the next forward arrives on it.
        await using var peer = await RawWebSocketPeer.StartAsync();
        await using var relay = await RelayHost.StartAsync(peer.Endpoint);
        var first = NewProposal();
        var second = NewProposal();

        (await relay.PostAsync(await ForwardForBobAsync(first))).Should().Be(HttpStatusCode.Accepted);
        var frame1 = await peer.NextAsync(Timeout);
        (await relay.PostAsync(await HandBuiltForwardAsync(new AttachmentData { Base64 = Base64Url.Encode(NotUtf8) }))).Should().Be(HttpStatusCode.Accepted);
        await relay.Handler.FlushAsync(Timeout);
        (await relay.PostAsync(await ForwardForBobAsync(second))).Should().Be(HttpStatusCode.Accepted);
        var frame2 = await peer.NextAsync(Timeout);

        frame1.Error.Should().BeNull();
        frame1.Type.Should().Be(WebSocketMessageType.Text);
        (await SpecClient().UnpackAsync(Encoding.UTF8.GetString(frame1.Bytes))).Message.Id.Should().Be(first.Id);
        frame2.Error.Should().BeNull("the next hop's connection was never broken");
        frame2.Type.Should().Be(WebSocketMessageType.Text);
        (await SpecClient().UnpackAsync(Encoding.UTF8.GetString(frame2.Bytes))).Message.Id.Should().Be(second.Id, "nothing was written between the two forwards");
        frame2.Connection.Should().Be(frame1.Connection, "the same onward connection carried both");
        relay.Sink.Accepted.Should().Be(2, "the refused forward never reached the sink");
        relay.Logs.Entries.Should().Contain(line => line.Contains("is not a DIDComm message", StringComparison.Ordinal));
    }

    // ---- DidCommClient.SendPackedAsync -------------------------------------------------------

    public static TheoryData<string, byte[]> NotADidCommMessage => new()
    {
        { "not UTF-8 and not JSON", NotUtf8 },
        { "not UTF-8 inside a JSON string", JsonWithInvalidUtf8 },
        { "UTF-8 text, not JSON", Encoding.UTF8.GetBytes("text " + Marker) },
        { "a JSON array, not an object", Encoding.UTF8.GetBytes("[\"" + Marker + "\"]") },
    };

    [Theory]
    [MemberData(nameof(NotADidCommMessage))]
    public async Task SendPackedAsync_refuses_bytes_that_are_not_a_DIDComm_message_before_sending_anything(string because, byte[] payload)
    {
        var transport = new CountingTransport();
        var client = SpecClient(transport, allowHost: "relay.test");

        var act = () => client.SendPackedAsync(payload, new Uri("https://relay.test/inbox"), MediaTypes.Encrypted);

        var failure = (await act.Should().ThrowAsync<ArgumentException>(because)).Which;
        failure.ParamName.Should().Be("packed");
        failure.Message.Should().NotContain(Marker);
        transport.Sends.Should().Be(0, "nothing reaches a transport");
    }

    [Fact]
    public async Task SendPackedAsync_still_sends_a_DIDComm_message()
    {
        var transport = new CountingTransport();
        var client = SpecClient(transport, allowHost: "relay.test");

        await client.SendPackedAsync(Encoding.UTF8.GetBytes(await PackAliceToBobAsync(NewProposal())), new Uri("https://relay.test/inbox"), MediaTypes.Encrypted);

        transport.Sends.Should().Be(1);
    }

    // ---- WebSocketDidCommTransport -----------------------------------------------------------

    [Theory]
    [InlineData("pooled socket")]
    [InlineData("session socket")]
    [InlineData("STOMP")]
    public async Task The_WebSocket_transport_refuses_a_payload_that_is_not_UTF8_writes_nothing_and_its_connection_stays_usable(string path)
    {
        await using var peer = await RawWebSocketPeer.StartAsync();
        var stomp = path == "STOMP";
        if (stomp)
        {
            peer.Answer = frame => StompFrameCodec.Decode(frame.Bytes).Command == "CONNECT"
                ? (WebSocketMessageType.Binary, StompFrameCodec.Encode(new StompFrame("CONNECTED", new[] { KeyValuePair.Create("version", "1.2") }, ReadOnlyMemory<byte>.Empty)))
                : null;
        }
        await using var transport = new WebSocketDidCommTransport(Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            UseStomp = stomp,
            // A custom Connect skips the transport's private-address guard (the peer is on loopback).
            Connect = (socket, uri, ct) => ((ClientWebSocket)socket).ConnectAsync(uri, ct),
        }));
        var valid1 = Encoding.UTF8.GetBytes(await PackAliceToBobAsync(NewProposal()));
        var valid2 = Encoding.UTF8.GetBytes(await PackAliceToBobAsync(NewProposal()));
        TransportRequest Request(byte[] payload) => path == "session socket"
            ? new TransportRequest(peer.Endpoint, payload, MediaTypes.Encrypted) { LocalDid = Alice, PeerDid = Bob, ReturnRoute = ReturnRouteMode.All }
            : new TransportRequest(peer.Endpoint, payload, MediaTypes.Encrypted);

        await transport.SendAsync(Request(valid1), default);
        var refusal = await Record.ExceptionAsync(() => transport.SendAsync(Request(NotUtf8), default));
        await transport.SendAsync(Request(valid2), default);
        if (stomp)
            StompFrameCodec.Decode((await peer.NextAsync(Timeout)).Bytes).Command.Should().Be("CONNECT");
        var frame1 = await peer.NextAsync(Timeout);
        var frame2 = await peer.NextAsync(Timeout);

        refusal.Should().BeOfType<TransportException>().Which.Message.Should().Contain("not valid UTF-8");
        frame1.Error.Should().BeNull();
        frame2.Error.Should().BeNull("the connection was never broken");
        frame2.Connection.Should().Be(frame1.Connection, "the same connection carried both");
        BodyOf(frame1, stomp).Should().Equal(valid1);
        BodyOf(frame2, stomp).Should().Equal(valid2, "nothing was written for the refused payload");
        if (path == "session socket")
            transport.ActiveSessionSocketCount.Should().Be(1);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static byte[] BodyOf(RawFrame frame, bool stomp)
        => stomp ? StompFrameCodec.Decode(frame.Bytes).Body.ToArray() : frame.Bytes;

    private static Message NewProposal() => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom(Alice)
        .WithTo(Bob)
        .WithBody(new JsonObject { ["messagespecificattribute"] = "and its value" })
        .Build();

    private static async Task<string> PackAliceToBobAsync(Message message)
        => (await SpecClient().PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, Forward: false))).Message;

    // Alice → Bob through Bob's mediator: one forward layer anoncrypted for mediator1.
    private static async Task<string> ForwardForBobAsync(Message message)
        => (await SpecClient().PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, Forward: true))).Message;

    // A forward to Bob built by hand, carrying `data`, anoncrypted for mediator1.
    private static async Task<string> HandBuiltForwardAsync(AttachmentData data)
    {
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(Mediator1)
            .WithBody(new JsonObject { ["next"] = Bob })
            .WithAttachment(new Attachment { Id = "payload", MediaType = ForwardConstants.PayloadMediaType, Data = data })
            .Build();
        return (await SpecClient().PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { Mediator1 }, From: null, Forward: false))).Message;
    }

    // The spec actors (Alice, Bob, the mediators) over the routing fixtures.
    private static DidCommClient SpecClient(IDidCommTransport? transport = null, string? allowHost = null)
    {
        var resolver = LoadRoutingResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());
        var options = new DidCommOptions();
        if (allowHost is not null)
            options.OutboundEndpointPolicy.AllowedHosts.Add(allowHost);
        var secrets = SpecActorRegistry.LoadDefault().AsSecretsResolver();
        return transport is null
            ? new DidCommClient(secrets, keyService, serviceResolver, options)
            : new DidCommClient(secrets, keyService, serviceResolver, new TransportRouter(new IDidCommTransport[] { transport }), options);
    }

    private static FixtureDidResolver LoadRoutingResolver()
    {
        var docs = new Dictionary<string, DidDocument>(StringComparer.Ordinal);
        foreach (var fileName in new[] { "alice.json", "bob-with-routing.json", "mediator1.json", "mediator2.json", "charlie.json" })
        {
            var path = Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec", fileName);
            var doc = DidDocumentSerializer.Deserialize(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"DID Document at '{path}' deserialised to null.");
            docs[doc.Id.Value!] = doc;
        }
        return new FixtureDidResolver(docs);
    }

    /// <summary>Counts the sends that reach it.</summary>
    private sealed class CountingTransport : IDidCommTransport
    {
        private int _sends;

        public int Sends => Volatile.Read(ref _sends);

        public string Scheme => "https";

        public bool CanHandle(Uri endpoint) => true;

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _sends);
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
        }
    }

    /// <summary>
    /// A pass-through relay: <c>AddRoutingMediator()</c> behind <c>MapDidCommEndpoint</c>, holding
    /// mediator1's keys, with Bob registered, and a sink that relays every forward to one WebSocket
    /// next hop with <see cref="DidCommClient.SendPackedAsync"/> (what samples/04's sink does over HTTP).
    /// </summary>
    private sealed class RelayHost : IAsyncDisposable
    {
        private RelayHost(WebApplication app, WebSocketRelaySink sink, CapturingLoggerProvider logs, WebSocketDidCommTransport onward)
        {
            App = app;
            Sink = sink;
            Logs = logs;
            Onward = onward;
        }

        public WebApplication App { get; }

        public WebSocketRelaySink Sink { get; }

        public CapturingLoggerProvider Logs { get; }

        private WebSocketDidCommTransport Onward { get; }

        public ForwardHandler Handler => App.Services.GetRequiredService<ForwardHandler>();

        public static async Task<RelayHost> StartAsync(Uri nextHop)
        {
            var onward = new WebSocketDidCommTransport(Options.Create(new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                Connect = (socket, uri, ct) => ((ClientWebSocket)socket).ConnectAsync(uri, ct),
            }));
            var sink = new WebSocketRelaySink(SpecClient(onward, allowHost: nextHop.Host), nextHop);
            var resolver = LoadRoutingResolver();
            var keyService = new NetDidKeyService(resolver);
            var logs = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton<IDidKeyService>(keyService);
            builder.Services.AddSingleton<IServiceEndpointResolver>(new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions()));
            builder.Services.AddSingleton<IForwardSink>(sink);
            builder.Services.AddDidComm(b =>
            {
                // A relay holds its own keys only.
                b.UseSecretsResolver(new KidPrefixSecrets(SpecActorRegistry.LoadDefault().AsSecretsResolver(), Mediator1 + "#"));
                b.AddBuiltInProtocols();
                b.AddRoutingMediator();
            });
            var app = builder.Build();
            app.UseRouting();
            app.MapDidCommEndpoint("/didcomm");
            await app.StartAsync();
            app.Services.GetRequiredService<InMemoryForwardRecipientRegistry>().Register(Bob);
            return new RelayHost(app, sink, logs, onward);
        }

        public async Task<HttpStatusCode> PostAsync(string packed)
        {
            using var client = App.GetTestServer().CreateClient();
            using var content = new StringContent(packed, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(MediaTypes.Encrypted);
            using var response = await client.PostAsync("/didcomm", content);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            await Onward.DisposeAsync();
        }
    }

    /// <summary>Relays every forward to one WebSocket next hop.</summary>
    private sealed class WebSocketRelaySink(DidCommClient relay, Uri nextHop) : IForwardSink
    {
        private int _accepted;

        public int Accepted => Volatile.Read(ref _accepted);

        public async Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _accepted);
            await relay.SendPackedAsync(result.OnwardPacked, nextHop, ForwardConstants.PayloadMediaType, ct);
        }
    }

    /// <summary>Restricts the shared spec-actor secrets to one actor's key ids.</summary>
    private sealed class KidPrefixSecrets(ISecretsResolver inner, string prefix) : ISecretsResolver
    {
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
            => kid.StartsWith(prefix, StringComparison.Ordinal) ? inner.FindAsync(kid, ct) : Task.FromResult<Jwk?>(null);

        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => inner.FindPresentAsync(kids.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList(), ct);
    }
}
