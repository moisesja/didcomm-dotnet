using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-2 for #99: the recipient client against this library's real Coordinate Mediation 2.0 mediator
/// role (FR-MED-09, FR-MED-10). A host that serves only the real <see cref="CoordinateMediation20Handler"/>
/// (with Discover Features) gets a negotiating client to fall back to 2.0: it enrols with one
/// routing DID (the first of two), registers, gets every update outcome back in order, and pages
/// and walks a list larger than the host's page size, including offsets at and past the end, after a
/// single Discover Features query. Against <c>AddMediator()</c>, a client pinned to 2.0 and one pinned
/// to 3.0, both for one peer DID, send no query and see each other's additions and removals. Over a
/// WebSocket, a client pinned to 2.0 enrols, registers and lists, and the mediator's 2.0 answers reach
/// it stamped with <c>created_time</c> and admitted by its replay check (FR-RR-08, FR-RR-09).
/// </summary>
public sealed class MediatorClientCoordinateMediation20Qa2Tests
{
    // ---- fallback to 2.0 against the real 2.0 handler alone ------------------------------------------

    [Fact]
    public async Task A_negotiating_client_falls_back_to_2_0_with_a_host_serving_only_the_real_2_0_handler_and_asks_only_once()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var host = await CoordinateMediation20OnlyHost.StartAsync(mediator);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        var grant = await harness.Mediation.RequestMediationAsync(host.Did, bob.Did);

        grant.Should().NotBeNull();
        grant!.PeerDid.Should().Be(bob.Did);
        grant.RoutingDids.Should().Equal(new[] { host.Did }, "a 2.0 grant carries one routing DID: the first configured");
        (await host.Store.GetGrantAsync(bob.Did, default))!.RoutingDids
            .Should().Equal(new[] { host.Did, CoordinateMediation20OnlyHost.SecondRoutingDid }, "the grant itself holds both");
        harness.Channel.Versions.TryGet(host.Did, out var version).Should().BeTrue();
        version.Should().Be(CoordinateMediationVersion.V2);

        // Register, list one page, walk: all in 2.0, on the version remembered from the one query.
        var published = await MediatorClientEnrolmentTests.PublishAsync(harness, grant);
        (await harness.Mediation.UpdateRecipientsAsync(host.Did, bob.Did, new[] { new RecipientUpdate(published.Did, RecipientUpdateAction.Add) }))
            .Should().Equal(new RecipientUpdateResult(published.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));
        var page = await harness.Mediation.QueryRecipientsAsync(host.Did, bob.Did);
        page.Dids.Should().Equal(published.Did);
        page.Pagination.Should().Be(new RecipientPagination(1, 0, 0));
        (await WalkAsync(harness.Mediation, host.Did, bob.Did)).Should().Equal(published.Did);

        host.Seen.From(bob.Did).Select(r => r.Type).Should().Equal(
            new[]
            {
                DiscoverFeatures.QueriesType,
                CoordinateMediation20.MediateRequestType,
                CoordinateMediation20.KeylistUpdateType,
                CoordinateMediation20.KeylistQueryType,
                CoordinateMediation20.KeylistQueryType,
            },
            "one Discover Features query, then 2.0 for every operation");
        mediator.LeakingLogLines(published.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task Pages_and_the_walker_agree_with_the_real_2_0_handler_over_several_pages_and_at_and_past_the_end()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var host = await CoordinateMediation20OnlyHost.StartAsync(mediator, maxPageSize: 7);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var mediation = harness.Mediation;
        (await mediation.RequestMediationAsync(host.Did, bob.Did)).Should().NotBeNull();
        var dids = Qa1.FakeDids(20, "w");
        (await mediation.UpdateRecipientsAsync(host.Did, bob.Did, dids.Select(d => new RecipientUpdate(d, RecipientUpdateAction.Add)).ToArray()))
            .Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);

        // The walker asks for 100 and gets 7 at a time: it advances by what it received.
        var before = host.Seen.Count(bob.Did, CoordinateMediation20.KeylistQueryType);
        (await WalkAsync(mediation, host.Did, bob.Did)).Should().Equal(dids);
        (host.Seen.Count(bob.Did, CoordinateMediation20.KeylistQueryType) - before).Should().Be(3, "7 + 7 + 6");

