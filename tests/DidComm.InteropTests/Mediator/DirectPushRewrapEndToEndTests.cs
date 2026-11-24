using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Transports;
using DidComm.Mediator;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
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
/// #110, owner decision OD-3, end to end: our mediator (<c>AddMediator</c> with
/// <c>DirectPush</c> and <see cref="RewrapMode.ReanoncryptToNext"/>) pushes the forward it built
/// for the recipient to the recipient's own plain <c>MapDidCommEndpoint</c>, which opens it and hands
/// the sender's message to its handler (or callback); the mailbox ends empty. And the mediator's own
/// endpoints are unchanged: <c>MapDidCommMediator</c> still leaves a forward addressed to the
/// mediator itself to its <see cref="ForwardHandler"/>, which opens it under its own rules (here: the
/// earliest expiry of every layer bounds the stored message, FR-ROUTE-09).
/// </summary>
public sealed class DirectPushRewrapEndToEndTests
{
    public enum RecipientEndpoint
    {
        Registry,
        Callback,
    }

    [Theory]
    [InlineData(RecipientEndpoint.Registry)]
    [InlineData(RecipientEndpoint.Callback)]
    public async Task Our_mediator_in_rewrap_mode_pushes_to_a_plain_endpoint_which_opens_it_for_its_handler_and_the_mailbox_ends_empty(RecipientEndpoint endpoint)
    {
        await using var world = await DirectPushWorld.StartAsync(o => o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext);
        var peer = await world.Mediator.NewPeerAsync();
        var bob = await world.Mediator.NewRecipientAsync();
        await world.Mediator.EnrolAsync(peer, bob.Did);
        await using var inbox = await PlainRecipient.StartAsync(world.Mediator.Network, bob);
        world.Endpoints.Set(peer.Did, endpoint == RecipientEndpoint.Registry ? inbox.RegistryEndpoint : inbox.CallbackEndpoint);
        var alice = await world.Mediator.NewSenderAsync();

        var note = await Qa.SendNoteAsync(world.Mediator, alice, bob);
        await world.OutcomesAsync(PushOutcomes.PushedDirect, 1);

        world.Mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedDirect);
        // What was pushed is the mediator's own forward, addressed to Bob himself (routing.md L71).
        var pushed = await bob.Client.UnpackAsync(Encoding.UTF8.GetString(inbox.Bodies.Should().ContainSingle().Subject));
        pushed.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri);
        pushed.AnonymousSender.Should().BeTrue();
        ForwardMessage.TryParse(pushed.Message, out var next, out _).Should().BeTrue();
        next.Should().Be(bob.Did, "the outer envelope is a forward whose next is the receiver itself");
        // The endpoint opened it: Bob's handler (or callback) read Alice's message, under her own envelope.
        var read = inbox.Read.Should().ContainSingle("the recipient read the message inside, once").Subject;
        read.Message.Id.Should().Be(note.Id);
        read.Message.Type.Should().Be(note.Type);
        alice.Peer.Kids.Should().Contain(read.SenderKid, "authcrypted by Alice: a layer adds no sender");
        (await world.StoredAsync(peer.Did)).Should().Be(0, "a 2xx removes it from the mailbox, and it was read");
        (await world.Mediator.StatusAsync(peer)).MessageCount.Should().Be(0);
        world.Mediator.LeakingLogLines(bob.Did).Should().BeEmpty();
        inbox.LeakingLogLines(bob, alice.Peer, world.Mediator.Did).Should().BeEmpty();
    }

    public enum MediatorTransport
    {
        Http,
        WebSocket,
    }

    [Theory]
    [InlineData(MediatorTransport.Http)]
    [InlineData(MediatorTransport.WebSocket)]
    public async Task MapDidCommMediator_still_leaves_a_forward_addressed_to_the_mediator_itself_to_its_forward_handler(MediatorTransport transport)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (forBob, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        var expires = mediator.Clock.Now.AddSeconds(90);
        var (selfAddressed, _) = await alice.ForwardAsync(mediator.Did, mediator.Did, forBob, Qa.Expires(expires));

        if (transport == MediatorTransport.Http)
        {
            (await mediator.PostAsync(selfAddressed)).Status.Should().Be(HttpStatusCode.Accepted);
        }
        else
        {
            using var socket = await mediator.ConnectAsync();
            await ReturnRouteTestHost.SendAsync(socket, selfAddressed);
            // Either event ends the wait: the forward handler counted it, or the endpoint discarded it.
            await MediatorTestServer.WaitUntilAsync(
                () => mediator.Outcomes.Count > 0 || mediator.Logs.Entries.Any(e => e.StartsWith("MapDidCommWebSocket: discarding", StringComparison.Ordinal)),
                "the mediator handled the forward");
        }
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued }, "the forward handler opened the layer and queued the forward inside");
        var now = mediator.Clock.Now;
        var stored = (await mediator.Store.LeaseAsync(
            new MessageLeaseRequest(bob.Did, null, 10, long.MaxValue, now, new MessageLease("test", now.AddMinutes(1))), CancellationToken.None))
            .Should().ContainSingle().Subject;
        // The first expired instant of the received layer's expires_time (the store's form: one
        // second after it, plus the expiry skew): the inner forward has none of its own.
        var skew = mediator.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DidCommOptions>>().Value.ExpiresClockSkew;
        stored.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(expires.ToUnixTimeSeconds() + (long)Math.Floor(skew.TotalSeconds) + 1),
            "the handler's own rule: the earliest expiry of the received forward and every layer it opened bounds the message (FR-ROUTE-09)");
        mediator.LeakingLogLines().Should().BeEmpty();
    }
}

