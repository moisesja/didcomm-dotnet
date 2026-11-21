using System.Text.RegularExpressions;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols.DiscoverFeatures;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientNegotiationTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-1 for #100 (Message Pickup 4.0), the recipient's negotiation (DC-6): the remembered version
/// lasts exactly an hour on the client's clock and is kept per mediator; disclosed ids are read as
/// the registry reads protocols — "it has to compare the protocol name and message type name
/// ignoring case and punctuation" (DIDComm v2.1 protocols.md L40 at <c>c78bcc4</c>), so 3.0's
/// spelling <c>messagepickup</c> with major 4 is Message Pickup 4 and 4.0's spelling with major 3
/// is Message Pickup 3; and the two <c>match</c> strings find their version under the three
/// readings of <c>match</c> in the field (the spec's trailing wildcard, feature_discovery.md L52;
/// a regular expression that must match the whole id, the Identus mediator; a prefix once
/// <c>.*</c> is removed, the Blocktrust mediator).
/// </summary>
public sealed class PickupClientQa1NegotiationTests
{
    private const string Pickup4 = "https://didcomm.org/message-pickup/4.0";
    private const string Pickup3 = "https://didcomm.org/messagepickup/3.0";
    private const string M2 = "did:example:mediator-two";

    // ---- the cache, through the client -------------------------------------------------------------

    [Fact]
    public async Task The_remembered_version_is_used_until_exactly_an_hour_has_passed_on_the_clients_clock_then_asked_again()
    {
        var exchange = Negotiating(Protocol(Pickup4, "mediator"));
        var client = PickupTest.Client(exchange);

        await client.GetStatusAsync(M, L);
        exchange.Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(1));
        await client.GetStatusAsync(M, L);
        var queriesBeforeTheHour = exchange.SentOf(DiscoverFeatures.QueriesType).Count;
        exchange.Clock.Advance(TimeSpan.FromTicks(1));
        await client.GetStatusAsync(M, L);
        await client.GetStatusAsync(M, L);

