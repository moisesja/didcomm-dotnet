using System.Globalization;

namespace MediatorWallet;

/// <summary>A command line that does not parse, or a missing or unknown option value: exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>A precondition the cell depends on does not hold (<c>--expect-env-proxy</c>): exit code 3.</summary>
internal sealed class PreconditionException(string message) : Exception(message);

/// <summary>What the mediator did differs from what the cell expects: exit code 1.</summary>
internal sealed class ExpectationException(string message) : Exception(message);

/// <summary>
/// The options of one command: <c>--name value</c> pairs, plus the value-less flags
/// <c>--explicit-ack</c> and <c>--relax-created-time</c>.
/// </summary>
internal sealed class CommandLine
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "explicit-ack", "relax-created-time" };

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private CommandLine()
    {
    }

    public static CommandLine Parse(IEnumerable<string> args)
    {
        var line = new CommandLine();
        string? pending = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null)
                    throw new UsageException($"Option '--{pending}' is missing a value.");
                var name = arg[2..];
                if (Flags.Contains(name))
                    line._values[name] = "true";
                else
                    pending = name;
            }
            else if (pending is not null)
            {
                line._values[pending] = arg;
                pending = null;
            }
            else
            {
                throw new UsageException($"Unexpected argument '{arg}'.");
            }
        }

        if (pending is not null)
            throw new UsageException($"Option '--{pending}' is missing a value.");
        return line;
    }

    public string Require(string name)
        => _values.TryGetValue(name, out var value) ? value : throw new UsageException($"Missing required option '--{name}'.");

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public bool Flag(string name) => _values.ContainsKey(name);

    public int Int(string name, int fallback)
    {
        if (!_values.TryGetValue(name, out var value))
            return fallback;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new UsageException($"Option '--{name}' needs a whole number, not '{value}'.");
    }

    public bool Bool(string name, bool fallback) => Get(name) switch
    {
        null => fallback,
        "true" => true,
        "false" => false,
        var other => throw new UsageException($"Option '--{name}' is true or false, not '{other}'."),
    };
}
