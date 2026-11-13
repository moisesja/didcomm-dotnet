using System.Runtime.CompilerServices;
using System.Text.Json;
using DidComm.Diagnostics;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.AspNetCore;

/// <summary>
/// What <see cref="InboundAdmission.Evaluate"/> decided for one unpacked inbound (PRD §8a).
/// </summary>
/// <param name="Admitted"><c>false</c> when FR-RR-08 refused the message: the endpoint dispatches nothing, opens no session and answers its normal acknowledgement (HTTP 202, nothing on a WebSocket).</param>
/// <param name="Header">The parsed <c>return_route</c> header; <see cref="ReturnRouteHeader.None"/> when absent or malformed.</param>
/// <param name="HeaderPresent"><c>true</c> when the message carries a <c>return_route</c> member at all (well-formed or not) — the WebSocket session changes mode only for such a message (FR-RR-05).</param>
/// <param name="SessionEligible"><c>true</c> when the inbound may open or use a session: identity-eligible (see <paramref name="PeerDid"/>), mode <see cref="ReturnRouteMode.All"/> and admitted.</param>
/// <param name="PeerDid">The DID subject of the authcrypt sender key id when the inbound is identity-eligible (encrypted and authenticated, key ids are DIDs, not a <c>forward</c>), whatever its mode; otherwise <c>null</c>.</param>
/// <param name="LocalDid">The DID subject of the decrypting key id, set together with <paramref name="PeerDid"/>.</param>
/// <param name="Replay">The replay guard's answer when the message was in FR-RR-08 scope and reached the guard; <c>null</c> when out of scope or refused before the guard.</param>
internal readonly record struct InboundAdmissionResult(
    bool Admitted,
    ReturnRouteHeader Header,
    bool HeaderPresent,
    bool SessionEligible,
    string? PeerDid,
    string? LocalDid,
    ReplayAdmission? Replay);

