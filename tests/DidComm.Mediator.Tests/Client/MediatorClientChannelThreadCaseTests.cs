using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The matcher (#96 design §4.2) finds the waiting request by the answer's thread: <c>thid</c>, or a
/// problem report's <c>pthid</c>. DIDComm v2.1 threading (threading.md L9, L15, L21) compares those
/// with the request id ignoring letter case, so an answer naming the request id in upper case
/// completes it — through the dispatcher's correlator call and in the HTTP response alike. An id that
/// differs in more than case completes nothing.
/// </summary>
public sealed class MediatorClientChannelThreadCaseTests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private string M => _w.Mediator.Did;

    private string L => _w.Local.Did;

    private static string Upper(string requestId)
    {
        var upper = requestId.ToUpperInvariant();
        upper.Should().NotBe(requestId, "precondition: the request id has letters to re-case");
        return upper;
    }

    [Fact]
    public async Task An_answer_whose_thid_is_the_request_id_in_upper_case_completes_the_request()
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = _w.Request();
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, M, L, default).Observed();
        await _send.FirstCall;
        var answerSnapshot = await _w.SnapshotAsync(_w.Status(Upper(request.Id), count: 3));

        var claimed = channel.AsCorrelator().OnInbound(answerSnapshot);
        var answer = await PickupTest.Within(exchange);

        claimed.Should().BeTrue();
        answer.Should().BeSameAs(answerSnapshot, "the answer is handed over as it was sent");
    }

    [Fact]
    public async Task A_problem_report_whose_pthid_is_the_request_id_in_upper_case_fails_the_request_with_its_code()
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = _w.Request();
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, M, L, default).Observed();
        await _send.FirstCall;
        var report = await _w.SnapshotAsync(_w.Problem(Upper(request.Id), code: "e.p.req.not-enrolled"));

        var claimed = channel.AsCorrelator().OnInbound(report);
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(exchange));

        claimed.Should().BeTrue();
        thrown.Should().BeOfType<MediationProtocolException>().Which.ProblemCode.Should().Be("e.p.req.not-enrolled");
    }

    [Fact]
    public async Task In_the_HTTP_response_an_answer_whose_thid_is_the_request_id_in_upper_case_completes_the_request()
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = _w.Request();
        var upper = Upper(request.Id);
        _send.ReturnWith(async _ => await _w.ReceiveAsync(_w.Status(upper, count: 2)));

        var answer = await channel.ExchangeAsync(HttpRoute, request, ClientSpecs.StatusRequest, M, L, default);

        answer.Thid.Should().Be(upper);
    }

    [Fact]
    public async Task An_answer_whose_thread_differs_from_the_request_id_in_more_than_case_completes_nothing()
    {
        // Control for the tests above: the upper-cased id with one more character, as thid and as a
        // problem report's pthid, is another thread; the genuine answer then completes the request.
        await using var channel = Create(_w, _send, time: _time);
        var request = _w.Request();
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, M, L, default).Observed();
        await _send.FirstCall;
        var other = Upper(request.Id) + "0";
        var otherStatus = await _w.SnapshotAsync(_w.Status(other, count: 9));
        var otherProblem = await _w.SnapshotAsync(_w.Problem(other));
        var genuine = await _w.SnapshotAsync(_w.Status(request.Id, count: 0));

        var statusClaimed = channel.AsCorrelator().OnInbound(otherStatus);
        var problemClaimed = channel.AsCorrelator().OnInbound(otherProblem);
        var waitingAfterBoth = !exchange.IsCompleted;
        var genuineClaimed = channel.AsCorrelator().OnInbound(genuine);
        var answer = await PickupTest.Within(exchange);

        statusClaimed.Should().BeFalse();
        problemClaimed.Should().BeFalse();
        waitingAfterBoth.Should().BeTrue("an answer on another thread neither completes nor fails the request");
        genuineClaimed.Should().BeTrue();
        answer.Thid.Should().Be(request.Id);
    }
}
