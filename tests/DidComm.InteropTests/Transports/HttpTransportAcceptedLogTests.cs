using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// The Debug line <see cref="HttpDidCommTransport"/> writes after a 2xx (#95; both defects were on
/// <c>main</c>): it names the scheme and the status, never the endpoint URI, which can be a secret
/// (a capability URL) or name a recipient (QA-2 defect 4, Q-9); and it is guarded, so a logging
/// provider that throws cannot turn a delivered send into a failure that a caller would retry and so
/// deliver twice (AD-7).
/// </summary>
public sealed class HttpTransportAcceptedLogTests
{
    private const string Secret = "capability-7f3a9c";
    private static readonly Uri Endpoint = new($"https://agents.example/inbox/{Secret}");

    [Fact]
    public async Task The_2xx_line_names_the_scheme_and_the_status_never_the_endpoint()
    {
        var logs = new HttpTransportResponseTests.ListLogger();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)));

        (await Build(handler, logs).SendAsync(Request(), default)).Accepted.Should().BeTrue();

        logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug)
            .Which.Text.Should().Be("HttpDidCommTransport POST over https → 202");
        logs.Entries.Should().NotContain(e => e.Text.Contains(Secret, StringComparison.Ordinal) || e.Text.Contains("agents.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throwing_logging_provider_never_turns_a_2xx_into_a_failure()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)));

        var result = await Build(handler, new ThrowingLogger()).SendAsync(Request(), default);

        result.Accepted.Should().BeTrue();
        handler.Requests.Should().HaveCount(1, "a delivered send is not re-sent because a log call threw");
    }

    [Fact]
    public async Task A_throwing_logging_provider_never_turns_a_200_with_an_in_band_reply_into_a_failure()
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x7b, 0x7d }) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/didcomm-encrypted+json");
            return Task.FromResult(response);
        });

        var result = await Build(handler, new ThrowingLogger()).SendAsync(Request() with { MaxResponseBytes = 1024 }, default);

        result.Accepted.Should().BeTrue();
        result.ResponsePayload!.Value.ToArray().Should().Equal(0x7b, 0x7d);
        handler.Requests.Should().HaveCount(1);
    }

    private static TransportRequest Request() => new(Endpoint, new byte[] { 0x7b, 0x7d }, "application/didcomm-encrypted+json");

    private static HttpDidCommTransport Build(Handler handler, ILogger<HttpDidCommTransport> logger)
    {
        var services = new ServiceCollection();
        services.AddOptions<HttpTransportOptions>().Configure(o =>
        {
            o.AllowedSchemes = new[] { "https" };
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            o.MaxRetryAttempts = 1;
            o.CircuitBreakerFailureThreshold = 100;
            o.RequestTimeout = TimeSpan.FromSeconds(5);
        });
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
