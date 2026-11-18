using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Xunit;
using HttpRoute = DidComm.Transports.Http.TrustedProxyRoute;
using WebSocketRoute = DidComm.Transports.WebSocket.TrustedProxyRoute;

namespace DidComm.InteropTests.Transports.Proxy;

/// <summary>
/// The HTTP and WebSocket transports each carry a copy of <c>TrustedProxyRoute</c> (same rules,
/// same messages; only the option name differs). Every case here runs against both copies and
/// asserts the same outcome, so a fix applied to one copy only fails the build's tests.
/// </summary>
public sealed class TrustedProxyRouteDriftTests
{
    private const string Option = "{option}";

    /// <summary>One copy of the class, behind the same shape.</summary>
    private sealed record Copy(
        string Name,
        string OptionName,
        Func<WebProxy?, (WebProxy Proxy, Func<DnsEndPoint, bool> IsProxyEndpoint)?> From,
        Func<SocketException, SocketException> WithDirectHint);

    private static readonly Copy[] Copies =
    {
        new("Http", HttpRoute.OptionName,
            p => HttpRoute.From(p) is { } r ? (r.Proxy, r.IsProxyEndpoint) : null,
            HttpRoute.WithDirectHint),
        new("WebSocket", WebSocketRoute.OptionName,
            p => WebSocketRoute.From(p) is { } r ? (r.Proxy, r.IsProxyEndpoint) : null,
            WebSocketRoute.WithDirectHint),
    };

    [Fact]
    public void Each_copy_names_its_own_option()
    {
        HttpRoute.OptionName.Should().Be("HttpTransportOptions.TrustedProxy");
        WebSocketRoute.OptionName.Should().Be("WebSocketTransportOptions.TrustedProxy");
    }

    public static TheoryData<string, string?> Validation => new()
    {
        // Accepted.
        { "http://127.0.0.1:3128", null },
        { "http://127.0.0.1:3128/", null },
        { "HTTP://127.0.0.1:3128", null },
        { "http://127.0.0.1", null },
        { "https://proxy.internal", null },
        { "http://[::1]:3128", null },
        { "socks4://proxy.internal:1080", null },
        { "socks4a://proxy.internal:1080", null },
        { "socks5://proxy.internal:1080", null },
        // Refused, each by its own rule, with the same message in both copies.
        { "no-address", $"{Option} has no Address. Set the proxy's address, for example new WebProxy(\"http://proxy.internal:3128\")." },
        { "relative", $"{Option} must have an absolute Address (scheme, host and port)." },
        { "ftp://127.0.0.1:2121", $"{Option} must use one of the schemes http, https, socks4, socks4a or socks5." },
        { "ws://127.0.0.1:3128", $"{Option} must use one of the schemes http, https, socks4, socks4a or socks5." },
        { "http://alice:s3cr3t-Pa55@127.0.0.1:3128", $"{Option} must not carry a user name or password in its Address. Set WebProxy.Credentials instead." },
        { "socks5:///", $"{Option} must name a host in its Address." },
        { "socks5://127.0.0.1", $"{Option} must name a port in its Address." },
        { "http://127.0.0.1:0", $"{Option} must name a port in its Address." },
        { "http://127.0.0.1:3128/proxy", $"{Option} must be a scheme, host and port only, with no path, query or fragment." },
        { "http://127.0.0.1:3128/?via=x", $"{Option} must be a scheme, host and port only, with no path, query or fragment." },
        { "http://127.0.0.1:3128/#x", $"{Option} must be a scheme, host and port only, with no path, query or fragment." },
        { "bypass-not-a-pattern", $"{Option}.BypassList contains an entry that is not a valid pattern." },
    };

    [Theory]
    [MemberData(nameof(Validation))]
    public void Both_copies_validate_identically(string proxy, string? expectedMessage)
    {
        var outcomes = Copies.Select(copy =>
        {
            try
            {
                var route = copy.From(WebSocketTrustedProxyTests.InvalidProxy(proxy));
                return route is null ? "null route" : "accepted";
            }
            catch (InvalidOperationException ex)
            {
                ex.ToString().Should().NotContain("s3cr3t-Pa55", "the {0} copy must never repeat the password", copy.Name);
                return ex.Message.Replace(copy.OptionName, Option, StringComparison.Ordinal);
            }
        }).ToArray();

        outcomes.Should().AllBe(expectedMessage ?? "accepted", "both copies must agree ({0})", string.Join(" / ", Copies.Select(c => c.Name)));
    }

