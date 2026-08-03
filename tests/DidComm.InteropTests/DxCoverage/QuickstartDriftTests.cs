using System.IO;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// FR-DX-06: the README quickstart cannot drift from compiling code. The README states its C#
/// block is the body of <c>samples/01-Quickstart</c>'s <c>RunAsync</c> minus the trailing
/// <c>return</c> — this test enforces exactly that relationship: every fenced C# block in the
/// README's Quickstart section must appear, whitespace-normalized and in order, as a contiguous
/// run of lines inside <c>Program.cs</c>. Editing either side without the other fails the build.
/// </summary>
public sealed class QuickstartDriftTests
{
    [Fact]
    public void ReadmeQuickstart_MatchesTheCompiledQuickstartSample()
    {
        var repoRoot = FindRepoRoot();
        var readme = File.ReadAllLines(Path.Combine(repoRoot, "README.md"));
        var program = File.ReadAllLines(Path.Combine(repoRoot, "samples", "01-Quickstart", "Program.cs"));

        var snippets = ExtractQuickstartCSharpBlocks(readme);
        snippets.Should().NotBeEmpty("the README's Quickstart section must contain at least one C# block");

        var programLines = program.Select(Normalize).Where(l => l.Length > 0).ToList();
        foreach (var snippet in snippets)
        {
            var snippetLines = snippet.Select(Normalize).Where(l => l.Length > 0).ToList();
            snippetLines.Should().NotBeEmpty();
            ContainsContiguousRun(programLines, snippetLines).Should().BeTrue(
                "every line of the README quickstart C# block must appear, in order and " +
                "contiguously, in samples/01-Quickstart/Program.cs — fix the README to match the " +
                "compiled sample (FR-DX-06). First snippet line: '" + snippetLines[0] + "'");
        }
    }

    /// <summary>The fenced <c>```csharp</c> blocks between the <c>## Quickstart</c> heading and the next <c>## </c> heading.</summary>
    private static List<List<string>> ExtractQuickstartCSharpBlocks(string[] readme)
    {
        var blocks = new List<List<string>>();
        var inQuickstart = false;
        List<string>? current = null;
        foreach (var line in readme)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inQuickstart)
                    break;
                inQuickstart = line.Trim().Equals("## Quickstart", StringComparison.Ordinal);
                continue;
            }

            if (!inQuickstart)
                continue;

            if (current is null && line.TrimEnd().Equals("```csharp", StringComparison.Ordinal))
            {
                current = [];
            }
            else if (current is not null && line.TrimEnd().Equals("```", StringComparison.Ordinal))
            {
                blocks.Add(current);
                current = null;
            }
            else
            {
                current?.Add(line);
            }
        }

        return blocks;
    }

    /// <summary>Trim and collapse internal whitespace runs, so indentation differences don't matter.</summary>
    private static string Normalize(string line)
        => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool ContainsContiguousRun(List<string> haystack, List<string> needle)
    {
        for (var start = 0; start <= haystack.Count - needle.Count; start++)
        {
            var all = true;
            for (var i = 0; i < needle.Count && all; i++)
                all = haystack[start + i] == needle[i];
            if (all)
                return true;
        }

        return false;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DidComm.sln")) && File.Exists(Path.Combine(dir.FullName, "README.md")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root (DidComm.sln + README.md) not found above the test output directory.");
    }
}
