using System.Text.Json.Nodes;
using DidComm.Messages;
// GlobalUsings pulls in System.Threading, whose ThreadState would shadow ours (L-014 family).
using ThreadState = DidComm.Threading.ThreadState;

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
    /// Warning code for "no acceptable language is available" (FR-I18N-04): sorter <c>w</c>
    /// (the protocol may continue, e.g. in a fallback language) + the spec's
    /// <c>msg.bad-lang</c> tail.
    /// </summary>
    public const string BadLangWarningCode = "w.msg.bad-lang";

    /// <summary>
    /// Error code for "no acceptable language is available" (FR-I18N-04): sorter <c>e</c> —
    /// the sender considers the interaction dead without a shared language.
    /// </summary>
    public const string BadLangErrorCode = "e.msg.bad-lang";

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

    /// <summary>
    /// Build a <c>bad-lang</c> Report Problem 2.0 message (FR-I18N-04): the peer asked for
    /// languages this agent cannot produce, so no acceptable language is available. The comment
    /// names the languages that WERE available (via <c>{1}</c> interpolation, FR-PROTO-07) so
    /// the peer can pick one or end the interaction.
    /// </summary>
    /// <param name="from">Sender DID.</param>
    /// <param name="to">Recipient DID — the peer whose <c>accept-lang</c> could not be honored.</param>
    /// <param name="pthid">REQUIRED parent-thread id — the <c>thid</c> of the thread whose language preference failed.</param>
    /// <param name="availableLangs">The IANA language codes this agent CAN produce; surfaced in the comment args. May be empty (no human-readable strings available at all).</param>
    /// <param name="fatal"><c>false</c> (default) emits the warning form <see cref="BadLangWarningCode"/> (<c>w.msg.bad-lang</c> — the protocol may continue in a fallback language); <c>true</c> emits <see cref="BadLangErrorCode"/> (<c>e.msg.bad-lang</c>).</param>
    /// <param name="escalateTo">Optional URI to escalate to (e.g. <c>mailto:support@example.com</c>).</param>
    /// <example>
    /// <code>
    /// var report = ProblemReport.CreateBadLang(
    ///     from: "did:example:bob", to: "did:example:alice",
    ///     pthid: inbound.Thid ?? inbound.Id,
    ///     availableLangs: new[] { "en", "es" });
    /// // report body: { "code": "w.msg.bad-lang", "comment": "...{1}...", "args": ["en, es"] }
    /// </code>
    /// </example>
    public static Message CreateBadLang(
        string from,
        string to,
        string pthid,
        IReadOnlyList<string> availableLangs,
        bool fatal = false,
        string? escalateTo = null)
    {
        ArgumentNullException.ThrowIfNull(availableLangs);
        var offered = availableLangs.Count > 0 ? string.Join(", ", availableLangs) : "none";
        return Create(
            from,
            to,
            fatal ? BadLangErrorCode : BadLangWarningCode,
            pthid,
            comment: "No acceptable language is available on this thread; languages available here: {1}.",
            args: new[] { offered },
            escalateTo: escalateTo);
    }

    /// <summary>
    /// Convenience seam over the FR-I18N-02 thread state: compare the peer's recorded
    /// <see cref="ThreadState.AcceptLang"/> preference against the languages this agent can
    /// produce and, when NONE is acceptable, build the <c>bad-lang</c> report for that thread
    /// (<c>pthid</c> = <see cref="ThreadState.Thid"/>). Returns <c>null</c> when no report is
    /// warranted — the thread declared no preference, or at least one preference is satisfiable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching is deliberately minimal (this is a MAY-level convenience, not a BCP 47
    /// negotiator): a preference is satisfied by an available language when the tags are equal
    /// ignoring case, or when one tag extends the other at a subtag boundary, RFC 4647-style —
    /// basic filtering (the range <c>en</c> matches the tag <c>en-GB</c>) plus lookup-style
    /// truncation (a preference for <c>en-US</c> is satisfied by bare <c>en</c>). Sibling tags
    /// never match: <c>en-US</c> is NOT satisfied by <c>en-GB</c>, nor <c>sr-Cyrl</c> by
    /// <c>sr-Latn</c>. A preference of exactly <c>*</c> — RFC 4647 §2.1's "any language is
    /// acceptable" — is satisfied by any available tag.
    /// </para>
    /// <para>
    /// A wildcard used as a SUBTAG (RFC 4647 §2.2 extended ranges such as <c>en-*</c> or
    /// <c>*-CH</c>) is deliberately NOT interpreted: extended filtering is a real matching
    /// algorithm, and half-implementing it here would be worse than not offering it. Such a
    /// range is treated as ordinary text, so <c>en-*</c> behaves like any other tag extending
    /// <c>en</c>. Callers needing extended filtering should do their own matching and call
    /// <see cref="CreateBadLang"/> directly.
    /// </para>
    /// <para>
    /// Whatever this method calls satisfiable, the caller's reply selector must be able to
    /// actually emit (FR-I18N-02 honors a preference "when a matching language is available"),
    /// so a selector that only truncates (<c>fr-CA</c> → <c>fr</c>) is too narrow: it must also
    /// serve a broad request from a more specific catalog entry (<c>en</c> → <c>en-GB</c>). The
    /// reference selector in the <c>ProfilesAndI18n</c> sample does both.
    /// </para>
    /// </remarks>
    /// <param name="from">Sender DID.</param>
    /// <param name="to">Recipient DID — the peer that declared the unsatisfiable preference.</param>
    /// <param name="thread">The thread whose <c>accept-lang</c> state to check (FR-I18N-02).</param>
    /// <param name="availableLangs">The IANA language codes this agent CAN produce.</param>
    /// <param name="fatal"><c>false</c> for <c>w.msg.bad-lang</c>; <c>true</c> for <c>e.msg.bad-lang</c>.</param>
    public static Message? CreateBadLangForThread(
        string from,
        string to,
        ThreadState thread,
        IReadOnlyList<string> availableLangs,
        bool fatal = false)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(availableLangs);
        if (thread.AcceptLang is not { Count: > 0 } preferences)
            return null; // no preference declared on this thread — nothing to fail
        if (AnyLanguageAcceptable(preferences, availableLangs))
            return null;
        return CreateBadLang(from, to, thread.Thid, availableLangs, fatal);
    }

    /// <summary>RFC 4647 §2.1: the language range that accepts any language.</summary>
    private const string WildcardRange = "*";

    /// <summary>
    /// True when any preferred tag is satisfied by any available tag: the <c>*</c> wildcard, an
    /// exact match, or a directional RFC 4647-style prefix match at a subtag boundary
    /// (case-insensitive, pure string ops — the peer's <c>accept-lang</c> is untrusted remote
    /// input, so no <c>CultureInfo</c>/ICU, whose behavior is platform-dependent).
    /// </summary>
    private static bool AnyLanguageAcceptable(IReadOnlyList<string> preferences, IReadOnlyList<string> available)
    {
        foreach (var preferred in preferences)
        {
            // Unusable tags are dropped BEFORE any comparison, the equality check included:
            // otherwise identical garbage on both sides ("-en" vs "-en") would match itself and
            // silently suppress the report.
            if (!IsUsableTag(preferred))
                continue;
            var acceptsAnything = string.Equals(preferred, WildcardRange, StringComparison.Ordinal);
            foreach (var offered in available)
            {
                if (!IsUsableTag(offered))
                    continue;
                // RFC 4647 §2.1: a range of "*" is satisfied by whatever we happen to have — but
                // it still needs SOMETHING, hence the check inside the available-tags loop.
                if (acceptsAnything)
                    return true;
                if (string.Equals(preferred, offered, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Directional prefix matching at subtag boundaries (RFC 4647 style):
                //  - basic filtering: the range "en" matches the tag "en-GB";
                //  - lookup truncation: a preference for "en-US" is satisfied by bare "en".
                // Siblings (en-US vs en-GB, sr-Cyrl vs sr-Latn) never match — neither extends
                // the other, so treating them as interchangeable would wrongly suppress the
                // bad-lang report.
                if (ExtendsAtSubtagBoundary(offered, prefix: preferred) ||
                    ExtendsAtSubtagBoundary(preferred, prefix: offered))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="tag"/> is worth comparing at all: non-blank, and with no EMPTY
    /// subtag — a leading <c>-</c> ("-fonipa"), a trailing <c>-</c> ("en-") or a doubled <c>-</c>
    /// ("en--GB") makes it malformed under BCP 47. Such a tag matches nothing, not even an
    /// identical copy of itself: garbage on both sides must not suppress the bad-lang report.
    /// </summary>
    private static bool IsUsableTag(string tag)
        => !string.IsNullOrWhiteSpace(tag)
           && tag[0] != '-'
           && tag[^1] != '-'
           && !tag.Contains("--", StringComparison.Ordinal);

    /// <summary>True when <paramref name="tag"/> starts with <paramref name="prefix"/> followed by a <c>-</c> (case-insensitive) — i.e. extends it at a subtag boundary.</summary>
    private static bool ExtendsAtSubtagBoundary(string tag, string prefix)
        => tag.Length > prefix.Length
           && tag[prefix.Length] == '-'
           && tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

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
