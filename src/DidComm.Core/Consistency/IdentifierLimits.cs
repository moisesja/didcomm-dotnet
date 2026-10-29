namespace DidComm.Consistency;

/// <summary>
/// Upper bounds on the length of the DIDs and key ids a peer puts on the wire (FR-API-09, #80).
/// The receive path checks each such value against its bound before didcomm hands it to a DID
/// parser, a DID resolver or the consumer's <see cref="Secrets.ISecretsResolver"/>, and a refusal
/// never quotes the value.
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
    /// cost grows linearly with length.
    /// </remarks>
    public const int MaxKeyIdLength = 4096;
}
