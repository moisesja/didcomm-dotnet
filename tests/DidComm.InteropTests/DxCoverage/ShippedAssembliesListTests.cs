using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// Two packaging facts that must not change silently.
/// <list type="number">
/// <item>The DX gate measures exactly the assemblies we ship: <see cref="CoverageComputation.ShippedAssemblies"/>
/// equals the set of packable <c>src/*/*.csproj</c> projects. A new package that is not on the list would
/// otherwise ship with an unmeasured public API, and nothing would fail.</item>
/// <item>The internals-coupled edges between shipped packages are a pinned list. Each
/// <c>InternalsVisibleTo</c> grant between two shipped projects makes the grantee's packed dependency on the
/// granter an exact version (Directory.Build.targets), which limits the versions a consumer can mix, so a new
/// grant has to be a deliberate change.</item>
/// </list>
/// Reading <see cref="CoverageComputation.ShippedAssemblies"/> does not start the gate's Cecil scan
/// (<see cref="CoverageComputation.Instance"/> is lazy), so these tests stay cheap. Each comparison has a
/// negative control that feeds the same helper altered data; no file is edited.
/// </summary>
public sealed class ShippedAssembliesListTests
{
    /// <summary>
    /// <c>grantee -> granter</c>: the grantee calls internals of the granter, so its package depends on the
    /// granter's at an exact version. <c>DidComm.Adapters.NetDid</c> is not here: it uses only Core's public API.
    /// </summary>
    private static readonly string[] PinnedFriendEdges =
    [
        "DidComm.AspNetCore -> DidComm.Core",
        "DidComm.Extensions.DependencyInjection -> DidComm.Core",
        "DidComm.Mediator -> DidComm.Core",
        "DidComm.Mediator -> DidComm.Mediator.Client",
        "DidComm.Mediator.AspNetCore -> DidComm.AspNetCore",
        "DidComm.Mediator.AspNetCore -> DidComm.Mediator",
        "DidComm.Mediator.Client -> DidComm.Core",
        "DidComm.Transports.WebSocket -> DidComm.Core",
    ];

    /// <summary>An assembly-level <c>[assembly: InternalsVisibleTo(…)]</c> in source, which the pack-time pin cannot see.</summary>
    private static readonly Regex AttributeGrant = new(
        @"\[\s*assembly\s*:\s*(?:global::)?(?:System\.Runtime\.CompilerServices\.)?InternalsVisibleTo(?:Attribute)?\s*\(",
        RegexOptions.CultureInvariant);

    private static readonly Lazy<IReadOnlyList<ShippedProjects.SrcProject>> Projects =
        new(() => ShippedProjects.Load(ShippedProjects.FindRepoRoot()));

    private static List<string> PackableAssemblies() =>
        Projects.Value.Where(p => p.IsPackable).Select(p => p.AssemblyName).ToList();

    [Fact]
    public void ShippedAssemblies_EqualsThePackableSrcProjects()
    {
        var packable = PackableAssemblies();
        packable.Should().Contain("DidComm.Core", "the scan must find the src projects, or the comparison below proves nothing");

        ShippedProjects.GateListProblems(packable, CoverageComputation.ShippedAssemblies).Should().BeEmpty(
            "the DX gate must measure every package we ship and nothing else");
    }

    [Fact]
    public void GateListComparison_NamesAPackableProjectMissingFromTheList_AndAListedNameThatDoesNotShip()
    {
        // A new packable project nobody added to the gate's list.
        var withNewPackage = PackableAssemblies().Append("DidComm.Protocols.Example");
        ShippedProjects.GateListProblems(withNewPackage, CoverageComputation.ShippedAssemblies)
            .Should().ContainSingle()
            .Which.Should().StartWith("DidComm.Protocols.Example:").And.Contain("does not measure it");

        // The reverse: a listed assembly whose project stopped packing.
        var withoutMediator = PackableAssemblies().Where(name => name != "DidComm.Mediator");
        ShippedProjects.GateListProblems(withoutMediator, CoverageComputation.ShippedAssemblies)
            .Should().ContainSingle()
            .Which.Should().StartWith("DidComm.Mediator:").And.Contain("no packable");
    }

