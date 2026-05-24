using System.IO;

namespace DidComm.Samples.Shared;

/// <summary>
/// Console narrator used by the cookbook so each sample section prints a labeled banner +
/// key=value frames the user can compare to the section's README expected output (FR-DX-03).
/// Writes go through an injectable <see cref="TextWriter"/> so smoke tests can capture output.
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

    /// <summary>Section banner — letter + title.</summary>
    /// <param name="letter">PRD §14.2 section letter (e.g. "K").</param>
    /// <param name="title">Human title (e.g. "Unpack and inspect metadata").</param>
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
