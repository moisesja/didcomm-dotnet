using System.Net;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace DidComm.Transports.Http;

/// <summary>
/// Builds the Polly resilience pipeline applied to every outbound HTTP request from
/// <see cref="HttpDidCommTransport"/> (FR-TRN-08). Order of strategies (outer-to-inner):
/// <list type="number">
///   <item>Retry — exponential backoff with jitter on 5xx + timeout (handles transient failures).</item>
///   <item>Circuit breaker — opens after N consecutive failures, half-open after the cooldown.</item>
///   <item>Timeout — caps each individual attempt at <see cref="HttpTransportOptions.RequestTimeout"/>.</item>
/// </list>
/// </summary>
internal static class HttpResiliencePipelineFactory
{
    internal static ResiliencePipeline<HttpResponseMessage> Create(HttpTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var circuit = new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(static r => IsTransient(r)),
            FailureRatio = 1.0,
            // Polly requires MinimumThroughput >= 2; clamp so a small configured threshold can't
            // throw a ValidationException at pipeline construction.
            MinimumThroughput = Math.Max(2, options.CircuitBreakerFailureThreshold),
            BreakDuration = options.CircuitBreakerOpenDuration,
            // SamplingDuration must be >= 500 ms (Polly invariant) and is the rolling window
            // the failure ratio + minimum throughput are evaluated over. Use the break duration
            // as a reasonable default.
            SamplingDuration = options.CircuitBreakerOpenDuration < TimeSpan.FromMilliseconds(500)
                ? TimeSpan.FromMilliseconds(500)
                : options.CircuitBreakerOpenDuration,
        };

        var timeout = new TimeoutStrategyOptions
        {
            Timeout = options.RequestTimeout,
        };

        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        if (options.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult(static r => IsTransient(r)),
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = options.RetryBaseDelay,
                UseJitter = true,
            });
        }
        builder.AddCircuitBreaker(circuit);
        builder.AddTimeout(timeout);
        return builder.Build();
    }

    private static bool IsTransient(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;
    }
}
