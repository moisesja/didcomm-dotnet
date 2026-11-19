using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.InteropTests.Transports;
using DidComm.Mediator;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Facade;
using DidComm.Transports.ReturnRoute;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.Adv98.D4;

/// <summary>
/// #98 D4 (E-6) on real Kestrel: the transport acknowledgement of a forward
/// (<c>ProtocolContext.Acknowledged</c>, completed from <c>Response.OnCompleted</c>) against a raw
/// socket. (a) An abort before the 202 is committed never relays. (b) A sender that never reads a
/// byte of its 202 is relayed anyway: "committed to Kestrel's output" is not "the sender holds the
/// 202", so a sender that gives up and retries gets two copies relayed (at-least-once).
/// (c) Two pipelined forwards on one connection, the second aborted while held. (d) Malformed
/// framing is rejected or read exactly as Kestrel frames it; never an extra relay. (e) A sender that
/// half-closes after its request: a 202 it can read always pairs with a relay, a drop never does.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class D4KestrelAcknowledgementTests
{
    private readonly ITestOutputHelper _output;

    public D4KestrelAcknowledgementTests(ITestOutputHelper output)
    {
        _output = output;
        Qa.RaiseThreadPoolFloor();
    }

    [Fact]
    public async Task D4a_A_forward_whose_socket_closes_while_its_202_is_held_is_never_relayed_on_Kestrel()
    {
        await using var mediator = await D4KestrelMediator.StartAsync();
        var (packed, _) = await mediator.ForwardForEnrolledBobAsync();
        var hold = mediator.HoldNextPost();

        using var tcp = await mediator.ConnectRawAsync();
        await tcp.GetStream().WriteAsync(D4KestrelMediator.PostBytes(mediator.Port, packed));
        await hold.Parked.Task.WaitAsync(MediatorTestServer.Timeout);

        tcp.Client.Shutdown(SocketShutdown.Both);
        tcp.Close();
        await hold.Aborted.Task.WaitAsync(MediatorTestServer.Timeout);
        hold.Release.SetResult();

        await MediatorTestServer.WaitUntilAsync(() => mediator.Outcome(ForwardOutcomes.DroppedUnacknowledged) == 1, "the abort cancels the acknowledgement");
        await mediator.Handler.FlushAsync(MediatorTestServer.Timeout);
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedUnacknowledged);
        mediator.Store.MessageCount.Should().Be(0, "the sender never received the 202, so nothing is relayed");
    }

    [Fact]
    public async Task D4b_A_sender_that_never_reads_its_202_is_relayed_and_its_retry_is_relayed_again_on_Kestrel()
    {
        await using var mediator = await D4KestrelMediator.StartAsync();
        var (packed, _) = await mediator.ForwardForEnrolledBobAsync();

        // The sender posts and never reads; its receive buffer is as small as the OS allows.
        using var tcp = new TcpClient(AddressFamily.InterNetwork) { ReceiveBufferSize = 1 };
        await tcp.ConnectAsync(IPAddress.Loopback, mediator.Port);
        await tcp.GetStream().WriteAsync(D4KestrelMediator.PostBytes(mediator.Port, packed));

        await MediatorTestServer.WaitUntilAsync(() => mediator.Outcome(ForwardOutcomes.Queued) == 1, "the forward is relayed once its 202 is in Kestrel's output");
        var unreadAtRelay = tcp.Available; // bytes the kernel holds for the sender that it never read

        // The sender gives up without reading (RST) and retries the same envelope on a new connection.
        tcp.LingerState = new LingerOption(true, 0);
        tcp.Close();
        var retry = await mediator.PostAsync(packed);

        await MediatorTestServer.WaitUntilAsync(() => mediator.Outcome(ForwardOutcomes.Queued) == 2, "the retry is relayed too");
        await mediator.Handler.FlushAsync(MediatorTestServer.Timeout);
        retry.Should().Be(HttpStatusCode.Accepted);
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);
        mediator.Store.MessageCount.Should().Be(2, "a forward is outside replay admission: the sender that never read its 202 gets both copies relayed");
        _output.WriteLine($"D4b: unread bytes at the sender when the forward was relayed: {unreadAtRelay}");
    }

    [Fact]
    public async Task D4c_Two_pipelined_forwards_the_second_aborted_while_held_relay_exactly_the_first_on_Kestrel()
    {
        await using var mediator = await D4KestrelMediator.StartAsync();
        var (first, _) = await mediator.ForwardForEnrolledBobAsync();
        var (second, _) = await mediator.ForwardForEnrolledBobAsync(enrol: false);
        mediator.PassNextPost();
        var hold = mediator.HoldNextPost();

        using var tcp = await mediator.ConnectRawAsync();
        var both = D4KestrelMediator.PostBytes(mediator.Port, first).Concat(D4KestrelMediator.PostBytes(mediator.Port, second)).ToArray();
        await tcp.GetStream().WriteAsync(both);
        var response1 = await D4KestrelMediator.ReadHeadAsync(tcp.GetStream());
        await hold.Parked.Task.WaitAsync(MediatorTestServer.Timeout);

        tcp.Client.Shutdown(SocketShutdown.Both);
        tcp.Close();
        await hold.Aborted.Task.WaitAsync(MediatorTestServer.Timeout);
        hold.Release.SetResult();

        await MediatorTestServer.WaitUntilAsync(() => mediator.Outcomes.Count == 2, "both pipelined forwards reach an outcome");
        await mediator.Handler.FlushAsync(MediatorTestServer.Timeout);
        response1.Should().StartWith("HTTP/1.1 202");
        mediator.Outcomes.Should().BeEquivalentTo(new[] { ForwardOutcomes.Queued, ForwardOutcomes.DroppedUnacknowledged }, "the pump records Queued after processing, so the order is not fixed");
        mediator.Store.MessageCount.Should().Be(1);
    }

    public static TheoryData<string> Framings => new()
    {
        "cl-and-te",        // Content-Length and Transfer-Encoding: chunked; a smuggled second request after the last chunk
        "duplicate-cl",     // two different Content-Length headers
        "chunked-trailing", // chunked body followed by garbage bytes
        "cl-short",         // Content-Length one byte shorter than the body; the extra byte starts the next "request"
    };

    [Theory]
    [MemberData(nameof(Framings))]
    public async Task D4d_Malformed_framing_never_relays_more_forwards_than_Kestrel_framed_requests(string framing)
    {
        await using var mediator = await D4KestrelMediator.StartAsync();
        var (packed, _) = await mediator.ForwardForEnrolledBobAsync();
        var (smuggled, _) = await mediator.ForwardForEnrolledBobAsync(enrol: false);
        var body = Encoding.UTF8.GetBytes(packed);
        var smuggledRequest = D4KestrelMediator.PostBytes(mediator.Port, smuggled);
        var head = $"POST /didcomm HTTP/1.1\r\nHost: 127.0.0.1:{mediator.Port}\r\nContent-Type: {MediaTypes.Encrypted}\r\n";
        byte[] wire = framing switch
        {
            "cl-and-te" => Concat(
                Encoding.ASCII.GetBytes(head + $"Content-Length: {body.Length}\r\nTransfer-Encoding: chunked\r\n\r\n{body.Length:x}\r\n"),
                body, Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"), smuggledRequest),
            "duplicate-cl" => Concat(Encoding.ASCII.GetBytes(head + $"Content-Length: {body.Length}\r\nContent-Length: {body.Length + 5}\r\n\r\n"), body, "xxxxx"u8.ToArray()),
            "chunked-trailing" => Concat(
                Encoding.ASCII.GetBytes(head + $"Transfer-Encoding: chunked\r\n\r\n{body.Length:x}\r\n"),
                body, Encoding.ASCII.GetBytes("\r\n0\r\n\r\nGARBAGE\r\n\r\n")),
            "cl-short" => Concat(Encoding.ASCII.GetBytes(head + $"Content-Length: {body.Length - 1}\r\n\r\n"), body),
            _ => throw new ArgumentOutOfRangeException(nameof(framing)),
        };

        // A sentinel request closes the connection once Kestrel has framed everything before it
        // (no half-close: a FIN aborts the request on Kestrel, see D4e).
        var sentinel = Encoding.ASCII.GetBytes($"GET /d4-sentinel HTTP/1.1\r\nHost: 127.0.0.1:{mediator.Port}\r\nConnection: close\r\n\r\n");
        using var tcp = await mediator.ConnectRawAsync();
        var stream = tcp.GetStream();
        await stream.WriteAsync(Concat(wire, sentinel));
        var answer = await D4KestrelMediator.ReadToEndAsync(stream);

        // Every request Kestrel framed reached a status line; wait for the handler to settle.
        var statuses = answer.Split("HTTP/1.1 ", StringSplitOptions.RemoveEmptyEntries).Select(s => s[..3]).ToList();
        await mediator.Handler.FlushAsync(MediatorTestServer.Timeout);
        _output.WriteLine($"D4d {framing}: statuses [{string.Join(",", statuses)}] outcomes [{string.Join(",", mediator.Outcomes)}] stored {mediator.Store.MessageCount}");
        var framed202 = statuses.Count(s => s == "202");
        mediator.Store.MessageCount.Should().BeLessThanOrEqualTo(framed202, "a forward is relayed only for a request Kestrel answered with a 202");
        mediator.Outcomes.Count.Should().BeLessThanOrEqualTo(statuses.Count, "no dispatch without a framed request");
    }

    [Fact]
    public async Task D4e_A_sender_that_half_closes_after_a_complete_request_is_answered_consistently_with_the_relay_on_Kestrel()
    {
        // HTTP/1.1 allows a client to half-close after its request; Kestrel treats the FIN as a
        // closed connection and fires RequestAborted. The invariant: a 202 the sender can read
        // implies a relay (no silent loss), and a drop implies no readable 202.
        await using var mediator = await D4KestrelMediator.StartAsync();
        var (packed, _) = await mediator.ForwardForEnrolledBobAsync();
        var hold = mediator.HoldNextPost();

        using var tcp = await mediator.ConnectRawAsync();
        var stream = tcp.GetStream();
        await stream.WriteAsync(D4KestrelMediator.PostBytes(mediator.Port, packed));
        await hold.Parked.Task.WaitAsync(MediatorTestServer.Timeout);
        tcp.Client.Shutdown(SocketShutdown.Send);
        await hold.Aborted.Task.WaitAsync(MediatorTestServer.Timeout);
        hold.Release.SetResult();
        var answer = await D4KestrelMediator.ReadToEndAsync(stream);

        await MediatorTestServer.WaitUntilAsync(() => mediator.Outcomes.Count == 1, "the forward reaches an outcome");
        await mediator.Handler.FlushAsync(MediatorTestServer.Timeout);
        _output.WriteLine($"D4e: answer [{answer.Split('\r')[0]}] outcomes [{string.Join(",", mediator.Outcomes)}] stored {mediator.Store.MessageCount}");
        var got202 = answer.StartsWith("HTTP/1.1 202", StringComparison.Ordinal);
        (got202 ? mediator.Store.MessageCount : 1).Should().Be(1, "a sender that can read a 202 must have its forward relayed");
        (mediator.Outcome(ForwardOutcomes.DroppedUnacknowledged) == 1 ? got202 : false).Should().BeFalse("a dropped forward never pairs with a readable 202");
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}

/// <summary>
/// #98 D4 attack 1 on real Kestrel: a live push whose WebSocket is reset (RST) before the recipient
/// read or acknowledged it is not lost (the dead session's lease is given back on its close and the
/// record is redelivered, same id, on the next socket), and the next arrival goes to the new socket,
/// never the dead one.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class D4KestrelLiveResetTests
{
    public D4KestrelLiveResetTests() => Qa.RaiseThreadPoolFloor();

    [Fact]
    public async Task D4_A_live_push_reset_unread_is_redelivered_on_the_next_socket_and_the_next_arrival_goes_there_on_Kestrel()
    {
        await using var mediator = await D4KestrelMediator.StartAsync();
        var (first, _) = await mediator.ForwardForEnrolledBobAsync();
        var bob = mediator.Bob;

        var ws1 = await mediator.OpenSocketAsync();
        (await mediator.AskAsync(ws1, MessagePickup.CreateLiveDeliveryChange(bob.Did, mediator.Did, true))).Type.Should().Be(MessagePickup.StatusType);
        (await mediator.PostAsync(first)).Should().Be(HttpStatusCode.Accepted);
        await MediatorTestServer.WaitUntilAsync(() => mediator.PushOutcome(PushOutcomes.PushedLive) == 1, "the first arrival is pushed to socket 1");

        // Socket 1 dies without reading or acknowledging the push.
        ws1.Abort();
        ws1.Dispose();

        // Socket 1's leases are given back in the background once Kestrel sees it close. Poll Bob's
        // pickup on socket 2, the record asserted below; the monotonic clock is only a hang guard.
        var ws2 = await mediator.OpenSocketAsync();
        IReadOnlyList<DeliveredMessage>? redelivered = null;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (redelivered is null && System.Diagnostics.Stopwatch.GetElapsedTime(started) < MediatorTestServer.Timeout)
        {
            var answer = await mediator.AskAsync(ws2, MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10));
            if (MessagePickup.TryParseDelivery(answer, out var messages, out _))
                redelivered = messages;
            else
                await Task.Delay(20);
        }

        (await mediator.AskAsync(ws2, MessagePickup.CreateLiveDeliveryChange(bob.Did, mediator.Did, true))).Type.Should().Be(MessagePickup.StatusType);
        var (second, _) = await mediator.ForwardForEnrolledBobAsync(enrol: false);
        (await mediator.PostAsync(second)).Should().Be(HttpStatusCode.Accepted);
        await MediatorTestServer.WaitUntilAsync(() => mediator.PushOutcome(PushOutcomes.PushedLive) == 2, "the second arrival is pushed");
        var pushed = await mediator.ReceiveAsync(ws2);

        redelivered.Should().ContainSingle("the unacknowledged push is deliverable again once socket 1 closed");
        MessagePickup.TryParseDelivery(pushed, out var live, out _).Should().BeTrue();
        live!.Should().ContainSingle().Which.Id.Should().NotBe(redelivered![0].Id, "the new arrival, on the new socket");
        mediator.Store.MessageCount.Should().Be(2);
        mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedLive, PushOutcomes.PushedLive);
        await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        ws2.Dispose();
    }
}

