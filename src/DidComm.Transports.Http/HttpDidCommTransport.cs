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
