using System.Text.Json.Nodes;
using DidComm.Messages;

namespace DidComm.Protocols.ProblemReport;

/// <summary>
/// Report Problem 2.0 (FR-PROTO-07/08/09) construction-side helpers + spec wire constants.
/// Mirror of what <see cref="ProblemReportHandler"/> reads on receive.
/// </summary>
public static class ProblemReport
{
    /// <summary>Protocol identifier URI for Report Problem 2.0.</summary>
    public const string ProtocolUri = "https://didcomm.org/report-problem/2.0";

    /// <summary>The single message type URI of the protocol.</summary>
    public const string MessageType = "https://didcomm.org/report-problem/2.0/problem-report";

    /// <summary>Spec body member: the problem-code string (FR-PROTO-08).</summary>
    public const string CodeField = "code";

    /// <summary>Spec body member: a human-readable comment with <c>{n}</c> placeholders (FR-PROTO-07).</summary>
    public const string CommentField = "comment";

    /// <summary>Spec body member: positional substitution values for <see cref="CommentField"/>.</summary>
    public const string ArgsField = "args";

    /// <summary>Spec body member: optional URI for escalation (e.g. <c>mailto:</c>).</summary>
    public const string EscalateToField = "escalate_to";

    /// <summary>
    /// The cascade-guard code emitted by <see cref="ProblemReportHandler"/> when a thread's
    /// error count crosses <see cref="ProblemReportOptions.CascadeThreshold"/> (FR-PROTO-10).
    /// </summary>
    public const string MaxErrorsExceededCode = "e.p.req.max-errors-exceeded";

    /// <summary>
    /// Build a Report Problem 2.0 message.
    /// </summary>
    /// <param name="from">Sender DID.</param>
    /// <param name="to">Recipient DID.</param>
    /// <param name="code">The problem-code string (parsed via <see cref="ProblemCode.Parse"/>).</param>
    /// <param name="pthid">REQUIRED parent-thread id — the failing thread's <c>thid</c> (FR-PROTO-07).</param>
    /// <param name="comment">Optional human-readable explanation with <c>{n}</c> placeholders.</param>
    /// <param name="args">Optional positional substitution values for <paramref name="comment"/>.</param>
    /// <param name="escalateTo">Optional URI to escalate to (e.g. <c>mailto:admin@example.com</c>).</param>
    /// <param name="ack">Optional <c>ack</c> list — makes the report also act as an explicit ACK (FR-THR-03).</param>
    /// <exception cref="DidComm.Exceptions.ProtocolException">When <paramref name="code"/> is malformed per FR-PROTO-08.</exception>
    public static Message Create(
        string from,
        string to,
        string code,
        string pthid,
        string? comment = null,
        IReadOnlyList<string>? args = null,
        string? escalateTo = null,
        IReadOnlyList<string>? ack = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(pthid);
        // Validate code shape up front; rather have a bug surface here than land on the wire.
        _ = ProblemCode.Parse(code);

        var body = new JsonObject { [CodeField] = code };
        if (!string.IsNullOrEmpty(comment))
            body[CommentField] = comment;
        if (args is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var a in args) arr.Add(a ?? string.Empty);
            body[ArgsField] = arr;
        }
        if (!string.IsNullOrEmpty(escalateTo))
            body[EscalateToField] = escalateTo;

        var builder = new MessageBuilder()
            .WithType(MessageType)
            .WithFrom(from)
            .WithTo(to)
            .WithPthid(pthid)
            .WithBody(body);

        if (ack is { Count: > 0 })
            builder.WithAck(ack.ToArray());

        return builder.Build();
    }

    /// <summary>
    /// Build an escalation reply per FR-PROTO-09: when a warning <paramref name="originalCode"/>
    /// proves unrecoverable, the responder MAY emit an <c>e.*</c> code with scope ≥ the
    /// original. Generates a new <c>id</c> and threads via the original's <c>thid</c> /
    /// <c>pthid</c>.
    /// </summary>
    /// <param name="from">Sender DID for the escalation message.</param>
    /// <param name="to">Recipient DID — typically the original sender of the warning.</param>
    /// <param name="originalCode">The warning code being escalated (must have sorter <c>w</c>).</param>
    /// <param name="escalatedDescriptor">The new error descriptor (e.g. <c>"xfer.cant-use-endpoint"</c>).</param>
    /// <param name="pthid">The failing thread's <c>thid</c> (carried over from the original warning).</param>
    /// <param name="comment">Optional comment for the escalation message.</param>
    /// <param name="args">Optional args for <paramref name="comment"/>.</param>
    /// <exception cref="ArgumentException">When <paramref name="originalCode"/> is not a warning, or the descriptor is empty.</exception>
    public static Message Escalate(
        string from,
        string to,
        ProblemCode originalCode,
        string escalatedDescriptor,
        string pthid,
        string? comment = null,
        IReadOnlyList<string>? args = null)
    {
        ArgumentNullException.ThrowIfNull(originalCode);
        ArgumentException.ThrowIfNullOrEmpty(escalatedDescriptor);
        if (!originalCode.IsWarning)
            throw new ArgumentException(
                $"Escalation only applies to warnings (sorter='w'), but got '{originalCode}' (sorter='{originalCode.Sorter}'). FR-PROTO-09.",
                nameof(originalCode));
        // Per FR-PROTO-09: the escalated code's scope MUST be ≥ the original. Since this
        // helper preserves the original scope (no narrowing), the predicate is trivially met:
        // we emit `e.<original-scope>.<descriptor>`.
        var newCode = $"e.{originalCode.Scope}.{escalatedDescriptor}";
        return Create(from, to, newCode, pthid, comment, args);
    }

    /// <summary>Read <c>body.code</c>; returns <c>null</c> when the message has no body or no code field, or when the value is not a JSON string.</summary>
    /// <param name="message">A message of type <see cref="MessageType"/>.</param>
    public static string? ReadCode(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Body is null) return null;
        if (!message.Body.TryGetPropertyValue(CodeField, out var node) || node is null) return null;
        // Pattern-match instead of an unconditional AsValue() cast: AsValue() throws
        // InvalidOperationException when the node is a JsonObject/JsonArray. A malformed
        // `body.code = { ... }` must surface as "absent or malformed", not a crash.
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>Read <c>body.comment</c> raw (no interpolation).</summary>
    /// <param name="message">A message of type <see cref="MessageType"/>.</param>
    public static string? ReadComment(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Body is null) return null;
        if (!message.Body.TryGetPropertyValue(CommentField, out var node) || node is null) return null;
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>Read <c>body.args</c>. Null / non-string entries are preserved as empty strings so the 1-based positional indexes used by <see cref="CommentInterpolator"/> stay aligned with the on-wire array.</summary>
    /// <param name="message">A message of type <see cref="MessageType"/>.</param>
    public static IReadOnlyList<string> ReadArgs(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Body is null) return Array.Empty<string>();
        if (!message.Body.TryGetPropertyValue(ArgsField, out var node) || node is not JsonArray arr) return Array.Empty<string>();
        var list = new List<string>(arr.Count);
        foreach (var n in arr)
            list.Add(n is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty);
        return list;
    }

    /// <summary>
    /// Render the human-readable comment with FR-PROTO-07 <c>{n}</c> interpolation against
    /// <c>body.args</c>. Missing args become literal <c>?</c>; extras are appended in a
    /// trailing <c>[extra: …]</c> block.
    /// </summary>
    /// <param name="message">A message of type <see cref="MessageType"/>.</param>
    public static string RenderComment(Message message)
        => CommentInterpolator.Interpolate(ReadComment(message), ReadArgs(message));
}
