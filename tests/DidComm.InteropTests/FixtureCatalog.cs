using System.Text.Json;

namespace DidComm.InteropTests;

/// <summary>
/// Discovers all fixture manifests under <c>fixtures/manifest/**/*.json</c> at test-host
/// startup. The <see cref="DidComm.InteropTests.csproj"/> copies the entire <c>fixtures/</c>
/// directory to the test output via the <c>None Include="fixtures\**\*"</c> item group, so
/// the catalog resolves paths relative to the running test assembly.
/// </summary>
internal static class FixtureCatalog
{
    private const string FixturesDirectoryName = "fixtures";
    private const string ManifestSubdirectory = "manifest";

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>Returns the absolute path to the <c>fixtures/</c> root.</summary>
    public static string FixturesRoot
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var root = Path.Combine(baseDir, FixturesDirectoryName);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException(
                    $"Fixtures directory not found at '{root}'. " +
                    "The DidComm.InteropTests.csproj 'None Include=\"fixtures\\**\\*\"' item group must copy fixtures to the test output.");
            }
            return root;
        }
    }

    /// <summary>Enumerates every manifest JSON file under <c>fixtures/manifest/**/*.json</c>.</summary>
    public static IEnumerable<string> EnumerateManifestPaths()
    {
        var manifestDir = Path.Combine(FixturesRoot, ManifestSubdirectory);
        if (!Directory.Exists(manifestDir))
            return [];

        return Directory.EnumerateFiles(manifestDir, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    /// <summary>Load and deserialize one manifest by absolute path.</summary>
    public static FixtureManifest Load(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(stream, ManifestSerializerOptions)
            ?? throw new InvalidDataException($"Fixture manifest at '{manifestPath}' deserialized to null.");
        return manifest;
    }
}
