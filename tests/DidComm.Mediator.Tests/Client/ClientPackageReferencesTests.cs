using System.IO;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The package boundary of #113. A wallet references <c>DidComm.Mediator.Client</c> only, so that
/// package must never bring the mediator service, hosting or ASP.NET Core with it; a mediator
/// references <c>DidComm.Mediator</c>, which depends on the client, never the other way round.
/// </summary>
/// <remarks>
/// Two views are pinned, because each misses what the other sees. The client assembly's metadata
/// references show what its code uses; a package that is declared but never used leaves no trace
/// there. The dependency graph in this test host's <c>.deps.json</c> (built from the restore graph)
/// shows every package and project the client declares, used or not. This host runs on
/// <c>Microsoft.NETCore.App</c> only: under ASP.NET Core, .NET 10 prunes the Extensions packages the
/// framework already provides from the graph, and the walk below could no longer see a hosting
/// dependency. A direct project reference from the client to <c>DidComm.Mediator</c> cannot be
/// built at all (the restore fails with MSB4006, a circular dependency); the self-test of the walk
/// stands in for it.
/// </remarks>
public sealed class ClientPackageReferencesTests
{
    private const string ClientNode = "DidComm.Mediator.Client";
    private const string MediatorNode = "DidComm.Mediator";

    private static readonly Assembly ClientAssembly = typeof(MediationClient).Assembly;
    private static readonly Assembly MediatorAssembly = typeof(MediatorOptions).Assembly;

    [Fact]
    public void The_client_assembly_uses_only_core_the_di_package_and_the_extensions_abstractions()
    {
        // Every reference outside the base shared framework counts, a System.* package included.
        var references = ClientAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !IsInTheBaseSharedFramework(n))
            .Order(StringComparer.Ordinal)
            .ToList();

