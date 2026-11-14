using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.DiscoverFeatures;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Coordinate Mediation version negotiation (#96 design §4.4, D-10, D-11; FR-MED-09, FR-MED-10):
/// a pinned version sends no query; otherwise a Discover Features query on the operation's own
/// route, whose answer is read within bounds (the first 256 disclosures, ids of at most 256
/// printable characters, roles), prefers 3.x over 2.x in the mediator role and is remembered per
/// mediator DID subject (64 entries, one hour, no insert when full); neither version is
/// <see cref="MediationNotSupportedException"/> with a cleaned <c>DisclosedProtocols</c> and a text
/// free of wire values; an unanswered query is the exchange's <see cref="TimeoutException"/>.
/// </summary>
public sealed class MediationClientNegotiationTests
{
    private const string Mediator = FakeMediatorExchange.MediatorDid;
    private const string Me = FakeMediatorExchange.LocalDid;
    private const string Cm3 = CoordinateMediation.ProtocolUri;
    private const string Cm2 = CoordinateMediation20.ProtocolUri;

    // ---- the query ------------------------------------------------------------------------

    [Fact]
    public async Task The_query_asks_for_every_coordinate_mediation_version_on_the_operations_route()
    {
        var exchange = new FakeMediatorExchange();

        await exchange.Client().RequestMediationAsync(Mediator, Me);

        exchange.RouteCalls.Should().ContainSingle("the query and the request share the operation's one route");
        var query = exchange.Queries.Should().ContainSingle().Subject;
        query.Spec.Should().BeSameAs(FeatureNegotiation.QuerySpec);
        query.Spec.Expected.Select(t => t.Value).Should().Equal(DiscoverFeatures.DiscloseType);
        query.Message.Type.Should().Be(DiscoverFeatures.QueriesType);
        query.Message.From.Should().Be(Me);
        query.Message.To.Should().Equal(Mediator);
        query.Message.CreatedTime.Should().BeNull();
        MediationClientTests.IsUuidV4(query.Message.Id).Should().BeTrue();
        query.Message.Body!.ToJsonString().Should().Be("""{"queries":[{"feature-type":"protocol","match":"https://didcomm.org/coordinate-mediation/*"}]}""");
        exchange.Sent.Select(s => s.Message.Type).Should().Equal(DiscoverFeatures.QueriesType, CoordinateMediation.MediateRequestType);
    }

    [Theory]
    [InlineData(CoordinateMediationVersion.V3, CoordinateMediation.MediateRequestType)]
    [InlineData(CoordinateMediationVersion.V2, CoordinateMediation20.MediateRequestType)]
    public async Task A_pinned_version_sends_no_query(CoordinateMediationVersion pinned, string expectedType)
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(pinned));
        exchange.Mediator.Disclosures.Clear(); // a mediator without Discover Features (e.g. Identus)

        (await exchange.Client().RequestMediationAsync(Mediator, Me)).Should().NotBeNull();

        exchange.Queries.Should().BeEmpty();
        exchange.Sent.Should().ContainSingle().Which.Message.Type.Should().Be(expectedType);
        exchange.Versions.Count.Should().Be(0, "a pinned version is not negotiated, so nothing is remembered");
    }

    // ---- choosing -------------------------------------------------------------------------

    [Fact]
    public async Task Both_disclosed_chooses_3_0()
    {
        var exchange = Disclosing(Protocol(Cm2, "mediator"), Protocol(Cm3, "mediator"));

        await exchange.Client().RequestMediationAsync(Mediator, Me);

        exchange.Operations.Single().Message.Type.Should().Be(CoordinateMediation.MediateRequestType);
    }

    [Fact]
    public async Task Only_2_0_disclosed_speaks_2_0_in_its_own_shapes_for_every_operation()
    {
        var exchange = Disclosing(Protocol(Cm2, "mediator"));
        exchange.Mediator.Registered.Add("did:example:listed");
        var client = exchange.Client();

        var grant = await client.RequestMediationAsync(Mediator, Me);
        var updated = await client.UpdateRecipientsAsync(Mediator, Me, new[] { MediationClientTests.Add("did:example:bob") });
        var page = await client.QueryRecipientsAsync(Mediator, Me);

        grant!.RoutingDids.Should().Equal("did:example:routing");
        updated.Should().ContainSingle().Which.Result.Should().Be(RecipientUpdateOutcome.Success);
        page.Dids.Should().Equal("did:example:listed", "did:example:bob");
        exchange.Operations.Select(s => s.Message.Type).Should().Equal(
            CoordinateMediation20.MediateRequestType, CoordinateMediation20.KeylistUpdateType, CoordinateMediation20.KeylistQueryType);
        exchange.Operations.Select(s => s.Spec).Should().Equal(
            CoordinateMediationWire.MediateSpec2, CoordinateMediationWire.UpdateSpec2, CoordinateMediationWire.QuerySpec2);
        exchange.Queries.Should().ContainSingle("the version is remembered after the first query");
    }

    [Theory]
    [InlineData("https://didcomm.org/coordinate-mediation/3.1", CoordinateMediationVersion.V3)]
    [InlineData("https://DIDComm.org/coordinate_mediation/3.0", CoordinateMediationVersion.V3)]
    [InlineData("https://didcomm.org/coordinate-mediation/2.4", CoordinateMediationVersion.V2)]
    public async Task Versions_are_matched_by_major_ignoring_case_and_punctuation(string disclosed, CoordinateMediationVersion expected)
    {
        var exchange = Disclosing(Protocol(disclosed, "mediator"));

        await exchange.Client().RequestMediationAsync(Mediator, Me);

        exchange.Versions.TryGet(Mediator, out var version).Should().BeTrue();
        version.Should().Be(expected);
    }

    public static TheoryData<string, JsonNode?, bool> Roles => new()
    {
        { "absent roles say nothing, so the entry counts", null, true },
        { "mediator", new JsonArray("mediator"), true },
        { "case is ignored", new JsonArray("Mediator"), true },
        { "among others", new JsonArray("recipient", "MEDIATOR"), true },
        { "recipient only", new JsonArray("recipient"), false },
        { "empty roles", new JsonArray(), false },
        { "roles not an array", JsonValue.Create("mediator"), false },
        { "roles not strings", new JsonArray(1, 2), false },
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Only_disclosures_in_the_mediator_role_count(string because, JsonNode? roles, bool counts)
    {
        var entry = new JsonObject { ["feature-type"] = "protocol", ["id"] = Cm3 };
        if (roles is not null)
            entry["roles"] = roles;
        var exchange = DisclosingRaw(new JsonArray(entry));

        var act = () => exchange.Client().RequestMediationAsync(Mediator, Me);

        if (counts)
            await act.Should().NotThrowAsync(because);
        else
            (await act.Should().ThrowAsync<MediationNotSupportedException>(because)).Which.DisclosedProtocols.Should().Equal(Cm3);
    }

    [Fact]
    public async Task An_explicit_null_roles_counts_as_absent()
    {
        var exchange = DisclosingRaw(JsonNode.Parse($$"""[{"feature-type":"protocol","id":"{{Cm3}}","roles":null}]""")!.AsArray());

        await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_3_0_disclosure_outside_the_mediator_role_falls_back_to_a_2_0_one_inside_it()
    {
        var exchange = Disclosing(Protocol(Cm3, "recipient"), Protocol(Cm2, "mediator"));

        await exchange.Client().RequestMediationAsync(Mediator, Me);

        exchange.Operations.Single().Message.Type.Should().Be(CoordinateMediation20.MediateRequestType);
    }

    [Theory]
    [InlineData("goal-code")]
    [InlineData("Protocol")]
    [InlineData("")]
    public async Task Only_protocol_disclosures_count(string featureType)
    {
        var exchange = Disclosing(new FeatureDisclosure { FeatureType = featureType, Id = Cm3, Roles = new[] { "mediator" } });

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationNotSupportedException>()).Which;

        thrown.DisclosedProtocols.Should().BeEmpty();
    }

    // ---- neither --------------------------------------------------------------------------

    [Fact]
    public async Task Neither_version_is_MediationNotSupportedException_naming_the_query_and_quoting_nothing()
    {
        var exchange = Disclosing(
            Protocol("https://didcomm.org/trust-ping/2.0", null),
            Protocol("https://didcomm.org/coordinate-mediation/1.0", "mediator"),
            Protocol("https://example.org/secret-protocol/1.0", "mediator"));

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationNotSupportedException>()).Which;

        thrown.Should().BeAssignableTo<MediationProtocolException>();
        thrown.RequestId.Should().Be(exchange.Queries.Single().Message.Id);
        thrown.ProblemCode.Should().BeNull();
        thrown.DisclosedProtocols.Should().Equal(
            "https://didcomm.org/trust-ping/2.0", "https://didcomm.org/coordinate-mediation/1.0", "https://example.org/secret-protocol/1.0");
        thrown.Message.Should().Contain("3 protocol(s)").And.Contain(thrown.RequestId)
            .And.NotContain("secret-protocol").And.NotContain("trust-ping").And.NotContain("example.org");
        exchange.Operations.Should().BeEmpty("nothing is sent in a version the mediator did not disclose");
        exchange.Versions.Count.Should().Be(0, "a failed negotiation is not remembered");
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"disclosures":5}""")]
    [InlineData("""{"disclosures":[1,"x",null,[]]}""")]
    [InlineData("""{"disclosures":[]}""")]
    public async Task A_disclose_without_usable_disclosures_discloses_nothing(string body)
    {
        var exchange = new FakeMediatorExchange();
        exchange.Respond = r => FakeMediatorExchange.IsQuery(r.Message)
            ? ScriptedMediator.Raw(DiscoverFeatures.DiscloseType, Mediator, Me, r.Message.Id, JsonNode.Parse(body)!.AsObject())
            : exchange.Mediator.Answer(r);

        (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationNotSupportedException>())
            .Which.DisclosedProtocols.Should().BeEmpty();
    }

    [Fact]
    public async Task A_query_answered_with_another_type_is_a_protocol_error_not_a_version()
    {
        var exchange = new FakeMediatorExchange();
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.MediateGrantType, Mediator, Me, r.Message.Id, new JsonObject { ["routing_did"] = new JsonArray("did:example:r") });

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.Should().NotBeOfType<MediationNotSupportedException>();
        exchange.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unanswered_query_is_the_exchanges_TimeoutException_and_nothing_is_guessed()
    {
        var exchange = new FakeMediatorExchange();
        exchange.Respond = r => FakeMediatorExchange.IsQuery(r.Message) ? null : exchange.Mediator.Answer(r);

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<TimeoutException>()).Which;

        thrown.Message.Should().Contain(exchange.Queries.Single().Message.Id);
        exchange.Operations.Should().BeEmpty("no version is guessed");
        exchange.Versions.Count.Should().Be(0);
    }

    // ---- hostile disclosures ----------------------------------------------------------------

    [Fact]
    public async Task Only_the_first_256_disclosures_are_inspected()
    {
        // 10 000 entries: 255 unrelated, then CM 3.0 at index 256 (the 257th) — never looked at.
        var beyond = Hostile(10_000, coordinateMediationAt: 256);
        var exchange = DisclosingRaw(beyond);

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationNotSupportedException>()).Which;

        thrown.DisclosedProtocols.Should().HaveCount(FeatureNegotiation.MaxReported);
        thrown.DisclosedProtocols.Should().NotContain(Cm3);

        // At index 255 (the 256th) it is inspected.
        var within = DisclosingRaw(Hostile(10_000, coordinateMediationAt: 255));
        await FluentActions.Awaiting(() => within.Client().RequestMediationAsync(Mediator, Me)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Disclosed_protocols_are_cleaned_distinct_and_capped_at_32()
    {
        var entries = new JsonArray();
        entries.Add(Raw("https://didcomm.org/a/1.0"));
        entries.Add(Raw("https://didcomm.org/a/1.0")); // duplicate
        entries.Add(Raw("https://didcomm.org/b\u0007/1.0")); // control character
        entries.Add(Raw("https://didcomm.org/c /1.0")); // space
        entries.Add(Raw("https://didcomm.org/d\u00e9/1.0")); // non-ASCII
        entries.Add(Raw("https://didcomm.org/" + new string('e', 65_536) + "/1.0")); // 64 KiB id
        entries.Add(Raw("https://didcomm.org/" + new string('f', 230) + "/1.0")); // 254 characters: kept
        entries.Add(Raw("https://didcomm.org/" + new string('g', 234) + "/1.0")); // 258 characters: dropped
        entries.Add(Raw("not a piuri"));
        entries.Add(Raw("https://didcomm.org/h/1"));
        entries.Add(new JsonObject { ["feature-type"] = "protocol", ["id"] = 42 });
        entries.Add(new JsonObject { ["feature-type"] = "protocol" });
        for (var i = 0; i < 100; i++)
            entries.Add(Raw($"https://didcomm.org/p{i}/1.0"));
        var exchange = DisclosingRaw(entries);

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationNotSupportedException>()).Which;

        thrown.DisclosedProtocols.Should().HaveCount(32);
        thrown.DisclosedProtocols.Should().OnlyHaveUniqueItems();
        thrown.DisclosedProtocols.Take(2).Should().Equal("https://didcomm.org/a/1.0", "https://didcomm.org/" + new string('f', 230) + "/1.0");
        thrown.DisclosedProtocols.Should().AllSatisfy(id =>
        {
            id.Length.Should().BeLessThanOrEqualTo(256);
            id.Should().MatchRegex("^[\\x21-\\x7E]+$");
            DidComm.Protocols.ProtocolIdentifier.TryParse(id, out _).Should().BeTrue();
        });
        thrown.Message.Should().NotContain("eeee").And.NotContain("didcomm.org");
    }

    [Fact]
    public async Task A_coordinate_mediation_id_with_a_control_character_or_padding_is_not_recognised()
    {
        var exchange = DisclosingRaw(new JsonArray(Raw(Cm3 + "\u0000"), Raw(" " + Cm3), Raw(Cm3 + "\n")));

        (await FluentActions.Awaiting(() => exchange.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationNotSupportedException>())
            .Which.DisclosedProtocols.Should().BeEmpty();
    }

    [Fact]
    public async Task At_most_32_roles_are_read_per_disclosure()
    {
        var tooFar = new JsonArray(Enumerable.Range(0, 32).Select(i => (JsonNode)$"role{i}").Append("mediator").ToArray());
        var within = new JsonArray(Enumerable.Range(0, 31).Select(i => (JsonNode)$"role{i}").Append("mediator").ToArray());

        var refused = DisclosingRaw(new JsonArray(new JsonObject { ["feature-type"] = "protocol", ["id"] = Cm3, ["roles"] = tooFar }));
        await FluentActions.Awaiting(() => refused.Client().RequestMediationAsync(Mediator, Me)).Should().ThrowAsync<MediationNotSupportedException>();

        var accepted = DisclosingRaw(new JsonArray(new JsonObject { ["feature-type"] = "protocol", ["id"] = Cm3, ["roles"] = within }));
        await FluentActions.Awaiting(() => accepted.Client().RequestMediationAsync(Mediator, Me)).Should().NotThrowAsync();
    }

    // ---- the cache --------------------------------------------------------------------------

    [Fact]
    public async Task The_negotiated_version_is_remembered_per_mediator_for_an_hour()
    {
        var time = new ManualTimeProvider();
        var exchange = new FakeMediatorExchange(time: time);
        var client = exchange.Client();

        await client.RequestMediationAsync(Mediator, Me);
        time.Advance(TimeSpan.FromMinutes(59));
        await client.QueryRecipientsAsync(Mediator, Me);
        exchange.Queries.Should().ContainSingle("within the hour the remembered version is used");

        await client.RequestMediationAsync("did:example:other-mediator", Me);
        exchange.Queries.Should().HaveCount(2, "another mediator is negotiated on its own");

        time.Advance(TimeSpan.FromMinutes(1));
        await client.RequestMediationAsync(Mediator, Me);
        exchange.Queries.Should().HaveCount(3, "after an hour the mediator is asked again");
    }

    [Fact]
    public async Task A_full_cache_takes_no_new_mediator_so_it_is_asked_every_time()
    {
        var exchange = new FakeMediatorExchange();
        exchange.Versions = new NegotiatedVersionCache(exchange.Time, capacity: 2);
        var client = exchange.Client();

        await client.RequestMediationAsync("did:example:m1", Me);
        await client.RequestMediationAsync("did:example:m2", Me);
        await client.RequestMediationAsync("did:example:m3", Me);
        await client.RequestMediationAsync("did:example:m3", Me);
        await client.RequestMediationAsync("did:example:m1", Me);

        exchange.Queries.Select(q => q.MediatorDid).Should().Equal("did:example:m1", "did:example:m2", "did:example:m3", "did:example:m3");
    }

    private static FakeMediatorExchange Disclosing(params FeatureDisclosure[] disclosures)
    {
        var exchange = new FakeMediatorExchange();
        exchange.Mediator.Disclosures.Clear();
        exchange.Mediator.Disclosures.AddRange(disclosures);
        return exchange;
    }

    private static FakeMediatorExchange DisclosingRaw(JsonArray disclosures)
    {
        var exchange = new FakeMediatorExchange();
        exchange.Respond = r => FakeMediatorExchange.IsQuery(r.Message)
            ? ScriptedMediator.Raw(DiscoverFeatures.DiscloseType, Mediator, Me, r.Message.Id, new JsonObject { ["disclosures"] = disclosures.DeepClone() })
            : exchange.Mediator.Answer(r);
        return exchange;
    }

    private static FeatureDisclosure Protocol(string id, string? role)
        => new() { FeatureType = "protocol", Id = id, Roles = role is null ? null : new[] { role } };

    private static JsonObject Raw(string id) => new() { ["feature-type"] = "protocol", ["id"] = id };

    private static JsonArray Hostile(int count, int coordinateMediationAt)
    {
        var array = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            array.Add(i == coordinateMediationAt
                ? new JsonObject { ["feature-type"] = "protocol", ["id"] = Cm3, ["roles"] = new JsonArray("mediator") }
                : new JsonObject { ["feature-type"] = "protocol", ["id"] = $"https://didcomm.org/noise{i}/1.0", ["roles"] = new JsonArray("mediator") });
        }
        return array;
    }
}
