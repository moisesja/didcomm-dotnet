using DidComm.InteropTests.Runner;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests;

/// <summary>
/// FR-IX-02: the data-driven runner enumerates every <c>manifest/**/*.json</c> fixture and
/// emits exactly one xUnit theory case per manifest. Phase 0 stops at fixture discovery —
/// the actual operation (pack/unpack/sign/verify) is dispatched starting in Phase 2.
/// </summary>
public sealed class FixtureDiscoveryTests
{
    /// <summary>Materializes manifest paths as xUnit theory data. Runs once per discovered file.</summary>
    public static IEnumerable<object[]> AllManifests()
    {
        foreach (var path in FixtureCatalog.EnumerateManifestPaths())
        {
            // Pass relative-from-fixtures-root so xUnit names cases readably
            // ("manifest/spec/_smoke.json") rather than absolute build-machine paths.
            var displayPath = Path.GetRelativePath(FixtureCatalog.FixturesRoot, path);
            yield return [displayPath, path];
        }
    }

    [Fact]
    public void At_least_one_manifest_is_discovered()
    {
        FixtureCatalog.EnumerateManifestPaths()
            .Should().NotBeEmpty(because: "Phase 0 ships a smoke manifest under fixtures/manifest/spec/ so the runner has something to enumerate (FR-IX-02).");
    }

    [Theory]
    [MemberData(nameof(AllManifests))]
    public void Manifest_loads_and_declares_v1_schema(string displayPath, string absolutePath)
    {
        _ = displayPath; // displayPath is for xUnit's case naming; absolutePath is the I/O target.

        var manifest = FixtureCatalog.Load(absolutePath);

        manifest.Schema.Should().Be("didcomm-fixture/v1",
            because: $"every manifest under fixtures/manifest/ MUST declare schema='didcomm-fixture/v1' per PRD §13.4 (file: {displayPath}).");

        manifest.Id.Should().NotBeNullOrWhiteSpace(
            because: $"fixture 'id' is required by the schema and must be stable for failure attribution ({displayPath}).");

        manifest.Source.Should().NotBeNullOrWhiteSpace(
            because: $"fixture 'source' is required by the schema and identifies provenance ({displayPath}).");

        manifest.Direction.Should().BeOneOf(
            new[] { "inbound", "outbound", "roundtrip" },
            because: $"fixture 'direction' must be one of the schema's enumerated values ({displayPath}).");

        manifest.Operation.Should().BeOneOf(
            new[] { "pack-encrypted", "pack-signed", "pack-plaintext", "unpack", "sign", "verify", "noop" },
            because: $"fixture 'operation' must be one of the schema's enumerated values ({displayPath}).");

        manifest.Expected.Outcome.Should().BeOneOf(
            new[] { "success", "error" },
            because: $"fixture 'expected.outcome' must be 'success' or 'error' ({displayPath}).");

        // FR-IX-01: dispatch the manifest's operation. Phase 2 implements unpack/verify;
        // unknown operations short-circuit per FixtureDispatcher.
        FixtureDispatcher.Execute(manifest, absolutePath);
    }
}
