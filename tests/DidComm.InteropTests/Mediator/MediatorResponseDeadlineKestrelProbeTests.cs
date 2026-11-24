using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #98 FX-1, probes on real Kestrel at its defaults: the response deadlines really end a stalled
/// socket. Opt-in (<c>DIDCOMM_ADV98_PROBES=1</c>): they wait on real time, so CI does not rely on them.
/// <list type="bullet">
/// <item><description>D3-F3: one raw client pipelines thousands of forwards to its own self-enrolled DID
/// and never reads. Once the kernel buffers and Kestrel's output pipe are full, the 202 at the stall
/// cannot be committed; its reservation is released at the forward deadline (10 s) and Kestrel ends the
/// connection.</description></item>
/// <item><description>D3-F2: a raw client posts a <c>delivery-request</c> with <c>return_route: all</c> and
/// never reads the 200; with <c>LeaseTimeout</c> = 5 s the request is aborted at about 5 s and the
/// records stay queued.</description></item>
/// </list>
/// Real time is read on the monotonic clock only; every wait polls the exact quantity it reports, with
/// a hang guard.
/// </summary>
[Collection(QaSerialTiming.Name)]
[Trait("Category", "Adv98Probe")]
public sealed class MediatorResponseDeadlineKestrelProbeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(90);
    private readonly ITestOutputHelper _out;

    public MediatorResponseDeadlineKestrelProbeTests(ITestOutputHelper output) => _out = output;

    [Adv98ProbeFact]
    public async Task On_Kestrel_a_pipelining_client_that_never_reads_loses_its_reservation_at_the_forward_deadline_and_its_connection()
    {
        using var floor = Qa.RaiseThreadPoolFloor();
        var requests = new ProbeRequestTracker();
        await using var host = await ProbeKestrel.StartAsync(requests);
        var handler = host.App.Services.GetRequiredService<ForwardHandler>();
        await using var mallory = await MediatorCast.MintPeerAsync();
        await host.EnrolAsync(mallory);

        var forward = ForwardMessage.Create(host.MediatorDid, mallory.Did, new[] { Qa.JsonPayloadOfSize(64) });
        var body = Encoding.UTF8.GetBytes((await mallory.Client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { host.MediatorDid }, From: null, Forward: false))).Message);
        var request = Encoding.ASCII.GetBytes($"POST /didcomm HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nContent-Type: {MediaTypes.Encrypted}\r\nContent-Length: {body.Length}\r\n\r\n").Concat(body).ToArray();

        using var tcp = new TcpClient { ReceiveBufferSize = 1024 };
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        var stream = tcp.GetStream();
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 6000 && !stop.IsCancellationRequested; i++)
                await stream.WriteAsync(request, stop.Token);
        });

        // The connection has stalled when the server stops taking requests while one is still reserved.
        var stalledAt = await ProbeKestrel.StallAsync(requests);
        var takenAtStall = requests.Requests;
        var reservedAtStall = handler.Outstanding;
        var released = await ProbeKestrel.UntilAsync(() => handler.Outstanding == 0, Guard);
        var releasedAfter = Stopwatch.GetElapsedTime(stalledAt);
        var ended = await ProbeKestrel.UntilAsync(() => requests.InFlight == 0, Guard);
        var endedAfter = Stopwatch.GetElapsedTime(stalledAt);
        var readAfterAbort = await ProbeKestrel.DrainAsync(stream);

        Report($"F3 Kestrel: {takenAtStall} pipelined forwards taken before the stall, {reservedAtStall} reserved at the stall; released {(released ? $"{releasedAfter.TotalSeconds:N1} s" : "never")} after the stall was seen; connection ended {(ended ? $"{endedAfter.TotalSeconds:N1} s" : "never")} after; the client then read {readAfterAbort.Bytes} B and saw {readAfterAbort.End}.");
        stop.Cancel();
        tcp.Dispose();
        await Record.ExceptionAsync(() => writer);

        reservedAtStall.Should().Be(1, "one connection holds one reservation");
        released.Should().BeTrue("the forward deadline releases the reservation");
        // The stall is seen about 2 s after the stalled request's dispatch; the deadline is 10 s after it.
        releasedAfter.Should().BeLessThan(TimeSpan.FromSeconds(20));
        ended.Should().BeTrue("the abort ends the stalled connection");
    }

    [Adv98ProbeFact]
    public async Task On_Kestrel_a_delivery_that_is_never_read_is_aborted_at_the_lease_and_its_records_stay_queued()
    {
        using var floor = Qa.RaiseThreadPoolFloor();
        var lease = TimeSpan.FromSeconds(5);
        var requests = new ProbeRequestTracker();
        await using var host = await ProbeKestrel.StartAsync(requests, m => m.Pickup.LeaseTimeout = lease);
        await using var mallory = await MediatorCast.MintPeerAsync();
        await using var alice = await MediatorCast.MintPeerAsync();
        await host.EnrolAsync(mallory);
        var store = (InMemoryMessageStore)host.App.Services.GetRequiredService<IMessageStore>();
        for (var i = 0; i < 10; i++)
        {
            var forward = ForwardMessage.Create(host.MediatorDid, mallory.Did, new[] { Qa.JsonPayloadOfSize(90 * 1024) });
            var packed = await alice.Client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { host.MediatorDid }, From: null, Forward: false));
            (await host.PostAsync(packed.Message)).Should().Be(HttpStatusCode.Accepted);
        }
        (await ProbeKestrel.UntilAsync(() => store.MessageCount == 10, Guard)).Should().BeTrue("the ten records are queued");

        var deliveryRequest = await host.AuthcryptAsync(mallory, MessagePickup.CreateDeliveryRequest(mallory.Did, host.MediatorDid, 10));
        var bodyBytes = Encoding.UTF8.GetBytes(deliveryRequest);
        using var tcp = new TcpClient { ReceiveBufferSize = 1024 };
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        var stream = tcp.GetStream();
        var inFlightBefore = requests.InFlight;
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /didcomm HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nContent-Type: {MediaTypes.Encrypted}\r\nContent-Length: {bodyBytes.Length}\r\n\r\n"));
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
        var started = await ProbeKestrel.UntilAsync(() => requests.LargeResponses == 1, Guard);
        var startedAt = Stopwatch.GetTimestamp();
        var ended = await ProbeKestrel.UntilAsync(() => requests.InFlight == inFlightBefore, Guard);
        var endedAfter = Stopwatch.GetElapsedTime(startedAt);
        var drained = await ProbeKestrel.DrainAsync(stream);

        Report($"F2 Kestrel: a {bodyBytes.Length} B delivery-request got a {requests.LargestLength} B 200 the client never read; the request ended {(ended ? $"{endedAfter.TotalSeconds:N1} s" : "never")} after the response started (lease {lease.TotalSeconds:N0} s); the client then read {drained.Bytes} B and saw {drained.End}; the store holds {store.MessageCount} records.");
        tcp.Dispose();

        started.Should().BeTrue();
        ended.Should().BeTrue("the 200 not committed within the lease is aborted");
        endedAfter.Should().BeLessThan(lease + TimeSpan.FromSeconds(10));
        store.MessageCount.Should().Be(10, "an aborted delivery loses nothing");
    }

    private void Report(string line)
    {
        _out.WriteLine(line);
        if (Environment.GetEnvironmentVariable("FX98_PROBE_OUT") is { Length: > 0 } path)
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
    }
}