/// <summary>
/// The one return-route admission pipeline the registry-aware endpoints and the session-aware
/// callback run after a successful unpack and before dispatch (PRD §8a, FR-RR-01/03/08): read the
/// header, decide session eligibility, and run FR-RR-08 replay admission for the in-scope
/// messages. A message that is not admitted gets no session and no dispatch.
/// </summary>
/// <remarks>
/// Log lines carry the message id and enum / exception type names only — never a DID, key id,
/// thread id or message body — and no logger fault can change the decision.
/// </remarks>
internal static class InboundAdmission
{
    /// <summary>
    /// Evaluate <paramref name="unpacked"/>. Never throws: a fault in the replay guard (host code
    /// when the host registered its own) fails closed as "not admitted".
    /// </summary>
    /// <param name="unpacked">The verified unpack result of the inbound.</param>
    /// <param name="options">The facade options; its clock (<c>DidCommOptions.Now()</c>) is read once here.</param>
    /// <param name="replayGuard">The FR-RR-08 guard.</param>
    /// <param name="endpoint">The per-endpoint state holding the log rate limiters.</param>
    /// <param name="logger">Optional logger.</param>
    internal static InboundAdmissionResult Evaluate(
        UnpackResult unpacked,
        DidCommOptions options,
        IInboundReplayGuard replayGuard,
        InboundAdmissionEndpoint endpoint,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(unpacked);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(replayGuard);
        ArgumentNullException.ThrowIfNull(endpoint);

        var message = unpacked.Message;
        var messageId = message.Id;
        var headerPresent = message.AdditionalHeaders?.ContainsKey(ReturnRouteHeader.HeaderName) == true;

        // FR-RR-01: a malformed header is `none` for this message and is logged — by id only, never
        // the value, rate-limited because any sender can produce one — and it never fails the message.
        if (!ReturnRouteHeader.TryRead(message, out var header))
            endpoint.LogMalformedHeader(logger, messageId);

        // FR-RR-03: the identity half. The peer is the authcrypt sender's DID subject; a message that
        // is not encrypted+authenticated, or is a forward (FR-RR-02), never binds a session.
        string? peerDid = null;
        string? localDid = null;
        if (ReturnRouteEligibility.TryGetPeer(unpacked, out var peer, out var local, out var why))
        {
            peerDid = peer;
            localDid = local;
        }
        else if (header.Mode == ReturnRouteMode.All)
        {
            SafeLog.Write(logger, LogLevel.Debug, "Inbound {MessageId}: return_route all cannot open a session ({Reason}, FR-RR-03).", LogValues.Id(messageId), why.ToString());
        }
        var sessionEligible = peerDid is not null && header.Mode == ReturnRouteMode.All;

        // FR-RR-08 (the one rule, shared with the client's inbound consumer, InboundReplayGate):
        // scope is (a) an authcrypted message with return_route other than none, or (b) a
        // mediator-service message type; a forward is exempt. "Authcrypted" is the FR-RR-03 sense: an
        // authcrypt layer (a sender key id). UnpackResult.Authenticated alone is not enough — a
        // verified JWS sets it too — and a signed-only or anoncrypt(signed) message, like plaintext and
        // anoncrypt, can neither open a session nor mutate mediator state: such in-scope messages pass
        // through and their handlers drop them (FR-MED-05 / FR-PICK-01). Admission is keyed on the
        // authcrypt sender key id's subject, never on the plaintext `from`; a sender key id that is not
        // a DID (or an empty id) fails closed (L-034), and so does a throwing guard.
        var gate = InboundReplayGate.Run(unpacked, header, replayGuard, options);
        switch (gate.Outcome)
        {
            case ReplayGateOutcome.OutOfScope:
                return new InboundAdmissionResult(true, header, headerPresent, sessionEligible, peerDid, localDid, null);
            case ReplayGateOutcome.Admitted:
                return new InboundAdmissionResult(true, header, headerPresent, sessionEligible, peerDid, localDid, gate.Replay);
            case ReplayGateOutcome.NoSenderSubject:
                endpoint.LogRefusal(logger, messageId, InboundAdmissionEndpoint.NoSenderSubjectSlot, "NoSenderSubject");
                return new InboundAdmissionResult(false, header, headerPresent, false, peerDid, localDid, null);
            case ReplayGateOutcome.GuardFault:
                endpoint.LogRefusal(logger, messageId, InboundAdmissionEndpoint.GuardFaultSlot, "GuardFault:" + gate.GuardFaultType);
                return new InboundAdmissionResult(false, header, headerPresent, false, peerDid, localDid, null);
            default:
                var replay = gate.Replay.GetValueOrDefault();
                endpoint.LogRefusal(logger, messageId, (int)replay, replay.ToString());
                return new InboundAdmissionResult(false, header, headerPresent, false, peerDid, localDid, gate.Replay);
        }
    }

    /// <summary>
    /// Register <paramref name="candidate"/> and close whatever the per-peer bound evicted. Returns
    /// <c>true</c> when the session is now registered; <c>false</c> when the registry refused it
    /// (total bound) or threw — the inbound is then dispatched without a session (FR-RR-03).
    /// </summary>
    /// <param name="registry">The session registry.</param>
    /// <param name="candidate">The new session.</param>
    /// <param name="messageId">The id of the inbound that opened it, for the log.</param>
    /// <param name="logger">Optional logger.</param>
    internal static bool TryOpen(IReturnRouteSessionRegistry registry, IReturnRouteSession candidate, string messageId, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(candidate);

        SessionRegistration registration;
        try
        {
            registration = registry.Register(candidate);
        }
        catch (Exception ex)
        {
            SafeLog.Write(logger, LogLevel.Warning, "Inbound {MessageId}: return-route session not opened: the registry threw {ExceptionType}; dispatched without a session.", LogValues.Id(messageId), ex.GetType().Name);
            return false;
        }

        switch (registration.Outcome)
        {
            case SessionRegistrationOutcome.Registered:
                CloseEvicted(registration.Evicted);
                return true;
            case SessionRegistrationOutcome.AlreadyRegistered:
                return true;
            default:
                SafeLog.Write(logger, LogLevel.Information, "Inbound {MessageId}: return-route session refused ({Outcome}); dispatched without a session (FR-RR-03).", LogValues.Id(messageId), registration.Outcome.ToString());
                return false;
        }
    }

