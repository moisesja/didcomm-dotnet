using System.Text;
using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Extensions.DependencyInjection;

/// <summary>
/// The built-in <see cref="IInboundFrameConsumer"/> registered by
/// <see cref="DidCommBuilder.UseInboundDispatch"/> (PRD §8a, FR-RR-07): for each message a peer pushed
/// on a return-route session socket it unpacks the frame, requires authcrypt from the session's peer
/// to its local DID, applies FR-RR-08 replay admission with the receive side's rule, and dispatches
/// the message to the protocol handlers with no session. A handler's reply is never sent (JC-9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Order and trust.</strong> Nothing from the wire is logged before the identity rule
/// passed: an unpack failure or an identity refusal is logged by its exception type or refusal
/// name only, without the message id (JC-12). Anoncrypt, signed-only, <c>anoncrypt(sign)</c>,
/// plaintext, Routing 2.0 <c>forward</c>, another sender and an envelope encrypted to another local
/// DID are all dropped there, before admission, so they consume no replay budget. Replay admission
/// runs once, here; handlers never re-check it (a second admission would refuse the message itself).
/// </para>
/// <para>
/// <strong>No reflector (L-036/L-037).</strong> The dispatcher may return a reply; it is logged at
/// Debug and dropped. Replies to pushed messages are the application's explicit sends.
/// </para>
/// <para>
/// <strong>Dependency walk (L-017).</strong> The constructor takes only the provider and a logger.
/// <see cref="DidCommClient"/>, <see cref="ProtocolDispatcher"/>, <see cref="IInboundReplayGuard"/>
/// and <see cref="DidCommOptions"/> are resolved on every call, because the WebSocket transport —
/// which takes this consumer in its constructor — is itself a dependency of
/// <see cref="DidCommClient"/>. A service that cannot be resolved (for example after the container
/// was disposed) drops the message, logged by exception type.
/// </para>
/// <para>
/// <strong>Logging.</strong> Message ids (after the identity rule) and enum / exception type names
/// only; never a DID, key id, thread id, endpoint, body or exception object. Every drop line can be
/// produced by a peer, so each kind is rate-limited: the first and every 1000th at Warning (with the
/// running count), every other one at Debug. A throwing logging provider changes nothing.
/// </para>
/// </remarks>
internal sealed class InboundDispatchConsumer : IInboundFrameConsumer
{
    private const long WarnEvery = 1000;

    // Drop-counter slots, one per kind of drop line.
    private const int ResolveSlot = 0;
    private const int UnpackSlot = 1;
    private const int IdentitySlot = 2;
    private const int MalformedHeaderSlot = 3;
    private const int AdmissionSlot = 4;
    private const int HandlerSlot = 5;

    private readonly IServiceProvider _services;
    private readonly ILogger? _logger;
    private readonly long[] _counts = new long[6];

    /// <summary>Create the consumer (L-017: the provider and a logger only).</summary>
    /// <param name="services">The application's root provider, used to resolve the facade, the dispatcher, the replay guard and the options on every call.</param>
    /// <param name="logger">Optional logger.</param>
    internal InboundDispatchConsumer(IServiceProvider services, ILogger<InboundDispatchConsumer>? logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);

        DidCommClient client;
        ProtocolDispatcher dispatcher;
        IInboundReplayGuard guard;
        DidCommOptions options;
        try
        {
            client = _services.GetRequiredService<DidCommClient>();
            dispatcher = _services.GetRequiredService<ProtocolDispatcher>();
            guard = _services.GetRequiredService<IInboundReplayGuard>();
            options = _services.GetRequiredService<IOptions<DidCommOptions>>().Value;
        }
        catch (Exception ex)
        {
            Drop(ResolveSlot, "Pushed message dropped: the inbound dispatch services could not be resolved ({ExceptionType}).", ex.GetType().Name);
            return;
        }

        UnpackResult unpacked;
        try
        {
            unpacked = await client.UnpackAsync(Encoding.UTF8.GetString(frame.Payload.Span), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // JC-12: no wire value (not even the id) before the identity rule passed.
            Drop(UnpackSlot, "Pushed message dropped: unpack failed ({ExceptionType}).", ex.GetType().Name);
            return;
        }

        // JC-6: authcrypt whose sender key names the session's peer and whose decrypting key names its
        // local DID; the transport's attribution is the expectation, the verified key ids are the evidence.
        if (!ReturnRouteIdentity.IsFromPeerToLocal(unpacked, frame.PeerDid, frame.LocalDid, out var refusal))
        {
            Drop(IdentitySlot, "Pushed message dropped: {Refusal} (FR-RR-07).", refusal.ToString());
            return;
        }

        var messageId = unpacked.Message.Id;
        // FR-RR-01: a malformed header is `none` for this message; logged by id only, never the value.
        if (!ReturnRouteHeader.TryRead(unpacked.Message, out var header))
            Drop(MalformedHeaderSlot, "Pushed message {MessageId}: malformed return_route header treated as none (FR-RR-01).", messageId);

        // FR-RR-08: the receive side's rule (InboundReplayGate), applied once, here.
        var gate = InboundReplayGate.Run(unpacked, header, guard, options);
        if (!gate.Proceeds)
        {
            var admission = gate.Outcome switch
            {
                ReplayGateOutcome.Refused => gate.Replay.GetValueOrDefault().ToString(),
                ReplayGateOutcome.GuardFault => "GuardFault:" + gate.GuardFaultType,
                _ => gate.Outcome.ToString(),
            };
            Drop(AdmissionSlot, "Pushed message {MessageId} dropped: replay admission {Admission} (FR-RR-08).", messageId, admission);
            return;
        }

        DispatchOutcome outcome;
        try
        {
            // No session (FR-RR-07): a push is not a request the client can answer in band.
            outcome = await dispatcher.DispatchAsync(unpacked, client, options, Task.CompletedTask, session: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Drop(HandlerSlot, "Pushed message {MessageId}: the handler threw {ExceptionType}; dropped.", messageId, ex.GetType().Name);
            return;
        }

        // JC-9 (L-036/L-037): never sent. A reply to a pushed message is the application's explicit send.
        if (outcome.Result == DispatchResult.ReplyProduced)
            Write(LogLevel.Debug, "Pushed message {MessageId}: handler reply not sent (replies to pushes are sent explicitly).", messageId);
    }

    /// <summary>Log one drop: the first and every 1000th of its kind at Warning (with the count), the rest at Debug.</summary>
    /// <param name="slot">The kind of drop.</param>
    /// <param name="message">The template (ids and type / enum names only).</param>
    /// <param name="args">The template arguments.</param>
    private void Drop(int slot, string message, params object?[] args)
    {
        var n = Interlocked.Increment(ref _counts[slot]);
        if (n == 1 || n % WarnEvery == 0)
        {
            var withCount = new object?[args.Length + 1];
            args.CopyTo(withCount, 0);
            withCount[^1] = n;
            Write(LogLevel.Warning, message + " {Count} such drops.", withCount);
        }
        else
        {
            Write(LogLevel.Debug, message, args);
        }
    }

    /// <summary>Write one log line; never throws.</summary>
    /// <param name="level">The level.</param>
    /// <param name="message">The template.</param>
    /// <param name="args">The template arguments.</param>
    private void Write(LogLevel level, string message, params object?[] args)
    {
        if (_logger is null)
            return;
        try
        {
            _logger.Log(level, message, args);
        }
        catch
        {
            // Logging providers are host code; they must not change what is dispatched.
        }
    }
}
