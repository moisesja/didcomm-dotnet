using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Resolution;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #96 acceptance for Coordinate Mediation 2.0 on the client (FR-MED-09, FR-MED-10): against a host
/// whose Discover Features discloses only 2.0 (<see cref="FakeCoordinateMediation20Mediator"/>, the
/// registry-aware endpoint with a test handler answering in the 2.0 page's shapes), the client
/// negotiates 2.0 and enrols, updates and lists in 2.0 messages when its answers carry the request's
/// <c>thid</c>; one whose answers leave it out gets no answer taken, even in the HTTP response to the
/// request (DIDComm v2.1 threading). A host disclosing neither version is
/// <see cref="MediationNotSupportedException"/>; a mediator disclosing 3.0 and 2.0 (this library's
/// own, as <c>AddMediator()</c> registers it) is spoken to in 3.0. A 2.0 host that ignores <c>paginate</c> (its whole list at offset 0, with or without a
/// <c>pagination</c>) is listed in one page larger than asked for, and one holding more than 1000
/// keys fails the walk rather than truncating it.
/// </summary>
public sealed class MediatorClientCoordinateMediation20Tests
{
    [Fact]
    public async Task Against_a_2_0_only_host_the_client_negotiates_2_0_and_enrols_updates_and_lists_in_2_0()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        var (grant, published) = await MediatorClientEnrolmentTests.EnrolAsync(harness, fake.Did, bob);

        grant.RoutingDids.Should().Equal(new[] { fake.Did }, "a 2.0 grant's single routing_did is a one-element list");
        fake.Handler!.Handled.Select(m => m.Type).Should().Equal(
            CoordinateMediation20.MediateRequestType,
            CoordinateMediation20.KeylistUpdateType,
            CoordinateMediation20.KeylistQueryType,
            CoordinateMediation20.KeylistQueryType);
        fake.Handler.KeysOf(bob.Did).Should().Equal(published.Did);
        harness.Channel.Versions.TryGet(fake.Did, out var version).Should().BeTrue();
        version.Should().Be(CoordinateMediationVersion.V2);

        // The service entry minted from a 2.0 grant points at the 2.0 mediator.
        var entry = DidCommServiceBuilder.ForMediation(grant).Build();
        entry.ServiceEndpoint!.Map!["uri"].GetString().Should().Be(fake.Did);
        mediator.LeakingLogLines(published.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_2_0_host_whose_answers_leave_out_thid_gets_no_answer_taken_and_the_mediate_request_fails_as_unanswered()
    {
        // Before #98 (PR #130 review, round 2 follow-up) the HTTP response was the answer with or
        // without thid, so this host enrolled. Without thid an answer belongs to no request's thread
        // (DIDComm v2.1 threading.md L17, message_structure.md L130), on every path.
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator, omitThid: true);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        var thrown = (await FluentActions.Awaiting(() => harness.Mediation.RequestMediationAsync(fake.Did, bob.Did))
            .Should().ThrowAsync<TimeoutException>()).Which;

        thrown.Message.Should().Contain("no answer in the HTTP response").And.NotContain(fake.Did).And.NotContain(bob.Did);
        fake.Handler!.Handled.Select(m => m.Type).Should().Equal(new[] { CoordinateMediation20.MediateRequestType },
            "the version was negotiated (the disclose carries its thid) and the mediate-request sent; nothing after it");
        harness.Channel.Versions.TryGet(fake.Did, out var version).Should().BeTrue();
        version.Should().Be(CoordinateMediationVersion.V2);
    }

