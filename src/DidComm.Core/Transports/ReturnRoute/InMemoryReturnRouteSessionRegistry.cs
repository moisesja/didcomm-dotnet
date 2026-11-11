using System.Threading.Channels;
using DidComm.Consistency;
using Microsoft.Extensions.Logging;

namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The default <see cref="IReturnRouteSessionRegistry"/>: process-local, bounded by
/// <see cref="ReturnRouteSessionOptions"/>. Registered as a singleton by <c>AddDidComm</c>, whose
/// container disposes it with the host; a host that registers its own
/// <see cref="IReturnRouteSessionRegistry"/> first keeps it.
/// </summary>
/// <remarks>
/// <para>
/// All state lives under one lock and every decision — already registered, per-peer eviction,
/// total bound, insert — is made inside it, so two concurrent registrations for one peer at the
/// bound evict exactly one session each. A peer at its own per-peer bound replaces its oldest
/// session even when the registry is at <see cref="ReturnRouteSessionOptions.MaxSessions"/> (the
/// total does not grow); only a registration that would grow the total is refused there. Eviction
/// is O(1) per registration (the peer's list is in registration order, so its oldest is the first
/// element); there is no amortized sweep. Log lines carry counts only, never a DID.
/// </para>
/// <para>
/// Notifications are best-effort and come from one notifier per registry. A
/// <see cref="Register"/> or <see cref="Unregister"/> that changed something writes one
/// notification — that call's events, with the subscribers captured at that moment — to a bounded
/// queue inside the lock, right after the state change, and returns: it never waits for room and
/// never runs a subscriber. One consumer loop, started once without the creating caller's
/// <see cref="ExecutionContext"/> (so no request's ambient state is captured for the registry's
/// lifetime, and none flows from the writers either), raises the notifications one at a time in
/// queue order, which is the order of the state changes: a registration's
/// <see cref="SessionClosed"/> (<see cref="SessionCloseReason.Evicted"/>) and then its
/// <see cref="SessionOpened"/>, an unregistration's <see cref="SessionClosed"/>
/// (<see cref="SessionCloseReason.Unregistered"/>). Subscribers are therefore never called
/// concurrently; each runs in its own <c>try</c>/<c>catch</c> (the exception type name is logged),
/// so a throwing subscriber never ends the loop. A subscriber that blocks holds the notifier's one
/// pool thread and stalls this registry's later notifications only — never a registration, an
/// eviction, <see cref="Find"/>, or the registrant's closing of an evicted session.
/// </para>
/// <para>
/// Backpressure: at most <see cref="ReturnRouteSessionOptions.MaxPendingNotifications"/>
/// notifications wait, besides the one being delivered. A call that finds them all taken drops the
/// oldest waiting one, counts it in <see cref="DroppedNotifications"/> and, after releasing the
/// lock, logs a Warning with the running total for the first drop and every 1000th. The oldest go
/// because under backpressure the newest changes describe the registry as it now is, while the
/// oldest are already superseded — an open whose session has since been evicted; refusing the new
/// notification instead would deliver stale history and lose the current changes. A dropped
/// notification leaves the queue, so it retains no session. <see cref="IReturnRouteSession.Closed"/>
/// stays the authoritative, prompt per-session signal.
/// </para>
/// <para>
/// <see cref="Dispose"/> stops the notifier without waiting for it (see there); the registry
/// itself keeps working. A registry that is never disposed — the endpoints' per-application
/// fallback when the host registered none — costs no thread while idle: its notifier is a parked
/// asynchronous loop, which becomes unreachable together with the registry.
/// </para>
/// </remarks>
public sealed class InMemoryReturnRouteSessionRegistry : IReturnRouteSessionRegistry, IDisposable
{
    private const long WarnEvery = 1000;

