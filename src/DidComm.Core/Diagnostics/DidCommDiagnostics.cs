using System.Diagnostics;

namespace DidComm.Diagnostics;

/// <summary>
/// Names the library's OpenTelemetry instrumentation (NFR-05). The library emits
/// <see cref="Activity"/> spans through a single <see cref="ActivitySource"/> named
/// <see cref="ActivitySourceName"/>, and metrics through a meter named <see cref="MeterName"/>
/// (a routing mediator's forward outcomes, and the mediator service's instruments);
/// consumers subscribe to them from their own OpenTelemetry (or plain
/// <see cref="ActivityListener"/> / <c>MeterListener</c>) pipeline — the library itself
/// references no OpenTelemetry SDK and exports nothing on its own. When nothing listens, every
/// span is a no-op (<c>StartActivity</c> returns <c>null</c>), so the tracing is zero-cost for
/// hosts that never opt in.
/// </summary>
/// <remarks>
/// <para>
/// Spans cover the five operations the PRD names: pack
/// (<c>didcomm.pack.encrypted</c> / <c>didcomm.pack.signed</c> / <c>didcomm.pack.plaintext</c>),
/// <c>didcomm.unpack</c>, <c>didcomm.send</c> (transport layer), <c>didcomm.resolve</c>
/// (DID resolution), and <c>didcomm.handle</c> (inbound protocol dispatch). Attributes are
/// restricted to header-level metadata — message type URIs, JOSE algorithm identifiers,
/// recipient counts, DID method names, exception <em>type</em> names — never private key
/// material, plaintext bodies, attachment data, JWK members, or exception messages (NFR-04).
/// </para>
/// </remarks>
/// <example>
/// Subscribing from an OpenTelemetry host:
/// <code>
/// services.AddOpenTelemetry().WithTracing(t => t.AddSource(DidCommDiagnostics.ActivitySourceName));
/// </code>
/// </example>
public static class DidCommDiagnostics
{
    /// <summary>
    /// The exact name of the library's <see cref="ActivitySource"/> (NFR-05). Pass this to
    /// <c>TracerProviderBuilder.AddSource(...)</c> (or an <see cref="ActivityListener"/>'s
    /// <c>ShouldListenTo</c>) to receive the library's spans.
    /// </summary>
    public const string ActivitySourceName = "didcomm-dotnet";

    /// <summary>
    /// The exact name of the library's <see cref="System.Diagnostics.Metrics.Meter"/> (NFR-05). Pass
    /// this to <c>MeterProviderBuilder.AddMeter(...)</c> (or a <c>MeterListener</c>) to receive the
    /// library's metrics. When the host's container provides an <c>IMeterFactory</c> (ASP.NET Core
    /// does), the meter is created through it.
    /// </summary>
    /// <remarks>
    /// <para>The instruments, all with a closed, low-cardinality set of tag values and never a DID:</para>
    /// <list type="bullet">
    /// <item><description><c>didcomm.forward.outcomes</c> (counter, unit <c>{forward}</c>): one count per forward that
    /// reaches a routing mediator's forward handler (one refused earlier, at unpack, is not counted),
    /// tagged <c>didcomm.forward.outcome</c> with how it ended:
    /// <c>Delivered-Live</c>, <c>Queued</c>, <c>Dropped-Malformed</c>, <c>Dropped-NotEncrypted</c>,
    /// <c>Dropped-Expired</c>, <c>Dropped-Unknown</c>, <c>Dropped-Saturated</c>,
    /// <c>Dropped-Unacknowledged</c>, <c>Dropped-Shutdown</c>, <c>Dropped-TooLarge</c>,
    /// <c>Dropped-Quota</c> or <c>Faulted</c>.</description></item>
    /// <item><description><c>didcomm.mediator.direct_push.outcomes</c> (counter): one count per direct push a
    /// mediator attempts or skips, tagged <c>didcomm.push.outcome</c> (<c>Pushed</c>,
    /// <c>Push-Failed</c>, or a <c>Push-Skipped-</c> reason).</description></item>
    /// <item><description><c>didcomm.mediator.queue.purged</c> (counter): expired and aged-out messages
    /// removed from the pickup queue by the periodic purge.</description></item>
    /// <item><description><c>didcomm.replay.saturated</c> and <c>didcomm.replay.clock_behind_refusals</c>
    /// (observable counters): replay-guard refusals because the guard was full, or because the
    /// host clock was far behind (only for the in-memory guard).</description></item>
    /// <item><description><c>didcomm.return_route.dropped_notifications</c> (observable counter): session
    /// lifecycle notifications dropped under backpressure (only for the in-memory session
    /// registry).</description></item>
    /// <item><description><c>didcomm.mediator.queue.messages</c> and <c>didcomm.mediator.queue.bytes</c>
    /// (observable gauges): what the in-memory pickup store currently holds.</description></item>
    /// </list>
    /// <para>The <c>didcomm.mediator.*</c>, replay and session instruments are published by a
    /// mediator host (<c>AddMediator()</c>).</para>
    /// </remarks>
    public const string MeterName = "didcomm-dotnet";

