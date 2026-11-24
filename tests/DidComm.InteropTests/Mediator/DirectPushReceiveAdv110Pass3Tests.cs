using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Protocols.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #110, red-team pass 3 (3c), attacked and not broken: the direct push labels every POST
/// <c>application/didcomm-encrypted+json</c>, so a payload that is not a JWE would reach a plain
/// endpoint under the wrong media type. It cannot: the mailbox refuses to store a forward's payload
/// that is not in encrypted form (<c>Dropped-PayloadNotEncrypted</c>, the unpack's own classifier),
/// so nothing is pushed and the recipient's encrypted-only endpoint never reads it.
/// </summary>
public sealed class DirectPushReceiveAdv110Pass3Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_forward_whose_payload_is_not_encrypted_is_never_pushed_with_the_encrypted_media_type(bool signedOnly)
    {
        await using var world = await DirectPushWorld.StartAsync();
        var peer = await world.Mediator.NewPeerAsync();
        var bob = await world.Mediator.NewRecipientAsync();
        await world.Mediator.EnrolAsync(peer, bob.Did);
        await using var inbox = await Inbox.StartAsync(world.Mediator.Network, bob);
        world.Endpoints.Set(peer.Did, inbox.Endpoint);
        var alice = await world.Mediator.NewSenderAsync();

        var note = alice.Note(bob.Did);
        var payload = signedOnly ? await alice.Client.PackSignedAsync(note, alice.Did) : await alice.Client.PackPlaintextAsync(note);
        var (packed, _) = await alice.ForwardAsync(world.Mediator.Did, bob.Did, payload);
        await Qa.PostForwardAsync(world.Mediator, packed);
        await world.Mediator.FlushAsync();
        await MediatorTestServer.WaitUntilAsync(() => world.Mediator.Outcome(ForwardOutcomes.DroppedPayloadNotEncrypted) >= 1, "the forward was dropped");
        await world.Mediator.PushesIdleAsync();

        world.Mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedPayloadNotEncrypted);
        world.Mediator.PushOutcomes.Should().BeEmpty("nothing was stored, so nothing is pushed");
        inbox.ContentTypes.Should().BeEmpty("no POST reached the recipient");
        inbox.Read.Should().BeEmpty();
        (await world.StoredAsync(peer.Did)).Should().Be(0);
    }

    /// <summary>A recipient's own plain endpoint at <c>http://recipient.test/inbox</c> (callback overload) whose accepted list holds one entry.</summary>
    private sealed class Inbox : IAsyncDisposable
    {
        private const string Host = "recipient.test";
        private readonly WebApplication _app;

        private Inbox(WebApplication app) => _app = app;

        public ConcurrentQueue<UnpackResult> Read { get; } = new();

        public ConcurrentQueue<string?> ContentTypes { get; } = new();

        public Uri Endpoint => new($"http://{Host}/inbox");

        public static async Task<Inbox> StartAsync(MediatorNetwork network, TestPeer recipient)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{Host}/"));
            builder.Logging.ClearProviders();
            builder.Services.AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(recipient.Secrets);
            });
            builder.Services.Configure<DidCommReceiveOptions>(x => x.AcceptedMediaTypes = new[] { DidCommMediaTypes.Encrypted });
            var app = builder.Build();
            var inbox = new Inbox(app);
            app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                inbox.ContentTypes.Enqueue(context.Request.ContentType);
                await next(context);
            });
            app.MapDidCommEndpoint("/inbox", (unpacked, _) =>
            {
                inbox.Read.Enqueue(unpacked);
                return Task.CompletedTask;
            });
            await app.StartAsync();
            network.Add(app.GetTestServer());
            return inbox;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
