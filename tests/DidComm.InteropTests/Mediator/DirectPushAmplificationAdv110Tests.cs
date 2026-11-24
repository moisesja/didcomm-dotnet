using System.Net.Http;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Pipeline;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// Red-team pass 1 (#110, direct push), amplification: what one direct push can be made to put on
/// the wire by an enrolled recipient that controls its registered endpoint.
/// </summary>
public sealed class DirectPushAmplificationAdv110Tests
{
    // The start-up check looks only at the named client's primary handler. A host that gives every
    // HttpClient of the app a retrying handler — .NET Aspire's service defaults do exactly this:
    // ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler()), whose retry covers
    // POST and 5xx unless DisableForUnsafeHttpMethods() is called — keeps UseHttpTransport()'s primary
    // handler, so the check passes, and every direct push then goes through that retry (and through
    // that handler's circuit breaker, shared by the named client: AD-6). The stand-in below retries a
    // 5xx three times, as the standard handler does by default.
    // Fixed: a direct push sends through the named client's innermost handler only, so a handler in
    // front of it (this retry, a breaker, logging, authentication) never applies to a push.
    [Fact]
    public async Task A_retrying_handler_every_client_gets_is_refused_at_start_up_or_never_repeats_a_push()
    {
        await using var endpoint = await LoopbackEndpoint.StartAsync(503);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport(o => o.AllowedSchemes = new[] { "http", "https" });
            b.Configure(o => o.OutboundEndpointPolicy = new OutboundEndpointPolicy { AllowedHosts = { "127.0.0.1" } });
        });
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler(() => new RetryTransientHandler(retries: 3)));
        await using var provider = services.BuildServiceProvider();

        DirectPushSender sender;
        try
        {
            sender = DirectPushSender.Create(provider);
        }
        catch (InvalidOperationException)
        {
            return; // refused at start-up: the guarantee holds
        }
        var delivered = await sender.SendAsync(endpoint.At("/inbox"), DirectPushHost.Payload, CancellationToken.None).WaitAsync(DirectPushHost.HangGuard);

        delivered.Should().NotBe(DirectSendResult.Delivered);
        endpoint.Posts.Should().Be(1, "a direct push is one attempt, never retried (D-6, D-7)");
    }

    /// <summary>Retries a 5xx answer, as the standard resilience handler's retry does by default.</summary>
    private sealed class RetryTransientHandler(int retries) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                var response = await base.SendAsync(request, cancellationToken);
                if ((int)response.StatusCode < 500 || attempt == retries)
                    return response;
                response.Dispose();
            }
        }
    }
}
