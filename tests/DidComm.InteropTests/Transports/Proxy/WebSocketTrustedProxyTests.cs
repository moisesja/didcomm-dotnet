using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Xunit;
using static DidComm.InteropTests.Transports.Proxy.WebSocketProxyTestKit;

namespace DidComm.InteropTests.Transports.Proxy;

/// <summary>
/// <see cref="WebSocketTransportOptions.TrustedProxy"/>: the named proxy carries every handshake
/// (first connects, reconnects, session sockets), is recognised however the handler spells its
/// host, and is dialled even on loopback; a forbidden destination is refused before the proxy is
/// asked, by the pre-send check and by the handshake check on its own; no redirect is followed
/// through the proxy; the value is checked and copied once; a custom <c>Connect</c> is unaffected.
/// </summary>
/// <remarks>
/// The stand-in proxy listens on 127.0.0.1 and the policy is the default (private networks
/// blocked, no allowlist): the proxy is reachable only because it is the named one. Destinations
/// are 203.0.113.10 (TEST-NET-3, public to the guard); nothing is dialled but the stand-in. No test
/// here touches the process proxy, so the class runs in parallel with the others.
/// </remarks>
public sealed class WebSocketTrustedProxyTests
{
    private static readonly Uri Public = new("ws://203.0.113.10/x");

    /// <summary>
    /// A stand-in that answers every request with 502. It answers rather than hanging up: the
    /// HTTP stack silently re-sends a request whose connection closed with no response, so a
    /// hang-up would be counted many times over within one attempt.
    /// </summary>
    private static StandInProxy Refusing(IPAddress? address = null) => new(c => c.StatusAsync("502 Bad Gateway"), address);

    /// <summary>Default-path options with the stand-in as the trusted proxy and the default policy.</summary>
    private static WebSocketTransportOptions Through(Uri proxy, int maxReconnectAttempts, StrongBox<int> sockets, OutboundEndpointPolicy? policy = null)
    {
        var options = DefaultPathOptions(policy ?? new OutboundEndpointPolicy(), maxReconnectAttempts, sockets);
        options.TrustedProxy = new WebProxy(proxy);
        return options;
    }

    // ---- The handshake goes to the named proxy ----

    [Theory]
    [InlineData("ws://203.0.113.10/x", "CONNECT 203.0.113.10:80 HTTP/1.1")]
    [InlineData("wss://203.0.113.10/x", "CONNECT 203.0.113.10:443 HTTP/1.1")]
    public async Task The_handshake_is_tunnelled_through_the_named_proxy_on_loopback(string destination, string expected)
    {
        await using var proxy = Refusing();
        await using var transport = Transport(Through(proxy.Address(), 0, new StrongBox<int>()));

        var thrown = await SendFailsAsync(transport, Request(new Uri(destination)));

        // The stand-in records the CONNECT before it closes the connection the send failed on.
        await proxy.WaitForRequestsAsync(1, HangGuard);
        proxy.Requests.Should().Equal(new[] { expected }, "the loopback proxy is dialled because it is the named one. Chain: {0}", Describe(thrown));
    }

    [Fact]
    public async Task A_send_through_the_named_proxy_is_delivered_over_the_tunnel()
    {
        var peer = new TunnelledWebSocketPeer();
        await using var proxy = new StandInProxy(peer.RespondAsync);
        await using (var transport = Transport(Through(proxy.Address(), 0, new StrongBox<int>())))
        {
            var result = await transport.SendAsync(Request(Public), default).WaitAsync(HangGuard);

            result.Accepted.Should().BeTrue();
            await peer.FirstMessage.WaitAsync(HangGuard);
            peer.Messages.Should().ContainSingle().Which.Should().Equal(Payload);
            proxy.Requests.Should().Equal("CONNECT 203.0.113.10:80 HTTP/1.1");
            peer.TunnelledRequests.Should().Equal("GET /x HTTP/1.1");
        }
    }

