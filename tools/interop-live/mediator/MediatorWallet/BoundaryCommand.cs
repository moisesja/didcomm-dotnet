using System.Text.Json;
using System.Text.Json.Nodes;

namespace MediatorWallet;

/// <summary>
/// <c>boundary --out refs.json [--forbid name,prefix*]</c>: the wallet's compiled references and
/// what it ships with (E-36, cell B-0's cross-check). Lists the assembly's referenced assemblies,
/// the libraries in its <c>deps.json</c> and the shared frameworks in its
/// <c>runtimeconfig.json</c>, and fails when any of them is mediator-side or ASP.NET Core
/// (<c>DidComm.Mediator</c>, <c>DidComm.Mediator.AspNetCore</c>, <c>DidComm.AspNetCore</c>,
/// <c>Microsoft.AspNetCore*</c>, the <c>Microsoft.AspNetCore.App</c> framework) or matches
/// <c>--forbid</c>, or when the client package is missing (positive control).
/// </summary>
internal static class BoundaryCommand
{
    private static readonly string[] Forbidden = ["DidComm.Mediator", "DidComm.Mediator.AspNetCore", "DidComm.AspNetCore", "Microsoft.AspNetCore*"];

    public static Task<int> RunAsync(CommandLine line, CommandResult result)
    {
        var outPath = line.Require("out");
        var forbidden = Forbidden.Concat((line.Get("forbid") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();

        var referenced = typeof(BoundaryCommand).Assembly.GetReferencedAssemblies().Select(a => a.Name!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var baseDir = AppContext.BaseDirectory;
        var depsPath = Path.Combine(baseDir, "MediatorWallet.deps.json");
        var runtimeConfigPath = Path.Combine(baseDir, "MediatorWallet.runtimeconfig.json");
        var libraries = Array.Empty<string>();
        if (File.Exists(depsPath))
        {
            using var deps = JsonDocument.Parse(File.ReadAllText(depsPath));
            libraries = deps.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name.Split('/')[0]).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }
        var frameworks = new List<string>();
        if (File.Exists(runtimeConfigPath))
        {
            using var config = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
            var options = config.RootElement.GetProperty("runtimeOptions");
            if (options.TryGetProperty("framework", out var single))
                frameworks.Add(single.GetProperty("name").GetString()!);
            if (options.TryGetProperty("frameworks", out var many))
                frameworks.AddRange(many.EnumerateArray().Select(f => f.GetProperty("name").GetString()!));
        }

        var refs = new JsonObject
        {
            ["referenced_assemblies"] = new JsonArray(referenced.Select(n => (JsonNode?)n).ToArray()),
            ["deps_libraries"] = new JsonArray(libraries.Select(n => (JsonNode?)n).ToArray()),
            ["frameworks"] = new JsonArray(frameworks.Select(n => (JsonNode?)n).ToArray()),
            ["forbidden"] = new JsonArray(forbidden.Select(n => (JsonNode?)n).ToArray()),
        };
        File.WriteAllText(outPath, refs.ToJsonString(CommandResult.Json) + "\n");
        result.Observed["refs"] = refs.DeepClone();

        result.Check("deps-json-and-runtimeconfig-read", libraries.Length > 0 && frameworks.Count > 0,
            new JsonObject { ["deps_libraries"] = libraries.Length, ["frameworks"] = frameworks.Count });
        result.Check("references-the-client-package", referenced.Contains("DidComm.Mediator.Client") && libraries.Contains("DidComm.Mediator.Client"));
        var hits = referenced.Concat(libraries).Where(n => forbidden.Any(f => Matches(f, n))).Distinct().ToArray();
        result.Check("no-forbidden-reference", hits.Length == 0, new JsonArray(hits.Select(h => (JsonNode?)h).ToArray()));
        result.Check("only-the-netcore-framework", frameworks.SequenceEqual(new[] { "Microsoft.NETCore.App" }),
            new JsonArray(frameworks.Select(f => (JsonNode?)f).ToArray()));
        return Task.FromResult(0);
    }

    private static bool Matches(string pattern, string name)
        => pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
}