        // Single pages: what the handler reports is what the client checks (count = page size,
        // offset = the one asked for, remaining not negative).
        await PageShouldBe(null, dids.Take(7), new RecipientPagination(7, 0, 13));
        await PageShouldBe(new RecipientQueryPaging(1000, 0), dids.Take(7), new RecipientPagination(7, 0, 13));
        await PageShouldBe(new RecipientQueryPaging(5, 18), dids.Skip(18), new RecipientPagination(2, 18, 0));
        await PageShouldBe(new RecipientQueryPaging(0, 0), Array.Empty<string>(), new RecipientPagination(0, 0, 20));
        await PageShouldBe(new RecipientQueryPaging(10, 20), Array.Empty<string>(), new RecipientPagination(0, 20, 0));
        await PageShouldBe(new RecipientQueryPaging(10, 1000), Array.Empty<string>(), new RecipientPagination(0, 1000, 0));
        await PageShouldBe(new RecipientQueryPaging(10, int.MaxValue), Array.Empty<string>(), new RecipientPagination(0, int.MaxValue, 0));
        host.Seen.From(bob.Did).Count(r => r.Type == DiscoverFeatures.QueriesType).Should().Be(1);
        mediator.LeakingLogLines().Should().BeEmpty();

        async Task PageShouldBe(RecipientQueryPaging? asked, IEnumerable<string> expected, RecipientPagination position)
        {
            var page = await mediation.QueryRecipientsAsync(host.Did, bob.Did, asked);
            page.Dids.Should().Equal(expected);
            page.Pagination.Should().Be(position);
        }
    }

    [Fact]
    public async Task Every_2_0_update_outcome_comes_back_in_order_as_a_result_and_a_DID_URL_is_refused_before_sending()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var host = await CoordinateMediation20OnlyHost.StartAsync(mediator);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var carol = await harness.NewPeerAsync();
        var dave = await harness.NewPeerAsync(); // never enrols
        var mediation = harness.Mediation;
        (await mediation.RequestMediationAsync(host.Did, bob.Did)).Should().NotBeNull();
        (await mediation.RequestMediationAsync(host.Did, carol.Did)).Should().NotBeNull();
        var d = Qa1.FakeDids(4, "o");
        (await mediation.UpdateRecipientsAsync(host.Did, carol.Did, new[] { new RecipientUpdate(d[3], RecipientUpdateAction.Add) }))
            .Single().Result.Should().Be(RecipientUpdateOutcome.Success);
        (await mediation.UpdateRecipientsAsync(host.Did, bob.Did, new[] { new RecipientUpdate(d[0], RecipientUpdateAction.Add), new RecipientUpdate(d[1], RecipientUpdateAction.Add) }))
            .Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);

        var mixed = new[]
        {
            new RecipientUpdate(d[0], RecipientUpdateAction.Add),                                         // no_change: already registered
            new RecipientUpdate(d[2], RecipientUpdateAction.Remove),                                      // client_error: not registered
            new RecipientUpdate(d[3], RecipientUpdateAction.Add),                                         // client_error: under Carol's grant
            new RecipientUpdate(host.Did, RecipientUpdateAction.Add),                                     // client_error: the mediator's first routing DID
            new RecipientUpdate(CoordinateMediation20OnlyHost.SecondRoutingDid, RecipientUpdateAction.Add), // client_error: its second
            new RecipientUpdate(d[1], RecipientUpdateAction.Remove),                                      // success
            new RecipientUpdate(d[2], RecipientUpdateAction.Add),                                         // success
        };
        var results = await mediation.UpdateRecipientsAsync(host.Did, bob.Did, mixed);

        results.Select(r => (r.RecipientDid, r.Action)).Should().Equal(mixed.Select(u => (u.RecipientDid, u.Action)));
        results.Select(r => r.Result).Should().Equal(
            RecipientUpdateOutcome.NoChange,
            RecipientUpdateOutcome.ClientError,
            RecipientUpdateOutcome.ClientError,
            RecipientUpdateOutcome.ClientError,
            RecipientUpdateOutcome.ClientError,
            RecipientUpdateOutcome.Success,
            RecipientUpdateOutcome.Success);
        (await WalkAsync(mediation, host.Did, bob.Did)).Should().Equal(d[0], d[2]);
        (await WalkAsync(mediation, host.Did, carol.Did)).Should().Equal(d[3]);

        // Without a grant: client_error for every entry and an empty page, as results, not exceptions.
        (await mediation.UpdateRecipientsAsync(host.Did, dave.Did, new[] { new RecipientUpdate(d[2], RecipientUpdateAction.Add), new RecipientUpdate(d[0], RecipientUpdateAction.Remove) }))
            .Select(r => r.Result).Should().Equal(RecipientUpdateOutcome.ClientError, RecipientUpdateOutcome.ClientError);
        var empty = await mediation.QueryRecipientsAsync(host.Did, dave.Did);
        empty.Dids.Should().BeEmpty();
        empty.Pagination.Should().Be(new RecipientPagination(0, 0, 0));

        // A DID URL never reaches the 2.0 handler: the client refuses it before anything is sent.
        var updatesBefore = host.Seen.Count(bob.Did, CoordinateMediation20.KeylistUpdateType);
        var didUrl = new[] { new RecipientUpdate(d[0] + "#key-1", RecipientUpdateAction.Add) };
        (await FluentActions.Awaiting(() => mediation.UpdateRecipientsAsync(host.Did, bob.Did, didUrl)).Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().NotContain(d[0]);
        host.Seen.Count(bob.Did, CoordinateMediation20.KeylistUpdateType).Should().Be(updatesBefore);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- one peer DID through both versions, both pinned ---------------------------------------------

    [Fact]
    public async Task Clients_pinned_to_2_0_and_to_3_0_for_one_peer_DID_ask_nothing_and_see_each_others_additions_and_removals()
    {
        var seen = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = seen.Register);
        await using var v2 = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V2);
        await using var v3 = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V3);
        var bob = await v2.NewPeerAsync();
        v3.Hold(bob);
        var d = Qa1.FakeDids(3, "x");

        (await v2.Mediation.RequestMediationAsync(mediator.Did, bob.Did))!.RoutingDids.Should().Equal(mediator.Did);
        (await v3.Mediation.RequestMediationAsync(mediator.Did, bob.Did))!.RoutingDids.Should().Equal(mediator.Did);

        // Added through 2.0, seen through 3.0; removed through 3.0, gone through 2.0.
        await UpdateAsync(v2, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success, d[0], d[1]);
        (await WalkAsync(v3.Mediation, mediator.Did, bob.Did)).Should().Equal(d[0], d[1]);
        await UpdateAsync(v3, RecipientUpdateAction.Add, RecipientUpdateOutcome.NoChange, d[1]);
        await UpdateAsync(v3, RecipientUpdateAction.Remove, RecipientUpdateOutcome.Success, d[0]);
        (await WalkAsync(v2.Mediation, mediator.Did, bob.Did)).Should().Equal(d[1]);
        await UpdateAsync(v2, RecipientUpdateAction.Remove, RecipientUpdateOutcome.ClientError, d[0]);

        // Added through 3.0, seen through 2.0; removed through 2.0, gone through 3.0.
        await UpdateAsync(v3, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success, d[2]);
        (await WalkAsync(v2.Mediation, mediator.Did, bob.Did)).Should().Equal(d[1], d[2]);
        await UpdateAsync(v2, RecipientUpdateAction.Remove, RecipientUpdateOutcome.Success, d[2]);
        (await WalkAsync(v3.Mediation, mediator.Did, bob.Did)).Should().Equal(d[1]);
        (await mediator.Mediation.FindGrantByRecipientDidAsync(d[1], default))!.PeerDid.Should().Be(bob.Did);
        (await mediator.Mediation.FindGrantByRecipientDidAsync(d[2], default)).Should().BeNull();

        // Neither pinned client asked which version to speak, and each spoke its own.
        var fromBob = seen.From(bob.Did);
        fromBob.Should().NotContain(r => r.Type == DiscoverFeatures.QueriesType);
        fromBob.Select(r => r.Type).Should().OnlyContain(t =>
            t.StartsWith(CoordinateMediation20.ProtocolUri + "/", StringComparison.Ordinal) || t.StartsWith(CoordinateMediation.ProtocolUri + "/", StringComparison.Ordinal));
        fromBob.Count(r => r.Type.StartsWith(CoordinateMediation20.ProtocolUri + "/", StringComparison.Ordinal)).Should().Be(1 + 3 + 2, "2.0: the request, three updates, two listings");
        v2.Channel.Versions.TryGet(mediator.Did, out _).Should().BeFalse("a pinned version is not negotiated");
        v3.Channel.Versions.TryGet(mediator.Did, out _).Should().BeFalse();
        mediator.LeakingLogLines().Should().BeEmpty();

        async Task UpdateAsync(MediatorClientHarness through, RecipientUpdateAction action, RecipientUpdateOutcome expected, params string[] dids)
        {
            var results = await through.Mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, dids.Select(x => new RecipientUpdate(x, action)).ToArray());
            results.Should().Equal(dids.Select(x => new RecipientUpdateResult(x, action, expected)));
        }
    }

    // ---- over a WebSocket --------------------------------------------------------------------------

    [Theory]
    [InlineData("ws-did")]
    [InlineData("ws-override")]
    public async Task Over_a_WebSocket_a_client_pinned_to_2_0_enrols_registers_and_lists_and_the_2_0_answers_arrive_stamped_and_admitted(string route)
    {
        var atMediator = new Qa1Recorder();
        var atClient = new Qa1Recorder();
        var identity = route == "ws-did" ? await MediatorClientHarness.MintMediatorIdentityAsync("ws") : null;
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            if (identity is not null)
                o.Identity = identity;
            o.PostConfigureServices = atMediator.Register;
        });
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.ConfigureServices = atClient.Register;
            o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V2;
        });
        var endpoint = route == "ws-override" ? harness.WebSocketEndpoint : null;
        var bob = await harness.NewPeerAsync();
        var registered = Qa1.FakeDids(1, "s")[0];

        var grant = await harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did, endpoint).WaitAsync(Qa1.Wait);
        var updated = await harness.Mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, new[] { new RecipientUpdate(registered, RecipientUpdateAction.Add) }, endpoint).WaitAsync(Qa1.Wait);
        var page = await harness.Mediation.QueryRecipientsAsync(mediator.Did, bob.Did, null, endpoint).WaitAsync(Qa1.Wait);

        grant!.RoutingDids.Should().Equal(mediator.Did);
        updated.Should().Equal(new RecipientUpdateResult(registered, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));
        page.Dids.Should().Equal(registered);
        page.Pagination.Should().Be(new RecipientPagination(1, 0, 0));
        harness.Connects.Should().BeGreaterThan(0, "the requests went over the WebSocket");

        // What the mediator received: the three 2.0 requests and no query.
        var requests = atMediator.From(bob.Did);
        requests.Select(r => r.Type).Should().Equal(
            CoordinateMediation20.MediateRequestType, CoordinateMediation20.KeylistUpdateType, CoordinateMediation20.KeylistQueryType);

        // What reached the client's dispatcher, so passed its replay check: the three 2.0 answers,
        // each on its request's thread and stamped with the mediator's clock (FR-RR-09).
        var answers = atClient.From(mediator.Did);
        answers.Select(a => a.Type).Should().Equal(
            CoordinateMediation20.MediateGrantType, CoordinateMediation20.KeylistUpdateResponseType, CoordinateMediation20.KeylistType);
        answers.Select(a => a.Thid).Should().Equal(requests.Select(r => r.Id));
        answers.Should().AllSatisfy(a => a.CreatedTime.Should().NotBeNull("2.0 answers are mediator-service messages, in the client's replay scope"));
        harness.Logs.Entries.Should().NotContain(e => e.Contains("dropped: replay admission", StringComparison.Ordinal));
        harness.Channel.PendingCount.Should().Be(0);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static async Task<List<string>> WalkAsync(MediationClient mediation, string mediatorDid, string peerDid)
    {
        var walked = new List<string>();
        await foreach (var did in mediation.QueryAllRecipientsAsync(mediatorDid, peerDid))
            walked.Add(did);
        return walked;
    }
}

