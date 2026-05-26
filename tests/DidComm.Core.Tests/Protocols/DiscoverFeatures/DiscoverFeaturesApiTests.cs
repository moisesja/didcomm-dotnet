using System.Text.Json.Nodes;
using DidComm.Protocols.DiscoverFeatures;
using FluentAssertions;
using Xunit;

// L-014: alias the static API class to dodge namespace shadowing.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.Tests.Protocols.DiscoverFeatures;

public sealed class DiscoverFeaturesApiTests
{
    [Fact]
    public void CreateQuery_emits_spec_body_shape()
    {
        var msg = DiscoverFeaturesApi.CreateQuery(
            from: "did:peer:alice", to: "did:peer:bob",
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "https://didcomm.org/*" },
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeConstraint, Match = DiscoverFeaturesApi.ConstraintMaxReceiveBytes });

        msg.Type.Should().Be(DiscoverFeaturesApi.QueriesType);
        msg.Body.Should().NotBeNull();
        var queries = msg.Body!["queries"]!.AsArray();
        queries.Should().HaveCount(2);

        // Spec uses the hyphenated wire field, not snake_case.
        queries[0]!["feature-type"]!.GetValue<string>().Should().Be("protocol");
        queries[0]!["match"]!.GetValue<string>().Should().Be("https://didcomm.org/*");
        queries[1]!["feature-type"]!.GetValue<string>().Should().Be("constraint");
        queries[1]!["match"]!.GetValue<string>().Should().Be("max_receive_bytes");
    }

    [Fact]
    public void CreateDisclose_threads_to_thid_and_carries_disclosures()
    {
        var disclose = DiscoverFeaturesApi.CreateDisclose(
            from: "did:peer:bob", to: "did:peer:alice", thid: "query-id-1",
            new FeatureDisclosure { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Id = "https://didcomm.org/trust-ping/2.0" },
            new FeatureDisclosure { FeatureType = DiscoverFeaturesApi.FeatureTypeConstraint, Id = DiscoverFeaturesApi.ConstraintMaxReceiveBytes, Value = 1_048_576 });

        disclose.Type.Should().Be(DiscoverFeaturesApi.DiscloseType);
        disclose.Thid.Should().Be("query-id-1");
        var disclosures = disclose.Body!["disclosures"]!.AsArray();
        disclosures.Should().HaveCount(2);
        disclosures[0]!["id"]!.GetValue<string>().Should().Be("https://didcomm.org/trust-ping/2.0");
        disclosures[1]!["value"]!.GetValue<long>().Should().Be(1_048_576);
    }

    [Fact]
    public void ReadQueries_parses_back_what_CreateQuery_emits()
    {
        var msg = DiscoverFeaturesApi.CreateQuery(
            from: "did:peer:alice", to: "did:peer:bob",
            new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/*" });

        var parsed = DiscoverFeaturesApi.ReadQueries(msg);
        parsed.Should().HaveCount(1);
        parsed[0].FeatureType.Should().Be("protocol");
        parsed[0].Match.Should().Be("https://didcomm.org/*");
    }

    [Fact]
    public void ReadDisclosures_omits_null_value_field_when_not_set()
    {
        var disclose = DiscoverFeaturesApi.CreateDisclose(
            from: "did:peer:bob", to: "did:peer:alice", thid: "thid",
            new FeatureDisclosure { FeatureType = "protocol", Id = "https://didcomm.org/empty/1.0" });

        var arr = disclose.Body!["disclosures"]!.AsArray();
        arr[0]!.AsObject().Should().NotContainKey("value");

        var parsed = DiscoverFeaturesApi.ReadDisclosures(disclose);
        parsed[0].Value.Should().BeNull();
    }

    [Fact]
    public void ReadQueries_returns_empty_when_body_absent()
    {
        var noBody = new DidComm.Messages.MessageBuilder().WithType(DiscoverFeaturesApi.QueriesType).Build();
        DiscoverFeaturesApi.ReadQueries(noBody).Should().BeEmpty();
    }

    [Fact]
    public void ReadQueries_skips_malformed_entries_rather_than_failing()
    {
        var body = JsonNode.Parse("""
            {"queries":[
                {"feature-type":"protocol","match":"https://didcomm.org/*"},
                "this is not an object",
                {"feature-type":"goal-code","match":"aries.*"}
            ]}
            """)!.AsObject();
        var msg = new DidComm.Messages.MessageBuilder().WithType(DiscoverFeaturesApi.QueriesType).WithBody(body).Build();

        var parsed = DiscoverFeaturesApi.ReadQueries(msg);
        // FR-PROTO-05 wants permissive parsing — drop the rogue entry, keep the two valid ones.
        parsed.Should().HaveCount(2);
        parsed.Select(q => q.FeatureType).Should().Equal("protocol", "goal-code");
    }

    [Fact]
    public void CreateQuery_rejects_empty_queries_array()
    {
        ((Action)(() => DiscoverFeaturesApi.CreateQuery("did:peer:a", "did:peer:b", Array.Empty<FeatureQuery>())))
            .Should().Throw<ArgumentException>();
    }
}
