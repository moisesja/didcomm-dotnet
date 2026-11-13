using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Transports;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.WebSocket;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Mediator;

/// <summary>A test collection that runs alone, after the parallel ones (timing measurements).</summary>
[Xunit.CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QaSerialTiming
{
    public const string Name = "QA-2 serial timing";
}

/// <summary>
/// QA-2 (#95) helpers shared by the <c>*QaTests</c> files: a hand-moved <see cref="TimeProvider"/>,
/// forward shapers, a parking message store, a WebSocket sender, and the FR-MED-08 timing method.
/// </summary>
internal static class Qa
{
    /// <summary>The thread-pool floor the QA tests raise to before blocking threads (CI runners have 2 cores).</summary>
    public static void RaiseThreadPoolFloor(int minimum = 64)
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        if (worker < minimum || io < minimum)
            ThreadPool.SetMinThreads(Math.Max(worker, minimum), Math.Max(io, minimum));
    }

    /// <summary>Set a raw <c>delay_milli</c> header value on a forward.</summary>
    public static Action<Message> DelayRaw(JsonElement value)
        => forward => (forward.AdditionalHeaders ??= new Dictionary<string, JsonElement>())["delay_milli"] = value;

    /// <summary>Set an integer <c>delay_milli</c> on a forward.</summary>
    public static Action<Message> Delay(long millis) => DelayRaw(JsonSerializer.SerializeToElement(millis));

    /// <summary>Set <c>expires_time</c> on a forward.</summary>
    public static Action<Message> Expires(DateTimeOffset at) => forward => forward.ExpiresTime = at.ToUnixTimeSeconds();

    /// <summary>Compose shapers.</summary>
    public static Action<Message> All(params Action<Message>[] shapers) => m =>
    {
        foreach (var s in shapers)
            s(m);
    };

    /// <summary>A JSON object whose compact serialisation is exactly <paramref name="bytes"/> ASCII bytes (an opaque onward payload).</summary>
    public static string JsonPayloadOfSize(int bytes)
    {
        const string Prefix = "{\"protected\":\"";
        const string Suffix = "\"}";
        var fill = bytes - Prefix.Length - Suffix.Length;
        fill.Should().BeGreaterThan(0);
        var text = Prefix + new string('a', fill) + Suffix;
        Encoding.UTF8.GetByteCount(text).Should().Be(bytes);
        return text;
    }

    /// <summary>Post <paramref name="packed"/> and expect the forward's 202 with an empty body.</summary>
    public static async Task PostForwardAsync(MediatorTestServer mediator, string packed, string? path = null)
    {
        var response = await mediator.PostAsync(packed, path);
        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
    }

    /// <summary>Alice's note to <paramref name="to"/> in a hand-built forward with <c>next</c> = <paramref name="next"/> (the recipient by default), posted; returns the note.</summary>
    public static async Task<Message> SendNoteAsync(MediatorTestServer mediator, MediatorSender alice, TestPeer to, Action<Message>? shape = null, string? next = null, bool flush = true, string content = "hello")
    {
        var note = alice.Note(to.Did, content);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, next ?? to.Did, await alice.AuthcryptAsync(note, to.Did), shape);
        await PostForwardAsync(mediator, packed);
        if (flush)
            await mediator.FlushAsync();
        return note;
    }

    /// <summary>Unpack <paramref name="payload"/> as <paramref name="recipient"/> and return the inner message's body <c>content</c>.</summary>
    public static async Task<(Message Message, UnpackResult Unpacked)> OpenAsync(TestPeer recipient, ReadOnlyMemory<byte> payload)
    {
        var unpacked = await recipient.Client.UnpackAsync(Encoding.UTF8.GetString(payload.Span));
        unpacked.Encrypted.Should().BeTrue();
        unpacked.Message.To.Should().Contain(recipient.Did);
        return (unpacked.Message, unpacked);
    }

    /// <summary>The median of <paramref name="values"/>.</summary>
    public static double Median(IReadOnlyCollection<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    /// <summary>POST <paramref name="packed"/> and return status, every response header (name: value) and the body length, timed.</summary>
    public static async Task<(HttpStatusCode Status, string Headers, int BodyLength, double Millis)> TimedPostAsync(MediatorTestServer mediator, HttpClient client, string packed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, mediator.Options.Pattern)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
        var watch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();
        watch.Stop();
        var headers = string.Join("|", response.Headers.Concat(response.Content.Headers)
            .Where(h => !string.Equals(h.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => h.Key + ":" + string.Join(",", h.Value)));
        return (response.StatusCode, headers, body.Length, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>Log lines of <paramref name="logs"/> that carry any of <paramref name="values"/>.</summary>
    public static IReadOnlyList<string> LinesWith(CapturingLoggerProvider logs, IEnumerable<string> values)
    {
        var sensitive = values.Where(v => !string.IsNullOrEmpty(v)).Distinct().ToArray();
        return logs.Entries.Where(line => sensitive.Any(v => line.Contains(v, StringComparison.Ordinal))).ToList();
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> the test moves by hand (a copy of the one in <c>DidComm.Mediator.Tests</c>,
/// which this project cannot see). Timers fire only from <see cref="Advance"/>, on the calling thread.
/// </summary>
internal sealed class QaManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private TaskCompletionSource _timerChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _now;

    public QaManualTimeProvider(DateTimeOffset? start = null)
        => _now = start ?? new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
            return _now.UtcTicks;
    }

    public int ScheduledTimers
    {
        get
        {
            lock (_gate)
                return _timers.Count(t => t.DueAt is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public async Task WaitForScheduledTimersAsync(int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_timers.Count(t => t.DueAt is not null) >= count)
                    return;
                changed = _timerChanged.Task;
            }
            await changed.WaitAsync(cts.Token);
        }
    }

    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(by));
        DateTimeOffset target;
        lock (_gate)
            target = _now + by;

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.DueAt is { } due && due <= target).OrderBy(t => t.DueAt).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                _now = next.DueAt!.Value > _now ? next.DueAt.Value : _now;
                next.DueAt = next.Period is { } period ? next.DueAt + period : null;
            }
            next.Fire();
        }
    }

    private void Changed(ManualTimer timer, DateTimeOffset? dueAt, TimeSpan? period, bool remove)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            timer.DueAt = dueAt;
            timer.Period = period;
            if (remove)
                _timers.Remove(timer);
            else if (!_timers.Contains(timer))
                _timers.Add(timer);
            signal = _timerChanged;
            _timerChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
    }

    private sealed class ManualTimer(QaManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }
        public TimeSpan? Period { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DateTimeOffset? dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
            TimeSpan? every = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            owner.Changed(this, dueAt, every, remove: false);
            return true;
        }

        public void Dispose() => owner.Changed(this, null, null, remove: true);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A host <see cref="DidComm.Mediator.Pickup.IMessageStore"/> around the product's in-memory store: counts enqueues
