using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.InteropTests.Samples;
using DidComm.Transports;
using Xunit;
using static DidComm.InteropTests.Transports.Proxy.WebSocketProxyTestKit;

namespace DidComm.InteropTests.Transports.Proxy;

/// <summary>
/// The WebSocket transport without <c>TrustedProxy</c> connects directly: a proxy set for the
/// whole process (<see cref="HttpClient.DefaultProxy"/>, <c>HTTP_PROXY</c>, <c>HTTPS_PROXY</c>,
/// <c>ALL_PROXY</c>) is never used, so the connect-time guard vets the destination's own socket
/// rather than the proxy's. Clearing a custom <c>Connect</c> after construction cannot open an
/// unguarded path, and a refused direct connect says how to send through a proxy.
/// </summary>
/// <remarks>
/// These tests never name <c>TrustedProxy</c>, so they compile against the code before the fix and
/// fail there (the proof that they bite). They change the process proxy, so they run in the
/// non-parallel <see cref="DefaultProxyCollection"/>.
/// </remarks>
[Collection(nameof(DefaultProxyCollection))]
public sealed class WebSocketDirectByDefaultTests
{
    public static TheoryData<string, string> ProcessProxies => new()
    {
        { "DefaultProxy", "ws" },
        { "DefaultProxy", "wss" },
        { "HTTP_PROXY", "ws" },
        { "HTTPS_PROXY", "wss" },
        { "ALL_PROXY", "ws" },
        { "ALL_PROXY", "wss" },
    };

    [Theory]
    [MemberData(nameof(ProcessProxies))]
    public async Task A_process_proxy_is_never_used_and_the_destination_itself_is_refused_at_connect_time(string source, string scheme)
    {
        // The stand-in proxy is on 127.0.0.1, which the policy allows: a transport that hands the
        // connection to the process proxy would have the guard vet 127.0.0.1 (allowed) and connect.
        // The destination 'localhost' passes the pre-send check (ResolveDnsNames = false) and is
        // refused only by the connect-time pin, which a direct connection always runs.
        await using var proxy = new StandInProxy();
        var destination = new Uri($"{scheme}://localhost:{proxy.Port}/socket");
        using var scope = source == "DefaultProxy"
            ? ProcessProxyScope.FromDefaultProxy(proxy.Address())
            : ProcessProxyScope.FromEnvironment(source, proxy.Address());
        ProcessProxyScope.AssertProxies(HttpForm(destination), proxy.Address());

        var sockets = new StrongBox<int>();
        var policy = new OutboundEndpointPolicy { ResolveDnsNames = false };
        policy.AllowedHosts.Add("127.0.0.1");
        await using var transport = Transport(DefaultPathOptions(policy, maxReconnectAttempts: 0, sockets));

        var thrown = await SendFailsAsync(transport, Request(destination));

        proxy.Accepted.Should().Be(0, "the process proxy ({0}) must never carry a connection", source);
        thrown.Should().BeOfType<TransportException>();
        Chain(thrown).OfType<TransportException>().Should().Contain(
            e => e.Message.Contains($"Refusing to connect to 'localhost:{proxy.Port}'", StringComparison.Ordinal),
            "the guard vetted the destination's own socket. Chain: {0}", Describe(thrown));
    }

