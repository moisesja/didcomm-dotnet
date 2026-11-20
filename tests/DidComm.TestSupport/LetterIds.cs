using DidComm.Messages;

namespace DidComm.TestSupport;

/// <summary>
/// Message ids for the letter-case tests. A random UUID v4 can be made of digits and hyphens only
/// (for example <c>12345678-1234-4123-8123-123456789012</c>), which upper-casing leaves unchanged,
/// so a test that re-cases an id must start from one that is certain to hold letters. Each id is
/// lowercase, unreserved, unique within its generator, contains a hyphen, and holds the hex letters
/// <c>a</c>–<c>f</c> that the "another letter" controls vary.
/// </summary>
public sealed class LetterIds : IMessageIdGenerator
{
    private readonly string _prefix;
    private int _next;

    /// <summary>Create a generator whose ids start with <paramref name="prefix"/>.</summary>
    /// <param name="prefix">A lowercase prefix that names the id's role in the test.</param>
    public LetterIds(string prefix = "case-id") => _prefix = prefix;

    /// <summary>The first id this generator hands out.</summary>
    public string First => Format(1);

    /// <summary>The id with sequence number <paramref name="n"/> (1-based) from a generator with <paramref name="prefix"/>.</summary>
    /// <param name="prefix">The generator's prefix.</param>
    /// <param name="n">The sequence number.</param>
    public static string Id(string prefix, int n = 1) => $"{prefix}-abcdef-{n:D4}";

    /// <inheritdoc />
    public string NewId() => Format(Interlocked.Increment(ref _next));

    private string Format(int n) => Id(_prefix, n);
}
