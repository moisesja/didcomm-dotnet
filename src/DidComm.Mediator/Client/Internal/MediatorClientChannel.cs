using System.Collections.Concurrent;
using System.Globalization;
using DidComm.Consistency;
using DidComm.Facade;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The one correlation channel of a container (design D-2, §3.2, §4.1–4.3): the bounded pending
/// map, the matcher (identity before lookup, D-7), route resolution, the live-stream registry, the
/// duplicate filter, the version cache and the unpack limiter. It is the
/// <see cref="IInboundCorrelator"/> the dispatcher hands every verified inbound to (DI only), and
/// the <see cref="IMediatorExchange"/> both facades are built over. Owner: engineer A.
/// </summary>
/// <remarks>
/// <para>
/// <strong>DI.</strong> <c>AddMediatorClient()</c> registers one instance as a singleton and as an
/// <see cref="IInboundCorrelator"/>; the constructor resolves nothing (L-017). The container
/// disposes it once per registration it was resolved through, so disposal is idempotent.
/// </para>
/// <para>
/// <strong>Non-DI.</strong> Each public facade constructor builds a private instance that is not a
/// correlator (<c>receivesInbound: false</c>): HTTP works, a WebSocket route is refused (D-4). Its
/// log lines go to the logger the caller passed to the facade.
/// </para>
/// <para>
/// <strong>Routes it cannot receive on (D-4).</strong> A route that is not HTTP is refused before
/// anything is sent when no answer could reach this channel: no inbound consumer is registered, or
/// the transport the facade would send through says that what the peer pushes back does not reach
/// it (<see cref="DidCommClient.ReceivesPushesOn"/>: the WebSocket transport with
/// <c>ReceiveInbound</c> off, or under STOMP). When the facade cannot tell (a router or transport of
/// the host's own) the route is allowed.
/// </para>
/// <para>
/// <strong>Matching (§4.2).</strong> An inbound is looked at only when its type is a mediator answer
/// type (<see cref="MediatorAnswerTypes"/>). Then, before any pending request or live stream is
/// looked up, it must be a verified snapshot (never a hand-built one) authcrypted by its
/// <c>from</c> to one of our DIDs (<see cref="ReturnRouteEligibility"/>, the return-route identity
/// rule: the key that decrypted it names the DID), and a <c>to</c>, when it has one (it is optional
/// in DIDComm v2.1), must name that DID: every comparison runs, combined without short-circuit, and
/// a failure returns silently. Only then is its thread (<c>pthid</c>, else <c>thid</c>, for a problem report) looked
/// up — never when it is longer than <see cref="MaxThreadIdLength"/> — and the request completes
/// only when its (mediator, local DID) pair and expected types agree. A message without a thread
/// completes the one HTTP request whose response carried it, or the only waiting request of its
/// pair that expects its type (none or several: dropped); a thread-less <c>delivery</c> is a live
/// push and reaches only the live stream of its pair, never a request. A message that does not
/// complete anything leaves every pending request as it was, so a forgery can neither answer nor
/// cancel one.
/// </para>
/// <para>
/// <strong>Deadlines.</strong> Every wait — the mediator's endpoint, the send and the answer — runs
/// under a <see cref="GuardedDeadline"/> on the injected <see cref="TimeProvider"/>; the token the
/// send receives is cancelled inside a <c>try</c>, so nothing host code registers on it can escape.
/// Finding the endpoint and each request have a <see cref="MediatorClientOptions.RequestTimeout"/>
/// of their own.
/// </para>
/// <para>
/// <strong>Log levels.</strong> A drop that points at a mediator problem is a Warning (the first and
/// every 1000th, Debug otherwise): an answer to a waiting request that does not fit it, an ambiguous
/// thread-less answer, a live push its stream's full buffer refuses. A message of an answer type
/// that simply is not for a waiting request (late, or another part of this agent's) is a Debug line
/// (the first and every 1000th, Trace otherwise). Neither ever claims the message.
/// </para>
/// </remarks>
internal sealed class MediatorClientChannel : IInboundCorrelator, IMediatorExchange, IDisposable, IAsyncDisposable
{
    /// <summary>The bound on open live streams per channel.</summary>
    internal const int MaxLiveStreams = 16;