/// <summary>A <see cref="FactAttribute"/> that runs only when <c>DIDCOMM_ADV98_PROBES=1</c>.</summary>
internal sealed class Adv98ProbeFactAttribute : FactAttribute
{
    public Adv98ProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DIDCOMM_ADV98_PROBES") != "1")
            Skip = "A real-Kestrel probe on real time: set DIDCOMM_ADV98_PROBES=1 to run it.";
    }
}

/// <summary>Counts POSTs in flight, those taken, and the 200s over 100 000 bytes that started.</summary>
internal sealed class ProbeRequestTracker
{
    private int _inFlight;
    private long _requests;
    private int _largeResponses;
    private long _largestLength;

    public int InFlight => Volatile.Read(ref _inFlight);

    public long Requests => Interlocked.Read(ref _requests);

    public int LargeResponses => Volatile.Read(ref _largeResponses);

    public long LargestLength => Interlocked.Read(ref _largestLength);

    public async Task Middleware(HttpContext context, RequestDelegate next)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }
        Interlocked.Increment(ref _inFlight);
        Interlocked.Increment(ref _requests);
        context.Response.OnStarting(() =>
        {
            if (context.Response.StatusCode == 200 && context.Response.ContentLength is long length && length > 100_000)
            {
                Interlocked.Exchange(ref _largestLength, Math.Max(Interlocked.Read(ref _largestLength), length));
                Interlocked.Increment(ref _largeResponses);
            }
            return Task.CompletedTask;
        });
        try
        {
            await next(context);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>A mediator on real Kestrel at its defaults, on a loopback port.</summary>
internal sealed class ProbeKestrelHost(WebApplication app, int port, string mediatorDid) : IAsyncDisposable
{
    public WebApplication App { get; } = app;

    public int Port { get; } = port;

    public string MediatorDid { get; } = mediatorDid;

    public async Task<HttpStatusCode> PostAsync(string packed)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/") };
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
        using var response = await http.PostAsync("didcomm", content);
        return response.StatusCode;
    }

    /// <summary>Stamp <paramref name="message"/> (created_time now, <c>return_route: all</c>) and authcrypt it from <paramref name="from"/>.</summary>
    public async Task<string> AuthcryptAsync(TestPeer from, Message message)
    {
        message.CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ReturnRouteHeaderWriter.Write(message, ReturnRouteMode.All, thread: null);
        return (await from.Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { MediatorDid }, From: from.Did, Forward: false))).Message;
    }

    /// <summary>Self-enrol <paramref name="peer"/> (mediate-request, then recipient-update adding its own DID).</summary>
    public async Task EnrolAsync(TestPeer peer)
    {
        (await PostAsync(await AuthcryptAsync(peer, CoordinateMediation.CreateMediateRequest(peer.Did, MediatorDid)))).Should().Be(HttpStatusCode.OK);
        (await PostAsync(await AuthcryptAsync(peer, CoordinateMediation.CreateRecipientUpdate(peer.Did, MediatorDid, new[] { new RecipientUpdate(peer.Did, RecipientUpdateAction.Add) })))).Should().Be(HttpStatusCode.OK);
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await App.StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The host gave up waiting for a connection a probe left behind.
        }
        await App.DisposeAsync();
    }
}

