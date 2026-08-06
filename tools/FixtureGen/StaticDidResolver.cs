using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Core.Serialization;

namespace FixtureGen;

/// <summary>
/// In-memory <see cref="IDidResolver"/> seeded from a directory of Appendix-B DID Document
/// JSON files (the fixtures' <c>diddocs/spec/</c>). Same shape as the InteropTests
/// <c>FixtureDidResolver</c>; duplicated here so the generator does not reference a test
/// assembly for resolution.
/// </summary>
internal sealed class StaticDidResolver : IDidResolver
{
    private readonly IReadOnlyDictionary<string, DidDocument> _docs;

    private StaticDidResolver(IReadOnlyDictionary<string, DidDocument> docs) => _docs = docs;

    /// <summary>Load every <c>*.json</c> file under <paramref name="directory"/> as a DID Document.</summary>
    public static StaticDidResolver LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"DID Document fixtures directory not found: {directory}");

        var map = new Dictionary<string, DidDocument>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var doc = DidDocumentSerializer.Deserialize(File.ReadAllText(file))
                ?? throw new InvalidOperationException($"DID Document at '{file}' deserialised to null.");
            var subject = doc.Id.Value
                ?? throw new InvalidOperationException($"DID Document at '{file}' is missing its 'id' subject.");
            map[subject] = doc;
        }

        return new StaticDidResolver(map);
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