    /// <summary>The longest thread id that is looked up; our request ids are 36-character UUIDs.</summary>
    internal const int MaxThreadIdLength = 64;

    /// <summary>How many <c>to</c> entries of an answer are compared with the DID it was decrypted for.</summary>
    internal const int MaxAddresseesInspected = 64;

    private const string CannotReceiveRefusal =
        "The mediator's endpoint is not HTTP, so its answers arrive on the WebSocket connection, and this client cannot receive them: " +
        "register UseWebSocketTransport(o => o.ReceiveInbound = true) and UseInboundDispatch() together with AddMediatorClient(). " +
        "A client built without dependency injection works over HTTP only. Nothing was sent.";

    private const string LiveNeedsWebSocketRefusal =
        "Live delivery needs a WebSocket route to the mediator (ws or wss): the mediator pushes messages on that connection. " +
        "Pass a ws:// or wss:// serviceEndpointOverride, or poll with FetchAsync. Nothing was sent.";

    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LiveRegistration> _live = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<Message, SendOptions, CancellationToken, Task<SendResult>> _send;
    private readonly Func<string, CancellationToken, Task<Uri>> _resolveEndpoint;
    private int _pendingCount;
    private int _liveCount;
    private int _active;
    private int _disposed;
    private long _unclaimedAnswers;
    private long _mismatchedAnswers;
    private long _ambiguousAnswers;
    private long _droppedPushes;
    private long _strayPushes;
    private long _problemReports;
    private long _lookups;

    /// <summary>Build a channel; resolves and starts nothing.</summary>
    /// <param name="client">The facade requests are sent through and deliveries unpacked with.</param>
    /// <param name="options">The settings; copied and validated here.</param>
    /// <param name="receivesInbound">Whether answers can reach this channel through the dispatcher: <c>true</c> under DI with an <c>IInboundFrameConsumer</c> registered (<c>UseInboundDispatch()</c>). A WebSocket route is refused when <c>false</c>.</param>
    /// <param name="time">The clock of every deadline and interval.</param>
    /// <param name="loggerFactory">The host's logger factory, or <c>null</c> for no logging.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/>, <paramref name="options"/> or <paramref name="time"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting of <paramref name="options"/> is outside its range.</exception>
    internal MediatorClientChannel(DidCommClient client, MediatorClientOptions options, bool receivesInbound, TimeProvider time, ILoggerFactory? loggerFactory)
        : this(client, options, receivesInbound, time, loggerFactory, send: null, resolveEndpoint: null, unpack: null)
    {
    }

