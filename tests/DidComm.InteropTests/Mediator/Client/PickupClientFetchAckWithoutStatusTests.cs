using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// The acknowledgement <see cref="PickupClient.FetchAsync"/> sends by itself after a delivery, against
/// a mediator that applies a <c>messages-received</c> and sends no <c>status</c> back (#98). Message
/// Pickup 3.0 says the mediator SHOULD send an updated status, not that it must; the Identus mediator
/// v1.2.1 sends none (over HTTP, a 202 with an empty body). The fetch does not read that status, so
/// an empty 202 is not a failure: nothing is logged as a failed acknowledgement. A problem report and
/// a mediator that does not answer in time are still logged, as before. The stand-in is the one of
/// <see cref="PickupClientAckWithoutStatusTests"/>: this library's mediator, every request handled for
/// real, only the answer reshaped.
/// </summary>
public sealed class PickupClientFetchAckWithoutStatusTests
{
    private const string DeliveryRequest = "delivery-request";
    private const string MessagesReceived = "messages-received";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_fetch_whose_acknowledgement_is_answered_with_an_empty_202_logs_no_failure_and_the_acknowledgement_is_applied()
    {
        // The Identus shape: messages-received applied, answered with nothing.
        var observer = new PickupRequestObserver();
        await using var mediator = await StartAsync(observer, (request, reply) => Is(request, MessagePickup.MessagesReceivedType) ? null : reply);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient, alice, note, before) = await QueueNoteAsync(mediator, harness, observer);

        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10));

        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        AckFailures(harness).Should().BeEmpty("an empty 202 is the mediator sending no status, which Message Pickup 3.0 allows");
        // No status-request: a fetch does not ask for the status it does not read.
        await ShouldHaveSentAsync(mediator, observer, bob, before, DeliveryRequest, MessagesReceived);
        mediator.Store.MessageCount.Should().Be(0, "the mediator applied the acknowledgement");
        (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did))).MessageCount.Should().Be(0, "the next status shows the acknowledgement applied");
    }

    [Fact]
    public async Task A_fetch_whose_acknowledgement_is_answered_with_a_problem_report_still_logs_the_failure_and_returns_its_batch()
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await StartAsync(observer, (request, reply) => Is(request, MessagePickup.MessagesReceivedType)
            ? ProblemReports.Create(request.To![0], request.From!, "e.p.req.refused-for-test", request.Id)
            : reply);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient, alice, note, before) = await QueueNoteAsync(mediator, harness, observer);

        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10));

        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        var failure = AckFailures(harness).Should().ContainSingle().Subject;
        failure.Level.Should().Be(LogLevel.Warning);
        failure.Text.Should().Contain(nameof(MediationProtocolException));
        await ShouldHaveSentAsync(mediator, observer, bob, before, DeliveryRequest, MessagesReceived);
    }

    [Fact]
    public async Task A_fetch_whose_acknowledgement_is_not_answered_in_time_still_logs_the_failure_after_one_RequestTimeout()
    {
        var time = new QaManualTimeProvider();
        var held = new HeldAnswer(MessagePickup.MessagesReceivedType);
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer, o =>
            o.PostConfigureServices = services => services.AddSingleton<IProtocolHandler>(
                sp => new HeldAnswerPickupHandler(sp.GetRequiredService<MessagePickupHandler>(), held)));
        try
        {
            await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
            {
                o.Time = time;
                o.Client = c => c.RequestTimeout = RequestTimeout;
            });
            var (bob, recipient, alice, note, _) = await QueueNoteAsync(mediator, harness, observer);

            var fetch = harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
            // The mediator holds the acknowledgement's answer, so its deadline is already running
            // on the client's clock (it starts before the request is sent).
            await held.Reached.WaitAsync(MediatorTestServer.Timeout);
            fetch.IsCompleted.Should().BeFalse("the acknowledgement's answer is held");
            time.Advance(RequestTimeout);
            var batch = await PickupE2E.Within(fetch);

            PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
            AckFailures(harness).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
            held.Count.Should().Be(1, "one acknowledgement was sent, and nothing more");
        }
        finally
        {
            held.Release();
        }
    }

    [Fact]
    public async Task Over_a_WebSocket_a_fetch_whose_acknowledgement_gets_no_status_is_still_logged_after_one_RequestTimeout()
    {
        // Unchanged on a WebSocket: silence there cannot be told from a slow answer.
        var time = new QaManualTimeProvider();
        var observer = new PickupRequestObserver();
        await using var mediator = await StartAsync(observer, (request, reply) => Is(request, MessagePickup.MessagesReceivedType) ? null : reply);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.Client = c => c.RequestTimeout = RequestTimeout;
        });
        var (bob, recipient, alice, note, before) = await QueueNoteAsync(mediator, harness, observer);

        var fetch = harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10, serviceEndpointOverride: harness.WebSocketEndpoint);
        // The mediator handled the acknowledgement, so its deadline is already running on the
        // client's clock (it starts before the request is sent).
        await observer.WaitForAsync(bob.Did, before + 2);
        fetch.IsCompleted.Should().BeFalse("on a WebSocket the client waits for the status");
        time.Advance(RequestTimeout);
        var batch = await PickupE2E.Within(fetch);

        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        AckFailures(harness).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        mediator.Store.MessageCount.Should().Be(0, "the mediator applied it all the same");
    }

    // ---- helpers ------------------------------------------------------------------------------

    // A mediator whose pickup answers are reshaped by `answer`.
    private static Task<MediatorTestServer> StartAsync(PickupRequestObserver observer, Func<Message, Message?, Message?> answer)
        => PickupE2E.StartMediatorAsync(observer, o =>
            // Registered after AddMediator(): the protocol registry is last-registration-wins.
            o.PostConfigureServices = services => services.AddSingleton<IProtocolHandler>(
                sp => new ReshapedPickupHandler(sp.GetRequiredService<MessagePickupHandler>(), answer)));

    // Bob enrols and Alice's note is queued for his recipient DID. `Before` is the baseline of the
    // pickup messages the mediator received from Bob, read once its observer queue is drained: none.
    private static async Task<(TestPeer Bob, TestPeer Recipient, MediatorSender Alice, Message Note, int Before)> QueueNoteAsync(MediatorTestServer mediator, MediatorClientHarness harness, PickupRequestObserver observer)
    {
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient);
        mediator.Store.MessageCount.Should().Be(1);
        var before = await observer.KindsFromAsync(mediator, bob.Did, 0);
        before.Should().BeEmpty("enrolment is not Message Pickup");
        return (bob, recipient, alice, note, before.Count);
    }

    // The client's "acknowledgement failed" lines (PickupLog.AckFailed), at any level.
    private static IReadOnlyList<(LogLevel Level, string Text)> AckFailures(MediatorClientHarness harness)
        => harness.Logs.CategorizedRecords
            .Where(r => r.Text.StartsWith("Acknowledgement ", StringComparison.Ordinal) && r.Text.Contains("the mediator delivers them again", StringComparison.Ordinal))
            .Select(r => (r.Level, r.Text))
            .ToArray();

    // What the mediator received from Bob after the first `before` requests is exactly `expected`.
    // The observer records asynchronously (a finished call's last request can still be on its
    // way), so the records asserted on are waited for and the observer queue drained first.
    private static async Task ShouldHaveSentAsync(MediatorTestServer mediator, PickupRequestObserver observer, TestPeer bob, int before, params string[] expected)
        => (await observer.KindsFromAsync(mediator, bob.Did, before + expected.Length)).Skip(before).Should().Equal(expected);

    private static bool Is(Message message, string type) => string.Equals(message.Type, type, StringComparison.Ordinal);
}

