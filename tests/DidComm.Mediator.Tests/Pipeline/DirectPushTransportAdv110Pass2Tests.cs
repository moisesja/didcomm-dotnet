using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// Red-team pass 2a (#110, direct push): the fixes pass 1 prompted in the push transport. ADV-110-1a-2
/// and 1a-3 were fixed by looking no name up before the take: a name is checked when the connection
/// opens, by the named client's connect callback, under the push deadline. These tests look for the
/// host configurations where that premise does not hold.
/// </summary>
public sealed class DirectPushTransportAdv110Pass2Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly byte[] Payload = "{\"protected\":\"adv-110-2a\"}"u8.ToArray();

    // ADV-110-2a-1. With a TrustedProxy the connect callback sees only the proxy's socket, so the HTTP
    // transport checks each hop's destination itself before sending it. The outbound check looks a
    // name up under the send's token, before the take and on every hop: a lookup that never completes
    // (the guard's lookup seam) ends with the push deadline, in both places.
    [Fact]
    public async Task With_a_TrustedProxy_the_push_deadline_ends_a_stalled_lookup_of_the_recipients_endpoint_name()
    {
        var lookups = new SemaphoreSlim(0);
        var policy = new OutboundEndpointPolicy { ResolveDnsAsync = (_, _) => { lookups.Release(); return new TaskCompletionSource<IPAddress[]>().Task; } };
        await using var container = Container(
            o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.TrustedProxy = new WebProxy("http://127.0.0.1:9");
            },
            more: s => s.Configure<DidCommOptions>(o => o.OutboundEndpointPolicy = policy));
        var sender = DirectPushSender.Create(container);
        var endpoint = new Uri("http://inbox.stall.recipient.example/didcomm");

        using var checkDeadline = new CancellationTokenSource();
        var check = sender.CheckTargetAsync(endpoint, checkDeadline.Token).AsTask();
        (await lookups.WaitAsync(Wait)).Should().BeTrue("the check before the take looks the name up");
        await checkDeadline.CancelAsync();
        var checkEnded = await Record.ExceptionAsync(() => check.WaitAsync(Wait));

        using var sendDeadline = new CancellationTokenSource();
        var push = sender.SendAsync(endpoint, Payload, sendDeadline.Token);
        (await lookups.WaitAsync(Wait)).Should().BeTrue("the transport checks the first hop's destination through the proxy");
        await sendDeadline.CancelAsync();
        var sendEnded = await Record.ExceptionAsync(() => push.WaitAsync(Wait));

        checkEnded.Should().BeAssignableTo<OperationCanceledException>("the push deadline ends the check's lookup (1a-2, 1a-3)");
        sendEnded.Should().BeAssignableTo<OperationCanceledException>("the push deadline ends the hop check's lookup (1a-2, 1a-3)");
    }

    // ADV-110-2a-1, what it cost: D-4 counts an abandoned push until its call ends. Two recipients
    // behind a TrustedProxy whose name lookups stalled held both operations a PushConcurrency of 1
    // allows past their deadlines, so every push of every recipient, live ones included, was skipped
    // as saturated. Now each stalled push settles at its deadline, and its recipient pauses.
    [Fact]
    public async Task With_a_TrustedProxy_recipients_whose_endpoint_name_lookups_stall_settle_at_their_deadlines()
    {
        using var floor = MessagePickupLive.RaiseThreadPoolFloor(4);
        var lookups = new SemaphoreSlim(0);
        var policy = new OutboundEndpointPolicy { ResolveDnsAsync = (_, _) => { lookups.Release(); return new TaskCompletionSource<IPAddress[]>().Task; } };
        await using var container = Container(
            o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.TrustedProxy = new WebProxy("http://127.0.0.1:9");
            },
            more: s => s.Configure<DidCommOptions>(o => o.OutboundEndpointPolicy = policy));
        var sender = DirectPushSender.Create(container);
        var endpoints = new DirectEndpoints();
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 1, s =>
            DirectPushRouting.Register(s, new DirectPushRoute(endpoints, new Lazy<IDirectPushSender>(() => sender))));
        var fixture = world.Fixture;
        var (healthy, session) = await world.LiveGrantAsync("adv2a-healthy");
        for (var i = 1; i <= 2; i++)
        {
            var stalling = await world.GrantAsync($"adv2a-stall-{i}");
            endpoints.Set(stalling.Peer, new Uri($"http://inbox-{i}.stall.recipient.example/didcomm"));
            await world.StoreAsync(stalling, (byte)i);
            var mark = fixture.Time.Mark();
            fixture.Dispatcher.Wake(stalling.Peer).Should().BeTrue();
            (await lookups.WaitAsync(Wait)).Should().BeTrue("the name lookup started");
            await fixture.Time.WaitForTimerAsync(PushTimeout, mark, Wait);
            fixture.Time.Advance(PushTimeout);
            await fixture.WaitForPushOutcomesAsync(i, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "each stalled push ends at its deadline (D-4; 1a-3)");

        fixture.Time.Advance(10 * PushTimeout);
        var message = await world.StoreAsync(healthy, 9);
        fixture.Dispatcher.Wake(healthy.Peer);
        await fixture.WaitForPushOutcomesAsync(3, Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.PushedLive);
        LivePushWorld.Deliveries(session).Select(d => d.Id).Should().Equal(message.MessageId);
    }

    // ADV-110-2a-2 (Medium; the fix for ADV-110-1a-2 removed a check the residual-5 hosts relied on;
    // fixed: the check before the take looks a name up again, asynchronously, under the deadline).
    // The start-up check accepts a SocketsHttpHandler with any connect callback, and any primary
    // handler that is neither an HttpClientHandler nor a SocketsHttpHandler (residual 5: "the host's
    // own connection path ... as for every send of that host", and the class docs: "the mediator's own
    // checks (the endpoint check before the take ...) still apply"). Before the fix that endpoint
    // check looked the name up, so a name resolving to a private address was refused before the take,
    // as the host's own sends refuse it (DidCommClient.SendPackedAsync looks names up before sending).
    // Now it looks no name up, and nothing else does on these hosts: the recipient registers
    // http://localhost/ (or a name pointing at 169.254.169.254 or an internal service) and the
    // mediator POSTs the stored message there. Measured: 1 POST reached 127.0.0.1 through the name
    // "localhost", counted Pushed-Direct, while the core pre-send check refuses the same endpoint.
    [Theory]
    [InlineData("a connect callback of the host's own (socket options)")]
    [InlineData("a primary handler of the host's own")]
    public async Task A_name_that_resolves_to_a_private_address_is_never_pushed_to_whatever_the_hosts_connection_path(string row)
    {
        await using var loopback = await LoopbackInbox.StartAsync();
        await using var container = Container(o => o.AllowedSchemes = new[] { "http", "https" }, more: s =>
        {
            var named = s.AddHttpClient(HttpDidCommTransport.HttpClientName);
            if (row.StartsWith("a connect callback", StringComparison.Ordinal))
                named.UseSocketsHttpHandler((h, _) => h.ConnectCallback = PlainConnectAsync);
            else
                named.ConfigurePrimaryHttpMessageHandler(() => new HostOwnHandler());
        });
        var sender = DirectPushSender.Create(container);
        var endpoint = new Uri($"http://localhost:{loopback.Port}/didcomm");
        var hostsOwnSend = Record.Exception(() => new OutboundEndpointGuard(new OutboundEndpointPolicy()).Validate(endpoint));
        hostsOwnSend.Should().BeOfType<DidComm.Exceptions.TransportException>("the host's own sends refuse a name that resolves to the loopback (the precondition)");

        var endpoints = new DirectEndpoints();
        await using var world = await LivePushWorld.StartAsync(null, s =>
            DirectPushRouting.Register(s, new DirectPushRoute(endpoints, new Lazy<IDirectPushSender>(() => sender))));
        var fixture = world.Fixture;
        var grant = await world.GrantAsync("adv2a-private-name");
        endpoints.Set(grant.Peer, endpoint);
        var stored = await world.StoreAsync(grant, 1);
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the refused push settled");
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(2, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        loopback.Posts.Should().Be(0, "a push never reaches a private address the host's own sends refuse (D-7)");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed, PushOutcomes.SkippedCooldown }, "refused before the take, then that recipient's direct pushes pause");
        (await world.PickupAsync(grant.Peer)).Should().Equal(stored.MessageId);
    }

    // ADV-110-2a-3 (Low, resolved as documented; the coordinator's call). Since 1a-4 a 4xx moves on
    // with no pause, so an endpoint that stalls each POST just under the deadline and then answers 404
    // holds a worker for up to MaxDeliveryBatch × PushTimeout per wake-up, as one answering 202 after
    // the same stall always could. MediatorOptions.PushTimeout states that bound.
    [Theory]
    [InlineData(404)]
    [InlineData(202)]
    public async Task An_endpoint_that_answers_each_post_just_under_the_deadline_holds_a_worker_at_most_MaxDeliveryBatch_times_the_deadline_per_wake_up(int status)
    {
        var answer = status == 202 ? DirectSendResult.Delivered : DirectSendResult.Refused;
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 1);
        var fixture = direct.Fixture;
        var slow = await direct.GrantAsync("adv2a-slow");
        var started = new SemaphoreSlim(0);
        var answers = new SemaphoreSlim(0);
        direct.Sends.OnAnswer = async (_, _, ct) =>
        {
            started.Release();
            await answers.WaitAsync(ct);
            return answer;
        };
        var batch = new MessagePickupOptions().MaxDeliveryBatch;
        for (var i = 1; i <= batch + 2; i++)
            await fixture.StoreAsync(slow.Recipient, PushFixture.Payload(64, (byte)i));

        fixture.Dispatcher.Wake(slow.Peer).Should().BeTrue();
        var workerTime = TimeSpan.Zero;
        var stall = PushTimeout - TimeSpan.FromMilliseconds(1);
        for (var post = 1; post <= batch; post++)
        {
            if (!await started.WaitAsync(post == 1 ? Wait : TimeSpan.FromSeconds(1)))
                break; // the operation ended: no further POST in this wake-up
            fixture.Time.Advance(stall).Should().Be(0, "the answer comes just before the deadline");
            workerTime += stall;
            answers.Release();
            await fixture.WaitForPushOutcomesAsync(post, Wait);
        }
        answers.Release(batch + 2); // the rest answer at once (a 2xx run yields and goes on)
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        workerTime.Should().BeLessThanOrEqualTo(batch * PushTimeout, "the documented bound for slow answers (MediatorOptions.PushTimeout)");
        workerTime.Should().BeGreaterThan(PushTimeout, "a 4xx or a 2xx moves on: the run is not cut short by a pause");
    }

    /// <summary>A host's own connect callback (for socket options, say): it connects where it is asked, with no address check.</summary>
    private static async ValueTask<Stream> PlainConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A host's own primary handler that is neither an <see cref="HttpClientHandler"/> nor a
    /// <see cref="SocketsHttpHandler"/> (a platform handler, or one of a library's): the start-up check
    /// lets it through as the host's own connection path. It follows no redirect and uses no proxy.
    /// </summary>
    private sealed class HostOwnHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _inner.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A plain HTTP/1.1 server on the IPv4 loopback that answers every request 202 and counts the POSTs.</summary>
    private sealed class LoopbackInbox : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        private int _posts;

        private LoopbackInbox(TcpListener listener)
        {
            _listener = listener;
            _serving = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Posts => Volatile.Read(ref _posts);

        public static Task<LoopbackInbox> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LoopbackInbox(listener));
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read >= 4 && System.Text.Encoding.ASCII.GetString(buffer, 0, 4) == "POST")
                        Interlocked.Increment(ref _posts);
                    await stream.WriteAsync("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
                // Stopped.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await Task.WhenAny(_serving, Task.Delay(Wait));
            _stop.Dispose();
        }
    }

    /// <summary>A container the way a mediator host builds one: <c>AddDidComm</c> with <c>UseHttpTransport</c>, then <paramref name="more"/>.</summary>
    private static ServiceProvider Container(Action<HttpTransportOptions> http, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport(http);
        });
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
