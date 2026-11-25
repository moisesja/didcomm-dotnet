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
/// duplicate filter, the version caches and the unpacker of delivered messages. It is the
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
/// rule: the key that decrypted it names the DID): every comparison runs, combined without
/// short-circuit, and a failure returns silently. The <c>to</c> header is advisory, as DIDComm v2.1
/// says (a recipient SHOULD check it, MUST NOT refuse a message over it, SHOULD warn): one that does
/// not name the decrypting DID is logged (rate-limited, no DID) and the answer is matched as any
/// other; an absent one is the normal case (it is optional). Only then is its thread (<c>pthid</c>, else <c>thid</c>, for a problem report) looked
/// up, ignoring letter case as DIDComm v2.1 threading compares ids — never when it is longer than
/// <see cref="MaxThreadIdLength"/> — and the request completes
/// only when its (mediator, local DID) pair and expected types agree. A message without a thread
/// completes no request, on any path: DIDComm v2.1 threading makes a reply carry the <c>thid</c>
/// of the message it answers ("Messages that do not share the same <c>thid</c> MUST NOT be
/// considered a part of the same thread"), and a message without one is its own thread. So it is
/// dropped and logged, even when it came in the HTTP response to our own request, and even when
/// only one request waits (it could be the late answer to a request given up on). Arriving on its
/// own (over a WebSocket), a thread-less <c>delivery</c> is a live push and reaches only the live
/// stream of its pair, never a request; in an HTTP response it reaches nothing (an HTTP exchange is
/// never live). A message that does not
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
/// <strong>What a send or an endpoint lookup throws.</strong> It is judged by whether the call is
/// being ended when the channel observes it, never by the exception's type (I-22). A call is being ended
/// when its deadline's token is cancelled (the deadline passed, the caller cancelled or the channel
/// was disposed), when the caller's token is cancelled, or when the channel is disposed. The last
/// two are read as well: each cancels the deadline's token through a callback of its own, and a
/// callback registered later on the same source runs before that one. The call then ends as the
/// disposal, the caller's cancellation or the timeout does, the first of them that is true in that
/// order, whatever was thrown: host code may report a cancellation as a failure of its own (a
/// transport that reports a closed connection, a resolver that maps every error to "not found"),
/// and may do so from a callback that runs before the channel's. What was thrown is then named in
/// one Debug line, by its type only and never its text. An answer that had already won still holds.
/// What is thrown while the call is not being ended is the call's own failure and is passed on as
/// it is, with one exception: when the answer to a request had already won before its send failed,
/// the answer is returned (and the failure is named in a Debug line, by its type only). The same
/// terminal compare-and-swap decides between the two, so an answer that arrives after the send
/// failed is not taken (I-25).
/// </para>
/// <para>
/// <strong>The token host code is handed.</strong> The channel reads the token it was given, its own
/// deadline's and its own disposal, and nothing else. Host code (a transport, a DID resolver) should
/// watch the token it is handed. Host code that watches another token instead (one of two that a
/// caller combined into the call's token, for example) and reports that token's cancellation as a
/// failure of its own may do so before any token the channel can read is cancelled: the call then
/// ends with that failure, not as a cancellation (a documented residual, R-16).
/// </para>
/// <para>
/// <strong>Log levels.</strong> A drop that points at a mediator problem is a Warning (the first and
/// every 1000th, Debug otherwise): an answer to a waiting request that does not fit it, an answer
/// without a thread (anything thread-less in an HTTP response; anything but a live push or a problem
/// report otherwise), a live push its stream's
/// full buffer refuses, an answer whose <c>to</c> does not name the DID that decrypted it (not a drop: it
/// is still matched). A message of an answer type
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

    // Keyed by request id; an answer's thread matches it ignoring letter case (DIDComm v2.1 threading).
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.OrdinalIgnoreCase);
    // Keyed by the (mediator, local DID) pair, compared exactly.
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
    private long _unthreadedAnswers;
    private long _notAddressedAnswers;
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
        Versions = new NegotiatedVersionCache<CoordinateMediationVersion>(time);
        PickupVersions = new NegotiatedVersionCache<MessagePickupVersion>(time);
        // The shutdown reaches deliveries being opened (ADV3-7).
        Unpacker = new DeliveredMessageUnpacker(unpack ?? client.UnpackDeliveredAsync, Options.UnpackTimeout, time, _shutdown.Token);
        _send = send ?? client.SendAsync;
        _resolveEndpoint = resolveEndpoint ?? client.ResolveDirectEndpointAsync;
    }

    /// <summary>The facade requests are sent through.</summary>
    internal DidCommClient Client { get; }

    /// <summary>Whether answers can reach this channel through the dispatcher (see the constructor).</summary>
    internal bool ReceivesInbound { get; }

    /// <summary>The channel's logger (category <c>DidComm.Mediator.Client.Internal.MediatorClientChannel</c>), or <c>null</c>.</summary>
    internal ILogger? Logger { get; }

    /// <summary>
    /// The unpacker behind <see cref="UnpackDeliveredBatchAsync"/>, one per channel (container) and
    /// linked to this channel's shutdown: one budget per delivery, up to sixteen messages at a time,
    /// each until it opens, fails or the budget ends (I-23); host unpack calls reserved before they
    /// start (256 per container, and per mediator and mailbox 16 per sender and 16 for messages
    /// without a named sender); and the memory of messages that did not open, held back until their
    /// pause ends.
    /// </summary>
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
    public NegotiatedVersionCache<CoordinateMediationVersion> Versions { get; }

    /// <inheritdoc/>
    public NegotiatedVersionCache<MessagePickupVersion> PickupVersions { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// What the lookup throws while the call is being ended (its deadline's token or the caller's is
    /// cancelled, or the channel is disposed) ends the call as the disposal, the caller's
    /// cancellation or the timeout does, whatever its type; otherwise it is passed on as it is (the
    /// class remarks, "What a send or an endpoint lookup throws").
    /// </remarks>
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
            catch (Exception ex) when (BeingEnded(deadline, ct))
            {
                // The call was being ended when this was observed: the disposal, the caller or the
                // deadline ended the lookup, and that is what the caller sees. That state decides,
                // not the exception's type: a lookup may report a cancellation as a failure of its
                // own, from a callback that runs before this method's. What was observed is kept in
                // the log, by its type only.
                MediatorLog.Write(Logger, LogLevel.Debug, "Finding the mediator's endpoint: the lookup step ended with {ExceptionType} while the call was being ended.",
                    ex.GetType().Name);
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
    /// <remarks>
    /// What the send throws while the call is being ended (the request's deadline's token or the
    /// caller's is cancelled, or the channel is disposed) ends the call as the disposal, the caller's
    /// cancellation or the timeout does, whatever its type; otherwise it is passed on as it is. In
    /// both cases an answer that had already won is returned instead (the class remarks, "What a
    /// send or an endpoint lookup throws").
    /// </remarks>
    public Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
        => ExchangeCheckedAsync(route, request, spec, mediatorDid, localDid, answerOptional: false, answerWait: null, ct)!; // never null without answerOptional

    /// <inheritdoc/>
    /// <remarks>What the send throws is judged as for <see cref="ExchangeAsync"/>.</remarks>
    public Task<InboundMessageSnapshot?> ExchangeAllowingNoAnswerAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, TimeSpan? answerWait, CancellationToken ct)
        => ExchangeCheckedAsync(route, request, spec, mediatorDid, localDid, answerOptional: true, answerWait, ct);

    // One request (§4.1): the checks, the bound and the registration around ExchangeCoreAsync.
    private async Task<InboundMessageSnapshot?> ExchangeCheckedAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, bool answerOptional, TimeSpan? answerWait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);
        if (answerWait is { } positive)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(positive, TimeSpan.Zero, nameof(answerWait));
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

            var answer = await ExchangeCoreAsync(route, request, pending, mediatorDid, localDid, answerOptional, answerWait, ct).ConfigureAwait(false);
            if (answer is null)
            {
                MediatorLog.Write(Logger, LogLevel.Debug, "Mediator request {RequestId} ({Request}) got no answer after {ElapsedMs} ms, which it allows.",
                    pending.Id, spec.Name, ElapsedMs(pending));
                return null;
            }
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

    // Steps 3–9 of §4.1, for a registered request. Returns null only with answerOptional: over HTTP
    // when the response carried no answer, and on any other route, with answerWait, when none came
    // within answerWait after the send completed.
    private async Task<InboundMessageSnapshot?> ExchangeCoreAsync(MediatorRoute route, Message request, PendingRequest pending, string mediatorDid, string localDid, bool answerOptional, TimeSpan? answerWait, CancellationToken ct)
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
        // A request the mediator may leave unanswered, on a route that is not HTTP: once sent, no
        // answer is an outcome (see the window below).
        var optionalAfterSend = !route.IsHttp && answerOptional && answerWait is not null;
        Task<SendResult>? sending = null;
        try
        {
            sending = _send(request, options, deadline.Token);
            sent = await sending.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (BeingEnded(deadline, ct))
        {
            // The call was being ended when this was observed: the disposal, the caller or the
            // deadline ended the send, and Unanswered says which. That state decides, not the
            // exception's type: a transport may report a cancellation as a failure of its own, from
            // a callback that runs before this method's. What was observed is kept in the log, by
            // its type only. What a send throws while the call is not being ended is not caught
            // here: it is the request's own failure, and the next clause decides it.
            MediatorLog.Write(Logger, LogLevel.Debug, "Mediator request {RequestId} ({Request}): the send step ended with {ExceptionType} while the call was being ended.",
                pending.Id, pending.Spec.Name, ex.GetType().Name);
            if (sending is not null)
                GuardedDeadline.ObserveAbandoned(sending);
            // An answer that already won (over a WebSocket, before the send returned) holds. So does
            // a send that completed before the deadline but was resumed after it (OD6-ADV-2): it was
            // sent, and the window below decides.
            if (!pending.IsCompleted && !(optionalAfterSend && sending is { IsCompletedSuccessfully: true }))
                throw Unanswered(pending, deadline, ct);
        }
        catch (Exception ex)
        {
            // Nothing was ending the call when this was observed: the send failed on its own. The
            // terminal CAS decides between that failure and an answer. The failure is the request's
            // own and is passed on as it is, and an answer that arrives after it finds the request
            // decided. An answer that had already won (over a WebSocket, before the send returned)
            // holds, as it does above: the call goes on to return it.
            if (pending.TryFail(ex) || !pending.IsCompleted)
                throw;
            MediatorLog.Write(Logger, LogLevel.Debug, "Mediator request {RequestId} ({Request}): the send step ended with {ExceptionType} after the answer had arrived; the answer holds.",
                pending.Id, pending.Spec.Name, ex.GetType().Name);
        }

        // The HTTP response's message (§4.2): it was identity-checked by SendAsync and goes through
        // the matcher like any other answer. It answers this request only when its thid names it.
        if (sent?.Returned is { } returned && InboundMessageSnapshot.TryGetFor(returned.Message, out var snapshot))
            Offer(snapshot, inHttpResponse: true);

        // Over HTTP the response is the only channel for the answer (FR-RR-04): waiting is pointless.
        // For a request the mediator may leave unanswered, no answer is an outcome, not a failure; the
        // same terminal CAS decides it, so an answer that won the race still holds.
        if (route.IsHttp && answerOptional && pending.TryAbandon())
            return null;
        if (route.IsHttp && pending.IsWaiting)
        {
            var noAnswer = new TimeoutException(
                $"Mediator request '{pending.Id}' ({pending.Spec.Name}) got no answer in the HTTP response; the mediator may still have acted on it, so retry it (every request these clients send is safe to repeat).");
            if (pending.TryFail(noAnswer))
                throw noAnswer;
        }

        // On any other route (a WebSocket) silence cannot be told from a slow answer. For a request
        // the mediator may leave unanswered, the answer is waited for answerWait once the request is
        // sent (the send completed above, or an answer already won), and never past the request's
        // deadline. No answer by the end of either is an outcome, not a failure: the request was
        // sent. The terminal CAS decides it, so an answer that won the race still holds. A send that
        // did not complete gets here only when an answer had already won, which is returned below;
        // otherwise it ended above, with the deadline, as a TimeoutException (or with its transport
        // failure). The caller's cancellation and the disposal are decided below, as for any request.
        if (optionalAfterSend && !pending.IsWaiting && !pending.IsCompleted
            && deadline.TimedOut && !ct.IsCancellationRequested && !IsDisposed)
        {
            // The deadline gave up between the send and here (OD6-ADV-2): it was sent, so no answer.
            return null;
        }
        if (optionalAfterSend && answerWait is { } wait && pending.IsWaiting)
        {
            using var window = new GuardedDeadline(Time, wait, deadline.Token);
            try
            {
                await pending.Completion.Task.WaitAsync(window.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when ((window.TimedOut || deadline.TimedOut) && !ct.IsCancellationRequested && !IsDisposed)
            {
                // The window: give up here. The deadline: its own registration already gave up, so
                // only an answer that won first (Completed) is left to take.
                if (pending.TryAbandon() || !pending.IsCompleted)
                    return null;
            }
            catch (OperationCanceledException)
            {
                // The caller or the shutdown: decided below, as for any request.
            }
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
    /// <remarks>
    /// What the send throws while the call is being ended (its deadline's token or the caller's is
    /// cancelled, or the channel is disposed) ends the call as the disposal, the caller's
    /// cancellation or the timeout does, whatever its type; otherwise it is passed on as it is (the
    /// class remarks, "What a send or an endpoint lookup throws").
    /// </remarks>
    public async Task SendOneWayAsync(MediatorRoute route, Message request, string name, string mediatorDid, string localDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (string.IsNullOrEmpty(request.Id) || request.Id.Length > MaxThreadIdLength)
            throw new ArgumentException($"The request id must be 1 to {MaxThreadIdLength} characters (a UUID this agent mints).", nameof(request));
        _ = DidSubject.DidSubjectOf(mediatorDid) ?? throw new ArgumentException("The mediator DID is not a DID.", nameof(mediatorDid));
        _ = DidSubject.DidSubjectOf(localDid) ?? throw new ArgumentException("The local DID is not a DID.", nameof(localDid));
        ct.ThrowIfCancellationRequested();

        // The same bound as a request that waits for an answer, taken before anything is sent
        // (L-055): a send in flight holds a slot until it completes.
        if (Interlocked.Increment(ref _pendingCount) > Options.MaxPendingRequests)
        {
            Interlocked.Decrement(ref _pendingCount);
            throw new MediatorBusyException(string.Create(CultureInfo.InvariantCulture,
                $"{Options.MaxPendingRequests} mediator requests are already waiting for an answer (MediatorClientOptions.MaxPendingRequests); request '{request.Id}' ({name}) was not sent."));
        }

        var startedAt = Time.GetTimestamp();
        try
        {
            if (IsDisposed)
                throw Disposed();
            using var deadline = new GuardedDeadline(Time, Options.RequestTimeout, ct, ShutdownToken);
            MediatorLog.Write(Logger, LogLevel.Debug, "Mediator message {RequestId} ({Request}) sending; no answer is expected.", MediatorLog.LogId(request.Id), name);
            // No return_route header: omitted, the extension's default is none (return_route
            // main.md L25), and Message Pickup 4.0 removed it from messages-received (L35). Nothing
            // is registered, so nothing can answer it; an HTTP response's message is not looked at.
            var options = new SendOptions(new[] { mediatorDid }, From: localDid, ServiceEndpointOverride: route.Endpoint)
            {
                ReturnRoute = null,
            };
            Task<SendResult>? sending = null;
            try
            {
                sending = _send(request, options, deadline.Token);
                await sending.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (BeingEnded(deadline, ct))
            {
                // The call was being ended when this was observed: the disposal, the caller or the
                // deadline ended the send, and that is what the caller sees. That state decides, not
                // the exception's type: a transport may report a cancellation as a failure of its
                // own, from a callback that runs before this method's. What was observed is kept in
                // the log, by its type only.
                MediatorLog.Write(Logger, LogLevel.Debug, "Mediator message {RequestId} ({Request}): the send step ended with {ExceptionType} while the call was being ended.",
                    MediatorLog.LogId(request.Id), name, ex.GetType().Name);
                if (sending is not null)
                    GuardedDeadline.ObserveAbandoned(sending);
                if (IsDisposed)
                    throw Disposed();
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                if (deadline.TimedOut)
                {
                    throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                        $"Mediator message '{request.Id}' ({name}) was not sent within {Options.RequestTimeout} (MediatorClientOptions.RequestTimeout); the mediator may still have received it, so retry it (every message these clients send is safe to repeat)."));
                }
                throw Disposed();
            }
            MediatorLog.Write(Logger, LogLevel.Debug, "Mediator message {RequestId} ({Request}) sent after {ElapsedMs} ms; no answer is expected.",
                MediatorLog.LogId(request.Id), name, (long)Time.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            MediatorLog.Write(Logger, LogLevel.Debug, "Mediator message {RequestId} ({Request}) was not sent after {ElapsedMs} ms: {ExceptionType}.",
                MediatorLog.LogId(request.Id), name, (long)Time.GetElapsedTime(startedAt).TotalMilliseconds, ex.GetType().Name);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCount);
        }
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
    public async Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(DeliveryScope scope, IReadOnlyList<DeliveredInput> messages, string? requestId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        try
        {
            return await Unpacker.UnpackAllAsync(scope, messages, requestId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            // The disposal cancelled the budget (ADV3-7): the caller sees the disposal, as it would
            // in any other step, also when it cancelled too (Unanswered's order: a disposal seen as
            // the call ends comes first).
            throw Disposed();
        }
    }

    /// <summary>
    /// The matcher's entry point for what the dispatcher receives (design §4.2): fast path when
    /// idle, identity before any lookup, then complete the pending request the snapshot answers or
    /// hand a thid-less delivery to its live stream. Never awaits, never parses a body.
    /// </summary>
    /// <param name="received">The verified snapshot the dispatcher created at unpack.</param>
    /// <returns><c>true</c> when the snapshot completed a pending request or reached a live stream.</returns>
    bool IInboundCorrelator.OnInbound(InboundMessageSnapshot received)
    {
        try
        {
            return Offer(received, inHttpResponse: false);
        }
        catch
        {
            // Never: Offer only compares fields. The dispatcher guards the call too.
            return false;
        }
    }

    /// <summary>
    /// The matcher (design §4.2): offer <paramref name="received"/> to the pending requests and the
    /// live streams. Only its thread names the request it answers, wherever it came from. Completes
    /// at most one request; never awaits, never parses a body, never removes or cancels anything.
    /// </summary>
    /// <param name="received">The snapshot.</param>
    /// <param name="inHttpResponse"><c>true</c> when it came in the HTTP response to one of our
    /// requests (then, without a thread, it reaches nothing); <c>false</c> for a message the
    /// dispatcher received.</param>
    /// <returns><c>true</c> when it completed a pending request or reached a live stream.</returns>
    internal bool Offer(InboundMessageSnapshot received, bool inHttpResponse)
    {
        ArgumentNullException.ThrowIfNull(received);
        // 0. Idle: one volatile read for a host that is also a mediator. (In an HTTP response, the
        //    request that carried it is still registered, so the channel is never idle then.)
        if (Volatile.Read(ref _active) == 0)
            return false;

        // 1. Only mediator answer types are looked at.
        if (!MessageTypeUri.TryParse(received.Type, out var type) || !MediatorAnswerTypes.TryClassify(type!, out var name, out var kind))
            return false;

        // 2. Identity before any lookup (D-7): a verified snapshot (never a hand-built one),
        //    authcrypted by the sender key's DID (the return-route rule: signer agrees, not a
        //    forward), whose `from` is that DID. Every comparison runs; they are combined without
        //    short-circuit. A failure is other traffic: returned silently, not logged. The `to`
        //    header is advisory (DIDComm v2.1: check it, MUST NOT refuse over it, SHOULD warn): one
        //    that does not name the DID that decrypted the answer is logged and the answer goes on
        //    to be matched by its thread and its pair, which the keys decide.
        var eligible = ReturnRouteEligibility.TryGetPeer(received, out var peer, out var local, out _);
        var fromOk = string.Equals(DidSubject.DidSubjectOf(received.From), peer, StringComparison.Ordinal);
        var addressed = IsAddressedTo(received.To, local);
        if (!(received.Verified & eligible & fromOk))
            return false;
        if (!addressed)
            LogNotAddressed(name);

        // 3. The thread: a problem report names the request in pthid, which is REQUIRED (DIDComm v2.1
        //    problems.md L83, "pthid - REQUIRED. The value is the thid of the thread in which the
        //    problem occurred."); one without pthid names no request, whatever its thid says
        //    (#100 ADV3-1). Any other answer names it in thid (owner answer "2", #98).
        var thread = kind == MediatorAnswerKind.ProblemReport ? received.Pthid : received.Thid;
        return !string.IsNullOrEmpty(thread)
            ? OfferThreaded(received, type!, name, kind, thread, peer, local)
            : OfferUnthreaded(received, name, kind, peer, local, inHttpResponse);
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

    // §4.2 rows 3–5: no thread. DIDComm v2.1 threading: a reply continues the thread of the
    // message it answers and MUST carry its `thid`; messages that do not share the `thid` MUST NOT
    // be taken as part of the same thread, and a message without one is its own thread. So nothing
    // here completes a request, on any path.
    private bool OfferUnthreaded(InboundMessageSnapshot received, string name, MediatorAnswerKind kind, string peer, string local, bool inHttpResponse)
    {
        if (inHttpResponse)
        {
            // In the HTTP response to our own request, yet not in its thread: the mediator's
            // mistake (the request gets no answer). Never a live push either: an HTTP exchange is
            // never live. A delivery dropped here was not acknowledged, so it is delivered again.
            LogUnthreaded(name);
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
                // An answer without a thread belongs to no request's thread, so it completes none:
                // not even the only waiting one, which it could be a late answer to an earlier
                // request of (PR #130 review). It is logged and dropped.
                LogUnthreaded(name);
                return false;
        }
    }

    /// <summary>
    /// Stop: pending requests fail with <see cref="ObjectDisposedException"/>, live streams end
    /// (their readers observe <see cref="ObjectDisposedException"/>), and deliveries being opened end
    /// at once with <see cref="ObjectDisposedException"/>, the host code opening them seeing its token
    /// cancelled (ADV3-7). Idempotent (the container disposes this instance once per registration it
    /// was resolved through).
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
    // does not name it is not, which is only warned about (Offer). Single pass, no early exit.
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

    // Whether a call is being ended from outside when what its send or its endpoint lookup threw is
    // observed: its deadline's token is cancelled (the deadline passed, the caller cancelled or the
    // channel was disposed), or the caller's token is, or the channel is disposed. The caller's
    // cancellation and the disposal each cancel the deadline's token, but through a callback of
    // their own, and a callback registered later on the same source runs before it (host code that
    // watches the caller's token, or the host code of a call started later, which may fail this
    // call's as it is cancelled): so they are read too.
    private bool BeingEnded(GuardedDeadline deadline, CancellationToken ct)
        => deadline.Token.IsCancellationRequested || ct.IsCancellationRequested || IsDisposed;

    // Why a request ended without an answer, from the caller's view, read once as it ends: the channel
    // went away, the caller cancelled, or the deadline. The caller's cancellation wins when it was
    // requested before the call ended without an answer, even if the deadline fired first; one
    // requested after that changes nothing.
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

    // An answer without a thread (DIDComm v2.1 threading: it belongs to no request's thread), in
    // the HTTP response to our own request or on its own. A mediator problem: Warning the first and
    // every 1000th time. Named by its constant, never a DID or wire value.
    private void LogUnthreaded(string answer)
    {
        var level = MediatorLog.RateLimited(ref _unthreadedAnswers, out var count);
        MediatorLog.Write(Logger, level, "Dropped a mediator answer ({Answer}) without a thread: an answer without thid belongs to no request's thread (DIDComm v2.1 threading), so it completes none, even in the HTTP response to its own request. {Count} so far.", answer, count);
    }

    // The `to` header of an answer whose identity passed does not name the DID that decrypted it
    // (DIDComm v2.1: SHOULD warn, MUST NOT refuse). Named by its constant, never a DID or wire value.
    private void LogNotAddressed(string answer)
    {
        var level = MediatorLog.RateLimited(ref _notAddressedAnswers, out var count);
        MediatorLog.Write(Logger, level, "A mediator answer ({Answer}) has a 'to' header that does not name the DID that decrypted it; it is still matched by its thread and its keys (the 'to' header is advisory in DIDComm v2.1). {Count} so far.", answer, count);
    }

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
