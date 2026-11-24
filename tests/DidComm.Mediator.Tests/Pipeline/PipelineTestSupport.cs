using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// A <see cref="TimeProvider"/> the test moves by hand. Timers (including the ones behind
/// <c>PeriodicTimer</c> and <c>Task.WaitAsync(TimeSpan, TimeProvider)</c>) fire only from
/// <see cref="Advance"/>, on the calling thread, after the clock has moved. The clock never moves
/// back: an <see cref="Advance"/> made while another one runs (from a timer callback, or a fake
/// mediator's handler on another thread) is kept, not overwritten (#119, shape I).
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private TaskCompletionSource _timerChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _now;
    private long _created;

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

    /// <summary>The earliest instant a scheduled timer is due at, or <c>null</c> when none is scheduled (to move the clock one firing at a time).</summary>
    public DateTimeOffset? NextDueAt
    {
        get
        {
            lock (_gate)
                return _timers.Where(t => t.DueAt is not null).Select(t => t.DueAt).Min();
        }
    }

    /// <summary>Whether a timer is scheduled whose relative due time was last set to exactly <paramref name="dueTime"/>.</summary>
    public bool IsScheduled(TimeSpan dueTime)
    {
        lock (_gate)
            return _timers.Any(t => t.DueAt is not null && t.DueTime == dueTime);
    }

    /// <summary>How many timers created after <paramref name="mark"/> (a <see cref="Mark"/>) are scheduled to fire.</summary>
    public int ScheduledTimersSince(long mark)
    {
        lock (_gate)
            return _timers.Count(t => t.Sequence > mark && t.DueAt is not null);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        long sequence;
        lock (_gate)
            sequence = ++_created;
        var timer = new ManualTimer(this, callback, state, sequence);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// A mark for <see cref="WaitForTimerAsync"/>: every timer created after this call has a higher
    /// creation number than the value returned.
    /// </summary>
    public long Mark()
    {
        lock (_gate)
            return _created;
    }

    /// <summary>
    /// Wait (bounded, in real time) until at least <paramref name="count"/> timers are scheduled.
    /// Any timer counts, whoever created it and whenever; to wait for one particular timer, use
    /// <see cref="WaitForTimerAsync"/>.
    /// </summary>
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

    /// <summary>
    /// Wait (bounded, in real time) until a timer created after <paramref name="createdAfter"/> (a
    /// <see cref="Mark"/>) is scheduled with exactly <paramref name="dueTime"/> (the relative due time
    /// it was last set to). A rendezvous on one particular timer — the one a step under test sets —
    /// that neither an older timer nor a timer of another length can satisfy. Times out with the
    /// timers that are scheduled, so a step that set the wrong one says so.
    /// </summary>
    public async Task WaitForTimerAsync(TimeSpan dueTime, long createdAfter, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            Task changed;
            string scheduled;
            lock (_gate)
            {
                if (_timers.Any(t => t.Sequence > createdAfter && t.DueAt is not null && t.DueTime == dueTime))
                    return;
                changed = _timerChanged.Task;
                scheduled = string.Join(", ", _timers.Where(t => t.DueAt is not null).Select(t => $"#{t.Sequence} {t.DueTime}"));
            }
            try
            {
                await changed.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"No timer of {dueTime} created after #{createdAfter} was scheduled within {timeout}; scheduled: [{scheduled}].");
            }
        }
    }

    /// <summary>
    /// Move the clock forward by <paramref name="by"/> and fire every timer that came due, in due
    /// order; returns how many fired. The clock ends at the later of the target and wherever an
    /// <see cref="Advance"/> made meanwhile (by a fired timer's callback, or on another thread) has
    /// moved it: never back.
    /// </summary>
    public int Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(by));
        DateTimeOffset target;
        lock (_gate)
            target = _now + by;

        var fired = 0;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.DueAt is { } due && due <= target).OrderBy(t => t.DueAt).FirstOrDefault();
                if (next is null)
                {
                    if (target > _now)
                        _now = target;
                    return fired;
                }
                _now = next.DueAt!.Value > _now ? next.DueAt.Value : _now;
                next.DueAt = next.Period is { } period ? next.DueAt + period : null;
            }
            next.Fire();
            fired++;
        }
    }

    private void Changed(ManualTimer timer, TimeSpan? dueTime, TimeSpan? period, bool remove)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            // The due instant is computed under the lock, from the clock as it is now.
            timer.DueAt = dueTime is { } relative ? _now + relative : null;
            timer.DueTime = dueTime;
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

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, long sequence) : ITimer
    {
        /// <summary>The creation number (see <see cref="Mark"/>).</summary>
        public long Sequence { get; } = sequence;

        public DateTimeOffset? DueAt { get; set; }

        /// <summary>The relative due time the timer was last set to (<c>null</c>: not scheduled).</summary>
        public TimeSpan? DueTime { get; set; }

        public TimeSpan? Period { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            TimeSpan? relative = dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;
            TimeSpan? every = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            owner.Changed(this, relative, every, remove: false);
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
    private readonly object _gate = new();
    // Completed (and replaced) after every measurement is enqueued; never runs a waiter on the
    // recording thread, which is the product's.
    private TaskCompletionSource _recorded = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            Recorded();
        });
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
                copy[tag.Key] = tag.Value;
            Measurements.Enqueue((instrument.Name, value, copy));
            Recorded();
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

    /// <summary>
    /// Wait until <paramref name="condition"/> holds over what has been recorded: a rendezvous on the
    /// records a test asserts, not on a state counter the product updates before it records (#119).
    /// <paramref name="timeout"/> is a hang guard only (real time); on it, the message lists every
    /// measurement recorded.
    /// </summary>
    public async Task WaitForAsync(Func<MeasurementRecorder, bool> condition, TimeSpan timeout, string because)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            Task recorded;
            lock (_gate)
                recorded = _recorded.Task; // taken before the check: a measurement after it completes this one
            if (condition(this))
                return;
            try
            {
                await recorded.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Not within {timeout}: {because}. Recorded: [{string.Join(", ", Measurements.Select(Describe))}]");
            }
        }
    }

    public void Dispose() => _listener.Dispose();

    private void Recorded()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            signal = _recorded;
            _recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
    }

    private static string Describe((string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags) m)
        => $"{m.Instrument}{{{string.Join(",", m.Tags.Select(t => $"{t.Key}={t.Value}"))}}}={m.Value}";
}