/// <summary>
/// A recipient's own plain endpoint on <c>recipient.test</c>: <c>AddDidComm</c> with the
/// recipient's keys and a Basic Message handler, the registry-aware <c>MapDidCommEndpoint</c> at
/// <c>/didcomm</c> and the callback overload at <c>/inbox</c>. Records every POST body as it
/// arrives and every message its handler or callback read.
/// </summary>
internal sealed class PlainRecipient : IAsyncDisposable
{
    public const string Host = "recipient.test";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<byte[]> _bodies = new();

    private PlainRecipient(WebApplication app, PlainRecipientState state, CapturingLoggerProvider logs)
    {
        _app = app;
        State = state;
        Logs = logs;
    }

    public PlainRecipientState State { get; }

    public CapturingLoggerProvider Logs { get; }

    public Uri RegistryEndpoint => new($"http://{Host}/didcomm");

    public Uri CallbackEndpoint => new($"http://{Host}/inbox");

    /// <summary>Every POST body, as it arrived.</summary>
    public IReadOnlyList<byte[]> Bodies => _bodies.ToArray();

    /// <summary>What the handler or the callback read.</summary>
    public IReadOnlyList<UnpackResult> Read => State.Read.ToArray();

    public static async Task<PlainRecipient> StartAsync(MediatorNetwork network, TestPeer recipient)
    {
        var state = new PlainRecipientState();
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{Host}/"));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(state);
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(recipient.Secrets);
            b.AddProtocol<BasicMessageReader>();
        });
        var app = builder.Build();
        PlainRecipient? self = null;
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            context.Request.EnableBuffering();
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            self!._bodies.Enqueue(ms.ToArray());
            context.Request.Body.Position = 0;
            await next(context);
        });
        app.MapDidCommEndpoint("/didcomm");
        app.MapDidCommEndpoint("/inbox", (unpacked, _) =>
        {
            state.Read.Enqueue(unpacked);
            return Task.CompletedTask;
        });
        self = new PlainRecipient(app, state, logs);
        await app.StartAsync();
        network.Add(app.GetTestServer());
        return self;
    }

    /// <summary>Log lines of this endpoint that carry a DID or key id of <paramref name="peers"/> or <paramref name="extra"/> (must be empty).</summary>
    public IReadOnlyList<string> LeakingLogLines(TestPeer bob, TestPeer alice, params string[] extra)
    {
        var sensitive = new[] { bob.Did, alice.Did }.Concat(bob.Kids).Concat(alice.Kids).Concat(extra).ToArray();
        return Logs.Entries.Where(line => sensitive.Any(v => line.Contains(v, StringComparison.Ordinal))).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>What the recipient read.</summary>
internal sealed class PlainRecipientState
{
    public ConcurrentQueue<UnpackResult> Read { get; } = new();
}

/// <summary>The recipient's Basic Message 2.0 handler: records what it read, replies nothing.</summary>
internal sealed class BasicMessageReader(PlainRecipientState state) : IProtocolHandler
{
    public string ProtocolUri => "https://didcomm.org/basicmessage/2.0";

    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        state.Read.Enqueue(context.Received);
        return Task.FromResult<Message?>(null);
    }
}
