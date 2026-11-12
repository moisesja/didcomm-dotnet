using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DidComm.Messages;
using DidComm.Transports;
using DidComm.Transports.Http;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #92 G4 — <see cref="HttpDidCommTransport"/>'s in-band response read (FR-RR-07, JC-2/JC-3): a
/// body is read only for a final <c>200</c> with a DIDComm <c>Content-Type</c> and only when the
/// request carries <see cref="TransportRequest.MaxResponseBytes"/>; it is capped before any
/// allocation that scales with it, bounded in time, and a body that is over the cap, late or broken
/// is discarded (the send still counts as accepted) with one Warning that names no URL. The bodies
/// come from a lazily generating, read-counting <see cref="HttpContent"/>, so a 100 MB response costs
/// nothing unless it is actually pulled.
/// </summary>
public sealed class HttpTransportResponseTests
{
    private const int Cap = 64 * 1024;
    private const int Chunk = 16 * 1024;
    private const long HundredMegabytes = 100L * 1024 * 1024;
    private static readonly Uri Endpoint = new("https://agents.r.us/inbox");

    public static TheoryData<string, string> DidCommContentTypes() => new()
    {
        { "application/didcomm-encrypted+json", MediaTypes.Encrypted },
        { "application/didcomm-signed+json", MediaTypes.Signed },
        { "application/didcomm-plain+json", MediaTypes.Plaintext },
        { "application/didcomm-encrypted+json; charset=utf-8", MediaTypes.Encrypted },
        { "APPLICATION/DIDCOMM-ENCRYPTED+JSON", MediaTypes.Encrypted },
    };

