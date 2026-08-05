using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// The execution half of the FR-DX-01 computation, run once per test session: every sample program
/// is re-run in-process against probe-instrumented copies of the shipped assemblies, and the
/// recorded hits say which public members genuinely <b>executed</b> — as opposed to the static
/// metadata-reference signal of <see cref="CoverageComputation"/>.
///
/// <para><b>Why not Coverlet / dotnet-coverage?</b> Collector-based coverage materializes its
/// report only after the test host exits, so a test inside the same <c>dotnet test</c> run can
/// never read it — that road needs an external wrapper command plus a skip-when-absent gate.
/// Instrumenting with Mono.Cecil (already a dependency of this project) keeps the whole
/// measurement deterministic inside a single ordinary <c>dotnet test</c>, locally and in CI, with
/// exact member attribution (the probe is IL, so JIT inlining cannot hide a hit).</para>
///
/// <para><b>Isolation.</b> Instrumented copies are written to a fresh temp directory and loaded in
/// a dedicated <see cref="AssemblyLoadContext"/> together with the sample assemblies and
/// <c>DidComm.TestSupport</c> (which exchanges DidComm.Core types with the samples), so the
/// instrumented world is type-consistent and the normal test assemblies are untouched. Framework
/// and third-party assemblies fall through to the default context. The temp directory cannot be
/// deleted while the process holds the loaded files; it is left for the OS temp cleanup.</para>
///
/// <para><b>What a hit means.</b> Executing a method with a body records its own id; executing an
/// implementation/override additionally records the shipped interface/base members it implements
/// (<see cref="PublicSurfaceScanner.RootIdsOf"/>) — a bodyless contract member "executes" exactly
/// when dispatch reaches an implementation during a sample run. Sample-side implementations of
/// shipped contracts record only the contract ids. There is deliberately no propagation in the
/// other direction: a concrete member whose interface was dispatched elsewhere is still
/// not-executed — that inflation is what this gate exists to remove.</para>
/// </summary>
public sealed class ExecutionCoverage
{
    private static readonly Lazy<ExecutionCoverage> LazyInstance = new(
        () => Task.Run(BuildAsync).GetAwaiter().GetResult(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The session-wide computation (instrumentation + sample runs happen once).</summary>
    public static ExecutionCoverage Instance => LazyInstance.Value;

    /// <summary>Canonical method-level ids observed executing during the sample runs.</summary>
    public IReadOnlySet<string> ExecutedIds { get; }

    /// <summary>Surface members proven executed (methods by own hit or implementation dispatch; properties/events through an accessor hit; types through a declared member).</summary>
    public IReadOnlyList<SurfaceMember> Executed { get; }

    /// <summary>Execution-measurable surface members that did not execute (before the allowlist).</summary>
    public IReadOnlyList<SurfaceMember> NotExecuted { get; }

    /// <summary>
    /// Surface members execution cannot see: fields (a consumer's <c>ldsfld/ldfld</c> runs no
    /// member code) and types with no executable member surface (enums, delegates, const-only
    /// holders). These stay guarded by the static reference gate only.
    /// </summary>
    public IReadOnlyList<SurfaceMember> NotMeasurable { get; }

    /// <summary>The sample programs that were re-run against the instrumented assemblies.</summary>
    public IReadOnlyList<string> SamplesRun { get; }

    private ExecutionCoverage(
        IReadOnlySet<string> executedIds,
        IReadOnlyList<SurfaceMember> executed,
        IReadOnlyList<SurfaceMember> notExecuted,
        IReadOnlyList<SurfaceMember> notMeasurable,
        IReadOnlyList<string> samplesRun)
    {
        ExecutedIds = executedIds;
        Executed = executed;
        NotExecuted = notExecuted;
        NotMeasurable = notMeasurable;
        SamplesRun = samplesRun;
    }

    private static async Task<ExecutionCoverage> BuildAsync()
    {
        var baseDir = AppContext.BaseDirectory;
        var surface = CoverageComputation.Instance.Surface;
        var shipped = CoverageComputation.ShippedAssemblies;

        // Every method-level id whose execution the surface cares about: surface methods
        // themselves plus the accessor methods properties/events are counted through.
        var wantedMethodIds = surface
            .Where(m => m.Kind == SurfaceKind.Method)
            .Select(m => m.Id)
            .Concat(surface.SelectMany(m => m.AccessorIds))
            .ToHashSet(StringComparer.Ordinal);

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(baseDir);
        var readerParameters = new ReaderParameters { AssemblyResolver = resolver };

        var instrumentedDir = Directory.CreateTempSubdirectory("didcomm-dx-execution-").FullName;
        var loadPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var hitMaps = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);

        // Shipped assemblies: a method records its own id (when it is surface/accessor) plus the
        // shipped contract members it implements — so internal implementations of public
        // interfaces still light the contract up.
        foreach (var assemblyName in shipped.Order(StringComparer.Ordinal))
        {
            var target = Path.Combine(instrumentedDir, assemblyName + ".dll");
            hitMaps[assemblyName] = ExecutionInstrumenter.Instrument(
                Path.Combine(baseDir, assemblyName + ".dll"),
                target,
                readerParameters,
                method =>
                {
                    var ids = new List<string>(1);
                    var ownId = MemberId.ForMethod(method);
                    if (wantedMethodIds.Contains(ownId))
                        ids.Add(ownId);
                    ids.AddRange(PublicSurfaceScanner.RootIdsOf(method, shipped)
                        .Where(id => id != ownId && wantedMethodIds.Contains(id)));
                    return ids.Distinct(StringComparer.Ordinal).ToArray();
                });
            loadPaths[assemblyName] = target;
        }

        // Sample assemblies (01..10 + _shared): executing a sample-side implementation of a
        // shipped contract member is that contract member's execution.
        var samplePaths = Directory.EnumerateFiles(baseDir, "DidComm.Samples.*.dll")
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var samplePath in samplePaths)
        {
            var assemblyName = Path.GetFileNameWithoutExtension(samplePath);
            var target = Path.Combine(instrumentedDir, assemblyName + ".dll");
            hitMaps[assemblyName] = ExecutionInstrumenter.Instrument(
                samplePath,
                target,
                readerParameters,
                method => PublicSurfaceScanner.RootIdsOf(method, shipped)
                    .Where(wantedMethodIds.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
            loadPaths[assemblyName] = target;
        }

        // TestSupport trades DidComm.Core types with the samples' shared helpers, so it must live
        // in the same load context; it is not sample code, so it carries no probes.
        loadPaths["DidComm.TestSupport"] = Path.Combine(baseDir, "DidComm.TestSupport.dll");

        var context = new ExecutionLoadContext(loadPaths);
        var samplesRun = new List<string>();
        foreach (var samplePath in samplePaths)
        {
            var assemblyName = Path.GetFileNameWithoutExtension(samplePath);
            var assembly = context.LoadFromAssemblyPath(loadPaths[assemblyName]);
            var runAsync = assembly.GetTypes()
                .Where(t => t.IsPublic && t.Name == "Program")
                .Select(t => t.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static))
                .FirstOrDefault(m => m is not null);
            if (runAsync is null)
                continue; // e.g. DidComm.Samples.Shared — helpers, no program

            var arguments = runAsync.GetParameters()
                .Select(parameter => parameter.ParameterType == typeof(TextWriter)
                    ? (object?)TextWriter.Null
                    : throw new InvalidOperationException(
                        $"{assemblyName}.Program.RunAsync has an unsupported parameter '{parameter.Name}' " +
                        $"({parameter.ParameterType}) — teach ExecutionCoverage how to supply it."))
                .ToArray();

            try
            {
                await ((Task)runAsync.Invoke(null, arguments)!).ConfigureAwait(false);
                samplesRun.Add(assemblyName);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Sample '{assemblyName}' failed while running against the instrumented assemblies.",
                    exception);
            }
        }

        // Read every probe back. Loading by path is idempotent within the context, and reflection
        // field access runs the probe's type initializer if nothing in the assembly ever executed.
        var executedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (assemblyName, map) in hitMaps)
        {
            var assembly = context.LoadFromAssemblyPath(loadPaths[assemblyName]);
            var probe = assembly.GetType(ExecutionInstrumenter.ProbeTypeFullName)
                ?? throw new InvalidOperationException($"Probe type missing from instrumented '{assemblyName}'.");
            var hits = (bool[])probe.GetField(ExecutionInstrumenter.ProbeHitsFieldName)!.GetValue(null)!;
            for (var index = 0; index < hits.Length; index++)
            {
                if (hits[index])
                    executedIds.UnionWith(map[index]);
            }
        }

