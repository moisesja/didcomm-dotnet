using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using DidComm.Protocols.OutOfBand;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// The mediator's start-up checks and its purge schedule (JC-9, FR-MED-07, FR-ROUTE-09,
/// FR-PICK-10), registered by <c>AddMediator()</c>. A lifecycle service, so its checks run in
/// <see cref="StartingAsync"/>, before any server starts listening; any failure fails the host's
/// start.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Start-up.</strong> The routing DIDs (at least one, each a bare DID); the forward sink in
/// effect is the mediator's; every protocol handler is built (so a pickup queue whose delivery
/// budget cannot carry any message fails now, and the admissible size is logged); the replay guard
/// and the session registry are built (so their settings are checked now); all settings are
/// checked together on the values in effect; the forward settings in effect are the mediator's (the
/// delay cap of <see cref="MediatorOptions.MaxForwardDelay"/> and <see cref="MediatorOptions.RewrapMode"/>
/// on the <c>ForwardProcessorOptions</c> handed out, no host routing-key provider with re-wrapping,
/// and a forward recipient directory that is not the routing mediator's empty default) whichever way
/// the container was composed; each routing DID passes FR-MED-07
/// (<see cref="RoutingDidValidator"/>); the out-of-band invitation is built from the first routing
/// DID; the observable instruments are registered; one Information line names counts and settings,
/// never a DID.
/// </para>
/// <para>
/// <strong>Running.</strong> Every <see cref="MediatorOptions.PurgeInterval"/> it removes expired
/// and aged-out messages from the pickup queue and reports, as Warnings with the remedy, how many
/// in-scope messages replay protection refused (budget full, clock behind) and how many session
/// notifications were dropped since the last report. A store fault is logged by type and the
/// schedule goes on. Nothing here runs outside a generic host.
/// </para>
/// <para>
/// Nothing is resolved in the constructor (L-017). Log lines carry counts, setting values and
/// exception type names; never a DID.
/// </para>
/// </remarks>
internal sealed class MediatorHostedService : IHostedLifecycleService, IAsyncDisposable, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IOptions<MediatorOptions> _options;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _validationTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _loop;
    private MessagePickupQueue? _queue;
    private MediatorMetrics? _metrics;
    private InMemoryReplayGuard? _replayGuard;
    private InMemoryReturnRouteSessionRegistry? _sessionRegistry;
    private bool _resolved;
    private long _lastSaturated;
    private long _lastClockBehind;
    private long _lastDroppedNotifications;
    private long _purgeFaultLines;

    /// <summary>Create the service; nothing is resolved until the host starts it.</summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The mediator settings, read when the host starts.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the purge schedule and the start-up bounds.</param>
    public MediatorHostedService(IServiceProvider services, IOptions<MediatorOptions> options, ILogger<MediatorHostedService>? logger, TimeProvider timeProvider)
        : this(services, options, logger, timeProvider, RoutingDidValidator.DefaultTimeout)
    {
    }

    /// <summary>Test seam: the service with an explicit bound on each routing DID's check.</summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The mediator settings.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the purge schedule and the start-up bounds.</param>
    /// <param name="validationTimeout">How long the check of one routing DID may take.</param>
    internal MediatorHostedService(IServiceProvider services, IOptions<MediatorOptions> options, ILogger<MediatorHostedService>? logger, TimeProvider timeProvider, TimeSpan validationTimeout)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _services = services;
        _options = options;
        _logger = logger;
        _time = timeProvider;
        _validationTimeout = validationTimeout;
    }

    /// <summary>The purge interval in effect, fixed when the host starts the service.</summary>
    internal TimeSpan PurgeInterval { get; private set; }

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        // (1) The routing DIDs, read once: at least one, each a bare DID. This is where a host's own
        //     IMediatorIdentity is held to the same rule as the configured list.
        var routingDids = ReadRoutingDids();

        // (2) The pipeline is the mediator's: a sink registered after AddMediator() would take every
        //     forward away from the queue without a word.
        if (_services.GetService<IForwardSink>() is not MediatorForwardSink)
        {
            throw new InvalidOperationException(
                "AddMediator(): the IForwardSink in effect is not the mediator's, so relayed messages would never reach the pickup queue. " +
                "An IForwardSink was registered after AddMediator(); remove it.");
        }

        // (3) Build every protocol handler now: the pickup queue checks its delivery budget against
        //     MaxReceiveBytes and logs the largest message it accepts; the forward processor checks
        //     its settings.
        _services.GetRequiredService<ProtocolDispatcher>();

        // (4) The replay guard and the session registry check their settings when built.
        var replayGuard = _services.GetRequiredService<IInboundReplayGuard>();
        var sessions = _services.GetRequiredService<IReturnRouteSessionRegistry>();

        // (5) Every setting, together, on the values in effect (the host's own Configure<T> calls
        //     included): the rules that span two roles cannot be checked anywhere else.
        var pipeline = _options.Value;
        var effective = MediatorOptions.Effective(
            pipeline,
            _services.GetRequiredService<IOptions<CoordinateMediationOptions>>().Value,
            _services.GetRequiredService<IOptions<MessagePickupOptions>>().Value,
            _services.GetRequiredService<IOptions<ReplayProtectionOptions>>().Value,
            _services.GetRequiredService<IOptions<ReturnRouteSessionOptions>>().Value);
        effective.Validate();
        PurgeInterval = effective.PurgeInterval;

        // (5b) The forward settings in effect: routes around AddMediator()'s own checks (a host
        //      Configure<MediatorOptions>, registrations made after the call) are caught here.
        CheckForwardSettings(effective);

        // (6) FR-MED-07: each routing DID is reachable the way senders reach it.
        var serviceResolver = _services.GetService<IServiceEndpointResolver>()
            ?? throw new InvalidOperationException(
                "AddMediator() needs DID resolution to check its routing DIDs (FR-MED-07): call UseNetDidResolver() on the DIDComm builder, or register an IServiceEndpointResolver.");
        var validator = new RoutingDidValidator(
            serviceResolver,
            _services.GetRequiredService<IDidKeyService>(),
            _services.GetRequiredService<ISecretsResolver>(),
            _time,
            _validationTimeout,
            _logger);
        await validator.ValidateAsync(routingDids, cancellationToken).ConfigureAwait(false);

        // (7) Direct push without a directory pushes nothing.
        if (effective.DirectPush && !IsRegistered(typeof(IRecipientEndpointDirectory)))
        {
            MediatorLog.Write(_logger, LogLevel.Warning,
                "Direct push is on but no IRecipientEndpointDirectory is registered: nothing will be pushed to an endpoint, and every message not pushed live waits for pickup. Register one before AddMediator().");
        }

        // (8) The out-of-band invitation, from the primary routing DID, now that it is known good.
        var client = _services.GetRequiredService<DidCommClient>();
        var invitation = OutOfBand.CreateInvitation(
            from: routingDids[0],
            goal: "RequestMediate",
            goalCode: "request-mediate",
            accept: new[] { "didcomm/v2" },
            id: Guid.NewGuid().ToString());
        _services.GetRequiredService<MediatorInvitationHolder>().PlaintextJson =
            await client.PackPlaintextAsync(invitation.Message, cancellationToken).ConfigureAwait(false);

        // (9) The observable instruments, for whichever of these is the library's in-memory type.
        Resolve();
        _metrics!.RegisterObservables(replayGuard, sessions, _services.GetRequiredService<IMessageStore>());

        // (10) One line, no DID.
        MediatorLog.Write(_logger, LogLevel.Information,
            "Mediator ready: {RoutingDidCount} routing DID(s) checked (FR-MED-07); forward delay honoured up to {MaxForwardDelay}; mailbox age limit {MaxMessageAge}; direct push {DirectPush}; purge every {PurgeInterval}.",
            routingDids.Count,
            effective.MaxForwardDelay,
            effective.Pickup.MaxMessageAge is { } age ? age.ToString() : "off",
            effective.DirectPush ? "on" : "off",
            effective.PurgeInterval);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_loop is not null || _stop.IsCancellationRequested)
                return Task.CompletedTask;
            var interval = PurgeInterval > TimeSpan.Zero ? PurgeInterval : _options.Value.PurgeInterval;
            var stop = _stop.Token;
            // The loop must not carry the context of the host's start-up call.
            var suppressed = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
            try
            {
                _loop = Task.Run(() => PurgeLoopAsync(interval, stop));
            }
            finally
            {
                suppressed?.Undo();
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        CancelQuietly(_stop);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancelQuietly(_stop);
        Task? loop;
        lock (_gate)
            loop = _loop;
        if (loop is null)
            return;
        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host gave up waiting; the loop ends on its own.
        }
    }

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task PurgeLoopAsync(TimeSpan interval, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(interval, _time);
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                await PurgeOnceAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            MediatorLog.Write(_logger, LogLevel.Error, "Mediator purge schedule stopped with {ExceptionType}.", ex.GetType().Name);
        }
    }

    /// <summary>
    /// One purge round (the test seam of the schedule): remove what has expired or aged out, count
    /// it, then report the replay and session counters that moved since the last round. A store
    /// fault is logged by type (rate-limited) and the round goes on. Only cancellation of
    /// <paramref name="ct"/> leaves.
    /// </summary>
    /// <param name="ct">The stop token.</param>
    internal async Task PurgeOnceAsync(CancellationToken ct)
    {
        Resolve();
        try
        {
            var removed = await _queue!.PurgeExpiredAsync(ct).ConfigureAwait(false);
            if (removed > 0)
            {
                _metrics!.RecordPurged(removed);
                MediatorLog.Write(_logger, LogLevel.Information, "Mediator purge: {Removed} expired or aged-out messages removed from the pickup queue.", removed);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var level = MediatorLog.RateLimited(ref _purgeFaultLines, out var n);
            MediatorLog.Write(_logger, level, "Mediator purge failed with {ExceptionType}; it runs again at the next interval. {Count} such failures.", ex.GetType().Name, n);
        }

        ReportCounters();
    }

    private void ReportCounters()
    {
        if (_replayGuard is { } guard)
        {
            Report(guard.SaturationCount, ref _lastSaturated,
                "Replay protection refused {Delta} in-scope messages because its memory was full ({Total} since start). Raise MediatorOptions.Replay.MaxEntries (about the accepted rate x (Window + ClockSkew)) or MaxEntriesPerPeer, and put rate limiting in front of the mediator's endpoint.");
            Report(guard.ClockBehindRefusalCount, ref _lastClockBehind,
                "Replay protection refused {Delta} in-scope messages because the host clock read behind its latest purge instant by more than Window + ClockSkew ({Total} since start): the clock stepped back. Messages are admitted again once the clock catches up or the process restarts.");
        }
        if (_sessionRegistry is { } registry)
        {
            Report(registry.DroppedNotifications, ref _lastDroppedNotifications,
                "The return-route session registry dropped {Delta} session notifications ({Total} since start): a subscriber is too slow. Make it faster, or raise MediatorOptions.Sessions.MaxPendingNotifications.");
        }
    }

    // Called from the purge round only, one at a time: no lock needed for the last-seen values.
    private void Report(long total, ref long lastSeen, string message)
    {
        var delta = total - lastSeen;
        if (delta <= 0)
            return;
        lastSeen = total;
        MediatorLog.Write(_logger, LogLevel.Warning, message, delta, total);
    }

    // What the purge round and the instruments need; resolved on first use, never in the constructor.
    private void Resolve()
    {
        lock (_gate)
        {
            if (_resolved)
                return;
            _queue = _services.GetRequiredService<MessagePickupQueue>();
            _metrics = _services.GetRequiredService<MediatorMetrics>();
            _replayGuard = _services.GetService<IInboundReplayGuard>() as InMemoryReplayGuard;
            _sessionRegistry = _services.GetService<IReturnRouteSessionRegistry>() as InMemoryReturnRouteSessionRegistry;
            _resolved = true;
        }
    }

    // The relay must run with the mediator's settings, whichever way the container was composed.
    // Every check reads what the container hands out, never a registration.
    private void CheckForwardSettings(MediatorOptions effective)
    {
        var forward = _services.GetRequiredService<ForwardProcessorOptions>();
        if (forward.MaxDelay is not { } cap || cap > effective.MaxForwardDelay)
        {
            throw new InvalidOperationException(
                "AddMediator(): the forward settings in effect (ForwardProcessorOptions) do not cap delay_milli at MediatorOptions.MaxForwardDelay, " +
                "so a sender could hold a recipient's quota with messages it cannot see for longer than the mediator allows (FR-PICK-10). " +
                "A ForwardProcessorOptions was registered after AddMediator(); remove it and set MediatorOptions.MaxForwardDelay instead.");
        }
        if (forward.Mode != effective.RewrapMode)
        {
            throw new InvalidOperationException(
                "AddMediator(): the forward settings in effect (ForwardProcessorOptions) re-wrap differently from MediatorOptions.RewrapMode. " +
                "A ForwardProcessorOptions was registered after AddMediator(); remove it and set MediatorOptions.RewrapMode instead.");
        }
        if (effective.RewrapMode == RewrapMode.ReanoncryptToNext && _services.GetService<IRecipientRoutingKeyProvider>() is not NoRecipientRoutingKeys)
        {
            throw new InvalidOperationException(
                "AddMediator(): MediatorOptions.RewrapMode is ReanoncryptToNext and an IRecipientRoutingKeyProvider is registered; re-wrapping never adds " +
                "the recipient's extra routing keys, so they would be ignored. Choose one: remove the provider, or keep RewrapMode.PassThrough.");
        }
        if (_services.GetService<IForwardRecipientRegistry>() is InMemoryForwardRecipientRegistry)
        {
            throw new InvalidOperationException(
                "AddMediator(): the forward recipient directory in effect is the routing mediator's empty default, so every forward would be dropped " +
                "as unknown. Keep CoordinateMediationOptions.UseAsForwardRecipientRegistry = true (the default), or register a directory of your " +
                "own, consulting IMediationStore.FindGrantByRecipientDidAsync, before AddMediator().");
        }
    }

    private IReadOnlyList<string> ReadRoutingDids()
    {
        string[] dids;
        try
        {
            dids = _services.GetRequiredService<IMediatorIdentity>().RoutingDids?.ToArray() ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"AddMediator(): the mediator's routing DIDs could not be read ({ex.GetType().Name}). A mediator needs at least one routing DID: add one to " +
                "MediatorOptions.CoordinateMediation.RoutingDids, or register an IMediatorIdentity that supplies one.");
        }

        if (dids.Length == 0)
        {
            throw new InvalidOperationException(
                "AddMediator(): the mediator has no routing DID (FR-MED-07). Add one to MediatorOptions.CoordinateMediation.RoutingDids, or register an " +
                "IMediatorIdentity that supplies one.");
        }
        for (var i = 0; i < dids.Length; i++)
        {
            if (!BareDid.IsBareDid(dids[i]))
            {
                throw new InvalidOperationException(
                    $"AddMediator(): routing DID at index {i} is not a bare DID (no path, query or fragment) of at most 2048 characters (FR-MED-07).");
            }
        }
        return dids;
    }

    private bool IsRegistered(Type service)
        => _services.GetService<IServiceProviderIsService>() is { } check
            ? check.IsService(service)
            : _services.GetService(service) is not null;

    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch
        {
            // A callback registered on the token threw, or the source is disposed: never escape.
        }
    }

    /// <summary>Stop the purge schedule; does not wait.</summary>
    public void Dispose() => CancelQuietly(_stop);

    /// <summary>Stop the purge schedule and wait briefly for it.</summary>
    public async ValueTask DisposeAsync()
    {
        CancelQuietly(_stop);
        Task? loop;
        lock (_gate)
            loop = _loop;
        if (loop is null)
            return;
        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // The loop ends on its own; nothing to add.
        }
    }
}