    [Fact]
    public async Task Clearing_a_construction_time_Connect_never_opens_an_unguarded_path_through_the_process_proxy()
    {
        // Before the fix, a Connect set at construction meant no guarded handler was built; clearing
        // it later sent the default path through ClientWebSocket.ConnectAsync(uri, ct): no guard,
        // and the process proxy.
        await using var proxy = new StandInProxy();
        var destination = new Uri($"ws://localhost:{proxy.Port}/socket");
        using var scope = ProcessProxyScope.FromDefaultProxy(proxy.Address());
        ProcessProxyScope.AssertProxies(HttpForm(destination), proxy.Address());

        var sockets = new StrongBox<int>();
        var options = DefaultPathOptions(new OutboundEndpointPolicy { ResolveDnsNames = false }, maxReconnectAttempts: 3, sockets);
        options.Connect = (_, _, _) => throw new InvalidOperationException("the construction-time Connect is cleared before any send");
        await using var transport = Transport(options);
        options.Connect = null;

        var thrown = await SendFailsAsync(transport, Request(destination));

        proxy.Accepted.Should().Be(0, "the cleared Connect must leave the guarded, direct default path, not the process proxy");
        sockets.Value.Should().Be(1, "the connect-time refusal is fatal: no reconnect is spent on it");
        thrown.Should().BeOfType<TransportException>().Which.Message.Should().Contain("permanently fatal");
        Chain(thrown).OfType<TransportException>().Should().Contain(
            e => e.Message.Contains($"Refusing to connect to 'localhost:{proxy.Port}'", StringComparison.Ordinal),
            "the guard vetted the destination. Chain: {0}", Describe(thrown));
    }

    [Fact]
    public async Task Clearing_a_construction_time_Connect_never_connects_without_the_guard()
    {
        // The same hole with no process proxy: the unguarded fallback dialled the loopback listener
        // (the destination 'localhost' resolves only to loopback) and sent it the upgrade request.
        await using var listener = new StandInProxy();
        var destination = new Uri($"ws://localhost:{listener.Port}/socket");
        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy(); // no address: every destination is bypassed
        try
        {
            var sockets = new StrongBox<int>();
            var options = DefaultPathOptions(new OutboundEndpointPolicy { ResolveDnsNames = false }, maxReconnectAttempts: 3, sockets);
            options.Connect = (_, _, _) => throw new InvalidOperationException("the construction-time Connect is cleared before any send");
            await using var transport = Transport(options);
            options.Connect = null;

            var thrown = await SendFailsAsync(transport, Request(destination));

            listener.Accepted.Should().Be(0, "a loopback destination is refused before it is dialled. Chain: {0}", Describe(thrown));
            sockets.Value.Should().Be(1, "the connect-time refusal is fatal. Chain: {0}", Describe(thrown));
            Chain(thrown).OfType<TransportException>().Should().Contain(
                e => e.Message.Contains($"Refusing to connect to 'localhost:{listener.Port}'", StringComparison.Ordinal),
                "Chain: {0}", Describe(thrown));
        }
        finally
        {
            HttpClient.DefaultProxy = previous;
        }
    }

    [Fact]
    public async Task A_refused_direct_connect_names_the_TrustedProxy_setting_and_is_still_retried()
    {
        // A closed loopback port, with the policy allowing loopback: the connect itself is refused
        // by the operating system. The error keeps its type and code (so it is retried as before)
        // and gains one sentence naming the setting a proxied host needs.
        var port = ClosedLoopbackPort();
        var sockets = new StrongBox<int>();
        var policy = new OutboundEndpointPolicy();
        policy.AllowedHosts.Add("127.0.0.1");
        await using var transport = Transport(DefaultPathOptions(policy, maxReconnectAttempts: 2, sockets));

        var thrown = await SendFailsAsync(transport, Request(new Uri($"ws://127.0.0.1:{port}/socket")));

        sockets.Value.Should().Be(3, "a refused connect is transient: the first attempt and both reconnects run");
        thrown.Should().BeOfType<TransportException>().Which.Message.Should().Contain("exhausting the reconnect budget (2 attempt(s))");
        var socketError = Chain(thrown).OfType<SocketException>().Should().ContainSingle().Subject;
        socketError.SocketErrorCode.Should().Be(SocketError.ConnectionRefused, "the hint keeps the error code");
        socketError.Message.Should().Contain("This transport connects directly: it does not use HTTP_PROXY, HTTPS_PROXY, ALL_PROXY or the system proxy.")
            .And.Contain("To send through a proxy, set WebSocketTransportOptions.TrustedProxy.");
    }
}
