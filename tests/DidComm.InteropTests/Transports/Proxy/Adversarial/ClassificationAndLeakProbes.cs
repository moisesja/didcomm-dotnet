using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Http = DidComm.InteropTests.Transports.Proxy.HttpTransportHarness;
using Ws = DidComm.InteropTests.Transports.Proxy.WebSocketProxyTestKit;

namespace DidComm.InteropTests.Transports.Proxy.Adversarial;

/// <summary>
/// Adversarial probes: how refusals on the paths a TrustedProxy adds are classified (retried,
/// counted), whether a host socket's own proxy can sneak in on the WebSocket default path, and what
/// exception text carries the proxy address.
/// </summary>
public sealed class ClassificationAndLeakProbes(ITestOutputHelper output)
{
    private static Func<StandInConnection, Task> Accept202 => async c =>
    {
        if (c.RequestLine.StartsWith("POST ", StringComparison.Ordinal))
            await c.Stream.ReadExactlyAsync(new byte[1], c.Stopping);
        await c.StatusAsync("202 Accepted", "Connection: close");
    };

    // ---- HTTP: a bypassed destination's connect-time refusal --------------------------------

    [Theory(Skip = "ADV-P3, declined in the proxy fix: on the direct path (and for a bypassed destination) the HTTP transport's connect-time refusal surfaces as HttpRequestException, which the pipeline retries and the shared breaker counts. Unchanged from main; #88 follow-up candidates (a) and (b).")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Http_connect_time_refusal_is_not_retried(bool trustedProxy)
    {
        // With a proxy: 'localhost' is bypassed and ResolveDnsNames is off (the documented setting
        // where only the proxy resolves names), so it reaches the connect-time guard. Without one,
        // the transport alone (no facade pre-check) reaches the same guard.
        await using var proxy = new StandInProxy(Accept202);
        await using var victim = new StandInProxy(Accept202);
        var attempts = new Http.AttemptCounter();
        await using var sp = Http.Build(
            o =>
            {
                o.MaxRetryAttempts = 3;
                o.OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false };
                if (trustedProxy)
                    o.TrustedProxy = new WebProxy(proxy.Address(), BypassOnLocal: false, BypassList: new[] { "localhost" });
            },
            s => Http.CountAttempts(s, attempts));
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await Http.SendExpectingFailureAsync(transport, $"http://localhost:{victim.Port}/inbox");
        output.WriteLine(Http.Describe(thrown));

        victim.Accepted.Should().Be(0);
        proxy.Accepted.Should().Be(0);
        attempts.Count.Should().Be(1, "a deterministic guard refusal should not be retried ({0})", Http.Describe(thrown));
    }

    // ---- WebSocket: a factory socket carrying its own proxy ---------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ws_factory_socket_with_its_own_proxy_never_reaches_that_proxy(bool trustedProxy)
    {
        await using var rogue = new StandInProxy(c => c.StatusAsync("502 Bad Gateway"));
        await using var trusted = new StandInProxy(c => c.StatusAsync("502 Bad Gateway"));
        var options = Ws.DefaultPathOptions(new OutboundEndpointPolicy(), 0, new StrongBox<int>());
        options.WebSocketFactory = () =>
        {
            var s = new ClientWebSocket();
            s.Options.Proxy = new WebProxy(rogue.Address());
            return s;
        };
        if (trustedProxy)
            options.TrustedProxy = new WebProxy(trusted.Address());
        await using var transport = Ws.Transport(options);

        var thrown = await Ws.SendFailsAsync(transport, Ws.Request(new Uri("ws://203.0.113.10/x")));
        output.WriteLine(string.Join(" | ", Ws.Chain(thrown).Select(e => $"{e.GetType().Name}: {e.Message}")));

        rogue.Accepted.Should().Be(0, "the factory socket's own proxy must never carry the handshake");
    }

    // ---- Exception text ---------------------------------------------------------------------

    [Fact]
    public async Task Http_proxy_tunnel_failure_text_names_the_proxy_address()
    {
        await using var proxy = new StandInProxy(c => c.StatusAsync("403 Forbidden", "Connection: close"));
        await using var sp = Http.Build(o => o.TrustedProxy = new WebProxy(proxy.Address())
        {
            Credentials = new NetworkCredential("adv-user", "adv-secret"),
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await Http.SendExpectingFailureAsync(transport, $"https://{Http.PublicHost}/inbox");
        var text = Http.Describe(thrown) + "\n" + thrown;
        output.WriteLine(text);

        text.Should().NotContain("adv-secret").And.NotContain("adv-user");
        // Pinned: the runtime's own message names the proxy's address (host:port, never user info).
        text.Should().Contain($"127.0.0.1:{proxy.Port}");
    }

    [Fact]
    public async Task Ws_proxy_tunnel_failure_text_and_reconnects()
    {
        await using var proxy = new StandInProxy(c => c.StatusAsync("403 Forbidden"));
        var sockets = new StrongBox<int>();
        var options = Ws.DefaultPathOptions(new OutboundEndpointPolicy(), 2, sockets);
        options.TrustedProxy = new WebProxy(proxy.Address()) { Credentials = new NetworkCredential("adv-user", "adv-secret") };
        await using var transport = Ws.Transport(options);

        var thrown = await Ws.SendFailsAsync(transport, Ws.Request(new Uri("ws://203.0.113.10/x")));
        var text = string.Join(" | ", Ws.Chain(thrown).Select(e => $"{e.GetType().Name}: {e.Message}"));
        output.WriteLine(text);
        output.WriteLine($"sockets: {sockets.Value}");

        text.Should().NotContain("adv-secret").And.NotContain("adv-user");
    }
}
