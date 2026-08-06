using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// The execution half of the FR-DX-01 gate: every execution-measurable public member of the six
/// shipped packages must genuinely <b>run</b> while the samples execute — measured by re-running
/// all sample programs in-process against probe-instrumented copies of the shipped assemblies
/// (see <see cref="ExecutionCoverage"/> for the mechanism and for why Coverlet-style collectors
/// cannot power an in-run gate). The static metadata-reference half lives in
/// <see cref="PublicApiCoverageTests"/>; together they support the claim "referenced by sample
/// code, and exercised at runtime when the samples run".
///
/// <para>The gate reports a three-way split — <b>executed</b> / <b>reference-gate-only</b>
/// (fields and member-less types, where execution is not an observable concept) /
/// <b>execution-allowlisted</b> (justified, in <c>execution-allowlist.json</c>) — and fails on
/// any measurable member outside all three.</para>
/// </summary>
public sealed class PublicApiExecutionTests
{
    private readonly ITestOutputHelper _output;

    public PublicApiExecutionTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// FR-DX-01 (execution half): 0 execution-measurable public members may go unexecuted by the
    /// sample runs, minus the justified execution allowlist. On failure the gap list is printed
    /// assembly-qualified and sorted — each entry needs a real sample demonstration or (last
    /// resort) an allowlist entry with a one-line justification.
    /// </summary>
    [Fact]
    public void EveryExecutableMember_IsExecutedByASampleRun()
    {
        var execution = ExecutionCoverage.Instance;
        var surface = CoverageComputation.Instance.Surface;
        var allowlist = LoadAllowlist();

        // Allowlist hygiene: entries must name real, execution-measurable, still-unexecuted members.
        var measurableIds = execution.Executed.Concat(execution.NotExecuted)
            .Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var notExecutedIds = execution.NotExecuted.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (member, reason) in allowlist)
        {
            reason.Should().NotBeNullOrWhiteSpace($"execution-allowlist entry '{member}' needs a one-line justification");
            measurableIds.Should().Contain(member,
                $"execution-allowlist entry '{member}' no longer names an execution-measurable public member — delete the stale entry");
            notExecutedIds.Should().Contain(member,
                $"execution-allowlist entry '{member}' now executes during the sample runs — delete the redundant entry");
        }

        var gaps = execution.NotExecuted.Where(m => !allowlist.ContainsKey(m.Id)).ToList();

        _output.WriteLine($"Public surface scanned              : {surface.Count} members across {CoverageComputation.ShippedAssemblies.Count} assemblies");
        _output.WriteLine($"Executed during sample runs         : {execution.Executed.Count}");
        _output.WriteLine($"Reference-gate-only (not execution-measurable: fields, member-less types) : {execution.NotMeasurable.Count}");
        _output.WriteLine($"Execution-allowlisted (justified)   : {allowlist.Count}");
        _output.WriteLine($"Execution gaps                      : {gaps.Count}");
        _output.WriteLine($"Samples re-run under instrumentation: {string.Join(", ", execution.SamplesRun)}");
        foreach (var member in execution.NotMeasurable.OrderBy(m => m.Id, StringComparer.Ordinal))
            _output.WriteLine($"  reference-gate-only: [{member.Kind}] {member.Id}");

        execution.SamplesRun.Should().HaveCountGreaterThanOrEqualTo(10,
            "all ten sample programs must run against the instrumented assemblies for the signal to be real");

        if (gaps.Count > 0)
        {
            var report = new StringBuilder();
            report.AppendLine($"{gaps.Count} public member(s) never execute during the sample runs (FR-DX-01, execution half).");
            report.AppendLine("Make a sample genuinely invoke each member, or (last resort) add a justified execution-allowlist entry:");
            foreach (var gap in gaps)
                report.AppendLine($"  [{gap.Kind}] {gap.Id}");
            Assert.Fail(report.ToString());
        }
    }

    private static Dictionary<string, string> LoadAllowlist()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "DxCoverage", "execution-allowlist.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
            entries.Add(entry.GetProperty("member").GetString()!, entry.GetProperty("reason").GetString()!);
        return entries;
    }
}
