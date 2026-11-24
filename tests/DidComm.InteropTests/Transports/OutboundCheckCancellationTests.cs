using System.Net;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.InteropTests.Transports.Proxy;
using DidComm.Transports;
using Xunit;
using static DidComm.InteropTests.Transports.Proxy.WebSocketProxyTestKit;

namespace DidComm.InteropTests.Transports;

/// <summary>The outbound check before a WebSocket send ends with the send's token while a host name is looked up.</summary>
public sealed class OutboundCheckCancellationTests
{
    [Fact]
    public async Task A_WebSocket_send_ends_with_its_token_while_the_name_is_looked_up()
    {
        var lookups = new SemaphoreSlim(0);
        var policy = new OutboundEndpointPolicy
        {
            ResolveDnsAsync = (_, _) =>
            {
                lookups.Release();
                return new TaskCompletionSource<IPAddress[]>().Task;
            },
        };
        var socket = new ScriptedSessionWebSocket();
        var connected = 0;
        var options = DefaultPathOptions(policy, 0, new StrongBox<int>());
        options.TrustedProxy = new WebProxy("http://127.0.0.1:9");
        options.WebSocketFactory = () => socket;
        options.Connect = (_, _, _) =>
        {
            Interlocked.Increment(ref connected);
            return Task.CompletedTask;
        };
        await using var transport = Transport(options);
        using var token = new CancellationTokenSource();

        var send = transport.SendAsync(Request(new Uri("ws://peer.example/x")), token.Token);
        (await lookups.WaitAsync(HangGuard)).Should().BeTrue();
        await token.CancelAsync();
        var caught = await Record.ExceptionAsync(() => send.WaitAsync(HangGuard));

        caught.Should().BeAssignableTo<OperationCanceledException>().Which.CancellationToken.Should().Be(token.Token);
        Volatile.Read(ref connected).Should().Be(0);
        socket.Sent.Should().BeEmpty();
    }
}
