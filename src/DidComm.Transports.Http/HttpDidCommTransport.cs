using System.Net;
using System.Net.Http.Headers;
using DidComm.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace DidComm.Transports.Http;

/// <summary>
/// HTTPS-flavored <see cref="IDidCommTransport"/> (PRD §9.2 / FR-TRN-04..08). Sends one packed
/// envelope per <c>HTTP POST</c>; treats any 2xx as success (FR-TRN-05); follows 307 redirects
/// only (FR-TRN-06) and refuses 301/308 + non-2xx with <see cref="TransportException"/>. The
/// underlying <see cref="HttpClient"/> chain carries a Polly resilience pipeline
/// (retry + circuit-breaker + timeout — FR-TRN-08) injected via
/// <see cref="HttpDidCommBuilderExtensions.UseHttpTransport"/>.
/// </summary>
/// <remarks>
/// Delivery-only (FR-TRN-03) unless the request asks for an in-band response
/// (<see cref="TransportRequest.MaxResponseBytes"/>, set by <c>DidCommClient.SendAsync</c> for a
/// return-route send, FR-RR-07): a final <c>200</c> with a DIDComm <c>Content-Type</c> then has its
/// body read, capped and time-bounded, into <see cref="TransportResult.ResponsePayload"/>. Any other
/// response body is never read.
/// </remarks>
/// <example>
/// Sends the packed envelope in <c>samples/04-MediatorAgent</c> and <c>samples/02-Cookbook</c> (section P).
/// </example>
public sealed class HttpDidCommTransport : IDidCommTransport
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client used by this transport.</summary>
    public const string HttpClientName = "didcomm";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpTransportOptions _options;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly ILogger<HttpDidCommTransport> _logger;

    /// <summary>Initialize the transport with a factory and bound options.</summary>
    /// <param name="httpClientFactory">Factory for the named <c>"didcomm"</c> HTTP client.</param>
    /// <param name="options">Bound <see cref="HttpTransportOptions"/>.</param>
    /// <param name="logger">Optional logger; pass <see cref="NullLogger{T}.Instance"/> outside DI.</param>
    public HttpDidCommTransport(
        IHttpClientFactory httpClientFactory,
        IOptions<HttpTransportOptions> options,
        ILogger<HttpDidCommTransport>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger ?? NullLogger<HttpDidCommTransport>.Instance;
        _pipeline = HttpResiliencePipelineFactory.Create(_options);
    }

    /// <inheritdoc />
    public string Scheme => "https";

    /// <inheritdoc />
    public bool CanHandle(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        foreach (var allowed in _options.AllowedSchemes)
        {
            if (string.Equals(endpoint.Scheme, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Endpoint);

        if (!CanHandle(request.Endpoint))
        {
            throw new TransportException(
                $"HttpDidCommTransport refuses scheme '{request.Endpoint.Scheme}'. Allowed schemes: [{string.Join(", ", _options.AllowedSchemes)}].",
                httpStatusCode: null,
                scheme: request.Endpoint.Scheme);
        }

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        var currentUri = request.Endpoint;

        for (var hop = 0; hop <= _options.MaxRedirectHops; hop++)
        {
            // Build a fresh HttpRequestMessage on every Polly attempt — the BCL HttpClient
            // refuses to re-send the same instance, so the retry loop would otherwise fail
            // on the second attempt with "request has already been sent".
            var uriForCallback = currentUri;
            var response = await _pipeline
                .ExecuteAsync(async token =>
                {
                    var httpRequest = BuildRequest(uriForCallback, request);
                    return await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                }, ct)
                .ConfigureAwait(false);

            try
            {
                var status = (int)response.StatusCode;
                if (status >= 200 && status < 300)
                {
                    _logger.LogDebug("HttpDidCommTransport POST {Uri} → {Status}", currentUri, status);
                    // FR-RR-07 (JC-3): the body is read only for a 200 with a DIDComm media type, and
                    // only when the request carries a cap, which the facade sets only for a send that
                    // asked for a return route. Otherwise the body is never touched.
                    if (status == 200
                        && request.MaxResponseBytes is int cap
                        && TryDidCommMediaType(response.Content.Headers.ContentType?.MediaType, out var canonical))
                    {
                        return await ReadCappedAsync(response.Content, cap, canonical, ct).ConfigureAwait(false);
                    }
                    return new TransportResult(Accepted: true, HttpStatusCode: status);
                }

                // FR-TRN-06: only the 307 (Temporary Redirect) is followed. 308 Permanent Redirect
                // and 301 Moved Permanently are explicitly refused — endpoint discovery happens at
                // the DID-resolution layer, not at HTTP redirect time.
                if (response.StatusCode == HttpStatusCode.TemporaryRedirect && response.Headers.Location is not null)
                {
                    currentUri = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(currentUri, response.Headers.Location);
                    if (!CanHandle(currentUri))
                    {
                        throw new TransportException(
                            $"HTTP 307 redirected to disallowed scheme '{currentUri.Scheme}'.",
                            httpStatusCode: status,
                            scheme: currentUri.Scheme);
                    }
                    continue;
                }

                throw new TransportException(
                    $"HTTP POST to '{currentUri}' failed with status {status} ({response.ReasonPhrase}). " +
                    (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.PermanentRedirect
                        ? "301/308 redirects are not followed per FR-TRN-06."
                        : "Non-2xx responses are surfaced after the retry budget is exhausted."),
                    httpStatusCode: status,
                    scheme: currentUri.Scheme);
            }
            finally
            {
                response.Dispose();
            }
        }

        throw new TransportException(
            $"HTTP send exceeded {_options.MaxRedirectHops} redirect hops starting from '{request.Endpoint}'.",
            httpStatusCode: 307,
            scheme: request.Endpoint.Scheme);
    }

    /// <summary>The 16 KiB unit a body of unknown (or large declared) length is read in.</summary>
    private const int ChunkSize = 16 * 1024;

    /// <summary>
    /// The largest declared <c>Content-Length</c> read into a buffer of exactly that size before any
    /// body byte arrived (ADV-04). A larger declared length is read like a body of unknown length,
    /// into a buffer that grows only as data arrives, so a peer cannot make the client commit up to
    /// the cap by declaring a length it never sends.
    /// </summary>
    private const int ExactSizeThreshold = 64 * 1024;

    /// <summary>The largest wait a <see cref="CancellationTokenSource"/> accepts.</summary>
    private static readonly TimeSpan MaxWaitBudget = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Why an in-band response body was discarded rather than returned (FR-RR-07).</summary>
    private enum DiscardReason
    {
        /// <summary>The body is longer than the request's cap.</summary>
        OverCap,

        /// <summary>The body did not arrive within <see cref="HttpTransportOptions.RequestTimeout"/>.</summary>
        ReadTimeout,

        /// <summary>
        /// Reading the body failed: a connection reset, a body shorter than its declared length, or
        /// any other fault of the response stream (a decompressing handler's corrupt data, for
        /// example).
        /// </summary>
        ReadFailed,
    }

    /// <summary>
    /// Read a <c>200</c> DIDComm response body of at most <paramref name="cap"/> bytes (FR-RR-07,
    /// JC-2). A declared <c>Content-Length</c> over the cap is discarded without reading a byte. A
    /// declared length of at most 64 KiB is read into a buffer of exactly that size. Any other body,
    /// of a larger declared length or of unknown length, is read in 16 KiB chunks into a buffer that
    /// grows only as data arrives (ADV-04): with a declared length, exactly that many bytes are read
    /// and fewer is a failed read; without one, reading stops as soon as the total exceeds the cap.
    /// So memory follows the bytes received, never the length a peer declares, and nothing scales
    /// with the body beyond the cap: peak memory is at most about twice the cap plus one chunk. The
    /// read is bounded by <see cref="HttpTransportOptions.RequestTimeout"/> (the resilience
    /// pipeline's timeout ends with the headers). A body that is over the cap, late or unreadable
    /// (whatever the response stream throws) yields an accepted result with
    /// <see cref="TransportResult.ResponseDiscarded"/> and one Warning naming the reason and, for a
    /// failed read, the exception's type; the caller's own cancellation propagates.
    /// </summary>
    /// <param name="content">The response content.</param>
    /// <param name="cap">The most bytes to accept.</param>
    /// <param name="canonical">The canonical DIDComm media type the response matched.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    private async Task<TransportResult> ReadCappedAsync(HttpContent content, int cap, string canonical, CancellationToken ct)
    {
        var declared = content.Headers.ContentLength;
        if (declared > cap)
            return Discarded(DiscardReason.OverCap, cap);
        if (declared == 0)
            return new TransportResult(Accepted: true, HttpStatusCode: 200);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(AsWaitBudget(_options.RequestTimeout));
        byte[]? chunk = null;
        try
        {
            var stream = await content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                byte[] body;
                if (declared is long length && length <= ExactSizeThreshold)
                {
                    body = new byte[length];
                    var read = 0;
                    while (read < body.Length)
                    {
                        var n = await stream.ReadAsync(body.AsMemory(read), budget.Token).ConfigureAwait(false);
                        if (n == 0)
                            return Discarded(DiscardReason.ReadFailed, cap);
                        read += n;
                    }
                }
                else
                {
                    // ADV-04: a large declared length commits nothing up front. With a declared
                    // length (at most the cap, checked above) exactly that many bytes are read, as
                    // on the exact-size path; without one, up to the cap plus one chunk.
                    var limit = declared ?? cap;
                    chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize);
                    using var buffer = new MemoryStream();
                    long total = 0;
                    while (declared is null || total < limit)
                    {
                        var want = declared is null ? ChunkSize : (int)Math.Min(ChunkSize, limit - total);
                        var n = await stream.ReadAsync(chunk.AsMemory(0, want), budget.Token).ConfigureAwait(false);
                        if (n == 0)
                            break;
                        total += n;
                        if (total > cap)
                            return Discarded(DiscardReason.OverCap, cap);
                        // Grow geometrically, but never past the declared length or the cap.
                        if (buffer.Capacity < total)
                            buffer.Capacity = (int)Math.Min(limit, Math.Max(total, 2L * buffer.Capacity));
                        buffer.Write(chunk, 0, n);
                    }
                    if (declared is long expected && total != expected)
                        return Discarded(DiscardReason.ReadFailed, cap);
                    if (total == 0)
                        return new TransportResult(Accepted: true, HttpStatusCode: 200);
                    // When the buffer is exactly the body, hand it over instead of copying it: always
                    // so for a declared length, since growth is clamped to it.
                    body = buffer.Capacity == total ? buffer.GetBuffer() : buffer.ToArray();
                }

                return new TransportResult(Accepted: true, HttpStatusCode: 200)
                {
                    ResponsePayload = body,
                    ResponseMediaType = canonical,
                };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller's own cancellation is not a discard: it propagates, first.
            throw;
        }
        catch (OperationCanceledException)
        {
            return Discarded(DiscardReason.ReadTimeout, cap);
        }
        catch (Exception ex)
        {
            // QA-F03: whatever else the response stream throws — an IOException or
            // HttpRequestException from the connection, an InvalidDataException from a GZip/Deflate
            // decompressing handler, an InvalidOperationException from Brotli — is an unreadable
            // body, never an exception escaping the transport. Logged by its type name only.
            return Discarded(DiscardReason.ReadFailed, cap, ex.GetType().Name);
        }
        finally
        {
            if (chunk is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>
    /// The accepted-but-discarded result, plus one Warning naming the reason, the limit and, for a
    /// failed read, the exception's type name only: never the URL, any body bytes, the exception's
    /// message or the exception object. A throwing logging provider changes nothing.
    /// </summary>
    /// <param name="reason">Why the body was discarded.</param>
    /// <param name="cap">The request's cap.</param>
    /// <param name="exceptionType">The type name of the exception that failed the read, if one did.</param>
    private TransportResult Discarded(DiscardReason reason, int cap, string? exceptionType = null)
    {
        try
        {
            if (exceptionType is null)
                _logger.LogWarning("HTTP 200 DIDComm response discarded: {Reason} (limit {MaxBytes} bytes, FR-RR-07).", reason.ToString(), cap);
            else
                _logger.LogWarning("HTTP 200 DIDComm response discarded: {Reason} ({ExceptionType}; limit {MaxBytes} bytes, FR-RR-07).", reason.ToString(), exceptionType, cap);
        }
        catch
        {
            // Logging providers are host code; they must not change the send's outcome.
        }
        return new TransportResult(Accepted: true, HttpStatusCode: 200) { ResponseDiscarded = true };
    }

    /// <summary>
    /// Match a response <c>Content-Type</c> media type (parameters already stripped) against the
    /// three DIDComm media types (FR-MSG-06 normalization); <paramref name="canonical"/> is the
    /// matching constant.
    /// </summary>
    /// <param name="mediaType">The response's media type, or <c>null</c>.</param>
    /// <param name="canonical">The matching canonical constant.</param>
    private static bool TryDidCommMediaType(string? mediaType, out string canonical)
    {
        foreach (var candidate in DidCommMediaTypes)
        {
            if (Messages.MediaTypes.Matches(mediaType, candidate))
            {
                canonical = candidate;
                return true;
            }
        }
        canonical = string.Empty;
        return false;
    }

    private static readonly string[] DidCommMediaTypes =
    {
        Messages.MediaTypes.Encrypted,
        Messages.MediaTypes.Signed,
        Messages.MediaTypes.Plaintext,
    };

    /// <summary>
    /// Clamp a configured timeout into a legal, finite <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// value: below zero (including <see cref="Timeout.InfiniteTimeSpan"/>) maps to zero, above
    /// <see cref="int.MaxValue"/> ms to that maximum.
    /// </summary>
    /// <param name="value">The configured timeout.</param>
    private static TimeSpan AsWaitBudget(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero
        : value > MaxWaitBudget ? MaxWaitBudget
        : value;

    private static HttpRequestMessage BuildRequest(Uri uri, TransportRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            // ByteArrayContent is rewindable, so the Polly retry layer can replay it without
            // the caller re-reading the source stream.
            Content = new ByteArrayContent(request.Payload.ToArray()),
        };
        // FR-TRN-02: carry the IANA media type of the envelope on Content-Type.
        httpRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        return httpRequest;
    }
}