    [Fact]
    public async Task A_host_disclosing_neither_version_is_MediationNotSupportedException_and_nothing_else_is_sent()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator, speaks20: false);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        var thrown = (await FluentActions.Awaiting(() => harness.Mediation.RequestMediationAsync(fake.Did, bob.Did))
            .Should().ThrowAsync<MediationNotSupportedException>()).Which;

        thrown.DisclosedProtocols.Should().BeEmpty("the query asks for coordinate-mediation/* only");
        thrown.RequestId.Should().NotBeNullOrEmpty();
        thrown.Message.Should().NotContain(fake.Did).And.NotContain(bob.Did);
        harness.Channel.Versions.TryGet(fake.Did, out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_mediator_disclosing_3_0_and_2_0_is_spoken_to_in_3_0()
    {
        // This library's mediator as AddMediator() registers it: it answers and discloses both versions.
        var seen = new Qa1Recorder();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = seen.Register);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var asker = await mediator.NewPeerAsync();
        var disclosed = await mediator.AskAsync(asker, DiscoverFeatures.CreateQuery(asker.Did, mediator.Did,
            new FeatureQuery { FeatureType = DiscoverFeatures.FeatureTypeProtocol, Match = "https://didcomm.org/coordinate-mediation/*" }));
        DiscoverFeatures.ReadDisclosures(disclosed.Message).Select(d => d.Id)
            .Should().BeEquivalentTo(new[] { CoordinateMediation.ProtocolUri, CoordinateMediation20.ProtocolUri }, "the mediator discloses both");

        var (grant, published) = await MediatorClientEnrolmentTests.EnrolAsync(harness, mediator.Did, bob);

        grant.RoutingDids.Should().Equal(mediator.Did);
        seen.From(bob.Did).Should().NotContain(r => r.Type.StartsWith(CoordinateMediation20.ProtocolUri + "/", StringComparison.Ordinal),
            "3.0 is preferred when both are disclosed");
        seen.From(bob.Did).Should().Contain(r => r.Type == CoordinateMediation.MediateRequestType);
        (await mediator.Mediation.FindGrantByRecipientDidAsync(published.Did, default))!.PeerDid.Should().Be(bob.Did);
        harness.Channel.Versions.TryGet(mediator.Did, out var version).Should().BeTrue();
        version.Should().Be(CoordinateMediationVersion.V3);
    }

    [Theory]
    [InlineData(KeylistPaging.IgnoredWithPosition)]
    [InlineData(KeylistPaging.IgnoredWithoutPosition)]
    public async Task A_2_0_host_that_ignores_paginate_is_listed_in_one_page_larger_than_asked_for(KeylistPaging paging)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator, paging: paging);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var mediation = harness.Mediation;
        (await mediation.RequestMediationAsync(fake.Did, bob.Did)).Should().NotBeNull();
        var keys = await RegisterAsync(mediation, fake, bob.Did, 250); // more than the walker's 100 per page
        var queriesBefore = KeylistQueries(fake);

        var walked = new List<string>();
        await foreach (var did in mediation.QueryAllRecipientsAsync(fake.Did, bob.Did))
            walked.Add(did);

        walked.Should().Equal(keys);
        (KeylistQueries(fake) - queriesBefore).Should().Be(1, "the first page holds the whole list and says so (no position, or nothing remaining)");

        var page = await mediation.QueryRecipientsAsync(fake.Did, bob.Did, new RecipientQueryPaging(10, 0));
        page.Dids.Should().Equal(keys, "250 DIDs for a limit of 10 are accepted: a page is bounded by 1000, not by the limit asked for");
        if (paging == KeylistPaging.IgnoredWithPosition)
        {
            page.Pagination.Should().Be(new RecipientPagination(250, 0, 0));
            // A later page asked for comes back at offset 0: the reported position gives it away.
            (await FluentActions.Awaiting(() => mediation.QueryRecipientsAsync(fake.Did, bob.Did, new RecipientQueryPaging(10, 20)))
                .Should().ThrowAsync<MediationProtocolException>()).Which.Message.Should().Contain("pagination").And.NotContain("did:");
        }
        else
        {
            page.Pagination.Should().BeNull("without a position nothing can be checked");
        }
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_2_0_host_that_ignores_paginate_and_holds_more_than_1000_keys_fails_the_walk_instead_of_truncating_it()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator, paging: KeylistPaging.IgnoredWithoutPosition);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();
        var mediation = harness.Mediation;
        (await mediation.RequestMediationAsync(fake.Did, bob.Did)).Should().NotBeNull();
        await RegisterAsync(mediation, fake, bob.Did, 1001);

        var walked = new List<string>();
        var walk = async () =>
        {
            await foreach (var did in mediation.QueryAllRecipientsAsync(fake.Did, bob.Did))
                walked.Add(did);
        };

        var thrown = (await walk.Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.InnerException.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("more than 1000 entries");
        thrown.Message.Should().NotContain("did:");
        walked.Should().BeEmpty("no DID of a refused page is handed over");
    }

    [Fact]
    public async Task A_pinned_2_0_client_enrols_with_the_2_0_host_without_asking_first()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V2);
        var bob = await harness.NewPeerAsync();

        (await harness.Mediation.RequestMediationAsync(fake.Did, bob.Did))!.RoutingDids.Should().Equal(fake.Did);

        fake.Handler!.Handled.Select(m => m.Type).Should().Equal(CoordinateMediation20.MediateRequestType);
        harness.Channel.Versions.TryGet(fake.Did, out _).Should().BeFalse("a pinned version is not negotiated");
    }

    // Registers count keys under peerDid's grant, at most 1024 per keylist-update; returns them in order.
    private static async Task<string[]> RegisterAsync(MediationClient mediation, FakeCoordinateMediation20Mediator fake, string peerDid, int count)
    {
        var keys = Enumerable.Range(0, count).Select(i => $"did:example:key{i:D4}").ToArray();
        foreach (var chunk in keys.Chunk(1024))
        {
            var results = await mediation.UpdateRecipientsAsync(fake.Did, peerDid, chunk.Select(k => new RecipientUpdate(k, RecipientUpdateAction.Add)).ToArray());
            results.Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);
        }
        fake.Handler!.KeysOf(peerDid).Should().Equal(keys);
        return keys;
    }

    private static int KeylistQueries(FakeCoordinateMediation20Mediator fake)
        => fake.Handler!.Handled.Count(m => m.Type == CoordinateMediation20.KeylistQueryType);
}