    [Fact]
    public async Task A_return_route_session_socket_also_goes_through_the_named_proxy()
    {
        var peer = new TunnelledWebSocketPeer();
        await using var proxy = new StandInProxy(peer.RespondAsync);
        await using (var transport = Transport(Through(proxy.Address(), 0, new StrongBox<int>())))
        {
            var request = Request(Public) with
            {
                ReturnRoute = ReturnRouteMode.All,
                LocalDid = "did:example:alice",
                PeerDid = "did:example:bob",
            };

            var result = await transport.SendAsync(request, default).WaitAsync(HangGuard);

            result.Accepted.Should().BeTrue();
            transport.ActiveSessionSocketCount.Should().Be(1, "the send opened a session socket, not the pooled one");
            await peer.FirstMessage.WaitAsync(HangGuard);
            peer.Messages.Should().ContainSingle().Which.Should().Equal(Payload);
            proxy.Requests.Should().Equal("CONNECT 203.0.113.10:80 HTTP/1.1");
        }
    }

    [Fact]
    public async Task Reconnects_go_through_the_named_proxy_like_first_connects()
    {
        await using var proxy = new StandInProxy(c => c.StatusAsync("403 Forbidden"));
        await using var transport = Transport(Through(proxy.Address(), 2, new StrongBox<int>()));

        var thrown = await SendFailsAsync(transport, Request(Public));

        // Three CONNECTs: the first attempt and both reconnects. Each is recorded before its 403.
        await proxy.WaitForRequestsAsync(3, HangGuard);
        proxy.Requests.Should().Equal(Enumerable.Repeat("CONNECT 203.0.113.10:80 HTTP/1.1", 3));
        thrown.Message.Should().Contain("exhausting the reconnect budget (2 attempt(s))", "a proxy's refusal is transient");
    }

    public static TheoryData<string, string> ProxySpellings => new()
    {
        { "http", "LOCALHOST." },
        { "http", "localhost" },
        { "HTTP", "127.0.0.1" },
        { "http", "127.0.0.1." },
        { "socks5", "127.0.0.1" },
        { "socks4", "127.0.0.1" },
        { "socks4a", "localhost" },
    };

    [Theory]
    [MemberData(nameof(ProxySpellings))]
    public async Task The_named_proxy_is_recognised_however_its_host_is_spelled(string scheme, string host)
    {
        await using var proxy = Refusing();
        await using var transport = Transport(Through(proxy.Address(scheme, host), 0, new StrongBox<int>()));

        var thrown = await SendFailsAsync(transport, Request(Public));

        await proxy.WaitForRequestsAsync(1, HangGuard);
        var expected = scheme.ToLowerInvariant() switch
        {
            "socks5" => "SOCKS5",
            "socks4" or "socks4a" => "SOCKS4",
            _ => "CONNECT 203.0.113.10:80 HTTP/1.1",
        };
        proxy.Requests.Should().Equal(new[] { expected }, "Chain: {0}", Describe(thrown));
    }

    [Fact]
    public async Task The_named_proxy_is_recognised_on_the_IPv6_loopback()
    {
        if (!HasIPv6Loopback())
            return; // No IPv6 loopback on this machine: nothing to prove here.

        await using var proxy = Refusing(IPAddress.IPv6Loopback);
        await using var transport = Transport(Through(new Uri($"http://[::1]:{proxy.Port}"), 0, new StrongBox<int>()));

        var thrown = await SendFailsAsync(transport, Request(Public));

        await proxy.WaitForRequestsAsync(1, HangGuard);
        proxy.Requests.Should().Equal(new[] { "CONNECT 203.0.113.10:80 HTTP/1.1" }, "Chain: {0}", Describe(thrown));
    }

    // ---- A forbidden destination is refused before the proxy is asked ----

    public static TheoryData<string> Forbidden => new()
    {
        "ws://10.0.0.1/x",
        "ws://169.254.169.254/latest/meta-data",
        "ws://[::ffff:127.0.0.1]/x",
        "ws://[64:ff9b::a9fe:a9fe]/x",
        "ws://127.0.0.1./x",
        "wss://10.0.0.1/x",
        "ws://localhost/x", // refused only while ResolveDnsNames is on (the default)
    };

