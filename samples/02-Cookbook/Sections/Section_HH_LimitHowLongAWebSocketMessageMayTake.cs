using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// How long a peer has to deliver each WebSocket message. Both WebSocket sides give every message a
/// time limit, <c>MessageReceiveTimeout</c>: 60 seconds by default, counted from the message's first
/// part to its last frame. A message that does not arrive whole in time is dropped and the connection
/// is closed with status 1008 (policy violation).
/// </summary>
/// <remarks>
/// <para>
/// <b>On your endpoint</b> (<c>MapDidCommWebSocket</c>, and the WebSocket endpoint of
/// <c>MapDidCommMediator</c>), a new connection must also finish its first message within the same time
/// of being accepted, so a connection that never sends anything is closed too. After the first message,
/// a connection waiting for its next one is never timed: return-route sessions and live delivery stay
/// open while they are idle. Set it with
/// <c>AddOptions&lt;DidCommReceiveOptions&gt;().Configure(o =&gt; o.MessageReceiveTimeout = ...)</c>.
/// </para>
/// <para>
/// <b>On the client transport</b> (<c>UseWebSocketTransport(o =&gt; o.MessageReceiveTimeout = ...)</c>),
/// the same limit applies to each message a peer pushes back on a return-route session socket. The
/// client accepts no connections, so only a message that has started is timed there.
/// </para>
/// <para>
/// <see cref="Timeout.InfiniteTimeSpan"/> turns the limit off. What that costs: a peer can then keep a
/// connection open with a message it never finishes, and keep what it sent of it in memory, for as long
/// as the connection lasts. If your peers are on slow links, raise the limit instead: 60 seconds is
/// about 17 KB/s for a message of 1 MiB, the default receive size limit.
/// </para>
/// <para>
/// The section runs offline: Bob's endpoint is an in-process test server with a 1-second limit, so
/// the closes happen quickly. It takes about four seconds.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>HH</strong> (FR-TRN-14).
/// </para>
/// </remarks>
public static class Section_HH_LimitHowLongAWebSocketMessageMayTake
{
    /// <summary>The limit on Bob's endpoint in this section: short, so the demonstration is quick.</summary>
    private static readonly TimeSpan DemoLimit = TimeSpan.FromSeconds(1);

    /// <summary>How long the second connection sits idle between two messages: longer than the limit.</summary>
    private static readonly TimeSpan IdleLongerThanTheLimit = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The longest any wait in this section may take before the section gives up with an error. A
    /// working endpoint answers each wait within the limit, so this only stops a broken run from hanging.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("HH", "Limit how long a WebSocket message may take");