internal static class ProbeKestrel
{
    public static async Task<ProbeKestrelHost> StartAsync(ProbeRequestTracker requests, Action<MediatorOptions>? mediator = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec($"http://127.0.0.1:{port}/didcomm", Accept: new[] { "didcomm/v2" }));
        var secrets = new InMemorySecretsResolver();
        foreach (var key in identity.Privates)
            secrets.Add(key);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddMediator(m =>
            {
                m.CoordinateMediation.RoutingDids.Add(identity.Did);
                mediator?.Invoke(m);
            });
        });
        var app = builder.Build();
        app.Use(requests.Middleware);
        app.UseWebSockets();
        app.MapDidCommMediator("/didcomm");
        await app.StartAsync();
        return new ProbeKestrelHost(app, port, identity.Did);
    }

    /// <summary>Wait until the server has taken no new request for 2 s; returns when that stall began.</summary>
    public static async Task<long> StallAsync(ProbeRequestTracker requests)
    {
        var guard = Stopwatch.GetTimestamp();
        long last = -1;
        var lastChange = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(guard) < TimeSpan.FromSeconds(60))
        {
            var count = requests.Requests;
            if (count != last)
            {
                last = count;
                lastChange = Stopwatch.GetTimestamp();
            }
            else if (count > 0 && Stopwatch.GetElapsedTime(lastChange) >= TimeSpan.FromSeconds(2))
            {
                return lastChange;
            }
            await Task.Delay(50); // a poll of the count, not a synchronisation point
        }
        return lastChange;
    }

    /// <summary>Poll <paramref name="condition"/> on the monotonic clock until it holds or <paramref name="guard"/> passes.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan guard)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start) > guard)
                return false;
            await Task.Delay(50); // a poll, not a synchronisation point
        }
        return true;
    }

    /// <summary>Read what the server left on the socket until it ends (or 30 s); report the bytes and how it ended.</summary>
    public static async Task<(long Bytes, string End)> DrainAsync(NetworkStream stream)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, cts.Token);
                if (n == 0)
                    return (total, "end of stream");
                total += n;
            }
        }
        catch (OperationCanceledException)
        {
            return (total, "no end within 30 s");
        }
        catch (IOException ex)
        {
            return (total, ex.InnerException is SocketException se ? $"socket error {se.SocketErrorCode}" : ex.GetType().Name);
        }
    }
}
