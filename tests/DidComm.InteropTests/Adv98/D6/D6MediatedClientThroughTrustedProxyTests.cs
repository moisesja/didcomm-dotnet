using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator;
using DidComm.InteropTests.Mediator;
using DidComm.InteropTests.Samples;
using DidComm.InteropTests.Transports.Proxy;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.Adv98.D6;

/// <summary>
/// #98 D6, the local analogue of E-47 (with E-48 and E-49): a real Kestrel mediator on loopback, a
/// real forwarding proxy in front of it, and a wallet whose HTTP and WebSocket transports both name
/// that proxy as <c>TrustedProxy</c>, with <c>ResolveDnsNames = false</c> as the live harness runs.
/// The wallet enrols (CM 3.0 <c>mediate-request</c>), registers a recipient, fetches over HTTP and
/// goes live over a WebSocket; Alice (also through the proxy) sends to the recipient. Meanwhile a
/// process proxy (an environment variable or <c>HttpClient.DefaultProxy</c>) names a trap.
/// The mediator is named <c>mediator.d6</c>, which only the proxy resolves (to loopback), so a
/// connection that bypassed the proxy could not reach it; on top of that, every request the
/// mediator served must have come from a socket the proxy opened, and the trap must see nothing.
/// </summary>
[Collection(nameof(DefaultProxyCollection))]
public sealed class D6MediatedClientThroughTrustedProxyTests(ITestOutputHelper output)
{
    private const string MediatorHost = "mediator.d6";

    [Theory]
    [InlineData("HTTP_PROXY")]
    [InlineData("ALL_PROXY")]
    [InlineData("DefaultProxy")]
    public async Task Enrol_register_fetch_and_live_all_ride_the_TrustedProxy_and_the_process_proxy_sees_nothing(string processProxy)
    {
        using var floor = Qa.RaiseThreadPoolFloor();
        await using var trap = new StandInProxy(c => c.StatusAsync("403 Forbidden", "Connection: close"));
        await using var mediator = await D6KestrelMediator.StartAsync(MediatorHost);
        await using var proxy = new D6ForwardingProxy(MediatorHost);
        using var scope = processProxy == "DefaultProxy"
            ? ProcessProxyScope.FromDefaultProxy(trap.Address())
            : ProcessProxyScope.FromEnvironment(processProxy, trap.Address());
        // Precondition: the process proxy really would carry the mediator's traffic to the trap.
        ProcessProxyScope.AssertProxies(mediator.HttpEndpoint, trap.Address());
        ProcessProxyScope.AssertProxies(new UriBuilder(mediator.WebSocketEndpoint) { Scheme = "http" }.Uri, trap.Address());

        var bob = await MediatorCast.MintAsync();
        var bobSecrets = Secrets(bob);
        await using var wallet = Container(bobSecrets, proxy.Address, wallet: true);
        var alice = await MediatorCast.MintAsync();
        await using var aliceContainer = Container(Secrets(alice), proxy.Address, wallet: false);
        var mediation = wallet.GetRequiredService<MediationClient>();
        var pickup = wallet.GetRequiredService<PickupClient>();
        var aliceClient = aliceContainer.GetRequiredService<DidCommClient>();

        var grant = await mediation.RequestMediationAsync(mediator.Did, bob.Did).WaitAsync(MediatorTestServer.Timeout);
        grant.Should().NotBeNull("open enrolment grants");
        var recipient = await MediatorCast.MintWithServiceEntryAsync(DidCommServiceBuilder.ForMediation(grant!).Build());
        foreach (var key in recipient.Privates)
            bobSecrets.Add(key);
        await mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, new[] { new RecipientUpdate(recipient.Did, RecipientUpdateAction.Add) })
            .WaitAsync(MediatorTestServer.Timeout);

