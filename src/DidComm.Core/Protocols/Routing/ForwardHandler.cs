using System.Collections.Concurrent;
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
/// <strong>Detached, after the 202.</strong> An admitted forward reserves its place on a bounded
/// queue inline (so saturation is decided on the request, like every other drop), but it is
/// published to the pump only once <see cref="ProtocolContext.Acknowledged"/> has completed — the
/// transport's signal that the inbound was acknowledged on the wire. The registry-aware HTTP
/// endpoint completes it from <c>Response.OnCompleted</c>, i.e. when the 202 has been committed,
/// so neither the <see cref="ForwardProcessor"/> nor the <see cref="IForwardSink"/> runs before
/// the sender holds the 202: response timing cannot depend on whether <c>next</c> is registered,
/// and a sink that pushes to a live session, queues, or re-encrypts (rewrap mode) never holds a
/// request. The WebSocket endpoints and a direct <c>ProtocolDispatcher.DispatchAsync</c> caller
/// provide no wire acknowledgement (<see cref="Task.CompletedTask"/>) and publish immediately.
/// An acknowledgement that is cancelled or faulted (client abort before the response was
/// committed; a rejection after the handler ran) releases the reservation and drops the forward
/// with a Warning — fail closed. The queued work runs on this handler's own shutdown token, never
/// the request token: a client that aborts after the 202 must not cancel the relay. The queue is
/// bounded twice — <see cref="DefaultCapacity"/> forwards reserved, queued or in flight, and
/// <see cref="DefaultByteBudget"/> of their plaintext UTF-8 bytes, because each queued item pins
/// the decoded message graph (attachment included) and the item cap alone would let a sender park
/// 1024 × <c>MaxReceiveBytes</c> behind a slow sink. A forward that would exceed either bound is
/// dropped with a rate-limited Warning — fail closed, never block the request.
/// </para>
/// <para>
/// <strong>Schedule fixed at receipt.</strong> The clock is read once per forward, inline: the
/// same instant judges the relay-time expiry and, with <c>delay_milli</c> resolved right there under
/// the processor's <see cref="ForwardProcessorOptions.MaxDelay"/>, becomes
/// <see cref="ForwardProcessingResult.NotBefore"/> (FR-ROUTE-10). Time spent waiting for the
/// acknowledgement or queued behind a slow sink never moves it.
/// </para>
/// <para>
/// <strong>Logging contract.</strong> Every line carries the message id and, on a failure, the
/// exception type name(s) only. No exception object — and so no exception message — is ever
/// handed to the logger: a resolution or sink fault can carry the next-hop DID in its text, and a
/// console provider would print it.
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
/// call has reached the sink or been dropped — including forwards still waiting for their
/// acknowledgement, which it waits for within its timeout.
/// </para>
/// </remarks>
public sealed class ForwardHandler : IProtocolHandler, IProtocolHandlerRoles, IDisposable, IAsyncDisposable
{
    /// <summary>Default bound on forwards queued or in flight behind the 202.</summary>
    internal const int DefaultCapacity = 1024;

    /// <summary>
    /// Default bound on the plaintext UTF-8 bytes of the forwards queued or in flight (64 MiB): the
    /// same exact-plaintext accounting <c>ObserverDelivery</c> uses, so the queue honours
    /// <c>MaxReceiveBytes</c> in aggregate and not only per message (epic #88, D7).
    /// </summary>
    internal const long DefaultByteBudget = 64L * 1024 * 1024;

    private static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromSeconds(5);
    private const long DropLogEvery = 1000;

    private readonly ForwardProcessor _processor;
    private readonly IForwardSink _sink;
    private readonly IForwardRecipientRegistry _registry;
    private readonly ILogger<ForwardHandler>? _logger;
    private readonly int _capacity;
    private readonly long _byteBudget;
    private readonly TimeSpan _shutdownGrace;
    private readonly Channel<WorkItem> _channel;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly TaskCompletionSource _shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;
    // Forwards admitted but not yet published: they hold their reservation while they wait for the
    // transport's acknowledgement. FlushAsync waits for them; shutdown unblocks them (discard).
    private readonly ConcurrentDictionary<Task, byte> _pendingAcknowledgements = new();
    private int _outstandingItems;
    private long _outstandingBytes;
    private long _dropped;
    private int _shutdownStarted;
    private int _discardedAtShutdown;

    // The dispatcher-built ProtocolContext (an immutable record) is everything the detached work
    // needs: the UnpackResult, the options/clock and the facade. The request token is NOT stored.
    // Bytes is the plaintext UTF-8 charge the item holds against the byte budget until released.
    // Delay/NotBefore are the delay_milli schedule fixed at admission (FR-ROUTE-10).
    private sealed record WorkItem(
        ProtocolContext? Context,
        int Bytes,
        TimeSpan? Delay,
        DateTimeOffset? NotBefore,
        TaskCompletionSource? Barrier);

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

