using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.TestSupport;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.Metrics;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// A real container with <c>AddMediator()</c> and direct push on, a scripted transport router behind
/// the real <c>DidCommClient</c>, a scripted endpoint directory, a manual clock for the push
/// deadline, a meter factory of its own and captured logs. No network.
/// </summary>
internal sealed class PushFixture : IAsyncDisposable
{
    public const string MediatorDid = "did:example:push-mediator";

    private PushFixture(ServiceProvider services, ManualTimeProvider time, TestMeterFactory meters, HandlerLogSink logs, ScriptedTransportRouter router, ScriptedDirectory? directory)
    {
        Services = services;
        Time = time;
        Meters = meters;
        Recorder = new MeasurementRecorder(meters);
        Logs = logs;
        Router = router;
        Directory = directory;
    }

    public ServiceProvider Services { get; }
    public ManualTimeProvider Time { get; }
    public TestMeterFactory Meters { get; }
    public MeasurementRecorder Recorder { get; }
    public HandlerLogSink Logs { get; }
    public ScriptedTransportRouter Router { get; }
    public ScriptedDirectory? Directory { get; }

    public DirectPushDispatcher Dispatcher => Services.GetRequiredService<DirectPushDispatcher>();
    public MessagePickupQueue Queue => Services.GetRequiredService<MessagePickupQueue>();
    public IMediationStore Mediation => Services.GetRequiredService<IMediationStore>();
    public InMemoryMessageStore Store => (InMemoryMessageStore)Services.GetRequiredService<IMessageStore>();

    /// <summary>The number of pushes recorded with <paramref name="outcome"/>.</summary>
    public long Pushes(string outcome) => Recorder.Sum(MediatorMetrics.DirectPushOutcomesInstrument, MediatorMetrics.PushOutcomeTag, outcome);

    /// <summary>Every push outcome recorded, in order.</summary>
    public IReadOnlyList<string?> PushOutcomeLog => Recorder.TagValues(MediatorMetrics.DirectPushOutcomesInstrument, MediatorMetrics.PushOutcomeTag);

    /// <param name="send">What the transport does with each push; default: accept with 202.</param>
    /// <param name="directory">The endpoint per grant; <c>null</c> registers no directory at all.</param>
    /// <param name="configure">More mediator settings (direct push is already on).</param>
    /// <param name="services">More registrations, made before <c>AddDidComm</c>.</param>
    /// <param name="blockPrivateNetworks">The SSRF guard's switch; off by default so the scripted endpoints need no DNS.</param>
    public static PushFixture Create(
        Func<TransportRequest, CancellationToken, Task<TransportResult>>? send = null,
        Func<string, Uri?>? directory = null,
        Action<MediatorOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        bool blockPrivateNetworks = false)
    {
        var time = new ManualTimeProvider();
        var meters = new TestMeterFactory();
        var logs = new HandlerLogSink();
        var router = new ScriptedTransportRouter(send ?? ((_, _) => Task.FromResult(new TransportResult(true, 202))));
        var scripted = directory is null ? null : new ScriptedDirectory(directory);

        var collection = new ServiceCollection();
        collection.AddSingleton(logs);
        collection.AddSingleton(typeof(ILogger<>), typeof(HandlerCapturingLogger<>));
        collection.AddSingleton<TimeProvider>(time);
        collection.AddSingleton<IMeterFactory>(meters);
        collection.AddSingleton<ITransportRouter>(router);
        if (scripted is not null)
            collection.AddSingleton<IRecipientEndpointDirectory>(scripted);
        services?.Invoke(collection);
        collection.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.Configure(o => o.OutboundEndpointPolicy.BlockPrivateNetworks = blockPrivateNetworks);
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(MediatorDid);
                o.DirectPush = true;
                configure?.Invoke(o);
            });
        });
        return new PushFixture(collection.BuildServiceProvider(), time, meters, logs, router, scripted);
    }

    /// <summary>Enrol <paramref name="peerDid"/> (straight through the store) and register <paramref name="recipientDids"/> under its grant.</summary>
    public async Task EnrolAsync(string peerDid, params string[] recipientDids)
    {
        var grant = await Mediation.GrantAsync(new MediationGrant(peerDid, new[] { MediatorDid }), CancellationToken.None)
            ?? throw new InvalidOperationException("The mediation store is full.");
        _ = grant;
        if (recipientDids.Length > 0)
        {
            var outcomes = await Mediation.UpdateRecipientsAsync(peerDid, recipientDids.Select(d => new RecipientUpdate(d, RecipientUpdateAction.Add)).ToArray(), CancellationToken.None);
            if (outcomes.Any(o => o is not (RecipientStoreOutcome.Added or RecipientStoreOutcome.AlreadyRegistered)))
                throw new InvalidOperationException("A recipient DID could not be registered.");
        }
    }

    /// <summary>Queue a message for <paramref name="recipientDid"/> and build the push job the sink would hand over.</summary>
    public async Task<PushJob> QueuedJobAsync(string recipientDid, byte[]? payload = null, string forwardId = "fwd-1")
    {
        payload ??= Payload(64);
        var result = await Queue.DeliverAsync(recipientDid, payload);
        if (result.Outcome != PickupDeliveryOutcome.Queued || result.MessageId is null || result.GrantPeerDid is null)
            throw new InvalidOperationException($"Not queued ({result.Outcome}).");
        return new PushJob(result.GrantPeerDid, recipientDid, result.MessageId, payload, forwardId);
    }

    public static byte[] Payload(int size, byte seed = 7)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
            bytes[i] = (byte)(seed + i * 13);
        return bytes;
    }

    /// <summary>A transport failure whose text names the endpoint, as the HTTP transport's does.</summary>
    public static TransportException TransportFailure(TransportRequest request, int status)
        => new($"POST {request.Endpoint} failed with {status}.", status, request.Endpoint.Scheme);

    public async ValueTask DisposeAsync()
    {
        Recorder.Dispose();
        await Services.DisposeAsync();
        Meters.Dispose();
    }
}

/// <summary>A gate a scripted transport waits on: it records arrivals and lets the test release them.</summary>
internal sealed class PushGate
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = new();
    private int _arrived;

    public int Arrived => Volatile.Read(ref _arrived);

    public void Open() => _open.TrySetResult();

    /// <summary>Record an arrival, then wait for the gate (or the token).</summary>
    public async Task PassAsync(CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _arrived);
        lock (_gate)
        {
            foreach (var waiter in _waiters.Where(w => w.Count <= n).ToArray())
            {
                waiter.Signal.TrySetResult();
                _waiters.Remove(waiter);
            }
        }
        await _open.Task.WaitAsync(ct);
    }

    /// <summary>Wait (bounded, in real time) until <paramref name="count"/> callers have arrived.</summary>
    public Task WaitForArrivalsAsync(int count, TimeSpan timeout)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _arrived) >= count)
                return Task.CompletedTask;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, signal));
            return signal.Task.WaitAsync(timeout);
        }
    }
}