    /// <summary>The single source every didcomm-dotnet span is started from. Thread-safe (NFR-03).</summary>
    internal static readonly ActivitySource Source = new(ActivitySourceName);

    // --- Span names (bounded vocabulary; see class remarks) ---

    /// <summary>Span emitted around an encrypted pack (authcrypt or anoncrypt, FR-API-02).</summary>
    internal const string PackEncryptedActivity = "didcomm.pack.encrypted";

    /// <summary>Span emitted around a standalone signed pack (FR-API-02).</summary>
    internal const string PackSignedActivity = "didcomm.pack.signed";

    /// <summary>Span emitted around a plaintext pack (FR-API-02).</summary>
    internal const string PackPlaintextActivity = "didcomm.pack.plaintext";

    /// <summary>Span emitted around an unpack (FR-API-03).</summary>
    internal const string UnpackActivity = "didcomm.unpack";

    /// <summary>Span emitted around a transport-layer send (FR-TRN-01).</summary>
    internal const string SendActivity = "didcomm.send";

    /// <summary>Span emitted around a DID resolution round-trip (FR-DID-01..05).</summary>
    internal const string ResolveActivity = "didcomm.resolve";

    /// <summary>Span emitted around inbound protocol dispatch (FR-PROTO-03).</summary>
    internal const string HandleActivity = "didcomm.handle";

    // --- Attribute keys (values are header-level metadata only — NFR-04) ---

    /// <summary>The plaintext message's <c>type</c> URI — a header, never body content.</summary>
    internal const string MessageTypeTag = "didcomm.message.type";

    /// <summary>JOSE key-management / signature algorithm identifier (e.g. <c>ECDH-1PU+A256KW</c>, <c>EdDSA</c>).</summary>
    internal const string AlgTag = "didcomm.alg";

    /// <summary>JOSE content-encryption algorithm identifier (e.g. <c>A256CBC-HS512</c>).</summary>
    internal const string EncTag = "didcomm.enc";

    /// <summary>Recipient count — a count, never the recipient DIDs themselves.</summary>
    internal const string RecipientsTag = "didcomm.recipients";

    /// <summary>The DID method name only (e.g. <c>peer</c>) — never the full DID.</summary>
    internal const string DidMethodTag = "didcomm.did.method";

    /// <summary>Dispatch outcome (<see cref="DidComm.Protocols.DispatchResult"/> member name).</summary>
    internal const string DispatchResultTag = "didcomm.dispatch.result";

    /// <summary>Transport scheme handling the send (e.g. <c>https</c>, <c>wss</c>).</summary>
    internal const string TransportSchemeTag = "didcomm.transport.scheme";

    /// <summary>The failing exception's <em>type name</em> — a bounded vocabulary; never the message text.</summary>
    internal const string ErrorTypeTag = "error.type";

    /// <summary>
    /// Mark <paramref name="activity"/> failed. Records only the exception's type name (as the
    /// status description and the <c>error.type</c> tag) — exception <em>messages</em> can embed
    /// header contents and are deliberately excluded from span attributes (NFR-04).
    /// </summary>
    /// <param name="activity">The span to mark; no-op when sampling produced none.</param>
    /// <param name="exception">The exception that failed the operation.</param>
    internal static void RecordFailure(Activity? activity, Exception exception)
    {
        if (activity is null)
            return;
        var errorType = exception.GetType().Name;
        activity.SetStatus(ActivityStatusCode.Error, errorType);
        activity.SetTag(ErrorTypeTag, errorType);
    }
}
