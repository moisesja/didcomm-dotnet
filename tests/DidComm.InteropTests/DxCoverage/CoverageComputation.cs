using System.IO;
using Mono.Cecil;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// The FR-DX-01 coverage computation, run once per test session and shared by the gate and the
/// §14.4 matrix assertions: scans the six shipped assemblies' public surface, collects every
/// reference the sample assemblies make into them, and intersects the two.
/// </summary>
public sealed class CoverageComputation
{
    /// <summary>The six shipped package assemblies whose public surface the gate measures.</summary>
    public static readonly IReadOnlySet<string> ShippedAssemblies = new HashSet<string>(StringComparer.Ordinal)
    {
        "DidComm.Core",
        "DidComm.AspNetCore",
        "DidComm.Transports.Http",
        "DidComm.Transports.WebSocket",
        "DidComm.Extensions.DependencyInjection",
        "DidComm.Adapters.NetDid",
    };

    private static readonly Lazy<CoverageComputation> LazyInstance = new(() => new CoverageComputation());

    /// <summary>The session-wide computation (Cecil scans run once, not per test).</summary>
    public static CoverageComputation Instance => LazyInstance.Value;

    /// <summary>The full demonstrable public surface of the shipped assemblies.</summary>
    public IReadOnlyList<SurfaceMember> Surface { get; }

    /// <summary>Structural-exemption counts accumulated while scanning the surface.</summary>
    public SurfaceExemptions Exemptions { get; } = new();

    /// <summary>Per-sample-assembly reference sets (keyed by assembly simple name).</summary>
    public IReadOnlyDictionary<string, SampleReferences> SampleRefsByAssembly { get; }

    /// <summary>The union of every sample's references.</summary>
    public SampleReferences AllSampleRefs { get; }

    /// <summary>Ids of surface members covered by a direct IL/metadata reference.</summary>
    public IReadOnlySet<string> CoveredDirectly { get; }

    /// <summary>Ids of surface members covered through interface-implementation/override mapping.</summary>
    public IReadOnlySet<string> CoveredViaMapping { get; }

    /// <summary>Surface members no sample demonstrates (before the allowlist is applied).</summary>
    public IReadOnlyList<SurfaceMember> Uncovered { get; }

    private CoverageComputation()
    {
        var baseDir = AppContext.BaseDirectory;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(baseDir);
        var readerParameters = new ReaderParameters { AssemblyResolver = resolver };

        // Side 1: the shipped assemblies' public surface.
        var surface = new List<SurfaceMember>();
        foreach (var assemblyName in ShippedAssemblies.Order(StringComparer.Ordinal))
        {
            using var module = ModuleDefinition.ReadModule(Path.Combine(baseDir, assemblyName + ".dll"), readerParameters);
            surface.AddRange(PublicSurfaceScanner.Scan(module, ShippedAssemblies, Exemptions));
        }

        Surface = surface;

        // Side 2: every reference the sample assemblies (01..10 + _shared) make into them.
        var sampleRefs = new Dictionary<string, SampleReferences>(StringComparer.Ordinal);
        var all = new SampleReferences();
        foreach (var samplePath in Directory.EnumerateFiles(baseDir, "DidComm.Samples.*.dll").Order(StringComparer.Ordinal))
        {
            using var module = ModuleDefinition.ReadModule(samplePath, readerParameters);
            var refs = SampleReferenceCollector.Collect(module, ShippedAssemblies);
            sampleRefs[module.Assembly.Name.Name] = refs;
            all.UnionWith(refs);
        }

        SampleRefsByAssembly = sampleRefs;
        AllSampleRefs = all;

        // Intersect. Direct references first, then propagate coverage from interface members and
        // virtual/abstract declarations to their implementations/overrides until stable.
        var coveredDirectly = new HashSet<string>(StringComparer.Ordinal);
        var coveredViaMapping = new HashSet<string>(StringComparer.Ordinal);
        var methodPool = new HashSet<string>(all.Methods, StringComparer.Ordinal);

        foreach (var member in surface)
        {
            var isCovered = member.Kind switch
            {
                SurfaceKind.Type => all.Types.Contains(member.Id),
                SurfaceKind.Field => all.Fields.Contains(member.Id),
                SurfaceKind.Method => all.Methods.Contains(member.Id),
                SurfaceKind.Property or SurfaceKind.Event => member.AccessorIds.Any(all.Methods.Contains),
                _ => false,
            };
            if (isCovered)
                coveredDirectly.Add(member.Id);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var member in surface)
            {
                if (member.RootIds.Count == 0
                    || coveredDirectly.Contains(member.Id)
                    || coveredViaMapping.Contains(member.Id)
                    || !member.RootIds.Any(methodPool.Contains))
                {
                    continue;
                }

                coveredViaMapping.Add(member.Id);
                changed = true;
                if (member.Kind == SurfaceKind.Method)
                    methodPool.Add(member.Id);
                else
                    methodPool.UnionWith(member.AccessorIds);
            }
        }
        while (changed);

        CoveredDirectly = coveredDirectly;
        CoveredViaMapping = coveredViaMapping;
        Uncovered = surface
            .Where(m => !coveredDirectly.Contains(m.Id) && !coveredViaMapping.Contains(m.Id))
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
    }
}
