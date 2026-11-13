using System.Collections.Concurrent;
using System.Threading.Channels;
using DidComm.Consistency;
using DidComm.Diagnostics;
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
/// <see cref="IForwardRecipientRegistry"/>. A forward addressed to this mediator itself is opened
/// here too, layer by layer, and the forward found inside is what is screened and looked up (see
/// below). Any failure drops the forward with a log line that
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
/// <strong>Logging contract.</strong> Every line carries the message id — bounded: an id longer
/// than 128 characters is written as its length — and, on a failure, the exception type name(s)
/// only. No exception object — and so no exception message — is ever
/// handed to the logger: a resolution or sink fault can carry the next-hop DID in its text, and a
/// console provider would print it. Any stranger can make the handler drop a forward, so each
/// drop line is rate-limited per reason: the first occurrence and every 1000th are written at
/// Warning (the "expired while queued" line at Information), the others at Debug with the same
/// text.
/// </para>
/// <para>
/// <strong>Outcome metric.</strong> Each forward this handler ends is counted once on the
/// <c>didcomm.forward.outcomes</c> counter of the <see cref="Diagnostics.DidCommDiagnostics.MeterName"/>
/// meter: <c>Dropped-Malformed</c> (not a forward, malformed, or refused by the processor as
/// inconsistent or malformed), <c>Dropped-NotEncrypted</c>, <c>Dropped-Expired</c> (on arrival or
/// while queued), <c>Dropped-Unknown</c> (<c>next</c> not a DID, or not registered),
/// <c>Dropped-Saturated</c>, <c>Dropped-Unacknowledged</c> (the transport never acknowledged it, or
/// the request was cancelled), <c>Dropped-Shutdown</c> or <c>Faulted</c> (a fault of the mediator's
/// own collaborators: the registry, the clock or the logger while screening, the processor or the
/// sink on the pump). A layer addressed to the mediator that cannot be opened is the sender's mistake
/// whatever the exception — <c>Dropped-Malformed</c>, or <c>Dropped-Expired</c> when the message
/// inside has expired — never <c>Faulted</c>. A forward the sink returns from normally is the sink's to count: the mediator's own
/// sink counts how it ended, a host's sink counts nothing.
/// </para>
/// <para>
/// <strong>Forwards addressed to this mediator itself (FR-ROUTE-06, receiver side).</strong> A
/// mediator that rewraps (DIDComm v2.1 §Routing "Rewrapping") sends the next hop a fresh
/// <c>forward</c> whose <c>next</c> is that hop itself, with the original forward inside. When the
/// DID subject of <c>next</c> is the DID whose key decrypted the forward
/// (<see cref="Facade.UnpackResult.RecipientKid"/>), the handler does not look it up in the
/// registry — the mediator's own DIDs are never registered recipients (FR-MED-05), and they are
/// public, so skipping the lookup tells a sender nothing. It opens the single attachment as it is,
/// with the mediator's own client, inline on the sender's request and before anything is reserved:
/// the sender pays for opening what it built, exactly as it pays for the outer unpack, and the
/// shared pump never runs a stranger's decryption or DID resolution. What is inside is screened
/// exactly as an arriving forward is, at the same receipt instant: a <c>forward</c>, encrypted to a
/// held key, not expired, its own <c>delay_milli</c> drawn under the cap, its <c>next</c> a
/// registered recipient (the one registry lookup of the request). A forward inside that is again
/// addressed to this mediator is opened in turn, at most <see cref="MaxSelfAddressedLayers"/> layers
/// per received forward — so one request costs at most that many unpacks, each of a payload smaller
/// than the last, the first bounded by <c>MaxReceiveBytes</c> — and one more is dropped as
/// malformed. The forward found inside is then admitted with the received one's acknowledgement
/// (both count against the byte budget) and the pump only processes and sinks it. What is inside and is not a forward is dropped: a mediator is not the final recipient
/// of an application message. A hold asked for by an outer layer is kept: the relayed forward's
/// not-before is the latest of its layers'. Only the DID that decrypted a layer counts as this
/// mediator: a forward naming another of the mediator's DIDs as <c>next</c> is looked up, and
/// dropped as unknown. The sink receives the innermost forward's processing result with the
/// received forward's context, and the forward the mediator received is counted once, by how the
/// chain ended.
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

    /// <summary>
    /// The most layers addressed to this mediator itself that are opened for one received forward
    /// (the received forward included): the longest route a sender builds, so a legal route never
    /// meets it, while a nesting made to burn decryptions does.
    /// </summary>
    internal const int MaxSelfAddressedLayers = Resolution.MediatorEndpointExpander.MaxForwardLayers;

    private readonly ForwardProcessor _processor;
    private readonly IForwardSink _sink;
    private readonly IForwardRecipientRegistry _registry;
    private readonly ILogger<ForwardHandler>? _logger;
    private readonly ForwardMetrics _metrics;
    private readonly bool _ownsMetrics;
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
    // One occurrence counter per drop line (DropReason), for the "first and every 1000th" rule.
    private readonly long[] _dropLines = new long[(int)DropReason.Count];
    private int _shutdownStarted;
    private int _discardedAtShutdown;

    /// <summary>Why the handler dropped a forward: each has its own rate-limited log line and one metric outcome.</summary>
    private enum DropReason
    {
        NotAForward,
        Malformed,
        Posture,
        ExpiredInline,
        NextNotDid,
        NotRegistered,
        ScreeningFault,
        Saturated,
        Unacknowledged,
        ExpiredWhileQueued,
        ProcessorMalformed,
        Faulted,
        SelfLayersTooDeep,
        PeelMalformed,
        InnerNotAForward,
        InnerPosture,
        InnerExpired,
        InnerNextNotDid,
        InnerNotRegistered,
        Count,
    }

    // The dispatcher-built ProtocolContext (an immutable record) is everything the detached work
    // needs: the UnpackResult, the options/clock and the facade. The request token is NOT stored.
    // Bytes is the plaintext UTF-8 charge the item holds against the byte budget until released.
    // Delay/NotBefore are the delay_milli schedule fixed at admission (FR-ROUTE-10). Opened: when the
    // received forward was addressed to this mediator itself, the forward found inside it — opened
    // inline, on the request (FR-ROUTE-06) — which is what the pump processes; null otherwise. The
    // context stays the received forward's: its acknowledgement, its id in the logs, the sink's view.
    private sealed record WorkItem(
        ProtocolContext? Context,
        int Bytes,
        TimeSpan? Delay,
        DateTimeOffset? NotBefore,
        TaskCompletionSource? Barrier,
        Facade.UnpackResult? Opened = null);

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

    /// <summary>
    /// Create the handler with the default queue bound and shutdown grace, counting outcomes on a
    /// shared <see cref="ForwardMetrics"/> (the container's, which the mediator's sink counts on
    /// too). <c>AddRoutingMediator()</c> uses this constructor.
    /// </summary>
    /// <param name="processor">The mediator's forward processor.</param>
    /// <param name="sink">Where processed forwards go.</param>
    /// <param name="registry">The recipients this mediator relays for.</param>
    /// <param name="metrics">The outcome counter; owned by the caller, never disposed here.</param>
    /// <param name="logger">Optional logger.</param>
    internal ForwardHandler(
        ForwardProcessor processor,
        IForwardSink sink,
        IForwardRecipientRegistry registry,
        ForwardMetrics metrics,
        ILogger<ForwardHandler>? logger)
        : this(processor, sink, registry, logger, DefaultCapacity, shutdownGrace: null, DefaultByteBudget, metrics ?? throw new ArgumentNullException(nameof(metrics))) { }

    /// <summary>Test seam: a handler with explicit queue bounds and shutdown grace.</summary>
    /// <param name="processor">The mediator's forward processor.</param>
    /// <param name="sink">Where processed forwards go.</param>
    /// <param name="registry">The recipients this mediator relays for.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="capacity">Maximum forwards queued or in flight; the newest is dropped beyond it.</param>
    /// <param name="shutdownGrace">How long <see cref="DisposeAsync"/> waits for the pump; <c>null</c> for 5 seconds.</param>
    /// <param name="byteBudget">Maximum plaintext UTF-8 bytes queued or in flight; a forward that would exceed it is dropped.</param>
    /// <param name="metrics">The outcome counter, owned by the caller; <c>null</c> to create one this handler owns and disposes.</param>
    internal ForwardHandler(
        ForwardProcessor processor,
        IForwardSink sink,
        IForwardRecipientRegistry registry,
        ILogger<ForwardHandler>? logger,
        int capacity,
        TimeSpan? shutdownGrace,
        long byteBudget = DefaultByteBudget,
        ForwardMetrics? metrics = null)
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
        _ownsMetrics = metrics is null;
        _metrics = metrics ?? new ForwardMetrics(meterFactory: null);
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
        if (ct.IsCancellationRequested)
        {
            // The request was abandoned before anything was decided: the sender gets no answer.
            _metrics.Record(ForwardOutcomes.DroppedUnacknowledged);
            ct.ThrowIfCancellationRequested();
        }

        // The id goes into log lines bounded: ids are the sender's, of any length.
        var logId = LogValues.Id(message.Id);
        int bytes;
        TimeSpan? delay;
        DateTimeOffset? notBefore;
        Facade.UnpackResult? opened = null;
        try
        {
            // (a) A forward at all, and well formed. Malformed (missing next/attachments, next over
            //     the FR-API-09 cap) is a drop, not a throw: nothing goes back to the sender.
            string next;
            try
            {
                if (!ForwardMessage.TryParse(message, out next, out _))
                {
                    Dropped(DropReason.NotAForward, "Forward {MessageId} dropped: not a forward message (FR-ROUTE-11).", logId);
                    return null;
                }
            }
            catch (MalformedMessageException ex)
            {
                Dropped(DropReason.Malformed, "Forward {MessageId} dropped: malformed forward (FR-ROUTE-01/FR-API-09): {ExceptionType}.", logId, ex.GetType().Name);
                return null;
            }

            // (b) FR-ROUTE-11 posture: encrypted and decrypted with a held key.
            if (!context.Received.Encrypted || string.IsNullOrEmpty(context.Received.RecipientKid))
            {
                Dropped(DropReason.Posture, "Forward {MessageId} dropped: plaintext, signed-only or not decrypted with a held key (FR-ROUTE-11).", logId);
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
                Dropped(DropReason.ExpiredInline, "Forward {MessageId} dropped: expired at relay time (FR-ROUTE-09).", logId);
                return null;
            }

            // The processor's own resolution, so its MaxDelay cap applies before the one draw.
            delay = _processor.ResolveDelay(message);
            notBefore = ForwardProcessor.NotBeforeOf(receivedAt, delay); // saturating: never throws near the end of time

            // (d) Registered recipient, by DID subject. Unknown → drop, no problem report (D4).
            var subject = DidSubject.DidSubjectOf(next);
            if (subject is null)
            {
                Dropped(DropReason.NextNotDid, "Forward {MessageId} dropped: next is not a DID or DID URL (FR-ROUTE-11).", logId);
                return null;
            }

            // (e) Size the forward for the byte budget before the registration lookup, so the
            //     work done inline is the same whether or not `next` is registered: the exact
            //     plaintext length the unpack path preserved (no re-serialization); a host-built
            //     synthetic result is serialized once. A sizing failure is a drop, never a throw.
            bytes = InboundMessageSnapshot.TryGetFor(message, out var snapshot)
                ? snapshot.Utf8ByteCount
                : InboundMessageSnapshot.CreateFallback(context.Received).Utf8ByteCount;

            // (f) A forward whose next is the DID that decrypted it is a rewrapped layer addressed to
            //     this mediator itself (FR-ROUTE-06). It is opened here, on the sender's own request
            //     and before anything is reserved — the sender pays for opening what it sent, as it
            //     paid for the outer unpack — and the forward found inside is screened like this
            //     one. The mediator's own DID is never looked up (it is public and never registered,
            //     FR-MED-05); the forward that is finally relayed is.
            if (IsSelfAddressed(subject, context.Received.RecipientKid))
            {
                if (await OpenSelfAddressedAsync(context.Received, receivedAt, context.Options.ExpiresClockSkew, logId, ct).ConfigureAwait(false) is not { } inside)
                    return null; // dropped and counted
                opened = inside.Forward;
                subject = inside.Subject;
                if (inside.NotBefore is { } later && (notBefore is not { } earlier || later > earlier))
                    (delay, notBefore) = (inside.Delay, inside.NotBefore); // a hold an outer layer asked for is kept
                // The opened forward is held with the received one: both count against the budget.
                bytes = (int)Math.Min(int.MaxValue, (long)bytes + PlaintextBytes(inside.Forward));
            }

            // (g) The forward to relay must name a registered recipient: one constant-work lookup,
            //     whether or not layers were opened to reach it.
            if (!await _registry.IsRegisteredAsync(subject, ct).ConfigureAwait(false))
            {
                Dropped(
                    opened is null ? DropReason.NotRegistered : DropReason.InnerNotRegistered,
                    opened is null
                        ? "Forward {MessageId} dropped: next is not a registered recipient (FR-ROUTE-11)."
                        : "Forward {MessageId} dropped: the forward inside it names a next that is not a registered recipient (FR-ROUTE-11).",
                    logId);
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _metrics.Record(ForwardOutcomes.DroppedUnacknowledged);
            throw;
        }
        catch (Exception ex)
        {
            // A registry (or logger / clock) fault must not become a 400 toward the sender.
            Dropped(DropReason.ScreeningFault, "Forward {MessageId} dropped: inline screening failed with {ExceptionType}.", logId, ex.GetType().Name);
            return null;
        }

        // Admit: reserve the slot and the bytes inline, so saturation is decided on the request
        // like every other drop. Everything that depends on the registration (processing, the
        // sink) happens on the pump, and only after the transport has acknowledged the inbound.
        var item = new WorkItem(context, bytes, delay, notBefore, Barrier: null, opened);
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
            DropSaturated(logId);
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
            Discarded(1);
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
        var id = LogValues.Id(context.Received.Message.Id);
        try
        {
            // The shutdown token, not the request token: a shutdown must release the reservation
            // promptly even when the host's acknowledgement never completes.
            await context.Acknowledged.WaitAsync(_shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            ReleaseSlot(item.Bytes);
            Discarded(1);
            return;
        }
        catch (Exception)
        {
            // Cancelled (client abort before the response was committed; a rejection after the
            // handler ran) or faulted: the sender never received the 202, so nothing is relayed.
            // The exception's text stays out of the logs.
            ReleaseSlot(item.Bytes);
            Dropped(DropReason.Unacknowledged, "Forward {MessageId} dropped: the transport did not acknowledge it; not relayed.", id);
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
            Discarded(1);
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
                    Discarded(1);
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
        var id = LogValues.Id(context.Received.Message.Id);
        // Which stage a fault came from decides its outcome: a processor that refuses the forward
        // as inconsistent or malformed ends a malformed forward; anything else is a fault.
        var processed = false;
        try
        {
            // A received forward addressed to this mediator may itself expire while it waits; the
            // forward opened from it is checked on the result below.
            if (item.Opened is not null
                && ForwardProcessingResult.IsExpiredAt(context.Received.Message.ExpiresTime, context.Options.Now(), context.Options.ExpiresClockSkew))
            {
                Dropped(DropReason.ExpiredWhileQueued, "Forward {MessageId} discarded: expired while queued (FR-ROUTE-09).", id);
                return;
            }

            // The schedule was fixed at admission (FR-ROUTE-10); the processor does not re-read the
            // clock. An opened forward was screened inline; the pump only processes and sinks it.
            var result = await _processor.ProcessAsync(item.Opened ?? context.Received, item.Delay, item.NotBefore, shutdown).ConfigureAwait(false);
            processed = true;

            // FR-ROUTE-09 again: the time spent queued may have crossed the forward's expiry.
            if (result.IsExpired(context.Options.Now(), context.Options.ExpiresClockSkew))
            {
                Dropped(DropReason.ExpiredWhileQueued, "Forward {MessageId} discarded: expired while queued (FR-ROUTE-09).", id);
                return;
            }

            // From here the sink counts how the forward ended; the handler counts it only when the
            // sink throws, or is cancelled by the shutdown.
            await _sink.AcceptAsync(result, context, shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Normal cooperative shutdown; the forward is discarded (fail closed).
            _metrics.Record(ForwardOutcomes.DroppedShutdown);
        }
        catch (Exception ex)
        {
            // ConsistencyException, MalformedMessageException, DidResolutionException,
            // CryptoException, sink faults. Type names only — a DidResolutionException carries the
            // next-hop DID in its message, so the exception object never reaches the logger.
            var reason = !processed && ex is ConsistencyException or MalformedMessageException
                ? DropReason.ProcessorMalformed
                : DropReason.Faulted;
            Dropped(
                reason,
                "Forward {MessageId} not relayed: {ExceptionType} (innermost: {InnerExceptionType}).",
                id, ex.GetType().Name, LogValues.InnermostTypeName(ex));
        }
    }

    /// <summary>
    /// Open a forward addressed to this mediator itself, and every such layer inside it, inline on
    /// the sender's request, and screen the forward found inside as an arriving forward is screened
    /// (all at the receipt instant). Returns that forward, the DID subject of its <c>next</c> and its
    /// own schedule, or <c>null</c> when the chain ended in a drop — counted and logged here, with the
    /// received forward's id. Cancellation of the request, and a fault that is not a refusal of the
    /// unpack, leave to <see cref="HandleAsync"/>.
    /// </summary>
    /// <param name="received">The received forward (addressed to this mediator itself).</param>
    /// <param name="receivedAt">The receipt reading of the clock.</param>
    /// <param name="skew">The expiry skew.</param>
    /// <param name="logId">The received forward's id, bounded.</param>
    /// <param name="ct">The request's token.</param>
    private async Task<(Facade.UnpackResult Forward, string Subject, TimeSpan? Delay, DateTimeOffset? NotBefore)?> OpenSelfAddressedAsync(
        Facade.UnpackResult received, DateTimeOffset receivedAt, TimeSpan skew, string logId, CancellationToken ct)
    {
        var layer = received;
        for (var opened = 1; ; opened++)
        {
            if (opened > MaxSelfAddressedLayers)
            {
                Dropped(DropReason.SelfLayersTooDeep, "Forward {MessageId} dropped: more than {MaxLayers} layers addressed to this mediator itself (FR-ROUTE-06).", logId, MaxSelfAddressedLayers);
                return null;
            }

            Facade.UnpackResult inner;
            try
            {
                inner = await _processor.PeelAsync(layer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (MalformedMessageException ex) when (ex.Expired)
            {
                Dropped(DropReason.InnerExpired, "Forward {MessageId} dropped: the forward inside it has expired (FR-ROUTE-09).", logId);
                return null;
            }
            catch (Exception ex)
            {
                // Whatever stops a layer the sender built from opening is the sender's: its structure,
                // a key this mediator does not hold, a DID that does not resolve (or a resolver that
                // throws while trying — the sender chose which DIDs the layer makes us resolve). So
                // it is a malformed forward, never a fault a stranger could raise at will; Faulted
                // stays for the mediator's own collaborators (registry, clock, logger, processor, sink).
                Dropped(DropReason.PeelMalformed, "Forward {MessageId} dropped: the message inside it could not be opened: {ExceptionType} (innermost: {InnerExceptionType}) (FR-ROUTE-06).", logId, ex.GetType().Name, LogValues.InnermostTypeName(ex));
                return null;
            }

            // What is inside is screened exactly as an arriving forward is ((a)..(d) of HandleAsync).
            string next;
            try
            {
                if (!ForwardMessage.TryParse(inner.Message, out next, out _))
                {
                    Dropped(DropReason.InnerNotAForward, "Forward {MessageId} dropped: the message inside it is not a forward; a mediator is not its final recipient (FR-ROUTE-06).", logId);
                    return null;
                }
            }
            catch (MalformedMessageException ex)
            {
                Dropped(DropReason.PeelMalformed, "Forward {MessageId} dropped: the message inside it could not be opened: {ExceptionType} (innermost: {InnerExceptionType}) (FR-ROUTE-06).", logId, ex.GetType().Name, LogValues.InnermostTypeName(ex));
                return null;
            }

            if (!inner.Encrypted || string.IsNullOrEmpty(inner.RecipientKid))
            {
                Dropped(DropReason.InnerPosture, "Forward {MessageId} dropped: the forward inside it is plaintext, signed-only or not decrypted with a held key (FR-ROUTE-11).", logId);
                return null;
            }

            if (ForwardProcessingResult.IsExpiredAt(inner.Message.ExpiresTime, receivedAt, skew))
            {
                Dropped(DropReason.InnerExpired, "Forward {MessageId} dropped: the forward inside it has expired (FR-ROUTE-09).", logId);
                return null;
            }

            var subject = DidSubject.DidSubjectOf(next);
            if (subject is null)
            {
                Dropped(DropReason.InnerNextNotDid, "Forward {MessageId} dropped: the forward inside it names a next that is not a DID or DID URL (FR-ROUTE-11).", logId);
                return null;
            }

            if (IsSelfAddressed(subject, inner.RecipientKid))
            {
                layer = inner;
                continue;
            }

            // Its own hold, drawn at receipt under the cap (the caller keeps an outer layer's later one).
            var delay = _processor.ResolveDelay(inner.Message);
            return (inner, subject, delay, ForwardProcessor.NotBeforeOf(receivedAt, delay));
        }
    }

    /// <summary>The exact plaintext size of an unpacked message, as the unpack path recorded it (serialized once otherwise).</summary>
    /// <param name="unpacked">The unpack outcome.</param>
    private static int PlaintextBytes(Facade.UnpackResult unpacked)
        => InboundMessageSnapshot.TryGetFor(unpacked.Message, out var snapshot)
            ? snapshot.Utf8ByteCount
            : InboundMessageSnapshot.CreateFallback(unpacked).Utf8ByteCount;

    /// <summary>
    /// Whether a forward is addressed to this mediator itself: the DID subject of its
    /// <c>next</c> is the DID of the key that decrypted it. Only that DID counts — not another DID
    /// the mediator also holds — so the answer needs nothing but the envelope.
    /// </summary>
    /// <param name="nextSubject">The DID subject of <c>next</c>.</param>
    /// <param name="recipientKid">The kid that decrypted the forward.</param>
    private static bool IsSelfAddressed(string nextSubject, string? recipientKid)
        => DidSubject.DidSubjectOf(recipientKid) is { } decryptedBy
           && string.Equals(nextSubject, decryptedBy, StringComparison.Ordinal);

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

    /// <summary>Test seam: the outcome counter this handler records on (its own, or the shared one it was given).</summary>
    internal ForwardMetrics Metrics => _metrics;

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
            if (_ownsMetrics)
                _metrics.Dispose();
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
            Discarded(discarded);
    }

    /// <summary>Count forwards discarded by the shutdown: the summary line's total and the metric, in one place.</summary>
    /// <param name="count">How many forwards.</param>
    private void Discarded(int count)
    {
        Interlocked.Add(ref _discardedAtShutdown, count);
        _metrics.Record(ForwardOutcomes.DroppedShutdown, count);
    }

    private void ReleaseSlot(int bytes)
    {
        lock (_gate)
        {
            _outstandingItems--;
            _outstandingBytes -= bytes;
        }
    }

    private void DropSaturated(string messageId)
    {
        _metrics.Record(ForwardOutcomes.DroppedSaturated);
        var level = RateLimitedLog.Level(ref _dropLines[(int)DropReason.Saturated], LogLevel.Warning, out var dropped);
        SafeLog(
            level,
            "Forward {MessageId} dropped: handler queue saturated (capacity {Capacity} forwards, {ByteBudget} plaintext bytes); fail closed. Total dropped: {Dropped}.",
            messageId,
            _capacity,
            _byteBudget,
            dropped);
    }

    /// <summary>
    /// Count one forward ended by <paramref name="reason"/> on the outcome metric and write its
    /// line, rate-limited per reason (the first and every 1000th at the line's level, Debug
    /// otherwise). The arguments are the message id and type names only.
    /// </summary>
    /// <param name="reason">Why the forward was dropped.</param>
    /// <param name="message">The line's template.</param>
    /// <param name="args">The template's arguments.</param>
    private void Dropped(DropReason reason, string message, params object?[] args)
    {
        _metrics.Record(OutcomeOf(reason));
        var loud = reason == DropReason.ExpiredWhileQueued ? LogLevel.Information : LogLevel.Warning;
        SafeLog(RateLimitedLog.Level(ref _dropLines[(int)reason], loud, out _), message, args);
    }

    private static string OutcomeOf(DropReason reason) => reason switch
    {
        DropReason.NotAForward or DropReason.Malformed or DropReason.ProcessorMalformed
            or DropReason.SelfLayersTooDeep or DropReason.PeelMalformed or DropReason.InnerNotAForward => ForwardOutcomes.DroppedMalformed,
        DropReason.Posture or DropReason.InnerPosture => ForwardOutcomes.DroppedNotEncrypted,
        DropReason.ExpiredInline or DropReason.ExpiredWhileQueued or DropReason.InnerExpired => ForwardOutcomes.DroppedExpired,
        DropReason.NextNotDid or DropReason.NotRegistered or DropReason.InnerNextNotDid or DropReason.InnerNotRegistered => ForwardOutcomes.DroppedUnknown,
        DropReason.Saturated => ForwardOutcomes.DroppedSaturated,
        DropReason.Unacknowledged => ForwardOutcomes.DroppedUnacknowledged,
        _ => ForwardOutcomes.Faulted,
    };

    // Never takes an exception: the logging contract is message id + exception type names only
    // (a DidResolutionException's message names the next-hop DID; a sink's may name anything).
    private void SafeLog(LogLevel level, string message, params object?[] args)
    {
        try
        {
            _logger?.Log(level, message, args);
        }
        catch
        {
            // Logging providers are host code; they must not change dispatch or shutdown behaviour.
        }
    }

    private void SafeLogWarning(string message, params object?[] args) => SafeLog(LogLevel.Warning, message, args);

    private void SafeLogInformation(string message, params object?[] args) => SafeLog(LogLevel.Information, message, args);

    private void ThrowIfShutdown()
    {
        if (Volatile.Read(ref _shutdownStarted) != 0)
            throw new ObjectDisposedException(nameof(ForwardHandler));
    }
}
