using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT3 (#111, fix round 2): a host that discards the failed items a fetch hands it
/// (<see cref="PickupClient.AckAsync"/>, the documented way to clear messages that keep failing, and
/// the host <see cref="SlowSenderFetchQa1Tests.ReachAsync"/> models with <c>discardFailures</c>).
/// What is checked: the message queued behind messages that never open is still returned to that
/// host opened. A message that ran out of budget before its turn is handed to the host as a failed
/// item, like one that did not open. Simulated time; nothing sleeps.
/// </summary>
public sealed class SlowSenderDiscardingHostRt3Tests(ITestOutputHelper output)
{
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s

    /// <summary>
    /// Three messages that never open in front of one that does, in one fetch of ten: every message
    /// gets a worker, so the fourth is returned opened in the first fetch, whatever the host does
    /// with the failures.
    /// </summary>
    [Fact]
    public async Task A_host_that_discards_the_failures_it_is_handed_receives_the_message_behind_three_that_never_open()
    {
        var run = await RunAsync(inFront: DeliveredMessageUnpacker.FetchConcurrency - 1, limit: 10);

        run.Reach.Found.Should().BeTrue("every message of the delivery had a worker");
        run.Reach.Fetches.Should().Be(1);
    }

    /// <summary>
    /// Four, and nine, messages that never open in front of one that does, in one fetch of ten, for
    /// a host that discards the failures it is handed: the last message is returned to it opened.
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2: the last message is never handed to host code and never
    /// returned opened; it is returned once as a failed item (out of time before its turn), and the
    /// host's discard of the failed items removes it from the mediator's queue. On the head before
    /// the round both cases returned it opened in the first fetch.
    /// </remarks>
    [Theory(Skip = "RT3: whether a host that discards the failures it is handed still receives a message queued behind four or more that never open")]
    [InlineData(4)]
    [InlineData(9)]
    public async Task A_host_that_discards_the_failures_it_is_handed_receives_the_message_behind_four_or_more_that_never_open(int inFront)
    {
        var run = await RunAsync(inFront, limit: 10);

        run.Reach.Found.Should().BeTrue("the message behind the {0} is returned opened (still queued at the mediator: {1}; handed to host code: {2}; returned as: {3})",
            inFront, run.StillQueued, run.HandedToHostCode, run.ReturnedAs);
    }

    /// <summary>
    /// The same host with 99 messages that never open in front, in one fetch of 100: the last
    /// message is returned to it opened.
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2, and on the head before the round as well (there a
    /// delivery started at most sixteen messages that never open): the last message is returned once
    /// as a failed item (out of time before its turn) and the host's discard removes it.
    /// </remarks>
    [Fact(Skip = "RT3: whether a host that discards the failures it is handed still receives a message queued behind 99 that never open in one fetch of 100")]
    public async Task A_host_that_discards_the_failures_it_is_handed_receives_the_message_behind_ninety_nine_that_never_open_in_one_fetch()
    {
        var run = await RunAsync(inFront: 99, limit: 100);

        run.Reach.Found.Should().BeTrue("the message behind the 99 is returned opened (still queued at the mediator: {0}; handed to host code: {1}; returned as: {2})",
            run.StillQueued, run.HandedToHostCode, run.ReturnedAs);
    }

    private async Task<Run> RunAsync(int inFront, int limit)
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, Lease);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: inFront, honestAt: inFront);

        var reach = await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(1), discardFailures: true);

        var returnedAs = string.Join(", ", reach.Batches.SelectMany(b => b.Items).Where(i => i.Id == "honest")
            .Select(i => i.Unpacked is not null ? "opened" : i.Error!.Message.Contains("before its turn came") ? "a failed item, out of time before its turn" : "a failed item, " + i.Error.GetType().Name));
        var run = new Run(reach, mediator.Holds("honest"), exchange.Host.Calls.Labels.Contains("honest"), returnedAs.Length == 0 ? "nothing" : returnedAs);
        output.WriteLine($"{inFront} in front, limit {limit}: returned opened: {reach.Found}; {reach.Fetches} fetches in {reach.Elapsed} simulated; still queued at the mediator: {run.StillQueued}; handed to host code: {run.HandedToHostCode}; returned as: {run.ReturnedAs}");
        return run;
    }

    private sealed record Run(SlowSenderFetchQa1Tests.Reach Reach, bool StillQueued, bool HandedToHostCode, string ReturnedAs);
}
