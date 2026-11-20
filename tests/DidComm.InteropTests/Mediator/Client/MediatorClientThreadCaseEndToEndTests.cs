using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.TestSupport;
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

    /// <summary>
    /// The harness's client, over its real channel, with each request's id taken from
    /// <see cref="LetterIds"/>: a random UUID v4 may hold no letter, and then upper-casing it would
    /// change nothing and the test would not test letter case.
    /// </summary>
    private static MediationClient WithLetterIds(MediatorClientHarness harness)
        => new(new LetterIdExchange(harness.Channel, new LetterIds("request")), logger: null);

    [Fact]
    public async Task Over_HTTP_answers_whose_thid_is_the_request_id_upper_cased_complete_enrolment_update_and_listing()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator, threadOf: id => id.ToUpperInvariant());
        await using var harness = await PinnedTo20Async(mediator);
        var bob = await harness.NewPeerAsync();
        var published = await harness.NewPeerAsync();
        var mediation = WithLetterIds(harness);

        var grant = await mediation.RequestMediationAsync(fake.Did, bob.Did);
        var updated = await mediation.UpdateRecipientsAsync(fake.Did, bob.Did, new[] { new RecipientUpdate(published.Did, RecipientUpdateAction.Add) });
        var listed = await mediation.QueryRecipientsAsync(fake.Did, bob.Did);

        fake.Handler!.Handled.Should().HaveCount(3);
        fake.Handler.Handled.Select(m => m.Id).Should().Equal(new[] { LetterIds.Id("request", 1), LetterIds.Id("request", 2), LetterIds.Id("request", 3) }, "precondition: the request ids are the pinned ones");
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
        var mediation = WithLetterIds(harness);

        var thrown = await Record.ExceptionAsync(() => mediation.RequestMediationAsync(fake.Did, bob.Did));

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
        var mediation = WithLetterIds(harness);

        var thrown = await Record.ExceptionAsync(() => mediation.RequestMediationAsync(fake.Did, bob.Did));

        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("no answer in the HTTP response");
        fake.Handler!.Handled.Should().ContainSingle("the mediator acted on the request; only its answer named another thread");
    }
}

/// <summary>
/// A pass-through <see cref="IMediatorExchange"/> that gives each request an id from
/// <paramref name="ids"/> before the real channel registers and sends it. Test-only: the product
/// builds request ids with a UUID v4.
/// </summary>
internal sealed class LetterIdExchange(IMediatorExchange inner, IMessageIdGenerator ids) : IMediatorExchange
{
    public MediatorClientOptions Options => inner.Options;

    public TimeProvider Time => inner.Time;

    public DuplicateFilter Duplicates => inner.Duplicates;

    public NegotiatedVersionCache<CoordinateMediationVersion> Versions => inner.Versions;
    public NegotiatedVersionCache<MessagePickupVersion> PickupVersions => inner.PickupVersions;

    public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
        => inner.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket, ct);

    public Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
    {
        request.Id = ids.NewId();
        return inner.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);
    }

    public Task<InboundMessageSnapshot?> ExchangeAllowingNoAnswerAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
    {
        request.Id = ids.NewId();
        return inner.ExchangeAllowingNoAnswerAsync(route, request, spec, mediatorDid, localDid, ct);
    }

    public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid) => inner.RegisterLiveStream(mediatorDid, localDid);


    public Task SendOneWayAsync(MediatorRoute route, Message request, string name, string mediatorDid, string localDid, CancellationToken ct)

    {

        request.Id = ids.NewId();

        return inner.SendOneWayAsync(route, request, name, mediatorDid, localDid, ct);

    }

    public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, string? requestId, CancellationToken ct)
        => inner.UnpackDeliveredBatchAsync(payloads, requestId, ct);
}
