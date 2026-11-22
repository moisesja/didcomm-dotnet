using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using DidComm.Protocols.Routing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #100 round 4, on real Kestrel: a WebSocket handshake that fails before the connection is accepted
/// (here, Kestrel's <c>Limits.MaxConcurrentUpgradedConnections</c> reached) is refused with
/// <c>503 Service Unavailable</c>, on all three WebSocket endpoints. Before, the exception escaped the
/// endpoint, and Kestrel answered 500 with one Error line and a stack trace per attempt. The refusal
/// is logged by type name only, rate-limited (the first and every 1000th at Warning, the rest at
/// Debug); a client that goes away during the handshake gets nothing and one Debug line.
/// </summary>
public sealed class WebSocketHandshakeRefusalTests
{
    private static readonly TimeSpan Guard = DeadlineKestrelHost.Guard;

    private const string DispatchCategory = "DidComm.AspNetCore.WebSocket.Dispatch";
    private const string CallbackCategory = "DidComm.AspNetCore.WebSocket";

    // What Kestrel throws at its limit: it must never reach the logs, nor its stack.
    private const string KestrelLimitText = "maximum number of upgraded connections";

    // The response headers the WebSocket handshake sets before Kestrel's upgrade can fail.
    private static readonly string[] HandshakeHeaders = { "Connection", "Upgrade", "Sec-WebSocket-Accept" };