    [Theory]
    [MemberData(nameof(DidCommContentTypes))]
    public async Task Status_200_with_each_DIDComm_type_is_read_when_the_request_asks(string contentType, string canonical)
    {
        var body = Encoding.UTF8.GetBytes("{\"ciphertext\":\"abc\"}");
        var content = GeneratedContent.Of(body, contentType);
        var (transport, logs) = Build(_ => Respond(HttpStatusCode.OK, content));

        var result = await transport.SendAsync(Request(Cap), default);

        result.Accepted.Should().BeTrue();
        result.HttpStatusCode.Should().Be(200);
        result.ResponseDiscarded.Should().BeFalse();
        result.ResponsePayload!.Value.ToArray().Should().Equal(body);
        result.ResponseMediaType.Should().Be(canonical, "the canonical constant that matched");
        logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Status_202_reads_nothing()
    {
        var content = GeneratedContent.Generated(1024, declareLength: true);
        var (transport, _) = Build(_ => Respond(HttpStatusCode.Accepted, content));

        var result = await transport.SendAsync(Request(Cap), default);

        result.Should().Be(new TransportResult(true, 202));
        content.Opened.Should().BeFalse("only a 200 can carry a returned message (FR-RR-04)");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData(null)]
    public async Task Non_DIDComm_type_reads_nothing(string? contentType)
    {
        var content = GeneratedContent.Generated(1024, declareLength: true, contentType);
        var (transport, _) = Build(_ => Respond(HttpStatusCode.OK, content));

        var result = await transport.SendAsync(Request(Cap), default);

        result.Should().Be(new TransportResult(true, 200));
        content.Opened.Should().BeFalse();
    }

    [Fact]
    public async Task Without_MaxResponseBytes_the_body_is_never_read()
    {
        var content = GeneratedContent.Generated(HundredMegabytes, declareLength: false);
        var (transport, _) = Build(_ => Respond(HttpStatusCode.OK, content));

        var result = await transport.SendAsync(Request(maxResponseBytes: null), default);

        result.Should().Be(new TransportResult(true, 200), "JC-3: a send that asked for nothing reads nothing");
        content.Opened.Should().BeFalse();
        content.BytesPulled.Should().Be(0);
    }

    [Fact]
    public async Task Over_cap_Content_Length_is_discarded_without_reading_a_byte()
    {
        var content = GeneratedContent.Generated(HundredMegabytes, declareLength: true);
        var (transport, logs) = Build(_ => Respond(HttpStatusCode.OK, content));

        var result = await transport.SendAsync(Request(Cap), default);

        result.Accepted.Should().BeTrue("the request itself was delivered");
        result.HttpStatusCode.Should().Be(200);
        result.ResponseDiscarded.Should().BeTrue();
        result.ResponsePayload.Should().BeNull();
        content.Opened.Should().BeFalse("the declared length alone refuses it");
        content.BytesPulled.Should().Be(0);
        var warning = logs.Warnings.Should().ContainSingle().Which;
        warning.Should().Contain("OverCap").And.Contain(Cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
        warning.Should().NotContain("agents.r.us", "no URL in the discard line");
    }

    [Fact]
    public async Task Over_cap_chunked_100_MB_body_is_cut_at_cap_plus_one_chunk()
    {
        var content = GeneratedContent.Generated(HundredMegabytes, declareLength: false);
        var (transport, logs) = Build(_ => Respond(HttpStatusCode.OK, content));

        var result = await transport.SendAsync(Request(Cap), default);

        result.Accepted.Should().BeTrue();
        result.ResponseDiscarded.Should().BeTrue();
        result.ResponsePayload.Should().BeNull();
        content.BytesPulled.Should().BeLessThanOrEqualTo(Cap + Chunk, "reading stops as soon as the total exceeds the cap");
        content.BytesPulled.Should().BeGreaterThan(Cap);
        logs.Warnings.Should().ContainSingle().Which.Should().Contain("OverCap").And.NotContain("agents.r.us");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exactly_cap_is_accepted_and_cap_plus_one_is_discarded(bool declareLength)
    {
        var exact = GeneratedContent.Generated(Cap, declareLength);
        var over = GeneratedContent.Generated(Cap + 1, declareLength);
        var (transport, _) = Build(request => Respond(HttpStatusCode.OK, request.RequestUri!.AbsolutePath == "/exact" ? exact : over));

        var accepted = await transport.SendAsync(Request(Cap, new Uri("https://agents.r.us/exact")), default);
        var discarded = await transport.SendAsync(Request(Cap, new Uri("https://agents.r.us/over")), default);

        accepted.ResponseDiscarded.Should().BeFalse();
        accepted.ResponsePayload!.Value.Length.Should().Be(Cap);
        accepted.ResponsePayload.Value.ToArray().Should().OnlyContain(b => b == GeneratedContent.Fill);
        discarded.ResponseDiscarded.Should().BeTrue();
        discarded.ResponsePayload.Should().BeNull();
        discarded.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task A_stalled_body_is_discarded_after_RequestTimeout_and_still_accepted()
    {
        var content = GeneratedContent.Stalled(declareLength: false);
        var (transport, logs) = Build(_ => Respond(HttpStatusCode.OK, content), o => o.RequestTimeout = TimeSpan.FromMilliseconds(200));

        var send = transport.SendAsync(Request(Cap), default);
        var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.Should().BeSameAs(send, "the body read is bounded by RequestTimeout; the resilience pipeline's timeout ends with the headers");
        var result = await send;
        result.Accepted.Should().BeTrue();
        result.ResponseDiscarded.Should().BeTrue();
        logs.Warnings.Should().ContainSingle().Which.Should().Contain("ReadTimeout");
    }

    [Fact]
    public async Task Caller_cancellation_during_the_body_propagates()
    {
        var content = GeneratedContent.Stalled(declareLength: true, length: 1024);
        var (transport, logs) = Build(_ => Respond(HttpStatusCode.OK, content));
        using var cts = new CancellationTokenSource();

        var send = transport.SendAsync(Request(Cap), cts.Token);
        await content.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await ((Func<Task>)(() => send)).Should().ThrowAsync<OperationCanceledException>();
        logs.Warnings.Should().BeEmpty("the caller's cancellation is not a discard");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failing_or_truncated_body_is_discarded_as_ReadFailed(bool truncated)
    {
        var content = truncated
            ? GeneratedContent.Generated(4096, declareLength: true, provide: 1000)
            : GeneratedContent.Generated(4096, declareLength: false, failAfter: 1000);
        var (transport, logs) = Build(_ => Respond(HttpStatusCode.OK, content));

        var result = await transport.SendAsync(Request(Cap), default);

        result.Accepted.Should().BeTrue();
        result.ResponseDiscarded.Should().BeTrue();
        logs.Warnings.Should().ContainSingle().Which.Should().Contain("ReadFailed");
    }

    [Fact]
    public async Task A_307_then_200_reads_the_final_body()
    {
        var redirectBody = GeneratedContent.Generated(1024, declareLength: true);
        var finalBody = GeneratedContent.Of(Encoding.UTF8.GetBytes("final"), MediaTypes.Encrypted);
        var (transport, _) = Build(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/inbox")
            {
                var redirect = Respond(HttpStatusCode.TemporaryRedirect, redirectBody);
                redirect.Headers.Location = new Uri("https://agents.r.us/v2/inbox");
                return redirect;
            }
            return Respond(HttpStatusCode.OK, finalBody);
        });

        var result = await transport.SendAsync(Request(Cap), default);

        Encoding.UTF8.GetString(result.ResponsePayload!.Value.Span).Should().Be("final");
        redirectBody.Opened.Should().BeFalse("only the final hop's body is read");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Zero_length_200_yields_no_payload(bool declareLength)
    {
        var content = GeneratedContent.Generated(0, declareLength);
        var (transport, logs) = Build(_ => Respond(HttpStatusCode.OK, content));

        var result = await transport.SendAsync(Request(Cap), default);

        result.Should().Be(new TransportResult(true, 200), "an empty body is no payload, not a discard");
        logs.Warnings.Should().BeEmpty();
    }

    // ---- helpers ----

    private static TransportRequest Request(int? maxResponseBytes, Uri? endpoint = null)
        => new(endpoint ?? Endpoint, new byte[] { 0x7b, 0x7d }, MediaTypes.Encrypted) { MaxResponseBytes = maxResponseBytes };

    private static HttpResponseMessage Respond(HttpStatusCode status, HttpContent content) => new(status) { Content = content };

    private static (HttpDidCommTransport Transport, ListLogger Logs) Build(
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
        var logs = new ListLogger();
        var transport = new HttpDidCommTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<HttpTransportOptions>>(),
            logs);
        return (transport, logs);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    internal sealed class ListLogger : ILogger<HttpDidCommTransport>
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();

        public IReadOnlyList<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? string.Empty : Environment.NewLine + exception)));
    }
}

/// <summary>
/// An <see cref="HttpContent"/> whose bytes are generated on demand as they are read, counting every
/// byte pulled, so a test can serve a 100 MB body for free and prove how much of it was read. It can
/// declare its length or not (chunked), stop short of it (truncated), fail with an
/// <see cref="IOException"/> after some bytes, or stall until cancelled.
/// </summary>
internal sealed class GeneratedContent : HttpContent
{
    public const byte Fill = (byte)'a';

