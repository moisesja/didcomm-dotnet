using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Transports;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The level of each "dropped" line of the channel (QA-1 observation): a Warning only when the drop
/// points at a mediator problem — an answer to a waiting request, from its pair, that does not fit
/// it; an answer without a thread that did not come in the HTTP response to its request (#98: it
/// belongs to no request's thread); a push its stream's full buffer refuses — and a quiet Debug
/// line for a message of an answer type that simply is not for a waiting request (late, another
/// peer's, an application's problem report, a <c>disclose</c> another client takes) or a push that
/// arrives as or after its stream ends. Both kinds are rate-limited (the first and every 1000th at
/// their level, one level lower otherwise), and none of them claims the message.
/// </summary>
public sealed class MediatorClientLogLevelTests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ClientLogCapture _logs = new();
    private MediatorClientChannel _channel = null!;

    public async Task InitializeAsync()
    {
        _w = await ClientWorld.CreateAsync();
        _channel = Create(_w, _send, time: _time, logs: _logs);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _w.DisposeAsync();
    }

    public static TheoryData<string> NotForAWaitingRequest => new()
    {
        "a late answer naming no waiting request",
        "an application's problem report",
        "a disclose for a query this client did not send",
        "another peer's answer naming our request",
    };

    [Theory]
    [MemberData(nameof(NotForAWaitingRequest))]
    public async Task A_message_that_is_not_for_a_waiting_request_is_a_Debug_line_and_is_not_claimed(string message)
    {
        var (request, exchange) = Start();

        _channel.AsCorrelator().OnInbound(await SnapshotAsync(message, request.Id)).Should().BeFalse("not a claim");

        Dropped().Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug, message);
        exchange.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task An_answer_from_the_requests_mediator_naming_it_with_a_type_it_does_not_expect_is_a_Warning_naming_our_request()
    {
        var (request, exchange) = Start();

        _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeFalse();

        var line = Dropped().Should().ContainSingle().Which;
        line.Level.Should().Be(LogLevel.Warning);
        line.Formatted.Should().Contain(request.Id).And.Contain("mediate-request");
        exchange.IsCompleted.Should().BeFalse("it keeps waiting");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_answer_without_a_thread_in_the_HTTP_response_to_its_own_request_is_a_Warning_naming_only_its_type(bool ofAnExpectedType)
    {
        // Before #98 (PR #130 review, round 2 follow-up) a thread-less answer of an expected type
        // completed the request whose HTTP response carried it, and one of another type was a
        // Warning naming the request. Without thid it now belongs to no request's thread on any path.
        var answer = ofAnExpectedType ? _w.Grant(thid: null) : _w.Status(thid: null);
        _send.ReturnWith(async _ => await _w.ReceiveAsync(answer));
        var request = _w.Request(CoordinateMediation.MediateRequestType);

        await FluentActions.Awaiting(() => _channel.ExchangeAsync(HttpRoute, request, ClientSpecs.MediateRequest, _w.Mediator.Did, _w.Local.Did, default))
            .Should().ThrowAsync<TimeoutException>("no answer in the HTTP response");

        var line = Dropped().Should().ContainSingle().Which;
        line.Level.Should().Be(LogLevel.Warning);
        line.Formatted.Should().Contain("without a thread").And.Contain(ofAnExpectedType ? "mediate-grant" : "status");
        foreach (var identifier in _w.Identifiers)
            line.Everything.Should().NotContain(identifier);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task An_answer_without_a_thread_outside_an_HTTP_response_is_a_Warning_naming_only_its_type_however_many_requests_wait(int waiting)
    {
        // Before #98 a thread-less answer went to the only waiting request of its pair, and was a
        // Warning only when two could take it. It now belongs to no request's thread either way.
        for (var i = 0; i < waiting; i++)
            Start();

        var claimed = _channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Grant(thid: null)));

        claimed.Should().BeFalse();
        var line = _logs.Of<MediatorClientChannel>().Should().ContainSingle(l => l.Formatted.Contains("without a thread", StringComparison.Ordinal)).Subject;
        line.Level.Should().Be(LogLevel.Warning);
        line.Formatted.Should().Contain("mediate-grant");
        foreach (var identifier in _w.Identifiers)
            line.Everything.Should().NotContain(identifier);
    }

    [Fact]
    public async Task A_push_its_streams_full_buffer_refuses_is_a_Warning_and_one_with_no_stream_or_after_the_stream_ended_is_Debug()
    {
        var push = await _w.SnapshotAsync(_w.Delivery(thid: null));
        Start(); // not idle

        _channel.AsCorrelator().OnInbound(push).Should().BeFalse("no stream is open");
        using var stream = _channel.RegisterLiveStream(_w.Mediator.Did, _w.Local.Did);
        for (var i = 0; i < LiveRegistration.PushCapacity; i++)
            _channel.AsCorrelator().OnInbound(push).Should().BeTrue();
        _channel.AsCorrelator().OnInbound(push).Should().BeFalse("the buffer is full");
        stream.Complete();
        _channel.AsCorrelator().OnInbound(push).Should().BeFalse("the stream has ended");

        var pushes = _logs.Of<MediatorClientChannel>().Where(l => l.Formatted.Contains("Dropped a live delivery push", StringComparison.Ordinal)).ToArray();
        pushes.Select(l => (l.Level, Reason: l.State["Reason"]?.ToString())).Should().Equal(
            (LogLevel.Debug, "no live stream is open for its mediator and DID"),
            (LogLevel.Warning, "its stream's buffer is full"),
            (LogLevel.Trace, "its stream has ended"));
    }

    [Fact]
    public async Task The_quiet_line_is_Debug_the_first_and_every_1000th_time_and_Trace_otherwise()
    {
        Start();
        var late = await _w.SnapshotAsync(_w.Status(Guid.NewGuid().ToString()));

        for (var i = 0; i < 1000; i++)
            _channel.AsCorrelator().OnInbound(late).Should().BeFalse();

        var levels = Dropped().Select(l => l.Level).ToArray();
        levels.Should().HaveCount(1000);
        levels[0].Should().Be(LogLevel.Debug);
        levels[1..999].Should().OnlyContain(l => l == LogLevel.Trace);
        levels[999].Should().Be(LogLevel.Debug);
    }

    // ---- helpers ------------------------------------------------------------------------------

    // The channel's "a mediator answer was dropped or ignored" lines.
    private ClientLogEntry[] Dropped()
        => _logs.Of<MediatorClientChannel>().Where(l => l.Formatted.Contains("mediator answer", StringComparison.Ordinal)).ToArray();

    private (Messages.Message Request, Task<InboundMessageSnapshot> Exchange) Start()
    {
        var request = _w.Request(CoordinateMediation.MediateRequestType);
        return (request, _channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.MediateRequest, _w.Mediator.Did, _w.Local.Did, default).Observed());
    }

    private async Task<InboundMessageSnapshot> SnapshotAsync(string message, string requestId) => message switch
    {
        "a late answer naming no waiting request" => await _w.SnapshotAsync(_w.Grant(Guid.NewGuid().ToString())),
        "an application's problem report" => await _w.SnapshotAsync(_w.Problem(Guid.NewGuid().ToString(), code: "e.p.msg.app-specific", from: _w.Alice.Did)),
        "a disclose for a query this client did not send" => await _w.SnapshotAsync(_w.Answer(DiscoverFeatures.DiscloseType, Guid.NewGuid().ToString(), body: new JsonObject { ["disclosures"] = new JsonArray() })),
        "another peer's answer naming our request" => await _w.SnapshotAsync(_w.Grant(requestId, from: _w.Mallory.Did)),
        _ => throw new ArgumentOutOfRangeException(nameof(message)),
    };
}
