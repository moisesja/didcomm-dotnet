using DidComm.Protocols.ProblemReport;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The one rule for a problem code the mediator sent before it reaches
/// <see cref="MediationProtocolException.ProblemCode"/> (design §2): at most
/// <see cref="MaxLength"/> characters of <c>a</c>–<c>z</c>, <c>0</c>–<c>9</c>, <c>.</c>, <c>_</c> and
/// <c>-</c>, and parsing as a Report Problem 2.0 code; anything else is <c>null</c>. The length and
/// the characters are checked first, so a hostile value is never split or copied.
/// </summary>
internal static class MediatorProblemCode
{
    /// <summary>The longest code kept.</summary>
    internal const int MaxLength = 128;

    /// <summary>The code when it is well formed, otherwise <c>null</c>.</summary>
    /// <param name="code">The <c>body.code</c> as it arrived, or <c>null</c>.</param>
    internal static string? Sanitize(string? code)
    {
        if (code is null || code.Length is 0 or > MaxLength)
            return null;
        foreach (var c in code)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'))
                return null;
        }
        return ProblemCode.TryParse(code, out _) ? code : null;
    }
}
