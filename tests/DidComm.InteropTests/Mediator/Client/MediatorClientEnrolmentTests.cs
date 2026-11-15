using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #96 acceptance for <see cref="MediationClient"/> against the #95 mediator (FR-MED-09, FR-RR-09),
/// end to end through <see cref="MediatorClientHarness"/>: Discover Features → <c>mediate-request</c>
/// → grant → a DID minted with <c>DidCommServiceBuilder.ForMediation(grant)</c> →
/// <c>UpdateRecipientsAsync(add)</c> → <c>QueryRecipientsAsync</c> and the page walker → Alice's
/// <c>SendAsync</c> to that DID wraps a forward that the mediator queues. Over HTTP (the answer in
/// the response) and over a WebSocket (the answer through the correlator), for a mediator whose DID
/// names a <c>ws://</c> service and for an <c>http</c> one reached with a <c>ws</c> override. A policy
/// deny is <c>null</c>; a mediator that cannot answer (a faulting store) is an immediate
/// <see cref="TimeoutException"/> naming our request id; every request carries the injected clock's
/// <c>created_time</c>.
/// </summary>
public sealed class MediatorClientEnrolmentTests
{
    [Fact]
    public async Task Over_HTTP_the_client_enrols_registers_the_DID_it_publishes_and_Alices_forward_to_it_is_queued()
    {
        var requests = new RequestRecorder();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = requests.Register);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        var (grant, published) = await EnrolAsync(harness, mediator.Did, bob);

        grant.RoutingDids.Should().Equal(mediator.Did);
        var alice = await harness.NewAliceAsync();
        var note = alice.Note(published.Did);
        var sent = await alice.Client.SendAsync(note, new SendOptions(new[] { published.Did }, alice.Did));
        sent.EndpointUsed.Should().Be(mediator.Endpoint, "the published DID names the routing DID, whose service is the mediator (FR-ROUTE-04)");
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.Store.MessageCount.Should().Be(1);

        // FR-RR-09: every request the client sent carries created_time from the injected clock.
        requests.From(bob.Did).Select(r => r.Type).Should().Equal(
            DiscoverFeatures.QueriesType,
            CoordinateMediation.MediateRequestType,
            CoordinateMediation.RecipientUpdateType,
            CoordinateMediation.RecipientQueryType,
            CoordinateMediation.RecipientQueryType);
        requests.From(bob.Did).Should().AllSatisfy(r => r.CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds()));
        mediator.LeakingLogLines(published.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task Over_a_WebSocket_to_a_mediator_whose_DID_names_a_ws_service_the_answers_come_through_the_correlator()
    {
        var requests = new RequestRecorder();
        var identity = await MediatorClientHarness.MintMediatorIdentityAsync("ws");
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Identity = identity;
            o.PostConfigureServices = requests.Register;
        });
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var bob = await harness.NewPeerAsync();

        var (grant, published) = await EnrolAsync(harness, mediator.Did, bob);

        harness.Connects.Should().BeGreaterThan(0, "the route is the DID document's ws:// entry");
        grant.RoutingDids.Should().Equal(mediator.Did);
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().NotBeNull();
        (await mediator.Mediation.FindGrantByRecipientDidAsync(published.Did, default))!.PeerDid.Should().Be(bob.Did);
        requests.From(bob.Did).Should().HaveCount(5).And.AllSatisfy(r => r.CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds()));
        mediator.LeakingLogLines(published.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task Over_a_WebSocket_override_to_a_mediator_whose_DID_names_http_the_enrolment_works_and_Alice_reaches_the_DID()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var bob = await harness.NewPeerAsync();

        var (_, published) = await EnrolAsync(harness, mediator.Did, bob, harness.WebSocketEndpoint);

        harness.Connects.Should().BeGreaterThan(0, "every call went over the override");
        var alice = await harness.NewAliceAsync();
        (await alice.Client.SendAsync(alice.Note(published.Did), new SendOptions(new[] { published.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();
        mediator.Store.MessageCount.Should().Be(1);
        mediator.LeakingLogLines(published.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_policy_deny_is_null_and_an_allowed_peer_is_granted()
    {
        var allowed = new AllowListPolicy();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IMediationPolicy>(allowed));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var carol = await harness.NewPeerAsync();
        allowed.Allow(carol.Did);

        (await harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did)).Should().BeNull("mediate-deny is a result, not an error");
        (await harness.Mediation.RequestMediationAsync(mediator.Did, carol.Did))!.PeerDid.Should().Be(carol.Did);
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_mediator_that_cannot_answer_is_an_immediate_TimeoutException_naming_the_request_and_no_DID()
    {
        var requests = new RequestRecorder();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.ConfigureServices = s => s.AddSingleton<IMediationStore>(new FaultingGrantStore());
            o.PostConfigureServices = requests.Register;
        });
        // A clock the test never moves: the request deadline cannot fire, so the only way out is
        // the HTTP response that carried no answer.
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.Time = new QaManualTimeProvider();
            o.Client = c => c.RequestTimeout = TimeSpan.FromMinutes(10);
        });
        var bob = await harness.NewPeerAsync();

        var thrown = (await FluentActions.Awaiting(() => harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did))
            .Should().ThrowAsync<TimeoutException>()).Which;

        var request = requests.From(bob.Did).Single(r => r.Type == CoordinateMediation.MediateRequestType);
        thrown.Message.Should().Contain(request.Id).And.NotContain(bob.Did).And.NotContain(mediator.Did);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    /// <summary>The whole enrolment through the client; returns the grant and the DID minted from it, registered and listed.</summary>
    internal static Task<(MediationGrant Grant, TestPeer Published)> EnrolAsync(MediatorClientHarness harness, string mediatorDid, TestPeer enrolling, Uri? endpoint = null)
        => EnrolAsync(harness.Mediation, harness, mediatorDid, enrolling, endpoint);

    /// <summary>The same through <paramref name="mediation"/>.</summary>
    internal static async Task<(MediationGrant Grant, TestPeer Published)> EnrolAsync(MediationClient mediation, MediatorClientHarness harness, string mediatorDid, TestPeer enrolling, Uri? endpoint = null)
    {
        var grant = await mediation.RequestMediationAsync(mediatorDid, enrolling.Did, endpoint);
        grant.Should().NotBeNull();
        grant!.PeerDid.Should().Be(enrolling.Did);

        var published = await PublishAsync(harness, grant);
        var updated = await mediation.UpdateRecipientsAsync(mediatorDid, enrolling.Did,
            new[] { new RecipientUpdate(published.Did, RecipientUpdateAction.Add) }, endpoint);
        updated.Should().Equal(new RecipientUpdateResult(published.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));

        var page = await mediation.QueryRecipientsAsync(mediatorDid, enrolling.Did, null, endpoint);
        page.Dids.Should().Equal(published.Did);
        page.Pagination.Should().Be(new RecipientPagination(1, 0, 0));

        var all = new List<string>();
        await foreach (var did in mediation.QueryAllRecipientsAsync(mediatorDid, enrolling.Did, endpoint))
            all.Add(did);
        all.Should().Equal(published.Did);
        return (grant, published);
    }

    /// <summary>A DID whose service entry is <c>DidCommServiceBuilder.ForMediation(grant)</c>, held by the harness.</summary>
    internal static async Task<TestPeer> PublishAsync(MediatorClientHarness harness, MediationGrant grant)
    {
        var entry = DidCommServiceBuilder.ForMediation(grant).Build();
        var peer = harness.Mediator.Own(TestPeer.Create(await MediatorCast.MintWithServiceEntryAsync(entry)));
        harness.Hold(peer);
        return peer;
    }
}

