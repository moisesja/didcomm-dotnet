namespace DidComm.AspNetCore;

/// <summary>
/// Per-endpoint configuration for the <c>MapDidCommEndpoint</c> / <c>MapDidCommWebSocket</c>
/// extensions on <see cref="DidCommEndpointRouteBuilderExtensions"/>. Defaults match the
/// DIDComm v2.1 spec.
/// </summary>
public sealed class DidCommReceiveOptions
{
    /// <summary>
    /// Media types accepted by the endpoint. The HTTP receive path returns 415 when the request
    /// <c>Content-Type</c> does not match any of these. Defaults cover the three DIDComm v2.1
    /// envelope flavors.
    /// </summary>
    public IReadOnlyList<string> AcceptedMediaTypes { get; set; } = new[]
    {
        DidCommMediaTypes.Encrypted,
        DidCommMediaTypes.Signed,
        DidCommMediaTypes.Plain,
    };

    /// <summary>
    /// When the registry-aware <c>MapDidCommWebSocket</c> overload's dispatcher produces a
    /// reply (e.g. a Trust Ping response), should it be sent back over the SAME WebSocket the
    /// inbound message arrived on? Defaults to <c>false</c>: DIDComm is one-way per FR-TRN-10
    /// and replies travel out of band by default. Operators that want the in-band convenience
    /// (e.g. chat samples) set this to <c>true</c> per endpoint.
    /// </summary>
    public bool AllowSameSocketReplies { get; set; }

    /// <summary>
    /// Minimum wall-clock time the HTTP receive endpoint takes before returning the uniform
    /// <c>400</c> rejection. The handler times itself from entry and, before answering 400, waits
    /// out the remainder of this floor so the response time no longer reveals how far envelope
    /// processing got. This closes the recipient-kid timing side-channel (#35): without it, an
    /// envelope addressed to a recipient key the server holds runs the full ECDH/AEAD path (~360 µs)
    /// while one addressed to a key it does NOT hold fast-fails (~180 µs) — a single timed request
    /// then tells an attacker which recipient keys the agent holds, the same enumeration the uniform
    /// 400 body (#20) set out to remove. Padding every rejection up to a fixed floor removes that
    /// gap (FR-API-07).
    ///
    /// Applies to the 400 path only. A successfully received message answers 202 and is never
    /// padded (it stays full-throughput); the 415 / 413 pre-decrypt rejections carry a different
    /// status code and reveal nothing about which keys are held, so they are not padded either. The
    /// cost is therefore paid only on rejected — typically hostile or malformed — traffic. The floor
    /// is measured from just after the request body is read, so a peer cannot exhaust it by padding
    /// the body out to the size limit.
    ///
    /// Keep this small (single-digit milliseconds): 5 ms already dominates the local-crypto spread
    /// with margin, and the wait is backed by a timer (it holds no thread). Defaults to 5 ms; set
    /// <see cref="TimeSpan.Zero"/> to disable the floor entirely.
    ///
    /// <para><b>What this does and does not close.</b> The floor closes the cheap, universal probe —
    /// an unauthenticated peer sending garbage ciphertext to guessed recipient kids and timing the
    /// 400 to learn which keys the agent holds. It does NOT, on its own, close the held-only path
    /// where a decryptable envelope triggers network DID resolution that runs longer than the floor
    /// (an attacker-controlled slow sender DID makes the held response visibly exceed the floor while
    /// an unheld one stays at it). That residual is network-bound and unbounded; close it with
    /// authentication / a rate-limiter in front of the endpoint, not by inflating this value.</para>
    /// </summary>
    public TimeSpan ReceiveRejectionFloor { get; set; } = TimeSpan.FromMilliseconds(5);
}
