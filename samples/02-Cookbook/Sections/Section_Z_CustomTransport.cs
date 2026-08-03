using System.Text;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Teaches the library a new way to move bytes. A transport is three members —
/// <c>Scheme</c>, <c>CanHandle</c>, <c>SendAsync</c> — and once registered, the
/// <c>TransportRouter</c> picks it automatically whenever a recipient's endpoint URI matches
/// its scheme, exactly the way the built-in HTTP and WebSocket transports are picked. The
/// section implements a queue-backed transport for a made-up <c>memq://</c> scheme and drives
/// a full <c>SendAsync</c> through it.
/// </summary>
/// <remarks>
/// <para>
/// The section also shows the router saying no: sending to an <c>https://</c> endpoint when
/// only the <c>memq</c> transport is registered fails with an error naming the unhandled
/// scheme. And note the cookbook has been eating this dog food all along —
/// <c>LoopbackTransport</c>, registered in <see cref="CookbookContext"/>, is itself a custom
/// <c>IDidCommTransport</c> (scheme <c>loopback</c>) that sections S and T ride.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>Z</strong> (FR-TRN-01 — transport extension point).
/// </para>
/// </remarks>
public static class Section_Z_CustomTransport
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("Z", "Custom transport (IDidCommTransport + TransportRouter)");

        // The custom transport (defined below) and a router that knows about it. In DI you'd
        // register it once — services.AddSingleton<IDidCommTransport, MemoryQueueTransport>() —
        // the way CookbookContext registers LoopbackTransport; here the pieces are built by
        // hand so the routing is visible.
        var transport = new MemoryQueueTransport();
        var router = new TransportRouter(new IDidCommTransport[] { transport });

        var sp = ctx.ServiceProvider;
        var sectionClient = new DidCommClient(
            sp.GetRequiredService<ISecretsResolver>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<IServiceEndpointResolver>(),
            router,
            new DidCommOptions());

        ctx.Narrator.Step("SendAsync to a memq:// endpoint — the router matches the scheme to the custom transport.");
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Delivered over a transport we just invented."}""")!.AsObject())
            .Build();

        var sent = await sectionClient.SendAsync(message, new SendOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did,
            ServiceEndpointOverride: new Uri("memq://bob-inbox")));

        ctx.Narrator.Value("EndpointUsed", sent.EndpointUsed);
        ctx.Narrator.Value("Transport.Accepted", sent.Transport.Accepted);
        ctx.Narrator.Value("Queue depth", transport.Delivered.Count);
        // The TransportRequest your transport receives is endpoint + payload + media type — the
        // endpoint is the exact URI the router matched your scheme against.
        ctx.Narrator.Value("Delivered endpoint", transport.Delivered[0].Endpoint);
        ctx.Narrator.Value("Delivered media type", transport.Delivered[0].MediaType);

        // The bytes the transport carried are a normal packed envelope — Bob unpacks them
        // with the shared client as if they had arrived over HTTP.
        var bobView = await ctx.Client.UnpackAsync(Encoding.UTF8.GetString(transport.Delivered[0].Payload.Span));
        ctx.Narrator.Value("ContentReceivedByBob", bobView.Message.Body?["content"]?.GetValue<string>());

        // Scheme dispatch cuts both ways: this router holds only the memq transport, so an
        // https endpoint has no taker and the send fails with the offending scheme named.
        ctx.Narrator.Step("An endpoint scheme no registered transport handles is refused.");
        try
        {
            await sectionClient.SendAsync(message, new SendOptions(
                Recipients: new[] { ctx.Bob.Did },
                From: ctx.Alice.Did,
                ServiceEndpointOverride: new Uri("https://agents.example/inbox")));
            ctx.Narrator.Note("UNEXPECTED: the unhandled scheme was not refused.");
        }
        catch (TransportException ex)
        {
            ctx.Narrator.Note($"Refused as designed: {ex.Message}");
        }

        ctx.Narrator.Note("LoopbackTransport in this very cookbook is the DI-registered flavor of the same extension point — sections S and T ride it.");
    }

    /// <summary>
    /// A complete custom transport: bind a scheme, accept matching endpoints, move the bytes.
    /// This one appends to an in-memory queue; a real one would speak Bluetooth, libp2p,
    /// a message bus — anything that can carry an opaque payload (FR-TRN-01). Transports are
    /// delivery-only: they report acceptance, never a protocol reply (FR-TRN-03).
    /// </summary>
    private sealed class MemoryQueueTransport : IDidCommTransport
    {
        /// <summary>Everything "delivered" so far — the section reads it back as Bob.</summary>
        public List<TransportRequest> Delivered { get; } = new();

        public string Scheme => "memq";

        public bool CanHandle(Uri endpoint) =>
            string.Equals(endpoint.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Delivered.Add(request);
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: null));
        }
    }
}