        await aliceClient.SendAsync(Note(alice.Did, recipient.Did, "one"), new SendOptions(Recipients: new[] { recipient.Did }, From: alice.Did))
            .WaitAsync(MediatorTestServer.Timeout);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 1, "Alice's first note was queued");
        var batch = await pickup.FetchAsync(mediator.Did, bob.Did, limit: 10).WaitAsync(MediatorTestServer.Timeout);

        PickupItem? pushed;
        await using (var live = pickup.ReceiveLiveAsync(mediator.Did, bob.Did, mediator.WebSocketEndpoint).GetAsyncEnumerator())
        {
            var next = live.MoveNextAsync().AsTask();
            await aliceClient.SendAsync(Note(alice.Did, recipient.Did, "two"), new SendOptions(Recipients: new[] { recipient.Did }, From: alice.Did))
                .WaitAsync(MediatorTestServer.Timeout);
            (await next.WaitAsync(MediatorTestServer.Timeout)).Should().BeTrue("the live stream yields Alice's second note");
            pushed = live.Current;
        }

        output.WriteLine($"proxy request lines: {string.Join(" ; ", proxy.Lines)}");
        output.WriteLine($"mediator requests: {mediator.Requests.Count}; via proxy: {mediator.Requests.Count(r => proxy.CameThrough(r.RemotePort))}; trap accepted: {trap.Accepted}");
        batch.Items.Should().ContainSingle();
        batch.Items[0].Unpacked!.Message.Body!["content"]!.GetValue<string>().Should().Be("one");
        pushed!.Unpacked!.Message.Body!["content"]!.GetValue<string>().Should().Be("two");
        proxy.Lines.Should().Contain(l => l.StartsWith($"POST {mediator.HttpEndpoint}", StringComparison.Ordinal), "HTTP goes through the proxy in absolute form");
        proxy.Lines.Should().Contain($"CONNECT {MediatorHost}:{mediator.Port} HTTP/1.1", "the WebSocket handshake is tunnelled");
        mediator.Requests.Should().NotBeEmpty();
        mediator.Requests.Should().OnlyContain(r => proxy.CameThrough(r.RemotePort), "every request the mediator served came from a socket the proxy opened");
        mediator.Requests.Should().Contain(r => r.WebSocket, "the live session is a WebSocket upgrade");
        trap.Accepted.Should().Be(0, "the process proxy ({0}) is never used", processProxy);
    }

    private static InMemorySecretsResolver Secrets(PeerIdentity identity)
    {
        var secrets = new InMemorySecretsResolver();
        foreach (var key in identity.Privates)
            secrets.Add(key);
        return secrets;
    }

    private static Message Note(string from, string to, string content)
        => new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(from)
            .WithTo(to)
            .WithBody(new JsonObject { ["content"] = content })
            .Build();

    /// <summary>A container whose transports both name <paramref name="proxy"/>; the mediator's name is allow-listed and never resolved locally.</summary>
    private static ServiceProvider Container(InMemorySecretsResolver secrets, Uri proxy, bool wallet)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http" };
                o.MaxRetryAttempts = 0;
                o.TrustedProxy = new WebProxy(proxy);
            });
            if (wallet)
            {
                b.UseWebSocketTransport(o =>
                {
                    o.AllowedSchemes = new[] { "ws" };
                    o.ReceiveInbound = true;
                    o.MaxReconnectAttempts = 0;
                    o.TrustedProxy = new WebProxy(proxy);
                });
                b.UseInboundDispatch();
                b.AddMediatorClient(c => c.MessagePickupVersion = MessagePickupVersion.V3);
            }
            b.Configure(o =>
            {
                o.OutboundEndpointPolicy.ResolveDnsNames = false;
                o.OutboundEndpointPolicy.AllowedHosts.Add(MediatorHost);
            });
        });
        return services.BuildServiceProvider();
    }
}

/// <summary>One request the mediator served: the peer port it came from, and whether it was a WebSocket upgrade.</summary>
file sealed record D6ServedRequest(int RemotePort, bool WebSocket);

