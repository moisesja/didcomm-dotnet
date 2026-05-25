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
    }

    private static async Task<(TestServer Server, List<UnpackResult> Received)> BuildServerAsync(Action<DidCommOptions>? configureOptions = null)
    {
        var received = new List<UnpackResult>();
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadResolver();
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

        var app = builder.Build();
        app.UseRouting();
        app.MapDidCommEndpoint("/didcomm", async (result, ct) =>
        {
            received.Add(result);
            await Task.CompletedTask;
        });

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