        // 1. The setting. The same rule applies to both settings: greater than zero, or
        //    Timeout.InfiniteTimeSpan for no limit. A value outside it is refused when it is set,
        //    so a bad configuration fails at start-up rather than closing every connection.
        ctx.Narrator.Step("One setting, MessageReceiveTimeout: on DidCommReceiveOptions for your endpoint, on WebSocketTransportOptions for the client transport.");
        var options = new DidCommReceiveOptions();
        ctx.Narrator.Value("DidCommReceiveOptions.MessageReceiveTimeout (default)", options.MessageReceiveTimeout);
        options.MessageReceiveTimeout = Timeout.InfiniteTimeSpan;
        ctx.Narrator.Value("Timeout.InfiniteTimeSpan accepted (turns the limit off)", options.MessageReceiveTimeout == Timeout.InfiniteTimeSpan);
        try
        {
            options.MessageReceiveTimeout = TimeSpan.Zero;
            ctx.Narrator.Note("UNEXPECTED: a zero limit was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
            ctx.Narrator.Value("TimeSpan.Zero", "refused (ArgumentOutOfRangeException)");
        }

        // 2. Bob's endpoint, with a 1-second limit for this demonstration.
        ctx.Narrator.Step($"Bob maps MapDidCommWebSocket with MessageReceiveTimeout = {DemoLimit.TotalSeconds:0} s (the default is 60 s).");
        var received = Channel.CreateUnbounded<UnpackResult>();
        await using var bob = await StartBobAsync(ctx, received.Writer);
        var server = bob.GetTestServer();
        var endpoint = new UriBuilder(server.BaseAddress) { Scheme = "ws", Path = "/ws/didcomm" }.Uri;
        var sockets = server.CreateWebSocketClient();

        // 2a. A connection that sends nothing: its first message's time started when Bob's endpoint
        //     accepted it, so it is closed when that time is up.
        ctx.Narrator.Step("A connection that sends nothing is closed when the limit, counted from the moment it was accepted, runs out.");
        using (var silent = await ConnectAsync(sockets, endpoint))
        {
            var (status, description) = await ReadCloseAsync(silent);
            ctx.Narrator.Value("CloseStatus", $"{(int?)status} ({status})");
            ctx.Narrator.Value("CloseStatusDescription", description);
        }

        // 2b. A connection whose first message arrives in time. After it, waiting is never timed:
        //     the connection sits idle for longer than the limit and still takes the next message.
        //     The messages are packed before connecting, so the first one goes out at once.
        var first = await PackAsync(ctx, "Section HH: the first message, sent at once.");
        var second = await PackAsync(ctx, "Section HH: the next message, after an idle wait longer than the limit.");
        var unfinished = await PackAsync(ctx, "Section HH: a message that stops after its first part.");

        ctx.Narrator.Step("A connection that sends its first message at once, waits, then sends another.");
        using var socket = await ConnectAsync(sockets, endpoint);
        await socket.SendAsync(new ArraySegment<byte>(first), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        ctx.Narrator.Value("Bob received", Content(await NextAsync(received.Reader)));

        await Task.Delay(IdleLongerThanTheLimit);
        ctx.Narrator.Note($"the connection was idle for {IdleLongerThanTheLimit.TotalSeconds:0.0} s, longer than the limit: waiting between messages is not timed.");
        await socket.SendAsync(new ArraySegment<byte>(second), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        ctx.Narrator.Value("Bob received", Content(await NextAsync(received.Reader)));

        // 2c. A later message is timed from its first part. This one sends half of the message and
        //     then nothing more, so Bob's endpoint closes the connection when its time is up and
        //     drops the half it read.
        ctx.Narrator.Step("The same connection starts a third message and never finishes it.");
        await socket.SendAsync(new ArraySegment<byte>(unfinished, 0, unfinished.Length / 2), WebSocketMessageType.Text, endOfMessage: false, CancellationToken.None);
        var (stalledStatus, stalledDescription) = await ReadCloseAsync(socket);
        ctx.Narrator.Value("CloseStatus", $"{(int?)stalledStatus} ({stalledStatus})");
        ctx.Narrator.Value("CloseStatusDescription", stalledDescription);
        ctx.Narrator.Value("The unfinished message reached Bob", received.Reader.TryRead(out _));

        // 3. The client transport: the same setting, for each message a peer pushes back on a
        //    return-route session socket this agent opened (see section EE). The transport reads it
        //    once, when it is built.
        ctx.Narrator.Step("The client transport takes the same setting, for messages pushed back on its return-route session sockets.");
        var clientLimit = TimeSpan.FromSeconds(30);
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(ctx.ServiceProvider.GetRequiredService<ISecretsResolver>());
            b.UseWebSocketTransport(o => o.MessageReceiveTimeout = clientLimit);
        });
        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<WebSocketDidCommTransport>(); // the transport reads the setting here
        ctx.Narrator.Value("UseWebSocketTransport(o => o.MessageReceiveTimeout = ...)", clientLimit);

        ctx.Narrator.Note("the limit bounds how long one connection can hold a message; it does not bound how many connections are open. On a public endpoint, also set Kestrel's connection limits and a per-client concurrency limiter (see SECURITY.md).");
        ctx.Narrator.Note("Timeout.InfiniteTimeSpan turns the limit off; a peer can then hold a connection open with a message it never finishes. Prefer raising the limit for peers on slow links.");
    }