    private readonly byte[]? _explicit;
    private readonly long _declared;
    private readonly bool _declareLength;
    private readonly long _provide;
    private readonly long? _failAfter;
    private readonly bool _stall;
    private long _pulled;
    private int _opened;

    private GeneratedContent(byte[]? bytes, long declared, bool declareLength, long provide, long? failAfter, bool stall, string? contentType)
    {
        _explicit = bytes;
        _declared = declared;
        _declareLength = declareLength;
        _provide = provide;
        _failAfter = failAfter;
        _stall = stall;
        if (contentType is not null)
            Headers.TryAddWithoutValidation("Content-Type", contentType);
    }

    public static GeneratedContent Of(byte[] bytes, string contentType)
        => new(bytes, bytes.Length, declareLength: true, bytes.Length, failAfter: null, stall: false, contentType);

    public static GeneratedContent Generated(long length, bool declareLength, string? contentType = MediaTypes.Encrypted, long? provide = null, long? failAfter = null)
        => new(null, length, declareLength, provide ?? length, failAfter, stall: false, contentType);

    public static GeneratedContent Stalled(bool declareLength, long length = 1024)
        => new(null, length, declareLength, length, failAfter: null, stall: true, MediaTypes.Encrypted);

    /// <summary>Whether anything opened the content's stream.</summary>
    public bool Opened => Volatile.Read(ref _opened) == 1;

    /// <summary>Bytes handed out so far.</summary>
    public long BytesPulled => Interlocked.Read(ref _pulled);

    /// <summary>Completed when the first read starts.</summary>
    public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => throw new NotSupportedException("The transport must read through ReadAsStreamAsync.");

    protected override bool TryComputeLength(out long length)
    {
        length = _declared;
        return _declareLength;
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        Interlocked.Exchange(ref _opened, 1);
        return Task.FromResult<Stream>(new Reader(this));
    }

    private sealed class Reader : Stream
    {
        private readonly GeneratedContent _owner;
        private long _position;

        public Reader(GeneratedContent owner) => _owner = owner;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _owner.ReadStarted.TrySetResult();
            if (_owner._stall)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (_owner._failAfter is long failAt && _position >= failAt)
                throw new IOException("connection reset (test)");
            var limit = _owner._failAfter is long f ? Math.Min(f, _owner._provide) : _owner._provide;
            var n = (int)Math.Min(buffer.Length, limit - _position);
            if (n <= 0)
                return 0;
            if (_owner._explicit is { } bytes)
                bytes.AsSpan((int)_position, n).CopyTo(buffer.Span);
            else
                buffer.Span[..n].Fill(Fill);
            _position += n;
            Interlocked.Add(ref _owner._pulled, n);
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
