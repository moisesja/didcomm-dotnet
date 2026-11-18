using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Transports;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Http = DidComm.InteropTests.Transports.Proxy.HttpTransportHarness;
using Ws = DidComm.InteropTests.Transports.Proxy.WebSocketProxyTestKit;

namespace DidComm.InteropTests.Transports.Proxy.Adversarial;

/// <summary>
/// ADV-P2: a host spelled with characters that IDNA (UTS #46) maps to ASCII digits and dots —
/// ideographic / full-width / half-width full stops, full-width or circled digits, a superscript
/// one, a zero-width space — is a DNS name to <see cref="Uri.DnsSafeHost"/> (what the guard's
/// <c>Validate</c> classifies) but an IPv4 literal in <see cref="Uri.IdnHost"/> (what the HTTP stack
/// puts on the wire: the absolute-form request line, the CONNECT authority). With a TrustedProxy
/// and <c>ResolveDnsNames = false</c> the documented promise is that IP literals are still checked;
/// these reach the proxy as <c>127.0.0.1</c> / <c>169.254.169.254</c>.
/// </summary>
/// <remarks>
/// Each case asserts the secure outcome (the proxy is never asked), so a failing case is the bug.
/// Without a proxy the connect-time pin sees the IdnHost and refuses it (the direct control).
/// </remarks>
public sealed class IdnMappedLiteralProbes(ITestOutputHelper output)
{
    private static Func<StandInConnection, Task> Accept202 => async c =>
    {
        if (c.RequestLine.StartsWith("POST ", StringComparison.Ordinal))
            await c.Stream.ReadExactlyAsync(new byte[1], c.Stopping);
        await c.StatusAsync("202 Accepted", "Connection: close");
    };

    public static TheoryData<string> Hosts => new()
    {
        "127。0。0。1",                 // ideographic full stop
        "127．0．0．1",                 // full-width full stop
        "127｡0｡0｡1",                 // half-width ideographic full stop
        "１２７.0.0.1",                 // full-width digits
        "①②⑦.0.0.1",                 // circled digits
        "127.0.0.¹",                           // superscript one
        "127.0.0.1​",                          // zero-width space
        "169。254。169。254",           // cloud metadata
        "10。0。0。1",                  // RFC 1918
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Http_idn_mapped_ip_literal_never_reaches_the_trusted_proxy(string host)
    {
        var uri = new Uri($"http://{host}/inbox");
        await using var proxy = new StandInProxy(Accept202);
        await using var sp = Http.Build(o =>
        {
            o.TrustedProxy = new WebProxy(proxy.Address());
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false };
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        string outcome;
        try
        {
            var r = await Http.SendAsync(transport, uri.OriginalString);
            outcome = $"accepted {r.HttpStatusCode}";
        }
        catch (Exception ex)
        {
            outcome = Http.Describe(ex);
        }
        output.WriteLine($"DnsSafeHost='{uri.DnsSafeHost}' IdnHost='{uri.IdnHost}' -> {outcome}; proxy saw [{string.Join("; ", proxy.Requests)}]");

        proxy.Accepted.Should().Be(0, "IdnHost '{0}' is an IP literal on the wire (proxy saw [{1}])", uri.IdnHost, string.Join("; ", proxy.Requests));
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Https_idn_mapped_ip_literal_never_reaches_the_trusted_proxy(string host)
    {
        var uri = new Uri($"https://{host}/latest/meta-data");
        await using var proxy = new StandInProxy(c => c.StatusAsync("403 Forbidden", "Connection: close"));
        await using var sp = Http.Build(o =>
        {
            o.TrustedProxy = new WebProxy(proxy.Address());
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false };
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await Http.SendExpectingFailureAsync(transport, uri.OriginalString);
        output.WriteLine($"IdnHost='{uri.IdnHost}' -> {Http.Describe(thrown)}; proxy saw [{string.Join("; ", proxy.Requests)}]");

        proxy.Accepted.Should().Be(0, "proxy saw [{0}]", string.Join("; ", proxy.Requests));
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Ws_idn_mapped_ip_literal_never_reaches_the_trusted_proxy(string host)
    {
        var uri = new Uri($"ws://{host}/x");
        await using var proxy = new StandInProxy(c => c.StatusAsync("502 Bad Gateway"));
        var options = Ws.DefaultPathOptions(new OutboundEndpointPolicy { ResolveDnsNames = false }, 0, new StrongBox<int>());
        options.TrustedProxy = new WebProxy(proxy.Address());
        await using var transport = Ws.Transport(options);

        var thrown = await Ws.SendFailsAsync(transport, Ws.Request(uri));
        output.WriteLine($"IdnHost='{uri.IdnHost}' -> {Ws.Describe(thrown)}; proxy saw [{string.Join("; ", proxy.Requests)}]");

        proxy.Accepted.Should().Be(0, "proxy saw [{0}]", string.Join("; ", proxy.Requests));
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Control_direct_path_refuses_the_idn_mapped_literal_at_connect_time(string host)
    {
        // Loopback-mapped spellings only: a failing guard must never dial off the machine.
        if (new Uri($"http://{host}/").IdnHost != "127.0.0.1")
            return;
        await using var victim = new StandInProxy(Accept202);
        var uri = new Uri($"http://{host}:{victim.Port}/inbox");
        await using var sp = Http.Build(o => o.OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await Http.SendExpectingFailureAsync(transport, uri.OriginalString);
        output.WriteLine(Http.Describe(thrown));

        victim.Accepted.Should().Be(0);
    }

    [Fact]
    public void Guard_Validate_classifies_DnsSafeHost_not_the_wire_host()
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy { ResolveDnsNames = false });
        var act = () => guard.Validate(new Uri("https://169。254。169。254/latest/meta-data"));
        act.Should().Throw<TransportException>("the wire host is 169.254.169.254");
    }
}
