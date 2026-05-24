using System.IO;

namespace DidComm.Samples.Shared;

/// <summary>
/// Tiny console formatter the cookbook uses to keep its output legible: section banners,
/// step labels, key/value rows, and inline notes. All writes go through an injectable
/// <see cref="TextWriter"/> so the smoke test can capture output without spawning a process.
/// </summary>
public sealed class Narrator
{
    private readonly TextWriter _writer;

    /// <summary>Initialize with the default console writer.</summary>
    public Narrator() : this(Console.Out) { }

    /// <summary>Initialize with a custom writer (tests).</summary>
    /// <param name="writer">Destination for narrator output.</param>
    public Narrator(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <summary>Print a section banner — a blank line, then the section identifier and title.</summary>
    /// <param name="letter">Section identifier (e.g. "K"). The cookbook re-uses the PRD §14.2 letters for traceability.</param>
    /// <param name="title">Plain-English title shown to the reader (e.g. "Unpack and inspect metadata").</param>
    public void Section(string letter, string title)
    {
        _writer.WriteLine();
        _writer.WriteLine($"== Section {letter} — {title} ==");
    }

    /// <summary>Sub-step label.</summary>
    /// <param name="text">Step description.</param>
    public void Step(string text) => _writer.WriteLine($"  • {text}");

    /// <summary>Key/value report line.</summary>
    /// <param name="key">Label.</param>
    /// <param name="value">Value to render.</param>
    public void Value(string key, object? value) => _writer.WriteLine($"    {key} = {value ?? "<null>"}");

    /// <summary>Inline note (e.g. exception explanation).</summary>
    /// <param name="text">Note text.</param>
    public void Note(string text) => _writer.WriteLine($"    note: {text}");
}
