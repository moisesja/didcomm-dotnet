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
/// Adversarial probes: with a TrustedProxy and <c>ResolveDnsNames = false</c> only IP literals are
/// checked locally, so every spelling a proxy could read as a blocked IP must be caught as a literal.
/// Also pins what a bypassed destination that names the proxy itself does.
/// </summary>
public sealed class ProxiedLiteralProbes(ITestOutputHelper output)
{
    private static Func<StandInConnection, Task> Accept202 => async c =>
    {
        if (c.RequestLine.StartsWith("POST ", StringComparison.Ordinal))
            await c.Stream.ReadExactlyAsync(new byte[1], c.Stopping);
        await c.StatusAsync("202 Accepted", "Connection: close");
    };

    public static TheoryData<string> Spellings => new()
    {
        "2130706433", "0x7f000001", "0x7F000001", "017700000001", "0177.0.0.1", "0177.1", "127.1", "127.0.1",
        "0x7f.0.0.1", "0x7f.1", "127.000.000.001", "127.0.0.1.", "0", "0.0.0.0", "[::]", "[::1]",
        "[::ffff:7f00:1]", "[::ffff:127.0.0.1]", "[0:0:0:0:0:ffff:127.0.0.1]", "[::ffff:0:7f00:1]",
        "[2002:7f00:1::]", "[64:ff9b::7f00:1]", "[fe80::1%25lo0]", "[fd00::1]",
        "169.254.169.254", "0xa9fea9fe", "2852039166", "0251.0376.0251.0376", "169.254.43518",
        "10.1", "167772161", "0xa.1", "192.168.1", "100.64.0.1", "224.0.0.1", "255.255.255.255",
        "user@127.0.0.1", "203.0.113.10@127.0.0.1", "LOCALHOST", "127.0.0.08", "0x7g.0.0.1",
    };

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task Http_proxied_literal_spellings_never_reach_the_proxy(string host)
    {
        var uri = new Uri($"http://{host}/inbox");
        output.WriteLine($"Host={uri.Host} DnsSafeHost={uri.DnsSafeHost} IdnHost={uri.IdnHost} HostNameType={uri.HostNameType} IPAddress.TryParse={IPAddress.TryParse(uri.DnsSafeHost, out var a)} {a}");
        await using var proxy = new StandInProxy(Accept202);
        await using var sp = Http.Build(o =>
        {
            o.TrustedProxy = new WebProxy(proxy.Address());
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false };
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        try
        {
            await Http.SendAsync(transport, uri.OriginalString);
        }
        catch (Exception ex)
        {
            output.WriteLine(Http.Describe(ex));
        }
        output.WriteLine("proxy saw: " + string.Join("; ", proxy.Requests));

        // Names (not IPs) are the documented limit: only report literal-looking ones that got through.
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            proxy.Accepted.Should().Be(0, "an IP literal is checked before the proxy is asked");
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task Ws_proxied_literal_spellings_never_reach_the_proxy(string host)
    {
        var uri = new Uri($"ws://{host}/x");
        output.WriteLine($"Host={uri.Host} DnsSafeHost={uri.DnsSafeHost} IdnHost={uri.IdnHost} HostNameType={uri.HostNameType}");
        await using var proxy = new StandInProxy(c => c.StatusAsync("502 Bad Gateway"));
        var options = Ws.DefaultPathOptions(new OutboundEndpointPolicy { ResolveDnsNames = false }, 0, new StrongBox<int>());
        options.TrustedProxy = new WebProxy(proxy.Address());
        await using var transport = Ws.Transport(options);

        var thrown = await Ws.SendFailsAsync(transport, Ws.Request(uri));
        output.WriteLine(Ws.Describe(thrown));
        output.WriteLine("proxy saw: " + string.Join("; ", proxy.Requests));

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            proxy.Accepted.Should().Be(0, "an IP literal is checked before the proxy is asked");
    }

    // ---- a bypassed destination that names the proxy itself ---------------------------------

    [Fact]
    public async Task Pinned_a_bypassed_destination_naming_the_proxy_is_dialled_unfiltered()
    {
        // ResolveDnsNames off (documented for proxy-only DNS), proxy named by a loopback host name,
        // the same name on the bypass list. The destination 'is' the proxy's endpoint, so the
        // connect callback dials it without the private-address filter, and the proxy receives an
        // origin-form request directly (not a proxied absolute-form one).
        await using var proxy = new StandInProxy(Accept202);
        await using var sp = Http.Build(o =>
        {
            o.TrustedProxy = new WebProxy(proxy.Address(host: "localhost"), BypassOnLocal: false, BypassList: new[] { "localhost" });
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false };
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var result = await Http.SendAsync(transport, $"http://localhost:{proxy.Port}/squid-internal-mgr/menu");
        await proxy.WaitForRequestsAsync(1, Http.HangGuard);
        output.WriteLine("proxy saw: " + string.Join("; ", proxy.Requests));

        result.Accepted.Should().BeTrue();
        proxy.Requests.Should().Equal("POST /squid-internal-mgr/menu HTTP/1.1");
    }
}
