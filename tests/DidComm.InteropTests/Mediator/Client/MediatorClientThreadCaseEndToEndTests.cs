using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// The mediator client over real HTTP against a third-party Coordinate Mediation 2.0 mediator that
/// writes the thread in its own letter case (<see cref="FakeCoordinateMediation20Mediator"/> with a
/// <c>threadOf</c>): DIDComm v2.1 threading.md L9, L15, L21 compare ids, thids and pthids ignoring
/// case, so an answer in the HTTP response whose <c>thid</c> is the request id upper-cased completes
/// the request — enrolment, update and listing alike — and a problem report whose <c>pthid</c> is
/// the request id upper-cased fails it with its code. An answer on a thread that differs in more
/// than case is no answer. The client is pinned to 2.0, so no Discover Features exchange precedes
/// the requests.
/// </summary>
public sealed class MediatorClientThreadCaseEndToEndTests
{
    private static Task<MediatorClientHarness> PinnedTo20Async(MediatorTestServer mediator)
        => MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V2);

    [Fact]
    public async Task Over_HTTP_answers_whose_thid_is_the_request_id_upper_cased_complete_enrolment_update_and_listing()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator, threadOf: id => id.ToUpperInvariant());
        await using var harness = await PinnedTo20Async(mediator);
        var bob = await harness.NewPeerAsync();
        var published = await harness.NewPeerAsync();

        var grant = await harness.Mediation.RequestMediationAsync(fake.Did, bob.Did);
        var updated = await harness.Mediation.UpdateRecipientsAsync(fake.Did, bob.Did, new[] { new RecipientUpdate(published.Did, RecipientUpdateAction.Add) });
        var listed = await harness.Mediation.QueryRecipientsAsync(fake.Did, bob.Did);

        fake.Handler!.Handled.Should().HaveCount(3);
        fake.Handler.Handled.Should().OnlyContain(m => m.Id.ToUpperInvariant() != m.Id, "precondition: every request id has letters the mediator re-cased");
        grant!.RoutingDids.Should().Equal(fake.Did);
        updated.Should().Equal(new RecipientUpdateResult(published.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));
        listed.Dids.Should().Equal(published.Did);
        harness.Channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Over_HTTP_a_problem_report_whose_pthid_is_the_request_id_upper_cased_fails_the_request_with_its_code()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator, threadOf: id => id.ToUpperInvariant(), problemCode: "e.p.req.not-enrolled");
        await using var harness = await PinnedTo20Async(mediator);
        var bob = await harness.NewPeerAsync();

        var thrown = await Record.ExceptionAsync(() => harness.Mediation.RequestMediationAsync(fake.Did, bob.Did));

        thrown.Should().BeOfType<MediationProtocolException>().Which.ProblemCode.Should().Be("e.p.req.not-enrolled");
        ((MediationProtocolException)thrown!).RequestId.Should().Be(fake.Handler!.Handled.Single().Id);
        harness.Channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Over_HTTP_an_answer_on_a_thread_that_differs_from_the_request_id_in_more_than_case_is_no_answer()
    {
        // Control for the tests above: the upper-cased id with one more character is another thread.
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator, threadOf: id => id.ToUpperInvariant() + "0");
        await using var harness = await PinnedTo20Async(mediator);
        var bob = await harness.NewPeerAsync();

        var thrown = await Record.ExceptionAsync(() => harness.Mediation.RequestMediationAsync(fake.Did, bob.Did));

        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("no answer in the HTTP response");
        fake.Handler!.Handled.Should().ContainSingle("the mediator acted on the request; only its answer named another thread");
    }
}
