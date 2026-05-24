using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Core.Serialization;

namespace DidComm.InteropTests.Resolution;

/// <summary>
/// In-memory <see cref="IDidResolver"/> seeded from a static dictionary or a directory of JSON
/// DID Documents. Used to drive Phase 3 facade and adapter tests against vendored Appendix B
/// fixtures without standing up a real `did:key` / `did:peer` resolver chain.
/// </summary>
public sealed class FixtureDidResolver : IDidResolver
{
    private readonly IReadOnlyDictionary<string, DidDocument> _docs;

    /// <summary>Initialise with a pre-built map of subject DID → document.</summary>
    /// <param name="docs">Dictionary keyed by the bare DID subject (no fragment).</param>
    public FixtureDidResolver(IReadOnlyDictionary<string, DidDocument> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        _docs = docs;
    }

    /// <summary>Load every <c>*.json</c> file under <paramref name="directory"/> as a DID Document.</summary>
    /// <param name="directory">Path to a folder of W3C DID Document JSON files.</param>
    public static FixtureDidResolver LoadFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"DID Document fixtures directory not found: {directory}");

        var map = new Dictionary<string, DidDocument>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var json = File.ReadAllText(file);
            var doc = DidDocumentSerializer.Deserialize(json)
                ?? throw new InvalidOperationException($"DID Document at '{file}' deserialised to null.");

            var subject = doc.Id.Value
                ?? throw new InvalidOperationException($"DID Document at '{file}' is missing its 'id' subject.");

            map[subject] = doc;
        }

        return new FixtureDidResolver(map);
    }

    /// <inheritdoc />
    public bool CanResolve(string did) => _docs.ContainsKey(did);

    /// <inheritdoc />
    public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
    {
        if (_docs.TryGetValue(did, out var doc))
        {
            return Task.FromResult(new DidResolutionResult
            {
                DidDocument = doc,
                ResolutionMetadata = new DidResolutionMetadata(),
            });
        }

        return Task.FromResult(DidResolutionResult.NotFound(did));
    }
}
