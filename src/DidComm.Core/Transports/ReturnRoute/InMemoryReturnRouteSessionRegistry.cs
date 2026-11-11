using DidComm.Consistency;
using Microsoft.Extensions.Logging;

namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The default <see cref="IReturnRouteSessionRegistry"/>: process-local, bounded by
/// <see cref="ReturnRouteSessionOptions"/>. Registered as a singleton by <c>AddDidComm</c>; a host
/// that registers its own <see cref="IReturnRouteSessionRegistry"/> first keeps it.
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
/// Notifications: after the state change and outside the lock, a <see cref="Register"/> or
/// <see cref="Unregister"/> that changed something queues one work item to the thread pool
/// (<see cref="ThreadPool.UnsafeQueueUserWorkItem{TState}(Action{TState}, TState, bool)"/>, so the
/// caller's <see cref="ExecutionContext"/> — a request's ambient state — does not flow into
/// subscriber code) and returns. The subscribers are captured when the work item is queued; it
/// raises that call's events in order — <see cref="SessionClosed"/>
/// (<see cref="SessionCloseReason.Evicted"/>) and then <see cref="SessionOpened"/> for a
/// registration — each subscriber in its own <c>try</c>/<c>catch</c> (the exception type name is
/// logged). A subscriber that blocks holds one pool thread and delays only the notifications of
/// that same call that come after it.
/// </para>
/// </remarks>
public sealed class InMemoryReturnRouteSessionRegistry : IReturnRouteSessionRegistry
{
    private readonly ReturnRouteSessionOptions _options;
    private readonly ILogger<InMemoryReturnRouteSessionRegistry>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<IReturnRouteSession>> _byPeer = new(StringComparer.Ordinal);
    private readonly HashSet<IReturnRouteSession> _all = new(ReferenceEqualityComparer.Instance);
    private int _count;

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
        }

        if (evicted is not null)
            SafeLogInformation("Return-route session evicted: the peer reached MaxSessionsPerPeer ({MaxSessionsPerPeer}); {Count} sessions registered.", _options.MaxSessionsPerPeer, count);
        QueueNotification(
            evicted is null ? null : new Notification(SessionClosed, new ReturnRouteSessionEventArgs(evicted, SessionCloseReason.Evicted)),
            new Notification(SessionOpened, new ReturnRouteSessionEventArgs(session, SessionCloseReason.None)));
        return new SessionRegistration(SessionRegistrationOutcome.Registered, evicted);

    Refused:
        SafeLogInformation("Return-route session refused: the registry is at MaxSessions ({MaxSessions}); {Count} sessions registered.", _options.MaxSessions, count);
        return new SessionRegistration(SessionRegistrationOutcome.RefusedTotalBound, null);
    }

    /// <inheritdoc />
    public bool Unregister(IReturnRouteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
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
        }
        QueueNotification(new Notification(SessionClosed, new ReturnRouteSessionEventArgs(session, SessionCloseReason.Unregistered)), null);
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

    /// <summary>One event of one call: the subscribers captured when the call queued it, and its arguments.</summary>
    private readonly record struct Notification(EventHandler<ReturnRouteSessionEventArgs>? Handlers, ReturnRouteSessionEventArgs Args);

    /// <summary>
    /// Raise one call's events, in order, on a thread-pool thread: never on the caller's thread, so
    /// no subscriber can delay the call that changed the state (or what its caller does next).
    /// </summary>
    /// <param name="first">The call's first event, or <c>null</c>.</param>
    /// <param name="second">The call's second event, or <c>null</c>.</param>
    private void QueueNotification(Notification? first, Notification? second)
    {
        if (first?.Handlers is null && second?.Handlers is null)
            return;
        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                state.Registry.Raise(state.First);
                state.Registry.Raise(state.Second);
            },
            (Registry: this, First: first, Second: second),
            preferLocal: false);
    }

    private void Raise(Notification? notification)
    {
        if (notification is not { Handlers: { } handlers, Args: var args })
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
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
