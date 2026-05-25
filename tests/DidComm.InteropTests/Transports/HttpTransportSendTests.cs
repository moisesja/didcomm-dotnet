using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using DidComm.Exceptions;
using DidComm.Transports;
using DidComm.Transports.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// Phase 5 Checkpoint B — covers <see cref="HttpDidCommTransport"/>'s FR-TRN-04..06/08
/// surface using a stubbed <see cref="HttpMessageHandler"/>. No network I/O; the resilience
/// pipeline ticks in test time.
/// </summary>
public sealed class HttpTransportSendTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task SendAsync_two_xx_marks_request_accepted(HttpStatusCode status)
    {
        var transport = BuildTransport(new StubHandler(_ => Respond(status)));

        var result = await transport.SendAsync(NewRequest(), default);

        result.Accepted.Should().BeTrue();
        result.HttpStatusCode.Should().Be((int)status);
    }

    [Fact]
    public async Task SendAsync_307_is_followed_to_final_2xx()
    {
        var hops = 0;
        var transport = BuildTransport(new StubHandler(req =>
        {
            hops++;
            if (req.RequestUri!.AbsolutePath == "/inbox")
                return Redirect(HttpStatusCode.TemporaryRedirect, "https://agents.r.us/v2/inbox");
            req.RequestUri.AbsoluteUri.Should().Be("https://agents.r.us/v2/inbox");
            return Respond(HttpStatusCode.Accepted);
        }));

        var result = await transport.SendAsync(NewRequest(), default);

        result.Accepted.Should().BeTrue();
        hops.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task SendAsync_301_and_308_are_refused(HttpStatusCode status)
    {
        var transport = BuildTransport(new StubHandler(_ => Redirect(status, "https://agents.r.us/v2/inbox")));

        var act = async () => await transport.SendAsync(NewRequest(), default);

        var ex = (await act.Should().ThrowAsync<TransportException>()).Which;
        ex.HttpStatusCode.Should().Be((int)status);
        ex.Message.Should().Contain("FR-TRN-06");
    }

    [Fact]
    public async Task SendAsync_500_retries_then_surfaces_TransportException()
    {
        var calls = 0;
        var transport = BuildTransport(
            new StubHandler(_ =>
            {
                calls++;
                return Respond(HttpStatusCode.InternalServerError);
            }),
            opts =>
            {
                opts.MaxRetryAttempts = 2;
                opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                opts.CircuitBreakerFailureThreshold = 100;
            });

        var act = async () => await transport.SendAsync(NewRequest(), default);

        var ex = (await act.Should().ThrowAsync<TransportException>()).Which;
        ex.HttpStatusCode.Should().Be(500);
        // MaxRetryAttempts = 2 → 1 initial + 2 retries = 3 total calls.
        calls.Should().Be(3);
    }

    [Fact]
    public async Task SendAsync_refuses_disallowed_scheme()
    {
        var transport = BuildTransport(
            new StubHandler(_ => Respond(HttpStatusCode.OK)),
            opts => opts.AllowedSchemes = new[] { "https" });

        var request = new TransportRequest(
            new Uri("http://agents.r.us/inbox"),
            new byte[] { 1 },
            "application/didcomm-encrypted+json");

        var act = async () => await transport.SendAsync(request, default);
        var ex = (await act.Should().ThrowAsync<TransportException>()).Which;
        ex.Scheme.Should().Be("http");
    }

    [Fact]
    public void CanHandle_returns_true_for_allowed_scheme_case_insensitively()
    {
        var transport = BuildTransport(new StubHandler(_ => Respond(HttpStatusCode.OK)));
        transport.CanHandle(new Uri("HTTPS://Agents.r.us/inbox")).Should().BeTrue();
        transport.CanHandle(new Uri("https://agents.r.us/inbox")).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_returns_false_for_disallowed_scheme()
    {
        var transport = BuildTransport(
            new StubHandler(_ => Respond(HttpStatusCode.OK)),
            opts => opts.AllowedSchemes = new[] { "https" });
        transport.CanHandle(new Uri("ws://agents.r.us/socket")).Should().BeFalse();
    }

    [Fact]
    public void Ctor_does_not_throw_when_circuit_breaker_threshold_below_polly_minimum()
    {
        // Polly's MinimumThroughput floor is 2; a configured threshold of 1 must be clamped, not
        // blow up the pipeline at construction.
        var act = () => BuildTransport(
            new StubHandler(_ => Respond(HttpStatusCode.OK)),
            opts => opts.CircuitBreakerFailureThreshold = 1);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task SendAsync_sets_content_type_from_request_media_type()
    {
        HttpRequestMessage? captured = null;
        var transport = BuildTransport(new StubHandler(req =>
        {
            captured = req;
            return Respond(HttpStatusCode.Accepted);
        }));

        await transport.SendAsync(NewRequest(), default);

        captured!.Content!.Headers.ContentType!.MediaType.Should().Be("application/didcomm-encrypted+json");
    }

    private static HttpDidCommTransport BuildTransport(
        HttpMessageHandler handler,
        Action<HttpTransportOptions>? configure = null)
    {
        var services = new ServiceCollection();
        var optionsBuilder = services.AddOptions<HttpTransportOptions>();
        // Tests need both scheme variants AND tight retry timing so the suite stays fast.
        optionsBuilder.Configure(opts =>
        {
            opts.AllowedSchemes = new[] { "https", "http" };
            opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            opts.MaxRetryAttempts = 1;
            opts.CircuitBreakerFailureThreshold = 100;
            opts.RequestTimeout = TimeSpan.FromSeconds(5);
        });
        if (configure is not null)
            optionsBuilder.Configure(configure);

        services.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => handler);

        var sp = services.BuildServiceProvider();
        return new HttpDidCommTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<HttpTransportOptions>>());
    }

    private static TransportRequest NewRequest() =>
        new(new Uri("https://agents.r.us/inbox"),
            new byte[] { 0x7b, 0x7d }, // "{}"
            "application/didcomm-encrypted+json");

    private static HttpResponseMessage Respond(HttpStatusCode status) => new(status);

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var resp = new HttpResponseMessage(status);
        resp.Headers.Location = new Uri(location);
        return resp;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) { _responder = responder; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
