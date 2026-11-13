using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// <see cref="TransportRequest.SingleAttempt"/> in <see cref="HttpDidCommTransport"/> (#95 AD-6): one
/// POST to exactly the endpoint given — no retry, no redirect followed (a 3xx is a failure), outside
/// the circuit breaker (neither counted nor refused) — while normal sends keep their retries,
/// redirects and breaker.
/// </summary>
public sealed class HttpTransportSingleAttemptTests
{
    private static readonly Uri Endpoint = new("https://push.example/inbox");

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task A_2xx_is_accepted_after_one_request(HttpStatusCode status)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var transport = Build(handler);

        var result = await transport.SendAsync(Single(), default);

        result.Accepted.Should().BeTrue();
        result.HttpStatusCode.Should().Be((int)status);
        handler.Requests.Should().ContainSingle().Which.Should().Be(Endpoint);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_failure_status_is_one_request_and_never_retried(HttpStatusCode status)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var transport = Build(handler, o => o.MaxRetryAttempts = 3);

        var failure = (await transport.Awaiting(t => t.SendAsync(Single(), default)).Should().ThrowAsync<TransportException>()).Which;

        failure.HttpStatusCode.Should().Be((int)status);
        handler.Requests.Should().HaveCount(1, "a single attempt is one request");
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    public async Task A_redirect_is_not_followed_and_not_accepted(HttpStatusCode status)
    {
        var handler = new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(request.RequestUri == Endpoint ? status : HttpStatusCode.Accepted);
            response.Headers.Location = new Uri("https://elsewhere.example/not-the-endpoint");
            return Task.FromResult(response);
        });
        var transport = Build(handler);

        var failure = (await transport.Awaiting(t => t.SendAsync(Single(), default)).Should().ThrowAsync<TransportException>()).Which;

        failure.HttpStatusCode.Should().Be((int)status);
        handler.Requests.Should().Equal(new[] { Endpoint }, "only the endpoint given is ever requested");
    }

    [Fact]
    public async Task Failing_single_attempts_never_open_the_breaker_for_other_sends()
    {
        var failing = true;
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(failing ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted)));
        var transport = Build(handler, o =>
        {
            o.MaxRetryAttempts = 0;
            o.CircuitBreakerFailureThreshold = 2;
        });

        for (var i = 0; i < 6; i++)
            await transport.Awaiting(t => t.SendAsync(Single(), default)).Should().ThrowAsync<TransportException>();
        failing = false;

        (await transport.SendAsync(Normal(), default)).Accepted.Should().BeTrue("six failed single attempts did not count against the breaker");
        handler.Requests.Should().HaveCount(7);
    }

    [Fact]
    public async Task An_open_breaker_does_not_refuse_a_single_attempt()
    {
        var failing = true;
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(failing ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted)));
        var transport = Build(handler, o =>
        {
            o.MaxRetryAttempts = 0;
            o.CircuitBreakerFailureThreshold = 2;
        });
        for (var i = 0; i < 2; i++)
            await transport.Awaiting(t => t.SendAsync(Normal(), default)).Should().ThrowAsync<TransportException>();
        failing = false;

        await transport.Awaiting(t => t.SendAsync(Normal(), default)).Should().ThrowAsync<BrokenCircuitException>("normal sends are refused while the breaker is open");
        var requestsBefore = handler.Requests.Count;

        (await transport.SendAsync(Single(), default)).Accepted.Should().BeTrue("a single attempt is outside the breaker");
        handler.Requests.Should().HaveCount(requestsBefore + 1);
    }

    [Fact]
    public async Task Normal_sends_keep_their_retries_and_their_redirects()
    {
        var calls = 0;
        var retrying = Build(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(Interlocked.Increment(ref calls) < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted))),
            o => o.MaxRetryAttempts = 3);
        (await retrying.SendAsync(Normal(), default)).Accepted.Should().BeTrue();
        calls.Should().Be(3);

        var redirecting = new Handler((request, _) =>
        {
            if (request.RequestUri == Endpoint)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("https://push.example/moved");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });
        (await Build(redirecting).SendAsync(Normal(), default)).Accepted.Should().BeTrue();
        redirecting.Requests.Should().Equal(Endpoint, new Uri("https://push.example/moved"));
    }

    [Fact]
    public async Task A_single_attempt_that_never_answers_ends_at_the_request_timeout_as_a_transport_failure()
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var transport = Build(handler, o => o.RequestTimeout = TimeSpan.FromMilliseconds(100));

        (await transport.Awaiting(t => t.SendAsync(Single(), default)).Should().ThrowAsync<TransportException>()).Which.HttpStatusCode.Should().BeNull();
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_cancelled_single_attempt_leaves_as_cancellation()
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var transport = Build(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await transport.Awaiting(t => t.SendAsync(Single(), cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_connection_failure_is_one_attempt_wrapped_as_a_transport_failure()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("connection refused by push.example"));
        var transport = Build(handler, o => o.MaxRetryAttempts = 3);

        (await transport.Awaiting(t => t.SendAsync(Single(), default)).Should().ThrowAsync<TransportException>()).Which.InnerException.Should().BeOfType<HttpRequestException>();
        handler.Requests.Should().HaveCount(1);
    }

    private static TransportRequest Normal() => new(Endpoint, new byte[] { 0x7b, 0x7d }, "application/didcomm-encrypted+json");

    private static TransportRequest Single() => Normal() with { SingleAttempt = true };

    private static HttpDidCommTransport Build(Handler handler, Action<HttpTransportOptions>? configure = null, ILogger<HttpDidCommTransport>? logger = null)
    {
        var services = new ServiceCollection();
        var options = services.AddOptions<HttpTransportOptions>();
        options.Configure(o =>
        {
            o.AllowedSchemes = new[] { "https" };
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            o.MaxRetryAttempts = 1;
            o.CircuitBreakerFailureThreshold = 100;
            o.RequestTimeout = TimeSpan.FromSeconds(5);
        });
        if (configure is not null)
            options.Configure(configure);
        services.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(_ => handler);
        var sp = services.BuildServiceProvider();
        return new HttpDidCommTransport(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IOptions<HttpTransportOptions>>(), logger);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Uri> _requests = new();

        public IReadOnlyCollection<Uri> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request.RequestUri!);
            return respond(request, cancellationToken);
        }
    }

    private sealed class ThrowingLogger : ILogger<HttpDidCommTransport>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("logging provider down");
    }
}
