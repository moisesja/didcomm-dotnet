using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.InteropTests.Transports;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Secrets;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetDid.Core.Model;
using NetDid.Core.Serialization;
using Xunit;

namespace DidComm.InteropTests.Routing;

/// <summary>
/// #90 test matrix I6..I9 — the mediator role on a real in-process host: <c>AddRoutingMediator()</c>
/// behind the registry-aware <c>MapDidCommEndpoint("/didcomm")</c> (and <c>MapDidCommWebSocket("/ws")</c>),
/// Appendix A actors over the vendored DID Documents. The mediator holds only mediator1's secrets;
/// Alice packs with <c>Forward = true</c> (bob-with-routing.json names mediator1 as the routing key) or
/// builds a forward by hand; the host's <see cref="IForwardSink"/> is a capturing sink and its logs
/// are captured.
/// </summary>
public sealed class MediatorEndpointRelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string Mediator1 = "did:example:mediator1";
    private const string Bob = "did:example:bob";
    private const string Alice = "did:example:alice";

    [Fact]
    public async Task Registered_recipient_forward_returns_202_before_the_sink_runs_then_Bob_unpacks_the_onward_payload()
    {
        await using var host = await StartMediatorAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Sink.Gate = gate;
        var proposal = NewProposal();
        var packed = await PackForBobViaMediatorAsync(proposal);

        var response = await PostEnvelopeAsync(host.Server, packed.Message);

        // FR-ROUTE-11: the 202 is written before any registration-dependent work — the sink is
        // still blocked on the gate when the response arrives.
        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
        gate.Task.IsCompleted.Should().BeFalse("the test owns the gate; nothing else may release it");
        await host.Sink.WaitForEnteredAsync(1, Timeout);
        host.Sink.Results.Should().BeEmpty("the sink is inside AcceptAsync, blocked on the gate, while the 202 is already in hand");

        gate.SetResult();
        var results = await host.Sink.WaitForCountAsync(1, Timeout);
        await host.Handler.FlushAsync(Timeout);
        results.Should().ContainSingle();
        host.Sink.Results.Should().ContainSingle("exactly one sink call for one forward");
        var result = results[0];
        result.NextHop.Should().Be(Bob);

        var bobUnpack = await BobClient().UnpackAsync(Encoding.UTF8.GetString(result.OnwardPacked));
        bobUnpack.Message.From.Should().Be(Alice);
        bobUnpack.Message.To.Should().Contain(Bob);
        bobUnpack.Message.Type.Should().Be(proposal.Type);
        bobUnpack.Message.Id.Should().Be(proposal.Id);
        bobUnpack.Authenticated.Should().BeTrue("Alice's authcrypt survives the relay untouched");
        bobUnpack.SenderKid.Should().StartWith(Alice + "#");
    }

    [Fact]
    public async Task Unknown_next_returns_202_and_nothing_reaches_the_sink()
    {
        await using var host = await StartMediatorAsync(registerBob: false);
        var packed = await PackForBobViaMediatorAsync(NewProposal());

        var response = await PostEnvelopeAsync(host.Server, packed.Message);

        response.Status.Should().Be(HttpStatusCode.Accepted, "an unregistered next is dropped silently, never reported (FR-ROUTE-11)");
        response.Body.Should().BeEmpty();
        await host.Handler.FlushAsync(Timeout);
        host.Sink.Results.Should().BeEmpty();
        host.Sink.Entered.Should().Be(0);
        host.Logs.Entries.Should().Contain(e => e.Contains("not a registered recipient"));
    }

    [Fact]
    public async Task Plaintext_forward_returns_202_and_nothing_reaches_the_sink()
    {
        await using var host = await StartMediatorAsync();
        var inner = (await AliceClient().PackEncryptedAsync(NewProposal(), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;
        var forward = ForwardMessage.Create(Mediator1, Bob, new[] { inner });
        var plain = await AliceClient().PackPlaintextAsync(forward);

        var response = await PostEnvelopeAsync(host.Server, plain, MediaTypes.Plaintext);

        response.Status.Should().Be(HttpStatusCode.Accepted, "the plaintext unpacks and dispatches; the handler drops it");
        await host.Handler.FlushAsync(Timeout);
        host.Sink.Results.Should().BeEmpty("a forward that was not decrypted with a held key is never relayed (FR-ROUTE-11)");
        host.Sink.Entered.Should().Be(0);
        host.Logs.Entries.Should().Contain(e => e.Contains(forward.Id) && e.Contains("plaintext, signed-only or not decrypted with a held key"));
    }

    [Fact]
    public async Task Expired_forward_is_refused_at_unpack_with_the_uniform_400_and_never_reaches_the_sink()
    {
        // Over HTTP the forward's own expires_time is judged first by UnpackAsync (FR-API-05) on the
        // same clock and skew the handler would use, so an expired forward never reaches the
        // handler's FR-ROUTE-09 check here — it gets the uniform 400 (FR-API-07). The handler-level
        // check is covered through the dispatcher with a split clock in RoutingMediatorDiTests.
        await using var host = await StartMediatorAsync();
        var inner = (await AliceClient().PackEncryptedAsync(NewProposal(), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;
        var expired = ForwardMessage.Create(Mediator1, Bob, new[] { inner },
            expiresTimeEpochSeconds: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        var packed = await AliceClient().PackEncryptedAsync(expired, new PackEncryptedOptions(Recipients: new[] { Mediator1 }));

        var response = await PostEnvelopeAsync(host.Server, packed.Message);

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.Body.Should().BeEmpty();
        await host.Handler.FlushAsync(Timeout);
        host.Sink.Results.Should().BeEmpty();
        host.Sink.Entered.Should().Be(0);
    }

    [Fact]
    public async Task Forward_without_a_to_header_but_decrypted_with_the_mediator_key_still_relays()
    {
        // The FR-ROUTE-11 posture is kid-based: the plaintext `to` plays no part. A forward that
        // omits `to` altogether passes the unpack-time addressing checks (nothing to compare) and
        // relays because it was decrypted with mediator1's key.
        await using var host = await StartMediatorAsync();
        var inner = (await AliceClient().PackEncryptedAsync(NewProposal(), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;
        var forward = ForwardMessage.Create(Mediator1, Bob, new[] { inner });
        forward.To = null;
        var packed = await AliceClient().PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { Mediator1 }));

        var response = await PostEnvelopeAsync(host.Server, packed.Message);

        response.Status.Should().Be(HttpStatusCode.Accepted);
        var results = await host.Sink.WaitForCountAsync(1, Timeout);
        results.Should().ContainSingle().Which.NextHop.Should().Be(Bob);
        // The inner JWE is relayed unchanged (key order is not significant in a JWE).
        JsonNode.DeepEquals(JsonNode.Parse(Encoding.UTF8.GetString(results[0].OnwardPacked)), JsonNode.Parse(inner)).Should().BeTrue();
    }

    [Fact]
    public async Task Forward_addressed_to_another_party_is_refused_at_unpack_and_never_reaches_the_sink()
    {
        // A forward whose `to` names someone other than the decrypting DID is a consistency
        // violation (FR-CONSIST-02) inside UnpackAsync: uniform 400, nothing dispatched.
        await using var host = await StartMediatorAsync();
        var inner = (await AliceClient().PackEncryptedAsync(NewProposal(), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;
        var forward = ForwardMessage.Create("did:example:charlie", Bob, new[] { inner });
        var packed = await AliceClient().PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { Mediator1 }));

        var response = await PostEnvelopeAsync(host.Server, packed.Message);

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.Body.Should().BeEmpty();
        await host.Handler.FlushAsync(Timeout);
        host.Sink.Results.Should().BeEmpty();
        host.Sink.Entered.Should().Be(0);
    }

    [Fact]
    public async Task Relay_logs_carry_the_message_id_and_never_the_next_DID()
    {
        await using var host = await StartMediatorAsync();
        var inner = (await AliceClient().PackEncryptedAsync(NewProposal(), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;

        // Relayed (Bob is registered) and dropped (charlie is not): both paths, known ids.
        var relayed = ForwardMessage.Create(Mediator1, Bob, new[] { inner });
        var dropped = ForwardMessage.Create(Mediator1, "did:example:charlie", new[] { inner });
        (await PostEnvelopeAsync(host.Server, (await AliceClient().PackEncryptedAsync(relayed, new PackEncryptedOptions(Recipients: new[] { Mediator1 }))).Message))
            .Status.Should().Be(HttpStatusCode.Accepted);
        (await PostEnvelopeAsync(host.Server, (await AliceClient().PackEncryptedAsync(dropped, new PackEncryptedOptions(Recipients: new[] { Mediator1 }))).Message))
            .Status.Should().Be(HttpStatusCode.Accepted);
        await host.Sink.WaitForCountAsync(1, Timeout);
        await host.Handler.FlushAsync(Timeout);
        host.Sink.Results.Should().ContainSingle().Which.NextHop.Should().Be(Bob);

        var entries = host.Logs.Entries.ToList();
        entries.Should().Contain(e => e.Contains(dropped.Id) && e.Contains("not a registered recipient (FR-ROUTE-11)"),
            "the drop is logged with the message id only");
        entries.Should().NotContain(e => e.Contains("did:example:charlie"), "the unregistered next DID never reaches the logs");
        entries.Should().NotContain(e => e.Contains(Bob), "the relayed next DID never reaches the logs");
        entries.Should().NotContain(e => e.Contains(Alice), "the inner sender never reaches the logs");
        entries.Where(e => e.StartsWith("Forward ", StringComparison.Ordinal))
            .Should().OnlyContain(e => e.Contains(relayed.Id) || e.Contains(dropped.Id), "every handler line names the forward by id");
    }

    [Fact]
    public async Task Two_forwards_in_quick_succession_reach_the_sink_in_order()
    {
        await using var host = await StartMediatorAsync();
        var first = NewProposal();
        var second = NewProposal();
        var packedFirst = await PackForBobViaMediatorAsync(first);
        var packedSecond = await PackForBobViaMediatorAsync(second);

        (await PostEnvelopeAsync(host.Server, packedFirst.Message)).Status.Should().Be(HttpStatusCode.Accepted);
        (await PostEnvelopeAsync(host.Server, packedSecond.Message)).Status.Should().Be(HttpStatusCode.Accepted);

        var results = await host.Sink.WaitForCountAsync(2, Timeout);
        await host.Handler.FlushAsync(Timeout);
        host.Sink.Results.Should().HaveCount(2);
        results.Select(r => r.NextHop).Should().Equal(Bob, Bob);
        var bob = BobClient();
        (await bob.UnpackAsync(Encoding.UTF8.GetString(results[0].OnwardPacked))).Message.Id.Should().Be(first.Id, "FIFO: the first forward is relayed first");
        (await bob.UnpackAsync(Encoding.UTF8.GetString(results[1].OnwardPacked))).Message.Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task Undecryptable_forward_returns_the_uniform_400_and_never_reaches_the_sink()
    {
        // J3 / FR-API-07: a JWE the mediator cannot open (anoncrypted to Bob's key, which the
        // mediator does not hold) fails inside UnpackAsync — the same bare 400 as any rejection,
        // before the handler and therefore before any registration-dependent work.
        await using var host = await StartMediatorAsync();
        var inner = (await AliceClient().PackEncryptedAsync(NewProposal(), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;
        var forward = ForwardMessage.Create(Bob, Bob, new[] { inner });
        var packedForBob = await AliceClient().PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { Bob }));

        var response = await PostEnvelopeAsync(host.Server, packedForBob.Message);

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.Body.Should().BeEmpty();
        await host.Handler.FlushAsync(Timeout);
        host.Sink.Results.Should().BeEmpty();
        host.Sink.Entered.Should().Be(0);
        (await PostEnvelopeAsync(host.Server, "not even json")).Should().Be(response, "the response is the same as for any other rejection");
    }

    [Fact]
    public async Task WebSocket_endpoint_relays_a_forward_to_the_sink()
    {
        await using var host = await StartMediatorAsync();
        var proposal = NewProposal();
        var packed = await PackForBobViaMediatorAsync(proposal);

        var wsClient = host.Server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(BuildWebSocketUri(host.Server, "/ws"), default);
        await socket.SendAsync(Encoding.UTF8.GetBytes(packed.Message), WebSocketMessageType.Binary, endOfMessage: true, default);

        var results = await host.Sink.WaitForCountAsync(1, Timeout);
        await host.Handler.FlushAsync(Timeout);
        host.Sink.Results.Should().ContainSingle("one binary frame, one sink call");
        results[0].NextHop.Should().Be(Bob);
        var bobUnpack = await BobClient().UnpackAsync(Encoding.UTF8.GetString(results[0].OnwardPacked));
        bobUnpack.Message.Id.Should().Be(proposal.Id);
        bobUnpack.Authenticated.Should().BeTrue();

        socket.State.Should().Be(WebSocketState.Open, "a mediator sends nothing back on the socket (FR-ROUTE-07)");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- harness ---------------------------------------------------------------------------

    private sealed class MediatorHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required CapturingForwardSink Sink { get; init; }
        public required CapturingLoggerProvider Logs { get; init; }
        public TestServer Server => App.GetTestServer();
        public ForwardHandler Handler => App.Services.GetRequiredService<ForwardHandler>();
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<MediatorHost> StartMediatorAsync(bool registerBob = true)
    {
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadRoutingResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());
        var sink = new CapturingForwardSink();
        var logs = new CapturingLoggerProvider();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        // Host-provided services go in BEFORE AddDidComm: its IDidKeyService check is satisfied by
        // the manual registration (no UseNetDidResolver), and the host's sink wins over NullForwardSink.
        builder.Services.AddSingleton<IDidKeyService>(keyService);
        builder.Services.AddSingleton<IServiceEndpointResolver>(serviceResolver);
        builder.Services.AddSingleton<IForwardSink>(sink);
        builder.Services.AddDidComm(b =>
        {
            // A real mediator holds its own keys only — not Bob's.
            b.UseSecretsResolver(new KidPrefixSecretsResolver(actors.AsSecretsResolver(), Mediator1 + "#"));
            b.AddBuiltInProtocols();
            b.AddRoutingMediator();
            b.Configure(o => o.MaxReceiveBytes = 64 * 1024);
        });

        var app = builder.Build();
        app.UseWebSockets();
        app.UseRouting();
        app.MapDidCommEndpoint("/didcomm");
        app.MapDidCommWebSocket("/ws");
        await app.StartAsync();

        if (registerBob)
            app.Services.GetRequiredService<InMemoryForwardRecipientRegistry>().Register(Bob);
        app.Services.GetRequiredService<IForwardSink>().Should().BeSameAs(sink);

        return new MediatorHost { App = app, Sink = sink, Logs = logs };
    }

    private static DidCommClient AliceClient() => ActorClient();
    private static DidCommClient BobClient() => ActorClient();

    private static DidCommClient ActorClient()
    {
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadRoutingResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());
        return new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, new DidCommOptions());
    }

    /// <summary>Alice authcrypts for Bob with <c>Forward = true</c>: one forward layer anoncrypted for mediator1.</summary>
    private static async Task<PackEncryptedResult> PackForBobViaMediatorAsync(Message message)
    {
        var packed = await AliceClient().PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { Bob },
            From: Alice,
            Forward: true));
        packed.ServiceEndpoint.Should().Be("http://example.com/path");
        return packed;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostEnvelopeAsync(
        TestServer server, string body, string contentType = MediaTypes.Encrypted)
    {
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/didcomm")
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static Uri BuildWebSocketUri(TestServer server, string path)
        => new UriBuilder(server.BaseAddress) { Scheme = "ws", Path = path }.Uri;

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

    private static Message NewProposal() => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom(Alice)
        .WithTo(Bob)
        .WithBody(System.Text.Json.Nodes.JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
        .Build();

    /// <summary>Restricts a shared Appendix A secrets resolver to one actor's key ids.</summary>
    private sealed class KidPrefixSecretsResolver : ISecretsResolver
    {
        private readonly ISecretsResolver _inner;
        private readonly string _prefix;

        public KidPrefixSecretsResolver(ISecretsResolver inner, string prefix)
        {
            _inner = inner;
            _prefix = prefix;
        }

        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
            => kid.StartsWith(_prefix, StringComparison.Ordinal) ? _inner.FindAsync(kid, ct) : Task.FromResult<Jwk?>(null);

        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => _inner.FindPresentAsync(kids.Where(k => k.StartsWith(_prefix, StringComparison.Ordinal)).ToList(), ct);
    }

    private sealed class CapturingForwardSink : IForwardSink
    {
        private readonly ConcurrentQueue<ForwardProcessingResult> _results = new();
        private int _entered;

        /// <summary>When set, <see cref="AcceptAsync"/> blocks on it before recording the result.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Sink invocations, counted before the gate.</summary>
        public int Entered => Volatile.Read(ref _entered);

        public IReadOnlyCollection<ForwardProcessingResult> Results => _results;

        public async Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _entered);
            if (Gate is { } gate)
                await gate.Task.WaitAsync(ct);
            _results.Enqueue(result);
        }

        public async Task WaitForEnteredAsync(int count, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (Entered < count)
            {
                if (watch.Elapsed > timeout)
                    throw new TimeoutException($"Sink was entered {Entered} time(s); expected {count} within {timeout}.");
                await Task.Delay(10);
            }
        }

        public async Task<IReadOnlyList<ForwardProcessingResult>> WaitForCountAsync(int count, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (_results.Count < count)
            {
                if (watch.Elapsed > timeout)
                    throw new TimeoutException($"Sink received {_results.Count} result(s); expected {count} within {timeout}.");
                await Task.Delay(10);
            }
            return _results.ToList();
        }
    }
}
