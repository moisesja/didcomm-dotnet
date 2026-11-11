using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.InteropTests.Transports;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #92 QA pass — the HTTP response handling the G4/G7 suites leave out (every 2xx status, a 307 to a
/// non-200, a retried 5xx, a declared length shorter than the body, caller cancellation of a chunked
/// body) and how the facade turns each into <see cref="SendResult.Returned"/> or a
/// <see cref="TransportException"/> (a stalled or truncated body, a zero-length 200, a non-DIDComm type,
/// sends that asked for nothing). Then, end to end over WebSocket against the #91 TestServer agent: two
/// local DIDs whose session opens genuinely overlap, and a client handler that acknowledges a push
/// from inside its own dispatch (the pickup 4.0 live-mode shape) without deadlocking the session.
/// </summary>
public sealed class ReturnRouteClientQaTests
{
    private const int Cap = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Uri Endpoint = new("https://agents.r.us/inbox");

    // ---- HttpDidCommTransport: which responses are read ----

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Created, false)]
    [InlineData(HttpStatusCode.Accepted, false)]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation, false)]
    [InlineData(HttpStatusCode.NoContent, false)]
    [InlineData(HttpStatusCode.PartialContent, false)]
    public async Task Only_a_final_200_with_a_DIDComm_type_is_read(HttpStatusCode status, bool read)
    {
        var content = GeneratedContent.Generated(1024, declareLength: true);
        var (transport, logs) = Build(_ => new HttpResponseMessage(status) { Content = content });

        var result = await transport.SendAsync(Request(Cap), default);

        result.Accepted.Should().BeTrue();
        result.HttpStatusCode.Should().Be((int)status);
        result.ResponseDiscarded.Should().BeFalse();
        content.Opened.Should().Be(read, "only a 200 can carry a returned message (FR-RR-04)");
        (result.ResponsePayload is not null).Should().Be(read);
        logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_307_to_a_201_reads_neither_body()
    {
        var redirectBody = GeneratedContent.Generated(1024, declareLength: true);
        var finalBody = GeneratedContent.Generated(1024, declareLength: true);
        var (transport, _) = Build(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/inbox")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = redirectBody };
                redirect.Headers.Location = new Uri("/v2/inbox", UriKind.Relative);
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = finalBody };
        });

        var result = await transport.SendAsync(Request(Cap), default);

        result.Should().Be(new TransportResult(true, 201));
        redirectBody.Opened.Should().BeFalse();
        finalBody.Opened.Should().BeFalse();
    }

    [Fact]
    public async Task A_retried_503_then_200_reads_only_the_final_body()
    {
        var calls = 0;
        var unavailable = GeneratedContent.Generated(1024, declareLength: true);
        var final = GeneratedContent.Of(Encoding.UTF8.GetBytes("final"), MediaTypes.Encrypted);
        var (transport, _) = Build(_ => Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = unavailable }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = final },
            o =>
            {
                o.MaxRetryAttempts = 1;
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            });

        var result = await transport.SendAsync(Request(Cap), default);

        calls.Should().Be(2);
        Encoding.UTF8.GetString(result.ResponsePayload!.Value.Span).Should().Be("final");
        unavailable.Opened.Should().BeFalse();
    }

    [Fact]
    public async Task A_declared_length_shorter_than_the_body_reads_exactly_the_declared_bytes()
    {
        var content = GeneratedContent.Generated(100, declareLength: true, provide: 10_000);
        var (transport, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var result = await transport.SendAsync(Request(Cap), default);

        result.ResponsePayload!.Value.Length.Should().Be(100, "the buffer is the declared length, never grown by what follows");
        content.BytesPulled.Should().Be(100);
    }

    [Fact]
    public async Task Caller_cancellation_during_a_chunked_body_propagates_and_is_not_a_discard()
    {
        var content = GeneratedContent.Stalled(declareLength: false);
        var (transport, logs) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var cts = new CancellationTokenSource();

        var send = transport.SendAsync(Request(Cap), cts.Token);
        await content.ReadStarted.Task.WaitAsync(Timeout);
        cts.Cancel();

        await ((Func<Task>)(() => send)).Should().ThrowAsync<OperationCanceledException>();
        logs.Warnings.Should().BeEmpty();
    }

    [Fact(Skip = "QA-F03: a non-IO exception thrown while reading the body (InvalidDataException from a decompressing handler) escapes HttpDidCommTransport raw instead of ResponseDiscarded/ReadFailed")]
    public async Task QA_F03_A_body_that_fails_with_a_non_IO_exception_is_discarded_as_ReadFailed()
    {
        // ReadCappedAsync promises that a body that is "over the cap, late or unreadable yields an
        // accepted result with ResponseDiscarded", but it only catches IOException,
        // HttpRequestException and ObjectDisposedException. A response stream can fail otherwise: a
        // GZip/Deflate decompressing handler (AutomaticDecompression on the "didcomm" client) throws
        // InvalidDataException, Brotli throws InvalidOperationException. Either escapes the transport
        // and SendAsync raw — and InvalidOperationException is also what SendAsync documents for a
        // routed return-route refusal.
        var content = new ThrowingContent(new InvalidDataException("corrupt compressed body (test)"));
        var (transport, logs) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var result = await transport.SendAsync(Request(Cap), default);

        result.Accepted.Should().BeTrue();
        result.ResponseDiscarded.Should().BeTrue();
        logs.Warnings.Should().ContainSingle().Which.Should().Contain("ReadFailed");
    }

    // ---- the facade over HTTP ----

    public static TheoryData<string> UnreadableBodies() => new() { "stalled", "truncated", "failing" };

    [Theory]
    [MemberData(nameof(UnreadableBodies))]
    public async Task An_unreadable_returned_body_fails_an_All_send_with_TransportException_200(string @case)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        Func<HttpMessageHandler> stub = () => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = @case switch
            {
                "stalled" => GeneratedContent.Stalled(declareLength: false),
                "truncated" => GeneratedContent.Generated(4096, declareLength: true, provide: 1000),
                _ => GeneratedContent.Generated(4096, declareLength: false, failAfter: 1000),
            },
        });
        await using var client = ReturnRouteClientHarness.Create(host, stub, o => o.RequestTimeout = TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        var act = () => client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        var thrown = (await act.Should().ThrowAsync<TransportException>(@case)).Which;
        watch.Elapsed.Should().BeLessThan(Timeout, "the body read is bounded by RequestTimeout");
        thrown.HttpStatusCode.Should().Be(200, "accepted, but unreadable: never silent loss");
        thrown.Message.Should().NotContain("did:").And.NotContain("localhost");
        host.LeakingLogLines().Should().BeEmpty();
    }

    public static TheoryData<string> NothingToReturn() => new() { "zero-length 200", "application/json 200", "202" };

    [Theory]
    [MemberData(nameof(NothingToReturn))]
    public async Task An_All_send_whose_response_carries_no_DIDComm_body_is_accepted_with_Returned_null(string @case)
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var contents = new ConcurrentQueue<GeneratedContent>();
        Func<HttpMessageHandler> stub = () => new StubHandler(_ =>
        {
            var content = @case switch
            {
                "zero-length 200" => GeneratedContent.Generated(0, declareLength: false),
                "application/json 200" => GeneratedContent.Generated(1024, declareLength: true, contentType: "application/json"),
                _ => GeneratedContent.Generated(1024, declareLength: true),
            };
            contents.Enqueue(content);
            return new HttpResponseMessage(@case == "202" ? HttpStatusCode.Accepted : HttpStatusCode.OK) { Content = content };
        });
        await using var client = ReturnRouteClientHarness.Create(host, stub);

        var result = await client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        result.Transport.Accepted.Should().BeTrue();
        result.Returned.Should().BeNull(@case);
        result.Transport.ResponseDiscarded.Should().BeFalse();
        if (@case != "zero-length 200")
            contents.Should().OnlyContain(c => !c.Opened, "a non-200 or non-DIDComm body is never read");
        host.LeakingLogLines().Should().BeEmpty();
    }

    public static TheoryData<string> SendsThatReadNothing() => new() { "header-less authcrypt", "none", "anoncrypt all", "anoncrypt thread" };

    [Theory]
    [MemberData(nameof(SendsThatReadNothing))]
    public async Task A_send_that_cannot_use_a_returned_message_never_opens_the_response_body(string @case)
    {
        // JC-3/JC-5: a 100 MB DIDComm 200 costs nothing unless the send asked for it and can verify it.
        await using var host = await ReturnRouteTestHost.StartAsync();
        var contents = new ConcurrentQueue<GeneratedContent>();
        Func<HttpMessageHandler> stub = () => new StubHandler(_ =>
        {
            var content = GeneratedContent.Generated(100L * 1024 * 1024, declareLength: false);
            contents.Enqueue(content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        await using var client = ReturnRouteClientHarness.Create(host, stub);
        var anoncrypt = @case.StartsWith("anoncrypt", StringComparison.Ordinal);
        var message = anoncrypt
            ? new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/probe").WithTo(host.ServerDid).Build()
            : host.RecordingMessage(host.Alice).Build();
        var options = anoncrypt ? client.ToServer() with { From = null } : client.ToServer();
        options = @case switch
        {
            "none" => options with { ReturnRoute = ReturnRouteMode.None },
            "anoncrypt all" => options with { ReturnRoute = ReturnRouteMode.All },
            "anoncrypt thread" => options with { ReturnRoute = ReturnRouteMode.Thread, ReturnRouteThread = "thr-qa" },
            _ => options,
        };

        var result = await client.Client.SendAsync(message, options);

        result.Transport.Should().Be(new TransportResult(true, 200));
        result.Returned.Should().BeNull();
        contents.Should().ContainSingle().Which.Opened.Should().BeFalse(@case);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- WebSocket, end to end ----

    [Fact]
    public async Task Two_local_DIDs_whose_session_opens_overlap_get_two_sockets_and_their_own_replies()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/reply")
            .WithFrom(host.ServerDid)
            .WithTo(message.From!)
            .WithThid(message.Id)
            .Build());
        // Both connects must arrive before either proceeds: the two opens genuinely overlap. The
        // rendezvous parks a pool thread per party, so it raises the pool's floor (CI has 2 cores).
        var rendezvous = new Rendezvous(2, Timeout);
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, configure: o => o.Connect = async (socket, uri, ct) =>
        {
            await Task.Run(() => rendezvous.Run(() => 0), ct).ConfigureAwait(false);
            await ((TestServerClientWebSocket)socket).ConnectAsync(host.Server, uri, ct).ConfigureAwait(false);
        }, holders: new[] { host.Alice, host.Carol });
        var fromAlice = host.RecordingMessage(host.Alice, "a").Build();
        var fromCarol = host.RecordingMessage(host.Carol, "c").Build();

        await Task.WhenAll(
            client.Client.SendAsync(fromAlice, client.ToServer(host.Alice) with { ReturnRoute = ReturnRouteMode.All }),
            client.Client.SendAsync(fromCarol, client.ToServer(host.Carol) with { ReturnRoute = ReturnRouteMode.All })).WaitAsync(Timeout);

        rendezvous.MaxConcurrent.Should().Be(2, "the two opens overlapped: one gate per (E, L, P), not per endpoint");
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 2, Timeout)).Should().BeTrue();
        var replies = client.Recorder.Seen.ToDictionary(s => s.Message.Thid!);
        replies[fromAlice.Id].Message.To.Should().Equal(host.Alice.Did);
        replies[fromCarol.Id].Message.To.Should().Equal(host.Carol.Did);
        client.Transport.ActiveSessionSocketCount.Should().Be(2);
        host.Registry.Count.Should().Be(2);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_handler_that_acknowledges_a_push_from_inside_its_dispatch_sends_over_S_without_deadlock()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        host.Recorder.Behavior = (message, _) => message.Type.EndsWith("/start", StringComparison.Ordinal)
            ? (new[]
            {
                new MessageBuilder().WithType(AckingHandler.ProtocolUri + "/work").WithFrom(host.ServerDid).WithTo(host.Alice.Did).WithThid(message.Id).Build(),
                new MessageBuilder().WithType(AckingHandler.ProtocolUri + "/work").WithFrom(host.ServerDid).WithTo(host.Alice.Did).WithThid(message.Id).Build(),
            }, null)
            : (Array.Empty<Message>(), null);
        var acking = new AckingState();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, configureDidComm: b =>
        {
            b.Services.AddSingleton(acking);
            b.AddProtocol<AckingHandler>();
        });
        acking.ServerDid = host.ServerDid;
        acking.From = host.Alice.Did;
        acking.Endpoint = client.Endpoint;

        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "start").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Seen.Count(s => s.Message.Type.EndsWith("/ack", StringComparison.Ordinal)) == 2, Timeout))
            .Should().BeTrue("each push was acknowledged by a send made from inside its own dispatch");
        acking.Faults.Should().BeEmpty();
        host.Recorder.Seen.Where(s => s.Message.Type.EndsWith("/ack", StringComparison.Ordinal))
            .Should().OnlyContain(s => s.Session != null, "the header-less acknowledgement rode the session socket");
        client.Connects.Should().Be(1);
        client.Transport.ActiveSessionSocketCount.Should().Be(1);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Through_the_real_receive_loop_only_the_peers_authcrypt_push_to_the_local_DID_reaches_a_handler()
    {
        // The whole client pipe — session socket loop → built-in consumer → identity rule → admission →
        // dispatch — fed by a scripted peer socket, with pushes packed by real peers.
        await using var host = await ReturnRouteTestHost.StartAsync();
        var sockets = new ScriptedSocketFactory();
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, configure: o =>
        {
            o.WebSocketFactory = sockets.Create;
            o.Connect = (_, _, _) => Task.CompletedTask;
        }, holders: new[] { host.Alice, host.Carol });
        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "open").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });
        var session = sockets.Sockets.Single();
        var server = host.App.Services.GetRequiredService<DidCommClient>();

        async Task<byte[]> Pack(Message message, string to, string? from, DidCommClient? packer = null)
            => Encoding.UTF8.GetBytes((await (packer ?? server).PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }, From: from))).Message);
        Message App(string name, string from, string to)
            => new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/" + name).WithFrom(from).WithTo(to).Build();

        var good = App("good", host.ServerDid, host.Alice.Did);
        session.PushMessage(await Pack(App("mallory", host.Mallory.Did, host.Alice.Did), host.Alice.Did, host.Mallory.Did, host.Mallory.Client));
        session.PushMessage(await Pack(App("to-carol", host.ServerDid, host.Carol.Did), host.Carol.Did, host.ServerDid));
        session.PushMessage(await Pack(App("anoncrypt", host.ServerDid, host.Alice.Did), host.Alice.Did, from: null));
        session.PushMessage(Encoding.UTF8.GetBytes(await server.PackPlaintextAsync(App("plaintext", host.ServerDid, host.Alice.Did))));
        session.PushMessage(Encoding.UTF8.GetBytes("{ not json"));
        session.PushMessage(await Pack(good, host.Alice.Did, host.ServerDid));

        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 1, Timeout)).Should().BeTrue("the last push is the peer's authcrypt to the local DID");
        client.Recorder.Seen.Single().Message.Id.Should().Be(good.Id);
        client.Recorder.Seen.Single().Session.Should().BeNull();
        client.Recorder.Invocations.Should().Be(1, "no other push reached a handler");
        session.Sent.Should().HaveCount(1, "the loop sent nothing back: no reply, no reflector");
        client.Transport.ActiveSessionSocketCount.Should().Be(1, "hostile pushes do not end the session");
        host.Logs.Entries.Count(l => l.Contains("Pushed message dropped", StringComparison.Ordinal)).Should().Be(5);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- helpers ----

    private static TransportRequest Request(int? maxResponseBytes)
        => new(Endpoint, new byte[] { 0x7b, 0x7d }, MediaTypes.Encrypted) { MaxResponseBytes = maxResponseBytes };

    private static (HttpDidCommTransport Transport, HttpTransportResponseTests.ListLogger Logs) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        Action<HttpTransportOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddOptions<HttpTransportOptions>().Configure(o =>
        {
            o.AllowedSchemes = new[] { "https", "http" };
            o.MaxRetryAttempts = 0;
            o.CircuitBreakerFailureThreshold = 100;
            o.RequestTimeout = TimeSpan.FromSeconds(30);
            configure?.Invoke(o);
        });
        services.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => new StubHandler(responder));
        var sp = services.BuildServiceProvider();
        var logs = new HttpTransportResponseTests.ListLogger();
        var transport = new HttpDidCommTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<HttpTransportOptions>>(),
            logs);
        return (transport, logs);
    }

    /// <summary>A DIDComm-typed, chunked body whose stream fails with the given exception on its first read.</summary>
    private sealed class ThrowingContent : HttpContent
    {
        private readonly Exception _failure;

        public ThrowingContent(Exception failure)
        {
            _failure = failure;
            Headers.TryAddWithoutValidation("Content-Type", MediaTypes.Encrypted);
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new FailingStream(_failure));

        private sealed class FailingStream : Stream
        {
            private readonly Exception _failure;

            public FailingStream(Exception failure) => _failure = failure;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => 0; set => throw new NotSupportedException(); }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(_failure);

            public override int Read(byte[] buffer, int offset, int count) => throw _failure;

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    /// <summary>What <see cref="AckingHandler"/> needs to answer, and what went wrong.</summary>
    internal sealed class AckingState
    {
        public string ServerDid { get; set; } = string.Empty;

        public string From { get; set; } = string.Empty;

        public Uri? Endpoint { get; set; }

        public ConcurrentQueue<string> Faults { get; } = new();
    }

    /// <summary>
    /// A client handler that, like a pickup 4.0 recipient in live mode, acknowledges each pushed message
    /// with an explicit header-less send made from inside its own dispatch.
    /// </summary>
    internal sealed class AckingHandler : IProtocolHandler
    {
        public const string ProtocolUri = "https://didcomm.org/test-qa-ack/1.0";

        private readonly AckingState _state;

        public AckingHandler(AckingState state) => _state = state;

        string IProtocolHandler.ProtocolUri => ProtocolUri;

        public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            try
            {
                var ack = new MessageBuilder()
                    .WithType(RecordingHandler.ProtocolUri + "/ack")
                    .WithFrom(_state.From)
                    .WithTo(_state.ServerDid)
                    .WithThid(message.Id)
                    .Build();
                await context.Client!.SendAsync(ack, new SendOptions(Recipients: new[] { _state.ServerDid }, From: _state.From, ServiceEndpointOverride: _state.Endpoint), ct);
            }
            catch (Exception ex)
            {
                _state.Faults.Enqueue(ex.GetType().Name);
            }
            return null;
        }
    }
}
