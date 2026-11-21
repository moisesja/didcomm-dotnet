using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.InteropTests.AspNetCore;
using DidComm.Messages;
using DidComm.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// A request body that stops arriving before its end — the client went away mid-body (for example
/// after <c>Expect: 100-continue</c>), the connection was reset, or the server's own body limits cut
/// it off — on each of the three HTTP receive endpoints. Nothing is unpacked or dispatched, no
/// exception escapes to the server (which would log it at Error as an unhandled exception), and one
/// Debug line names the exception type. A body the server refused is answered with the server's
/// status for it, a cancelled read with 400, and a body whose stream failed ends the request. A
/// cancellation while the request is aborted still propagates. The DIDComm HTTPS rules (a
/// successful receipt is answered 2xx) say nothing about a body that never arrived.
/// </summary>
public sealed class HttpRequestBodyReadFailureTests
{
    private const string Callback = "/callback";
    private const string Registry = "/registry";
    private const string Session = "/session";
    private const int MaxReceiveBytes = 4096;

    public static TheoryData<string> Endpoints => new() { Callback, Registry, Session };

    public static TheoryData<string, string> EndpointsAndRefusals => Pairs(Refusals.Keys);

    public static TheoryData<string, string> EndpointsAndStreamFailures => Pairs(StreamFailures.Keys);