/// <summary>
/// The answer the stand-in holds back: requests of one type are applied, then their answer waits
/// until <see cref="Release"/> (or until the request is abandoned).
/// </summary>
internal sealed class HeldAnswer(string type)
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public string Type => type;

    /// <summary>Completes when the first request of the type was applied and its answer is being held.</summary>
    public Task Reached => _reached.Task;

    /// <summary>How many requests of the type were held.</summary>
    public int Count => Volatile.Read(ref _count);

    public void Release() => _release.TrySetResult();

    public async Task HoldAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _count);
        _reached.TrySetResult();
        await _release.Task.WaitAsync(ct);
    }
}

/// <summary>
/// Message Pickup 3.0 for the stand-in mediator: every request goes to this library's
/// <see cref="MessagePickupHandler"/> (so it is applied), and the answer to a request of the
/// <see cref="HeldAnswer"/>'s type is held.
/// </summary>
internal sealed class HeldAnswerPickupHandler(MessagePickupHandler inner, HeldAnswer held) : IProtocolHandler
{
    public string ProtocolUri => inner.ProtocolUri;

    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        var reply = await inner.HandleAsync(message, context, ct);
        if (string.Equals(message.Type, held.Type, StringComparison.Ordinal))
            await held.HoldAsync(ct);
        return reply;
    }
}
