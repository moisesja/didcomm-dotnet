using System.IO;
using System.Xml.Linq;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// The library projects under <c>src/</c>, read straight from their <c>.csproj</c> files: which of them
/// pack into a NuGet package, and which assemblies each one lets use its internals
/// (<c>InternalsVisibleTo</c>). Pure parsing and set comparison: no build, no Cecil scan, so the tests
/// that use it stay cheap and can feed it altered data as their negative controls.
/// </summary>
internal static class ShippedProjects
{
    /// <summary>One <c>src/*/*.csproj</c>, as far as packaging is concerned.</summary>
    /// <param name="FullPath">The absolute path of the <c>.csproj</c>.</param>
    /// <param name="AssemblyName">The <c>&lt;AssemblyName&gt;</c>, or else the file name without its extension (the SDK default).</param>
    /// <param name="IsPackable">False only when the project sets <c>&lt;IsPackable&gt;false&lt;/IsPackable&gt;</c>.</param>
    /// <param name="FriendAssemblies">The assemblies named by its <c>&lt;InternalsVisibleTo Include="…" /&gt;</c> items: the name before the first comma, so a strong-named grant (<c>"DidComm.X, PublicKey=…"</c>) counts as <c>DidComm.X</c>, as Directory.Build.targets reads it.</param>
    /// <param name="ReferencedProjectFiles">The absolute paths of the projects it references directly.</param>
    internal sealed record SrcProject(
        string FullPath,
        string AssemblyName,
        bool IsPackable,
        IReadOnlyList<string> FriendAssemblies,
        IReadOnlyList<string> ReferencedProjectFiles);

    /// <summary>The repository root: the first directory above the test output that holds <c>DidComm.sln</c> and <c>src/</c>.</summary>
    internal static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DidComm.sln")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root (DidComm.sln + src/) not found above the test output directory.");
    }

    /// <summary>Every <c>src/*/*.csproj</c> (one directory level, as the repository lays them out), ordered by path.</summary>
    internal static IReadOnlyList<SrcProject> Load(string repoRoot) =>
        Directory.EnumerateDirectories(Path.Combine(repoRoot, "src"))
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly))
            .Order(StringComparer.Ordinal)
            .Select(path => Parse(Path.GetFullPath(path), XDocument.Load(path)))
            .ToList();

    /// <summary>Reads one project file. <paramref name="fullPath"/> resolves its relative <c>ProjectReference</c> paths.</summary>
    internal static SrcProject Parse(string fullPath, XDocument project)
    {
        var elements = project.Descendants().ToList();
        string? Property(string name) => elements.LastOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        IEnumerable<string> Includes(string itemType) => elements
            .Where(e => e.Name.LocalName == itemType)
            .Select(e => e.Attribute("Include")?.Value.Trim())
            .OfType<string>()
            .Where(v => v.Length > 0);

        var directory = Path.GetDirectoryName(fullPath)!;
        var assemblyName = Property("AssemblyName");
        return new SrcProject(
            fullPath,
            string.IsNullOrEmpty(assemblyName) ? Path.GetFileNameWithoutExtension(fullPath) : assemblyName,
            !string.Equals(Property("IsPackable"), "false", StringComparison.OrdinalIgnoreCase),
            Includes("InternalsVisibleTo").Select(FriendAssemblyName).Where(v => v.Length > 0).ToList(),
            Includes("ProjectReference")
                .Select(include => Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar))))
                .ToList());
    }

    /// <summary>
    /// The internals-coupled edges between packable projects, as <c>"grantee -> granter"</c>, sorted: the
    /// grantee calls internals of the granter. Grants to test assemblies are not edges, since tests never ship.
    /// </summary>
    internal static IReadOnlyList<string> FriendEdges(IReadOnlyList<SrcProject> projects)
    {
        var packable = projects.Where(p => p.IsPackable).Select(p => p.AssemblyName).ToHashSet(StringComparer.Ordinal);
        return projects
            .Where(granter => granter.IsPackable)
            .SelectMany(granter => granter.FriendAssemblies.Where(packable.Contains).Select(grantee => Edge(grantee, granter.AssemblyName)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Edges whose grantee does not reference its granter directly. <c>dotnet pack</c> turns only direct
    /// project references into dependencies, and Directory.Build.targets pins exactly those, so such a
    /// grantee would ship with no exact-version dependency on the package whose internals it calls.
    /// </summary>
    internal static IReadOnlyList<string> FriendEdgesWithoutDirectReference(IReadOnlyList<SrcProject> projects)
    {
        var packable = projects.Where(p => p.IsPackable).ToDictionary(p => p.AssemblyName, StringComparer.Ordinal);
        return packable.Values
            .SelectMany(granter => granter.FriendAssemblies
                .Where(packable.ContainsKey)
                .Where(grantee => !packable[grantee].ReferencedProjectFiles.Contains(granter.FullPath, StringComparer.OrdinalIgnoreCase))
                .Select(grantee => $"{Edge(grantee, granter.AssemblyName)}: {grantee} calls internals of {granter.AssemblyName} but does not reference it "
                    + "directly, so its package would not depend on it at an exact version; add the ProjectReference."))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// What differs between the packable projects and the DX gate's assembly list
    /// (<see cref="CoverageComputation.ShippedAssemblies"/>), one line per name; empty when they are equal.
    /// </summary>
    internal static IReadOnlyList<string> GateListProblems(IEnumerable<string> packableAssemblies, IEnumerable<string> gateList) =>
        Differences(
            packableAssemblies,
            gateList,
            name => $"{name}: packs into a NuGet package, but the DX gate does not measure it. Add it to CoverageComputation.ShippedAssemblies, "
                + "or set <IsPackable>false</IsPackable> if it does not ship.",
            name => $"{name}: listed in CoverageComputation.ShippedAssemblies, but no packable src/*/*.csproj builds it. Remove it from the list.");

    /// <summary>What differs between the edges in the project files and the pinned list, one line per edge; empty when they are equal.</summary>
    internal static IReadOnlyList<string> FriendEdgeProblems(IEnumerable<string> actualEdges, IEnumerable<string> pinnedEdges) =>
        Differences(
            actualEdges,
            pinnedEdges,
            edge => $"{edge}: a new InternalsVisibleTo grant between shipped packages. Pack makes this dependency an exact version, so consumers can "
                + "no longer mix these two packages' versions. If that is intended, add the edge to the pinned list and say so in the CHANGELOG.",
            edge => $"{edge}: pinned, but no grant exists any more. Remove it from the pinned list; the dependency goes back to a minimum version "
                + "(say so in the CHANGELOG).");

    private static string Edge(string grantee, string granter) => $"{grantee} -> {granter}";

    // "DidComm.X, PublicKey=…" → "DidComm.X": the text before the first comma, trimmed.
    private static string FriendAssemblyName(string include)
    {
        var comma = include.IndexOf(',', StringComparison.Ordinal);
        return (comma < 0 ? include : include[..comma]).Trim();
    }

    private static List<string> Differences(
        IEnumerable<string> actual, IEnumerable<string> expected, Func<string, string> onlyActual, Func<string, string> onlyExpected)
    {
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        return actualSet.Except(expectedSet).Order(StringComparer.Ordinal).Select(onlyActual)
            .Concat(expectedSet.Except(actualSet).Order(StringComparer.Ordinal).Select(onlyExpected))
            .ToList();
    }
}