    /// <summary>
    /// Build a channel over replaceable steps (the test seam): the send, the endpoint resolution and
    /// the delivered-message unpack default to <paramref name="client"/>'s
    /// <see cref="DidCommClient.SendAsync"/>, <see cref="DidCommClient.ResolveDirectEndpointAsync"/>
    /// and <see cref="DidCommClient.UnpackDeliveredAsync"/>.
    /// </summary>
    /// <param name="client">The facade.</param>
    /// <param name="options">The settings; copied and validated here.</param>
    /// <param name="receivesInbound">Whether answers can reach this channel through the dispatcher.</param>
    /// <param name="time">The clock of every deadline and interval.</param>
    /// <param name="loggerFactory">The host's logger factory, or <c>null</c>.</param>
    /// <param name="send">The send step, or <c>null</c> for <paramref name="client"/>'s.</param>
    /// <param name="resolveEndpoint">The endpoint resolution step, or <c>null</c> for <paramref name="client"/>'s.</param>
    /// <param name="unpack">The delivered-message unpack step, or <c>null</c> for <paramref name="client"/>'s.</param>
    internal MediatorClientChannel(
        DidCommClient client,
        MediatorClientOptions options,
        bool receivesInbound,
        TimeProvider time,
        ILoggerFactory? loggerFactory,
        Func<Message, SendOptions, CancellationToken, Task<SendResult>>? send,
        Func<string, CancellationToken, Task<Uri>>? resolveEndpoint,
        Func<string, CancellationToken, Task<DeliveredUnpack>>? unpack)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        Client = client;
        Options = options.ValidatedCopy();
        ReceivesInbound = receivesInbound;
        Time = time;
        Logger = loggerFactory?.CreateLogger<MediatorClientChannel>();
        Duplicates = new DuplicateFilter(Options.DedupeCapacity);
        Versions = new NegotiatedVersionCache(time);
        Unpacker = new DeliveredMessageUnpacker(unpack ?? client.UnpackDeliveredAsync, Options.UnpackTimeout, time);
        _send = send ?? client.SendAsync;
        _resolveEndpoint = resolveEndpoint ?? client.ResolveDirectEndpointAsync;
    }

    /// <summary>The facade requests are sent through.</summary>
    internal DidCommClient Client { get; }

    /// <summary>Whether answers can reach this channel through the dispatcher (see the constructor).</summary>
    internal bool ReceivesInbound { get; }

    /// <summary>The channel's logger (category <c>DidComm.Mediator.Client.Internal.MediatorClientChannel</c>), or <c>null</c>.</summary>
    internal ILogger? Logger { get; }

    /// <summary>The unpack limiter behind <see cref="UnpackDeliveredAsync"/> and <see cref="UnpackDeliveredBatchAsync"/>.</summary>
    internal DeliveredMessageUnpacker Unpacker { get; }

    /// <summary>Whether <see cref="Dispose"/> or <see cref="DisposeAsync"/> has run.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>How many requests are waiting now (test seam).</summary>
    internal int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>How many live streams are open now (test seam).</summary>
    internal int LiveStreamCount => Volatile.Read(ref _liveCount);

    /// <summary>How many times the matcher looked into the pending map or the live registry (test seam: identity comes first, D-7).</summary>
    internal long Lookups => Interlocked.Read(ref _lookups);

    /// <inheritdoc/>
    public MediatorClientOptions Options { get; }

    /// <inheritdoc/>
    public TimeProvider Time { get; }

    /// <inheritdoc/>
    public DuplicateFilter Duplicates { get; }

    /// <inheritdoc/>
    public NegotiatedVersionCache Versions { get; }

    /// <inheritdoc/>
    public async Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentException.ThrowIfNullOrEmpty(mediatorDid);
        ct.ThrowIfCancellationRequested();

        Uri endpoint;
        if (serviceEndpointOverride is not null)
        {
            // The caller's endpoint is trusted and not guarded, as SendOptions.ServiceEndpointOverride.
            if (!serviceEndpointOverride.IsAbsoluteUri)
                throw new ArgumentException("The service endpoint override must be an absolute URI.", nameof(serviceEndpointOverride));
            endpoint = serviceEndpointOverride;
        }
        else
        {
            using var deadline = new GuardedDeadline(Time, Options.RequestTimeout, ct, ShutdownToken);
            Task<Uri>? resolving = null;
            try
            {
                resolving = _resolveEndpoint(mediatorDid, deadline.Token);
                endpoint = await resolving.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.Token.IsCancellationRequested)
            {
                if (resolving is not null)
                    GuardedDeadline.ObserveAbandoned(resolving);
                if (IsDisposed)
                    throw Disposed();
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                    $"Finding the mediator's endpoint did not finish within {Options.RequestTimeout} (MediatorClientOptions.RequestTimeout); nothing was sent."));
            }
        }

        // Uri.Scheme is lower case. Any scheme that is neither HTTP nor WebSocket is treated like a
        // WebSocket: the answer can only come back through the dispatcher.
        var scheme = endpoint.Scheme;
        var isHttp = scheme is "http" or "https";
        var isWebSocket = scheme is "ws" or "wss";
        if (!isHttp && !CanReceiveOn(endpoint))
            throw new InvalidOperationException(CannotReceiveRefusal);
        if (requireWebSocket && !isWebSocket)
            throw new InvalidOperationException(LiveNeedsWebSocketRefusal);
        return new MediatorRoute(endpoint, isHttp, isWebSocket);
    }

    /// <inheritdoc/>
    public async Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        MediatorAnswerTypes.Validate(spec);
        if (string.IsNullOrEmpty(request.Id) || request.Id.Length > MaxThreadIdLength)
            throw new ArgumentException($"The request id must be 1 to {MaxThreadIdLength} characters (a UUID this agent mints).", nameof(request));
        var mediatorSubject = DidSubject.DidSubjectOf(mediatorDid)
            ?? throw new ArgumentException("The mediator DID is not a DID.", nameof(mediatorDid));
        var localSubject = DidSubject.DidSubjectOf(localDid)
            ?? throw new ArgumentException("The local DID is not a DID.", nameof(localDid));
        ct.ThrowIfCancellationRequested();

        // The bound is taken before anything is registered or sent (L-055).
        if (Interlocked.Increment(ref _pendingCount) > Options.MaxPendingRequests)
        {
            Interlocked.Decrement(ref _pendingCount);
            throw new MediatorBusyException(string.Create(CultureInfo.InvariantCulture,
                $"{Options.MaxPendingRequests} mediator requests are already waiting for an answer (MediatorClientOptions.MaxPendingRequests); request '{request.Id}' ({spec.Name}) was not sent."));
        }

        var pending = new PendingRequest(request.Id, spec, mediatorSubject, localSubject, Time.GetTimestamp());
        Interlocked.Increment(ref _active);
        try
        {
            // Registered before sending: over a WebSocket the answer can arrive before the send returns.
            if (!_pending.TryAdd(request.Id, pending))
                throw new InvalidOperationException($"Mediator request '{request.Id}' ({spec.Name}) is already waiting; a request id is used once.");
            // Disposal may have failed the waiting requests just before this one was added.
            if (IsDisposed)
                throw Disposed();

            var answer = await ExchangeCoreAsync(route, request, pending, mediatorDid, localDid, ct).ConfigureAwait(false);
            MediatorLog.Write(Logger, LogLevel.Debug, "Mediator request {RequestId} ({Request}) answered after {ElapsedMs} ms.",
                pending.Id, spec.Name, ElapsedMs(pending));
            return answer;
        }
        catch (Exception ex)
        {
            MediatorLog.Write(Logger, LogLevel.Debug, "Mediator request {RequestId} ({Request}) ended without a usable answer after {ElapsedMs} ms: {ExceptionType}.",
                pending.Id, spec.Name, ElapsedMs(pending), ex.GetType().Name);
            throw;
        }
        finally
        {
            // Whatever ended the exchange, a late answer now finds no waiting request.
            pending.TryAbandon();
            _pending.TryRemove(new KeyValuePair<string, PendingRequest>(request.Id, pending));
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Decrement(ref _active);
        }
    }

    // Steps 3–9 of §4.1, for a registered request.
    private async Task<InboundMessageSnapshot> ExchangeCoreAsync(MediatorRoute route, Message request, PendingRequest pending, string mediatorDid, string localDid, CancellationToken ct)
    {
        using var deadline = new GuardedDeadline(Time, Options.RequestTimeout, ct, ShutdownToken);
        // The deadline, the caller and the shutdown end the wait through the same terminal CAS an
        // answer competes on.
        using var abandon = deadline.Token.UnsafeRegister(static state => ((PendingRequest)state!).TryAbandon(), pending);

        MediatorLog.Write(Logger, LogLevel.Debug, "Mediator request {RequestId} ({Request}) sending.", pending.Id, pending.Spec.Name);
        SendResult? sent = null;
        var options = new SendOptions(new[] { mediatorDid }, From: localDid, ServiceEndpointOverride: route.Endpoint)
        {
            ReturnRoute = ReturnRouteMode.All,
        };
        Task<SendResult>? sending = null;
        try
        {
            sending = _send(request, options, deadline.Token);
            sent = await sending.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.Token.IsCancellationRequested)
        {
            if (sending is not null)
                GuardedDeadline.ObserveAbandoned(sending);
            // An answer that already won (over a WebSocket, before the send returned) holds.
            if (!pending.IsCompleted)
                throw Unanswered(pending, deadline, ct);
        }

        // The HTTP response's message, bound to this request (§4.2): it was identity-checked by
        // SendAsync and is checked again by the matcher, like any other answer.
        if (sent?.Returned is { } returned && InboundMessageSnapshot.TryGetFor(returned.Message, out var snapshot))
            Offer(snapshot, pending);

        // Over HTTP the response is the only channel for the answer (FR-RR-04): waiting is pointless.
        if (route.IsHttp && pending.IsWaiting)
        {
            var noAnswer = new TimeoutException(
                $"Mediator request '{pending.Id}' ({pending.Spec.Name}) got no answer in the HTTP response; the mediator may still have acted on it, so retry it (every request these clients send is safe to repeat).");
            if (pending.TryFail(noAnswer))
                throw noAnswer;
        }

        InboundMessageSnapshot answer;
        try
        {
            answer = await pending.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw Unanswered(pending, deadline, ct);
        }

        // Parsing never extends the dispatcher's receive path, even when the answer was handed over
        // before the send returned.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        if (IsProblemReport(answer))
        {
            var level = MediatorLog.RateLimited(ref _problemReports, out var count);
            MediatorLog.Write(Logger, level, "Mediator request {RequestId} ({Request}) was answered with a problem report. {Count} so far.",
                pending.Id, pending.Spec.Name, count);
            throw new MediationProtocolException(
                $"The mediator answered request '{pending.Id}' ({pending.Spec.Name}) with a problem report; its code, when well formed, is on ProblemCode.",
                pending.Id,
                MediatorProblemCode.Sanitize(ReadProblemCode(answer)));
        }
        return answer;
    }

    /// <inheritdoc/>
    public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var mediatorSubject = DidSubject.DidSubjectOf(mediatorDid)
            ?? throw new ArgumentException("The mediator DID is not a DID.", nameof(mediatorDid));
        var localSubject = DidSubject.DidSubjectOf(localDid)
            ?? throw new ArgumentException("The local DID is not a DID.", nameof(localDid));

        if (Interlocked.Increment(ref _liveCount) > MaxLiveStreams)
        {
            Interlocked.Decrement(ref _liveCount);
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{MaxLiveStreams} live streams are already open on this client; end one before opening another."));
        }
        var registration = new LiveRegistration(mediatorSubject, localSubject, RemoveLive);
        if (!_live.TryAdd(registration.Key, registration))
        {
            Interlocked.Decrement(ref _liveCount);
            throw new InvalidOperationException("A live stream for this mediator and DID is already open; only one may be open at a time.");
        }
        Interlocked.Increment(ref _active);
        if (IsDisposed)
        {
            registration.Dispose();
            throw Disposed();
        }
        return registration;
    }

    /// <inheritdoc/>
    public Task<DeliveredOutcome> UnpackDeliveredAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Unpacker.UnpackAsync(payload, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Unpacker.UnpackAllAsync(payloads, ct);
    }

    /// <summary>
    /// The matcher's unbound entry point (design §4.2): fast path when idle, identity before any
    /// lookup, then complete the pending request the snapshot answers or hand a thid-less delivery
    /// to its live stream. Never awaits, never parses a body.
    /// </summary>
    /// <param name="received">The verified snapshot the dispatcher created at unpack.</param>
    /// <returns><c>true</c> when the snapshot completed a pending request or reached a live stream.</returns>
    bool IInboundCorrelator.OnInbound(InboundMessageSnapshot received)
    {
        try
        {
            return Offer(received, boundTo: null);
        }
        catch
        {
            // Never: Offer only compares fields. The dispatcher guards the call too.
            return false;
        }
    }

    /// <summary>
    /// The matcher (design §4.2): offer <paramref name="received"/> to the pending requests and the
    /// live streams. <paramref name="boundTo"/> is the HTTP request whose response carried it, or
    /// <c>null</c> for a message the dispatcher received. Completes at most one request; never
    /// awaits, never parses a body, never removes or cancels anything.
    /// </summary>
    /// <param name="received">The snapshot.</param>
    /// <param name="boundTo">The HTTP request whose response carried it, or <c>null</c>.</param>
    /// <returns><c>true</c> when it completed a pending request or reached a live stream.</returns>
    internal bool Offer(InboundMessageSnapshot received, PendingRequest? boundTo)
    {
        ArgumentNullException.ThrowIfNull(received);
        // 0. Idle: one volatile read for a host that is also a mediator.
        if (boundTo is null && Volatile.Read(ref _active) == 0)
            return false;

        // 1. Only mediator answer types are looked at.
        if (!MessageTypeUri.TryParse(received.Type, out var type) || !MediatorAnswerTypes.TryClassify(type!, out var name, out var kind))
            return false;

        // 2. Identity before any lookup (D-7): a verified snapshot (never a hand-built one),
        //    authcrypted by the sender key's DID (the return-route rule: signer agrees, not a
        //    forward), whose `from` is that DID and whose `to`, when present, names the DID that
        //    decrypted it. Every comparison runs; they are combined without short-circuit. A
        //    failure is other traffic: returned silently, not logged.
        var eligible = ReturnRouteEligibility.TryGetPeer(received, out var peer, out var local, out _);
        var fromOk = string.Equals(DidSubject.DidSubjectOf(received.From), peer, StringComparison.Ordinal);
        var toOk = IsAddressedTo(received.To, local);
        if (!(received.Verified & eligible & fromOk & toOk))
            return false;

        // 3. The thread: a problem report names the request in pthid (else thid).
        var thread = kind == MediatorAnswerKind.ProblemReport && !string.IsNullOrEmpty(received.Pthid) ? received.Pthid : received.Thid;
        return !string.IsNullOrEmpty(thread)
            ? OfferThreaded(received, type!, name, kind, thread, peer, local)
            : OfferUnthreaded(received, type!, name, kind, peer, local, boundTo);
    }

    // §4.2 row 1–2: a thread names the request; bound or unbound alike (it may answer another request).
    private bool OfferThreaded(InboundMessageSnapshot received, MessageTypeUri type, string name, MediatorAnswerKind kind, string thread, string peer, string local)
    {
        if (thread.Length <= MaxThreadIdLength && Looked() && _pending.TryGetValue(thread, out var pending))
        {
            var pairOk = string.Equals(pending.MediatorSubject, peer, StringComparison.Ordinal)
                & string.Equals(pending.LocalSubject, local, StringComparison.Ordinal);
            var typeOk = (kind == MediatorAnswerKind.ProblemReport) | MediatorAnswerTypes.Expects(pending.Spec, type);
            if (pairOk & typeOk && pending.TryComplete(received))
                return true;
            // From the request's mediator to its DID, naming it, yet of a type it does not expect:
            // the mediator's mistake (the request keeps waiting and times out).
            if (pairOk & !typeOk)
            {
                LogMismatched(name, pending);
                return false;
            }
        }
        LogUnclaimed(name);
        return false;
    }

    // §4.2 rows 3–5: no thread.
    private bool OfferUnthreaded(InboundMessageSnapshot received, MessageTypeUri type, string name, MediatorAnswerKind kind, string peer, string local, PendingRequest? boundTo)
    {
        if (boundTo is not null)
        {
            // The HTTP exchange binds a thread-less answer to its own request — never to a stream.
            var pairOk = string.Equals(boundTo.MediatorSubject, peer, StringComparison.Ordinal)
                & string.Equals(boundTo.LocalSubject, local, StringComparison.Ordinal);
            var typeOk = kind switch
            {
                MediatorAnswerKind.Delivery => boundTo.Spec.ExpectsDelivery,
                MediatorAnswerKind.ProblemReport => true,
                _ => MediatorAnswerTypes.Expects(boundTo.Spec, type),
            };
            if (!(pairOk & typeOk))
            {
                // The response to our own request carried something that does not answer it.
                LogMismatched(name, boundTo);
                return false;
            }
            if (boundTo.TryComplete(received))
                return true;
            LogUnclaimed(name);
            return false;
        }

        switch (kind)
        {
            case MediatorAnswerKind.Delivery:
                // A live push: only the stream of its pair; never a pending delivery-request.
                if (Looked() && _live.TryGetValue(LiveRegistration.KeyFor(peer, local), out var stream))
                {
                    if (stream.TryPush(received))
                        return true;
                    if (stream.IsEnded)
                        LogStrayPush("its stream has ended");
                    else
                        LogDroppedPush("its stream's buffer is full");
                    return false;
                }
                LogStrayPush("no live stream is open for its mediator and DID");
                return false;

            case MediatorAnswerKind.ProblemReport:
                // A problem report names the request in pthid (REQUIRED); without a thread it answers nothing.
                LogUnclaimed(name);
                return false;

            default:
                return OfferToOnlyCandidate(received, type, name, peer, local);
        }
    }

    // §4.2 row 5, unbound: exactly one waiting request of the pair expecting the type takes it.
    private bool OfferToOnlyCandidate(InboundMessageSnapshot received, MessageTypeUri type, string name, string peer, string local)
    {
        PendingRequest? only = null;
        var candidates = 0;
        Looked();
        foreach (var pending in _pending.Values)
        {
            if (!pending.IsWaiting)
                continue;
            var pairOk = string.Equals(pending.MediatorSubject, peer, StringComparison.Ordinal)
                & string.Equals(pending.LocalSubject, local, StringComparison.Ordinal);
            if (pairOk && MediatorAnswerTypes.Expects(pending.Spec, type))
            {
                only = pending;
                if (++candidates > 1)
                    break;
            }
        }

        if (candidates == 1 && only!.TryComplete(received))
            return true;
        if (candidates > 1)
        {
            var level = MediatorLog.RateLimited(ref _ambiguousAnswers, out var count);
            MediatorLog.Write(Logger, level, "Dropped a mediator answer ({Answer}) without a thread that more than one waiting request could take; they keep waiting. {Count} so far.", name, count);
            return false;
        }
        LogUnclaimed(name);
        return false;
    }

    /// <summary>
    /// Stop: pending requests fail with <see cref="ObjectDisposedException"/> and live streams end
    /// (their readers observe <see cref="ObjectDisposedException"/>). Idempotent (the container
    /// disposes this instance once per registration it was resolved through).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var pending in _pending.Values)
            pending.TryFail(Disposed());
        // In-flight sends and resolutions see their deadline end; their callers see the disposal.
        GuardedDeadline.CancelQuietly(_shutdown);
        foreach (var stream in _live.Values)
        {
            stream.Complete(Disposed());
            RemoveLive(stream);
        }
    }

    /// <summary>Stop, as <see cref="Dispose"/> does.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // The shutdown token; a disposed channel's is already cancelled.
    private CancellationToken ShutdownToken => _shutdown.Token;

    // Whether an answer to a request sent to a route that is not HTTP can reach this channel (D-4):
    // an inbound consumer is registered, and the transport the facade would send through does not
    // say that what the peer pushes back is discarded (null, cannot tell: allowed).
    private bool CanReceiveOn(Uri endpoint) => ReceivesInbound && Client.ReceivesPushesOn(endpoint) != false;

    // `to` is optional (DIDComm v2.1) and the key that decrypted the answer already names `local`:
    // an answer without one (or with an empty one) is addressed to `local`; one with a `to` that
    // does not name it is not. Single pass, no early exit.
    private static bool IsAddressedTo(IReadOnlyList<string> to, string local)
    {
        var found = to.Count == 0;
        var inspected = Math.Min(to.Count, MaxAddresseesInspected);
        for (var i = 0; i < inspected; i++)
            found |= string.Equals(DidSubject.DidSubjectOf(to[i]), local, StringComparison.Ordinal);
        return found;
    }

    private static bool IsProblemReport(InboundMessageSnapshot answer)
        => MessageTypeUri.TryParse(answer.Type, out var type)
           && MediatorAnswerTypes.TryClassify(type!, out _, out var kind)
           && kind == MediatorAnswerKind.ProblemReport;

    // Off the receive path, on the caller's continuation. A body that does not parse has no code.
    private static string? ReadProblemCode(InboundMessageSnapshot answer)
    {
        try
        {
            return Protocols.ProblemReport.ProblemReport.ReadCode(answer.DeserializeMessage());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // Why a request ended without an answer, from the caller's view: the channel went away, the
    // caller cancelled (which wins over a deadline that fired at the same time), or the deadline.
    private Exception Unanswered(PendingRequest pending, GuardedDeadline deadline, CancellationToken ct)
    {
        if (IsDisposed)
            return Disposed();
        if (ct.IsCancellationRequested)
            return new OperationCanceledException(ct);
        if (deadline.TimedOut)
        {
            return new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"Mediator request '{pending.Id}' ({pending.Spec.Name}) was not answered within {Options.RequestTimeout}; the mediator may still have acted on it, so retry it (every request these clients send is safe to repeat)."));
        }
        return Disposed();
    }

    private static ObjectDisposedException Disposed()
        => new(nameof(MediatorClientChannel), "The mediator client's container was disposed.");

    private void RemoveLive(LiveRegistration registration)
    {
        if (_live.TryRemove(new KeyValuePair<string, LiveRegistration>(registration.Key, registration)))
        {
            Interlocked.Decrement(ref _liveCount);
            Interlocked.Decrement(ref _active);
        }
    }

    // Counts one look into the pending map or the live registry; always true.
    private bool Looked()
    {
        Interlocked.Increment(ref _lookups);
        return true;
    }

    private long ElapsedMs(PendingRequest pending) => (long)Time.GetElapsedTime(pending.StartedAt).TotalMilliseconds;

    // A mediator (or anyone the identity rule admits) can repeat every line below, so each is
    // rate-limited. The answer is named by its constant, never by the wire type or id.

    // Not a mediator problem: a late answer, or a message of an answer type meant for another part
    // of this agent (an application's problem report, a disclose the Discover Features client
    // takes). Debug for the first and every 1000th, Trace otherwise.
    private void LogUnclaimed(string answer)
    {
        var level = QuietlyRateLimited(ref _unclaimedAnswers, out var count);
        MediatorLog.Write(Logger, level, "Ignored a message of a mediator answer type ({Answer}) that no waiting request takes: late, or not an answer to this client. {Count} so far.", answer, count);
    }

    // A mediator problem: the answer names (or came back in the response to) a waiting request of
    // ours from its pair, but does not fit it. Warning for the first and every 1000th.
    private void LogMismatched(string answer, PendingRequest request)
    {
        var level = MediatorLog.RateLimited(ref _mismatchedAnswers, out var count);
        MediatorLog.Write(Logger, level, "Dropped a mediator answer ({Answer}) to request {RequestId} ({Request}) that does not fit it: a type it does not expect, or not from its mediator to its DID. {Count} so far.",
            answer, request.Id, request.Spec.Name, count);
    }

    // A mediator problem only in so far as it pushes faster than the stream reads: Warning.
    private void LogDroppedPush(string reason)
    {
        var level = MediatorLog.RateLimited(ref _droppedPushes, out var count);
        MediatorLog.Write(Logger, level, "Dropped a live delivery push: {Reason}. It was not acknowledged, so the mediator delivers it again. {Count} so far.", reason, count);
    }

    // A push that arrives as a stream ends, or after: expected, quiet.
    private void LogStrayPush(string reason)
    {
        var level = QuietlyRateLimited(ref _strayPushes, out var count);
        MediatorLog.Write(Logger, level, "Dropped a live delivery push: {Reason}. It was not acknowledged, so the mediator delivers it again. {Count} so far.", reason, count);
    }

    // MediatorLog.RateLimited one level down: Debug for the first and every 1000th, Trace otherwise.
    private static LogLevel QuietlyRateLimited(ref long counter, out long count)
        => MediatorLog.RateLimited(ref counter, out count) == LogLevel.Warning ? LogLevel.Debug : LogLevel.Trace;
}