        references.Should().Equal(
            [
                "DidComm.Core",
                "DidComm.Extensions.DependencyInjection",
                "Microsoft.Extensions.DependencyInjection.Abstractions",
                "Microsoft.Extensions.Logging.Abstractions",
                "Microsoft.Extensions.Options",
            ],
            "the client must not use DidComm.Mediator, hosting or ASP.NET Core; a new reference here is a new dependency for every wallet");
    }

    [Fact]
    public void The_shared_types_live_in_the_client_and_the_server_types_stay_in_the_mediator()
    {
        ClientAssembly.GetName().Name.Should().Be(ClientNode);
        MediatorAssembly.GetName().Name.Should().Be(MediatorNode);

        Type[] inClient =
        [
            typeof(MediationClient), typeof(PickupClient), typeof(MediatorClientOptions),
            typeof(MediatorClientBuilderExtensions), typeof(MediationServiceBuilderExtensions),
            typeof(CoordinateMediation), typeof(MediationGrant), typeof(RecipientUpdate), typeof(RecipientUpdateResult),
            typeof(MessagePickup), typeof(PickupStatus), typeof(DeliveredMessage),
        ];
        Type[] inMediator =
        [
            typeof(MediatorOptions), typeof(MediatorBuilderExtensions), typeof(CoordinateMediationHandler),
            typeof(CoordinateMediationOptions), typeof(IMediationStore), typeof(RecipientPage), typeof(RecipientStoreOutcome),
            typeof(MessagePickupHandler), typeof(MessagePickupOptions), typeof(IMessageStore),
        ];

        inClient.Should().AllSatisfy(t => t.Assembly.Should().BeSameAs(ClientAssembly, t.FullName));
        inMediator.Should().AllSatisfy(t => t.Assembly.Should().BeSameAs(MediatorAssembly, t.FullName));
    }

    [Fact]
    public void No_type_is_declared_in_both_assemblies()
    {
        // The compiler embeds its own attribute types in each assembly; only our namespaces count.
        static HashSet<string> OwnTypes(Assembly assembly) => assembly.GetTypes()
            .Select(t => t.FullName!)
            .Where(n => n.StartsWith("DidComm.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var client = OwnTypes(ClientAssembly);
        client.Should().Contain(typeof(MediationClient).FullName!, "the scan must see the client's types");

        client.Intersect(OwnTypes(MediatorAssembly)).Should().BeEmpty(
            "a type declared twice splits its identity: code compiled against one copy cannot use the other");
    }

    [Fact]
    public void The_test_host_runs_on_the_base_framework_only_so_the_graph_walk_can_see_hosting()
    {
        Frameworks().Should().Equal(
            ["Microsoft.NETCore.App"],
            "under ASP.NET Core the Extensions packages are pruned from .deps.json and the walk below would go blind; an ASP.NET Core framework reference that reaches this host may also come from the client itself");
    }

    [Fact]
    public void The_client_package_graph_holds_no_mediator_no_hosting_and_no_asp_net_core()
    {
        var graph = LoadGraph();
        graph.Should().ContainKey(ClientNode);

        var closure = Closure(graph, ClientNode);

        closure.Should().Contain(
            ["DidComm.Core", "DidComm.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions"],
            "the walk must reach the client's own dependencies, or it is not reading the graph");
        closure.Where(IsForbiddenForTheClient).Should().BeEmpty(
            "a wallet that references DidComm.Mediator.Client must not get the mediator service, hosting or ASP.NET Core");

        // What the package declares (its nuspec dependencies), exactly: a new one is a new dependency for every wallet.
        graph[ClientNode].Order(StringComparer.Ordinal).Should().Equal(
            ["DidComm.Core", "DidComm.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions"]);
    }

    [Fact]
    public void The_mediator_package_graph_brings_the_client_and_hosting()
    {
        // The positive control of the walk: the same predicate does see a hosting dependency when
        // there is one, and the dependency points from the mediator to the client.
        var closure = Closure(LoadGraph(), MediatorNode);

        closure.Should().Contain(ClientNode);
        closure.Should().Contain("Microsoft.Extensions.Hosting.Abstractions");
        closure.Where(IsForbiddenForTheClient).Should().NotBeEmpty();
    }

    [Fact]
    public void The_graph_walk_flags_a_client_that_depends_on_the_mediator()
    {
        // The issue's negative control as data: a client node that depends on DidComm.Mediator (which
        // cannot be built as a project reference, because it is a cycle) is caught, directly and
        // through the hosting package behind it.
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [ClientNode] = ["DidComm.Core", MediatorNode],
            [MediatorNode] = ["DidComm.Core", "Microsoft.Extensions.Hosting.Abstractions"],
            ["Microsoft.Extensions.Hosting.Abstractions"] = [],
            ["DidComm.Core"] = [],
        };

        Closure(graph, ClientNode).Where(IsForbiddenForTheClient).Should().BeEquivalentTo(
            [MediatorNode, "Microsoft.Extensions.Hosting.Abstractions"]);

        IsForbiddenForTheClient("DidComm.Mediator.AspNetCore").Should().BeTrue();
        IsForbiddenForTheClient("DidComm.AspNetCore").Should().BeTrue();
        IsForbiddenForTheClient("Microsoft.Extensions.Hosting").Should().BeTrue();
        IsForbiddenForTheClient("Microsoft.AspNetCore.Http.Abstractions").Should().BeTrue();
        IsForbiddenForTheClient(ClientNode).Should().BeFalse();
        IsForbiddenForTheClient("DidComm.Extensions.DependencyInjection").Should().BeFalse();
    }

    /// <summary>Whether the assembly ships in <c>Microsoft.NETCore.App</c>, the framework this host runs on (checked by name in its directory).</summary>
    private static bool IsInTheBaseSharedFramework(string name) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, name + ".dll"));

    private static bool IsForbiddenForTheClient(string name) =>
        name is MediatorNode or "DidComm.Mediator.AspNetCore" or "DidComm.AspNetCore"
        || name.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal);

    private static HashSet<string> Closure(IReadOnlyDictionary<string, IReadOnlyList<string>> graph, string start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!graph.TryGetValue(node, out var dependencies))
                continue;
            foreach (var dependency in dependencies)
            {
                if (seen.Add(dependency))
                    pending.Push(dependency);
            }
        }

        return seen;
    }

    /// <summary>The runtime target of this host's <c>.deps.json</c>: package or project name → the names it depends on.</summary>
    private static Dictionary<string, IReadOnlyList<string>> LoadGraph()
    {
        var path = Path.Combine(AppContext.BaseDirectory, typeof(ClientPackageReferencesTests).Assembly.GetName().Name + ".deps.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var target = root.GetProperty("targets").GetProperty(root.GetProperty("runtimeTarget").GetProperty("name").GetString()!);

        // Keys are "Name/Version"; the version is not pinned (a release builds with its own).
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var library in target.EnumerateObject())
        {
            var name = library.Name.Split('/')[0];
            graph[name] = library.Value.TryGetProperty("dependencies", out var dependencies)
                ? dependencies.EnumerateObject().Select(d => d.Name).ToList()
                : [];
        }

        return graph;
    }

    /// <summary>The shared frameworks this host's <c>.runtimeconfig.json</c> names, in either of its two shapes.</summary>
    private static List<string> Frameworks()
    {
        var path = Path.Combine(AppContext.BaseDirectory, typeof(ClientPackageReferencesTests).Assembly.GetName().Name + ".runtimeconfig.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var options = document.RootElement.GetProperty("runtimeOptions");

        var names = new List<string>();
        if (options.TryGetProperty("framework", out var single))
            names.Add(single.GetProperty("name").GetString()!);
        if (options.TryGetProperty("frameworks", out var several))
            names.AddRange(several.EnumerateArray().Select(f => f.GetProperty("name").GetString()!));
        return names;
    }
}
