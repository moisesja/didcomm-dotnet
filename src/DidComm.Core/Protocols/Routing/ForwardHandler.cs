using System.Threading.Channels;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Messages;
using Microsoft.Extensions.Logging;

namespace DidComm.Protocols.Routing;

/// <summary>
/// The mediator-role <see cref="IProtocolHandler"/> for Routing Protocol 2.0 <c>forward</c>
/// (FR-ROUTE-09/10/11). Registered by <c>AddRoutingMediator()</c>, it is dispatched through the
/// protocol handler registry like any other PIURI and discloses <c>routing/2.0</c> with
/// <c>roles: ["mediator"]</c> through <see cref="IProtocolHandlerRoles"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Inline, on the request.</strong> <see cref="HandleAsync"/> does only cheap checks:
/// parse the forward, require the FR-ROUTE-11 posture (the envelope was encrypted and decrypted
/// with a held key — <see cref="Facade.UnpackResult.Encrypted"/> and a non-empty
/// <see cref="Facade.UnpackResult.RecipientKid"/>), refuse a forward whose own
/// <c>expires_time</c> has passed (FR-ROUTE-09, judged on <c>DidCommOptions.Clock</c> with the
/// FR-API-05 skew), reduce <c>body.next</c> to its DID subject and ask the
/// <see cref="IForwardRecipientRegistry"/>. Any failure drops the forward with a Warning that
/// carries the message id only — no DID, no kid, no payload — and the handler returns
/// <c>null</c>: a mediator sends nothing back toward the sender, no problem report, and
/// <c>please_ack</c> is never read (FR-ROUTE-07). The method never throws, except
/// <see cref="OperationCanceledException"/> when the caller's token is cancelled.
/// </para>
/// <para>
/// <strong>Detached, after the 202.</strong> An admitted forward is queued on a bounded channel and
/// the transport acknowledges it (202) before the <see cref="ForwardProcessor"/> and the
/// <see cref="IForwardSink"/> run — so response timing does not depend on whether <c>next</c> is
/// registered, and a sink that pushes to a live session, queues, or re-encrypts (rewrap mode)
/// never holds a request. Detaching once here means every sink runs after the 202 on every
/// transport instead of each sink detaching itself. The queued work runs on this handler's own
/// shutdown token, never the request token: a client that aborts after the 202 must not cancel
/// the relay. A saturated queue (<see cref="DefaultCapacity"/> outstanding) drops the newest
/// forward with a rate-limited Warning — fail closed, never block the request.
/// </para>
/// <para>
/// <strong>Expiry twice, on purpose.</strong> Inline, so an expired forward never costs a queue slot
/// or a sink call; and again on the pump, for the time spent queued. Both use
/// <see cref="ForwardProcessingResult.IsExpiredAt"/>. Only the forward's own header is
/// enforceable — the inner payload's <c>expires_time</c> is encrypted and invisible to the mediator.
/// </para>
/// <para>
/// <strong>Lifecycle.</strong> A singleton; thread-safe. <see cref="Dispose"/> stops admission,
/// cancels the pump and discards queued forwards (fail closed, one Information line with the
/// count); <see cref="DisposeAsync"/> additionally waits for the pump up to a grace period.
/// <see cref="FlushAsync"/> is a test seam that completes once every forward admitted before the
/// call has reached the sink or been dropped.
/// </para>
/// </remarks>
public sealed class ForwardHandler : IProtocolHandler, IProtocolHandlerRoles, IDisposable, IAsyncDisposable
{
    /// <summary>Default bound on forwards queued or in flight behind the 202.</summary>
    internal const int DefaultCapacity = 1024;

    private static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromSeconds(5);
    private const long DropLogEvery = 1000;

    private readonly ForwardProcessor _processor;
    private readonly IForwardSink _sink;
    private readonly IForwardRecipientRegistry _registry;
    private readonly ILogger<ForwardHandler>? _logger;
    private readonly int _capacity;
    private readonly TimeSpan _shutdownGrace;
    private readonly Channel<WorkItem> _channel;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly TaskCompletionSource _shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;
    private int _outstandingItems;
    private long _dropped;
    private int _shutdownStarted;