/// <summary>One request the mediator received: its type, sender, id and <c>created_time</c>.</summary>
internal sealed record RecordedRequest(string Type, string? From, string Id, long? CreatedTime);

/// <summary>
/// Records every verified inbound the mediator's dispatcher sees, inline (an internal
/// <see cref="IInboundCorrelator"/> that claims nothing), so a test reads what the client actually
/// sent without waiting on an observer queue.
/// </summary>
internal sealed class RequestRecorder : IInboundCorrelator
{
    private readonly ConcurrentQueue<RecordedRequest> _seen = new();

    public void Register(IServiceCollection services)
        => services.TryAddEnumerable(ServiceDescriptor.Singleton<IInboundCorrelator>(this));

    public IReadOnlyList<RecordedRequest> From(string did) => _seen.Where(r => r.From == did).ToArray();

    bool IInboundCorrelator.OnInbound(InboundMessageSnapshot received)
    {
        _seen.Enqueue(new RecordedRequest(received.Type, received.From, received.Id, received.DeserializeMessage().CreatedTime));
        return false;
    }
}

/// <summary>Grants only the peers allowed so far; denies the rest.</summary>
internal sealed class AllowListPolicy : IMediationPolicy
{
    private readonly ConcurrentDictionary<string, bool> _allowed = new(StringComparer.Ordinal);

    public void Allow(string did) => _allowed[did] = true;

    public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct)
        => new(_allowed.ContainsKey(peerDid) ? MediationDecision.Grant : MediationDecision.Deny);

    public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct)
        => new(true);
}

/// <summary>A mediation store whose grant reads and writes fail; everything else is the in-memory store.</summary>
internal sealed class FaultingGrantStore : IMediationStore
{
    private readonly InMemoryMediationStore _inner = new();

    public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct) => throw new InvalidOperationException("store down");

    public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct) => throw new InvalidOperationException("store down");

    public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct) => _inner.RevokeAsync(peerDid, ct);

    public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct)
        => _inner.UpdateRecipientsAsync(peerDid, updates, ct);

    public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct) => _inner.ListRecipientsAsync(peerDid, offset, limit, ct);

    public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct) => _inner.FindGrantByRecipientDidAsync(recipientDid, ct);
}
