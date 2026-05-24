using System.Text.Json;
using DidComm.Resolution;
using FluentAssertions;
using NetDid.Core.Model;
using Xunit;

namespace DidComm.Tests.Resolution;

/// <summary>
/// Phase 4 Checkpoint B — covers <see cref="ServiceEndpointParser"/> against the FR-ROUTE-03
/// conformance shapes (object and array of objects) plus the DD-10 bare-string tolerance and
/// negative cases (missing uri, non-DIDCommMessaging types, empty input).
/// </summary>
public sealed class ServiceEndpointParserTests
{
    [Fact]
    public void Parses_canonical_single_object_with_uri_and_routing_keys_and_accept()
    {
        var endpoint = ServiceEndpointValue.FromMap(MakeMap(
            ("uri", "https://example.com/path"),
            ("routingKeys", JsonElement(@"[""did:example:mediator#k""]")),
            ("accept", JsonElement(@"[""didcomm/v2""]"))));

        var services = new[] { MakeService(endpoint) };

        var result = ServiceEndpointParser.Parse(services);

        result.Should().ContainSingle();
        var entry = result[0];
        entry.Uri.Should().Be("https://example.com/path");
        entry.RoutingKeys.Should().ContainSingle().Which.Should().Be("did:example:mediator#k");
        entry.Accept.Should().ContainSingle().Which.Should().Be("didcomm/v2");
    }

    [Fact]
    public void Parses_array_of_objects_preserving_preference_order()
    {
        var first = ServiceEndpointValue.FromMap(MakeMap(("uri", "https://first.example/")));
        var second = ServiceEndpointValue.FromMap(MakeMap(("uri", "https://second.example/")));
        var set = ServiceEndpointValue.FromSet(new[] { first, second });

        var result = ServiceEndpointParser.Parse(new[] { MakeService(set) });

        result.Should().HaveCount(2);
        result[0].Uri.Should().Be("https://first.example/");
        result[1].Uri.Should().Be("https://second.example/");
    }

    [Fact]
    public void Skips_service_entries_whose_type_is_not_DIDCommMessaging()
    {
        var didcomm = MakeService(ServiceEndpointValue.FromMap(MakeMap(("uri", "https://kept/"))));
        var other = MakeService(
            ServiceEndpointValue.FromMap(MakeMap(("uri", "https://skipped/"))),
            type: "LinkedDomains");

        var result = ServiceEndpointParser.Parse(new[] { other, didcomm });

        result.Should().ContainSingle().Which.Uri.Should().Be("https://kept/");
    }

    [Fact]
    public void Skips_object_form_entries_missing_a_uri_string()
    {
        var endpoint = ServiceEndpointValue.FromMap(MakeMap(
            ("accept", JsonElement(@"[""didcomm/v2""]"))));

        var result = ServiceEndpointParser.Parse(new[] { MakeService(endpoint) });

        result.Should().BeEmpty("uri is REQUIRED inside the v2.1 object form (FR-ROUTE-03)");
    }

    [Fact]
    public void Empty_or_null_inputs_return_an_empty_list()
    {
        ServiceEndpointParser.Parse(null).Should().BeEmpty();
        ServiceEndpointParser.Parse(Array.Empty<Service>()).Should().BeEmpty();
    }

    [Fact]
    public void Defaults_routing_keys_and_accept_to_empty_when_absent_from_the_map()
    {
        var endpoint = ServiceEndpointValue.FromMap(MakeMap(("uri", "https://only-uri/")));

        var result = ServiceEndpointParser.Parse(new[] { MakeService(endpoint) });

        result[0].RoutingKeys.Should().BeEmpty();
        result[0].Accept.Should().BeEmpty();
    }

    [Fact]
    public void Bare_string_endpoint_is_dropped_by_default_DD10_tolerance_off()
    {
        var endpoint = ServiceEndpointValue.FromUri("https://example.com/legacy-shape");

        var result = ServiceEndpointParser.Parse(new[] { MakeService(endpoint) });

        result.Should().BeEmpty("DD-10 bare-string is non-canonical; off by default");
    }

    [Fact]
    public void Bare_string_endpoint_is_surfaced_when_DD10_tolerance_is_enabled()
    {
        var endpoint = ServiceEndpointValue.FromUri("https://example.com/legacy-shape");

        var result = ServiceEndpointParser.Parse(
            new[] { MakeService(endpoint) },
            allowBareStringServiceEndpoint: true);

        result.Should().ContainSingle();
        result[0].Uri.Should().Be("https://example.com/legacy-shape");
        result[0].RoutingKeys.Should().BeEmpty();
        result[0].Accept.Should().BeEmpty();
    }

    [Fact]
    public void Set_form_flattens_a_mix_of_uri_and_map_entries_when_DD10_is_on()
    {
        var uriEntry = ServiceEndpointValue.FromUri("https://legacy/");
        var mapEntry = ServiceEndpointValue.FromMap(MakeMap(("uri", "https://canonical/")));
        var set = ServiceEndpointValue.FromSet(new[] { uriEntry, mapEntry });

        var result = ServiceEndpointParser.Parse(
            new[] { MakeService(set) },
            allowBareStringServiceEndpoint: true);

        result.Should().HaveCount(2);
        result[0].Uri.Should().Be("https://legacy/");
        result[1].Uri.Should().Be("https://canonical/");
    }

    [Fact]
    public void Routing_keys_array_filters_out_non_string_entries()
    {
        var endpoint = ServiceEndpointValue.FromMap(MakeMap(
            ("uri", "https://example/"),
            ("routingKeys", JsonElement(@"[""did:example:m#a"", 42, null, """", ""did:example:m#b""]"))));

        var result = ServiceEndpointParser.Parse(new[] { MakeService(endpoint) });

        result[0].RoutingKeys.Should().BeEquivalentTo(new[] { "did:example:m#a", "did:example:m#b" });
    }

    [Fact]
    public void Multiple_DIDCommMessaging_services_emit_one_info_per_object()
    {
        var alpha = MakeService(ServiceEndpointValue.FromMap(MakeMap(("uri", "https://alpha/"))));
        var beta = MakeService(ServiceEndpointValue.FromMap(MakeMap(("uri", "https://beta/"))));

        var result = ServiceEndpointParser.Parse(new[] { alpha, beta });

        result.Select(r => r.Uri).Should().Equal("https://alpha/", "https://beta/");
    }

    private static Service MakeService(ServiceEndpointValue endpoint, string type = "DIDCommMessaging") =>
        new() { Id = "did:example:x#svc-1", Type = type, ServiceEndpoint = endpoint };

    private static IReadOnlyDictionary<string, JsonElement> MakeMap(params (string Key, object Value)[] entries)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            map[key] = value switch
            {
                string s => JsonElement($"\"{s}\""),
                JsonElement je => je,
                _ => throw new ArgumentException("Use string or JsonElement; other value types not modeled.", nameof(entries)),
            };
        }
        return map;
    }

    private static JsonElement JsonElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