    // The dispatcher-built ProtocolContext (an immutable record) is everything the detached work
    // needs: the UnpackResult, the options/clock and the facade. The request token is NOT stored.
    private sealed record WorkItem(ProtocolContext? Context, TaskCompletionSource? Barrier);

    /// <summary>Create the handler with the default queue bound and shutdown grace.</summary>
    /// <param name="processor">The mediator's forward processor (pass-through / extra wraps / rewrap).</param>
    /// <param name="sink">Where processed forwards go; <see cref="NullForwardSink"/> drops them.</param>
    /// <param name="registry">The recipients this mediator relays for, keyed by DID subject.</param>
    /// <param name="logger">Optional logger; every line carries the message id and never a DID.</param>
    public ForwardHandler(
        ForwardProcessor processor,
        IForwardSink sink,
        IForwardRecipientRegistry registry,
        ILogger<ForwardHandler>? logger = null)
        : this(processor, sink, registry, logger, DefaultCapacity, shutdownGrace: null) { }

    /// <summary>Test seam: a handler with an explicit queue bound and shutdown grace.</summary>
    /// <param name="processor">The mediator's forward processor.</param>
    /// <param name="sink">Where processed forwards go.</param>
    /// <param name="registry">The recipients this mediator relays for.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="capacity">Maximum forwards queued or in flight; the newest is dropped beyond it.</param>
    /// <param name="shutdownGrace">How long <see cref="DisposeAsync"/> waits for the pump; <c>null</c> for 5 seconds.</param>
    internal ForwardHandler(
        ForwardProcessor processor,
        IForwardSink sink,
        IForwardRecipientRegistry registry,
        ILogger<ForwardHandler>? logger,
        int capacity,
        TimeSpan? shutdownGrace)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(registry);
        if (capacity < 1 || capacity == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _shutdownGrace = shutdownGrace ?? DefaultShutdownGrace;
        if (_shutdownGrace <= TimeSpan.Zero && _shutdownGrace != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(shutdownGrace));

        _processor = processor;
        _sink = sink;
        _registry = registry;
        _logger = logger;
        _capacity = capacity;
        _shutdownToken = _shutdown.Token; // capture before the pump can start
        // One extra physical slot is reserved for the serialized Flush barrier. Forward admission is
        // independently capped at `capacity`, including the in-flight item.
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity + 1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false, // the shutdown path may drain while the pump is in the sink
            SingleWriter = false,
        });
        _pump = Task.Run(() => PumpAsync(_shutdownToken));
    }

    /// <inheritdoc />
    public string ProtocolUri => ForwardConstants.ProtocolIdentifier;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles { get; } = new[] { "mediator" };

    /// <summary>
    /// Screen the forward inline and, when it may be relayed, queue it for the detached processor
    /// and sink. Always returns <c>null</c>: a mediator sends only destward, never back toward the
    /// sender (FR-ROUTE-07/11). Never throws, except <see cref="OperationCanceledException"/> on
    /// <paramref name="ct"/> (and <see cref="ArgumentNullException"/> for a null argument, a caller bug).
    /// </summary>
    /// <param name="message">The unpacked inbound message.</param>
    /// <param name="context">The dispatcher context — carries the unpack metadata the posture check needs.</param>
    /// <param name="ct">The request's cancellation token; checked inline only, never handed to the queued work.</param>
    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        try
        {
            // (a) A forward at all, and well formed. Malformed (missing next/attachments, next over
            //     the FR-API-09 cap) is a drop, not a throw: nothing goes back to the sender.
            string next;
            try
            {
                if (!ForwardMessage.TryParse(message, out next, out _))
                {
                    SafeLogWarning(null, "Forward {MessageId} dropped: not a forward message (FR-ROUTE-11).", message.Id);
                    return null;
                }
            }
            catch (MalformedMessageException ex)
            {
                SafeLogWarning(ex, "Forward {MessageId} dropped: malformed forward (FR-ROUTE-01/FR-API-09).", message.Id);
                return null;
            }

            // (b) FR-ROUTE-11 posture: encrypted and decrypted with a held key.
            if (!context.Received.Encrypted || string.IsNullOrEmpty(context.Received.RecipientKid))
            {
                SafeLogWarning(null, "Forward {MessageId} dropped: plaintext, signed-only or not decrypted with a held key (FR-ROUTE-11).", message.Id);
                return null;
            }

            // (c) FR-ROUTE-09 relay-time expiry, regardless of any FR-API-05 setting: the unpack
            //     path may have rejected already; this is the handler's own check.
            if (ForwardProcessingResult.IsExpiredAt(message.ExpiresTime, context.Options.Now(), context.Options.ExpiresClockSkew))
            {
                SafeLogWarning(null, "Forward {MessageId} dropped: expired at relay time (FR-ROUTE-09).", message.Id);
                return null;
            }

            // (d) Registered recipient, by DID subject. Unknown → drop, no problem report (D4).
            var subject = DidSubject.DidSubjectOf(next);
            if (subject is null)
            {
                SafeLogWarning(null, "Forward {MessageId} dropped: next is not a DID or DID URL (FR-ROUTE-11).", message.Id);
                return null;
            }

            if (!await _registry.IsRegisteredAsync(subject, ct).ConfigureAwait(false))
            {
                SafeLogWarning(null, "Forward {MessageId} dropped: next is not a registered recipient (FR-ROUTE-11).", message.Id);
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A registry (or logger) fault must not become a 400 toward the sender.
            SafeLogWarning(ex, "Forward {MessageId} dropped: inline screening failed with {ExceptionType}.", message.Id, ex.GetType().Name);
            return null;
        }

        // Admit: the 202 is written by the transport as soon as we return; everything that depends
        // on the registration (processing, the sink) happens on the pump.
        var accepted = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _shutdownStarted) == 0 && _outstandingItems < _capacity)
            {
                accepted = _channel.Writer.TryWrite(new WorkItem(context, Barrier: null));
                if (accepted)
                    _outstandingItems++;
            }
        }

        if (!accepted)
            Drop(message.Id);
        return null;
    }

    private async Task PumpAsync(CancellationToken shutdown)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(shutdown).ConfigureAwait(false))
            {
                if (item.Barrier is not null)
                {
                    item.Barrier.TrySetResult();
                    continue;
                }

                try
                {
                    await ProcessOneAsync(item.Context!, shutdown).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseSlot();
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Normal pump shutdown.
        }
        catch (Exception ex)
        {
            SafeLogWarning(ex, "Forward handler pump faulted; the fault was observed and isolated.");
        }
        finally
        {
            DrainQueued();
        }
    }

    private async Task ProcessOneAsync(ProtocolContext context, CancellationToken shutdown)
    {
        var id = context.Received.Message.Id;
        try
        {
            var result = await _processor.ProcessAsync(context.Received, shutdown).ConfigureAwait(false);

            // FR-ROUTE-09 again: the time spent queued may have crossed the forward's expiry.
            if (result.IsExpired(context.Options.Now(), context.Options.ExpiresClockSkew))
            {
                SafeLogInformation("Forward {MessageId} discarded: expired while queued (FR-ROUTE-09).", id);
                return;
            }

            await _sink.AcceptAsync(result, context, shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Normal cooperative shutdown; the forward is discarded (fail closed).
        }
        catch (Exception ex)
        {
            // ConsistencyException, MalformedMessageException, DidResolutionException,
            // CryptoException, sink faults. The exception's own text travels in the exception slot
            // only; processor messages contain no wire values. Never the `next` value.
            SafeLogWarning(ex, "Forward {MessageId} not relayed: {ExceptionType}.", id, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Complete when every forward admitted before this call has been handed to the sink or
    /// dropped. The timeout covers flush serialization, barrier insertion and sink completion.
    /// </summary>
    /// <param name="timeout">How long to wait for the barrier to drain.</param>
    /// <exception cref="ObjectDisposedException">After <see cref="Dispose"/> / <see cref="DisposeAsync"/>.</exception>
    internal async Task FlushAsync(TimeSpan timeout)
    {
        ThrowIfShutdown();
        using var cts = new CancellationTokenSource(timeout);
        await _flushGate.WaitAsync(cts.Token).ConfigureAwait(false);
        try
        {
            ThrowIfShutdown();
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                ThrowIfShutdown();
                // Admission is capped at _capacity while the channel has _capacity + 1 physical
                // slots, and Flush calls are serialized. The barrier therefore fits.
                if (!_channel.Writer.TryWrite(new WorkItem(Context: null, Barrier: barrier)))
                    throw new InvalidOperationException("Forward flush barrier could not be admitted despite its reserved channel slot.");
            }

            await barrier.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>Test/diagnostic seam: forwards queued or in flight right now.</summary>
    internal int Outstanding
    {
        get
        {
            lock (_gate)
                return _outstandingItems;
        }
    }

    /// <summary>Test/diagnostic seam for proving shutdown completed.</summary>
    internal Task ShutdownCompletion => _shutdownCompletion.Task;

    /// <summary>Stop admission, cancel the pump and discard queued forwards, without waiting for the sink.</summary>
    public void Dispose() => BeginShutdown();

    /// <summary>
    /// Stop admission, cancel the pump, discard queued forwards and wait for the pump to finish up
    /// to the shutdown grace. A sink call that ignores cancellation may outlive this method; its
    /// eventual completion is still observed and owns the cancellation source until it exits.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var completion = BeginShutdown();
        try
        {
            await completion.WaitAsync(_shutdownGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The in-flight sink call may ignore cancellation. Queued forwards were already drained;
            // ObservePumpAndDisposeAsync observes the pump and disposes the CTS when it returns.
        }
    }

    private Task BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            _channel.Writer.TryComplete();

            try
            {
                _shutdown.Cancel(throwOnFirstException: false);
            }
            catch (Exception ex)
            {
                SafeLogWarning(ex, "A forward cancellation callback threw during shutdown; continuing to drain the forward queue.");
            }

            DrainQueued();
            _ = ObservePumpAndDisposeAsync();
        }

        return _shutdownCompletion.Task;
    }

    private async Task ObservePumpAndDisposeAsync()
    {
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The pump guards its own work; observe any unexpected fault here as a final boundary.
            SafeLogWarning(ex, "The forward handler pump faulted during shutdown; the fault was observed.");
        }
        finally
        {
            _shutdown.Dispose();
            _shutdownCompletion.TrySetResult();
        }
    }

    private void DrainQueued()
    {
        var discarded = 0;
        while (_channel.Reader.TryRead(out var item))
        {
            if (item.Barrier is not null)
            {
                item.Barrier.TrySetException(new ObjectDisposedException(nameof(ForwardHandler)));
                continue;
            }

            discarded++;
            ReleaseSlot();
        }

        if (discarded > 0)
            SafeLogInformation("Forward handler shutting down: {Count} queued forward(s) discarded (fail closed).", discarded);
    }

    private void ReleaseSlot()
    {
        lock (_gate)
            _outstandingItems--;
    }

    private void Drop(string messageId)
    {
        var dropped = Interlocked.Increment(ref _dropped);
        if (dropped != 1 && dropped % DropLogEvery != 0)
            return;

        SafeLogWarning(
            null,
            "Forward {MessageId} dropped: handler queue saturated (capacity {Capacity}); fail closed. Total dropped: {Dropped}.",
            messageId,
            _capacity,
            dropped);
    }

    private void SafeLogWarning(Exception? exception, string message, params object?[] args)
    {
        try
        {
            if (exception is null)
                _logger?.LogWarning(message, args);
            else
                _logger?.LogWarning(exception, message, args);
        }
        catch
        {
            // Logging providers are host code; they must not change dispatch or shutdown behaviour.
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
            // See SafeLogWarning.
        }
    }

    private void ThrowIfShutdown()
    {
        if (Volatile.Read(ref _shutdownStarted) != 0)
            throw new ObjectDisposedException(nameof(ForwardHandler));
    }
}