/// <summary>
/// QA-2 (#99): a mediator that serves only Coordinate Mediation 2.0 through this library's real
/// <see cref="CoordinateMediation20Handler"/>, composed by hand as a host does (the FF.2 (b) way, but
/// in a container): <c>AddDidComm</c> with did:peer resolution and its own keys,
/// <c>AddBuiltInProtocols()</c> (Discover Features answers from the handler registry, so a
/// <c>coordinate-mediation/*</c> query discloses 2.0 in the mediator role and nothing else), the
/// handler over an <see cref="InMemoryMediationStore"/>, <see cref="DefaultMediationPolicy"/> and a
/// <see cref="MediatorIdentity"/> of two routing DIDs (its own first), and the registry-aware
/// <c>MapDidCommEndpoint("/didcomm")</c>, which runs replay admission before dispatch. It joins the
/// <see cref="MediatorTestServer"/>'s network, clock and log capture at <c>http://m1.test/didcomm</c>.
/// </summary>
internal sealed class CoordinateMediation20OnlyHost : IAsyncDisposable
{
    public const string HostName = "m1.test";
    public const string Path = "/didcomm";

    /// <summary>The second routing DID the identity lists (nobody resolves it; a 2.0 grant never names it).</summary>
    public const string SecondRoutingDid = "did:example:qa2secondrouting";