/// <summary>One parked POST: <see cref="Parked"/> when its response reaches <c>OnStarting</c>, <see cref="Aborted"/> when <c>RequestAborted</c> fires; held until <see cref="Release"/>.</summary>
file sealed class D4Hold
{
    public TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The #98 Kestrel template (adversarial plan §0): <c>AddMediator()</c> + <c>MapDidCommMediator("/didcomm")</c> on a loopback port, Kestrel defaults.</summary>
file sealed class D4KestrelMediator : IAsyncDisposable
{
    private readonly ConcurrentQueue<D4Hold?> _holds = new();
    private readonly List<IAsyncDisposable> _owned = new();
    private TestPeer? _bob;
    private MediatorSender? _alice;

    private D4KestrelMediator(WebApplication app, int port, string did, TestClock clock, MediatorTestMeterFactory meters, MediatorMeasurements measurements, CapturingLoggerProvider logs)
    {
        App = app;
        Port = port;
        Did = did;
        Clock = clock;
        Meters = meters;
        Measurements = measurements;
        Logs = logs;
    }

    public WebApplication App { get; }
    public int Port { get; }
    public string Did { get; }
    public TestClock Clock { get; }
    public MediatorTestMeterFactory Meters { get; }
    public MediatorMeasurements Measurements { get; }
    public CapturingLoggerProvider Logs { get; }
    public ForwardHandler Handler => App.Services.GetRequiredService<ForwardHandler>();
    public InMemoryMessageStore Store => (InMemoryMessageStore)App.Services.GetRequiredService<IMessageStore>();
    public long Outcome(string outcome) => Measurements.Sum(ForwardOutcomes.ForwardOutcomesInstrument, ForwardOutcomes.OutcomeTag, outcome);
    public IReadOnlyList<string?> Outcomes => Measurements.TagValues(ForwardOutcomes.ForwardOutcomesInstrument, ForwardOutcomes.OutcomeTag);

    public TestPeer Bob => _bob ?? throw new InvalidOperationException("Build a forward first.");
    public long PushOutcome(string outcome) => Measurements.Sum(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag, outcome);
    public IReadOnlyList<string?> PushOutcomes => Measurements.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag);

    public async Task<ClientWebSocket> OpenSocketAsync()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/didcomm"), CancellationToken.None);
        return ws;
    }

    /// <summary>Bob's request (stamped, return_route all, authcrypted to the mediator) on <paramref name="ws"/>; returns the next frame, unpacked.</summary>
    public async Task<Message> AskAsync(ClientWebSocket ws, Message request)
    {
        request.CreatedTime = Clock.Now.ToUnixTimeSeconds();
        ReturnRouteHeaderWriter.Write(request, ReturnRouteMode.All, thread: null);
        var packed = await Bob.Client.PackEncryptedAsync(request, new PackEncryptedOptions(Recipients: new[] { Did }, From: Bob.Did, Forward: false));
        await ws.SendAsync(Encoding.UTF8.GetBytes(packed.Message), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        return await ReceiveAsync(ws);
    }

    public async Task<Message> ReceiveAsync(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(MediatorTestServer.Timeout);
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        var unpacked = await Bob.Client.UnpackAsync(Encoding.UTF8.GetString(ms.ToArray()));
        return unpacked.Message;
    }

    public D4Hold HoldNextPost()
    {
        var hold = new D4Hold();
        _holds.Enqueue(hold);
        return hold;
    }

    public void PassNextPost() => _holds.Enqueue(null);

    public static async Task<D4KestrelMediator> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec($"http://127.0.0.1:{port}/didcomm", Accept: new[] { "didcomm/v2" }));
        var secrets = new InMemorySecretsResolver();
        foreach (var jwk in identity.Privates)
            secrets.Add(jwk);
        var logs = new CapturingLoggerProvider();
        var meters = new MediatorTestMeterFactory();
        var measurements = new MediatorMeasurements(meters);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<IMeterFactory>(meters);
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddMediator(m => m.CoordinateMediation.RoutingDids.Add(identity.Did));
            b.Configure(o => o.Clock = () => clock.Now);
        });
        var app = builder.Build();
        D4KestrelMediator? self = null;
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) && self!._holds.TryDequeue(out var hold) && hold is not null)
            {
                context.RequestAborted.Register(static h => ((D4Hold)h!).Aborted.TrySetResult(), hold);
                context.Response.OnStarting(static async h =>
                {
                    var held = (D4Hold)h;
                    held.Parked.TrySetResult();
                    await held.Release.Task;
                }, hold);
            }
            await next(context);
        });
        app.UseWebSockets();
        app.MapDidCommMediator("/didcomm");
        self = new D4KestrelMediator(app, port, identity.Did, clock, meters, measurements, logs);
        await app.StartAsync();
        return self;
    }

    /// <summary>Enrol Bob (through the store) once, and build a forward from Alice to him.</summary>
    public async Task<(string Packed, Message Forward)> ForwardForEnrolledBobAsync(bool enrol = true)
    {
        if (_bob is null)
        {
            _bob = await MediatorCast.MintPeerAsync();
            _owned.Add(_bob);
            _alice = await MediatorSender.CreateAsync(new MediatorNetwork(), Clock);
            _owned.Add(_alice);
        }
        if (enrol)
        {
            var mediation = App.Services.GetRequiredService<IMediationStore>();
            if (await mediation.GetGrantAsync(_bob.Did, CancellationToken.None) is null)
            {
                (await mediation.GrantAsync(new MediationGrant(_bob.Did, new[] { Did }), CancellationToken.None)).Should().NotBeNull();
                (await mediation.UpdateRecipientsAsync(_bob.Did, new[] { new RecipientUpdate(_bob.Did, RecipientUpdateAction.Add) }, CancellationToken.None))
                    .Should().Equal(RecipientStoreOutcome.Added);
            }
        }
        return await _alice!.ForwardAsync(Did, _bob.Did, await _alice.AuthcryptAsync(_alice.Note(_bob.Did), _bob.Did));
    }

    public async Task<TcpClient> ConnectRawAsync()
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork);
        await tcp.ConnectAsync(IPAddress.Loopback, Port);
        return tcp;
    }

    public static byte[] PostBytes(int port, string packed)
    {
        var body = Encoding.UTF8.GetBytes(packed);
        var head = Encoding.ASCII.GetBytes($"POST /didcomm HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: {MediaTypes.Encrypted}\r\nContent-Length: {body.Length}\r\n\r\n");
        return head.Concat(body).ToArray();
    }

    public async Task<HttpStatusCode> PostAsync(string packed)
    {
        using var http = new HttpClient { Timeout = MediatorTestServer.Timeout };
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
        using var response = await http.PostAsync($"http://127.0.0.1:{Port}/didcomm", content);
        return response.StatusCode;
    }

    /// <summary>Read one response head (status line and headers; the mediator's 202 carries no body).</summary>
    public static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        using var cts = new CancellationTokenSource(MediatorTestServer.Timeout);
        while (!(buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n'))
        {
            var read = await stream.ReadAsync(one, cts.Token);
            if (read == 0)
                break;
            buffer.Add(one[0]);
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    public static async Task<string> ReadToEndAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(MediatorTestServer.Timeout);
        using var ms = new MemoryStream();
        try
        {
            await stream.CopyToAsync(ms, cts.Token);
        }
        catch (IOException)
        {
            // Kestrel may reset the connection after its 400; what was read is what counts.
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hold in _holds)
            hold?.Release.TrySetResult();
        await App.StopAsync();
        await App.DisposeAsync();
        foreach (var owned in _owned)
            await owned.DisposeAsync();
        Measurements.Dispose();
        Meters.Dispose();
    }
}
