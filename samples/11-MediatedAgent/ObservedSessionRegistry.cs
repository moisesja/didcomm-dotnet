using DidComm.Transports.ReturnRoute;

namespace DidComm.Samples.MediatedAgent;

/// <summary>
/// The mediator's registry of live connections (return-route sessions), wrapped so the operator can
/// count what happens to them. It forwards every call to the library's in-memory registry.
/// </summary>
/// <remarks>
/// <para>
/// Registered before <c>AddDidComm</c>, it replaces the registry the library would register.
/// Like any registry it must not take the dispatcher, a protocol handler or <c>DidCommClient</c>
/// in its constructor; this one takes only the registry it wraps.
/// </para>
/// <para>
/// The mediator reports the in-memory registry's dropped notifications on its own only when it can
/// see that type; behind a wrapper the operator reads <see cref="DroppedNotifications"/> itself.
/// </para>
/// </remarks>
internal sealed class ObservedSessionRegistry : IReturnRouteSessionRegistry, IDisposable
{
    private readonly InMemoryReturnRouteSessionRegistry _inner;
    private long _registered;
    private long _evicted;
    private long _refused;
    private long _unregistered;

    /// <summary>Wrap <paramref name="inner"/>; this object owns it and disposes it.</summary>
    /// <param name="inner">The registry to forward to.</param>
    public ObservedSessionRegistry(InMemoryReturnRouteSessionRegistry inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>Connections that became sessions.</summary>
    public long Registered => Interlocked.Read(ref _registered);

    /// <summary>Sessions pushed out because their peer opened one too many.</summary>
    public long Evicted => Interlocked.Read(ref _evicted);

    /// <summary>Sessions refused because the registry was full.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>Sessions that ended.</summary>
    public long Unregistered => Interlocked.Read(ref _unregistered);

    /// <summary>Lifecycle notifications dropped because a subscriber lagged too far behind.</summary>
    public long DroppedNotifications => _inner.DroppedNotifications;

    /// <inheritdoc />
    public int Count => _inner.Count;

    /// <inheritdoc />
    public event EventHandler<ReturnRouteSessionEventArgs>? SessionOpened
    {
        add => _inner.SessionOpened += value;
        remove => _inner.SessionOpened -= value;
    }

    /// <inheritdoc />
    public event EventHandler<ReturnRouteSessionEventArgs>? SessionClosed
    {
        add => _inner.SessionClosed += value;
        remove => _inner.SessionClosed -= value;
    }

    /// <inheritdoc />
    public SessionRegistration Register(IReturnRouteSession session)
    {
        var registration = _inner.Register(session);
        if (registration.Registered)
            Interlocked.Increment(ref _registered);
        if (registration.Evicted is not null)
            Interlocked.Increment(ref _evicted); // closing the evicted session's connection is the caller's job
        if (registration.Outcome == SessionRegistrationOutcome.RefusedTotalBound)
            Interlocked.Increment(ref _refused);
        return registration;
    }

    /// <inheritdoc />
    public bool Unregister(IReturnRouteSession session)
    {
        var removed = _inner.Unregister(session);
        if (removed)
            Interlocked.Increment(ref _unregistered);
        return removed;
    }

    /// <inheritdoc />
    public IReadOnlyList<IReturnRouteSession> Find(string did) => _inner.Find(did);

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}
