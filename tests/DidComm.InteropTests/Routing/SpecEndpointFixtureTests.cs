using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests.Routing;

/// <summary>
/// Phase 4 Checkpoint A — KAT-style anchors for the DIDComm v2.1 §Service Endpoint examples
/// (spec lines 184–203). Pinning the exact JSON the spec ships before any wrapping code runs
/// guards against L-005 (self-round-trip ≠ spec interop). The fixtures live under
/// <c>fixtures/spec/</c> and the InteropTests csproj copies them to the test output.
/// </summary>
public sealed class SpecEndpointFixtureTests
{
    [Fact]
    public void Endpoint_example_1_is_a_single_object_with_a_did_uri_and_no_routing_keys()
    {
        using var doc = LoadFixture("endpoint-example-1.json");

        doc.RootElement.GetProperty("type").GetString().Should().Be("DIDCommMessaging");

        var endpoint = doc.RootElement.GetProperty("serviceEndpoint");
        endpoint.ValueKind.Should().Be(JsonValueKind.Object, "spec endpoint example 1 uses the single-object form");
        endpoint.GetProperty("uri").GetString().Should().Be("did:example:somemediator");
        endpoint.TryGetProperty("routingKeys", out _).Should().BeFalse("example 1 deliberately omits routingKeys");
    }

    [Fact]
    public void Endpoint_example_2_is_a_single_object_with_a_did_uri_and_one_routing_key()
    {
        using var doc = LoadFixture("endpoint-example-2.json");

        var endpoint = doc.RootElement.GetProperty("serviceEndpoint");
        endpoint.ValueKind.Should().Be(JsonValueKind.Object);
        endpoint.GetProperty("uri").GetString().Should().Be("did:example:somemediator");

        var routing = endpoint.GetProperty("routingKeys");
        routing.ValueKind.Should().Be(JsonValueKind.Array);
        routing.EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo(new[]
        {
            "did:example:anothermediator#somekey",
        });
    }

    private static JsonDocument LoadFixture(string name)
    {
        var path = Path.Combine(FixtureCatalog.FixturesRoot, "spec", name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Spec endpoint fixture not found at '{path}'.");
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }
}