        // Classify the surface. Methods/properties/events are execution-measurable; a type is
        // proven through any declared measurable member; fields and member-less types are not
        // observable by execution and stay under the reference gate.
        var executed = new List<SurfaceMember>();
        var notExecuted = new List<SurfaceMember>();
        var notMeasurable = new List<SurfaceMember>();
        var executedTypeIds = new HashSet<string>(StringComparer.Ordinal);
        var typesWithMeasurableMembers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in surface)
        {
            if (member.Kind is SurfaceKind.Type or SurfaceKind.Field)
                continue;
            typesWithMeasurableMembers.Add(member.DeclaringTypeId);
            if (IsMemberExecuted(member, executedIds))
                executedTypeIds.Add(member.DeclaringTypeId);
        }

        foreach (var member in surface)
        {
            switch (member.Kind)
            {
                case SurfaceKind.Field:
                    notMeasurable.Add(member);
                    break;
                case SurfaceKind.Type when !typesWithMeasurableMembers.Contains(member.Id):
                    notMeasurable.Add(member);
                    break;
                case SurfaceKind.Type:
                    (executedTypeIds.Contains(member.Id) ? executed : notExecuted).Add(member);
                    break;
                default:
                    (IsMemberExecuted(member, executedIds) ? executed : notExecuted).Add(member);
                    break;
            }
        }

        return new ExecutionCoverage(
            executedIds,
            executed,
            notExecuted.OrderBy(m => m.Id, StringComparer.Ordinal).ToList(),
            notMeasurable,
            samplesRun);
    }

    private static bool IsMemberExecuted(SurfaceMember member, HashSet<string> executedIds)
        => member.Kind switch
        {
            SurfaceKind.Method => executedIds.Contains(member.Id),
            SurfaceKind.Property or SurfaceKind.Event => member.AccessorIds.Any(executedIds.Contains),
            _ => false,
        };

    /// <summary>
    /// Loads the instrumented shipped + sample assemblies (plus TestSupport) as one isolated,
    /// type-consistent world; everything else resolves through the default context.
    /// </summary>
    private sealed class ExecutionLoadContext : AssemblyLoadContext
    {
        private readonly IReadOnlyDictionary<string, string> _loadPaths;

        public ExecutionLoadContext(IReadOnlyDictionary<string, string> loadPaths)
            : base("DidComm.DxExecution", isCollectible: false)
            => _loadPaths = loadPaths;

        protected override Assembly? Load(AssemblyName assemblyName)
            => assemblyName.Name is { } name && _loadPaths.TryGetValue(name, out var path)
                ? LoadFromAssemblyPath(path)
                : null;
    }
}