    [Theory]
    [MemberData(nameof(Forbidden))]
    public async Task A_forbidden_destination_is_refused_before_any_connect_and_never_reaches_the_proxy(string destination)
    {
        await using var proxy = Refusing();
        var sockets = new StrongBox<int>();
        await using var transport = Transport(Through(proxy.Address(), 3, sockets));

        var thrown = await SendFailsAsync(transport, Request(new Uri(destination)));

        thrown.Should().BeOfType<TransportException>().Which.Message.Should().Contain("private or reserved");
        sockets.Value.Should().Be(0, "the pre-send check refuses before any socket is made, so no attempt is spent");
        proxy.Accepted.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Forbidden))]
    public async Task The_handshake_check_alone_refuses_a_forbidden_destination_before_the_proxy_is_asked(string destination)
    {
        // Isolate the handshake check from the pre-send check: the pre-send check runs only when no
        // custom Connect is set as the send starts. Here one is, so it is skipped; the delegate
        // clears itself and fails its attempt with a transient error, so the reconnect runs the
        // default connect, where only the handshake check stands between the request and the proxy.
        await using var proxy = Refusing();
        var sockets = new StrongBox<int>();
        var options = Through(proxy.Address(), 3, sockets);
        options.Connect = (_, _, _) =>
        {
            options.Connect = null;
            throw new WebSocketException("first attempt: hand the reconnect to the default connect");
        };
        await using var transport = Transport(options);

        var thrown = await SendFailsAsync(transport, Request(new Uri(destination)));

        proxy.Accepted.Should().Be(0, "the request was refused before it was handed to the proxy");
        sockets.Value.Should().Be(2, "the refusal on the reconnect is fatal: the other two attempts are not spent");
        thrown.Should().BeOfType<TransportException>().Which.Message.Should()
            .Contain("failed on attempt 2: the endpoint was refused before the proxy was asked (SSRF defense)")
            .And.Contain("permanently fatal");
        thrown.InnerException.Should().BeOfType<TransportException>().Which.Message.Should().Contain("private or reserved");
    }

    [Fact]
    public async Task A_destination_on_the_BypassList_is_connected_directly_with_the_full_connect_time_check()
    {
        // 'localhost' passes the pre-send and handshake checks (ResolveDnsNames = false). It is on
        // the proxy's bypass list, so it is dialled directly, where the connect-time pin refuses it.
        await using var proxy = Refusing();
        await using var listener = new StandInProxy();
        var sockets = new StrongBox<int>();
        var options = Through(proxy.Address(), 3, sockets, new OutboundEndpointPolicy { ResolveDnsNames = false });
        options.TrustedProxy!.BypassList = new[] { "localhost" };
        await using var transport = Transport(options);

        var thrown = await SendFailsAsync(transport, Request(new Uri($"ws://localhost:{listener.Port}/x")));

        listener.Accepted.Should().Be(0, "a bypassed destination gets the full connect-time check");
        proxy.Accepted.Should().Be(0, "a bypassed destination does not go to the proxy");
        sockets.Value.Should().Be(1, "the connect-time refusal is fatal");
        thrown.InnerException.Should().BeOfType<TransportException>()
            .Which.Message.Should().Contain($"Refusing to connect to 'localhost:{listener.Port}'");
    }

    // ---- Redirects ----

    [Fact]
    public async Task A_handshake_redirect_is_not_followed_through_the_named_proxy()
    {
        var tunnelled = new ConcurrentQueue<string>();
        await using var proxy = new StandInProxy(async c =>
        {
            if (!c.RequestLine.StartsWith("CONNECT ", StringComparison.Ordinal))
            {
                await c.StatusAsync("502 Bad Gateway");
                return;
            }
            await c.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n");
            if (await c.ReadNextRequestLineAsync() is not { } upgrade)
                return;
            tunnelled.Enqueue(upgrade);
            await c.StatusAsync("302 Found", "Location: http://10.0.0.1/");
        });
        await using var transport = Transport(Through(proxy.Address(), 0, new StrongBox<int>()));

        var thrown = await SendFailsAsync(transport, Request(Public));

        // The redirect would be a second request to the stand-in, recorded before the send failed.
        proxy.Requests.Should().Equal(new[] { "CONNECT 203.0.113.10:80 HTTP/1.1" }, "the 302 must not be followed. Chain: {0}", Describe(thrown));
        tunnelled.Should().Equal("GET /x HTTP/1.1");
        Chain(thrown).OfType<WebSocketException>().Should().NotBeEmpty("the 302 fails the handshake");
    }

    [Fact]
    public async Task A_handshake_redirect_is_still_followed_on_a_direct_connection()
    {
        // The contrast: directly, the connect callback pins each hop, so the redirect is followed.
        await using var target = Refusing();
        await using var origin = new StandInProxy(c => c.StatusAsync("302 Found", $"Location: http://127.0.0.1:{target.Port}/landed"));
        var policy = new OutboundEndpointPolicy();
        policy.AllowedHosts.Add("127.0.0.1");
        await using var transport = Transport(DefaultPathOptions(policy, 0, new StrongBox<int>()));

        var thrown = await SendFailsAsync(transport, Request(new Uri($"ws://127.0.0.1:{origin.Port}/x")));

        await target.WaitForRequestsAsync(1, HangGuard);
        origin.Requests.Should().Equal("GET /x HTTP/1.1");
        target.Requests.Should().Equal(new[] { "GET /landed HTTP/1.1" }, "Chain: {0}", Describe(thrown));
    }

    // ---- Checked and copied once ----

    [Fact]
    public async Task Changing_the_configured_WebProxy_after_construction_does_not_move_the_transport()
    {
        await using var original = Refusing();
        await using var moved = Refusing();
        var options = Through(original.Address(), 0, new StrongBox<int>());
        await using var transport = Transport(options);
        options.TrustedProxy!.Address = moved.Address();

        var thrown = await SendFailsAsync(transport, Request(Public));

        await original.WaitForRequestsAsync(1, HangGuard);
        original.Requests.Should().Equal(new[] { "CONNECT 203.0.113.10:80 HTTP/1.1" }, "Chain: {0}", Describe(thrown));
        moved.Accepted.Should().Be(0);
    }

    private const string Password = "s3cr3t-Pa55";

    public static TheoryData<string, string> InvalidProxies => new()
    {
        { "no-address", "has no Address" },
        { "relative", "must have an absolute Address" },
        { "ftp://127.0.0.1:2121", "must use one of the schemes http, https, socks4, socks4a or socks5" },
        { $"http://alice:{Password}@127.0.0.1:3128", "must not carry a user name or password in its Address" },
        { $"http://{Password}@127.0.0.1:3128", "must not carry a user name or password in its Address" },
        { "socks5:///", "must name a host in its Address" },
        { "socks5://127.0.0.1", "must name a port in its Address" },
        { "http://127.0.0.1:0", "must name a port in its Address" },
        { "http://127.0.0.1:3128/proxy", "must be a scheme, host and port only, with no path, query or fragment" },
        { "http://127.0.0.1:3128/?via=x", "must be a scheme, host and port only, with no path, query or fragment" },
        { "http://127.0.0.1:3128/#x", "must be a scheme, host and port only, with no path, query or fragment" },
        { "bypass-not-a-pattern", "BypassList contains an entry that is not a valid pattern" },
    };

    [Theory]
    [MemberData(nameof(InvalidProxies))]
    public void An_invalid_TrustedProxy_fails_construction_naming_the_setting_and_never_the_password(string proxy, string rule)
    {
        foreach (var withCustomConnect in new[] { false, true })
        {
            var options = DefaultPathOptions(new OutboundEndpointPolicy(), 0, new StrongBox<int>());
            options.TrustedProxy = InvalidProxy(proxy);
            if (withCustomConnect)
                options.Connect = (_, _, _) => Task.CompletedTask;

            var construct = () => Transport(options);

            var error = construct.Should().Throw<InvalidOperationException>(withCustomConnect ? "with a custom Connect too" : "").Which;
            error.Message.Should().StartWith("WebSocketTransportOptions.TrustedProxy").And.Contain(rule);
            error.ToString().Should().NotContain(Password).And.NotContain("alice");
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:3128")]
    [InlineData("http://127.0.0.1:3128/")]
    [InlineData("http://127.0.0.1")]
    [InlineData("https://proxy.internal:443")]
    [InlineData("socks4://proxy.internal:1080")]
    [InlineData("socks4a://proxy.internal:1080")]
    [InlineData("socks5://[::1]:1080")]
    public async Task A_valid_TrustedProxy_is_accepted(string proxy)
    {
        var options = DefaultPathOptions(new OutboundEndpointPolicy(), 0, new StrongBox<int>());
        options.TrustedProxy = new WebProxy(new Uri(proxy)) { Credentials = new NetworkCredential("alice", Password) };

        await using var transport = Transport(options);

        transport.Should().NotBeNull();
    }

    internal static WebProxy InvalidProxy(string proxy) => proxy switch
    {
        "no-address" => new WebProxy(),
        "relative" => new WebProxy { Address = new Uri("/proxy:3128", UriKind.Relative) },
        "bypass-not-a-pattern" => WithUnparsableBypassEntry(),
        _ => new WebProxy(new Uri(proxy)),
    };

    private static WebProxy WithUnparsableBypassEntry()
    {
        // The BypassList setter rejects a bad pattern at once; the live list does not, so a host can
        // still hand the transport one.
        var proxy = new WebProxy(new Uri("http://127.0.0.1:3128"));
        proxy.BypassArrayList.Add("(");
        return proxy;
    }

    // ---- The direct-connect hint is for direct connections only ----

    [Fact]
    public async Task An_unreachable_named_proxy_fails_without_the_direct_connect_hint()
    {
        var port = ClosedLoopbackPort();
        await using var transport = Transport(Through(new Uri($"http://127.0.0.1:{port}"), 0, new StrongBox<int>()));

        var thrown = await SendFailsAsync(transport, Request(Public));

        Chain(thrown).OfType<SocketException>().Should().ContainSingle()
            .Which.SocketErrorCode.Should().Be(SocketError.ConnectionRefused, "the named proxy was dialled. Chain: {0}", Describe(thrown));
        Describe(thrown).Should().NotContain("connects directly").And.NotContain("set WebSocketTransportOptions.TrustedProxy");
    }

    [Fact]
    public async Task A_refused_bypassed_direct_connect_carries_no_hint_while_TrustedProxy_is_set()
    {
        await using var proxy = Refusing();
        var port = ClosedLoopbackPort();
        var policy = new OutboundEndpointPolicy();
        policy.AllowedHosts.Add("127.0.0.1");
        var options = Through(proxy.Address(), 0, new StrongBox<int>(), policy);
        options.TrustedProxy!.BypassList = new[] { @"127\.0\.0\.1" };
        await using var transport = Transport(options);

        var thrown = await SendFailsAsync(transport, Request(new Uri($"ws://127.0.0.1:{port}/x")));

        proxy.Accepted.Should().Be(0, "the destination is on the bypass list");
        Chain(thrown).OfType<SocketException>().Should().ContainSingle()
            .Which.SocketErrorCode.Should().Be(SocketError.ConnectionRefused, "Chain: {0}", Describe(thrown));
        Describe(thrown).Should().NotContain("connects directly", "a host that set TrustedProxy already knows how to use a proxy");
    }

    // ---- A custom Connect owns its connection ----

    [Fact]
    public async Task A_custom_Connect_is_used_unchanged_when_TrustedProxy_is_set()
    {
        await using var proxy = Refusing();
        var socket = new ScriptedSessionWebSocket();
        Uri? connectedTo = null;
        var options = Through(proxy.Address(), 0, new StrongBox<int>());
        options.WebSocketFactory = () => socket;
        options.Connect = (_, endpoint, _) =>
        {
            connectedTo = endpoint;
            return Task.CompletedTask;
        };
        await using var transport = Transport(options);

        var result = await transport.SendAsync(Request(new Uri("ws://10.0.0.1/x")), default).WaitAsync(HangGuard);

        result.Accepted.Should().BeTrue("a custom Connect owns its vetting: no pre-send refusal");
        connectedTo.Should().Be(new Uri("ws://10.0.0.1/x"));
        socket.Sent.Should().ContainSingle().Which.Should().Equal(Payload);
        proxy.Accepted.Should().Be(0, "TrustedProxy does not apply to a custom Connect");
    }
}
