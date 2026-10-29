using System.Buffers;

namespace DidComm.Consistency;

/// <summary>
/// Upper bounds on the length of the DIDs and key ids a peer puts on the wire (FR-API-09, #80).
/// The receive path checks each such value against its bound before didcomm hands it to a DID
/// parser, a DID resolver or the consumer's <see cref="Secrets.ISecretsResolver"/>, and a refusal
/// never quotes the value. A JWE recipient kid over the limits is the one exception to refusal: it is
/// treated as not held, so one group member's long kid cannot block delivery to the others.
/// </summary>
/// <remarks>
/// <para>
/// NetDid parses DIDs and DID URLs with backtracking regular expressions. The cost is linear in the
/// input, not catastrophic, but without a bound a value can be as long as the whole envelope
/// (<c>DidCommOptions.MaxReceiveBytes</c>, 1 MiB by default). Parsing one value of that size costs
/// tens of milliseconds and tens of megabytes of transient allocation, and any sender can trigger
/// it before authentication. A per-value bound caps both costs for each parse.
/// </para>
/// <para>
/// Lengths are counted in UTF-16 code units (<see cref="string.Length"/>), the unit the plaintext
/// <c>from</c>/<c>to</c> checks already used. Real values are far shorter than either bound:
/// the longest DID URL in the interop fixtures is a few dozen characters.
/// </para>
/// </remarks>
internal static class IdentifierLimits
{
    /// <summary>
    /// Maximum length of a DID: the plaintext <c>from</c> and each <c>to</c> entry (FR-MSG-07/08), and
    /// the <c>from_prior</c> claims <c>iss</c> and <c>sub</c>.
    /// </summary>
    public const int MaxDidLength = 2048;

    /// <summary>
    /// Maximum length of a key id: a JWS signer <c>kid</c>, a JWE recipient <c>kid</c>, the authcrypt
    /// sender <c>skid</c> (including one decoded from <c>apu</c>), the <c>from_prior</c> header
    /// <c>kid</c>, and a forward's <c>next</c>. Also the backstop bound applied by
    /// <see cref="DidSubject.DidSubjectOf"/> and <c>NetDidKeyService.RejectUnsupportedMethod</c>, so
    /// nothing the wire checks accept is refused further down.
    /// </summary>
    /// <remarks>
    /// Deliberately larger than <see cref="MaxDidLength"/>. A kid is a DID plus a <c>#fragment</c>, and
    /// FR-CONSIST-01/03 require the authcrypt <c>skid</c> and the signer <c>kid</c> to have the same
    /// DID subject as <c>from</c>. With a single 2048 bound, a sender whose legal <c>from</c> is close
    /// to 2048 characters could never present its own key id. The headroom is cheap because parse
    /// cost grows linearly with length. The headroom is for the fragment only: a wire key id must
    /// also pass the DID-part rule in <see cref="IsWithinKeyIdLimits"/>.
    /// </remarks>
    public const int MaxKeyIdLength = 4096;

    // DID syntax allows none of these inside a DID (W3C DID Core §3.1: the method-specific id is
    // idchar / ':' only), and each one starts the next DID URL component: path, the legacy ';'
    // parameter, query, fragment. So everything before the first of them is exactly the DID part.
    private static readonly SearchValues<char> DidPartTerminators = SearchValues.Create("/;?#");

    /// <summary>
    /// Whether a wire key id is within both FR-API-09 bounds (#80): at most
    /// <see cref="MaxKeyIdLength"/> characters in all, and at most <see cref="MaxDidLength"/>
    /// characters in its DID part, meaning everything before its first <c>/</c>, <c>;</c>, <c>?</c>
    /// or <c>#</c> (the whole value when none appears).
    /// </summary>
    /// <remarks>
    /// The DID-part rule is what makes the headroom in <see cref="MaxKeyIdLength"/> a fragment
    /// allowance, not a larger DID bound. Without it a 4096-character kid could carry a 4090-character
    /// DID, and that DID would be resolved before FR-CONSIST-01/03 compare it with <c>from</c>. The
    /// check is one length comparison plus a vectorized linear scan, never a regular expression, and
    /// the total length is tested first, so an over-long value is never scanned.
    /// </remarks>
    /// <param name="value">The key id as it arrived.</param>
    public static bool IsWithinKeyIdLimits(string value)
        => value.Length <= MaxKeyIdLength && DidPartLength(value) <= MaxDidLength;

    /// <summary>
    /// The refusal text for a key id that fails <see cref="IsWithinKeyIdLimits"/>. It names the field
    /// and the bound that was broken, and never quotes the value.
    /// </summary>
    /// <param name="field">The field the key id came from, as the message should name it (e.g. <c>JWS signer 'kid'</c>).</param>
    /// <param name="value">The refused key id; only its length is consulted.</param>
    public static string KeyIdLimitMessage(string field, string value)
        => value.Length > MaxKeyIdLength
            ? $"{field} exceeds the maximum length of {MaxKeyIdLength} characters (FR-API-09)."
            : $"{field} has a DID part longer than the maximum of {MaxDidLength} characters (FR-API-09).";

    /// <summary>
    /// The length of the DID part of <paramref name="value"/>: the index of its first <c>/</c>,
    /// <c>;</c>, <c>?</c> or <c>#</c>, or its whole length when none appears. One vectorized linear
    /// scan, never a regular expression.
    /// </summary>
    /// <param name="value">A DID, a DID URL, or any other string.</param>
    public static int DidPartLength(string value)
    {
        var end = value.AsSpan().IndexOfAny(DidPartTerminators);
        return end < 0 ? value.Length : end;
    }
}