    [Fact]
    public void Both_copies_return_no_route_for_no_proxy_and_a_detached_copy_for_a_proxy()
    {
        var credentials = new NetworkCredential("alice", "s3cr3t-Pa55");
        foreach (var copy in Copies)
        {
            copy.From(null).Should().BeNull("no proxy means direct connections ({0})", copy.Name);

            var configured = new WebProxy(new Uri("http://proxy.internal:3128"), true, new[] { @"\.corp$" }, credentials);
            var route = copy.From(configured)!.Value;

            route.Proxy.Should().NotBeSameAs(configured, "the {0} copy must not keep the host's instance", copy.Name);
            route.Proxy.Address.Should().Be(configured.Address);
            route.Proxy.BypassProxyOnLocal.Should().BeTrue();
            route.Proxy.BypassList.Should().Equal(@"\.corp$");
            route.Proxy.Credentials.Should().BeSameAs(credentials);

            configured.Address = new Uri("http://elsewhere.internal:3128");
            configured.BypassArrayList.Add("203");
            route.Proxy.Address.Should().Be(new Uri("http://proxy.internal:3128"), "later changes do not reach the {0} copy", copy.Name);
            route.Proxy.BypassList.Should().Equal(@"\.corp$");
        }
    }

    public static TheoryData<string, string, int, bool> Endpoints => new()
    {
        // An IPv4 literal proxy.
        { "http://127.0.0.1:3128", "127.0.0.1", 3128, true },
        { "http://127.0.0.1:3128", "127.0.0.1.", 3128, true },
        { "http://127.0.0.1:3128", "127.0.0.1", 3129, false },
        { "http://127.0.0.1:3128", "localhost", 3128, false },
        { "http://127.0.0.1:3128", "::ffff:127.0.0.1", 3128, false },
        { "http://127.0.0.1:3128", "[::ffff:127.0.0.1]", 3128, false },
        { "http://127.0.0.1:3128", "127.0.0.2", 3128, false },
        // An IPv6 literal proxy: with or without brackets, any spelling of the same address.
        { "http://[::1]:3128", "[::1]", 3128, true },
        { "http://[::1]:3128", "::1", 3128, true },
        { "http://[::1]:3128", "0:0:0:0:0:0:0:1", 3128, true },
        { "http://[::1]:3128", "[::1]", 3129, false },
        { "http://[::1]:3128", "127.0.0.1", 3128, false },
        // A named proxy: case and one trailing dot do not matter; anything else does.
        { "http://Proxy.Internal:3128", "proxy.internal", 3128, true },
        { "http://Proxy.Internal:3128", "PROXY.INTERNAL", 3128, true },
        { "http://Proxy.Internal:3128", "proxy.internal.", 3128, true },
        { "http://proxy.internal.:3128", "proxy.internal", 3128, true },
        { "http://proxy.internal.:3128", "Proxy.Internal.", 3128, true },
        { "http://Proxy.Internal:3128", "proxy.internal", 8080, false },
        { "http://Proxy.Internal:3128", "proxy.internal.evil", 3128, false },
        { "http://Proxy.Internal:3128", "evil.proxy.internal", 3128, false },
        { "http://Proxy.Internal:3128", "proxy.internal..", 3128, false },
        { "http://Proxy.Internal:3128", "10.0.0.1", 3128, false },
        // Default ports come from the scheme.
        { "http://127.0.0.1", "127.0.0.1", 80, true },
        { "https://proxy.internal", "proxy.internal", 443, true },
        { "https://proxy.internal", "proxy.internal", 80, false },
        { "socks5://127.0.0.1:1080", "127.0.0.1", 1080, true },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void Both_copies_recognise_the_proxy_endpoint_identically(string proxy, string host, int port, bool expected)
    {
        var verdicts = Copies.Select(copy => copy.From(new WebProxy(new Uri(proxy)))!.Value.IsProxyEndpoint(new DnsEndPoint(host, port))).ToArray();

        verdicts.Should().AllBeEquivalentTo(expected, "both copies must agree on whether {0}:{1} is the proxy {2}", host, port, proxy);
    }

    [Fact]
    public void Both_copies_word_the_direct_connect_hint_identically_and_keep_the_error_code()
    {
        var hints = Copies.Select(copy =>
        {
            var hinted = copy.WithDirectHint(new SocketException((int)SocketError.ConnectionRefused));
            hinted.SocketErrorCode.Should().Be(SocketError.ConnectionRefused, "the {0} copy keeps the code", copy.Name);
            return hinted.Message.Replace(copy.OptionName, Option, StringComparison.Ordinal);
        }).ToArray();

        hints.Distinct().Should().ContainSingle("both copies word the hint the same way");
        hints[0].Should().EndWith(
            ". This transport connects directly: it does not use HTTP_PROXY, HTTPS_PROXY, ALL_PROXY or the system proxy. " +
            $"To send through a proxy, set {Option}.");
    }
}
