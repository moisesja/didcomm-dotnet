using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Section EE: messages that come back over a WebSocket the client opened. Three views of the same
/// connection, from the inside out:
/// <list type="number">
/// <item><description>On the mediator: a protocol handler that answers a <c>return_route: all</c>
/// request by pushing several messages over the caller's connection (the return-route session).</description></item>
/// <item><description>On the client, by hand: a WebSocket transport with an inbound consumer of your
/// own that sees every message pushed back on the connection.</description></item>
/// <item><description>On the client, the usual way: three set-up calls, then
/// <c>PickupClient.ReceiveLiveAsync</c> receives a mediator's messages as they arrive.</description></item>
/// </list>
/// Maps to PRD §14.2 task EE (FR-RR-03..07, FR-PICK-06..08, FR-PICK-11).
/// </summary>
public static class Section_EE_LiveDeliveryOverWebSocket
{
    // Every wait in this section is on the event the next line reads, with this hang guard.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("EE", "Live delivery over WebSocket");
        var narrator = ctx.Narrator;

        // ── EE.1 The mediator: AddMediator() plus a handler of our own that pushes notices ──────
        narrator.Step("Start a mediator that also serves a small \"notices\" protocol (ASP.NET Core on a loopback port).");

        // The mediator keeps live connections in a session registry. Register one yourself (before
        // AddDidComm) to choose its bounds; this one queues at most 64 open/close notifications for
        // its subscribers. An instance you register is yours to dispose: the container won't.
        using var sessions = new InMemoryReturnRouteSessionRegistry(new ReturnRouteSessionOptions { MaxPendingNotifications = 64 });
        var notices = new NoticeHandler();
        var mediatorSecrets = new InMemorySecretsResolver();

        // A mediator's DID names its own address, and AddMediator() checks that DID before the server
        // listens, so the port must be known first: LoopbackPort finds a free one (production uses
        // its fixed public wss:// address). This DID names a ws:// address, so wallets reach the
        // mediator over a WebSocket without being told to. Plain ws is for this loopback demo only.
        var (app, mediatorDid) = await LoopbackPort.StartAsync(async port =>
        {
            var mediator = await MintAsync(ctx, new DidCommServiceSpec($"ws://127.0.0.1:{port}/didcomm", Accept: new[] { "didcomm/v2" }));
            foreach (var jwk in mediator.Privates) mediatorSecrets.Add(jwk);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders(); // keep this cookbook's console to the narration
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            builder.Services.AddSingleton<IReturnRouteSessionRegistry>(sessions);
            builder.Services.AddSingleton(notices); // AddProtocol<NoticeHandler>() below reuses this instance
            builder.Services.AddDidComm(b => b
                .UseNetDidResolver()
                .UseSecretsResolver(mediatorSecrets)
                // Enrolment is open here for brevity; section FF shows the allow-list a public mediator needs.
                .AddMediator(o => o.CoordinateMediation.RoutingDids.Add(mediator.Did))
                .AddProtocol<NoticeHandler>());
            var built = builder.Build();
            built.UseWebSockets(); // before mapping, or the WebSocket endpoint answers 400
            built.MapDidCommMediator("/didcomm");
            try
            {
                await built.StartAsync();
            }
            catch
            {
                await built.DisposeAsync();
                throw;
            }
            return (built, mediator.Did);
        });

