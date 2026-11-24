using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Transports;
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

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #98 FX-1, review finding P1 on PR #130, on real Kestrel. Kestrel runs the completion callbacks
/// after it has sent the response, one at a time, the last registered first. A middleware that
/// registers its own asynchronous <c>Response.OnCompleted</c> cleanup after the endpoint therefore
/// runs before the endpoint's acknowledgement callback. When the deadline's jurisdiction ended only
/// with that callback, a cleanup slower than the deadline made it cancel the acknowledgement and
/// abort the connection although the client already held its response: a forward the sender held a
/// 202 for was dropped as <c>Dropped-Unacknowledged</c> (silent loss), and a delivery the client had
/// read was aborted. The deadline now ends when the response has been handed to the transport. The
/// deadline's clock is a hand-moved <see cref="QaManualTimeProvider"/> in the host's container, so
/// "past the deadline" needs no real waiting; the slow cleanup is held by the test, not slept.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class MediatorAcknowledgementDeadlineKestrelTests : IDisposable
{
    private const string DeadlineLine = "was not committed within its deadline";

    // Held for each test and put back when it ends (xunit disposes the instance after its test).
    private readonly IDisposable _floor = Qa.RaiseThreadPoolFloor();

    public void Dispose() => _floor.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task On_Kestrel_a_forward_whose_202_the_client_holds_is_relayed_once_although_a_later_completion_callback_outlasts_the_deadline(bool closeAfterReading)
    {
        // closeAfterReading = false: the review's case, the connection stays open and the deadline
        // passes while the cleanup runs. true: the client closes its connection once it has read the
        // 202, while the cleanup still runs; that abort must not take the acknowledgement back either.
        var time = new QaManualTimeProvider();
        await using var host = await DeadlineKestrelMediator.StartAsync(time);
        var bob = await host.EnrolAsync();
        var (forward, note) = await host.ForwardFromAliceAsync(bob);

        var cleanup = host.HoldCompletionOfNextPost();
        var response = await host.PostAsync(forward, closeAfterReading); // the client has read its whole 202
        // The later callback runs (the endpoint's own completion callback waits behind it). Kestrel runs
        // completion callbacks only once the request delegate has returned, so the endpoint's result,
        // which hands the response over, has run to its end by now.
        await cleanup.Started.Task.WaitAsync(MediatorTestServer.Timeout);
        var timersAfterHandingOver = time.ScheduledTimers;
        time.Advance(DidCommEndpointRouteBuilderExtensions.ForwardResponseDeadline); // the cleanup outlasts the deadline
        cleanup.Release.SetResult();
        await host.Handler.FlushAsync(MediatorTestServer.Timeout);
        var queued = host.Store.MessageCount;

        response.Status.Should().Be(HttpStatusCode.Accepted);
        host.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued }, "never Dropped-Unacknowledged after the client has its 202");
        queued.Should().Be(1, "the sender holds a 202, so the forward is relayed, exactly once");
        host.Logs.Entries.Should().NotContain(e => e.Contains(DeadlineLine, StringComparison.Ordinal));
        timersAfterHandingOver.Should().Be(host.IdleTimers, "handing the 202 to the transport ended its deadline");
        var fetched = await host.FetchAsync(bob);
        (await MediatorRecipient.UnpackFromSenderAsync(bob, fetched.Should().ContainSingle().Subject.Payload, host.Alice)).Message.Id.Should().Be(note.Id);
    }

    [Fact]
    public async Task On_Kestrel_a_delivery_the_client_has_read_is_not_aborted_although_a_later_completion_callback_outlasts_the_lease()
    {
        var time = new QaManualTimeProvider();
        await using var host = await DeadlineKestrelMediator.StartAsync(time);
        var bob = await host.EnrolAsync();
        var (forward, note) = await host.ForwardFromAliceAsync(bob);
        (await host.PostAsync(forward)).Status.Should().Be(HttpStatusCode.Accepted);
        await host.Handler.FlushAsync(MediatorTestServer.Timeout);
        await MediatorTestServer.WaitUntilAsync(() => time.ScheduledTimers == host.IdleTimers, "the forward's deadline is gone");

        var cleanup = host.HoldCompletionOfNextPost();
        var request = MessagePickup.CreateDeliveryRequest(bob.Did, host.Did, 10);
        var response = await host.PostAsync(await host.AuthcryptAsync(bob, request)); // the client has read its whole 200; the connection stays open
        await cleanup.Started.Task.WaitAsync(MediatorTestServer.Timeout); // as above: the endpoint's result has run to its end
        var timersAfterHandingOver = time.ScheduledTimers;
        time.Advance(new MessagePickupOptions().LeaseTimeout); // the cleanup outlasts the lease deadline
        var linesAtTheLease = host.Logs.Entries.Count(e => e.Contains(DeadlineLine, StringComparison.Ordinal));
        cleanup.Release.SetResult();
        var unpacked = await bob.Client.UnpackAsync(response.Body);
        var delivered = new PickupAnswer(unpacked.Message, Encoding.UTF8.GetBytes(response.Body)).Delivery(request.Id);

        response.Status.Should().Be(HttpStatusCode.OK);
        linesAtTheLease.Should().Be(0, "a delivery the client has read is never aborted");
        timersAfterHandingOver.Should().Be(host.IdleTimers, "handing the 200 to the transport ended its deadline");
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Should().ContainSingle().Subject.Payload, host.Alice)).Message.Id.Should().Be(note.Id);
    }
}