    /// <summary>
    /// Close the transport of a session the registry evicted (FR-RR-03: the peer observes it). Only
    /// this assembly's WebSocket sessions have a transport to close; a session some other host code
    /// registered is that code's to close.
    /// </summary>
    /// <param name="evicted">The evicted session, or <c>null</c>.</param>
    internal static void CloseEvicted(IReturnRouteSession? evicted)
    {
        if (evicted is WebSocketReturnRouteSession webSocket)
            webSocket.RequestClose(SessionCloseReason.Evicted);
    }
}

/// <summary>
/// Per-mapped-endpoint state of the return-route pipeline: the drop-log rate limiters (JC-22), and
/// the application's shared defaults for a host that registered no registry or replay guard
/// (JC-17, <see cref="InboundAdmissionDefaults"/>). One instance is created by each <c>Map*</c>
/// call, so one endpoint (or test host) can never suppress another's first Warning.
/// </summary>
internal sealed class InboundAdmissionEndpoint
{
    /// <summary>Drop-counter slot for an authcrypted in-scope message whose sender key id has no DID subject (or whose id is empty).</summary>
    internal const int NoSenderSubjectSlot = 7;

    /// <summary>Drop-counter slot for a replay guard that threw.</summary>
    internal const int GuardFaultSlot = 8;

    private const long WarnEvery = 1000;

    private readonly long[] _drops = new long[9];
    private readonly InboundAdmissionDefaults _defaults;
    private long _malformedHeaders;

    /// <summary>Create the state of one endpoint mapped on the application whose root services are <paramref name="applicationServices"/>.</summary>
    /// <param name="applicationServices">The application's root provider (<c>IEndpointRouteBuilder.ServiceProvider</c>); it keys the defaults every endpoint of the application shares.</param>
    internal InboundAdmissionEndpoint(IServiceProvider applicationServices)
    {
        ArgumentNullException.ThrowIfNull(applicationServices);
        _defaults = InboundAdmissionDefaults.For(applicationServices);
    }

    /// <summary>
    /// The replay guard for this request: the host's registered <see cref="IInboundReplayGuard"/>,
    /// else the application's shared default (built once, logged once at Information).
    /// </summary>
    /// <param name="services">The request services.</param>
    /// <param name="logger">Optional logger.</param>
    internal IInboundReplayGuard ResolveGuard(IServiceProvider services, ILogger? logger)
        => services.GetService<IInboundReplayGuard>() ?? _defaults.Guard(services, logger);

    /// <summary>
    /// The session registry for this connection: the host's registered
    /// <see cref="IReturnRouteSessionRegistry"/>, else the application's shared default (built once,
    /// logged once at Information).
    /// </summary>
    /// <param name="services">The request services.</param>
    /// <param name="logger">Optional logger.</param>
    internal IReturnRouteSessionRegistry ResolveRegistry(IServiceProvider services, ILogger? logger)
        => services.GetService<IReturnRouteSessionRegistry>() ?? _defaults.Registry(services, logger);

    /// <summary>
    /// Log one malformed <c>return_route</c> header (FR-RR-01): the first and every 1000th at Warning
    /// (with the running count), every other one at Debug — any sender can produce one, authenticated
    /// or not, so it is rate-limited like the replay drops. The line carries the message id only.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="messageId">The message's id.</param>
    internal void LogMalformedHeader(ILogger? logger, string messageId)
    {
        var n = Interlocked.Increment(ref _malformedHeaders);
        if (n == 1 || n % WarnEvery == 0)
            SafeLog.Write(logger, LogLevel.Warning, "Inbound {MessageId}: malformed return_route header treated as none (FR-RR-01); {Count} such headers on this endpoint.", LogValues.Id(messageId), n);
        else
            SafeLog.Write(logger, LogLevel.Debug, "Inbound {MessageId}: malformed return_route header treated as none (FR-RR-01).", LogValues.Id(messageId));
    }

    /// <summary>The session options (validated): the host's configured <see cref="ReturnRouteSessionOptions"/>, else the defaults.</summary>
    /// <param name="services">The request services.</param>
    internal static ReturnRouteSessionOptions ResolveSessionOptions(IServiceProvider services)
    {
        var options = services.GetService<IOptions<ReturnRouteSessionOptions>>()?.Value ?? new ReturnRouteSessionOptions();
        options.Validate();
        return options;
    }

