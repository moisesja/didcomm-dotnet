using System.Text;

namespace DidComm.Protocols.ProblemReport;

/// <summary>
/// FR-PROTO-07 comment interpolation. Replaces <c>{1}</c>, <c>{2}</c>, … in a comment string
/// with the matching 1-based entry from the problem-report body's <c>args</c> array; missing
/// args become the literal <c>?</c>; extra args (not referenced by any placeholder) are
/// appended at the end as <c>" [extra: arg, arg, …]"</c> so they are not lost.
/// </summary>
/// <remarks>
/// The 1-based indexing matches the DIDComm v2.1 spec example ("If the comment is
/// '{1} cannot be used because {2} does not respond.' and the args are ['foo.com', 'bar.com']
/// the message becomes 'foo.com cannot be used because bar.com does not respond.'").
/// </remarks>
internal static class CommentInterpolator
{
    /// <summary>
    /// Interpolate <paramref name="comment"/> against <paramref name="args"/>. Returns an
    /// empty string when <paramref name="comment"/> is null/empty.
    /// </summary>
    /// <param name="comment">The comment template, e.g. <c>"Cannot use {1} for {2}."</c>.</param>
    /// <param name="args">Positional substitution values (1-based — <c>{1}</c> ↔ <c>args[0]</c>).</param>
    public static string Interpolate(string? comment, IReadOnlyList<string>? args)
    {
        if (string.IsNullOrEmpty(comment)) return string.Empty;
        if (args is null || args.Count == 0) return comment;

        var used = new bool[args.Count];
        var sb = new StringBuilder(comment.Length + 16);
        var i = 0;
        while (i < comment.Length)
        {
            var ch = comment[i];
            if (ch == '}' && i + 1 < comment.Length && comment[i + 1] == '}')
            {
                // String.Format-style `}}` literal-brace escape.
                sb.Append('}');
                i += 2;
                continue;
            }
            if (ch != '{')
            {
                sb.Append(ch);
                i++;
                continue;
            }

            // Read a {N} placeholder; tolerate `{{` as a literal-brace escape.
            if (i + 1 < comment.Length && comment[i + 1] == '{')
            {
                sb.Append('{');
                i += 2;
                continue;
            }

            var close = comment.IndexOf('}', i + 1);
            if (close < 0)
            {
                // Unclosed brace — emit verbatim so we don't lose characters.
                sb.Append(ch);
                i++;
                continue;
            }

            var token = comment.AsSpan(i + 1, close - i - 1);
            if (!int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                // Not a positional placeholder — emit verbatim.
                sb.Append(comment, i, close - i + 1);
                i = close + 1;
                continue;
            }

            if (n - 1 < args.Count)
            {
                sb.Append(args[n - 1]);
                used[n - 1] = true;
            }
            else
            {
                // Missing arg → literal '?'.
                sb.Append('?');
            }
            i = close + 1;
        }

        // Append extras (args present but never referenced).
        var extras = new List<string>();
        for (var k = 0; k < args.Count; k++)
            if (!used[k]) extras.Add(args[k]);
        if (extras.Count > 0)
        {
            sb.Append(" [extra: ");
            sb.Append(string.Join(", ", extras));
            sb.Append(']');
        }

        return sb.ToString();
    }
}