/// <summary>An <see cref="IMediationStore"/> over the real in-memory store whose recipient lookup a test can replace (to make it throw or park).</summary>
internal sealed class ScriptedMediationStore : IMediationStore
{
    private readonly InMemoryMediationStore _inner = new(new CoordinateMediationOptions());

    public Func<string, CancellationToken, ValueTask<MediationGrant?>>? OnFind { get; set; }

    public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct) => _inner.GetGrantAsync(peerDid, ct);
    public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct) => _inner.GrantAsync(grant, ct);
    public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct) => _inner.RevokeAsync(peerDid, ct);
    public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct) => _inner.UpdateRecipientsAsync(peerDid, updates, ct);
    public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct) => _inner.ListRecipientsAsync(peerDid, offset, limit, ct);

    public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct)
        => OnFind is { } hook ? hook(recipientDid, ct) : _inner.FindGrantByRecipientDidAsync(recipientDid, ct);
}

/// <summary>An <see cref="IMessageStore"/> over the real in-memory store whose purge, lease, acknowledgement, give-back and status read a test can replace; signals every purge call.</summary>
internal sealed class ScriptedMessageStore : IMessageStore
{
    private readonly object _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = new();
    private int _purges;

    public InMemoryMessageStore Inner { get; } = new(new MessagePickupOptions());

    /// <summary>Replaces the purge; the default purges the inner store.</summary>
    public Func<DateTimeOffset, CancellationToken, ValueTask<int>>? OnPurgeExpired { get; set; }

    public int Purges => Volatile.Read(ref _purges);

