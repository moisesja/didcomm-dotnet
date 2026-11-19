using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.InteropTests.DxCoverage;
using Xunit;

namespace DidComm.InteropTests.InteropLive;

/// <summary>
/// The package boundary of the mediator leg's wallet (<c>tools/interop-live/mediator/MediatorWallet</c>,
/// #98, E-36), checked on every PR: what the leg proves against the Identus mediator is proven by a
/// wallet that references the client package and the transports only, never the mediator side or
/// ASP.NET Core. The wallet is not referenced by this project (that would bring it into this
/// project's graph); its own restore graph and build output are read instead, as
/// <see cref="Samples.MediatedAgentSmokeTests"/> reads sample 11's wallet. The solution build
/// restores and builds it before the tests run.
/// </summary>
public sealed class MediatorWalletBoundaryTests
{
    /// <summary>Library names (restore graph and deps.json) that must not be anywhere in the wallet's graph.</summary>
    private static readonly string[] ForbiddenLibraries = ["DidComm.Mediator", "DidComm.Mediator.AspNetCore", "DidComm.AspNetCore"];

    private static string WalletDirectory()
        => Path.Combine(ShippedProjects.FindRepoRoot(), "tools", "interop-live", "mediator", "MediatorWallet");

    /// <summary>Every build of the wallet present (bin/&lt;configuration&gt;/net10.0); at least one, or the solution was not built.</summary>
    private static IReadOnlyList<string> BuildOutputs()
    {
        var bin = Path.Combine(WalletDirectory(), "bin");
        return Directory.Exists(bin)
            ? Directory.GetDirectories(bin).Select(c => Path.Combine(c, "net10.0")).Where(d => File.Exists(Path.Combine(d, "MediatorWallet.dll"))).ToList()
            : [];
    }

    private static bool IsForbiddenLibrary(string name)
        => ForbiddenLibraries.Any(f => string.Equals(name, f, StringComparison.OrdinalIgnoreCase))
           || name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Restore_graph_holds_the_client_package_and_nothing_from_the_mediator_side_or_AspNetCore()
    {
        var assetsPath = Path.Combine(WalletDirectory(), "obj", "project.assets.json");
        File.Exists(assetsPath).Should().BeTrue("the solution restore restores the wallet project before the tests are built");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var root = assets.RootElement;

        // Every package and project in the graph, direct or transitive ("Name/Version").
        var libraries = root.GetProperty("libraries").EnumerateObject().Select(p => p.Name.Split('/')[0]).ToList();
        libraries.Should().Contain("DidComm.Mediator.Client", "positive control: the wallet depends on the client package");
        libraries.Should().Contain("DidComm.Transports.Http", "positive control: and on the HTTP transport");
        libraries.Should().NotContain(name => IsForbiddenLibrary(name));

        // Shared frameworks: the project's own, and those any project or package in the graph brings.
        var frameworks = new List<string>();
        foreach (var framework in root.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            if (framework.Value.TryGetProperty("frameworkReferences", out var own))
                frameworks.AddRange(own.EnumerateObject().Select(f => f.Name));
        }
        foreach (var target in root.GetProperty("targets").EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                if (library.Value.TryGetProperty("frameworkReferences", out var brought) && brought.ValueKind == JsonValueKind.Array)
                    frameworks.AddRange(brought.EnumerateArray().Select(f => f.GetString() ?? string.Empty));
            }
        }
        frameworks.Should().Contain("Microsoft.NETCore.App", "positive control: the framework section was read");
        frameworks.Should().NotContain(name => name.StartsWith("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_output_runs_on_NETCore_only_and_ships_nothing_from_the_mediator_side()
    {
        var outputs = BuildOutputs();
        outputs.Should().NotBeEmpty("the solution build builds the wallet before the tests run");

        foreach (var output in outputs)
        {
            // runtimeconfig.json: the shared frameworks the wallet starts with.
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "MediatorWallet.runtimeconfig.json")));
            var options = config.RootElement.GetProperty("runtimeOptions");
            var frameworks = new List<string>();
            if (options.TryGetProperty("framework", out var single))
                frameworks.Add(single.GetProperty("name").GetString()!);
            if (options.TryGetProperty("frameworks", out var many))
                frameworks.AddRange(many.EnumerateArray().Select(f => f.GetProperty("name").GetString()!));
            frameworks.Should().Equal(new[] { "Microsoft.NETCore.App" }, "the wallet must run without ASP.NET Core ({0})", output);

            // deps.json: every library it ships with.
            using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "MediatorWallet.deps.json")));
            var shipped = deps.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name.Split('/')[0]).ToList();
            shipped.Should().Contain("DidComm.Mediator.Client", "positive control ({0})", output);
            shipped.Should().NotContain(name => IsForbiddenLibrary(name));

            // The compiled references, read from the assembly's metadata without loading it.
            var references = ReadAssemblyReferences(Path.Combine(output, "MediatorWallet.dll"));
            references.Should().Contain("DidComm.Mediator.Client", "positive control: the wallet calls the client package ({0})", output);
            references.Should().NotContain(name => IsForbiddenLibrary(name));
        }
    }

    private static List<string> ReadAssemblyReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToList();
    }
}