/// <summary>A Kestrel mediator on a reserved loopback port whose routing DID names <c>http://{host}:{port}/didcomm</c>.</summary>
file sealed class D6KestrelMediator : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<D6ServedRequest> _requests;

    private D6KestrelMediator(WebApplication app, string did, string host, int port, ConcurrentQueue<D6ServedRequest> requests)
    {
        _app = app;
        Did = did;
        Port = port;
        HttpEndpoint = new Uri($"http://{host}:{port}/didcomm");
        WebSocketEndpoint = new Uri($"ws://{host}:{port}/didcomm");
        _requests = requests;
    }

    public string Did { get; }

    public int Port { get; }

    public Uri HttpEndpoint { get; }

    public Uri WebSocketEndpoint { get; }

    public IReadOnlyList<D6ServedRequest> Requests => _requests.ToArray();

    public InMemoryMessageStore Store => (InMemoryMessageStore)_app.Services.GetRequiredService<IMessageStore>();

    public static async Task<D6KestrelMediator> StartAsync(string host)
    {
        var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
        reserve.Stop();

        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec($"http://{host}:{port}/didcomm", Accept: new[] { "didcomm/v2" }));
        var secrets = new InMemorySecretsResolver();
        foreach (var key in identity.Privates)
            secrets.Add(key);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(secrets)
            .AddMediator(m => m.CoordinateMediation.RoutingDids.Add(identity.Did)));
        var app = builder.Build();
        var requests = new ConcurrentQueue<D6ServedRequest>();
        app.UseWebSockets(); // first, so the recorder below can tell an upgrade
        app.Use(async (HttpContext context, Func<Task> next) =>
        {
            requests.Enqueue(new D6ServedRequest(context.Connection.RemotePort, context.WebSockets.IsWebSocketRequest));
            await next();
        });
        app.MapDidCommMediator("/didcomm");
        await app.StartAsync();
        return new D6KestrelMediator(app, identity.Did, host, port, requests);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// A minimal forwarding proxy on loopback: <c>CONNECT host:port</c> is tunnelled, an absolute-form
/// request is forwarded verbatim (Kestrel accepts absolute-form targets), then bytes are piped both
/// ways. It resolves only the one name it is given, to loopback, and refuses everything else, so it
/// never dials anything but the test's mediator. It records each first request line and the local
/// port of every upstream socket it opens.
/// </summary>
file sealed class D6ForwardingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly ConcurrentDictionary<int, byte> _upstreamPorts = new();
    private readonly string _name;
    private readonly Task _loop;

    public D6ForwardingProxy(string name)
    {
        _name = name;
        _listener.Start();
        Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _loop = Task.Run(LoopAsync);
    }

    public Uri Address { get; }

    public IReadOnlyList<string> Lines => _lines.ToArray();

    public bool CameThrough(int remotePort) => _upstreamPorts.ContainsKey(remotePort);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var downstream = client.GetStream();
                var head = await ReadHeadAsync(downstream);
                if (head is null)
                    return;
                var text = Encoding.ASCII.GetString(head);
                var line = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
                _lines.Enqueue(line);
                var parts = line.Split(' ');
                var connect = parts[0] == "CONNECT";
                var target = connect ? new Uri("http://" + parts[1]) : new Uri(parts[1]);
                if (!string.Equals(target.Host, _name, StringComparison.OrdinalIgnoreCase))
                {
                    await downstream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                    return;
                }
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(IPAddress.Loopback, target.Port, _stop.Token);
                _upstreamPorts[((IPEndPoint)upstream.Client.LocalEndPoint!).Port] = 0;
                var upstreamStream = upstream.GetStream();
                if (connect)
                    await downstream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), _stop.Token);
                else
                    await upstreamStream.WriteAsync(head, _stop.Token);
                var up = downstream.CopyToAsync(upstreamStream, _stop.Token);
                var down = upstreamStream.CopyToAsync(downstream, _stop.Token);
                await Task.WhenAny(up, down);
            }
            catch (Exception)
            {
                // A peer that hangs up, or a stopping proxy.
            }
        }
    }

    private async Task<byte[]?> ReadHeadAsync(NetworkStream stream)
    {
        var head = new MemoryStream();
        var one = new byte[1];
        while (head.Length < 16 * 1024)
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0)
                return null;
            head.WriteByte(one[0]);
            var b = head.GetBuffer();
            var n = head.Length;
            if (n >= 4 && b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n')
                return head.ToArray();
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // The loop ends with the listener.
        }
        _stop.Dispose();
    }
}
