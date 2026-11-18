using System.Net.Http;
using System.Net.Sockets;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.InteropTests.Samples;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Transports.Proxy;

/// <summary>
/// FR-TRN-13, default mode (no trusted proxy configured): the HTTP transport connects directly and
/// never uses the process proxy (<see cref="HttpClient.DefaultProxy"/>, <c>HTTP_PROXY</c>,
/// <c>HTTPS_PROXY</c>, <c>ALL_PROXY</c>). Through a process proxy the connect-time guard vetted the
/// proxy's socket instead of the destination's, so a forbidden destination was handed to the proxy.
/// </summary>
/// <remarks>
/// The stand-in proxy is on the policy's <c>AllowedHosts</c>, so a transport that does use the
/// process proxy gets past the guard and the stand-in sees a connection: that is what fails these
/// tests on the code before the change. They name no <c>TrustedProxy</c>, so they build there.
/// </remarks>
[Collection(nameof(DefaultProxyCollection))]
public sealed class HttpTransportProcessProxyTests
{
    public static TheoryData<string, string> ProcessProxies => new()
    {
        { "DefaultProxy", "http://10.0.0.1/inbox" },
        { "DefaultProxy", "https://10.0.0.1/inbox" },
        { "HTTP_PROXY", "http://10.0.0.1/inbox" },
        { "HTTPS_PROXY", "https://10.0.0.1/inbox" },
        { "ALL_PROXY", "http://10.0.0.1/inbox" },
        { "ALL_PROXY", "https://10.0.0.1/inbox" },
    };

    [Theory]
    [MemberData(nameof(ProcessProxies))]
    public async Task A_process_proxy_is_never_used_and_the_guard_refuses_the_forbidden_destination_itself(string source, string destination)
    {
        await using var standIn = new StandInProxy();
        var proxy = standIn.Address();
        using var scope = source == "DefaultProxy"
            ? ProcessProxyScope.FromDefaultProxy(proxy)
            : ProcessProxyScope.FromEnvironment(source, proxy);
        ProcessProxyScope.AssertProxies(new Uri(destination), proxy);

        // The stand-in is allowed: a transport that dials the process proxy passes the guard.
        await using var sp = HttpTransportHarness.Build(o =>
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { AllowedHosts = { "127.0.0.1" } });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await HttpTransportHarness.SendExpectingFailureAsync(transport, destination);

        // The send has finished, so no later connection can come: the count is final.
        standIn.Accepted.Should().Be(0,
            "the {0} process proxy must not carry the send (it recorded: [{1}]); failure was: {2}",
            source, string.Join("; ", standIn.Requests), HttpTransportHarness.Describe(thrown));
        HttpTransportHarness.Chain(thrown).OfType<TransportException>().Should().ContainSingle()
            .Which.Message.Should().Contain("private or reserved", "the connect-time guard refused 10.0.0.1 itself");
    }
}

/// <summary>
/// FR-TRN-13: a direct connection that fails (refused, or a name that does not resolve) says that
/// the transport connects directly and names <c>HttpTransportOptions.TrustedProxy</c>, without
/// changing how the failure is classified: the inner <see cref="SocketException"/> keeps its error
/// code and <see cref="HttpRequestException.HttpRequestError"/> is what it was before the change.
/// </summary>
/// <remarks>
/// The hint assertions come last: on the code before the change these tests fail only there,
/// which shows the classification assertions above them hold on both.
/// </remarks>
public sealed class HttpTransportDirectHintTests
{
    private const string Hint = "HttpTransportOptions.TrustedProxy";

    [Fact]
    public async Task A_refused_direct_connection_carries_the_hint_and_keeps_its_classification()
    {
        var port = HttpTransportHarness.ClosedLoopbackPort();
        await using var sp = HttpTransportHarness.Build(o =>
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { AllowedHosts = { "127.0.0.1" } });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await HttpTransportHarness.SendExpectingFailureAsync(transport, $"http://127.0.0.1:{port}/inbox");

        var http = thrown.Should().BeOfType<HttpRequestException>().Which;
        http.HttpRequestError.Should().Be(HttpRequestError.ConnectionError);
        var socket = HttpTransportHarness.Chain(thrown).OfType<SocketException>().Should().ContainSingle().Which;
        socket.SocketErrorCode.Should().Be(SocketError.ConnectionRefused);

        socket.Message.Should().Contain(Hint).And.Contain("connects directly");
        http.Message.Should().Contain(Hint, "the HttpRequestException's message carries the socket error's text");
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_carries_the_hint_and_keeps_its_classification()
    {
        // ResolveDnsNames = false: nothing looks the name up before the connect does.
        await using var sp = HttpTransportHarness.Build(o =>
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var thrown = await HttpTransportHarness.SendExpectingFailureAsync(transport, "http://didcomm-qa-no-such-host.invalid/inbox");

        var http = thrown.Should().BeOfType<HttpRequestException>().Which;
        http.HttpRequestError.Should().Be(HttpRequestError.NameResolutionError);
        var socket = HttpTransportHarness.Chain(thrown).OfType<SocketException>().Should().ContainSingle().Which;
        socket.SocketErrorCode.Should().Be(SocketError.HostNotFound);

        socket.Message.Should().Contain(Hint).And.Contain("connects directly");
        http.Message.Should().Contain(Hint);
    }
}