    [Theory]
    [InlineData(DeadlineEndpoint.Dispatch, DispatchCategory)]
    [InlineData(DeadlineEndpoint.Callback, CallbackCategory)]
    [InlineData(DeadlineEndpoint.Mediator, DispatchCategory)]
    public async Task At_Kestrels_upgrade_limit_the_endpoint_refuses_the_handshake_with_503_and_nothing_logs_at_Error(DeadlineEndpoint endpoint, string category)
    {
        var host = await StartAtLimitAsync();
        await using var _host = host;
        using var open = await host.ConnectAsync(endpoint); // holds the only place

        using (var refused = new ClientWebSocket())
        {
            refused.Options.CollectHttpResponseDetails = true;
            using var guard = new CancellationTokenSource(Guard);
            await ((Func<Task>)(() => refused.ConnectAsync(host.UriOf(endpoint), guard.Token))).Should().ThrowAsync<WebSocketException>();
            refused.HttpStatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            refused.HttpResponseHeaders!.Keys.Should().NotContain(k => HandshakeHeaders.Contains(k, StringComparer.OrdinalIgnoreCase),
                "the refusal is a plain error response, without the handshake's headers");
        }
        // Once the hosting layer's end-of-request line is written, every line about the request is.
        await WebSocketMessageFinishTests.WaitRequestFinishedAsync(host, DeadlineKestrelHost.PathOf(endpoint));

        RefusalLines(host).Should().ContainSingle().Which.Should().Be((category, LogLevel.Warning, Refused(1)));
        WebSocketMessageFinishTests.Loud(host).Should().BeEmpty("a refused handshake is not an application error, for the endpoint nor for Kestrel");
        host.Logs.Entries.Should().NotContain(e => e.Contains(KestrelLimitText, StringComparison.Ordinal), "the refusal is logged by type name only, with no message or stack");
        host.EndedWebSockets.Should().Be(1, "the refused request ended; the open connection still runs");
        open.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task Refused_handshakes_in_a_row_follow_the_rate_limit_rule_not_one_Warning_each()
    {
        var host = await StartAtLimitAsync();
        await using var _host = host;
        using var open = await host.ConnectAsync(DeadlineEndpoint.Dispatch); // holds the only place

        // One keep-alive connection, one refused upgrade after the other: the rule's whole period.
        var attempts = (int)RateLimitedLog.Every;
        using var tcp = await ConnectTcpAsync(host);
        var stream = tcp.GetStream();
        for (var i = 0; i < attempts; i++)
        {
            using var cts = new CancellationTokenSource(Guard);
            await stream.WriteAsync(UpgradeRequest(host.Port, DeadlineKestrelHost.DispatchPath), cts.Token);
            var head = await ReadHeadAsync(stream, cts.Token);
            if (!head.StartsWith("HTTP/1.1 503 ", StringComparison.Ordinal))
                head.Should().StartWith("HTTP/1.1 503 ", $"attempt {i + 1} is refused with 503");
        }

        var lines = RefusalLines(host);
        lines.Should().HaveCount(attempts, "one line per refusal");
        lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text).Should().Equal(
            new[] { Refused(1), Refused(attempts) }, "the first and every 1000th refusal at Warning");
        lines.Where(l => l.Level != LogLevel.Warning).Should().HaveCount(attempts - 2).And.OnlyContain(l => l.Level == LogLevel.Debug);
        lines.Select(l => l.Text).Should().Equal(Enumerable.Range(1, attempts).Select(Refused), "each line carries its count on this endpoint");
        WebSocketMessageFinishTests.Loud(host).Should().BeEmpty();
        host.Logs.Entries.Should().NotContain(e => e.Contains(KestrelLimitText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_client_that_goes_away_during_the_handshake_gets_nothing_and_one_Debug_line()
    {
        // The handshake is held until the client leaves, as a server's handshake still under way
        // when its client goes away; it then fails with the request's cancellation.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = await DeadlineKestrelHost.StartAsync(o => o.Handshake = async context =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        await using var _host = host;

        using var tcp = await ConnectTcpAsync(host);
        var stream = tcp.GetStream();
        using (var cts = new CancellationTokenSource(Guard))
            await stream.WriteAsync(UpgradeRequest(host.Port, DeadlineKestrelHost.DispatchPath), cts.Token);
        await entered.Task.WaitAsync(Guard);
        tcp.Client.Shutdown(SocketShutdown.Send); // the client goes away (it can still read)

        (await ReadUntilServerEndsAsync(stream)).Should().BeEmpty("nothing is answered to a client that went away");
        await host.WaitEndedWebSocketsAsync(1);
        await WebSocketMessageFinishTests.WaitRequestFinishedAsync(host, DeadlineKestrelHost.DispatchPath);
        host.Logs.CategorizedRecords.Where(r => r.Category == DispatchCategory).Should().ContainSingle()
            .Which.Should().Be((DispatchCategory, LogLevel.Debug,
                "MapDidCommWebSocket: the client went away during the WebSocket handshake (TaskCanceledException); nothing is answered."));
        RefusalLines(host).Should().BeEmpty("a client that left is not a refusal: the limiter's count is not used up");
        WebSocketMessageFinishTests.Loud(host).Should().BeEmpty();
    }

    [Fact]
    public async Task A_handshake_that_fails_after_its_response_started_sends_no_status_and_nothing_logs_at_Error()
    {
        const string failure = "The test's handshake fails after its response started.";
        var host = await DeadlineKestrelHost.StartAsync(o => o.Handshake = async context =>
        {
            await context.Response.StartAsync();
            throw new InvalidOperationException(failure);
        });
        await using var _host = host;

        using (var refused = new ClientWebSocket())
        {
            refused.Options.CollectHttpResponseDetails = true;
            using var guard = new CancellationTokenSource(Guard);
            await ((Func<Task>)(() => refused.ConnectAsync(host.UriOf(DeadlineEndpoint.Dispatch), guard.Token))).Should().ThrowAsync<WebSocketException>();
            refused.HttpStatusCode.Should().Be(HttpStatusCode.OK, "the status went out with the response's start; the endpoint does not try to change it");
        }
        await WebSocketMessageFinishTests.WaitRequestFinishedAsync(host, DeadlineKestrelHost.DispatchPath);

        RefusalLines(host).Should().ContainSingle().Which.Should().Be((DispatchCategory, LogLevel.Warning,
            "MapDidCommWebSocket: a WebSocket handshake failed (InvalidOperationException) after its response had started, so no status could be sent; 1 so far on this endpoint."));
        WebSocketMessageFinishTests.Loud(host).Should().BeEmpty("setting a status on a started response would throw, and escape");
        host.Logs.Entries.Should().NotContain(e => e.Contains(failure, StringComparison.Ordinal));
    }

    // ---- harness -------------------------------------------------------------------------------------

    /// <summary>The refusal line of the first <paramref name="count"/>-th refused handshake on an endpoint, at Kestrel's limit.</summary>
    internal static string Refused(int count)
        => $"MapDidCommWebSocket: a WebSocket handshake failed (InvalidOperationException) and was refused with 503 (Service Unavailable); a server limit such as Kestrel's MaxConcurrentUpgradedConnections may have been reached; {count} so far on this endpoint.";

    /// <summary>The endpoints' handshake-refusal lines, in order.</summary>
    internal static IReadOnlyList<(string Category, LogLevel Level, string Text)> RefusalLines(DeadlineKestrelHost host)
        => host.Logs.CategorizedRecords.Where(r => r.Text.StartsWith("MapDidCommWebSocket: a WebSocket handshake failed", StringComparison.Ordinal)).ToList();

    private static Task<DeadlineKestrelHost> StartAtLimitAsync()
        => DeadlineKestrelHost.StartAsync(o => o.Builder = b => b.WebHost.ConfigureKestrel(k => k.Limits.MaxConcurrentUpgradedConnections = 1));

    private static async Task<TcpClient> ConnectTcpAsync(DeadlineKestrelHost host)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using var cts = new CancellationTokenSource(Guard);
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port, cts.Token);
        return tcp;
    }

    private static byte[] UpgradeRequest(int port, string path)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        return Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n");
    }

    /// <summary>Read one response head with no body (the 503 carries <c>Content-Length: 0</c>), and nothing after it.</summary>
    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var head = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                throw new IOException("The server ended the connection before the response head.");
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            var text = head.ToString();
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
                continue;
            text.Length.Should().Be(end + 4, "the response has no body and nothing follows it");
            text.Should().Contain("\r\nContent-Length: 0\r\n");
            foreach (var header in HandshakeHeaders)
                text.Should().NotContainEquivalentOf($"\r\n{header}:", "the refusal is a plain error response, without the handshake's headers");
            return text;
        }
    }

    /// <summary>Everything the server sends until it ends the connection (a FIN or a reset); <see cref="Guard"/> is a hang guard.</summary>
    private static async Task<byte[]> ReadUntilServerEndsAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(Guard);
        using var received = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, cts.Token);
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"The server did not end the connection within the {Guard} hang guard.");
            }
            catch (IOException)
            {
                return received.ToArray(); // a reset
            }
            if (read == 0)
                return received.ToArray();
            received.Write(buffer, 0, read);
        }
    }
}
