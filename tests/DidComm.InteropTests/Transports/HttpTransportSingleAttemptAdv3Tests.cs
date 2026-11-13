using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// ADV-3 (#95): the single-attempt send behind direct push, through the REAL wiring
/// (<c>UseHttpTransport</c>: <see cref="SocketsHttpHandler"/> with auto-redirect off and the SSRF
/// connect callback) against a raw TCP server the test scripts byte by byte — so what is counted is
/// what reached the wire, not what a stub handler saw.
/// </summary>
public sealed class HttpTransportSingleAttemptAdv3Tests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    // ── Redirects: every 3xx, every kind of Location, is one request on the wire and a failure ──

    [Theory]
    [InlineData(301, "http://169.254.169.254/latest/meta-data/")]
    [InlineData(302, "/elsewhere")]
    [InlineData(303, "http://10.0.0.1/")]
    [InlineData(307, "/inbox-moved")]
    [InlineData(307, "http://[::ffff:0:7f00:1]/")]
    [InlineData(308, "/inbox-moved")]
    [InlineData(300, "/inbox-moved")]
    public async Task A_redirect_answer_is_one_request_on_the_wire_and_a_failure(int status, string location)
    {
        // A relative Location points back at the same server: had the client followed it, the server
        // would have seen a second request.
        await using var server = RawHttpServer.Start((_, _) => RawAction.Respond(status, location));
        await using var sp = BuildTransport(o => o.MaxRetryAttempts = 3);
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var failure = (await transport.Awaiting(t => t.SendAsync(Single(server.Endpoint), default).WaitAsync(Bound))
            .Should().ThrowAsync<TransportException>()).Which;

        failure.HttpStatusCode.Should().Be(status);
        server.Requests.Should().ContainSingle("a single attempt follows no redirect ({0} → {1})", status, location)
            .Which.RequestLine.Should().Be("POST /inbox HTTP/1.1");
        server.Connections.Should().Be(1);
    }

    [Fact]
    public async Task A_normal_send_still_follows_a_307_on_the_wire()
    {
        // The contrast: the single-attempt switch did not change a normal send (FR-TRN-06).
        await using var server = RawHttpServer.Start((index, _) => index == 1 ? RawAction.Respond(307, "/inbox-moved") : RawAction.Respond(202));
        await using var sp = BuildTransport();
        var transport = sp.GetRequiredService<IDidCommTransport>();

        (await transport.SendAsync(Single(server.Endpoint) with { SingleAttempt = false }, default).WaitAsync(Bound)).Accepted.Should().BeTrue();

        server.Requests.Select(r => r.RequestLine).Should().Equal("POST /inbox HTTP/1.1", "POST /inbox-moved HTTP/1.1");
    }

    // ── Retries: none on any path a stranger's endpoint can produce ──────────────────────

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(408)]
    [InlineData(429)]
    public async Task A_failure_status_is_one_request_on_the_wire(int status)
    {
        await using var server = RawHttpServer.Start((_, _) => RawAction.Respond(status));
        await using var sp = BuildTransport(o => o.MaxRetryAttempts = 3);
        var transport = sp.GetRequiredService<IDidCommTransport>();

        (await transport.Awaiting(t => t.SendAsync(Single(server.Endpoint), default).WaitAsync(Bound))
            .Should().ThrowAsync<TransportException>()).Which.HttpStatusCode.Should().Be(status);
        server.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_endpoint_that_never_answers_gets_one_request_and_the_send_ends_at_the_request_timeout()
    {
        await using var server = RawHttpServer.Start((_, _) => RawAction.Hang);
        await using var sp = BuildTransport(o =>
        {
            o.MaxRetryAttempts = 3;
            // Long enough that the request reaches the hanging server even under the parallel test
            // load (2-core CI runners); 300 ms sometimes expired before the connection was accepted,
            // leaving zero requests recorded. The property under test is "one request, no retry".
            o.RequestTimeout = TimeSpan.FromSeconds(3);
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        (await transport.Awaiting(t => t.SendAsync(Single(server.Endpoint), default).WaitAsync(Bound))
            .Should().ThrowAsync<TransportException>()).Which.HttpStatusCode.Should().BeNull();
        server.Requests.Should().HaveCount(1, "a timeout is not retried");
    }

    [Fact]
    public async Task A_kept_alive_connection_closed_after_the_request_arrived_is_not_resent_on_a_new_one()
    {
        // SocketsHttpHandler resends a request on a fresh connection when a REUSED connection fails
        // before the request went out (the server had closed it while idle). Here the endpoint reads
        // the whole second POST on the reused connection and closes without answering: had the
        // handler resent it, the endpoint would hold the message twice.
        await using var server = RawHttpServer.Start((index, _) => index == 2 ? RawAction.CloseWithoutResponse : RawAction.Respond(202));
        await using var sp = BuildTransport(o => o.MaxRetryAttempts = 3);
        var transport = sp.GetRequiredService<IDidCommTransport>();

        (await transport.SendAsync(Single(server.Endpoint), default).WaitAsync(Bound)).Accepted.Should().BeTrue();
        await transport.Awaiting(t => t.SendAsync(Single(server.Endpoint), default).WaitAsync(Bound)).Should().ThrowAsync<TransportException>();

        server.Requests.Should().HaveCount(2);
        server.Requests.Select(r => r.Connection).Should().Equal(new[] { 1, 1 }, "the second POST went out once, on the reused connection");
    }

    // ── The breaker: a single attempt is neither refused nor counted ────────────────────

    [Fact]
    public async Task On_the_wire_an_open_breaker_does_not_refuse_a_single_attempt_and_failing_single_attempts_never_open_it()
    {
        var failing = 1;
        await using var server = RawHttpServer.Start((_, _) => Volatile.Read(ref failing) == 1 ? RawAction.Respond(503) : RawAction.Respond(202));
        await using var sp = BuildTransport(o =>
        {
            o.MaxRetryAttempts = 0;
            o.CircuitBreakerFailureThreshold = 2;
            o.CircuitBreakerOpenDuration = TimeSpan.FromMinutes(5);
        });
        var transport = sp.GetRequiredService<IDidCommTransport>();

        // Ten failing single attempts are not counted: the first two failing normal sends afterwards
        // reach the endpoint (had the singles counted, the breaker would already refuse them).
        for (var i = 0; i < 10; i++)
            await transport.Awaiting(t => t.SendAsync(Single(server.Endpoint), default).WaitAsync(Bound)).Should().ThrowAsync<TransportException>();
        for (var i = 0; i < 2; i++)
            await transport.Awaiting(t => t.SendAsync(Single(server.Endpoint) with { SingleAttempt = false }, default).WaitAsync(Bound)).Should().ThrowAsync<TransportException>();
        server.Requests.Should().HaveCount(12, "the breaker was closed when the normal sends started");

        // Those two opened it; a single attempt still goes out and is accepted.
        Volatile.Write(ref failing, 0);
        await transport.Awaiting(t => t.SendAsync(Single(server.Endpoint) with { SingleAttempt = false }, default).WaitAsync(Bound))
            .Should().ThrowAsync<Polly.CircuitBreaker.BrokenCircuitException>();
        var before = server.Requests.Count;
        (await transport.SendAsync(Single(server.Endpoint), default).WaitAsync(Bound)).Accepted.Should().BeTrue();
        server.Requests.Should().HaveCount(before + 1);
    }

    // ── Connect-time pinning still applies to a single attempt ──────────────────────────

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("[::ffff:0:7f00:1]")]
    [InlineData("[::127.0.0.1]")]
    [InlineData("[64:ff9b:1::7f00:1]")]
    [InlineData("127.0.0.1.")]
    [InlineData("2130706433")]
    public async Task A_single_attempt_to_a_private_destination_is_refused_at_connect_time_with_nothing_sent(string host)
    {
        // The facade's pre-send check is bypassed here (the transport is called directly): only the
        // named client's connect callback stands between the push and the loopback listener.
        await using var server = RawHttpServer.Start((_, _) => RawAction.Respond(202));
        await using var sp = BuildTransport(policy: new OutboundEndpointPolicy());
        var transport = sp.GetRequiredService<IDidCommTransport>();
        var endpoint = new Uri($"http://{host}:{server.Endpoint.Port}/inbox");

        var failure = (await transport.Awaiting(t => t.SendAsync(Single(endpoint), default).WaitAsync(Bound))
            .Should().ThrowAsync<TransportException>()).Which;

        Flatten(failure).Should().Contain("private or reserved", host);
        server.Connections.Should().Be(0, "nothing reached the loopback listener ({0})", host);
    }

    private static TransportRequest Single(Uri endpoint)
        => new(endpoint, Encoding.UTF8.GetBytes("{\"ciphertext\":\"x\"}"), "application/didcomm-encrypted+json") { SingleAttempt = true };

    private static string Flatten(Exception ex)
    {
        var text = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            text.Append(e.Message).Append(" | ");
        return text.ToString();
    }

    private static ServiceProvider BuildTransport(Action<HttpTransportOptions>? configure = null, OutboundEndpointPolicy? policy = null)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                o.RequestTimeout = TimeSpan.FromSeconds(10);
                o.OutboundEndpointPolicy = policy ?? new OutboundEndpointPolicy { AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" } };
                configure?.Invoke(o);
            });
        });
        return services.BuildServiceProvider();
    }

    internal sealed record RawRequest(int Index, int Connection, string RequestLine, string Headers, int BodyBytes);

    internal sealed record RawAction(int Status, string? Location, bool Close)
    {
        public static RawAction Respond(int status, string? location = null) => new(status, location, Close: false);
        public static readonly RawAction CloseWithoutResponse = new(0, null, Close: true);
        public static readonly RawAction Hang = new(-1, null, Close: false);
    }

    /// <summary>
    /// A minimal HTTP/1.1 server on 127.0.0.1: reads each request (headers and Content-Length body),
    /// records it, and does what the script says — answer with a status (keep-alive), or close the
    /// connection without a byte of response.
    /// </summary>
    internal sealed class RawHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<int, RawRequest, RawAction> _script;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<RawRequest> _requests = new();
        private readonly ConcurrentBag<TcpClient> _clients = new();
        private int _index;
        private int _connections;
        private Task _accept = Task.CompletedTask;

        private RawHttpServer(Func<int, RawRequest, RawAction> script)
        {
            _script = script;
            _listener = new TcpListener(IPAddress.Loopback, 0);
        }

        public Uri Endpoint => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/inbox");
        public IReadOnlyCollection<RawRequest> Requests => _requests;
        public int Connections => Volatile.Read(ref _connections);

        public static RawHttpServer Start(Func<int, RawRequest, RawAction> script)
        {
            var server = new RawHttpServer(script);
            server._listener.Start();
            server._accept = Task.Run(server.AcceptLoopAsync);
            return server;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch
                {
                    return;
                }
                _clients.Add(client);
                var connection = Interlocked.Increment(ref _connections);
                _ = Task.Run(() => ServeAsync(client, connection));
            }
        }

        private async Task ServeAsync(TcpClient client, int connection)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new List<byte>();
                var chunk = new byte[4096];
                while (!_stop.IsCancellationRequested)
                {
                    // Headers.
                    int end;
                    while ((end = IndexOfHeaderEnd(buffer)) < 0)
                    {
                        var n = await stream.ReadAsync(chunk, _stop.Token);
                        if (n == 0)
                            return;
                        buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                    }
                    var head = Encoding.ASCII.GetString(buffer.GetRange(0, end).ToArray());
                    buffer.RemoveRange(0, end + 4);
                    var length = 0;
                    foreach (var line in head.Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                    while (buffer.Count < length)
                    {
                        var n = await stream.ReadAsync(chunk, _stop.Token);
                        if (n == 0)
                            return;
                        buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                    }
                    buffer.RemoveRange(0, length);

                    var request = new RawRequest(Interlocked.Increment(ref _index), connection, head.Split("\r\n")[0], head, length);
                    _requests.Enqueue(request);
                    var action = _script(request.Index, request);
                    if (action.Status == -1)
                    {
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        return;
                    }
                    if (action.Close)
                    {
                        client.Client.Shutdown(SocketShutdown.Both);
                        client.Close();
                        return;
                    }
                    var response = new StringBuilder()
                        .Append("HTTP/1.1 ").Append(action.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" X\r\n")
                        .Append("Content-Length: 0\r\n")
                        .Append("Connection: keep-alive\r\n");
                    if (action.Location is not null)
                        response.Append("Location: ").Append(action.Location).Append("\r\n");
                    response.Append("\r\n");
                    var bytes = Encoding.ASCII.GetBytes(response.ToString());
                    await stream.WriteAsync(bytes, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
            }
            catch
            {
                // The test decides; a torn connection is part of some scripts.
            }
        }

        private static int IndexOfHeaderEnd(List<byte> buffer)
        {
            for (var i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            foreach (var client in _clients)
                client.Dispose();
            try
            {
                await _accept;
            }
            catch
            {
                // Stopped.
            }
            _stop.Dispose();
        }
    }
}
