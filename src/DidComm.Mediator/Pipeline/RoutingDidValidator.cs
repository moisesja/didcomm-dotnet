using DidComm.Exceptions;
using DidComm.Mediator.Internal;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using Jwk = DataProofsDotnet.Jose.Jwk;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// The start-up check of FR-MED-07: each routing DID must be reachable the way a sender will reach
/// it. Senders resolve the routing DID, take the first <c>DIDCommMessaging</c> service entry as the
/// mediator's endpoint and encrypt the forward to its <c>keyAgreement</c> keys (FR-ROUTE-04), so the
/// mediator checks exactly that: a first entry whose <c>uri</c> is an absolute <c>http</c>,
/// <c>https</c>, <c>ws</c> or <c>wss</c> URI (never a DID, never the queue URI), and at least one
/// <c>keyAgreement</c> key whose private key the secrets resolver holds.
/// </summary>
/// <remarks>
/// A failure throws <see cref="InvalidOperationException"/> naming the DID's index in the list and,
/// for a resolution failure, the exception type: never the DID, the endpoint or an exception's text.
/// Resolution failure, an unsupported method and a check that takes longer than its bound all fail:
/// a DID the mediator cannot resolve is one its senders cannot resolve either. Routing keys on the
/// mediator's own first entry (the spec's SHOULD NOT; senders ignore them) and a DID-valued later
/// entry are reported as Warnings.
/// </remarks>
internal sealed class RoutingDidValidator
{
    /// <summary>How long the check of one routing DID may take.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IServiceEndpointResolver _serviceResolver;
    private readonly IDidKeyService _keyService;
    private readonly ISecretsResolver _secrets;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;

    /// <summary>Create the validator.</summary>
    /// <param name="serviceResolver">Resolves a DID's <c>DIDCommMessaging</c> service entries.</param>
    /// <param name="keyService">Resolves a DID's <c>keyAgreement</c> keys.</param>
    /// <param name="secrets">The host's private keys.</param>
    /// <param name="timeProvider">The clock behind the per-DID bound.</param>
    /// <param name="timeout">How long the check of one DID may take.</param>
    /// <param name="logger">Optional logger, for the Warnings.</param>
    public RoutingDidValidator(
        IServiceEndpointResolver serviceResolver,
        IDidKeyService keyService,
        ISecretsResolver secrets,
        TimeProvider timeProvider,
        TimeSpan timeout,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(serviceResolver);
        ArgumentNullException.ThrowIfNull(keyService);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _serviceResolver = serviceResolver;
        _keyService = keyService;
        _secrets = secrets;
        _time = timeProvider;
        _timeout = timeout;
        _logger = logger;
    }

    /// <summary>Check every routing DID, in order; the first failure ends the check.</summary>
    /// <param name="routingDids">The routing DIDs, each already a bare DID.</param>
    /// <param name="ct">Cancellation token (the host's start-up token).</param>
    /// <exception cref="InvalidOperationException">A routing DID fails the check; the message names its index.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task ValidateAsync(IReadOnlyList<string> routingDids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(routingDids);
        for (var index = 0; index < routingDids.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var check = CheckAsync(index, routingDids[index], ct);
            try
            {
                await check.WaitAsync(_timeout, _time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The abandoned check may still fault later; observe it so it is not reported as unobserved.
                _ = check.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw Refusal(index, $"its check did not finish within {_timeout.TotalSeconds:0.###} seconds (resolving it, or reading the secrets, is too slow or hangs)");
            }
        }
    }

    private async Task CheckAsync(int index, string did, CancellationToken ct)
    {
        IReadOnlyList<DidCommServiceInfo>? services;
        try
        {
            services = await _serviceResolver.ResolveAsync(did, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw Unresolvable(index, ex);
        }

        if (services is null || services.Count == 0)
            throw Refusal(index, "its DID document has no DIDCommMessaging service, so senders cannot reach the mediator");

        var first = services[0];
        if (first is null || string.IsNullOrEmpty(first.Uri))
            throw Refusal(index, "its first DIDCommMessaging service has no uri");
        if (first.Uri.StartsWith("did:", StringComparison.Ordinal))
            throw Refusal(index, "its first DIDCommMessaging service names a DID as its uri; a mediator's own service must name a transport URI (http, https, ws or wss), because senders do not resolve a mediator's endpoint recursively (FR-ROUTE-04)");
        if (QueueTransport.IsQueueTransport(first.Uri)
            || !Uri.TryCreate(first.Uri, UriKind.Absolute, out var endpoint)
            || !IsTransportScheme(endpoint.Scheme))
        {
            throw Refusal(index, "its first DIDCommMessaging service uri is not an absolute http, https, ws or wss URI");
        }

        if (first.RoutingKeys is { Count: > 0 })
        {
            MediatorLog.Write(_logger, LogLevel.Warning,
                "Routing DID at index {Index}: its first DIDCommMessaging service lists routing keys. A mediator's own service should not (FR-MED-07); senders that reach the mediator through its DID ignore them.",
                index);
        }
        for (var i = 1; i < services.Count; i++)
        {
            if (services[i]?.Uri is { } other && other.StartsWith("did:", StringComparison.Ordinal))
            {
                MediatorLog.Write(_logger, LogLevel.Warning,
                    "Routing DID at index {Index}: DIDCommMessaging service entry {Entry} names a DID as its uri. A DID representing a mediator should not use alternative endpoints in its own DID document; senders skip it.",
                    index, i);
                break;
            }
        }

        IReadOnlyList<Jwk>? keys;
        try
        {
            keys = await _keyService.GetVerificationMethodsAsync(did, VerificationRelationship.KeyAgreement, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw Unresolvable(index, ex);
        }

        var kids = keys?.Select(k => k?.Kid).OfType<string>().Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        if (kids.Length == 0)
            throw Refusal(index, "its DID document has no keyAgreement key, so no sender can encrypt a forward to the mediator");

        IReadOnlyList<string>? present;
        try
        {
            present = await _secrets.FindPresentAsync(kids, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw Refusal(index, $"the secrets resolver failed with {ex.GetType().Name} while looking up its keyAgreement keys");
        }

        if (present is null || !present.Any(kid => kids.Contains(kid, StringComparer.Ordinal)))
            throw Refusal(index, "the secrets resolver holds the private key of none of its keyAgreement keys, so the mediator could not decrypt a forward sent to it");
    }

    private static bool IsTransportScheme(string scheme)
        => string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
           || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
           || string.Equals(scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase)
           || string.Equals(scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase);

    private static InvalidOperationException Unresolvable(int index, Exception ex)
    {
        var what = ex is UnsupportedDidMethodException ? "its DID method is not supported" : "it could not be resolved";
        // Type name only, and no inner exception: a resolution error's text names the DID.
        return Refusal(index, $"{what} ({ex.GetType().Name}); senders resolve the same DID, so the mediator would be unreachable");
    }

    private static InvalidOperationException Refusal(int index, string reason)
        => new($"AddMediator(): routing DID at index {index} is not usable (FR-MED-07): {reason}.");
}