    /// <summary>
    /// Log one admission refusal: the first and every 1000th per outcome at Warning (with the
    /// running count), every other one at Debug. Each line carries the message id and the outcome
    /// name only.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="messageId">The refused message's id.</param>
    /// <param name="slot">The counter slot (the <see cref="ReplayAdmission"/> value, or one of the extra slots).</param>
    /// <param name="outcome">The outcome name written to the log.</param>
    internal void LogRefusal(ILogger? logger, string messageId, int slot, string outcome)
    {
        var index = (uint)slot < (uint)_drops.Length ? slot : GuardFaultSlot;
        var n = Interlocked.Increment(ref _drops[index]);
        if (n == 1 || n % WarnEvery == 0)
            SafeLog.Write(logger, LogLevel.Warning, "Inbound {MessageId} dropped: replay admission {Admission} (FR-RR-08); {Count} such drops on this endpoint.", LogValues.Id(messageId), outcome, n);
        else
            SafeLog.Write(logger, LogLevel.Debug, "Inbound {MessageId} dropped: replay admission {Admission} (FR-RR-08).", LogValues.Id(messageId), outcome);
    }
}

/// <summary>
/// The default session registry and replay guard of one application, used by every registry-aware
/// endpoint it maps when the host registered none (JC-17): built lazily on first use, logged once
/// at Information. Keyed by the application's root service provider, so all endpoints of a host
/// share one replay cache — a request admitted over HTTP is a duplicate on the WebSocket endpoint
/// and vice versa (FR-RR-08) — and one session registry (its bounds hold per application), while
/// separate hosts in one process never share state. The table holds its keys weakly. The default
/// registry is never disposed: nothing can subscribe to it (it is not in the container), so its
/// notifier never has anything to deliver and stays a parked asynchronous loop that holds no
/// thread; it becomes unreachable together with the registry once the application's provider is
/// gone.
/// </summary>
internal sealed class InboundAdmissionDefaults
{
    private static readonly ConditionalWeakTable<IServiceProvider, InboundAdmissionDefaults> PerApplication = new();

    private readonly object _gate = new();
    private IReturnRouteSessionRegistry? _registry;
    private IInboundReplayGuard? _guard;

    /// <summary>The defaults of the application whose root provider is <paramref name="applicationServices"/>.</summary>
    /// <param name="applicationServices">The application's root service provider.</param>
    internal static InboundAdmissionDefaults For(IServiceProvider applicationServices)
        => PerApplication.GetValue(applicationServices, static _ => new InboundAdmissionDefaults());

    /// <summary>The application's default replay guard, built on first use from the configured <see cref="ReplayProtectionOptions"/> (or the defaults).</summary>
    /// <param name="services">The request services.</param>
    /// <param name="logger">Optional logger.</param>
    internal IInboundReplayGuard Guard(IServiceProvider services, ILogger? logger)
    {
        var existing = Volatile.Read(ref _guard);
        if (existing is not null)
            return existing;
        lock (_gate)
        {
            if (_guard is null)
            {
                var options = services.GetService<IOptions<ReplayProtectionOptions>>()?.Value ?? new ReplayProtectionOptions();
                Volatile.Write(ref _guard, new InMemoryReplayGuard(options, services.GetService<ILogger<InMemoryReplayGuard>>()));
                SafeLog.Write(logger, LogLevel.Information, "No IInboundReplayGuard is registered; this application's return-route endpoints share one in-memory replay guard. Register it through AddDidComm to share it with handlers too.");
            }
            return _guard;
        }
    }

    /// <summary>The application's default session registry, built on first use from the configured <see cref="ReturnRouteSessionOptions"/> (or the defaults).</summary>
    /// <param name="services">The request services.</param>
    /// <param name="logger">Optional logger.</param>
    internal IReturnRouteSessionRegistry Registry(IServiceProvider services, ILogger? logger)
    {
        var existing = Volatile.Read(ref _registry);
        if (existing is not null)
            return existing;
        lock (_gate)
        {
            if (_registry is null)
            {
                Volatile.Write(ref _registry, new InMemoryReturnRouteSessionRegistry(
                    InboundAdmissionEndpoint.ResolveSessionOptions(services),
                    services.GetService<ILogger<InMemoryReturnRouteSessionRegistry>>()));
                SafeLog.Write(logger, LogLevel.Information, "No IReturnRouteSessionRegistry is registered; this application's return-route endpoints share one in-memory session registry. Register it through AddDidComm to share it with handlers too.");
            }
            return _registry;
        }
    }
}