/// <summary>A held completion callback: <see cref="Started"/> once it runs, <see cref="Release"/> to let it finish.</summary>
internal sealed class DeadlineCompletionHold
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// A mediator (<c>AddMediator</c>, <c>MapDidCommMediator("/didcomm")</c>) on real Kestrel at its
/// defaults, with a hand-moved <see cref="TimeProvider"/> in its container and a middleware that can
/// hold a completion callback it registers after the endpoint ran, as cleanup middleware does.
/// </summary>
internal sealed class DeadlineKestrelMediator : IAsyncDisposable
{
    private readonly ConcurrentQueue<DeadlineCompletionHold> _holds = new();
    private readonly List<IAsyncDisposable> _owned = new();

    private readonly MediatorTestMeterFactory _meters;
    // Kept alive across requests, so a response's connection stays open after it was read.
    private readonly HttpClient _http = new() { Timeout = MediatorTestServer.Timeout };

    private DeadlineKestrelMediator(WebApplication app, int port, string did, CapturingLoggerProvider logs, MediatorTestMeterFactory meters, MediatorMeasurements measurements)
    {
        _meters = meters;
        App = app;
        Port = port;
        Did = did;
        Logs = logs;
        Measurements = measurements;
    }

    public WebApplication App { get; }

    public int Port { get; }

    public string Did { get; }

    public CapturingLoggerProvider Logs { get; }

    public MediatorMeasurements Measurements { get; }

    /// <summary>The timers scheduled while no request is in flight: the purge loop's.</summary>
    public int IdleTimers => 1;

    public MediatorSender Alice { get; private set; } = null!;

    public ForwardHandler Handler => App.Services.GetRequiredService<ForwardHandler>();

    public InMemoryMessageStore Store => (InMemoryMessageStore)App.Services.GetRequiredService<IMessageStore>();

    public IReadOnlyList<string?> Outcomes => Measurements.TagValues(ForwardOutcomes.ForwardOutcomesInstrument, ForwardOutcomes.OutcomeTag);

