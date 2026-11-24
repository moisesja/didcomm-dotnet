using AwesomeAssertions;
using DidComm.Mediator;
using DidComm.Mediator.Pipeline;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA round 3 (#110, direct push): what ends a stalled push POST. The push deadline is not the only
/// clock on that POST: the transport's request timeout and the named <c>"didcomm"</c> client's own
/// <c>HttpClient.Timeout</c> (100 seconds unless the host sets it) can end it first. Whatever ends
/// it, a direct push that does not deliver pauses its recipient's direct pushes.
/// </summary>
public sealed class DirectPushTimeoutQaTests
{
    private const string Site = "m1.test";

    // QA-110-QA3-3. The client's own timeout ends the stalled POST before the push deadline. Before the
    // fix the step counted it not delivered (a TaskCanceledException that is not the deadline's), no
    // pause started, and the operation went on to the next record, which got a stalled POST of its
    // own: one per queued message, run after run (QA2-1's shape). Now any direct push that does not
    // deliver ends its run and pauses its recipient's direct pushes, whatever ended it. Here the
    // host's client gives up after 100 ms of real time (with the defaults, 100 s, which a PushTimeout
    // of up to 5 minutes can exceed); the push workers' manual clock, and so the push deadline, never moves.
    [Fact]
    public async Task QA3_3_A_tarpit_the_named_clients_own_timeout_gives_up_on_before_the_push_deadline_is_posted_once_and_its_recipients_direct_pushes_pause()
    {
        var network = new MediatorNetwork();
        await using var site = await ScriptedSite.StartAsync(network, Site);
        site.Respond = async context =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
            context.Abort();
        };
        await using var world = await DirectPushWorld.StartAsync(
            o => o.PostConfigureServices = s => s.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMilliseconds(100)),
            network: network);
        var (bob, b, _) = await world.EnrolAsync("bob");
        world.Endpoints.Set(bob.Did, site.At("/tarpit"));
        var alice = await world.Mediator.NewSenderAsync();
        var parked = world.Endpoints.ParkNext(bob.Did);
        for (var i = 0; i < 3; i++)
            await Qa.SendNoteAsync(world.Mediator, alice, b);
        await parked.Arrived.WaitAsync(MediatorTestServer.Timeout);
        parked.Release();

        await world.OutcomesAsync(PushOutcomes.Failed, 1);
        await MediatorTestServer.WaitUntilAsync(() => world.Pushes.Unsettled == 0, "the operation ended");
        await world.Mediator.PushesIdleAsync();

        // One attempt, counted where it is exact: the store handed one record to a direct push. The
        // client's 100 ms are real time, so on a loaded machine they may run out before the POST
        // reaches the site: at most one POST arrives.
        world.Store.DirectTakes.Should().ContainSingle("a tarpit holds a worker once per pause (residual 1): one attempt, then its recipient's direct pushes pause");
        site.Posts.Should().BeLessThanOrEqualTo(1);
        world.Mediator.PushOutcomes.Where(o => o != PushOutcomes.SkippedCooldown).Should().Equal(new[] { PushOutcomes.Failed }, "the client's timeout ended the stall, and the pause followed");
        var skipped = world.Mediator.PushOutcome(PushOutcomes.SkippedCooldown);
        await Qa.SendNoteAsync(world.Mediator, alice, b);
        await MediatorTestServer.WaitUntilAsync(() => world.Mediator.PushOutcome(PushOutcomes.SkippedCooldown) == skipped + 1, "Bob's direct pushes are paused");
        world.Store.DirectTakes.Should().ContainSingle("nothing is attempted during the pause");
        site.Posts.Should().BeLessThanOrEqualTo(1);
        (await world.Mediator.FetchAsync(bob)).Should().HaveCount(4, "nothing is lost");
    }
}