    private readonly ReturnRouteSessionOptions _options;
    private readonly ILogger<InMemoryReturnRouteSessionRegistry>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<IReturnRouteSession>> _byPeer = new(StringComparer.Ordinal);
    private readonly HashSet<IReturnRouteSession> _all = new(ReferenceEqualityComparer.Instance);
    private readonly Channel<PendingNotification> _notifications;
    private readonly Task _notifier;
    private int _count;
    private long _dropped;
    private int _delivering;
    private volatile bool _disposed;

    /// <summary>Create a registry with default <see cref="ReturnRouteSessionOptions"/>.</summary>
    public InMemoryReturnRouteSessionRegistry() : this(new ReturnRouteSessionOptions(), logger: null) { }

    /// <summary>Create a registry with the given bounds; validates them.</summary>
    /// <param name="options">The bounds; <see cref="ReturnRouteSessionOptions.Validate"/> runs here.</param>
    public InMemoryReturnRouteSessionRegistry(ReturnRouteSessionOptions options) : this(options, logger: null) { }

    /// <summary>Create a registry with the given bounds and a logger; validates the bounds.</summary>
    /// <param name="options">The bounds; <see cref="ReturnRouteSessionOptions.Validate"/> runs here.</param>
    /// <param name="logger">Optional logger; lines carry counts only.</param>
    public InMemoryReturnRouteSessionRegistry(ReturnRouteSessionOptions options, ILogger<InMemoryReturnRouteSessionRegistry>? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _logger = logger;
        _notifications = Channel.CreateBounded<PendingNotification>(
            new BoundedChannelOptions(options.MaxPendingNotifications)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                // Writes happen inside the registry lock: a waiting notifier must resume on the
                // thread pool, never inline on the writing thread.
                AllowSynchronousContinuations = false,
            },
            // Runs inside the write, so inside the registry lock: count only, no logging, no host code.
            _ => Interlocked.Increment(ref _dropped));
        _notifier = StartNotifier();
    }

    /// <inheritdoc />
    public event EventHandler<ReturnRouteSessionEventArgs>? SessionOpened;

    /// <inheritdoc />
    public event EventHandler<ReturnRouteSessionEventArgs>? SessionClosed;

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>
    /// Lifecycle notifications discarded without being delivered: the oldest waiting one each time
    /// a call found <see cref="ReturnRouteSessionOptions.MaxPendingNotifications"/> already waiting
    /// (a subscriber is not keeping up), plus, once the registry is disposed, every notification
    /// still waiting and every one a later call would have queued. A notification is the events of
    /// one <see cref="Register"/> or <see cref="Unregister"/> call; a call made while nobody
    /// subscribes has nothing to deliver and drops nothing. For monitoring: a growing value means
    /// subscribers are missing lifecycle changes and must rely on
    /// <see cref="IReturnRouteSession.Closed"/> and <see cref="Find"/>.
    /// </summary>
    public long DroppedNotifications => Interlocked.Read(ref _dropped);

    /// <summary>Notifications queued plus the one being delivered (at most <see cref="ReturnRouteSessionOptions.MaxPendingNotifications"/> + 1). For tests.</summary>
    internal int PendingNotifications => _notifications.Reader.Count + Volatile.Read(ref _delivering);

    /// <summary>The notifier loop; it completes once the registry is disposed and the loop has left any subscriber it was in. For tests.</summary>
    internal Task Notifier => _notifier;

    /// <inheritdoc />
    public SessionRegistration Register(IReturnRouteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var peer = session.PeerDid;
        var subject = DidSubject.DidSubjectOf(peer);
        if (subject is null || !string.Equals(subject, peer, StringComparison.Ordinal))
            throw new ArgumentException("The session's PeerDid must be a bare DID subject.", nameof(session));

        IReturnRouteSession? evicted = null;
        int count;
        long dropped;
        lock (_gate)
        {
            if (_all.Contains(session))
                return new SessionRegistration(SessionRegistrationOutcome.AlreadyRegistered, null);

            // The per-peer bound is checked first: a peer at its own bound replaces its oldest
            // session, which never grows the total and never touches another DID — so a peer
            // reconnecting while the registry is full (its old socket not yet noticed dead) is not
            // locked out by a flood of other DIDs. Only a registration that would grow the total
            // is refused at MaxSessions.
            _byPeer.TryGetValue(peer, out var list);
            if (list is not null && list.Count >= _options.MaxSessionsPerPeer)
            {
                evicted = list[0];
                list.RemoveAt(0);
                _all.Remove(evicted);
                _count--;
            }
            else if (_count >= _options.MaxSessions)
            {
                count = _count;
                goto Refused;
            }

            if (list is null)
            {
                list = new List<IReturnRouteSession>();
                _byPeer[peer] = list;
            }

            list.Add(session);
            _all.Add(session);
            _count++;
            count = _count;
            dropped = Enqueue(
                evicted is null ? null : Capture(SessionClosed, evicted, SessionCloseReason.Evicted),
                Capture(SessionOpened, session, SessionCloseReason.None));
        }

        if (evicted is not null)
            SafeLogInformation("Return-route session evicted: the peer reached MaxSessionsPerPeer ({MaxSessionsPerPeer}); {Count} sessions registered.", _options.MaxSessionsPerPeer, count);
        LogDropped(dropped);
        return new SessionRegistration(SessionRegistrationOutcome.Registered, evicted);

    Refused:
        SafeLogInformation("Return-route session refused: the registry is at MaxSessions ({MaxSessions}); {Count} sessions registered.", _options.MaxSessions, count);
        return new SessionRegistration(SessionRegistrationOutcome.RefusedTotalBound, null);
    }

    /// <inheritdoc />
    public bool Unregister(IReturnRouteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        long dropped;
        lock (_gate)
        {
            if (!_all.Remove(session))
                return false;
            _count--;
            if (_byPeer.TryGetValue(session.PeerDid, out var list))
            {
                list.Remove(session);
                if (list.Count == 0)
                    _byPeer.Remove(session.PeerDid);
            }
            dropped = Enqueue(Capture(SessionClosed, session, SessionCloseReason.Unregistered), null);
        }
        LogDropped(dropped);
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<IReturnRouteSession> Find(string did)
    {
        var subject = DidSubject.DidSubjectOf(did);
        if (subject is null)
            return Array.Empty<IReturnRouteSession>();
        lock (_gate)
        {
            return _byPeer.TryGetValue(subject, out var list)
                ? list.ToArray()
                : Array.Empty<IReturnRouteSession>();
        }
    }

    /// <summary>
    /// Stop the notifier. Idempotent, and never waits — neither for a subscriber that does not
    /// return nor for the notifier to finish. Notifications still waiting are never delivered: they
    /// are dropped and counted in <see cref="DroppedNotifications"/> when the notifier stops — at
    /// once when it is idle, otherwise when the subscriber call it is in returns. The notifier
    /// checks for the stop before every subscriber call, so a call already running completes and no
    /// further one starts. The registry keeps working: <see cref="Register"/>, <see cref="Unregister"/>,
    /// <see cref="Find"/> and <see cref="Count"/> behave as before (an endpoint may still unregister
    /// its sessions while the host shuts down), and the notifications of those calls are dropped and
    /// counted.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            // Under the lock, so every write either precedes the completion (and is dropped when
            // the notifier stops) or fails after it (and is counted by Enqueue).
            _notifications.Writer.TryComplete();
        }
    }

    /// <summary>One event of one call: the subscribers captured when the call queued it, and its arguments.</summary>
    private readonly record struct Notification(EventHandler<ReturnRouteSessionEventArgs> Handlers, ReturnRouteSessionEventArgs Args);

    /// <summary>What one call queued: its events in the order they are raised (either may be absent).</summary>
    private readonly record struct PendingNotification(Notification? First, Notification? Second);

    /// <summary>The event for <paramref name="session"/>, or <c>null</c> when nobody subscribes to it.</summary>
    private static Notification? Capture(EventHandler<ReturnRouteSessionEventArgs>? handlers, IReturnRouteSession session, SessionCloseReason reason)
        => handlers is null ? null : new Notification(handlers, new ReturnRouteSessionEventArgs(session, reason));

    /// <summary>
    /// Queue one call's events. Called inside the registry lock, right after the state change, so
    /// the queue order is the state-change order. Never blocks: a full queue drops its oldest
    /// notification (counted by the channel's drop callback, which runs inside this write).
    /// Returns the running total of dropped notifications when this call made the queue drop one
    /// for backpressure, else <c>0</c>.
    /// </summary>
    /// <param name="first">The call's first event, or <c>null</c>.</param>
    /// <param name="second">The call's second event, or <c>null</c>.</param>
    private long Enqueue(Notification? first, Notification? second)
    {
        if (first is null && second is null)
            return 0;
        var before = Interlocked.Read(ref _dropped);
        if (!_notifications.Writer.TryWrite(new PendingNotification(first, second)))
        {
            // Disposed: there is no notifier to deliver it. Counted; not a backpressure warning.
            Interlocked.Increment(ref _dropped);
            return 0;
        }
        var after = Interlocked.Read(ref _dropped);
        return after != before ? after : 0;
    }

    /// <summary>
    /// Start the notifier with the ambient <see cref="ExecutionContext"/> suppressed: the loop lives
    /// as long as the registry, so it must not keep the ambient state (activity, logging scopes,
    /// <see cref="AsyncLocal{T}"/> values) of whichever caller happened to create the registry.
    /// </summary>
    private Task StartNotifier()
    {
        if (ExecutionContext.IsFlowSuppressed())
            return Task.Run(NotifyAsync);
        using (ExecutionContext.SuppressFlow())
            return Task.Run(NotifyAsync);
    }

    /// <summary>
    /// The one consumer: deliver each queued notification in order until the registry is disposed,
    /// then drop (and count) whatever is still queued, which releases its sessions.
    /// </summary>
    private async Task NotifyAsync()
    {
        var reader = _notifications.Reader;
        while (!_disposed && await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (!_disposed && reader.TryRead(out var item))
            {
                Volatile.Write(ref _delivering, 1);
                try
                {
                    Raise(item.First);
                    Raise(item.Second);
                }
                catch (Exception ex)
                {
                    // Defensive: every subscriber call has its own catch, so nothing here is
                    // expected to throw — but a fault must never end the registry's only notifier.
                    SafeLogWarning("A return-route session notification failed with {ExceptionType}; the notifier continues.", ex.GetType().Name);
                }
                finally
                {
                    Volatile.Write(ref _delivering, 0);
                }
            }
        }

        while (reader.TryRead(out _))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>Invoke each captured subscriber of <paramref name="notification"/>, each in its own <c>try</c>/<c>catch</c>; stop once the registry is disposed.</summary>
    /// <param name="notification">The event, or <c>null</c>.</param>
    private void Raise(Notification? notification)
    {
        if (notification is not { Handlers: var handlers, Args: var args })
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            if (_disposed)
                return;
            try
            {
                ((EventHandler<ReturnRouteSessionEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                SafeLogWarning("A return-route session event subscriber threw {ExceptionType}; isolated.", ex.GetType().Name);
            }
        }
    }

    /// <summary>Log a backpressure drop — the first and every 1000th, at Warning, with the running total. Outside the lock.</summary>
    /// <param name="dropped">The running total after this call's drop, or <c>0</c> when it dropped nothing.</param>
    private void LogDropped(long dropped)
    {
        if (dropped == 1 || (dropped > 0 && dropped % WarnEvery == 0))
            SafeLogWarning("Return-route session lifecycle notifications dropped: a subscriber is not keeping up; {Dropped} dropped in total.", dropped);
    }

    private void SafeLogInformation(string message, params object?[] args)
    {
        try
        {
            _logger?.LogInformation(message, args);
        }
        catch
        {
            // Logging providers are host code; they must not change registry behaviour.
        }
    }

    private void SafeLogWarning(string message, params object?[] args)
    {
        try
        {
            _logger?.LogWarning(message, args);
        }
        catch
        {
            // See SafeLogInformation.
        }
    }
}
