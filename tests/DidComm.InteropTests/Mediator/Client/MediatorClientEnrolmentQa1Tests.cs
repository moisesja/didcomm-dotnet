using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Resolution;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-1 (#96): <see cref="MediationClient"/> end to end against the #95 mediator — the issue's
/// acceptance flow (negotiate → enrol → <c>ForMediation</c> DID document → register → Alice's
/// forward reaches the queue of that DID) over HTTP, over a WebSocket named by the mediator's DID and
/// over an http DID with a <c>ws</c> override; FR-MED-03 / FR-RR-09 on what reached the mediator
/// (every request <c>return_route: all</c>, <c>created_time</c> = the <em>client's</em> clock, which is
/// deliberately offset from the mediator's); the deny paths; timeouts; correlation across several
/// local DIDs and several mediators; the real mediator's pagination; recipient updates; CM version
/// negotiation and its cache across two mediators; the version pin; a host that is both.
/// </summary>
public sealed class MediatorClientEnrolmentQa1Tests
{
    // The client runs 20 s ahead of the mediator: within the 60 s skew both ways, and enough that a
    // created_time from the mediator's clock would be told apart from the client's.
    private static readonly TimeSpan ClientAhead = TimeSpan.FromSeconds(20);

    public static TheoryData<string> Transports => new() { "http", "ws-did", "ws-override" };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Acceptance_flow_negotiate_enrol_publish_register_and_Alices_forward_reaches_the_queue_of_that_DID(string transport)
    {
        var atMediator = new Qa1Recorder();
        var atClient = new Qa1Recorder();
        var identity = transport == "ws-did" ? await MediatorClientHarness.MintMediatorIdentityAsync("ws") : null;
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            if (identity is not null)
                o.Identity = identity;
            o.PostConfigureServices = atMediator.Register;
        });
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = transport != "http";
            o.ClockOffset = ClientAhead;
            o.ConfigureServices = atClient.Register;
        });
        var endpoint = transport == "ws-override" ? harness.WebSocketEndpoint : null;
        var bob = await harness.NewPeerAsync();

        var grant = await harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did, endpoint).WaitAsync(Qa1.Wait);
        grant.Should().NotBeNull();
        grant!.PeerDid.Should().Be(bob.Did);
        grant.RoutingDids.Should().Equal(mediator.Did);

        // The DID others write to, with the FR-MED-07 service entry built from the grant.
        var entry = DidCommServiceBuilder.ForMediation(grant).Build();
        entry.ServiceEndpoint!.Map!["uri"].GetString().Should().Be(mediator.Did);
        var published = mediator.Own(TestPeer.Create(await MediatorCast.MintWithServiceEntryAsync(entry)));
        harness.Hold(published);
        var updated = await harness.Mediation.UpdateRecipientsAsync(mediator.Did, bob.Did,
            new[] { new RecipientUpdate(published.Did, RecipientUpdateAction.Add) }, endpoint).WaitAsync(Qa1.Wait);
        updated.Should().Equal(new RecipientUpdateResult(published.Did, RecipientUpdateAction.Add, RecipientUpdateOutcome.Success));

        var alice = await harness.NewAliceAsync();
        if (transport == "ws-did")
        {
            // Alice's container speaks HTTP only and the mediator's DID names ws://, so she builds the
            // forward to the routing DID herself and posts it (the wrapping is FR-ROUTE-04's either way).
            var (packed, _) = await alice.ForwardAsync(mediator.Did, published.Did, await alice.AuthcryptAsync(alice.Note(published.Did), published.Did));
            (await mediator.PostAsync(packed)).Status.Should().Be(System.Net.HttpStatusCode.Accepted);
        }
        else
        {
            var sent = await alice.Client.SendAsync(alice.Note(published.Did), new SendOptions(new[] { published.Did }, alice.Did));
            sent.Transport.Accepted.Should().BeTrue();
            sent.EndpointUsed.Should().Be(mediator.Endpoint, "the published DID's uri is the routing DID, whose service is the mediator (FR-ROUTE-04)");
        }
        await mediator.FlushAsync();
        (await mediator.Store.GetStatusAsync(bob.Did, published.Did, mediator.Clock.Now, default)).MessageCount
            .Should().Be(1, "Alice's forward is queued for the DID the client registered");

        // FR-MED-03 / FR-RR-09 on what the mediator received: every request return_route: all and
        // stamped with the client's clock (not the mediator's).
        var clientNow = (mediator.Clock.Now + ClientAhead).ToUnixTimeSeconds();
        var fromBob = atMediator.From(bob.Did);
        fromBob.Select(r => r.Type).Should().Equal(
            DiscoverFeatures.QueriesType,
            CoordinateMediation.MediateRequestType,
            CoordinateMediation.RecipientUpdateType);
        fromBob.Should().AllSatisfy(r =>
        {
            r.CreatedTime.Should().Be(clientNow);
            r.ReturnRoute.Should().Be(ReturnRouteMode.All);
        });
        if (transport != "http")
            harness.Connects.Should().BeGreaterThan(0);

        // Logs: no DID or key id anywhere; no mediator message id in a client line.
        mediator.LeakingLogLines(published.Did, alice.Did).Should().BeEmpty();
        var answerIds = atClient.All.Select(a => a.Id).ToArray();
        if (transport != "http")
            answerIds.Should().NotBeEmpty("over a WebSocket the answers reach the client's dispatcher");
        Qa1.ClientLines(harness.Logs).Should().NotContain(l => answerIds.Any(id => l.Text.Contains(id, StringComparison.Ordinal)));
        harness.Channel.PendingCount.Should().Be(0);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("ws-override")]
    public async Task A_full_grant_store_denies_with_null_not_an_exception_and_a_held_grant_is_reissued(string transport)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.CoordinateMediation.MaxGrants = 1);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = transport != "http");
        var endpoint = transport == "ws-override" ? harness.WebSocketEndpoint : null;
        var carol = await harness.NewPeerAsync();
        var bob = await harness.NewPeerAsync();

        (await harness.Mediation.RequestMediationAsync(mediator.Did, carol.Did, endpoint).WaitAsync(Qa1.Wait)).Should().NotBeNull();
        (await harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did, endpoint).WaitAsync(Qa1.Wait))
            .Should().BeNull("the store holds no more grants: mediate-deny, which is a result");
        (await harness.Mediation.RequestMediationAsync(mediator.Did, carol.Did, endpoint).WaitAsync(Qa1.Wait))!
            .RoutingDids.Should().Equal(new[] { mediator.Did }, "a repeated request for a held grant is idempotent");
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();
        harness.Channel.PendingCount.Should().Be(0);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Over_a_WebSocket_a_mediator_that_never_answers_is_a_TimeoutException_naming_the_request_and_no_DID()
    {
        var atMediator = new Qa1Recorder();
        var time = new QaManualTimeProvider();
        var identity = await MediatorClientHarness.MintMediatorIdentityAsync("ws");
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Identity = identity;
            o.ConfigureServices = s => s.AddSingleton<IMediationStore>(new FaultingGrantStore());
            o.PostConfigureServices = atMediator.Register;
        });
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseHttp = false;
            o.UseWebSocket = true;
            o.Time = time;
            o.Client = c => c.RequestTimeout = TimeSpan.FromSeconds(30);
        });
        var bob = await harness.NewPeerAsync();

        var call = harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did);
        await MediatorTestServer.WaitUntilAsync(() => atMediator.Count(bob.Did, CoordinateMediation.MediateRequestType) == 1, "the mediate-request reached the mediator");
        call.IsCompleted.Should().BeFalse("over a WebSocket the client waits for the answer");
        time.Advance(TimeSpan.FromSeconds(30));

        var thrown = (await FluentActions.Awaiting(() => call.WaitAsync(Qa1.Wait)).Should().ThrowAsync<TimeoutException>()).Which;
        var request = atMediator.From(bob.Did).Single(r => r.Type == CoordinateMediation.MediateRequestType);
        thrown.Message.Should().Contain(request.Id).And.NotContain(bob.Did).And.NotContain(mediator.Did);
        harness.Channel.PendingCount.Should().Be(0);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Over_HTTP_an_unanswered_Discover_Features_query_is_an_immediate_TimeoutException_naming_the_query_and_nothing_is_guessed()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        // A host with no protocols at all: the query is accepted (202) and never answered.
        await using var bare = await Qa1BareHost.StartAsync(mediator, "m1.test");
        // A clock that never fires: only the HTTP no-answer rule can end the call.
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.Time = new QaManualTimeProvider();
            o.Client = c => c.RequestTimeout = TimeSpan.FromMinutes(10);
        });
        var bob = await harness.NewPeerAsync();

        var thrown = (await FluentActions.Awaiting(() => harness.Mediation.RequestMediationAsync(bare.Did, bob.Did).WaitAsync(Qa1.Wait))
            .Should().ThrowAsync<TimeoutException>()).Which;

        var query = bare.Recorder.From(bob.Did).Should().ContainSingle("only the query is sent: no version is guessed").Subject;
        query.Type.Should().Be(DiscoverFeatures.QueriesType);
        query.ReturnRoute.Should().Be(ReturnRouteMode.All, "the bootstrap query is sent with ReturnRoute: All (FR-MED-09)");
        query.CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds(), "the bootstrap query is timestamped (FR-RR-09)");
        thrown.Message.Should().Contain(query.Id).And.NotContain(bare.Did).And.NotContain(bob.Did);
        harness.Channel.Versions.TryGet(bare.Did, out _).Should().BeFalse();
        harness.Channel.PendingCount.Should().Be(0);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("ws-did")]
    public async Task Two_local_DIDs_enrolling_at_one_mediator_at_the_same_time_each_get_their_own_grant(string transport)
    {
        var identity = transport == "ws-did" ? await MediatorClientHarness.MintMediatorIdentityAsync("ws") : null;
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            if (identity is not null)
                o.Identity = identity;
        });
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = transport != "http");
        var peers = new[] { await harness.NewPeerAsync(), await harness.NewPeerAsync(), await harness.NewPeerAsync() };

        var grants = await Task.WhenAll(peers.Select(p => harness.Mediation.RequestMediationAsync(mediator.Did, p.Did))).WaitAsync(Qa1.Wait);

        grants.Select(g => g!.PeerDid).Should().Equal(peers.Select(p => p.Did));
        foreach (var peer in peers)
            (await mediator.Mediation.GetGrantAsync(peer.Did, default)).Should().NotBeNull();
        harness.Channel.PendingCount.Should().Be(0);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("ws-did")]
    public async Task One_local_DID_enrolling_at_two_mediators_at_the_same_time_gets_each_mediators_grant(string transport)
    {
        var scheme = transport == "http" ? "http" : "ws";
        var first = await MediatorClientHarness.MintMediatorIdentityAsync(scheme, "mediator.test");
        var second = await MediatorClientHarness.MintMediatorIdentityAsync(scheme, "m1.test");
        await using var one = await MediatorTestServer.StartAsync(o => o.Identity = first);
        await using var two = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m1.test";
            o.Identity = second;
            o.Network = one.Network;
            o.Clock = one.Clock;
        });
        // Over a WebSocket the harness connects to the first server's TestServer only, so the second
        // mediator is reached over HTTP there; the pair is still (one local DID, two mediators).
        await using var harness = await MediatorClientHarness.CreateAsync(one, o => o.UseWebSocket = transport != "http");
        var bob = await harness.NewPeerAsync();
        Uri? secondEndpoint = transport == "http" ? null : two.Endpoint;

        var grants = await Task.WhenAll(
            harness.Mediation.RequestMediationAsync(one.Did, bob.Did),
            harness.Mediation.RequestMediationAsync(two.Did, bob.Did, secondEndpoint)).WaitAsync(Qa1.Wait);

        grants[0]!.RoutingDids.Should().Equal(one.Did);
        grants[1]!.RoutingDids.Should().Equal(two.Did);
        (await one.Mediation.GetGrantAsync(bob.Did, default)).Should().NotBeNull();
        (await two.Mediation.GetGrantAsync(bob.Did, default)).Should().NotBeNull();
        harness.Channel.Versions.TryGet(one.Did, out _).Should().BeTrue();
        harness.Channel.Versions.TryGet(two.Did, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(250)]
    public async Task Pages_and_the_walker_against_the_real_mediator_return_exactly_what_is_registered(int registered)
    {
        var atMediator = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = atMediator.Register);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var mediation = harness.Mediation;
        (await mediation.RequestMediationAsync(mediator.Did, bob.Did)).Should().NotBeNull();
        var dids = Qa1.FakeDids(registered, "p");
        if (registered > 0)
        {
            var results = await mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, dids.Select(d => new RecipientUpdate(d, RecipientUpdateAction.Add)).ToArray());
            results.Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);
        }

        // The walker: every DID once, in registration order, one page per 100.
        var walked = new List<string>();
        var before = atMediator.Count(bob.Did, CoordinateMediation.RecipientQueryType);
        await foreach (var did in mediation.QueryAllRecipientsAsync(mediator.Did, bob.Did))
            walked.Add(did);
        walked.Should().Equal(dids);
        (atMediator.Count(bob.Did, CoordinateMediation.RecipientQueryType) - before).Should().Be(Math.Max(1, (registered + 99) / 100));

        // Exact pages.
        for (var offset = 0; offset <= registered + 100; offset += 100)
        {
            var page = await mediation.QueryRecipientsAsync(mediator.Did, bob.Did, new RecipientQueryPaging(100, offset));
            var expected = dids.Skip(offset).Take(100).ToArray();
            page.Dids.Should().Equal(expected);
            page.Pagination.Should().Be(new RecipientPagination(expected.Length, offset, Math.Max(0, registered - offset - expected.Length)));
        }

        // paginate edge values.
        var noPaginate = await mediation.QueryRecipientsAsync(mediator.Did, bob.Did);
        noPaginate.Dids.Should().Equal(dids.Take(100));
        noPaginate.Pagination.Should().NotBeNull("our mediator always reports the position (FR-MED-06)");
        var limitZero = await mediation.QueryRecipientsAsync(mediator.Did, bob.Did, new RecipientQueryPaging(0, 0));
        limitZero.Dids.Should().BeEmpty();
        limitZero.Pagination.Should().Be(new RecipientPagination(0, 0, registered));
        var clamped = await mediation.QueryRecipientsAsync(mediator.Did, bob.Did, new RecipientQueryPaging(1000, 0));
        clamped.Dids.Should().Equal(dids.Take(100), "the mediator clamps the limit to its page size and the client accepts it");
        var farOffset = await mediation.QueryRecipientsAsync(mediator.Did, bob.Did, new RecipientQueryPaging(10, int.MaxValue));
        farOffset.Dids.Should().BeEmpty();
        farOffset.Pagination.Should().Be(new RecipientPagination(0, int.MaxValue, 0));
        var one = await mediation.QueryRecipientsAsync(mediator.Did, bob.Did, new RecipientQueryPaging(1, Math.Max(0, registered - 1)));
        one.Dids.Should().Equal(dids.Skip(Math.Max(0, registered - 1)).Take(1));
    }

    [Fact]
    public async Task Updates_add_remove_mixed_no_change_and_client_error_come_back_in_order()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var carol = await harness.NewPeerAsync();
        var mediation = harness.Mediation;
        (await mediation.RequestMediationAsync(mediator.Did, bob.Did)).Should().NotBeNull();
        (await mediation.RequestMediationAsync(mediator.Did, carol.Did)).Should().NotBeNull();
        var d = Qa1.FakeDids(4, "u");
        (await mediation.UpdateRecipientsAsync(mediator.Did, carol.Did, new[] { new RecipientUpdate(d[3], RecipientUpdateAction.Add) }))
            .Single().Result.Should().Be(RecipientUpdateOutcome.Success);

        var mixed = new[]
        {
            new RecipientUpdate(d[0], RecipientUpdateAction.Add),
            new RecipientUpdate(d[1], RecipientUpdateAction.Add),
            new RecipientUpdate(d[0], RecipientUpdateAction.Add),          // no_change: already under this grant
            new RecipientUpdate(d[2], RecipientUpdateAction.Remove),       // client_error: not registered
            new RecipientUpdate(d[3], RecipientUpdateAction.Add),          // client_error: under Carol's grant
            new RecipientUpdate(mediator.Did, RecipientUpdateAction.Add),  // client_error: the mediator's own DID
            new RecipientUpdate(d[1], RecipientUpdateAction.Remove),       // success
        };
        var results = await mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, mixed);

        results.Select(r => (r.RecipientDid, r.Action)).Should().Equal(mixed.Select(u => (u.RecipientDid, u.Action)));
        results.Select(r => r.Result).Should().Equal(
            RecipientUpdateOutcome.Success,
            RecipientUpdateOutcome.Success,
            RecipientUpdateOutcome.NoChange,
            RecipientUpdateOutcome.ClientError,
            RecipientUpdateOutcome.ClientError,
            RecipientUpdateOutcome.ClientError,
            RecipientUpdateOutcome.Success);
        var listed = new List<string>();
        await foreach (var did in mediation.QueryAllRecipientsAsync(mediator.Did, bob.Did))
            listed.Add(did);
        listed.Should().Equal(d[0]);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task An_update_of_1024_entries_is_one_message_answered_entry_by_entry_and_1025_or_a_DID_URL_is_refused_before_sending()
    {
        var atMediator = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = atMediator.Register);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var mediation = harness.Mediation;
        (await mediation.RequestMediationAsync(mediator.Did, bob.Did)).Should().NotBeNull();
        var dids = Qa1.FakeDids(1025, "b");

        var results = await mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, dids.Take(1024).Select(x => new RecipientUpdate(x, RecipientUpdateAction.Add)).ToArray());

        results.Should().HaveCount(1024);
        results.Select(r => r.RecipientDid).Should().Equal(dids.Take(1024));
        results.Take(256).Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);
        results.Skip(256).Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.ClientError, "the per-grant bound (256 by default) is the peer's limit");
        atMediator.Count(bob.Did, CoordinateMediation.RecipientUpdateType).Should().Be(1);

        await FluentActions.Awaiting(() => mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, dids.Select(x => new RecipientUpdate(x, RecipientUpdateAction.Add)).ToArray()))
            .Should().ThrowAsync<ArgumentException>();
        var didUrl = new[] { new RecipientUpdate(bob.Did + "#key-1", RecipientUpdateAction.Add) };
        (await FluentActions.Awaiting(() => mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, didUrl)).Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().NotContain(bob.Did);
        atMediator.Count(bob.Did, CoordinateMediation.RecipientUpdateType).Should().Be(1, "neither refused list was sent");
    }

    [Fact]
    public async Task One_client_negotiates_3_0_and_2_0_with_two_mediators_remembers_each_for_an_hour_then_asks_again()
    {
        var atMediator = new Qa1Recorder();
        var time = new QaManualTimeProvider();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = atMediator.Register);
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.Time = time);
        var bob = await harness.NewPeerAsync();
        var mediation = harness.Mediation;

        var grants = await Task.WhenAll(
            mediation.RequestMediationAsync(mediator.Did, bob.Did),
            mediation.RequestMediationAsync(fake.Did, bob.Did)).WaitAsync(Qa1.Wait);
        grants[0]!.RoutingDids.Should().Equal(mediator.Did);
        grants[1]!.RoutingDids.Should().Equal(fake.Did);
        harness.Channel.Versions.TryGet(mediator.Did, out var v3).Should().BeTrue();
        harness.Channel.Versions.TryGet(fake.Did, out var v2).Should().BeTrue();
        v3.Should().Be(CoordinateMediationVersion.V3);
        v2.Should().Be(CoordinateMediationVersion.V2);

        // Within the hour: no second query.
        time.Advance(TimeSpan.FromMinutes(59));
        await mediation.QueryRecipientsAsync(mediator.Did, bob.Did);
        await mediation.QueryRecipientsAsync(fake.Did, bob.Did);
        atMediator.Count(bob.Did, DiscoverFeatures.QueriesType).Should().Be(1);
        fake.Handler!.Handled.Select(m => m.Type).Should().Equal(CoordinateMediation20.MediateRequestType, CoordinateMediation20.KeylistQueryType);

        // After the hour: asked again, and the answer is the same.
        time.Advance(TimeSpan.FromMinutes(1));
        await mediation.QueryRecipientsAsync(mediator.Did, bob.Did);
        atMediator.Count(bob.Did, DiscoverFeatures.QueriesType).Should().Be(2);
        await mediation.QueryRecipientsAsync(fake.Did, bob.Did);
        fake.Handler.Handled.Last().Type.Should().Be(CoordinateMediation20.KeylistQueryType, "2.0 again after renegotiating");
    }

    [Fact]
    public async Task A_pinned_version_the_mediator_does_not_speak_fails_as_no_answer_and_grants_nothing()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator);
        await using var v3Harness = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V3);
        await using var v2Harness = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V2);
        var bob = await v3Harness.NewPeerAsync();
        v2Harness.Hold(bob);

        // 3.0 to the 2.0-only host: no handler, 202, no answer.
        var asV3 = (await FluentActions.Awaiting(() => v3Harness.Mediation.RequestMediationAsync(fake.Did, bob.Did).WaitAsync(Qa1.Wait))
            .Should().ThrowAsync<TimeoutException>()).Which;
        asV3.Message.Should().NotContain(fake.Did).And.NotContain(bob.Did);
        fake.Handler!.Handled.Should().BeEmpty();

        // 2.0 to our 3.0-only mediator: the same, and no grant.
        await FluentActions.Awaiting(() => v2Harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did).WaitAsync(Qa1.Wait))
            .Should().ThrowAsync<TimeoutException>();
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_host_that_is_both_enrols_upstream_over_its_WebSocket_while_its_own_mediator_answers_a_recipient()
    {
        var gate = new Qa1GatedPolicy();
        await using var upstream = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m1.test";
            o.ConfigureServices = s => s.AddSingleton<IMediationPolicy>(gate);
        });
        await using var host = await MediatorAndRecipientHost.StartAsync(upstream);
        var ws = new UriBuilder(upstream.Server.BaseAddress) { Scheme = "ws", Path = upstream.Options.Pattern }.Uri;
        var hostMediation = host.App.Services.GetRequiredService<MediationClient>();

        var pending = hostMediation.RequestMediationAsync(upstream.Did, host.Did, ws);
        await gate.Entered.WaitAsync(Qa1.Wait);

        // Its own mediator keeps answering others while its request upstream waits.
        var bob = upstream.Own(await MediatorCast.MintPeerAsync());
        await host.EnrolAsync(bob, bob.Did).WaitAsync(Qa1.Wait);
        pending.IsCompleted.Should().BeFalse();

        gate.Release();
        var grant = await pending.WaitAsync(Qa1.Wait);
        grant!.RoutingDids.Should().Equal(upstream.Did);
        (await upstream.Mediation.GetGrantAsync(host.Did, default)).Should().NotBeNull();
        host.Logs.Records.Should().NotContain(r => r.Text.Contains("No protocol handler registered") && r.Level >= Microsoft.Extensions.Logging.LogLevel.Information);
        var everyone = new[] { upstream.Did, host.Did, bob.Did };
        host.Logs.Entries.Where(e => everyone.Any(v => e.Contains(v, StringComparison.Ordinal))).Should().BeEmpty();
    }

    /// <summary>
    /// Q1-1. The documented set-up for a WebSocket route is <c>UseWebSocketTransport(o =&gt;
    /// o.ReceiveInbound = true)</c> plus <c>UseInboundDispatch()</c>, and the docs promise
    /// ("Without both, a WebSocket route is refused before anything is sent",
    /// <c>MediatorClientBuilderExtensions.AddMediatorClient</c> remarks; "without them a WebSocket
    /// route is refused before anything is sent", <c>MediationClient</c> remarks; design D-4). With
    /// <c>UseInboundDispatch()</c> but <c>ReceiveInbound</c> left at its default (off), the channel
    /// thinks it can receive (it checks only that an <c>IInboundFrameConsumer</c> is registered), so
    /// the request is sent, the mediator acts on it (grants), its answer is discarded by the
    /// transport, and the caller gets a <see cref="TimeoutException"/> after the full
    /// <c>RequestTimeout</c> instead of the clear <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public async Task Q1_1_With_inbound_dispatch_but_ReceiveInbound_off_a_WebSocket_route_is_refused_before_anything_is_sent()
    {
        var atMediator = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = atMediator.Register);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.UseInboundDispatch = true;
            o.WebSocket = w => w.ReceiveInbound = false;
            o.Client = c =>
            {
                c.RequestTimeout = TimeSpan.FromSeconds(2);
                c.CoordinateMediationVersion = CoordinateMediationVersion.V3;
            };
        });
        var bob = await harness.NewPeerAsync();

        var act = () => harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).WaitAsync(Qa1.Wait);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("ReceiveInbound");
        atMediator.From(bob.Did).Should().BeEmpty("nothing is sent on a route the client cannot receive on");
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();
    }
}