    [Fact]
    public void Parse_ReadsIsPackableAssemblyNameGrantsAndReferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "src", "DidComm.Example", "DidComm.Example.csproj");

        var notPackable = ShippedProjects.Parse(path, XDocument.Parse(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Other.Name</AssemblyName><IsPackable>False</IsPackable></PropertyGroup></Project>"));
        notPackable.IsPackable.Should().BeFalse();
        notPackable.AssemblyName.Should().Be("Other.Name");

        var packable = ShippedProjects.Parse(path, XDocument.Parse(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + "<ProjectReference Include=\"..\\DidComm.Core\\DidComm.Core.csproj\" />"
            + "<InternalsVisibleTo Include=\"DidComm.Example.Tests\" /><InternalsVisibleTo Include=\"DidComm.Other\" />"
            + "</ItemGroup></Project>"));
        packable.IsPackable.Should().BeTrue("a project is packable unless it says IsPackable=false");
        packable.AssemblyName.Should().Be("DidComm.Example", "without <AssemblyName> the SDK uses the project file name");
        packable.FriendAssemblies.Should().Equal("DidComm.Example.Tests", "DidComm.Other");
        packable.ReferencedProjectFiles.Should().Equal(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "src", "DidComm.Core", "DidComm.Core.csproj")));

        // A strong-named grant names the friend before the first comma; the pack-time pin reads it the
        // same way, so it is an edge like any other.
        var strongNamed = ShippedProjects.Parse(path, XDocument.Parse(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + "<InternalsVisibleTo Include=\"DidComm.Strong, PublicKey=0024000004800000940000000602000000240000\" />"
            + "<InternalsVisibleTo Include=\"DidComm.Spaced ,PublicKey=00\" />"
            + "</ItemGroup></Project>"));
        strongNamed.FriendAssemblies.Should().Equal("DidComm.Strong", "DidComm.Spaced");
    }

    [Fact]
    public void FriendEdges_BetweenShippedProjects_AreThePinnedList()
    {
        ShippedProjects.FriendEdgeProblems(ShippedProjects.FriendEdges(Projects.Value), PinnedFriendEdges).Should().BeEmpty(
            "every InternalsVisibleTo grant between shipped projects becomes an exact-version package dependency; change this list only on purpose");
    }

    [Fact]
    public void FriendEdges_AreAllDirectProjectReferences_SoPackPinsThem()
    {
        ShippedProjects.FriendEdgesWithoutDirectReference(Projects.Value).Should().BeEmpty();
    }

    [Fact]
    public void SrcCode_DeclaresNoInternalsVisibleToAttribute_SoTheCsprojGrantsAreComplete()
    {
        // The pack-time pin reads the <InternalsVisibleTo> items. A grant written as an attribute in code
        // would bypass it (and the edge list above), so every grant must be a csproj item.
        var srcRoot = Path.Combine(ShippedProjects.FindRepoRoot(), "src");
        var separator = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Where(f => AttributeGrant.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(srcRoot, f))
            .ToList();

        offenders.Should().BeEmpty("declare grants as <InternalsVisibleTo Include=\"…\" /> in the .csproj");
    }

    [Fact]
    public void FriendEdgeChecks_NameANewGrant_ARemovedGrant_AndAGrantWithoutADirectReference()
    {
        static IReadOnlyList<ShippedProjects.SrcProject> WithGrants(
            IReadOnlyList<ShippedProjects.SrcProject> projects, string granter, Func<IReadOnlyList<string>, IEnumerable<string>> change) =>
            projects.Select(p => p.AssemblyName == granter ? p with { FriendAssemblies = change(p.FriendAssemblies).ToList() } : p).ToList();

        var projects = Projects.Value;

        // Core lets Transports.Http (which references Core) and a test assembly use its internals: only the shipped grantee is a new edge.
        var newGrant = WithGrants(projects, "DidComm.Core", grants => grants.Append("DidComm.Transports.Http").Append("DidComm.Example.Tests"));
        ShippedProjects.FriendEdgeProblems(ShippedProjects.FriendEdges(newGrant), PinnedFriendEdges)
            .Should().ContainSingle()
            .Which.Should().StartWith("DidComm.Transports.Http -> DidComm.Core:").And.Contain("new InternalsVisibleTo grant");

        // Core stops granting Transports.WebSocket: the pinned edge is reported as gone.
        var removedGrant = WithGrants(projects, "DidComm.Core", grants => grants.Where(g => g != "DidComm.Transports.WebSocket"));
        ShippedProjects.FriendEdgeProblems(ShippedProjects.FriendEdges(removedGrant), PinnedFriendEdges)
            .Should().ContainSingle()
            .Which.Should().StartWith("DidComm.Transports.WebSocket -> DidComm.Core:").And.Contain("no grant exists");

        // Mediator.Client grants Transports.Http, which does not reference it: pack could not pin that dependency.
        var indirect = WithGrants(projects, "DidComm.Mediator.Client", grants => grants.Append("DidComm.Transports.Http"));
        ShippedProjects.FriendEdgesWithoutDirectReference(indirect)
            .Should().ContainSingle()
            .Which.Should().StartWith("DidComm.Transports.Http -> DidComm.Mediator.Client:");

        // The attribute scan recognises the spellings a grant in code can take.
        AttributeGrant.IsMatch("[assembly: InternalsVisibleTo(\"X\")]").Should().BeTrue();
        AttributeGrant.IsMatch("[assembly:System.Runtime.CompilerServices.InternalsVisibleToAttribute(\"X\")]").Should().BeTrue();
        AttributeGrant.IsMatch("// reaches them through InternalsVisibleTo, not reflection").Should().BeFalse();
    }
}
