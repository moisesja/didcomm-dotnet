using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DidComm.AspNetCore;
using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports;
using DidComm.Transports.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// Phase 5 Checkpoint C — end-to-end FR-TRN-04/05/07 + FR-API-06 round-trip: Alice packs
/// authcrypt for Bob, sends via <see cref="HttpDidCommTransport"/> against an in-process
/// <see cref="TestServer"/> hosting <c>MapDidCommEndpoint</c>, and the receiver decrypts the
/// original plaintext.
/// </summary>
public sealed class AspNetCoreReceiveRoundTripTests
{
    [Fact]
    public async Task RoundTrip_Alice_HTTP_to_Bob_unpacks_original_plaintext()
    {
        var (server, received) = await BuildServerAsync();

        var sendResult = await SendFromAliceAsync(server, NewProposal());

        sendResult.Transport.Accepted.Should().BeTrue();
        sendResult.Transport.HttpStatusCode.Should().Be(StatusCodes.Status202Accepted);

        var unpacked = received.Single();
        unpacked.Message.From.Should().Be("did:example:alice");
        unpacked.Message.To.Should().Contain("did:example:bob");
        unpacked.Message.Type.Should().Be("http://example.com/protocols/lets_do_lunch/1.0/proposal");
        unpacked.Authenticated.Should().BeTrue();
    }

    [Fact]
    public async Task Returns_415_when_content_type_does_not_match_didcomm_media_types()
    {
        var (server, _) = await BuildServerAsync();

        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/didcomm")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Returns_415_when_content_type_is_malformed()
    {
        var (server, _) = await BuildServerAsync();

        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/didcomm")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
        };
        // A malformed Content-Type (no "type/subtype") must answer 415, not surface a 500 from
        // the media-type parser. TryAddWithoutValidation bypasses the client-side header check so
        // the bad value actually reaches the endpoint.
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "not-a-valid-type");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Returns_413_when_payload_exceeds_MaxReceiveBytes()
    {
        var (server, _) = await BuildServerAsync(opts => opts.MaxReceiveBytes = 64);

        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/didcomm")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('x', 256))),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/didcomm-encrypted+json");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Returns_400_when_body_is_not_a_valid_didcomm_envelope()
    {
        var (server, _) = await BuildServerAsync();

        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/didcomm")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("not even json")),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/didcomm-encrypted+json");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Issue #20: the body carries no detail (no Problem JSON) that could form an oracle.
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Receive_rejections_are_indistinguishable_so_there_is_no_oracle()
    {
        // Issues #20/#28: a malformed envelope (MalformedMessageException), an undecryptable one
        // (CryptoException), and a self-consistent forgery that fails an addressing rule
        // (ConsistencyException) MUST all yield the SAME response — same 400 status, same empty body.
        // Previously crypto failures leaked the failing recipient kid / layer, and a ConsistencyException
        // escaped to a 500 (+ dev stack trace), so a peer could distinguish the failure class.
        // (#35 closes the residual TIMING channel these same classes left open — covered by the
        // floor tests below; this test asserts the status/body parity only.)
        var (server, _) = await BuildServerAsync();

        var malformed = "not even json";
        var undecryptable = CorruptField(await PackAnoncryptForBobAsync(), "ciphertext");
        var forged = await PackMismatchedFromAuthcryptAsync();

        var results = new[]
        {
            await PostEnvelopeAsync(server, malformed),
            await PostEnvelopeAsync(server, undecryptable),
            await PostEnvelopeAsync(server, forged),
        };

        results.Should().AllSatisfy(r =>
        {
            r.Status.Should().Be(HttpStatusCode.BadRequest);
            r.Body.Should().BeEmpty(); // no kid / layer / DID / stack-trace text distinguishes the classes
        });
    }

    // === #35: constant-time rejection floor =================================================
    // The pad helper is exercised directly (no HTTP, no flaky upper bounds) and through the live
    // endpoint. Every assertion against a rejection is a LOWER bound on elapsed time: a slow CI can
    // only make elapsed larger, never smaller, so these do not flake. The one upper-bound assertion
    // (the success path) is given a multi-second floor it must NOT pay, so its margin is enormous.

