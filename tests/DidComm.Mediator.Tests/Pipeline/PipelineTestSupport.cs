using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// A <see cref="TimeProvider"/> the test moves by hand. Timers (including the ones behind
/// <c>PeriodicTimer</c> and <c>Task.WaitAsync(TimeSpan, TimeProvider)</c>) fire only from
/// <see cref="Advance"/>, on the calling thread, after the clock has moved.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private TaskCompletionSource _timerChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
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

    /// <summary>How many timers are scheduled to fire.</summary>
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

    /// <summary>Wait (bounded, in real time) until at least <paramref name="count"/> timers are scheduled.</summary>
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

    /// <summary>Move the clock forward and fire every timer that came due, in due order.</summary>
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

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
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
/// An <see cref="IMeterFactory"/> whose meters carry it as their scope, so a test listens to its own
/// container's instruments only (tests run in parallel and share the meter name).
/// </summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly ConcurrentDictionary<string, Meter> _meters = new(StringComparer.Ordinal);

    public Meter Create(MeterOptions options)
        => _meters.GetOrAdd(options.Name, name => new Meter(new MeterOptions(name) { Version = options.Version, Tags = options.Tags, Scope = this }));

    public void Dispose()
    {
        foreach (var meter in _meters.Values)
            meter.Dispose();
    }
}

/// <summary>Records every measurement of the instruments whose meter belongs to one <see cref="TestMeterFactory"/>.</summary>
internal sealed class MeasurementRecorder : IDisposable
{
    private readonly MeterListener _listener = new();

    public MeasurementRecorder(TestMeterFactory factory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
                copy[tag.Key] = tag.Value;
            Measurements.Enqueue((instrument.Name, value, copy));
        });
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
                copy[tag.Key] = tag.Value;
            Measurements.Enqueue((instrument.Name, value, copy));
        });
        _listener.Start();
    }

    public ConcurrentQueue<(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags)> Measurements { get; } = new();

    /// <summary>Pull the current value of every observable instrument (they report only when asked).</summary>
    public void Observe() => _listener.RecordObservableInstruments();

    /// <summary>The sum recorded on <paramref name="instrument"/> with tag <paramref name="tag"/> = <paramref name="value"/> (or all, when <paramref name="tag"/> is null).</summary>
    public long Sum(string instrument, string? tag = null, string? value = null)
        => Measurements.Where(m => m.Instrument == instrument && (tag is null || Equals(m.Tags.GetValueOrDefault(tag), value))).Sum(m => m.Value);

    /// <summary>Every tag value recorded on <paramref name="instrument"/> under <paramref name="tag"/>, in order.</summary>
    public IReadOnlyList<string?> TagValues(string instrument, string tag)
        => Measurements.Where(m => m.Instrument == instrument).Select(m => m.Tags.GetValueOrDefault(tag) as string).ToArray();

    public void Dispose() => _listener.Dispose();
}

/// <summary>An <see cref="IRecipientEndpointDirectory"/> a test scripts; counts its calls.</summary>
internal sealed class ScriptedDirectory(Func<string, Uri?> find) : IRecipientEndpointDirectory
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Uri? Find(string grantPeerDid)
    {
        Interlocked.Increment(ref _calls);
        return find(grantPeerDid);
    }
}

/// <summary>An <see cref="ITransportRouter"/> a test scripts; records every request it is handed.</summary>
internal sealed class ScriptedTransportRouter(Func<TransportRequest, CancellationToken, Task<TransportResult>> send) : ITransportRouter
{
    public ConcurrentQueue<TransportRequest> Requests { get; } = new();

    public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return send(request, ct);
    }
}

/// <summary>Mints real did:peer identities (self-resolving, so any container resolves them) and keeps their secrets.</summary>
internal sealed class PipelinePeers : IAsyncDisposable
{
    private readonly ServiceProvider _minter;

    public PipelinePeers()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        _minter = services.BuildServiceProvider();
    }

    /// <summary>Every private key minted so far; hand it to the container under test.</summary>
    public InMemorySecretsResolver Secrets { get; } = new();

    /// <summary>A new did:peer:2 with an X25519 keyAgreement key, an Ed25519 key and, when given, one DIDComm service; its secrets go into <see cref="Secrets"/> unless <paramref name="holdSecrets"/> is false.</summary>
    public async Task<PeerIdentity> NewAsync(DidCommServiceSpec? service = null, bool holdSecrets = true)
    {
        var peer = await PeerIdentityFactory.CreateAsync(
            _minter.GetRequiredService<IDidManager>(),
            _minter.GetRequiredService<IKeyGenerator>(),
            _minter.GetRequiredService<ICryptoProvider>(),
            service);
        if (holdSecrets)
        {
            foreach (var jwk in peer.Privates)
                Secrets.Add(jwk);
        }
        return peer;
    }

    public ValueTask DisposeAsync() => _minter.DisposeAsync();
}

/// <summary>Log assertions shared by the pipeline tests.</summary>
internal static class PipelineLogs
{
    /// <summary>Lines (rendered plus structured values) that contain any of <paramref name="secrets"/>.</summary>
    public static IReadOnlyList<HandlerLogEntry> Leaking(HandlerLogSink sink, params string[] secrets)
        => sink.Entries.Where(e => secrets.Any(s => !string.IsNullOrEmpty(s) && e.Everything.Contains(s, StringComparison.Ordinal))).ToArray();

    /// <summary>The lines of one category.</summary>
    public static IReadOnlyList<HandlerLogEntry> Of<T>(HandlerLogSink sink)
        => sink.Entries.Where(e => e.Category == typeof(T).FullName).ToArray();
}