    /// <summary>Wait (bounded, in real time) until <paramref name="count"/> purge calls have completed.</summary>
    public Task WaitForPurgesAsync(int count, TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_purges >= count)
                return Task.CompletedTask;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, signal));
            return signal.Task.WaitAsync(timeout);
        }
    }

    public async ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            return OnPurgeExpired is { } hook ? await hook(now, ct) : await Inner.PurgeExpiredAsync(now, ct);
        }
        finally
        {
            lock (_gate)
            {
                _purges++;
                foreach (var waiter in _waiters.Where(w => w.Count <= _purges).ToArray())
                {
                    waiter.Signal.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }
        }
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => Inner.EnqueueAsync(message, lease, now, ct);
    /// <summary>Replaces the lease (the take before a push or a pickup); the default leases in the inner store.</summary>
    public Func<MessageLeaseRequest, CancellationToken, ValueTask<IReadOnlyList<QueuedMessage>>>? OnLease { get; set; }
    public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => OnLease is { } hook ? hook(request, ct) : Inner.LeaseAsync(request, ct);
    /// <summary>Replaces the acknowledgement; the default acknowledges in the inner store.</summary>
    public Func<string, IReadOnlyList<string>, CancellationToken, ValueTask<int>>? OnAcknowledge { get; set; }

    /// <summary>Replaces the give-back by lease id; the default gives back in the inner store.</summary>
    public Func<string, IReadOnlyList<string>, string, CancellationToken, ValueTask<int>>? OnRelease { get; set; }

    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
        => OnAcknowledge is { } hook ? hook(grantPeerDid, messageIds, ct) : Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct)
        => OnRelease is { } hook ? hook(grantPeerDid, messageIds, leaseId, ct) : Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    /// <summary>Replaces the give-back of a push that was not sent; the default gives back in the inner store.</summary>
    public Func<string, IReadOnlyList<string>, string, CancellationToken, ValueTask<int>>? OnReturnUnsent { get; set; }
    public ValueTask<int> ReturnUnsentAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct)
        => OnReturnUnsent is { } hook ? hook(grantPeerDid, messageIds, leaseId, ct) : Inner.ReturnUnsentAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);
    /// <summary>Replaces the status read; the default reads the inner store.</summary>
    public Func<string, string?, DateTimeOffset, CancellationToken, ValueTask<QueueStatus>>? OnGetStatus { get; set; }
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct)
        => OnGetStatus is { } hook ? hook(grantPeerDid, recipientDid, now, ct) : Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
}

/// <summary>An <see cref="IServiceEndpointResolver"/> a test scripts per DID.</summary>
internal sealed class ScriptedServiceResolver(Func<string, CancellationToken, Task<IReadOnlyList<DidCommServiceInfo>>> resolve) : IServiceEndpointResolver
{
    public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default) => resolve(did, ct);
}

/// <summary>A live (WebSocket-like) return-route session that takes every message and records it.</summary>
internal sealed class LiveTestSession(string peerDid, string localDid) : IReturnRouteSession
{
    private readonly CancellationTokenSource _closed = new();

    public ConcurrentQueue<Message> Sent { get; } = new();
    public string PeerDid { get; } = peerDid;
    public string LocalDid { get; } = localDid;
    public ReturnRouteMode Mode => ReturnRouteMode.All;
    public string? Thread => null;
    public ReturnRouteTransport Transport => ReturnRouteTransport.WebSocket;
    public bool IsLive => true;
    public CancellationToken Closed => _closed.Token;

    public ValueTask<bool> TrySendAsync(Message message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return ValueTask.FromResult(true);
    }

    /// <summary>What a sized send (a live push) does instead of taking the message; <c>null</c> takes every one.</summary>
    public Func<Message, CancellationToken, Task<ReturnRouteSendResult>>? OnPush { get; set; }

    /// <summary>End the session, as a dropped connection does.</summary>
    public void Close() => _closed.Cancel();

    public async ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct)
    {
        var result = OnPush is { } push ? await push(message, ct) : ReturnRouteSendResult.Sent;
        if (result == ReturnRouteSendResult.Sent)
            Sent.Enqueue(message);
        return result;
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

    /// <summary>Like <see cref="NewAsync"/>, with a service entry built by hand (one the library's builder would refuse, such as an unsupported scheme).</summary>
    public async Task<PeerIdentity> NewWithServiceEntryAsync(NetDid.Core.Model.Service serviceEntry, bool holdSecrets = true)
    {
        var peer = await PeerIdentityFactory.CreateWithServiceEntryAsync(
            _minter.GetRequiredService<IDidManager>(),
            _minter.GetRequiredService<IKeyGenerator>(),
            _minter.GetRequiredService<ICryptoProvider>(),
            serviceEntry);
        if (holdSecrets)
        {
            foreach (var jwk in peer.Privates)
                Secrets.Add(jwk);
        }
        return peer;
    }

    /// <summary>A <c>DIDCommMessaging</c> service entry with exactly this <c>uri</c>, unchecked.</summary>
    public static NetDid.Core.Model.Service RawDidCommService(string uri) => new()
    {
        Id = "#didcomm",
        Type = "DIDCommMessaging",
        ServiceEndpoint = NetDid.Core.Model.ServiceEndpointValue.FromMap(new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["uri"] = System.Text.Json.JsonSerializer.SerializeToElement(uri),
        }),
    };

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