    [Fact]
    public async Task DelayToRejectionFloor_waits_out_the_remaining_floor()
    {
        var start = Stopwatch.GetTimestamp();
        var floor = TimeSpan.FromMilliseconds(200);

        await DidCommEndpointRouteBuilderExtensions.DelayToRejectionFloorAsync(start, floor);

        var elapsed = Stopwatch.GetElapsedTime(start);
        // Lower bound only (180 ms < 200 ms floor, slack for timer granularity). Task.Delay never
        // fires early, so a held-kid path that finished in microseconds is still lifted to the floor.
        elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180));
    }

    [Fact]
    public async Task DelayToRejectionFloor_is_a_noop_when_floor_is_zero()
    {
        var start = Stopwatch.GetTimestamp();

        await DidCommEndpointRouteBuilderExtensions.DelayToRejectionFloorAsync(start, TimeSpan.Zero);

        // Disabled floor: returns essentially immediately (no wait). Generous ceiling — this only
        // guards against an accidental unconditional delay, not a precise timing.
        Stopwatch.GetElapsedTime(start).Should().BeLessThan(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task DelayToRejectionFloor_is_a_noop_when_work_already_exceeded_the_floor()
    {
        var start = Stopwatch.GetTimestamp();

        // A floor already elapsed by the time we call (1 tick = 100 ns) → no further delay. Models the
        // held + DID-resolution path that legitimately ran longer than the floor.
        await DidCommEndpointRouteBuilderExtensions.DelayToRejectionFloorAsync(start, TimeSpan.FromTicks(1));

        Stopwatch.GetElapsedTime(start).Should().BeLessThan(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task Rejection_response_is_padded_to_the_floor()
    {
        // With a 200 ms floor, the malformed-envelope rejection (which fast-fails in microseconds)
        // must still take ≥ ~floor before answering 400 — the timing channel is closed end-to-end.
        var (server, _) = await BuildServerAsync(rejectionFloor: TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        var result = await PostEnvelopeAsync(server, "not even json");
        sw.Stop();

        result.Status.Should().Be(HttpStatusCode.BadRequest);
        result.Body.Should().BeEmpty();
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180));
    }

    [Fact]
    public async Task Large_body_rejection_is_still_padded_to_the_floor()
    {
        // PR #43 red-team (Finding 1): the floor must be measured AFTER the body read, not from
        // handler entry. Otherwise a peer pads the envelope out toward MaxReceiveBytes so the read
        // time alone exhausts the floor, the pad collapses to zero, and the held-vs-unheld crypto gap
        // is re-exposed. A large malformed body must therefore STILL be padded to the floor.
        var (server, _) = await BuildServerAsync(
            configureOptions: o => o.MaxReceiveBytes = 1 * 1024 * 1024,
            rejectionFloor: TimeSpan.FromMilliseconds(200));

        var largeMalformed = new string('x', 256 * 1024); // 256 KiB, well under the 1 MiB cap; not valid JSON
        var sw = Stopwatch.StartNew();
        var result = await PostEnvelopeAsync(server, largeMalformed);
        sw.Stop();

        result.Status.Should().Be(HttpStatusCode.BadRequest);
        result.Body.Should().BeEmpty();
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180));
    }

    [Fact]
    public async Task Successful_receive_is_not_padded_by_the_floor()
    {
        // Pin a deliberately huge floor (2 s) that ONLY the rejection path would pay. A successfully
        // unpacked envelope answers 202 and must return far faster — proving the floor never touches
        // the hot success path (which would otherwise be a throughput regression).
        var (server, _) = await BuildServerAsync(rejectionFloor: TimeSpan.FromSeconds(2));

        var valid = await PackAnoncryptForBobAsync();
        var sw = Stopwatch.StartNew();
        var result = await PostEnvelopeAsync(server, valid);
        sw.Stop();

        result.Status.Should().Be(HttpStatusCode.Accepted); // 202, decrypted for Bob
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1)); // nowhere near the 2 s rejection floor
    }

    private static DidCommClient NewPackerClient()
    {
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());
        return new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, new DidCommOptions());
    }

    private static async Task<string> PackAnoncryptForBobAsync()
    {
        var packed = await NewPackerClient().PackEncryptedAsync(
            NewProposal(), new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }));
        return packed.Message;
    }

    private static async Task<string> PackMismatchedFromAuthcryptAsync()
    {
        // authcrypt skid = alice, but the inner plaintext claims from = carol → FR-CONSIST-01 fails
        // on unpack (a self-consistent forgery the server can fully decrypt before rejecting).
        var message = new MessageBuilder()
            .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
            .WithFrom("did:example:carol")
            .WithTo("did:example:bob")
            .WithBody(System.Text.Json.Nodes.JsonNode.Parse("""{"a":"b"}""")!.AsObject())
            .Build();
        var packed = await NewPackerClient().PackEncryptedAsync(
            message, new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, From: "did:example:alice"));
        return packed.Message;
    }

    private static string CorruptField(string jweJson, string field)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(jweJson)!.AsObject();
        var original = node[field]!.GetValue<string>();
        var flipped = original[..^1] + (original[^1] == 'A' ? 'B' : 'A'); // still valid base64url; AEAD tag fails
        node[field] = flipped;
        return node.ToJsonString();
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostEnvelopeAsync(TestServer server, string body)
    {
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/didcomm")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/didcomm-encrypted+json");
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Throwing_onReceive_callback_still_returns_202_not_a_500_oracle()
    {
        // Issue #20 red-team: a consumer callback that throws after a successful unpack must NOT escape
        // as a 500 — otherwise an attacker could probe "did this envelope decrypt for us?" by inducing
        // the throw, re-exposing the unpack-success oracle. The receive is one-way: log + ack 202.
        var (server, _) = await BuildServerAsync(
            onReceive: (_, _) => throw new InvalidOperationException("downstream handler blew up"));

        var valid = await PackAnoncryptForBobAsync();
        var result = await PostEnvelopeAsync(server, valid);

        result.Status.Should().Be(HttpStatusCode.Accepted); // 202, not 500
    }

    [Fact]
    public async Task Downstream_resolution_timeout_returns_400_not_a_500_oracle()
    {
        // PR #37 review: a downstream DID-resolution timeout surfaces as TaskCanceledException :
        // OperationCanceledException with the request token NOT cancelled. It must collapse to the
        // uniform 400, not be mistaken for a client abort and rethrown as a distinguishable 500 — which
        // would re-open the 400-vs-500 oracle via an attacker-controlled did:webvh host that hangs until
        // the resolver's HttpClient.Timeout fires.
        var (server, _) = await BuildServerAsync(resolverOverride: new TimeoutResolver());
        var valid = await PackAnoncryptForBobAsync(); // decrypts (Bob's secret), then resolution throws

        var result = await PostEnvelopeAsync(server, valid);

        result.Status.Should().Be(HttpStatusCode.BadRequest); // 400, not 500; RequestAborted is not cancelled
        result.Body.Should().BeEmpty();
    }

    /// <summary>
    /// Simulates a downstream resolver whose HttpClient.Timeout fires: throws
    /// <see cref="TaskCanceledException"/> (a <see cref="OperationCanceledException"/>) with the
    /// caller's token NOT cancelled — exactly what net-did's webvh client surfaces on a hung host.
    /// </summary>
    private sealed class TimeoutResolver : IDidResolver
    {
        public bool CanResolve(string did) => true;

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
            => throw new TaskCanceledException("simulated downstream HttpClient.Timeout (caller token not cancelled)");
    }

    private static async Task<(TestServer Server, List<UnpackResult> Received)> BuildServerAsync(
        Action<DidCommOptions>? configureOptions = null,
        Func<UnpackResult, CancellationToken, Task>? onReceive = null,
        IDidResolver? resolverOverride = null,
        TimeSpan? rejectionFloor = null)
    {
        var received = new List<UnpackResult>();
        var actors = SpecActorRegistry.LoadDefault();
        IDidResolver resolver = resolverOverride ?? LoadResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISecretsResolver>(actors.AsSecretsResolver());
        builder.Services.AddSingleton<IDidKeyService>(keyService);
        builder.Services.AddSingleton<IServiceEndpointResolver>(serviceResolver);
        builder.Services.AddSingleton<DidCommClient>(sp => new DidCommClient(
            sp.GetRequiredService<ISecretsResolver>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<IServiceEndpointResolver>(),
            sp.GetRequiredService<IOptions<DidCommOptions>>().Value));
        builder.Services.AddOptions<DidCommOptions>().Configure(o =>
        {
            // Default headroom so the round-trip case doesn't trip the 413 limit. The 413 test
            // overrides this via configureOptions below.
            o.MaxReceiveBytes = 64 * 1024;
            configureOptions?.Invoke(o);
        });
        // #35: let a test pin the constant-time rejection floor. When unset, the endpoint falls back
        // to the DidCommReceiveOptions default (5 ms), exercising the production posture.
        if (rejectionFloor is { } floor)
            builder.Services.Configure<DidCommReceiveOptions>(o => o.ReceiveRejectionFloor = floor);

        var app = builder.Build();
        app.UseRouting();
        app.MapDidCommEndpoint("/didcomm", onReceive ?? (async (result, ct) =>
        {
            received.Add(result);
            await Task.CompletedTask;
        }));

        await app.StartAsync();
        return (app.GetTestServer(), received);
    }

    private static async Task<SendResult> SendFromAliceAsync(TestServer server, Message message)
    {
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());

        var services = new ServiceCollection();
        services.AddOptions<HttpTransportOptions>().Configure(opts =>
        {
            opts.AllowedSchemes = new[] { "http", "https" };
            opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            opts.MaxRetryAttempts = 0;
        });
        services.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => server.CreateHandler());
        var sp = services.BuildServiceProvider();

        var transport = new HttpDidCommTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<HttpTransportOptions>>());
        var router = new TransportRouter(new IDidCommTransport[] { transport });

        var client = new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, router, new DidCommOptions());
        // The TestServer is reachable at the root URI, so override the service endpoint. This
        // skips Forward wrapping and sends Bob's authcrypt envelope directly into the receiver
        // — exactly what a direct (non-mediated) FR-TRN-04 path looks like.
        var endpoint = new Uri(new Uri(server.BaseAddress.ToString()), "/didcomm");
        return await client.SendAsync(message, new SendOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice",
            ServiceEndpointOverride: endpoint));
    }

    private static FixtureDidResolver LoadResolver()
    {
        var docs = new Dictionary<string, NetDid.Core.Model.DidDocument>(StringComparer.Ordinal);
        foreach (var file in new[] { "alice.json", "bob.json" })
        {
            var path = Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec", file);
            var doc = NetDid.Core.Serialization.DidDocumentSerializer.Deserialize(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"failed to deserialize {file}");
            docs[doc.Id.Value!] = doc;
        }
        return new FixtureDidResolver(docs);
    }

    private static Message NewProposal() => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .WithBody(System.Text.Json.Nodes.JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
        .Build();
}
