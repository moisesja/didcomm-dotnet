using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Sending from a network where the only way out is a proxy. The HTTP and WebSocket transports
/// connect directly by default and never pick up a proxy from the environment (<c>HTTP_PROXY</c>,
/// <c>HTTPS_PROXY</c>, <c>ALL_PROXY</c>, the system proxy). When your host can reach other agents
/// only through a proxy, you name that proxy yourself with <c>TrustedProxy</c> on each transport's
/// options.
/// </summary>
/// <remarks>
/// <para>
/// Why it is opt-in: before connecting, the library checks that a destination is not a private,
/// loopback or cloud-metadata address, because a DID document's service endpoint is written by
/// whoever controls that DID. Once a connection goes through a proxy, the library can no longer see
/// where it ends up. So with <c>TrustedProxy</c> set, it still refuses destinations it can judge on
/// its own (an IP address, and a name it can look up), but <b>your proxy must refuse internal
/// destinations</b> itself (egress filtering). The proxy's own address is your choice and is never
/// refused, even on a private network.
/// </para>
/// <para>
/// The section runs offline: a stand-in proxy on loopback plays your egress proxy. It answers the
/// one public address it allows (<c>203.0.113.10</c>, a documentation address) and refuses the rest,
/// as an egress filter would. Nothing leaves the machine.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>GG</strong> (FR-TRN-13).
/// </para>
/// </remarks>
public static class Section_GG_SendThroughATrustedProxy
{
    /// <summary>The one destination the stand-in proxy lets through (TEST-NET-3, never routed).</summary>
    private const string AllowedDestination = "203.0.113.10";

    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("GG", "Send through a proxy you trust");

        await using var proxy = new EgressProxy(AllowedDestination);

        var secrets = new InMemorySecretsResolver();
        foreach (var jwk in ctx.Alice.Privates) secrets.Add(jwk);

        ctx.Narrator.Step("Name the proxy on both transports. Without this, every connection is direct.");
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };  // the stand-in speaks plain http
                o.MaxRetryAttempts = 0;
                // Credentials, if your proxy needs them, go in WebProxy.Credentials, never in the address.
                o.TrustedProxy = new WebProxy(proxy.Address);
            });
            b.UseWebSocketTransport(o =>
            {
                o.AllowedSchemes = new[] { "ws", "wss" };
                o.MaxReconnectAttempts = 0;
                o.TrustedProxy = new WebProxy(proxy.Address);
            });
        });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<DidCommClient>();
        ctx.Narrator.Value("HttpTransportOptions.TrustedProxy",
            provider.GetRequiredService<IOptions<HttpTransportOptions>>().Value.TrustedProxy?.Address);
        ctx.Narrator.Value("WebSocketTransportOptions.TrustedProxy",
            provider.GetRequiredService<IOptions<WebSocketTransportOptions>>().Value.TrustedProxy?.Address);
        ctx.Narrator.Note("the stand-in proxy listens on loopback, a private address, and is used anyway: it is your choice.");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Section GG: out through the egress proxy."}""")!.AsObject())
            .Build();
        SendOptions To(string endpoint) => new(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did,
            ServiceEndpointOverride: new Uri(endpoint));

        // 1. An allowed destination over HTTP: the request goes to the proxy, which answers for it.
        ctx.Narrator.Step($"HTTP to http://{AllowedDestination}/didcomm: the request goes through the proxy.");
        var sent = await client.SendAsync(message, To($"http://{AllowedDestination}/didcomm"));
        ctx.Narrator.Value("Accepted", sent.Transport.Accepted);
        ctx.Narrator.Value("HttpStatusCode", sent.Transport.HttpStatusCode);
        ctx.Narrator.Value("The proxy received", proxy.Seen.Last());

        // 2. A destination the library can judge on its own is refused before the proxy is asked,
        //    even one you passed yourself as an endpoint override.
        ctx.Narrator.Step("HTTP to the cloud-metadata address: refused by the library before the proxy is asked.");
        await ShowRefusalAsync(ctx, proxy, () => client.SendAsync(message, To("http://169.254.169.254/latest/meta-data")));

        // 3. WebSocket goes through the proxy too, as a tunnel. This destination is not on the proxy's
        //    list, so the proxy refuses it: past the proxy, its rules decide.
        ctx.Narrator.Step("WebSocket to ws://198.51.100.7/didcomm: the tunnel request reaches the proxy, and the proxy says no.");
        await ShowRefusalAsync(ctx, proxy, () => client.SendAsync(message, To("ws://198.51.100.7/didcomm")));
        ctx.Narrator.Value("The proxy received", proxy.Seen.Last());

        ctx.Narrator.Step("WebSocket to a private address: refused by the library before the proxy is asked.");
        await ShowRefusalAsync(ctx, proxy, () => client.SendAsync(message, To("ws://10.0.0.1/didcomm")));

        ctx.Narrator.Note("where only the proxy can look up names, set OutboundEndpointPolicy.ResolveDnsNames = false; then the library checks IP addresses only, and names are your proxy's to filter.");
    }

    private static async Task ShowRefusalAsync(CookbookContext ctx, EgressProxy proxy, Func<Task> send)
    {
        // The proxy records every request before it answers, so after the send fails the count says
        // whether the proxy was asked at all.
        var before = proxy.Seen.Count;
        try
        {
            await send();
            ctx.Narrator.Note("UNEXPECTED: the send was not refused.");
        }
        catch (TransportException)
        {
            ctx.Narrator.Value("Refused", proxy.Seen.Count == before ? "by the library, before the proxy was asked" : "by the proxy");
        }
    }

    /// <summary>
    /// A stand-in for your egress proxy, on loopback. It forwards nothing: it answers a forwarded
    /// request for the one destination it allows with <c>202 Accepted</c> and refuses every other
    /// request, tunnels included, with <c>403 Forbidden</c>, as an egress filter would.
    /// </summary>
    private sealed class EgressProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _seen = new();
        private readonly string _allowedPrefix;
        private readonly Task _loop;

        public EgressProxy(string allowedHost)
        {
            _allowedPrefix = $"POST http://{allowedHost}/";
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _loop = Task.Run(AcceptAsync);
        }

        /// <summary>The proxy's address, for <c>new WebProxy(...)</c>.</summary>
        public Uri Address { get; }

        /// <summary>The request line of every request received, recorded before it is answered.</summary>
        public IReadOnlyCollection<string> Seen => _seen;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // The loop ends with the listener.
            }
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream);
                    var requestLine = head[..head.IndexOf("\r\n", StringComparison.Ordinal)];
                    // Read the body before answering, so closing the connection resets nothing.
                    var length = ContentLength(head);
                    var body = new byte[length];
                    for (var read = 0; read < length;)
                    {
                        var n = await stream.ReadAsync(body.AsMemory(read), _stop.Token);
                        if (n == 0)
                            throw new IOException("closed");
                        read += n;
                    }
                    _seen.Enqueue(requestLine);
                    var status = requestLine.StartsWith(_allowedPrefix, StringComparison.Ordinal) ? "202 Accepted" : "403 Forbidden";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                }
                catch (Exception)
                {
                    // A client that hangs up, or a stopping proxy.
                }
            }
        }

        private async Task<string> ReadHeadAsync(NetworkStream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0)
                    throw new IOException("closed");
                head.Append((char)one[0]);
            }
            return head.ToString();
        }

        private static int ContentLength(string head)
        {
            foreach (var line in head.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    return int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
            return 0;
        }
    }
}
