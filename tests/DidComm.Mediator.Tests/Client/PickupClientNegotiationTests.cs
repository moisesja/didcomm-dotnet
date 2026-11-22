using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Protocols.DiscoverFeatures;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Message Pickup version negotiation (#100 DC-6, FR-PICK-12): with
/// <see cref="MessagePickupVersion.Negotiate"/>, every <see cref="PickupClient"/> call resolves the
/// version after its route — the pin, else the version remembered for the mediator, else one
/// Discover Features query on the same route with two queries (4.x first, then 3.x) — and speaks
/// 4.0 when a 4.x disclosure counts in the mediator role, 3.0 when only a 3.x one does; neither is
/// <see cref="MediationNotSupportedException"/>. The role rule is Coordinate Mediation's (owner
/// decision 2026-11-18, #99).
/// </summary>
public sealed class PickupClientNegotiationTests
{
    private const string Pickup4 = "https://didcomm.org/message-pickup/4.0";
    private const string Pickup3 = "https://didcomm.org/messagepickup/3.0";

    // ---- the query ------------------------------------------------------------------------

    [Fact]
    public async Task The_query_is_one_message_with_two_queries_4_first_then_3_sent_on_the_calls_route_before_the_request()
    {
        var exchange = Negotiating(Protocol(Pickup4, "mediator"));

        await PickupTest.Client(exchange).GetStatusAsync(M, L);

        exchange.RouteCalls.Should().ContainSingle("the query and the request share the call's one route");
        exchange.Sent.Select(s => s.Type).Should().Equal(DiscoverFeatures.QueriesType, MessagePickup40.StatusRequestType);
        var query = exchange.Sent[0];
        query.Spec.Should().BeSameAs(FeatureNegotiation.QuerySpec);
        query.Route.Should().Be(exchange.Route);
        query.Request.From.Should().Be(L);
        query.Request.To.Should().Equal(M);
        query.Request.Body!.ToJsonString().Should().Be(
            """{"queries":[{"feature-type":"protocol","match":"https://didcomm.org/message-pickup/4.*"},{"feature-type":"protocol","match":"https://didcomm.org/messagepickup/3.*"}]}""");
    }

    // ---- choosing -------------------------------------------------------------------------

    public static TheoryData<string, FeatureDisclosure[], MessagePickupVersion?> Table => new()
    {
        { "both disclosed: 4.0 wins", new[] { Protocol(Pickup3, "mediator"), Protocol(Pickup4, "mediator") }, MessagePickupVersion.V4 },
        { "only 3.0", new[] { Protocol(Pickup3, "mediator") }, MessagePickupVersion.V3 },
        { "Identus 1.2.1: messagepickup/3.0 roles [mediator]", new[] { Protocol(CoordinateMediation.ProtocolUri, "mediator"), Protocol(Pickup3, "mediator") }, MessagePickupVersion.V3 },
        { "roles [] says nothing, so 4.0 counts", new[] { Protocols(Pickup4), Protocol(Pickup3, "mediator") }, MessagePickupVersion.V4 },
        { "absent roles count", new[] { Protocol(Pickup4, null) }, MessagePickupVersion.V4 },
        { "4.0 in the recipient role only does not count", new[] { Protocol(Pickup4, "recipient"), Protocol(Pickup3, "mediator") }, MessagePickupVersion.V3 },
        { "a 4.x minor counts as 4.0", new[] { Protocol("https://didcomm.org/message-pickup/4.1", "mediator") }, MessagePickupVersion.V4 },
        { "neither: only Coordinate Mediation", new[] { Protocol(CoordinateMediation.ProtocolUri, "mediator") }, null },
        { "neither: 4.0 and 3.0 in the recipient role only", new[] { Protocol(Pickup4, "recipient"), Protocol(Pickup3, "recipient") }, null },
        { "neither: another major", new[] { Protocol("https://didcomm.org/messagepickup/2.0", "mediator"), Protocol("https://didcomm.org/message-pickup/5.0", "mediator") }, null },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public async Task The_disclosure_decides_the_version(string because, FeatureDisclosure[] disclosed, MessagePickupVersion? expected)
    {
        var exchange = Negotiating(disclosed);
        var act = () => PickupTest.Client(exchange).GetStatusAsync(M, L);

        if (expected is not { } version)
        {
            var thrown = (await act.Should().ThrowAsync<MediationNotSupportedException>(because)).Which;
            var query = exchange.Sent[0].Request.Id;
            thrown.RequestId.Should().Be(query);
            thrown.Message.Should().Contain("none of them Message Pickup 4.0 or 3.0 in the mediator role").And.Contain(query);
            foreach (var disclosure in disclosed)
                thrown.Message.Should().NotContain(disclosure.Id, "the text quotes no wire value");
            thrown.DisclosedProtocols.Should().Equal(disclosed.Select(d => d.Id), "every protocol disclosure inspected is reported, whatever its roles");
            exchange.Sent.Should().ContainSingle("nothing is sent after a negotiation that found no version");
            exchange.PickupVersions.Count.Should().Be(0, "a failed negotiation is not remembered");
            return;
        }

        (await act()).MessageCount.Should().Be(0);
        exchange.Sent.Select(s => s.Type).Should().Equal(
            new[] { DiscoverFeatures.QueriesType, version == MessagePickupVersion.V4 ? MessagePickup40.StatusRequestType : MessagePickup.StatusRequestType }, because);
        exchange.PickupVersions.TryGet(M, out var remembered).Should().BeTrue();
        remembered.Should().Be(version);
        exchange.Versions.Count.Should().Be(0, "the Coordinate Mediation cache is another cache");
    }

    [Fact]
    public async Task The_negotiated_version_is_remembered_so_a_second_call_sends_no_query()
    {
        var exchange = Negotiating(Protocol(Pickup4, "mediator"));
        var client = PickupTest.Client(exchange);

        await client.GetStatusAsync(M, L);
        await client.FetchAsync(M, L, limit: 5);

        exchange.Sent.Select(s => s.Type).Should().Equal(DiscoverFeatures.QueriesType, MessagePickup40.StatusRequestType, MessagePickup40.DeliveryRequestType);
    }

    [Fact]
    public async Task An_unremembered_mediator_is_asked_again_when_the_cache_remembers_nothing()
    {
        // Control for the test above: what stops the second query is the cache.
        var exchange = Negotiating(Protocol(Pickup4, "mediator"));
        exchange.PickupVersions = new NegotiatedVersionCache<MessagePickupVersion>(exchange.Clock, capacity: 0);
        var client = PickupTest.Client(exchange);

        await client.GetStatusAsync(M, L);
        await client.GetStatusAsync(M, L);

        exchange.Sent.Count(s => s.Is(DiscoverFeatures.QueriesType)).Should().Be(2);
    }

    [Theory]
    [InlineData(MessagePickupVersion.V4, MessagePickup40.StatusRequestType)]
    [InlineData(MessagePickupVersion.V3, MessagePickup.StatusRequestType)]
    public async Task A_pinned_version_sends_no_query_and_wins_over_a_remembered_one(MessagePickupVersion pinned, string expectedType)
    {
        var exchange = new FakePickupExchange(o => o.MessagePickupVersion = pinned);
        exchange.PickupVersions.Set(M, pinned == MessagePickupVersion.V4 ? MessagePickupVersion.V3 : MessagePickupVersion.V4);

        await PickupTest.Client(exchange).GetStatusAsync(M, L);

        exchange.Sent.Should().ContainSingle().Which.Type.Should().Be(expectedType);
    }

    [Fact]
    public async Task Every_method_resolves_the_version_after_its_route()
    {
        var exchange = Negotiating(Protocol(Pickup4, "mediator"));
        exchange.PickupVersions = new NegotiatedVersionCache<MessagePickupVersion>(exchange.Clock, capacity: 0);
        exchange.Route = WebSocketRoute;
        var client = PickupTest.Client(exchange);

        await client.GetStatusAsync(M, L);
        await client.FetchAsync(M, L, limit: 1);
        await client.AckAsync(M, L, new[] { "q1" });
        await client.SetLiveDeliveryAsync(M, L, liveDelivery: true);

        exchange.Sent.Select(s => s.Type).Should().Equal(
            DiscoverFeatures.QueriesType, MessagePickup40.StatusRequestType,
            DiscoverFeatures.QueriesType, MessagePickup40.DeliveryRequestType,
            DiscoverFeatures.QueriesType, MessagePickup40.MessagesReceivedType,
            DiscoverFeatures.QueriesType, MessagePickup40.LiveDeliveryChangeType);
        exchange.RouteCalls.Should().HaveCount(4, "one route per call, resolved before its query");
    }

    [Fact]
    public async Task A_query_that_gets_no_answer_fails_the_call_and_nothing_else_is_sent()
    {
        var exchange = new FakePickupExchange(o => o.MessagePickupVersion = MessagePickupVersion.Negotiate)
            .On(DiscoverFeatures.QueriesType, r => Task.FromException<DidComm.Protocols.InboundMessageSnapshot>(new TimeoutException($"Mediator request '{r.Request.Id}' (discover-features query) was not answered.")));

        var act = () => PickupTest.Client(exchange).FetchAsync(M, L, limit: 5);

        await act.Should().ThrowAsync<TimeoutException>();
        exchange.Sent.Should().ContainSingle().Which.Type.Should().Be(DiscoverFeatures.QueriesType, "no version is guessed");
        exchange.PickupVersions.Count.Should().Be(0);
    }

    [Fact]
    public async Task The_negotiation_is_logged_at_Debug_with_a_template_of_its_own()
    {
        var exchange = Negotiating(Protocol(Pickup4, "mediator"));
        var logs = new PickupTestLogs();

        await PickupTest.Client(exchange, logs).GetStatusAsync(M, L);

        var line = logs.Entries.Should().ContainSingle(e => e.Message.StartsWith("Message Pickup version negotiated by query ", StringComparison.Ordinal)).Subject;
        line.Level.Should().Be(LogLevel.Debug);
        line.Message.Should().EndWith(": V4.");
        logs.Entries.Should().NotContain(e => e.Message.StartsWith("Coordinate Mediation version negotiated", StringComparison.Ordinal),
            "the live wallet counts Coordinate Mediation's line; a pickup negotiation must not add to that count");
        logs.Entries.Should().NotContain(e => e.Message.Contains(M, StringComparison.Ordinal) || e.Message.Contains(L, StringComparison.Ordinal));
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>A fake mediator that negotiates (no pin) and answers the query with <paramref name="disclosures"/>.</summary>
    internal static FakePickupExchange Negotiating(params FeatureDisclosure[] disclosures)
        => new FakePickupExchange(o => o.MessagePickupVersion = MessagePickupVersion.Negotiate)
            .On(DiscoverFeatures.QueriesType, r => Snapshot(DiscoverFeatures.CreateDisclose(M, L, r.Request.Id, disclosures)));

    internal static FeatureDisclosure Protocol(string id, string? role)
        => new() { FeatureType = "protocol", Id = id, Roles = role is null ? null : new[] { role } };

    // A disclosure whose roles list is present and empty.
    private static FeatureDisclosure Protocols(string id) => new() { FeatureType = "protocol", Id = id, Roles = Array.Empty<string>() };
}