    /// <summary>Pack an authcrypted message from Alice to Bob, as the bytes of one WebSocket message.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    /// <param name="content">The message's text.</param>
    private static async Task<byte[]> PackAsync(CookbookContext ctx, string content)
    {
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(new JsonObject { ["content"] = content })
            .Build();
        var packed = await ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did));
        return Encoding.UTF8.GetBytes(packed.Message);
    }

    /// <summary>Open a connection to Bob's endpoint.</summary>
    /// <param name="sockets">The test server's WebSocket client.</param>
    /// <param name="endpoint">Bob's endpoint.</param>
    private static async Task<System.Net.WebSockets.WebSocket> ConnectAsync(WebSocketClient sockets, Uri endpoint)
    {
        using var guard = new CancellationTokenSource(HangGuard);
        return await sockets.ConnectAsync(endpoint, guard.Token);
    }

    /// <summary>The text of a message Bob received.</summary>
    /// <param name="unpacked">The unpacked message.</param>
    private static string? Content(UnpackResult unpacked)
        => unpacked.Message.Body?["content"]?.GetValue<string>();

    /// <summary>Wait for the next message Bob's endpoint received.</summary>
    /// <param name="reader">Where Bob's endpoint puts what it receives.</param>
    private static async Task<UnpackResult> NextAsync(ChannelReader<UnpackResult> reader)
    {
        using var guard = new CancellationTokenSource(HangGuard);
        try
        {
            return await reader.ReadAsync(guard.Token);
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            throw new TimeoutException($"Section HH: Bob's endpoint received nothing within {HangGuard.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// Read until Bob's endpoint closes the connection, answer its close so the closing handshake
    /// completes on both sides, and return the status and reason it sent.
    /// </summary>
    /// <param name="socket">The connection.</param>
    private static async Task<(WebSocketCloseStatus? Status, string? Description)> ReadCloseAsync(System.Net.WebSockets.WebSocket socket)
    {
        using var guard = new CancellationTokenSource(HangGuard);
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), guard.Token);
                if (result.MessageType != WebSocketMessageType.Close)
                    continue; // nothing else is expected here: read on to the close
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", guard.Token);
                return (result.CloseStatus, result.CloseStatusDescription);
            }
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            throw new TimeoutException($"Section HH: Bob's endpoint did not close the connection within {HangGuard.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// Bob's endpoint: <c>MapDidCommWebSocket</c> on an in-process test server, with
    /// <see cref="DemoLimit"/> as its <see cref="DidCommReceiveOptions.MessageReceiveTimeout"/>. Each
    /// message it receives is written to <paramref name="received"/>.
    /// </summary>
    /// <param name="ctx">The shared cookbook context (Bob's keys and the DID resolver).</param>
    /// <param name="received">Where the endpoint puts each message it receives.</param>
    private static async Task<WebApplication> StartBobAsync(CookbookContext ctx, ChannelWriter<UnpackResult> received)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders(); // keep the cookbook's output to the narration
        builder.Services.AddSingleton(ctx.ServiceProvider.GetRequiredService<ISecretsResolver>());
        builder.Services.AddSingleton(ctx.ServiceProvider.GetRequiredService<IDidKeyService>());
        builder.Services.AddSingleton(ctx.ServiceProvider.GetRequiredService<IServiceEndpointResolver>());
        builder.Services.AddOptions<DidCommOptions>();
        builder.Services.AddSingleton(sp => new DidCommClient(
            sp.GetRequiredService<ISecretsResolver>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<IServiceEndpointResolver>(),
            sp.GetRequiredService<IOptions<DidCommOptions>>().Value));

        // The receive time limit of Bob's WebSocket endpoints. In production keep the default (60 s)
        // or raise it for peers on slow links.
        builder.Services.AddOptions<DidCommReceiveOptions>().Configure(o => o.MessageReceiveTimeout = DemoLimit);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapDidCommWebSocket("/ws/didcomm", async (unpacked, ct) => await received.WriteAsync(unpacked, ct));
        await app.StartAsync();
        return app;
    }
}