        queriesBeforeTheHour.Should().Be(1, "one tick short of an hour the remembered version is still used");
        exchange.Sent.Select(s => s.Type).Should().Equal(
            new[]
            {
                DiscoverFeatures.QueriesType, MessagePickup40.StatusRequestType,
                MessagePickup40.StatusRequestType,
                DiscoverFeatures.QueriesType, MessagePickup40.StatusRequestType,
                MessagePickup40.StatusRequestType,
            },
            "at one hour the entry has expired: one new question, remembered again for the call after it");
    }

    [Fact]
    public async Task Each_mediator_is_asked_once_and_spoken_to_in_the_version_it_disclosed()
    {
        var exchange = new FakePickupExchange(o => o.MessagePickupVersion = MessagePickupVersion.Negotiate)
            .On(DiscoverFeatures.QueriesType, r => Snapshot(DiscoverFeatures.CreateDisclose(r.MediatorDid, L, r.Request.Id,
                r.MediatorDid == M ? new[] { Protocol(Pickup4, "mediator"), Protocol(Pickup3, "mediator") } : new[] { Protocol(Pickup3, "mediator") })));
        var client = PickupTest.Client(exchange);

        await client.GetStatusAsync(M, L);
        await client.GetStatusAsync(M2, L);
        await client.GetStatusAsync(M, L);
        await client.GetStatusAsync(M2, L);

        exchange.Sent.Select(s => (s.MediatorDid, s.Type)).Should().Equal(
            (M, DiscoverFeatures.QueriesType), (M, MessagePickup40.StatusRequestType),
            (M2, DiscoverFeatures.QueriesType), (M2, MessagePickup.StatusRequestType),
            (M, MessagePickup40.StatusRequestType),
            (M2, MessagePickup.StatusRequestType));
        exchange.PickupVersions.TryGet(M, out var first).Should().BeTrue();
        exchange.PickupVersions.TryGet(M2, out var second).Should().BeTrue();
        (first, second).Should().Be((MessagePickupVersion.V4, MessagePickupVersion.V3));
    }

    // ---- how a disclosed id is read -------------------------------------------------------------------

    public static TheoryData<string, FeatureDisclosure[], MessagePickupVersion?> Readings => new()
    {
        // protocols.md L40: the protocol name is compared ignoring case and punctuation.
        { "3.0's spelling with major 4 is Message Pickup 4", new[] { Protocol("https://didcomm.org/messagepickup/4.0", "mediator") }, MessagePickupVersion.V4 },
        { "4.0's spelling with major 3 is Message Pickup 3", new[] { Protocol("https://didcomm.org/message-pickup/3.0", "mediator") }, MessagePickupVersion.V3 },
        { "case and underscore are punctuation too", new[] { Protocol("https://didcomm.org/Message_Pickup/4.0", "mediator") }, MessagePickupVersion.V4 },
        { "an upper-case 3.0 name", new[] { Protocol("https://didcomm.org/MESSAGEPICKUP/3.2", "mediator") }, MessagePickupVersion.V3 },
        { "major 40 is not major 4", new[] { Protocol("https://didcomm.org/message-pickup/40.0", "mediator") }, null },
        { "a version without a minor is not a PIURI", new[] { Protocol("https://didcomm.org/message-pickup/4", "mediator") }, null },
        { "a message type URI is not a PIURI", new[] { Protocol(MessagePickup40.StatusType, "mediator") }, null },
        { "another documentation URI is another protocol", new[] { Protocol("https://example.org/message-pickup/4.0", "mediator") }, null },
        // The role rule (owner decision 2026-11-18, #99): roles compared ignoring case; a list that names both counts.
        { "roles compared ignoring case", new[] { Protocol(Pickup4, "MEDIATOR"), Protocol(Pickup3, "mediator") }, MessagePickupVersion.V4 },
        { "roles naming both sides count", new[] { Roles(Pickup4, "recipient", "mediator"), Protocol(Pickup3, "mediator") }, MessagePickupVersion.V4 },
        { "4.0 twice, once per role, counts", new[] { Protocol(Pickup4, "recipient"), Protocol(Pickup4, "mediator") }, MessagePickupVersion.V4 },
    };

    [Theory]
    [MemberData(nameof(Readings))]
    public async Task A_disclosed_id_is_read_as_the_registry_reads_protocols(string because, FeatureDisclosure[] disclosed, MessagePickupVersion? expected)
    {
        var exchange = Negotiating(disclosed);
        var act = () => PickupTest.Client(exchange).GetStatusAsync(M, L);

        if (expected is not { } version)
        {
            var thrown = (await act.Should().ThrowAsync<MediationNotSupportedException>(because)).Which;
            thrown.Message.Should().NotContain(disclosed[0].Id, "the text quotes no wire value");
            thrown.DisclosedProtocols.Should().Equal(disclosed.Select(d => d.Id).Where(id => DidComm.Protocols.ProtocolIdentifier.TryParse(id, out _)),
                "every disclosure whose id is a PIURI is reported; one that is not is no protocol");
            exchange.Sent.Should().ContainSingle("nothing is sent after a negotiation that found no version");
            return;
        }

        await act();
        exchange.Sent.Select(s => s.Type).Should().Equal(
            new[] { DiscoverFeatures.QueriesType, version == MessagePickupVersion.V4 ? MessagePickup40.StatusRequestType : MessagePickup.StatusRequestType }, because);
    }

    // ---- the two match strings under the three readings in the field ------------------------------

    public static TheoryData<string> FieldReadings => new() { "spec wildcard", "regex full match", "prefix without .*" };

    [Theory]
    [MemberData(nameof(FieldReadings))]
    public void Each_query_finds_its_own_version_and_not_the_other_under_every_reading_of_match(string reading)
    {
        Func<string, string, bool> reads = reading switch
        {
            // feature_discovery.md L52: "the * wildcard ... to match a prefix".
            "spec wildcard" => (match, id) => match.EndsWith('*') ? id.StartsWith(match[..^1], StringComparison.Ordinal) : id == match,
            // identus-mediator DiscoverFeaturesExecuter.scala: Regex(match).matches(id).
            "regex full match" => (match, id) => Regex.IsMatch(id, "^(?:" + match + ")$"),
            // blocktrust.Mediator ProcessDiscoverFeaturesHandler.cs: "*", else Id.StartsWith(Match.Replace(".*", "")).
            "prefix without .*" => (match, id) => match == "*" || id.StartsWith(match.Replace(".*", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(reading)),
        };

        var four = FeatureNegotiation.PickupQueryMatch4;
        var three = FeatureNegotiation.PickupQueryMatch3;

        reads(four, Pickup4).Should().BeTrue("the 4.x query finds message-pickup/4.0");
        reads(four, "https://didcomm.org/message-pickup/4.1").Should().BeTrue("and any 4.x minor");
        reads(four, Pickup3).Should().BeFalse("but not 3.0");
        reads(three, Pickup3).Should().BeTrue("the 3.x query finds messagepickup/3.0, the id Identus and Blocktrust disclose");
        reads(three, "https://didcomm.org/messagepickup/3.1").Should().BeTrue("and any 3.x minor");
        reads(three, Pickup4).Should().BeFalse("but not 4.0");
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static FeatureDisclosure Roles(string id, params string[] roles) => new() { FeatureType = "protocol", Id = id, Roles = roles };
}