    private CoordinateMediation20OnlyHost(WebApplication app, string did, InMemoryMediationStore store, Qa1Recorder seen)
    {
        App = app;
        Did = did;
        Store = store;
        Seen = seen;
    }

    public WebApplication App { get; }

    /// <summary>The host's DID (its service is <c>http://m1.test/didcomm</c>): its first routing DID.</summary>
    public string Did { get; }

    /// <summary>The grants and registrations the 2.0 handler keeps.</summary>
    public InMemoryMediationStore Store { get; }

    /// <summary>Every verified inbound the host's dispatcher saw.</summary>
    public Qa1Recorder Seen { get; }

    /// <param name="network">The test network the host joins.</param>
    /// <param name="maxPageSize">The 2.0 handler's page size.</param>
    /// <param name="discovery">
    /// A Discover Features responder of a third-party mediator to answer the client's question
    /// instead of this library's (#99, owner decision O-1); <c>null</c> for the built-in protocols.
    /// </param>
    public static async Task<CoordinateMediation20OnlyHost> StartAsync(MediatorTestServer network, int maxPageSize = 100, IProtocolHandler? discovery = null)
    {
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec($"http://{HostName}{Path}", Accept: new[] { "didcomm/v2" }));
        var secrets = new InMemorySecretsResolver();
        foreach (var jwk in identity.Privates)
            secrets.Add(jwk);
        var options = new CoordinateMediationOptions { MaxPageSize = maxPageSize };
        options.Validate();
        var store = new InMemoryMediationStore(options);
        var routing = new MediatorIdentity(identity.Did, SecondRoutingDid);
        var seen = new Qa1Recorder();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{HostName}/"));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(network.Logs);
        seen.Register(builder.Services);
        builder.Services.AddSingleton(sp => new CoordinateMediation20Handler(
            store, new DefaultMediationPolicy(), routing, Options.Create(options), sp.GetService<ILogger<CoordinateMediation20Handler>>()));
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            if (discovery is null)
                b.AddBuiltInProtocols();
            else
                b.Services.AddSingleton(discovery);
            b.AddProtocol<CoordinateMediation20Handler>();
            b.Configure(o => o.Clock = () => network.Clock.Now);
        });

        var app = builder.Build();
        app.UseRouting();
        app.MapDidCommEndpoint(Path);
        await app.StartAsync();
        network.Network.Add(app.GetTestServer());
        network.AddSensitive(identity.Did);
        network.AddSensitive(SecondRoutingDid);
        foreach (var kid in DidComm.InteropTests.AspNetCore.ReturnRouteTestHost.KidsOf(identity))
            network.AddSensitive(kid);
        return new CoordinateMediation20OnlyHost(app, identity.Did, store, seen);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