    /// <summary>The server refusing a body, or a read cancelled without the request being aborted, and the status the endpoint answers.</summary>
    private static readonly IReadOnlyDictionary<string, (Func<Exception> Create, int Status)> Refusals = new Dictionary<string, (Func<Exception>, int)>
    {
        ["unexpected-end"] = (() => new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest), StatusCodes.Status400BadRequest),
        ["body-too-slow"] = (() => new BadHttpRequestException("Reading the request body timed out due to data arriving too slowly.", StatusCodes.Status408RequestTimeout), StatusCodes.Status408RequestTimeout),
        ["over-server-limit"] = (() => new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge), StatusCodes.Status413PayloadTooLarge),
        ["cancelled"] = (() => new OperationCanceledException("a cancellation that is not the request's abort"), StatusCodes.Status400BadRequest),
        ["task-cancelled"] = (() => new TaskCanceledException("a timeout of some other operation"), StatusCodes.Status400BadRequest),
    };

    /// <summary>The body stream itself failing under the read.</summary>
    private static readonly IReadOnlyDictionary<string, Func<Exception>> StreamFailures = new Dictionary<string, Func<Exception>>
    {
        ["io"] = () => new IOException("the body stream failed"),
        ["connection-reset"] = () => new ConnectionResetException("the client reset the connection"),
    };

    /// <summary>
    /// The body delivers a whole, valid plaintext message and then fails instead of ending: had the
    /// endpoint treated the failure as the end of the body, it would have dispatched that message.
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointsAndRefusals))]
    public async Task A_body_the_server_refused_or_a_cancelled_read_is_answered_without_processing(string endpoint, string failure)
    {
        await using var host = await BodyFailureHost.StartAsync();
        var (create, status) = Refusals[failure];
        var id = "bodyfail-" + Guid.NewGuid().ToString("N");

        HttpContext? answered = null;
        var thrown = await Record.ExceptionAsync(async () => answered = await host.Server.SendAsync(ctx =>
            Post(ctx, endpoint, new FailingBody(Probe(id), _ => throw create()))));

        thrown.Should().BeNull("a body that could not be read is answered by the endpoint, not escaped to the server");
        host.Escaped.Should().BeEmpty();
        answered!.Response.StatusCode.Should().Be(status);
        host.Processed.Should().Be(0, "nothing was unpacked, dispatched or handed to a callback");
        AssertOneDebugLine(host, create().GetType().Name, id);
    }

    /// <summary>
    /// The body stream fails under the read (a reset connection): the endpoint ends the request,
    /// since nothing can be answered on that connection and a server left to read the rest of the
    /// body would log its own failure at Error. The same whole message arrived first.
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointsAndStreamFailures))]
    public async Task A_body_whose_stream_failed_ends_the_request_without_processing(string endpoint, string failure)
    {
        await using var host = await BodyFailureHost.StartAsync();
        var create = StreamFailures[failure];
        var id = "bodyfail-" + Guid.NewGuid().ToString("N");
        HttpContext? request = null;

        // What the client sees of a request the server aborted is TestHost's business; the endpoint's
        // exit is what the middleware around it records.
        await Record.ExceptionAsync(() => host.Server.SendAsync(ctx =>
        {
            request = ctx;
            Post(ctx, endpoint, new FailingBody(Probe(id), _ => throw create()));
        }));

        host.Escaped.Should().BeEmpty("the failure is handled by the endpoint, not escaped to the server");
        request!.RequestAborted.IsCancellationRequested.Should().BeTrue("the endpoint aborted the request");
        host.Processed.Should().Be(0, "nothing was unpacked, dispatched or handed to a callback");
        AssertOneDebugLine(host, create().GetType().Name, id);
    }

    /// <summary>The control: the same body that ends normally is processed once and answered 202.</summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task The_same_body_that_ends_normally_is_processed(string endpoint)
    {
        await using var host = await BodyFailureHost.StartAsync();

        var answered = await host.Server.SendAsync(ctx => Post(ctx, endpoint, new FailingBody(Probe("bodyok-" + Guid.NewGuid().ToString("N")), failure: null)));

        answered.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        host.Processed.Should().Be(1);
        host.Escaped.Should().BeEmpty();
    }

    /// <summary>A body read cancelled because the request was aborted propagates, as before: it is not answered 400.</summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_cancellation_while_the_request_is_aborted_still_propagates(string endpoint)
    {
        await using var host = await BodyFailureHost.StartAsync();
        var abortedWhenRead = false;
        HttpContext? request = null;

        // What the client sees of an aborted request is TestHost's business; the endpoint's exit is
        // what the middleware around it records.
        await Record.ExceptionAsync(() => host.Server.SendAsync(ctx =>
        {
            request = ctx;
            Post(ctx, endpoint, new FailingBody(Probe("bodyabort-" + Guid.NewGuid().ToString("N")), ct =>
            {
                request!.Abort();
                abortedWhenRead = ct.IsCancellationRequested;
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("the abort did not cancel the read's token");
            }));
        }));

        abortedWhenRead.Should().BeTrue("precondition: the read's token is the request's, and the abort cancelled it");
        // The client sees the abort as it happens; the middleware records what escaped the endpoint
        // as the server's pipeline unwinds, which can be later under load. Wait for that record, the
        // one asserted (L-065 rule 1); the timeout is only a hang guard.
        (await ReturnRouteTestHost.WaitUntilAsync(() => !host.Escaped.IsEmpty, TimeSpan.FromSeconds(15))).Should().BeTrue("the server's pipeline recorded what escaped the endpoint");
        host.Escaped.Should().ContainSingle().Which.Should().BeAssignableTo<OperationCanceledException>();
        host.Processed.Should().Be(0);
        host.Logs.Entries.Should().NotContain(e => e.Contains("could not be read", StringComparison.Ordinal),
            "a genuine abort is not written as an unreadable body");
    }

    /// <summary>A body over <c>MaxReceiveBytes</c> with no Content-Length is still answered 413 at the cap.</summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_body_over_the_receive_limit_is_still_answered_413(string endpoint)
    {
        await using var host = await BodyFailureHost.StartAsync();

        var answered = await host.Server.SendAsync(ctx => Post(ctx, endpoint, new FailingBody(new byte[MaxReceiveBytes + 1], failure: null)));

        answered.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        host.Processed.Should().Be(0);
        host.Escaped.Should().BeEmpty();
        host.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// On Kestrel: an <c>Expect: 100-continue</c> request whose body stalls after a few bytes is cut
    /// off by Kestrel's minimum body data rate. The endpoint answers Kestrel's 408 and nothing is
    /// logged at Error. Kestrel's own timer decides when; the test waits for the answer, which comes
    /// with a correct product.
    /// </summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task On_Kestrel_a_body_stalled_under_the_minimum_data_rate_is_answered_408_with_no_error(string endpoint)
    {
        await using var host = await BodyFailureHost.StartAsync(kestrel: true);
        var body = Probe("bodystall-" + Guid.NewGuid().ToString("N"));

        using var tcp = await host.ConnectRawAsync();
        var stream = tcp.GetStream();
        await stream.WriteAsync(host.Head(endpoint, body.Length));
        var interim = await ReadHeadAsync(stream);
        await stream.WriteAsync(body.AsMemory(0, 10));
        var final = await ReadHeadAsync(stream);
        await BodyFailureHost.WaitUntilAsync(() => host.RequestsFinished == 1, "the hosting layer finished the request");

        interim.Should().StartWith("HTTP/1.1 100");
        final.Should().StartWith("HTTP/1.1 408");
        host.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Error, "a stalled body is not an unhandled exception");
        host.Processed.Should().Be(0);
    }

    /// <summary>
    /// The control on Kestrel: a client that uses <c>Expect: 100-continue</c> correctly (the head, the
    /// server's 100, then the whole body) is processed and answered 202, as before the body-read change.
    /// </summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task On_Kestrel_a_body_sent_in_full_after_100_continue_is_processed(string endpoint)
    {
        await using var host = await BodyFailureHost.StartAsync(kestrel: true);
        var body = Probe("bodycontinue-" + Guid.NewGuid().ToString("N"));

        using var tcp = await host.ConnectRawAsync();
        var stream = tcp.GetStream();
        await stream.WriteAsync(host.Head(endpoint, body.Length));
        var interim = await ReadHeadAsync(stream);
        await stream.WriteAsync(body);
        var final = await ReadHeadAsync(stream);
        await BodyFailureHost.WaitUntilAsync(() => host.RequestsFinished == 1, "the hosting layer finished the request");

        interim.Should().StartWith("HTTP/1.1 100");
        final.Should().StartWith("HTTP/1.1 202");
        host.Processed.Should().Be(1);
        host.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Warning);
        host.Logs.Entries.Should().NotContain(e => e.Contains("could not be read", StringComparison.Ordinal));
    }

    /// <summary>
    /// Smoke test on Kestrel: <c>Expect: 100-continue</c>, half the body, then a reset (RST). Whether
    /// the endpoint's read sees the reset as an I/O failure or as the request's abort is a race inside
    /// Kestrel; neither may be logged at Error, and nothing is dispatched.
    /// </summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task On_Kestrel_a_reset_mid_body_dispatches_nothing_and_logs_no_error(string endpoint)
    {
        await using var host = await BodyFailureHost.StartAsync(kestrel: true);
        var body = Probe("bodyreset-" + Guid.NewGuid().ToString("N"));
        const int attempts = 10;

        for (var i = 1; i <= attempts; i++)
        {
            var tcp = await host.ConnectRawAsync();
            var stream = tcp.GetStream();
            await stream.WriteAsync(host.Head(endpoint, body.Length));
            (await ReadHeadAsync(stream)).Should().StartWith("HTTP/1.1 100");
            await stream.WriteAsync(body.AsMemory(0, body.Length / 2));
            tcp.LingerState = new LingerOption(true, 0);
            tcp.Close();
            var expected = i;
            await BodyFailureHost.WaitUntilAsync(() => host.RequestsFinished == expected, "the hosting layer finished the request");
        }

        host.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Error);
        host.Processed.Should().Be(0);
    }

    /// <summary>
    /// On Kestrel: a body over the server's own <c>MaxRequestBodySize</c> (here below the endpoint's
    /// receive limit), declared by Content-Length or sent chunked, is answered with Kestrel's 413 and
    /// nothing is logged at Error.
    /// </summary>
    [Theory]
    [InlineData(Callback, false)]
    [InlineData(Registry, false)]
    [InlineData(Session, false)]
    [InlineData(Callback, true)]
    [InlineData(Registry, true)]
    [InlineData(Session, true)]
    public async Task On_Kestrel_a_body_over_the_servers_own_limit_is_answered_413_with_no_error(string endpoint, bool chunked)
    {
        await using var host = await BodyFailureHost.StartAsync(kestrel: true, maxRequestBodySize: 100);
        var body = Probe("bodylimit-" + Guid.NewGuid().ToString("N"));
        body.Length.Should().BeGreaterThan(100).And.BeLessThan(MaxReceiveBytes, "precondition: over the server's limit, under the endpoint's");

        using var tcp = await host.ConnectRawAsync();
        var stream = tcp.GetStream();
        await stream.WriteAsync(host.Head(endpoint, chunked ? null : body.Length, expectContinue: false));
        await stream.WriteAsync(chunked
            ? Encoding.ASCII.GetBytes($"{body.Length:x}\r\n").Concat(body).Concat(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n")).ToArray()
            : body);
        var final = await ReadHeadAsync(stream);
        await BodyFailureHost.WaitUntilAsync(() => host.RequestsFinished == 1, "the hosting layer finished the request");

        final.Should().StartWith("HTTP/1.1 413");
        host.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Error, "a body the server refused is not an unhandled exception");
        host.Processed.Should().Be(0);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static TheoryData<string, string> Pairs(IEnumerable<string> failures)
    {
        var data = new TheoryData<string, string>();
        foreach (var endpoint in new[] { Callback, Registry, Session })
            foreach (var failure in failures)
                data.Add(endpoint, failure);
        return data;
    }

    /// <summary>One Debug line from the library naming <paramref name="exceptionType"/>, nothing at Warning or above, and no wire value or exception text in any line.</summary>
    private static void AssertOneDebugLine(BodyFailureHost host, string exceptionType, string id)
    {
        host.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Warning, "a body that never arrived is not a refused envelope or an application error");
        var unreadLines = host.Logs.CategorizedRecords.Where(r => r.Text.Contains("could not be read", StringComparison.Ordinal)).ToList();
        unreadLines.Should().ContainSingle("one line per unreadable body");
        unreadLines[0].Category.Should().StartWith("DidComm.AspNetCore");
        unreadLines[0].Level.Should().Be(LogLevel.Debug);
        unreadLines[0].Text.Should().Contain(exceptionType, "the line names the exception type");
        host.Logs.Entries.Should().NotContain(e => e.Contains(id, StringComparison.Ordinal) || e.Contains("the body stream failed", StringComparison.Ordinal),
            "the line names the exception type only, never a wire value or the exception's text");
    }

    private static void Post(HttpContext ctx, string path, Stream body)
    {
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = path;
        ctx.Request.ContentType = DidCommMediaTypes.Plain;
        ctx.Request.Body = body;
    }

    /// <summary>A plaintext message of the recording protocol, which all three endpoints process.</summary>
    private static byte[] Probe(string id)
        => Encoding.UTF8.GetBytes(new JsonObject
        {
            ["id"] = id,
            ["typ"] = DidCommMediaTypes.Plain,
            ["type"] = RecordingHandler.ProtocolUri + "/probe",
            ["body"] = new JsonObject(),
        }.ToJsonString());

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(BodyFailureHost.Timeout);
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(one, cts.Token);
            if (read == 0)
                break;
            head.Append((char)one[0]);
        }
        return head.ToString();
    }

    /// <summary>
    /// A request body that returns <paramref name="content"/> in two reads and then, instead of
    /// ending, runs <paramref name="failure"/> (which throws); <c>null</c> ends the body normally.
    /// </summary>
    private sealed class FailingBody(byte[] content, Func<CancellationToken, int>? failure) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < content.Length)
            {
                var chunk = Math.Min(buffer.Length, Math.Max(1, content.Length / 2));
                chunk = Math.Min(chunk, content.Length - _position);
                content.AsMemory(_position, chunk).CopyTo(buffer);
                _position += chunk;
                return ValueTask.FromResult(chunk);
            }
            return ValueTask.FromResult(failure is null ? 0 : failure(cancellationToken));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("the endpoints read asynchronously");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A host with the three HTTP receive endpoints (the callback overload, the registry-aware one with
    /// a recording handler, the session-aware callback overload), a capturing logger at Trace, and a
    /// middleware around the endpoints that records any exception escaping them. On TestServer, or on
    /// Kestrel at a loopback port (minimum body data rate 100 B/s after a 2 s grace; optionally a smaller MaxRequestBodySize).
    /// </summary>
    private sealed class BodyFailureHost : IAsyncDisposable
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        private readonly WebApplication _app;
        private readonly RecordingHandlerState _recorder;
        private int _callbacks;

        private BodyFailureHost(WebApplication app, RecordingHandlerState recorder, CapturingLoggerProvider logs)
        {
            _app = app;
            _recorder = recorder;
            Logs = logs;
        }

        public TestServer Server => _app.GetTestServer();

        public CapturingLoggerProvider Logs { get; }

        public ConcurrentQueue<Exception> Escaped { get; } = new();

        /// <summary>Messages handed to a callback or to the recording handler.</summary>
        public int Processed => Volatile.Read(ref _callbacks) + _recorder.Invocations;

        public int RequestsFinished => Logs.Entries.Count(e => e.StartsWith("Request finished HTTP/1.1 POST", StringComparison.Ordinal));

        public static async Task<BodyFailureHost> StartAsync(bool kestrel = false, long? maxRequestBodySize = null)
        {
            var logs = new CapturingLoggerProvider();
            var recorder = new RecordingHandlerState();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            if (kestrel)
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.WebHost.ConfigureKestrel(k =>
                {
                    k.Limits.MinRequestBodyDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(2));
                    if (maxRequestBodySize is { } max)
                        k.Limits.MaxRequestBodySize = max;
                });
            }
            else
            {
                builder.WebHost.UseTestServer();
            }
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton(recorder);
            builder.Services.AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(new InMemorySecretsResolver());
                b.AddProtocol<RecordingHandler>();
                b.Configure(o => o.MaxReceiveBytes = MaxReceiveBytes);
            });

            var app = builder.Build();
            BodyFailureHost? self = null;
            app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                try
                {
                    await next(context);
                }
                catch (Exception ex)
                {
                    self!.Escaped.Enqueue(ex);
                    throw;
                }
            });
            app.UseRouting();
            app.MapDidCommEndpoint(Callback, (_, _) =>
            {
                Interlocked.Increment(ref self!._callbacks);
                return Task.CompletedTask;
            });
            app.MapDidCommEndpoint(Registry);
            app.MapDidCommEndpoint(Session, (_, _, _) =>
            {
                Interlocked.Increment(ref self!._callbacks);
                return Task.FromResult<Message?>(null);
            });
            self = new BodyFailureHost(app, recorder, logs);
            await app.StartAsync();
            return self;
        }

        public async Task<TcpClient> ConnectRawAsync()
        {
            var address = new Uri(_app.Urls.Single());
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, address.Port);
            return tcp;
        }

        /// <summary>The request head of a POST to <paramref name="path"/>: Content-Length when given, chunked otherwise.</summary>
        public byte[] Head(string path, int? contentLength, bool expectContinue = true)
        {
            var address = new Uri(_app.Urls.Single());
            var length = contentLength is { } n ? $"Content-Length: {n}\r\n" : "Transfer-Encoding: chunked\r\n";
            var expect = expectContinue ? "Expect: 100-continue\r\n" : string.Empty;
            return Encoding.ASCII.GetBytes(
                $"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{address.Port}\r\nContent-Type: {DidCommMediaTypes.Plain}\r\n{length}{expect}\r\n");
        }

        public static async Task WaitUntilAsync(Func<bool> condition, string because)
            => (await ReturnRouteTestHost.WaitUntilAsync(condition, Timeout)).Should().BeTrue(because);

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
