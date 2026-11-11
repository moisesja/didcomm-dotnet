using System.Buffers;
using System.Diagnostics;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Text.Json;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.OutOfBand;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// L-014: alias the static OutOfBand API class so the same-named namespace doesn't shadow it.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace DidComm.AspNetCore;

/// <summary>
/// Minimal-API extensions for receiving DIDComm v2.1 envelopes over ASP.NET Core (PRD §9.2/§9.3,
/// FR-TRN-07/09/10, FR-API-06).
/// </summary>
/// <example>
/// HTTP receive with 415/413 handling runs in <c>samples/02-Cookbook</c> (section Q) and <c>samples/04-MediatorAgent</c>.
/// </example>
public static class DidCommEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Map an HTTP POST endpoint that accepts DIDComm envelopes, unpacks them via
    /// <see cref="DidCommClient.UnpackAsync"/>, and dispatches to <paramref name="onReceive"/>
    /// (FR-TRN-07). Returns <c>202 Accepted</c> on success, <c>415</c> for an unknown content type,
    /// and <c>413</c> when the body exceeds <see cref="DidCommOptions.MaxReceiveBytes"/> (FR-API-06).
    /// ANY envelope/processing failure — crypto, malformed structure, consistency, or DID resolution —
    /// returns a uniform <c>400 Bad Request</c> with an empty body and the reason logged server-side
    /// only, so the response cannot be used as a decryption / recipient-kid / resolution oracle
    /// (FR-API-07, issues #20/#28/#33).
    /// <para>
    /// No return-route session and no FR-RR-08 replay admission: the callback receives every
    /// unpacked message as before and the response is always <c>202</c>. Use the registry-aware
    /// overload (<c>MapDidCommEndpoint(pattern)</c>) or the session-aware callback overload
    /// (<c>Func&lt;UnpackResult, IReturnRouteSession?, CancellationToken, Task&lt;Message?&gt;&gt;</c>)
    /// for FR-RR-* behaviour.
    /// </para>
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/didcomm"</c>).</param>
    /// <param name="onReceive">Inline handler invoked with the unpacked message.</param>
    public static IEndpointConventionBuilder MapDidCommEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<UnpackResult, CancellationToken, Task> onReceive)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(onReceive);

        return endpoints.MapPost(pattern, async (HttpContext httpContext) =>
        {
            var client = httpContext.RequestServices.GetRequiredService<DidCommClient>();
            var coreOptions = httpContext.RequestServices.GetRequiredService<IOptions<DidCommOptions>>().Value;
            var receiveOptions = httpContext.RequestServices.GetService<IOptions<DidCommReceiveOptions>>()?.Value
                                 ?? new DidCommReceiveOptions();

            // FR-TRN-02: validate the inbound Content-Type matches one of the DIDComm media
            // types we know how to unpack. Mismatched → 415 (the standard HTTP code for an
            // unsupported request body shape).
            var contentType = httpContext.Request.ContentType;
            if (string.IsNullOrEmpty(contentType) || !MatchesMediaType(contentType, receiveOptions.AcceptedMediaTypes))
            {
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            // FR-API-06: cap body at MaxReceiveBytes before we even allocate the buffer; the
            // declared Content-Length is the fast path. The streaming read below enforces the
            // same cap even when the client lies about Content-Length.
            var maxBytes = coreOptions.MaxReceiveBytes;
            if (httpContext.Request.ContentLength is long declared && declared > maxBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            string body;
            try
            {
                body = await ReadCappedAsync(httpContext.Request.Body, maxBytes, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (PayloadTooLargeException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var logger = httpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("DidComm.AspNetCore.Receive");

            // #35: start the constant-time clock HERE — after the body has been read — so the floor
            // budgets only the unpack window where the recipient-kid-dependent timing lives. The body
            // read is kid-independent (the peer chose its size), so charging it against the floor would
            // let a peer pad the request out to MaxReceiveBytes, exhaust the floor before any crypto,
            // and re-expose the held-vs-unheld gap (PR #43 red-team).
            var startTimestamp = Stopwatch.GetTimestamp();

            UnpackResult unpacked;
            try
            {
                unpacked = await client.UnpackAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                throw; // genuine client abort — propagate, do not mask as a rejection
            }
            catch (Exception ex)
            {
                // Every unpack failure collapses to one opaque 400 so the peer can't distinguish them:
                // crypto / malformed / consistency / resolution / did:web / missing-secret, AND a
                // downstream DID-resolution timeout (TaskCanceledException : OperationCanceledException
                // with the request token NOT cancelled, e.g. an attacker-controlled did:webvh host that
                // hangs). Only a real client abort is rethrown above, so there is no 400-vs-500 oracle.
                return await NormalizedReceiveRejectionAsync(logger, ex, startTimestamp, receiveOptions.ReceiveRejectionFloor).ConfigureAwait(false);
            }

            // The message was received and unpacked; HTTP receive is one-way / fire-and-forget
            // (FR-TRN-10). A fault in the consumer callback is the receiver's own concern — log it but
            // still answer 202, so a throwing callback can't turn a successfully-unpacked message into a
            // distinguishable 500. Otherwise an attacker could probe "did this envelope decrypt for us?"
            // by inducing a callback throw — re-exposing the unpack-success oracle the uniform-400
            // rejection closes (#20 red-team).
            try
            {
                await onReceive(unpacked, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                throw; // genuine client abort
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "DidComm receive: onReceive callback threw after a successful unpack; acknowledging anyway (one-way receive).");
            }

            return Results.StatusCode(StatusCodes.Status202Accepted);
        });
    }

    /// <summary>
    /// Registry-aware HTTP receive endpoint (FR-PROTO-03). Same shape as the
    /// <c>onReceive</c>-callback overload (validates content-type → 415, caps body → 413, opaque
    /// <c>400</c> for any envelope or processing failure) but resolves the inbound message to a
    /// registered <see cref="IProtocolHandler"/> via <see cref="ProtocolDispatcher"/>.
    /// <para>
    /// <b>Return-route (FR-RR-04).</b> After unpack, the inbound goes through return-route
    /// admission: an authenticated message carrying <c>return_route</c> (other than <c>none</c>) or
    /// of a mediator-service type must pass the FR-RR-08 replay guard (a fresh <c>created_time</c>
    /// and a first-seen id) — a refused one is answered <c>202</c> with an empty body and is not
    /// dispatched. When the inbound was authcrypted (<c>UnpackResult.Encrypted &amp;&amp; Authenticated</c>),
    /// carries <c>return_route: all</c> and is not a <c>forward</c>, the handler gets a session
    /// (<see cref="ProtocolContext.Session"/>) bound to the authenticated sender; a reply the handler
    /// returns — or the one message it pushes through the session — is authcrypted from the DID
    /// that decrypted the inbound to exactly that sender and returned as the <c>200</c> body
    /// (<c>Content-Type: application/didcomm-encrypted+json</c>). At most one message per request: a
    /// second push, or a returned reply after a push, is dropped (the push wins). Otherwise the
    /// answer is <c>202</c> with an empty body and a handler reply is only logged. There is no
    /// switch: the response is addressed only to the authenticated sender, which is exactly why it
    /// is allowed. A reply to anyone else is never sent automatically — an inbound-triggered
    /// outbound send that trusts the message's (possibly attacker-advertised) sender or endpoint is
    /// a reflection / amplification / cross-tenant hazard; deliver such replies out of band with
    /// <see cref="DidCommClient.SendAsync"/> under your own egress policy. A Discover Features
    /// round-trip completes when the responder app sends its <c>disclose</c> back that way.
    /// </para>
    /// <para>
    /// The dispatch carries a transport acknowledgement (<see cref="ProtocolContext.Acknowledged"/>):
    /// it completes from <c>Response.OnCompleted</c>, once the response (the <c>202</c>, or the
    /// <c>200</c> carrying the returned message) has been committed to the client, and is cancelled
    /// when the request is aborted first or when the response turns into the uniform 400 after the
    /// handler ran. A handler that must not act before the sender holds the response — the
    /// mediator's <c>ForwardHandler</c>, whose processor and sink never run before the response is
    /// on the wire (FR-ROUTE-11) — waits on it; other handlers ignore it.
    /// </para>
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/didcomm"</c>).</param>
    public static IEndpointConventionBuilder MapDidCommEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        // Per mapped endpoint (JC-17 defaults, JC-22 drop-log rate limiters).
        var admissionEndpoint = new InboundAdmissionEndpoint();

        return endpoints.MapPost(pattern, async (HttpContext httpContext) =>
        {
            var sp = httpContext.RequestServices;
            var client = sp.GetRequiredService<DidCommClient>();
            var coreOptions = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
            var receiveOptions = sp.GetService<IOptions<DidCommReceiveOptions>>()?.Value ?? new DidCommReceiveOptions();
            var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("DidComm.AspNetCore.Dispatch");
            var replayGuard = admissionEndpoint.ResolveGuard(sp, coreOptions, logger);

            var contentType = httpContext.Request.ContentType;
            if (string.IsNullOrEmpty(contentType) || !MatchesMediaType(contentType, receiveOptions.AcceptedMediaTypes))
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            var maxBytes = coreOptions.MaxReceiveBytes;
            if (httpContext.Request.ContentLength is long declared && declared > maxBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            string body;
            try
            {
                body = await ReadCappedAsync(httpContext.Request.Body, maxBytes, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (PayloadTooLargeException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            // #35: start the constant-time clock after the (kid-independent) body read, covering only
            // the unpack+dispatch window where the recipient-kid-dependent timing lives — see the
            // matching note in the callback overload above.
            var startTimestamp = Stopwatch.GetTimestamp();

            // The transport acknowledgement handed to the handler (ProtocolContext.Acknowledged):
            // completes once the response has been committed to the client (Response.OnCompleted),
            // which — because every path below that reaches the handler returns 202, or the 200
            // carrying the returned message — means "the sender holds the response". Cancelled when
            // the request aborts before that, so a forward whose sender never received its 202 is
            // not relayed (the abort registration is kept until OnCompleted fires, not disposed when
            // this delegate returns, because the response is written after it returns). If the
            // response completed first, TrySetResult has already won and the late cancel is a no-op.
            var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var abortRegistration = httpContext.RequestAborted.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), acknowledged);
            httpContext.Response.OnCompleted(static state =>
            {
                var (tcs, registration) = ((TaskCompletionSource, CancellationTokenRegistration))state;
                registration.Dispose();
                tcs.TrySetResult();
                return Task.CompletedTask;
            }, (acknowledged, abortRegistration));

            // Unpack AND dispatch share one opaque exit: a handler bug (FR-THR-04 InvalidOperationException),
            // an unpack rejection (crypto/malformed/consistency/resolution), and a downstream
            // DID-resolution timeout (TaskCanceledException with the request token NOT cancelled) all
            // return the same 400 with no body, so the status code itself is not an oracle (no
            // 400-vs-500 partition). Only a genuine client abort is rethrown.
            HttpReturnRouteSession? session = null;
            try
            {
                var unpacked = await client.UnpackAsync(body, httpContext.RequestAborted).ConfigureAwait(false);

                // FR-RR-01/03/08: header, eligibility, replay. A refused message is answered like an
                // accepted one (202, empty) and is neither dispatched nor given a session (JC-2).
                var admission = InboundAdmission.Evaluate(unpacked, coreOptions, replayGuard, admissionEndpoint, logger);
                if (!admission.Admitted)
                    return Results.StatusCode(StatusCodes.Status202Accepted);

                // FR-RR-04: no switch on HTTP — an eligible request gets a request-scoped session,
                // never registered (JC-16).
                if (admission.SessionEligible)
                    session = new HttpReturnRouteSession(client, admission.PeerDid!, admission.LocalDid!, logger);

                var outcome = await dispatcher.DispatchAsync(unpacked, client, coreOptions, acknowledged.Task, session, httpContext.RequestAborted).ConfigureAwait(false);

                // Without a session a reply is logged, not sent: an automatic inbound-triggered send to
                // anyone but the authenticated sender is deliberately absent (see the remarks above).
                var returned = false;
                if (session is not null && outcome is { Result: DispatchResult.ReplyProduced, Reply: not null })
                {
                    // A pack failure is swallowed (type name logged) inside TrySendAsync and the
                    // request still answers 202 — never a 400 after dispatch (JC-13).
                    returned = await session.TrySendAsync(outcome.Reply, httpContext.RequestAborted).ConfigureAwait(false);
                    if (!returned && session.HasResponse)
                        SafeLog.Write(logger, LogLevel.Information, "Inbound {MessageId}: the handler's returned reply was dropped: the HTTP session already holds a message (FR-RR-04).", unpacked.Message.Id);
                }
                LogOutcome(logger, outcome, returnedOnSession: returned);

                // The last step closes the session and takes its message in one transition (PA-2):
                // a push still packing now returns false and never reaches this response.
                var returnedBody = session?.TakeResponseAndClose();
                // The only exits after the handler ran: the 202, or the 200 carrying the returned
                // message. OnCompleted then completes the acknowledgement once it is committed.
                return returnedBody is null
                    ? Results.StatusCode(StatusCodes.Status202Accepted)
                    : Results.Bytes(returnedBody, DidCommMediaTypes.Encrypted);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                session?.TakeResponseAndClose();
                acknowledged.TrySetCanceled();
                throw; // genuine client abort — propagate
            }
            catch (Exception ex)
            {
                // A 400 also completes OnCompleted, but it is never an acknowledgement of the inbound:
                // cancel first, so a handler that ran before this throw (a logger fault in
                // LogOutcome is the one such path) drops its deferred work instead of publishing it.
                // The session closes without a response: nothing it held is sent.
                session?.TakeResponseAndClose();
                acknowledged.TrySetCanceled();
                return await NormalizedReceiveRejectionAsync(logger, ex, startTimestamp, receiveOptions.ReceiveRejectionFloor).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Map an HTTP POST endpoint that unpacks DIDComm envelopes, runs return-route admission, and
    /// hands each admitted message to <paramref name="onReceive"/> together with its return-route
    /// session (FR-RR-04) — the callback counterpart of the registry-aware
    /// <c>MapDidCommEndpoint(pattern)</c>. Same envelope handling as the other overloads: 415 for an
    /// unknown content type, 413 above <see cref="DidCommOptions.MaxReceiveBytes"/>, and one opaque,
    /// time-floored <c>400</c> for any unpack failure (FR-API-07).
    /// <para>
    /// The session argument is non-null only when the inbound was authcrypted, carries
    /// <c>return_route: all</c>, is not a <c>forward</c> and passed FR-RR-08 replay admission; a
    /// message the replay guard refuses is answered <c>202</c> and never reaches the callback. A
    /// non-null message the callback returns is sent through the session (authcrypted from the DID
    /// that decrypted the inbound to exactly the authenticated sender; refused otherwise) and
    /// becomes the <c>200</c> body (<c>Content-Type: application/didcomm-encrypted+json</c>); at most
    /// one message per request, and a message the callback already pushed through the session wins
    /// over its return value. Every other outcome is <c>202</c> with an empty body.
    /// </para>
    /// <para>
    /// A callback that throws is logged (exception type name only) and answered <c>202</c> — never a
    /// 500, so a callback fault cannot become an unpack-success oracle — and anything it pushed
    /// before throwing is discarded. There is no transport acknowledgement here (no
    /// <see cref="ProtocolContext"/>): ordering work after the response is the callback's own job.
    /// </para>
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/didcomm"</c>).</param>
    /// <param name="onReceive">Invoked with each admitted message and its session (or <c>null</c>); returns the message to send back on the session, or <c>null</c>.</param>
    public static IEndpointConventionBuilder MapDidCommEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<UnpackResult, IReturnRouteSession?, CancellationToken, Task<Message?>> onReceive)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(onReceive);

        var admissionEndpoint = new InboundAdmissionEndpoint();

        return endpoints.MapPost(pattern, async (HttpContext httpContext) =>
        {
            var sp = httpContext.RequestServices;
            var client = sp.GetRequiredService<DidCommClient>();
            var coreOptions = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
            var receiveOptions = sp.GetService<IOptions<DidCommReceiveOptions>>()?.Value ?? new DidCommReceiveOptions();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("DidComm.AspNetCore.Receive");
            var replayGuard = admissionEndpoint.ResolveGuard(sp, coreOptions, logger);

            var contentType = httpContext.Request.ContentType;
            if (string.IsNullOrEmpty(contentType) || !MatchesMediaType(contentType, receiveOptions.AcceptedMediaTypes))
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            var maxBytes = coreOptions.MaxReceiveBytes;
            if (httpContext.Request.ContentLength is long declared && declared > maxBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            string body;
            try
            {
                body = await ReadCappedAsync(httpContext.Request.Body, maxBytes, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (PayloadTooLargeException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            // #35: the floor budgets only the unpack window (see the first callback overload).
            var startTimestamp = Stopwatch.GetTimestamp();

            UnpackResult unpacked;
            try
            {
                unpacked = await client.UnpackAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                throw; // genuine client abort — propagate, do not mask as a rejection
            }
            catch (Exception ex)
            {
                // The same uniform, time-floored 400 as the other overloads (#20/#28/#33/#35).
                return await NormalizedReceiveRejectionAsync(logger, ex, startTimestamp, receiveOptions.ReceiveRejectionFloor).ConfigureAwait(false);
            }

            var admission = InboundAdmission.Evaluate(unpacked, coreOptions, replayGuard, admissionEndpoint, logger);
            if (!admission.Admitted)
                return Results.StatusCode(StatusCodes.Status202Accepted);

            var session = admission.SessionEligible
                ? new HttpReturnRouteSession(client, admission.PeerDid!, admission.LocalDid!, logger)
                : null;

            Message? reply;
            try
            {
                reply = await onReceive(unpacked, session, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                session?.TakeResponseAndClose();
                throw; // genuine client abort
            }
            catch (Exception ex)
            {
                // The callback is the host's own code: log by type name, answer 202, send nothing it
                // may have pushed (a fault must not become a 500 or a partial response).
                session?.TakeResponseAndClose();
                SafeLog.Write(logger, LogLevel.Error, "Inbound {MessageId}: onReceive callback threw {ExceptionType} after a successful unpack; acknowledging with 202.", unpacked.Message.Id, ex.GetType().Name);
                return Results.StatusCode(StatusCodes.Status202Accepted);
            }

            if (reply is not null)
            {
                if (session is null)
                {
                    SafeLog.Write(logger, LogLevel.Debug, "Inbound {MessageId}: callback reply not returned: no return-route session for this request.", unpacked.Message.Id);
                }
                else
                {
                    bool returned;
                    try
                    {
                        returned = await session.TrySendAsync(reply, httpContext.RequestAborted).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
                    {
                        session.TakeResponseAndClose();
                        throw;
                    }
                    if (!returned && session.HasResponse)
                        SafeLog.Write(logger, LogLevel.Information, "Inbound {MessageId}: the callback's returned reply was dropped: the HTTP session already holds a message (FR-RR-04).", unpacked.Message.Id);
                }
            }

            var returnedBody = session?.TakeResponseAndClose();
            return returnedBody is null
                ? Results.StatusCode(StatusCodes.Status202Accepted)
                : Results.Bytes(returnedBody, DidCommMediaTypes.Encrypted);
        });
    }

    /// <summary>
    /// Map an HTTP GET endpoint that serves the short-form of an out-of-band invitation
    /// (FR-OOB-04). A recipient dereferences a <c>?_oobid=&lt;id&gt;</c> URL; this endpoint reads
    /// the id, looks it up in <paramref name="store"/>, and returns the full plaintext invitation
    /// (<c>200</c>, <c>application/didcomm-plain+json</c>). Returns <c>400</c> when the
    /// <c>_oobid</c> query value is absent and <c>404</c> when the id is unknown. The sender hosts
    /// this itself rather than using a public URL shortener, which the spec forbids for privacy.
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/oob"</c>) — must match the path used in <c>OutOfBand.ToShortUrl</c>.</param>
    /// <param name="store">The invitation store the sender populated when it minted the short URL.</param>
    public static IEndpointConventionBuilder MapDidCommOobEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        IOobInvitationStore store)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(store);

        return endpoints.MapGet(pattern, (HttpContext httpContext) =>
        {
            // The short URL carries the id in the _oobid query parameter (OutOfBand.OobIdParameter).
            var oobId = httpContext.Request.Query[OutOfBandApi.OobIdParameter].ToString();
            if (string.IsNullOrEmpty(oobId))
                return Results.StatusCode(StatusCodes.Status400BadRequest);

            var invitation = store.Retrieve(oobId);
            if (invitation is null)
                return Results.StatusCode(StatusCodes.Status404NotFound);

            return Results.Text(invitation, MediaTypes.Plaintext);
        });
    }

    /// <summary>
    /// Map a WebSocket endpoint that accepts DIDComm envelopes per FR-TRN-09 (one logical
    /// WebSocket message per packed envelope; multi-frame messages are reassembled before
    /// processing) and dispatches each to <paramref name="onReceive"/>. One-way per FR-TRN-10:
    /// the server does NOT send protocol replies back on the same socket. Oversize messages
    /// (per <see cref="DidCommOptions.MaxReceiveBytes"/>, FR-API-06) trigger a 1009 close.
    /// With <see cref="DidCommReceiveOptions.UseStomp"/> the endpoint instead speaks the
    /// minimal STOMP 1.2 subset (FR-TRN-12): CONNECT→CONNECTED, one packed envelope per SEND
    /// frame body, DISCONNECT to close.
    /// <para>
    /// No return-route session and no FR-RR-08 replay admission: the callback receives every
    /// unpacked message as before. Use the registry-aware overload
    /// (<c>MapDidCommWebSocket(pattern)</c>) for FR-RR-* behaviour.
    /// </para>
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/ws/didcomm"</c>).</param>
    /// <param name="onReceive">Inline handler invoked with each unpacked message.</param>
    public static IEndpointConventionBuilder MapDidCommWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<UnpackResult, CancellationToken, Task> onReceive)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(onReceive);

        return endpoints.MapGet(pattern, async (HttpContext httpContext) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var client = httpContext.RequestServices.GetRequiredService<DidCommClient>();
            var coreOptions = httpContext.RequestServices.GetRequiredService<IOptions<DidCommOptions>>().Value;
            var receiveOptions = httpContext.RequestServices.GetService<IOptions<DidCommReceiveOptions>>()?.Value
                                 ?? new DidCommReceiveOptions();
            var loggerFactory = httpContext.RequestServices.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("DidComm.AspNetCore.WebSocket");

            await ReceiveLoopAsync(socket, client, coreOptions, receiveOptions, onReceive, logger, httpContext.RequestAborted).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Registry-aware WebSocket overload (FR-PROTO-03). Same wire behavior as the inline-
    /// callback overload (FR-TRN-09/10/API-06 enforced); each reassembled envelope is unpacked,
    /// passed through return-route admission and dispatched through <see cref="ProtocolDispatcher"/>.
    /// <para>
    /// <b>Replay (FR-RR-08).</b> An authenticated message carrying <c>return_route</c> (other than
    /// <c>none</c>) or of a mediator-service type must pass the replay guard; a refused one is
    /// dropped with nothing written and nothing dispatched.
    /// </para>
    /// <para>
    /// <b>Return-route (FR-RR-05)</b>, only when <see cref="DidCommReceiveOptions.AllowSameSocketReplies"/>
    /// is <c>true</c> (default <c>false</c>, FR-TRN-10 one-way) and never under STOMP. The connection
    /// becomes a session at its first message that was authcrypted, carries <c>return_route: all</c>
    /// and is not a <c>forward</c>; the session's peer (the authenticated sender) is fixed from then
    /// on and it is registered in the <see cref="IReturnRouteSessionRegistry"/>, where handlers find
    /// it for live pushes. Handler replies and <see cref="IReturnRouteSession.TrySendAsync"/> pushes
    /// are authcrypted from the DID that decrypted the opening message to exactly that peer and
    /// written as one binary WebSocket message each, by a single writer. A later header-less message
    /// leaves the mode unchanged; a later <c>none</c> (or <c>thread</c>) from the same pair stops
    /// pushes and <c>all</c> resumes them. A message authenticated by another DID on the same socket
    /// is dispatched without a session and its reply is not written. A header-less message on a
    /// socket without a session gets no reply. The registry bounds the sessions per peer (one more
    /// evicts that peer's oldest, whose socket is then closed with a normal close frame) and in total
    /// (a new session is refused and the message is dispatched without one). Closing the socket
    /// unregisters the session.
    /// </para>
    /// <para>
    /// There is no per-message wire acknowledgement on a WebSocket (nothing is written back for an
    /// accepted envelope), so the dispatch carries none: <see cref="ProtocolContext.Acknowledged"/>
    /// is already complete and a handler that defers past the acknowledgement — the mediator's
    /// <c>ForwardHandler</c> — proceeds as soon as it has admitted the message.
    /// </para>
    /// </summary>
    /// <param name="endpoints">The ASP.NET Core endpoint route builder.</param>
    /// <param name="pattern">URL pattern (e.g. <c>"/ws/didcomm"</c>).</param>
    public static IEndpointConventionBuilder MapDidCommWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        // Per mapped endpoint (JC-17 defaults, JC-22 drop-log rate limiters).
        var admissionEndpoint = new InboundAdmissionEndpoint();

        return endpoints.MapGet(pattern, async (HttpContext httpContext) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var sp = httpContext.RequestServices;
            var client = sp.GetRequiredService<DidCommClient>();
            var coreOptions = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
            var receiveOptions = sp.GetService<IOptions<DidCommReceiveOptions>>()?.Value ?? new DidCommReceiveOptions();
            var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("DidComm.AspNetCore.WebSocket.Dispatch");
            var returnRoute = new WebSocketReturnRoute(
                admissionEndpoint,
                admissionEndpoint.ResolveGuard(sp, coreOptions, logger),
                // The registry only matters when the switch can open sessions (never under STOMP).
                receiveOptions.AllowSameSocketReplies && !receiveOptions.UseStomp ? admissionEndpoint.ResolveRegistry(sp, logger) : null,
                receiveOptions.AllowSameSocketReplies && !receiveOptions.UseStomp ? InboundAdmissionEndpoint.ResolveSessionOptions(sp).MaxPendingPushes : 0);

            await ReceiveLoopWithDispatchAsync(socket, client, coreOptions, receiveOptions, dispatcher, returnRoute, logger, httpContext.RequestAborted)
                .ConfigureAwait(false);
        });
    }

    /// <summary>The return-route collaborators of one WebSocket connection.</summary>
    /// <param name="Endpoint">The mapped endpoint's state (drop-log rate limiters).</param>
    /// <param name="Guard">The FR-RR-08 replay guard.</param>
    /// <param name="Registry">The session registry; <c>null</c> when this connection can never open a session.</param>
    /// <param name="MaxPendingPushes">The writer queue depth for a session on this connection.</param>
    private sealed record WebSocketReturnRoute(
        InboundAdmissionEndpoint Endpoint,
        IInboundReplayGuard Guard,
        IReturnRouteSessionRegistry? Registry,
        int MaxPendingPushes);

    private static async Task ReceiveLoopWithDispatchAsync(
        System.Net.WebSockets.WebSocket socket,
        DidCommClient client,
        DidCommOptions coreOptions,
        DidCommReceiveOptions receiveOptions,
        ProtocolDispatcher dispatcher,
        WebSocketReturnRoute returnRoute,
        ILogger? logger,
        CancellationToken requestAborted)
    {
        // The connection's own token: cancelled by a client abort, or by the session when the socket
        // is broken (writer fault) or an evicted peer never answers the close frame (JC-21).
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = connectionCts.Token;
        var maxBytes = coreOptions.MaxReceiveBytes;
        // FR-TRN-12: opt-in STOMP session for this connection. The MaxReceiveBytes 1009 close
        // below still applies to the whole (framed) message BEFORE any STOMP decoding.
        var stomp = receiveOptions.UseStomp ? new StompServerSession() : null;
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        // FR-RR-05: the connection's return-route session, opened at the first eligible message.
        // Once it closed (evicted, writer fault) it is never reopened or re-bound on this socket (PA-3).
        WebSocketReturnRouteSession? session = null;
        // The socket's (peer, local) pair is fixed by its first eligible message even when the registry
        // refused that message's session (total bound): only the same pair may retry, so another DID
        // on this socket never opens or re-binds a session here (FR-RR-05).
        string? socketPeer = null;
        string? socketLocal = null;
        var stompNoticeLogged = false;
        // Close requests from the loop are deferred to `finally`, after the session's writer has
        // stopped, so a close never overlaps a send (the writer is the only other sender).
        WebSocketCloseStatus? pendingClose = null;
        string? pendingCloseReason = null;
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                long total = 0;
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        pendingClose = WebSocketCloseStatus.NormalClosure;
                        pendingCloseReason = "client closed";
                        break;
                    }
                    total += result.Count;
                    if (total > maxBytes)
                    {
                        pendingClose = WebSocketCloseStatus.MessageTooBig;
                        pendingCloseReason = "payload too large";
                        break;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                if (pendingClose is not null)
                    break;

                string packed;
                if (stomp is not null)
                {
                    var (envelope, closeConnection) = await stomp.ProcessAsync(
                        socket, ms.ToArray(), receiveOptions.AcceptedMediaTypes, logger, ct).ConfigureAwait(false);
                    if (closeConnection)
                        return;
                    if (envelope is null)
                        continue; // control frame handled, or an unacceptable SEND discarded
                    packed = envelope;
                }
                else
                {
                    packed = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                }

                // #35: the recipient-kid timing side-channel that the HTTP receive path pads against
                // (see DelayToRejectionFloorAsync) does NOT apply here — a failed unpack is logged and
                // discarded with NOTHING written back on the socket, so there is no per-message response
                // for a peer to time. We deliberately do not pad the discard: it would add latency to the
                // whole multiplexed message stream on the connection for a far weaker, noisier signal.
                UnpackResult unpacked;
                try
                {
                    unpacked = await client.UnpackAsync(packed, ct).ConfigureAwait(false);
                }
                catch (MalformedMessageException ex)
                {
                    logger?.LogWarning(ex, "MapDidCommWebSocket: discarding malformed envelope");
                    continue;
                }
                catch (CryptoException ex)
                {
                    logger?.LogWarning(ex, "MapDidCommWebSocket: discarding undecryptable envelope");
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // ANY other unpack failure is a property of THIS envelope, not of the connection.
                    // Without this the first such message escapes the loop and tears down a socket
                    // that may be carrying other peers' traffic — a one-envelope disconnect. That
                    // covers the typed rejections (consistency, DID resolution, did:web, missing
                    // secret, protocol) AND untyped faults that the unpack contract is not supposed
                    // to emit but might: a parse fault nothing upstream has mapped yet, and a
                    // DID-resolution timeout, which arrives as a TaskCanceledException with our token
                    // NOT cancelled. The
                    // HTTP path already collapses every unpack failure into one opaque rejection for
                    // the same reason; this is the socket equivalent. A genuine shutdown (our token
                    // cancelled) still propagates and ends the loop.
                    logger?.LogWarning(ex, "MapDidCommWebSocket: discarding rejected envelope");
                    continue;
                }

                // FR-RR-01/03/08: header, eligibility, replay. A refused message is dropped: nothing
                // written, nothing dispatched, the session untouched (JC-2).
                var admission = InboundAdmission.Evaluate(unpacked, coreOptions, returnRoute.Guard, returnRoute.Endpoint, logger);
                if (!admission.Admitted)
                    continue;

                // FR-RR-05: open, update or use the connection's session. Only an identity-eligible
                // message (encrypted+authenticated, not a forward) ever touches it.
                IReturnRouteSession? dispatchSession = null;
                if (admission.PeerDid is not null)
                {
                    if (stomp is not null)
                    {
                        // A spec-shaped push needs STOMP MESSAGE frames and subscriptions (broker
                        // semantics outside the minimal subset), so STOMP never gets a session.
                        if (admission.SessionEligible && !stompNoticeLogged)
                        {
                            stompNoticeLogged = true;
                            SafeLog.Write(logger, LogLevel.Information, "Inbound {MessageId}: return_route all is not available under STOMP (FR-RR-05); no session is opened on this connection.", unpacked.Message.Id);
                        }
                    }
                    else if (returnRoute.Registry is not null)
                    {
                        if (session is null)
                        {
                            if (admission.SessionEligible && (socketPeer is null || IsPair(socketPeer, socketLocal, admission)))
                            {
                                socketPeer ??= admission.PeerDid;
                                socketLocal ??= admission.LocalDid;
                                var candidate = new WebSocketReturnRouteSession(
                                    socket, client, admission.PeerDid, admission.LocalDid!, returnRoute.MaxPendingPushes, connectionCts, logger);
                                if (InboundAdmission.TryOpen(returnRoute.Registry, candidate, unpacked.Message.Id, logger))
                                {
                                    session = candidate;
                                    session.Start();
                                }
                                // Refused (total bound): dispatched without a session; a later eligible
                                // message from the same pair may try again.
                            }
                        }
                        else if (IsSessionPair(session, admission) && admission.HeaderPresent)
                        {
                            // The mode changes only for a message that carries return_route (a
                            // header-less one leaves it unchanged); a closed session ignores it (PA-3).
                            session.ApplyHeader(admission.Header);
                        }

                        // Another DID on this socket is dispatched without a session and never re-binds it.
                        if (session is not null && !session.IsClosed && IsSessionPair(session, admission))
                            dispatchSession = session;
                    }
                }

                DispatchOutcome outcome;
                try
                {
                    // No wire acknowledgement on a WebSocket (#90): the dispatch carries none.
                    outcome = await dispatcher.DispatchAsync(unpacked, client, coreOptions, Task.CompletedTask, dispatchSession, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // FR-THR-04 rule 2 violation in a handler (a bug). Don't tear down the
                    // socket — log and keep serving the connection so a buggy protocol doesn't
                    // poison every other protocol sharing the connection.
                    logger?.LogWarning(ex, "MapDidCommWebSocket: handler bug (FR-THR-04 rule 2); dropping inbound and continuing.");
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Same posture as above for any other handler-thrown exception: log + keep
                    // the connection alive. Cancellation propagates so the loop can exit cleanly.
                    logger?.LogError(ex, "MapDidCommWebSocket: handler threw an unhandled exception; dropping inbound and continuing.");
                    continue;
                }

                // A reply rides the session only; without one it is logged, not sent (a header-less
                // message on a socket without a session gets no reply — FR-RR-01/05).
                var returned = false;
                if (dispatchSession is not null && outcome is { Result: DispatchResult.ReplyProduced, Reply: not null })
                    returned = await dispatchSession.TrySendAsync(outcome.Reply, ct).ConfigureAwait(false);
                LogOutcome(logger, outcome, returnedOnSession: returned);
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException or WebSocketException)
                                   && connectionCts.IsCancellationRequested
                                   && !requestAborted.IsCancellationRequested)
        {
            // Server-initiated end of the connection: an evicted peer never answered the close frame,
            // or the session's writer found the socket broken. Nothing more to read or write.
        }
        finally
        {
            var mayClose = true;
            if (session is not null)
            {
                // Unregister first (raises SessionClosed(Unregistered); a no-op when the registry
                // already evicted it), then stop the writer, so a close never overlaps a send.
                try
                {
                    returnRoute.Registry!.Unregister(session);
                }
                catch (Exception ex)
                {
                    SafeLog.Write(logger, LogLevel.Warning, "Return-route session unregister threw {ExceptionType}.", ex.GetType().Name);
                }
                mayClose = await session.StopAsync().ConfigureAwait(false);
            }

            if (!mayClose)
            {
                socket.Abort();
            }
            else if (pendingClose is { } closeStatus && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(closeStatus, pendingCloseReason, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    SafeLog.Write(logger, LogLevel.Debug, "MapDidCommWebSocket: close failed with {ExceptionType}.", ex.GetType().Name);
                }
            }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsSessionPair(WebSocketReturnRouteSession session, InboundAdmissionResult admission)
        => IsPair(session.PeerDid, session.LocalDid, admission);

    // PA-5: both ordinal comparisons always run (non-short-circuit `&`), so the comparison of a wire
    // identity against the socket's pair has no data-dependent branch between them.
    private static bool IsPair(string? peerDid, string? localDid, InboundAdmissionResult admission)
        => string.Equals(peerDid, admission.PeerDid, StringComparison.Ordinal)
           & string.Equals(localDid, admission.LocalDid, StringComparison.Ordinal);

    private static void LogOutcome(ILogger? logger, DispatchOutcome outcome, bool returnedOnSession)
    {
        if (logger is null) return;
        switch (outcome.Result)
        {
            case DispatchResult.ReplyProduced:
                logger.LogInformation(
                    "Protocol dispatch produced a reply (handler={Handler}, returned-on-session={Returned}).",
                    outcome.Handler?.ProtocolUri, returnedOnSession);
                break;
            case DispatchResult.NoReply:
                logger.LogDebug("Protocol dispatch ran handler '{Handler}' which produced no reply.", outcome.Handler?.ProtocolUri);
                break;
            case DispatchResult.NoHandler:
                logger.LogDebug("Protocol dispatch found no handler for inbound message.");
                break;
            case DispatchResult.DroppedAsAckLoop:
                logger.LogWarning("Protocol dispatch dropped inbound pure-ACK that also requested an ACK (FR-THR-04 rule 3).");
                break;
        }
    }

    /// <summary>
    /// FR-API-07 / FR-TRN-10: collapse every input- or handler-triggered failure on the HTTP receive
    /// path to one opaque rejection — a bare <c>400</c> with no body — so a remote peer cannot use the
    /// status code or response text as a decryption / recipient-kid / DID-resolution oracle (issues
    /// #20/#28/#33). The detailed reason is logged server-side only. The WebSocket paths already take
    /// this posture (log-and-discard, echo nothing); this brings the HTTP paths to parity.
    /// <para>
    /// #35: the uniform body/status alone is not enough — the response <em>time</em> still partitioned
    /// the failures (a held recipient kid runs the full ECDH/AEAD path; an unheld one fast-fails before
    /// any crypto), so a single timed request enumerated which keys the agent holds. Before returning
    /// the 400 we therefore pad the response out to <paramref name="rejectionFloor"/> measured from
    /// <paramref name="startTimestamp"/> (captured after the body read, so the floor budgets only the
    /// unpack window), making the rejection time independent of the crypto work done. See
    /// <see cref="DelayToRejectionFloorAsync"/>.
    /// </para>
    /// </summary>
    private static async Task<IResult> NormalizedReceiveRejectionAsync(
        ILogger? logger, Exception ex, long startTimestamp, TimeSpan rejectionFloor)
    {
        if (ex is DidCommException)
        {
            // Expected, peer-triggered category (malformed / crypto / consistency / resolution /
            // unsupported-method / secret-missing) — it's the peer's envelope, not our bug.
            logger?.LogWarning(ex, "DidComm receive: rejecting inbound envelope ({Reason}).", ex.GetType().Name);
        }
        else
        {
            // A handler bug (FR-THR-04 InvalidOperationException) or any other unexpected throw.
            logger?.LogError(ex, "DidComm receive: inbound processing failed ({Reason}).", ex.GetType().Name);
        }

        await DelayToRejectionFloorAsync(startTimestamp, rejectionFloor).ConfigureAwait(false);
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// #35: wait until at least <paramref name="floor"/> has elapsed since <paramref name="startTimestamp"/>
    /// (captured after the body read, so the floor budgets only the unpack window) before the caller
    /// returns its rejection. This pins the cheap, universal enumeration probe — garbage ciphertext to a
    /// guessed kid — to a constant time: the fast-fail "unheld kid" path and the full-crypto "held kid +
    /// AEAD fail" path both finish well under the floor and so are padded to the same duration.
    /// <para>
    /// The wait is deliberately <b>not</b> cancelable: a cancelable delay would let a peer abort the
    /// connection early to skip the pad and re-expose the timing gap. <see cref="Task.Delay(TimeSpan)"/>
    /// is timer-backed and holds no thread, so padding a soon-to-be-rejected request is cheap.
    /// </para>
    /// <para>
    /// LIMITATION (documented, not fixed by a fixed floor): a fixed floor only masks failures that
    /// finish <em>under</em> it. The held-only path that decrypts and then does network DID resolution
    /// (authcrypt sender / FR-CONSIST-06 / from_prior) can run <em>longer</em> than the floor — and an
    /// attacker who can drive that (e.g. authcrypt to a guessed kid signed by an attacker-controlled,
    /// deliberately-slow did:webvh sender) sees "response ≫ floor" for a held kid versus "response ≈
    /// floor" for an unheld one. That residual is unbounded (network latency), so a fixed floor cannot
    /// close it without an absurd value; mitigate it with auth / a rate-limiter in front of the receive
    /// endpoint (the deployment tradeoff the #35 issue calls out, tracked in #44). The durable fix for
    /// the local-crypto gap is constant-time recipient-kid selection in the JWE parser, tracked upstream.
    /// </para>
    /// </summary>
    internal static async Task DelayToRejectionFloorAsync(long startTimestamp, TimeSpan floor)
    {
        if (floor <= TimeSpan.Zero)
            return;

        var remaining = floor - Stopwatch.GetElapsedTime(startTimestamp);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining).ConfigureAwait(false);
    }

    private static async Task ReceiveLoopAsync(
        System.Net.WebSockets.WebSocket socket,
        DidCommClient client,
        DidCommOptions coreOptions,
        DidCommReceiveOptions receiveOptions,
        Func<UnpackResult, CancellationToken, Task> onReceive,
        ILogger? logger,
        CancellationToken ct)
    {
        var maxBytes = coreOptions.MaxReceiveBytes;
        // FR-TRN-12: opt-in STOMP session — same posture as the dispatcher-backed loop (L-043:
        // both receive loops must speak the same framing).
        var stomp = receiveOptions.UseStomp ? new StompServerSession() : null;
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                long total = 0;
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closed", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    total += result.Count;
                    if (total > maxBytes)
                    {
                        // FR-API-06 + RFC 6455 §7.4.1 close code 1009 "Message Too Big".
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "payload too large", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                // FR-TRN-09: the reassembled bytes are exactly one packed envelope — or, in the
                // opt-in STOMP mode (FR-TRN-12), exactly one STOMP frame whose SEND body is one
                // packed envelope.
                string packed;
                if (stomp is not null)
                {
                    var (envelope, closeConnection) = await stomp.ProcessAsync(
                        socket, ms.ToArray(), receiveOptions.AcceptedMediaTypes, logger, ct).ConfigureAwait(false);
                    if (closeConnection)
                        return;
                    if (envelope is null)
                        continue; // control frame handled, or an unacceptable SEND discarded
                    packed = envelope;
                }
                else
                {
                    packed = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                }

                // #35: no constant-time pad here (unlike the HTTP 400 path) — a failed unpack is logged
                // and discarded with nothing written back, so there is no per-message response a peer can
                // time to enumerate held recipient kids.
                UnpackResult unpacked;
                try
                {
                    unpacked = await client.UnpackAsync(packed, ct).ConfigureAwait(false);
                }
                catch (MalformedMessageException ex)
                {
                    logger?.LogWarning(ex, "MapDidCommWebSocket: discarding malformed envelope");
                    continue;
                }
                catch (CryptoException ex)
                {
                    logger?.LogWarning(ex, "MapDidCommWebSocket: discarding undecryptable envelope");
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Any other unpack failure belongs to THIS envelope, not to the connection —
                    // otherwise one hostile message disconnects a socket that may be carrying other
                    // peers' traffic. Covers the typed rejections (consistency, DID resolution,
                    // did:web, missing secret, protocol) and untyped faults the unpack contract is
                    // not supposed to emit but might: a parse fault nothing upstream has mapped yet,
                    // and a DID-resolution timeout, which arrives as TaskCanceledException with our
                    // token NOT cancelled. A genuine shutdown (our token cancelled) still ends the loop.
                    logger?.LogWarning(ex, "MapDidCommWebSocket: discarding rejected envelope");
                    continue;
                }

                try
                {
                    await onReceive(unpacked, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // The receive callback is the host's own code; a fault in it is the host's
                    // concern, and it must not take the connection (or the next message) down.
                    logger?.LogError(ex, "MapDidCommWebSocket: receive callback threw; continuing");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Internal (not private) so the FR-TRN-12 StompServerSession applies the same accepted-media-type
    // gate to a SEND frame's content-type that the HTTP path applies to the Content-Type header.
    internal static bool MatchesMediaType(string contentType, IReadOnlyList<string> accepted)
    {
        // ContentType can include parameters (e.g. "; charset=utf-8") — strip and compare the
        // base media type only. A malformed header throws FormatException; treat that as "no
        // match" so the endpoint answers 415 rather than letting a 500 escape.
        string? mediaType;
        try
        {
            mediaType = new ContentType(contentType).MediaType;
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var allowed in accepted)
        {
            if (string.Equals(mediaType, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Read the request body into a UTF-8 string but throw <see cref="PayloadTooLargeException"/>
    /// the moment the cumulative read exceeds <paramref name="maxBytes"/>. Prevents the
    /// declared <c>Content-Length</c> from being trusted as the only gate.
    /// </summary>
    internal static async Task<string> ReadCappedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            var read = await body.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > maxBytes)
                throw new PayloadTooLargeException(maxBytes);
            ms.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    internal sealed class PayloadTooLargeException : Exception
    {
        public PayloadTooLargeException(long maxBytes) : base($"Payload exceeded {maxBytes} bytes.") { }
    }
}