/// <summary>
/// The outbound half shared by both session types: snapshot the caller's message, check the
/// FR-RR-03 addressing rule on the snapshot, and authcrypt it from the session's local DID to its
/// peer (no forward wrapping).
/// </summary>
internal static class SessionOutbound
{
    /// <summary>
    /// Snapshot <paramref name="message"/> (serialize → deserialize, so a caller that keeps
    /// mutating its instance cannot race the check) and check it is from
    /// <paramref name="localDid"/> to exactly <paramref name="peerDid"/>. Returns the snapshot, or
    /// <c>null</c> (logged by refusal or exception type name).
    /// </summary>
    /// <param name="message">The caller's message.</param>
    /// <param name="peerDid">The session peer's DID subject.</param>
    /// <param name="localDid">The session's local DID subject.</param>
    /// <param name="logger">Optional logger.</param>
    internal static Message? TrySnapshotAndCheck(Message message, string peerDid, string localDid, ILogger? logger)
    {
        Message snapshot;
        try
        {
            var json = JsonSerializer.Serialize(message, DidCommJson.Default);
            snapshot = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)
                ?? throw new JsonException("The message deserialized to null.");
        }
        catch (Exception ex)
        {
            SafeLog.Write(logger, LogLevel.Warning, "Return-route message {MessageId} not sent: snapshot failed with {ExceptionType}.", LogValues.Id(message.Id), ex.GetType().Name);
            return null;
        }

        var refusal = ReturnRouteAddressing.Check(snapshot, peerDid, localDid);
        if (refusal != ReturnRouteSendRefusal.None)
        {
            SafeLog.Write(logger, LogLevel.Warning, "Return-route message {MessageId} not sent: {Refusal} (FR-RR-03).", LogValues.Id(snapshot.Id), refusal.ToString());
            return null;
        }
        return snapshot;
    }

    /// <summary>
    /// Authcrypt <paramref name="snapshot"/> from <paramref name="localDid"/> to
    /// <paramref name="peerDid"/>, first stamping <c>created_time</c> on it when it falls under the
    /// FR-RR-09 scope and has none. Returns the packed envelope's UTF-8 bytes, or <c>null</c> when the
    /// pack failed (logged by exception type name). Throws <see cref="OperationCanceledException"/>
    /// only for <paramref name="ct"/>.
    /// </summary>
    /// <param name="client">The facade that packs.</param>
    /// <param name="snapshot">The checked snapshot.</param>
    /// <param name="peerDid">The session peer's DID subject (the only recipient).</param>
    /// <param name="localDid">The session's local DID subject (the authcrypt sender).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async ValueTask<byte[]?> TryPackAsync(
        DidCommClient client, Message snapshot, string peerDid, string localDid, ILogger? logger, CancellationToken ct)
    {
        try
        {
            // FR-RR-09 (JC-17): a reply or push of a mediator-service type, or carrying return_route
            // all/thread, falls under the client's FR-RR-08 admission, which refuses it without
            // created_time. Stamped on the library's own snapshot; a value already set is kept.
            OutgoingCreatedTime.StampIfInScope(snapshot, client.Options.Now());
            var packed = await client.PackEncryptedAsync(snapshot, new PackEncryptedOptions(
                Recipients: new[] { peerDid },
                From: localDid,
                Forward: false), ct).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetBytes(packed.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            SafeLog.Write(logger, LogLevel.Warning, "Return-route message {MessageId} not sent: pack failed with {ExceptionType}.", LogValues.Id(snapshot.Id), ex.GetType().Name);
            return null;
        }
    }
}

/// <summary>Logging that host code cannot turn into a behaviour change: a throwing provider is swallowed.</summary>
internal static class SafeLog
{
    /// <summary>Write one structured log line; never throws.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="level">The level.</param>
    /// <param name="message">The message template.</param>
    /// <param name="args">The template arguments (ids and type / enum names only).</param>
    internal static void Write(ILogger? logger, LogLevel level, string message, params object?[] args)
    {
        if (logger is null)
            return;
        try
        {
            logger.Log(level, message, args);
        }
        catch
        {
            // Logging providers are host code; they must not change admission, dispatch or the response.
        }
    }
}