    public static async Task<DeadlineKestrelMediator> StartAsync(QaManualTimeProvider time)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
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
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddMediator(m => m.CoordinateMediation.RoutingDids.Add(identity.Did));
        });
        var app = builder.Build();
        DeadlineKestrelMediator? self = null;
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            DeadlineCompletionHold? hold = null;
            if (HttpMethods.IsPost(context.Request.Method))
                self!._holds.TryDequeue(out hold);
            await next(context);
            // Registered after the endpoint ran, so Kestrel runs it before the endpoint's own callback.
            if (hold is not null)
            {
                context.Response.OnCompleted(static async state =>
                {
                    var held = (DeadlineCompletionHold)state;
                    held.Started.TrySetResult();
                    await Task.WhenAny(held.Release.Task, Task.Delay(MediatorTestServer.Timeout));
                }, hold);
            }
        });
        app.UseWebSockets();
        app.MapDidCommMediator("/didcomm");
        self = new DeadlineKestrelMediator(app, port, identity.Did, logs, meters, measurements);
        await app.StartAsync();
        await time.WaitForScheduledTimersAsync(1, MediatorTestServer.Timeout); // the purge loop
        self.Alice = await MediatorSender.CreateAsync(new MediatorNetwork(), new TestClock(DateTimeOffset.UtcNow));
        self._owned.Add(self.Alice);
        return self;
    }

    /// <summary>The next POST's completion is held by a callback the middleware registers after the endpoint ran.</summary>
    public DeadlineCompletionHold HoldCompletionOfNextPost()
    {
        var hold = new DeadlineCompletionHold();
        _holds.Enqueue(hold);
        return hold;
    }

    /// <summary>Bob, granted mediation with his own DID registered (through the store: no request, no timer).</summary>
    public async Task<TestPeer> EnrolAsync()
    {
        var bob = await MediatorCast.MintPeerAsync();
        _owned.Add(bob);
        var mediation = App.Services.GetRequiredService<IMediationStore>();
        (await mediation.GrantAsync(new MediationGrant(bob.Did, new[] { Did }), CancellationToken.None)).Should().NotBeNull();
        (await mediation.UpdateRecipientsAsync(bob.Did, new[] { new RecipientUpdate(bob.Did, RecipientUpdateAction.Add) }, CancellationToken.None))
            .Should().Equal(RecipientStoreOutcome.Added);
        return bob;
    }

    /// <summary>Alice's note to <paramref name="bob"/> in a forward anoncrypted to this mediator.</summary>
    public async Task<(string Packed, Message Note)> ForwardFromAliceAsync(TestPeer bob)
    {
        var note = Alice.Note(bob.Did);
        var (packed, _) = await Alice.ForwardAsync(Did, bob.Did, await Alice.AuthcryptAsync(note, bob.Did));
        return (packed, note);
    }

    /// <summary>Stamp <paramref name="request"/> (created_time now, <c>return_route: all</c>) and authcrypt it from <paramref name="from"/>.</summary>
    public async Task<string> AuthcryptAsync(TestPeer from, Message request)
    {
        request.CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ReturnRouteHeaderWriter.Write(request, ReturnRouteMode.All, thread: null);
        return (await from.Client.PackEncryptedAsync(request, new PackEncryptedOptions(Recipients: new[] { Did }, From: from.Did, Forward: false))).Message;
    }

    /// <summary>
    /// POST <paramref name="packed"/> and read the whole response: on the host's kept-alive client, or
    /// on a client of its own that closes its connection once the response has been read.
    /// </summary>
    public async Task<(HttpStatusCode Status, string Body)> PostAsync(string packed, bool closeAfterReading = false)
    {
        using var own = closeAfterReading ? new HttpClient { Timeout = MediatorTestServer.Timeout } : null;
        var http = own ?? _http;
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
        using var response = await http.PostAsync($"http://127.0.0.1:{Port}/didcomm", content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A <c>delivery-request</c> from <paramref name="peer"/>; the delivered messages.</summary>
    public async Task<IReadOnlyList<DeliveredMessage>> FetchAsync(TestPeer peer)
    {
        var request = MessagePickup.CreateDeliveryRequest(peer.Did, Did, 10);
        var response = await PostAsync(await AuthcryptAsync(peer, request));
        response.Status.Should().Be(HttpStatusCode.OK);
        var unpacked = await peer.Client.UnpackAsync(response.Body);
        return new PickupAnswer(unpacked.Message, Encoding.UTF8.GetBytes(response.Body)).Delivery(request.Id);
    }

    public async ValueTask DisposeAsync()
    {
        while (_holds.TryDequeue(out var hold))
            hold.Release.TrySetResult();
        _http.Dispose();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await App.StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The host gave up waiting for a request a test left behind.
        }
        await App.DisposeAsync();
        foreach (var owned in _owned)
            await owned.DisposeAsync();
        Measurements.Dispose();
        _meters.Dispose();
    }
}