        try
        {
            narrator.Value("[mediator] routing DID", Trunc(mediatorDid));

            // A registry you build yourself starts empty; the default constructor uses the default bounds.
            using (var standalone = new InMemoryReturnRouteSessionRegistry())
                narrator.Value("A fresh session registry holds", standalone.Count);

            await RunHandRolledClientAsync(ctx, mediatorDid, notices, sessions);
            await RunLivePickupAsync(ctx, mediatorDid);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            notices.StopWatching();
        }
    }

    // ── EE.2 By hand: a WebSocket transport and an inbound consumer of your own ─────────────────
    private static async Task RunHandRolledClientAsync(
        CookbookContext ctx, string mediatorDid, NoticeHandler notices, IReturnRouteSessionRegistry sessions)
    {
        var narrator = ctx.Narrator;
        narrator.Step("Bob subscribes with return_route: all over a WebSocket he built by hand; his own consumer reads what comes back.");

        // Bob's keys, and a container only for DID resolution and key lookup; the transport and the
        // client are built by hand below.
        var bobSecrets = new InMemorySecretsResolver(ctx.Bob.Privates);
        var bobServices = new ServiceCollection();
        bobServices.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(bobSecrets));
        await using var bobBase = bobServices.BuildServiceProvider();

        // The SSRF guard refuses private and loopback addresses by default. This demo talks to
        // 127.0.0.1, so it opens exactly that host and nothing else; production keeps the default.
        var bobOptions = new DidCommOptions();
        bobOptions.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1");

        // What Bob's consumer hands over: a bounded channel, so a flood cannot grow memory.
        var received = Channel.CreateBounded<ReceivedFrame>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropWrite });

        // The four-argument constructor takes the consumer. ReceiveInbound = true makes the transport
        // read the connection a return_route: all request opened, and hand every whole message the
        // peer pushes on it to the consumer.
        var transport = new WebSocketDidCommTransport(
            Options.Create(new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws", "wss" }, // production: wss only (the default)
                MaxReconnectAttempts = 0,
                ReceiveInbound = true,
            }),
            NullLogger<WebSocketDidCommTransport>.Instance,
            Options.Create(bobOptions),
            new CollectingFrameConsumer(received.Writer));
        try
        {
            var bobClient = new DidCommClient(
                bobSecrets,
                bobBase.GetRequiredService<IDidKeyService>(),
                bobBase.GetRequiredService<IServiceEndpointResolver>(),
                new TransportRouter(new IDidCommTransport[] { transport }),
                bobOptions);

            // return_route: all asks the mediator to send messages for Bob back over this connection.
            // It is honoured only for an authenticated (authcrypt) sender, which SendAsync's From gives.
            var subscribe = new MessageBuilder()
                .WithType(NoticeHandler.SubscribeType)
                .WithFrom(ctx.Bob.Did)
                .WithTo(mediatorDid)
                .WithBody(new JsonObject { ["topic"] = "weather" })
                .WithReturnRoute(ReturnRouteMode.All)
                .Build();
            await bobClient.SendAsync(subscribe, new SendOptions(new[] { mediatorDid }, ctx.Bob.Did));

            // What the mediator's handler saw and did (it runs on the server; we read its report here).
            var report = await AwaitOrFailAsync(notices.Report, "the notices handler");
            narrator.Value("[mediator] context.ReturnRoute == (All, no thread)", report.HeaderIsAll);
            narrator.Value("[mediator] context.ReturnRoute == ReturnRouteHeader.None", report.HeaderIsNone);
            narrator.Value("[mediator] ReturnRouteHeader.TryRead / Read", $"{report.TryReadOk} / {report.ReadMode}");
            narrator.Value("[mediator] Parsed header Mode / Thread", $"{report.ParsedMode} / {report.ParsedThread ?? "<none>"}");
            narrator.Value("[mediator] Handler saw a session", $"Transport = {report.Transport}, IsLive = {report.IsLive}, Mode = {report.Mode}");
            narrator.Value("[mediator] Session.PeerDid is Bob", report.PeerDid == ctx.Bob.Did);
            narrator.Value("[mediator] Session.LocalDid is the mediator", report.LocalDid == mediatorDid);
            narrator.Value("[mediator] Session.Thread", report.Thread ?? "<none>");
            narrator.Value("[mediator] Session closed yet", report.ClosedAtStart);
            narrator.Value("[mediator] TrySendAsync(notice)", report.FirstSent);
            narrator.Value("[mediator] TrySendAsync(notice, 256 bytes)", report.TooLarge);
            narrator.Note("TooLarge: nothing was sent. The session measures the packed bytes, so the bound is exact.");
            narrator.Value("[mediator] TrySendAsync(notice, 64 KiB)", report.Bounded);

            // Bob's consumer got the two notices the session sent (the oversized one never left).
            for (var i = 1; i <= 2; i++)
            {
                var frame = await AwaitOrFailAsync(received.Reader.ReadAsync().AsTask(), $"pushed notice {i}");
                narrator.Value($"[bob] Frame {i} came in on", frame.Endpoint);
                narrator.Value($"[bob] Frame {i} LocalDid is Bob / PeerDid is the mediator", $"{frame.LocalDid == ctx.Bob.Did} / {frame.PeerDid == mediatorDid}");
                narrator.Value($"[bob] Frame {i} payload bytes", frame.Payload.Length);

                // The frame's DIDs only say which connection it came in on. Only the authcrypt envelope
                // proves who sent it, so unpack and check the sender before trusting the content.
                var opened = await bobClient.UnpackAsync(Encoding.UTF8.GetString(frame.Payload));
                var fromPeer = opened.Authenticated
                    && opened.SenderKid is { } kid
                    && kid.StartsWith(frame.PeerDid + "#", StringComparison.Ordinal);
                narrator.Value($"[bob] Frame {i} authcrypted by the mediator", fromPeer);
                narrator.Value($"[bob] Frame {i} text", opened.Message.Body?["text"]?.GetValue<string>());
            }

            // While the connection is open, the mediator can find Bob's session by his DID.
            narrator.Value("[mediator] Sessions for Bob", sessions.Find(ctx.Bob.Did).Count);
        }
        finally
        {
            // Closing the transport closes the connection, and with it the mediator's session.
            await transport.DisposeAsync();
        }

        await AwaitOrFailAsync(notices.SessionClosed, "the session's Closed token");
        narrator.Value("Session closed (its Closed token fired)", true);
        narrator.Note("The registry's SessionOpened / SessionClosed events are best-effort notifications; the session's Closed token is the authoritative end signal, so key per-session state on it.");
    }

    // ── EE.3 The usual way: three set-up calls, then ReceiveLiveAsync ────────────────────────────
    private static async Task RunLivePickupAsync(CookbookContext ctx, string mediatorDid)
    {
        var narrator = ctx.Narrator;
        narrator.Step("Bob's wallet: three set-up calls, enrol, then receive live.");

        var bobSecrets = new InMemorySecretsResolver(ctx.Bob.Privates);
        var walletServices = new ServiceCollection();
        walletServices.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(bobSecrets)
            // 1. A WebSocket transport that reads the connection back (production: wss only)...
            .UseWebSocketTransport(o =>
            {
                o.AllowedSchemes = new[] { "ws", "wss" };
                o.ReceiveInbound = true;
            })
            // 2. ...hands what arrives to the built-in consumer, which checks it came from the
            //    mediator to Bob, applies replay protection and dispatches it...
            .UseInboundDispatch()
            // 3. ...to the mediator clients, which match answers and pushes to what Bob is waiting for.
            //    A short poll interval suits this demo (see the note on polling below).
            .AddMediatorClient(o => o.LivePollInterval = TimeSpan.FromSeconds(10))
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")));
        await using var wallet = walletServices.BuildServiceProvider();
        var mediation = wallet.GetRequiredService<MediationClient>();
        var pickup = wallet.GetRequiredService<PickupClient>();

        // Enrol (section CC tells this part in full): a grant, then a public DID routed through the
        // mediator, registered so the mediator accepts forwards for it.
        var grant = await mediation.RequestMediationAsync(mediatorDid, ctx.Bob.Did)
            ?? throw new InvalidOperationException("The mediator denied mediation.");
        var bobPublic = await MintAsync(ctx, new DidCommServiceSpec(grant.RoutingDids[0]));
        // Added while nothing is being unpacked: this sample key store is add-then-read only.
        foreach (var jwk in bobPublic.Privates) bobSecrets.Add(jwk);
        var registered = await mediation.UpdateRecipientsAsync(mediatorDid, ctx.Bob.Did,
            new[] { new RecipientUpdate(bobPublic.Did, RecipientUpdateAction.Add) });
        narrator.Value("[bob] public DID registered", registered[0].Result);

        // Alice writes to Bob's public DID. SendAsync sees it is reached through the mediator and
        // wraps the message in a forward for it (section O); she needs a WebSocket transport only
        // because the mediator's DID names a ws:// address.
        var aliceSecrets = new InMemorySecretsResolver(ctx.Alice.Privates);
        var aliceServices = new ServiceCollection();
        aliceServices.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(aliceSecrets)
            .UseWebSocketTransport(o => o.AllowedSchemes = new[] { "ws", "wss" })
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")));
        await using var aliceServiceProvider = aliceServices.BuildServiceProvider();
        var alice = aliceServiceProvider.GetRequiredService<DidCommClient>();
        async Task SendToBobAsync(string content)
        {
            var note = new MessageBuilder()
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithFrom(ctx.Alice.Did)
                .WithTo(bobPublic.Did)
                .WithBody(new JsonObject { ["content"] = content })
                .Build();
            await alice.SendAsync(note, new SendOptions(new[] { bobPublic.Did }, ctx.Alice.Did));
        }

        // Live delivery can be switched by hand. ReceiveLiveAsync below does both for you; use these
        // only when you handle the pushes some other way.
        var on = await pickup.SetLiveDeliveryAsync(mediatorDid, ctx.Bob.Did, true);
        var off = await pickup.SetLiveDeliveryAsync(mediatorDid, ctx.Bob.Did, false);
        narrator.Value("[bob] SetLiveDeliveryAsync(true / false) -> live", $"{on.LiveDelivery} / {off.LiveDelivery}");

        await SendToBobAsync("Hello Bob");

        // The stream turns live delivery on, takes what is already queued, then yields each message
        // the mediator pushes. A message may arrive either way; the stream does not care which.
        using var stop = new CancellationTokenSource();
        var live = pickup.ReceiveLiveAsync(mediatorDid, ctx.Bob.Did, ct: stop.Token).GetAsyncEnumerator();
        PickupItem last;
        try
        {
            var first = await NextLiveAsync(live, stop, "Bob's first live item");
            narrator.Value("[bob] live", first.Unpacked?.Message.Body?["content"]?.GetValue<string>());

            await SendToBobAsync("Hello again");
            // Asking for the next item acknowledges the previous one.
            last = await NextLiveAsync(live, stop, "Bob's second live item");
            narrator.Value("[bob] live", last.Unpacked?.Message.Body?["content"]?.GetValue<string>());
        }
        finally
        {
            // Ending the enumeration (here, like a break) acknowledges what was taken and turns live
            // delivery off. The last item is NOT acknowledged: the stream cannot tell whether you
            // handled it, so the mediator would deliver it again later.
            await live.DisposeAsync();
        }

        // To discard the last item, acknowledge it yourself.
        var left = await pickup.AckAsync(mediatorDid, ctx.Bob.Did, new[] { last.Id });
        narrator.Value("[bob] Messages left after discarding the last one", left.MessageCount);

        narrator.Note("A live item is acknowledged when you ask for the next one; AckAsync(item.Id) discards the last one.");
        narrator.Note("Delivery is at-least-once: a message can come twice (after a dropped connection or an expired lease). The stream skips repeats it remembers; de-duplicate by message id too.");
        narrator.Note("Polling in live mode is permanent: the next poll comes one LivePollInterval after the previous drain ends. A delayed message that becomes due is not pushed; the next poll picks it up.");
        narrator.Note("So keep LivePollInterval at or below the longest delay your senders ask for (this library's mediator caps a delay at 1 hour by default).");
    }

    private static async Task<PickupItem> NextLiveAsync(IAsyncEnumerator<PickupItem> live, CancellationTokenSource stop, string what)
    {
        var move = live.MoveNextAsync().AsTask();
        if (await Task.WhenAny(move, Task.Delay(HangGuard)) != move)
        {
            await stop.CancelAsync(); // end the pending move before the stream is disposed
            throw new TimeoutException($"Timed out waiting for {what}.");
        }
        if (!await move)
            throw new InvalidOperationException($"The live stream ended before {what}.");
        var item = live.Current;
        if (item.Error is not null)
            throw new InvalidOperationException($"{what} did not unpack: {item.Error.GetType().Name}.");
        return item;
    }

    private static async Task<T> AwaitOrFailAsync<T>(Task<T> task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(HangGuard)) != task)
            throw new TimeoutException($"Timed out waiting for {what}.");
        return await task;
    }

    private static async Task AwaitOrFailAsync(Task task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(HangGuard)) != task)
            throw new TimeoutException($"Timed out waiting for {what}.");
        await task;
    }

    private static Task<PeerIdentity> MintAsync(CookbookContext ctx, DidCommServiceSpec? service)
        => PeerIdentityFactory.CreateAsync(
            ctx.ServiceProvider.GetRequiredService<IDidManager>(),
            ctx.ServiceProvider.GetRequiredService<IKeyGenerator>(),
            ctx.ServiceProvider.GetRequiredService<ICryptoProvider>(),
            service);

    private static string Trunc(string value) => value.Length <= 56 ? value : value[..55] + "…";

    /// <summary>What Bob's consumer keeps of one pushed message.</summary>
    private sealed record ReceivedFrame(Uri Endpoint, string LocalDid, string PeerDid, byte[] Payload);

    /// <summary>
    /// A minimal <see cref="IInboundFrameConsumer"/>: copies each frame into a bounded channel and
    /// returns at once. The transport reads the connection's next message only after this returns,
    /// so a consumer must never wait here for another message on the same connection. It takes no
    /// <see cref="DidCommClient"/>: the transport is a dependency of the client, so the section
    /// unpacks outside the consumer.
    /// </summary>
    private sealed class CollectingFrameConsumer(ChannelWriter<ReceivedFrame> received) : IInboundFrameConsumer
    {
        public ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct)
        {
            // Payload is untrusted bytes from the network, at most the client's receive cap.
            // LocalDid / PeerDid are the transport's attribution: the DIDs of the send that opened
            // the connection, not proof of who sent this message.
            received.TryWrite(new ReceivedFrame(frame.Endpoint, frame.LocalDid, frame.PeerDid, frame.Payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>What the notices handler observed, for the section to print.</summary>
    private sealed record NoticeReport(
        bool HeaderIsAll,
        bool HeaderIsNone,
        bool TryReadOk,
        ReturnRouteMode ReadMode,
        ReturnRouteMode ParsedMode,
        string? ParsedThread,
        string PeerDid,
        string LocalDid,
        ReturnRouteMode Mode,
        string? Thread,
        ReturnRouteTransport Transport,
        bool IsLive,
        bool ClosedAtStart,
        bool FirstSent,
        ReturnRouteSendResult TooLarge,
        ReturnRouteSendResult Bounded);

    /// <summary>
    /// A toy protocol on the mediator: a <c>subscribe</c> that carries <c>return_route: all</c> gets
    /// its notices pushed back over the subscriber's own connection, through the return-route
    /// session the endpoint opened for it.
    /// </summary>
    private sealed class NoticeHandler : IProtocolHandler
    {
        public const string ProtocolUriValue = "https://example.org/notices/1.0";
        public const string SubscribeType = ProtocolUriValue + "/subscribe";
        public const string NoticeType = ProtocolUriValue + "/notice";

        private readonly TaskCompletionSource<NoticeReport> _report = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _closedRegistration;

        public string ProtocolUri => ProtocolUriValue;

        public Task<NoticeReport> Report => _report.Task;

        public Task SessionClosed => _closed.Task;

        public void StopWatching() => _closedRegistration.Dispose();

        public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            if (!string.Equals(message.Type, SubscribeType, StringComparison.Ordinal))
                return null;

            // The session exists only for an authcrypted message that asked for return_route: all
            // (and passed replay protection). Without one, there is no connection to push on.
            if (context.Session is not { } session)
            {
                _report.TrySetException(new InvalidOperationException("The subscribe arrived without a return-route session."));
                return null;
            }

            // The header as the dispatcher read it, and the same header read from the message.
            var header = context.ReturnRoute;
            var tryReadOk = ReturnRouteHeader.TryRead(message, out var parsed);

            // Pushes must be addressed from the session's LocalDid to exactly its PeerDid; the session
            // authcrypts them, so a hijacked connection only ever sees ciphertext.
            var first = await session.TrySendAsync(Notice(session, "Notice 1: subscribed to weather"), ct);
            var tooLarge = await session.TrySendAsync(Notice(session, new string('x', 2048)), 256, ct);
            var bounded = await session.TrySendAsync(Notice(session, "Notice 2: sunny"), 64 * 1024, ct);

            // The session's Closed token is cancelled when the connection closes or is evicted.
            // The callback must be short and must not throw; dispose the registration when done.
            // This demo has one subscriber; a real handler keeps its per-session state keyed by the
            // session and drops it when that session's Closed token fires.
            _closedRegistration = session.Closed.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), _closed);

            _report.TrySetResult(new NoticeReport(
                HeaderIsAll: header == new ReturnRouteHeader(ReturnRouteMode.All, null),
                HeaderIsNone: header == ReturnRouteHeader.None,
                TryReadOk: tryReadOk,
                ReadMode: ReturnRouteHeader.Read(message).Mode,
                ParsedMode: parsed.Mode,
                ParsedThread: parsed.Thread,
                PeerDid: session.PeerDid,
                LocalDid: session.LocalDid,
                Mode: session.Mode,
                Thread: session.Thread,
                Transport: session.Transport,
                IsLive: session.IsLive,
                ClosedAtStart: session.Closed.IsCancellationRequested,
                FirstSent: first,
                TooLarge: tooLarge,
                Bounded: bounded));
            return null; // nothing more to answer: the notices went out on the session
        }

        private static Message Notice(IReturnRouteSession session, string text)
            => new MessageBuilder()
                .WithType(NoticeType)
                .WithFrom(session.LocalDid)
                .WithTo(session.PeerDid)
                .WithBody(new JsonObject { ["text"] = text })
                .Build();
    }
}