    /// <summary>Test seam: a handler with explicit queue bounds and shutdown grace.</summary>
    /// <param name="processor">The mediator's forward processor.</param>
    /// <param name="sink">Where processed forwards go.</param>
    /// <param name="registry">The recipients this mediator relays for.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="capacity">Maximum forwards queued or in flight; the newest is dropped beyond it.</param>
    /// <param name="shutdownGrace">How long <see cref="DisposeAsync"/> waits for the pump; <c>null</c> for 5 seconds.</param>
    /// <param name="byteBudget">Maximum plaintext UTF-8 bytes queued or in flight; a forward that would exceed it is dropped.</param>
    internal ForwardHandler(
        ForwardProcessor processor,
        IForwardSink sink,
        IForwardRecipientRegistry registry,
        ILogger<ForwardHandler>? logger,
        int capacity,
        TimeSpan? shutdownGrace,
        long byteBudget = DefaultByteBudget)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(registry);
        if (capacity < 1 || capacity == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (byteBudget < 1)
            throw new ArgumentOutOfRangeException(nameof(byteBudget));

        _shutdownGrace = shutdownGrace ?? DefaultShutdownGrace;
        if (_shutdownGrace <= TimeSpan.Zero && _shutdownGrace != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(shutdownGrace));

        _processor = processor;
        _sink = sink;
        _registry = registry;
        _logger = logger;
        _capacity = capacity;
        _byteBudget = byteBudget;
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
    /// Screen the forward inline and, when it may be relayed, reserve its place on the detached
    /// queue; it is published to the processor and sink once
    /// <see cref="ProtocolContext.Acknowledged"/> completes (immediately when the transport offers
    /// no acknowledgement). Always returns <c>null</c>: a mediator sends only destward, never back
    /// toward the sender (FR-ROUTE-07/11). Never throws, except
    /// <see cref="OperationCanceledException"/> on <paramref name="ct"/> (and
    /// <see cref="ArgumentNullException"/> for a null argument, a caller bug).
    /// </summary>
    /// <param name="message">The unpacked inbound message.</param>
    /// <param name="context">The dispatcher context — carries the unpack metadata the posture check needs.</param>
    /// <param name="ct">The request's cancellation token; checked inline only, never handed to the queued work.</param>
    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        int bytes;
        TimeSpan? delay;
        DateTimeOffset? notBefore;
        try
        {
            // (a) A forward at all, and well formed. Malformed (missing next/attachments, next over
            //     the FR-API-09 cap) is a drop, not a throw: nothing goes back to the sender.
            string next;
            try
            {
                if (!ForwardMessage.TryParse(message, out next, out _))
                {
                    SafeLogWarning("Forward {MessageId} dropped: not a forward message (FR-ROUTE-11).", message.Id);
                    return null;
                }
            }
            catch (MalformedMessageException ex)
            {
                SafeLogWarning("Forward {MessageId} dropped: malformed forward (FR-ROUTE-01/FR-API-09): {ExceptionType}.", message.Id, ex.GetType().Name);
                return null;
            }

            // (b) FR-ROUTE-11 posture: encrypted and decrypted with a held key.
            if (!context.Received.Encrypted || string.IsNullOrEmpty(context.Received.RecipientKid))
            {
                SafeLogWarning("Forward {MessageId} dropped: plaintext, signed-only or not decrypted with a held key (FR-ROUTE-11).", message.Id);
                return null;
            }

            // (c) FR-ROUTE-09 relay-time expiry, regardless of any FR-API-05 setting: the unpack
            //     path may have rejected already; this is the handler's own check. One clock read
            //     per forward: the same instant is the receipt time the delay_milli schedule is
            //     fixed against (FR-ROUTE-10), so waiting for the acknowledgement or queueing
            //     behind a slow sink never moves NotBefore.
            var receivedAt = context.Options.Now();
            if (ForwardProcessingResult.IsExpiredAt(message.ExpiresTime, receivedAt, context.Options.ExpiresClockSkew))
            {
                SafeLogWarning("Forward {MessageId} dropped: expired at relay time (FR-ROUTE-09).", message.Id);
                return null;
            }

            // The processor's own resolution, so its MaxDelay cap applies before the one draw.
            delay = _processor.ResolveDelay(message);
            notBefore = delay is TimeSpan d ? receivedAt + d : null;

            // (d) Registered recipient, by DID subject. Unknown → drop, no problem report (D4).
            var subject = DidSubject.DidSubjectOf(next);
            if (subject is null)
            {
                SafeLogWarning("Forward {MessageId} dropped: next is not a DID or DID URL (FR-ROUTE-11).", message.Id);
                return null;
            }

            // (e) Size the forward for the byte budget before the registration lookup, so the
            //     work done inline is the same whether or not `next` is registered: the exact
            //     plaintext length the unpack path preserved (no re-serialization); a host-built
            //     synthetic result is serialized once. A sizing failure is a drop, never a throw.
            bytes = InboundMessageSnapshot.TryGetFor(message, out var snapshot)
                ? snapshot.Utf8ByteCount
                : InboundMessageSnapshot.CreateFallback(context.Received).Utf8ByteCount;

            if (!await _registry.IsRegisteredAsync(subject, ct).ConfigureAwait(false))
            {
                SafeLogWarning("Forward {MessageId} dropped: next is not a registered recipient (FR-ROUTE-11).", message.Id);
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A registry (or logger / clock) fault must not become a 400 toward the sender.
            SafeLogWarning("Forward {MessageId} dropped: inline screening failed with {ExceptionType}.", message.Id, ex.GetType().Name);
            return null;
        }

        // Admit: reserve the slot and the bytes inline, so saturation is decided on the request
        // like every other drop. Everything that depends on the registration (processing, the
        // sink) happens on the pump, and only after the transport has acknowledged the inbound.
        var item = new WorkItem(context, bytes, delay, notBefore, Barrier: null);
        var reserved = false;
        var publishNow = false;
        var published = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _shutdownStarted) == 0
                && _outstandingItems < _capacity
                && bytes <= _byteBudget - _outstandingBytes)
            {
                reserved = true;
                _outstandingItems++;
                _outstandingBytes += bytes;
                // No wire acknowledgement to wait for (dispatcher-only use, WebSocket): publish now.
                // Decided once, here — an acknowledgement that completes after this point is
                // observed by the continuation, never by a second read.
                publishNow = context.Acknowledged.IsCompletedSuccessfully;
                if (publishNow)
                    published = TryPublish(item);
            }
        }

        if (!reserved)
        {
            Drop(message.Id);
            return null;
        }

        if (!publishNow)
        {
            TrackPendingAcknowledgement(PublishWhenAcknowledgedAsync(item));
        }
        else if (!published)
        {
            // TryWrite refused: the writer was completed by a concurrent shutdown. Fail closed.
            ReleaseSlot(bytes);
            Interlocked.Increment(ref _discardedAtShutdown);
        }

        return null;
    }

    /// <summary>Write an admitted item to the pump. Called under <see cref="_gate"/>; the slot is already reserved.</summary>
    private bool TryPublish(WorkItem item) => _channel.Writer.TryWrite(item);

    /// <summary>
    /// Hold the reserved forward until the transport has acknowledged it on the wire, then publish
    /// it; drop it (releasing the reservation) when the acknowledgement is cancelled or faulted, and
    /// discard it when this handler shuts down first. Never throws.
    /// </summary>
    private async Task PublishWhenAcknowledgedAsync(WorkItem item)
    {
        var context = item.Context!;
        var id = context.Received.Message.Id;
        try
        {
            // The shutdown token, not the request token: a shutdown must release the reservation
            // promptly even when the host's acknowledgement never completes.
            await context.Acknowledged.WaitAsync(_shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            ReleaseSlot(item.Bytes);
            Interlocked.Increment(ref _discardedAtShutdown);
            return;
        }
        catch (Exception)
        {
            // Cancelled (client abort before the response was committed; a rejection after the
            // handler ran) or faulted: the sender never received the 202, so nothing is relayed.
            // The exception's text stays out of the logs.
            ReleaseSlot(item.Bytes);
            SafeLogWarning("Forward {MessageId} dropped: the transport did not acknowledge it; not relayed.", id);
            return;
        }

        bool published;
        lock (_gate)
        {
            published = Volatile.Read(ref _shutdownStarted) == 0 && TryPublish(item);
        }

        if (!published)
        {
            ReleaseSlot(item.Bytes);
            Interlocked.Increment(ref _discardedAtShutdown);
        }
    }

    private void TrackPendingAcknowledgement(Task pending)
    {
        if (pending.IsCompleted)
            return; // ran to completion synchronously (the acknowledgement was already cancelled or faulted)

        _pendingAcknowledgements.TryAdd(pending, 0);
        // Remove on completion. If it completed between the check and the add, this runs at once.
        _ = pending.ContinueWith(
            static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
            _pendingAcknowledgements,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

                // ReadAllAsync hands out already-buffered items without re-checking the token, so
                // once shutdown is signalled a sink that returns on cancellation would let this loop
                // race DrainQueued for the forwards still queued — and hand them to the sink with a
                // cancelled token. Discard this one and stop; the finally drains the rest (fail closed).
                if (shutdown.IsCancellationRequested)
                {
                    ReleaseSlot(item.Bytes);
                    Interlocked.Increment(ref _discardedAtShutdown);
                    break;
                }

                try
                {
                    await ProcessOneAsync(item, shutdown).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseSlot(item.Bytes);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Normal pump shutdown.
        }
        catch (Exception ex)
        {
            SafeLogWarning("Forward handler pump faulted with {ExceptionType}; the fault was observed and isolated.", ex.GetType().Name);
        }
        finally
        {
            DrainQueued();
        }
    }

    private async Task ProcessOneAsync(WorkItem item, CancellationToken shutdown)
    {
        var context = item.Context!;
        var id = context.Received.Message.Id;
        try
        {
            // The schedule was fixed at admission (FR-ROUTE-10); the processor does not re-read the clock.
            var result = await _processor.ProcessAsync(context.Received, item.Delay, item.NotBefore, shutdown).ConfigureAwait(false);

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
            // CryptoException, sink faults. Type names only — a DidResolutionException carries the
            // next-hop DID in its message, so the exception object never reaches the logger.
            SafeLogWarning(
                "Forward {MessageId} not relayed: {ExceptionType} (innermost: {InnerExceptionType}).",
                id, ex.GetType().Name, InnermostTypeName(ex));
        }
    }

    private static string InnermostTypeName(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException is { } next)
            inner = next;
        return inner.GetType().Name;
    }

    /// <summary>
    /// Complete when every forward admitted before this call has been handed to the sink or
    /// dropped — forwards still waiting for their transport acknowledgement are waited for first.
    /// The timeout covers flush serialization, pending acknowledgements, barrier insertion and
    /// sink completion.
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
            // Everything admitted before this call includes the forwards parked on their
            // acknowledgement: wait until each has been published or dropped, then fence the pump.
            var pending = _pendingAcknowledgements.Keys.ToArray();
            if (pending.Length > 0)
                await Task.WhenAll(pending).WaitAsync(cts.Token).ConfigureAwait(false);

            ThrowIfShutdown();
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                ThrowIfShutdown();
                // Admission is capped at _capacity while the channel has _capacity + 1 physical
                // slots, and Flush calls are serialized. The barrier therefore fits.
                if (!_channel.Writer.TryWrite(new WorkItem(Context: null, Bytes: 0, Delay: null, NotBefore: null, Barrier: barrier)))
                    throw new InvalidOperationException("Forward flush barrier could not be admitted despite its reserved channel slot.");
            }

            await barrier.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>Test/diagnostic seam: forwards reserved (awaiting acknowledgement), queued or in flight right now.</summary>
    internal int Outstanding
    {
        get
        {
            lock (_gate)
                return _outstandingItems;
        }
    }

    /// <summary>Test/diagnostic seam: plaintext UTF-8 bytes charged against the byte budget right now.</summary>
    internal long OutstandingBytes
    {
        get
        {
            lock (_gate)
                return _outstandingBytes;
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
                SafeLogWarning("A forward cancellation callback threw during shutdown ({ExceptionType}); continuing to drain the forward queue.", ex.GetType().Name);
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
            // Forwards parked on their acknowledgement were unblocked by the shutdown token; each
            // releases its reservation and counts itself as discarded. They never throw.
            var pending = _pendingAcknowledgements.Keys.ToArray();
            if (pending.Length > 0)
                await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The pump guards its own work; observe any unexpected fault here as a final boundary.
            SafeLogWarning("The forward handler pump faulted during shutdown ({ExceptionType}); the fault was observed.", ex.GetType().Name);
        }
        finally
        {
            // Both drains (the disposer's, synchronous in BeginShutdown, and the pump's own finally)
            // and the parked forwards have run by now, so this is the one place the total is
            // known: one line, once.
            var discarded = Volatile.Read(ref _discardedAtShutdown);
            if (discarded > 0)
                SafeLogInformation("Forward handler shut down: {Count} queued forward(s) discarded (fail closed).", discarded);
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
            ReleaseSlot(item.Bytes);
        }

        if (discarded > 0)
            Interlocked.Add(ref _discardedAtShutdown, discarded);
    }

    private void ReleaseSlot(int bytes)
    {
        lock (_gate)
        {
            _outstandingItems--;
            _outstandingBytes -= bytes;
        }
    }

    private void Drop(string messageId)
    {
        var dropped = Interlocked.Increment(ref _dropped);
        if (dropped != 1 && dropped % DropLogEvery != 0)
            return;

        SafeLogWarning(
            "Forward {MessageId} dropped: handler queue saturated (capacity {Capacity} forwards, {ByteBudget} plaintext bytes); fail closed. Total dropped: {Dropped}.",
            messageId,
            _capacity,
            _byteBudget,
            dropped);
    }

    // Never takes an exception: the logging contract is message id + exception type names only
    // (a DidResolutionException's message names the next-hop DID; a sink's may name anything).
    private void SafeLogWarning(string message, params object?[] args)
    {
        try
        {
            _logger?.LogWarning(message, args);
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