/// (the sink's only write) and can hold the next N enqueues at the door until released.
/// </summary>
internal sealed class QaParkingStore : DidComm.Mediator.Pickup.IMessageStore
{
    private readonly ConcurrentQueue<Parked> _parked = new();
    private int _enqueued;
    private int _entered;

    public QaParkingStore(DidComm.Mediator.Pickup.InMemoryMessageStore inner) => Inner = inner;

    public QaParkingStore() : this(new DidComm.Mediator.Pickup.InMemoryMessageStore(new DidComm.Mediator.Pickup.MessagePickupOptions())) { }

    public DidComm.Mediator.Pickup.InMemoryMessageStore Inner { get; }

    /// <summary>Enqueues that completed.</summary>
    public int Enqueued => Volatile.Read(ref _enqueued);

    /// <summary>Enqueues that reached the store (before any parking).</summary>
    public int Entered => Volatile.Read(ref _entered);

    /// <summary>Hold the next enqueue at the door.</summary>
    public Parked ParkNextEnqueue()
    {
        var parked = new Parked();
        _parked.Enqueue(parked);
        return parked;
    }

    public async ValueTask<DidComm.Mediator.Pickup.EnqueueOutcome> EnqueueAsync(DidComm.Mediator.Pickup.QueuedMessage message, DidComm.Mediator.Pickup.MessageLease? lease, DateTimeOffset now, CancellationToken ct)
    {
        Interlocked.Increment(ref _entered);
        if (_parked.TryDequeue(out var parked))
            await parked.ArriveAsync();
        var outcome = await Inner.EnqueueAsync(message, lease, now, ct);
        Interlocked.Increment(ref _enqueued);
        return outcome;
    }

    public ValueTask<IReadOnlyList<DidComm.Mediator.Pickup.QueuedMessage>> LeaseAsync(DidComm.Mediator.Pickup.MessageLeaseRequest request, CancellationToken ct) => Inner.LeaseAsync(request, ct);
    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<DidComm.Mediator.Pickup.QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);

    public sealed class Parked
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Arrived => _arrived.Task;

        public void Release() => _gate.TrySetResult();

        internal async Task ArriveAsync()
        {
            _arrived.TrySetResult();
            await Task.WhenAny(_gate.Task, Task.Delay(MediatorTestServer.Timeout));
        }
    }
}

/// <summary>
/// A sender built the way an application would, like <see cref="MediatorSender"/>, but for a peer the
/// test chose (one with its own <c>DIDCommMessaging</c> service) and over either transport: HTTP over
/// the test network, or the WebSocket transport connected to <paramref name="mediator"/>'s TestServer
/// (<c>SendAsync</c> reaches the mediator when the mediator DID's service names a <c>ws://</c> URI).
/// </summary>
internal sealed class QaSender : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private QaSender(TestPeer peer, ServiceProvider services)
    {
        Peer = peer;
        _services = services;
        Client = services.GetRequiredService<DidCommClient>();
    }

    public TestPeer Peer { get; }

    public string Did => Peer.Did;

    public DidCommClient Client { get; }

    public static async Task<QaSender> CreateAsync(MediatorTestServer mediator, bool webSocket, DidCommServiceSpec? service = null)
    {
        var peer = await MediatorCast.MintPeerAsync(service);
        mediator.AddSensitive(peer);
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(mediator.Logs);
        });
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(peer.Secrets);
            if (webSocket)
            {
                b.UseWebSocketTransport(o =>
                {
                    o.AllowedSchemes = new[] { "ws" };
                    o.MaxReconnectAttempts = 0;
                    o.WebSocketFactory = () => new TestServerClientWebSocket();
                    o.Connect = (socket, uri, ct) => ((TestServerClientWebSocket)socket).ConnectAsync(mediator.Server, uri, ct);
                });
            }
            else
            {
                b.UseHttpTransport(MediatorTestServer.ConfigureHttp);
            }
            b.Configure(o =>
            {
                o.Clock = () => mediator.Clock.Now;
                foreach (var host in MediatorNetwork.KnownHosts)
                    o.OutboundEndpointPolicy.AllowedHosts.Add(host);
            });
        });
        if (!webSocket)
            services.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(mediator.Network.CreateHandler);
        return new QaSender(peer, services.BuildServiceProvider());
    }

    public Message Note(string to, string content = "hello")
        => new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(Did)
            .WithTo(to)
            .WithBody(new JsonObject { ["content"] = content })
            .Build();

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await Peer.DisposeAsync();
    }
}
