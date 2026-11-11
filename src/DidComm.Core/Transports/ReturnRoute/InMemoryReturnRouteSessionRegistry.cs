using DidComm.Consistency;
using Microsoft.Extensions.Logging;

namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The default <see cref="IReturnRouteSessionRegistry"/>: process-local, bounded by
/// <see cref="ReturnRouteSessionOptions"/>. Registered as a singleton by <c>AddDidComm</c>; a host
/// that registers its own <see cref="IReturnRouteSessionRegistry"/> first keeps it.
/// </summary>
/// <remarks>
/// All state lives under one lock and every decision — already registered, total bound, per-peer
/// eviction, insert — is made inside it, so two concurrent registrations for one peer at the bound
/// evict exactly one session each. Eviction is O(1) per registration (the peer's list is in
/// registration order, so its oldest is the first element); there is no amortized sweep. Events
/// are raised after the lock is released. Log lines carry counts only, never a DID.
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
            if (_count >= _options.MaxSessions)
            {
                count = _count;
                goto Refused;
            }

            if (!_byPeer.TryGetValue(peer, out var list))
            {
                list = new List<IReturnRouteSession>();
                _byPeer[peer] = list;
            }
            else if (list.Count >= _options.MaxSessionsPerPeer)
            {
                evicted = list[0];
                list.RemoveAt(0);
                _all.Remove(evicted);
                _count--;
            }

            list.Add(session);
            _all.Add(session);
            _count++;
            count = _count;
        }

        if (evicted is not null)
        {
            SafeLogInformation("Return-route session evicted: the peer reached MaxSessionsPerPeer ({MaxSessionsPerPeer}); {Count} sessions registered.", _options.MaxSessionsPerPeer, count);
            Raise(SessionClosed, evicted, SessionCloseReason.Evicted);
        }
        Raise(SessionOpened, session, SessionCloseReason.None);
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
        Raise(SessionClosed, session, SessionCloseReason.Unregistered);
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

    private void Raise(EventHandler<ReturnRouteSessionEventArgs>? handlers, IReturnRouteSession session, SessionCloseReason reason)
    {
        if (handlers is null)
            return;
        var args = new ReturnRouteSessionEventArgs(session, reason);
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
