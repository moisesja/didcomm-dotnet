using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.InteropTests.Samples;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DidComm.InteropTests.Transports.Proxy.HttpTransportHarness;

namespace DidComm.InteropTests.Transports.Proxy.Adversarial;

/// <summary>
/// Adversarial probes: host configuration of the <c>"didcomm"</c> named client through the
/// <see cref="IHttpClientFactory"/> builder APIs that are meant to <em>tweak</em> the primary handler
/// (<c>UseSocketsHttpHandler</c>, <c>ConfigureHttpClientDefaults</c>), with and without
/// <see cref="HttpTransportOptions.TrustedProxy"/>. Without a proxy the transport's primary handler is
/// a <see cref="SocketsHttpHandler"/>, so a tweak keeps the connect-time guard; with one it is a
/// <c>DelegatingHandler</c>, so <c>UseSocketsHttpHandler</c> replaces it with a fresh handler.
/// </summary>
/// <remarks>
/// The "victim" is a loopback listener standing in for an internal service; reaching it is the
/// SSRF. Runs in the non-parallel <see cref="DefaultProxyCollection"/> because one case sets the
/// process-wide proxy.
/// </remarks>
[Collection(nameof(DefaultProxyCollection))]
public sealed class HttpClientFactoryInterplayProbes
{
    private static Func<StandInConnection, Task> Accept202 => async c =>
    {
        if (c.RequestLine.StartsWith("POST ", StringComparison.Ordinal))
            await c.Stream.ReadExactlyAsync(new byte[1], c.Stopping);
        await c.StatusAsync("202 Accepted", "Connection: close");
    };

    private static void Tweak(SocketsHttpHandler handler, IServiceProvider _) =>
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    // ---- control: without TrustedProxy a tweak keeps the guard --------------------------------

    [Fact]
    public async Task Control_UseSocketsHttpHandler_tweak_without_TrustedProxy_keeps_the_guard()
    {
        await using var victim = new StandInProxy(Accept202);
        await using var sp = Build(
            more: s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).UseSocketsHttpHandler(Tweak));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await SendExpectingFailureAsync(transport, $"http://127.0.0.1:{victim.Port}/inbox");

        victim.Accepted.Should().Be(0, "the guard pinned the connection ({0})", Describe(thrown));
    }

    // ---- ADV-P1: with TrustedProxy the same tweak replaces the guarded handler ----------------

    [Fact]
    public async Task UseSocketsHttpHandler_tweak_with_TrustedProxy_keeps_the_destination_check()
    {
        await using var proxy = new StandInProxy(Accept202);
        await using var victim = new StandInProxy(Accept202);
        await using var sp = Build(
            o => o.TrustedProxy = new WebProxy(proxy.Address()),
            s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).UseSocketsHttpHandler(Tweak));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        Exception? thrown = null;
        TransportResult? result = null;
        try
        {
            result = await SendAsync(transport, $"http://127.0.0.1:{victim.Port}/inbox");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        victim.Accepted.Should().Be(0,
            "a loopback destination must be refused (result: {0}; thrown: {1}; victim saw [{2}]; proxy saw [{3}])",
            result?.HttpStatusCode, thrown is null ? "none" : Describe(thrown), string.Join("; ", victim.Requests), string.Join("; ", proxy.Requests));
        thrown.Should().BeOfType<TransportException>();
    }

    [Fact]
    public async Task UseSocketsHttpHandler_tweak_with_TrustedProxy_never_brings_back_the_process_proxy()
    {
        await using var trusted = new StandInProxy(Accept202);
        await using var rogue = new StandInProxy(Accept202);
        using var scope = ProcessProxyScope.FromDefaultProxy(rogue.Address());
        ProcessProxyScope.AssertProxies(new Uri($"http://{PublicHost}/inbox"), rogue.Address());
        await using var sp = Build(
            o => o.TrustedProxy = new WebProxy(trusted.Address()),
            s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).UseSocketsHttpHandler(Tweak));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        try
        {
            await SendAsync(transport, $"http://{PublicHost}/inbox");
        }
        catch (Exception)
        {
            // Only where the request went matters.
        }

        rogue.Accepted.Should().Be(0,
            "the process proxy must never be used (rogue saw [{0}]; trusted saw [{1}])",
            string.Join("; ", rogue.Requests), string.Join("; ", trusted.Requests));
    }

    // ---- defaults registered after UseHttpTransport -------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigureHttpClientDefaults_UseSocketsHttpHandler_keeps_the_guard(bool trustedProxy)
    {
        await using var proxy = new StandInProxy(Accept202);
        await using var victim = new StandInProxy(Accept202);
        await using var sp = Build(
            o =>
            {
                if (trustedProxy)
                    o.TrustedProxy = new WebProxy(proxy.Address());
            },
            s => s.ConfigureHttpClientDefaults(b => b.UseSocketsHttpHandler(Tweak)));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await SendExpectingFailureAsync(transport, $"http://127.0.0.1:{victim.Port}/inbox");

        victim.Accepted.Should().Be(0, Describe(thrown));
        proxy.Accepted.Should().Be(0, Describe(thrown));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigureHttpClientDefaults_primary_handler_after_UseHttpTransport_does_not_win(bool trustedProxy)
    {
        await using var proxy = new StandInProxy(Accept202);
        await using var victim = new StandInProxy(Accept202);
        await using var sp = Build(
            o =>
            {
                if (trustedProxy)
                    o.TrustedProxy = new WebProxy(proxy.Address());
            },
            s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler())));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await SendExpectingFailureAsync(transport, $"http://127.0.0.1:{victim.Port}/inbox");

        victim.Accepted.Should().Be(0, Describe(thrown));
    }
}
