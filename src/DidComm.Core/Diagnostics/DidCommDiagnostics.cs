using System.Diagnostics;

namespace DidComm.Diagnostics;

/// <summary>
/// Names the library's OpenTelemetry instrumentation (NFR-05). The library emits
/// <see cref="Activity"/> spans through a single <see cref="ActivitySource"/> named
/// <see cref="ActivitySourceName"/>; consumers subscribe to it from their own OpenTelemetry
/// (or plain <see cref="ActivityListener"/>) pipeline — the library itself references no
/// OpenTelemetry SDK and exports nothing on its own. When nothing listens, every
/// instrumentation point is a no-op (<c>StartActivity</c> returns <c>null</c>), so the
/// telemetry is zero-cost for hosts that never opt in.
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
