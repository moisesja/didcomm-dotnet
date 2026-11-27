using DataProofsDotnet.Jose.Encryption;
using DidComm.Consistency;
using DidComm.Jose;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// Which sender a delivered message's unpack is counted against in <see cref="UnpackReservations"/>
/// (#111, I-14, D-1): the DID subject of the outer envelope's sender key when that envelope is an
/// authcrypt JWE (<c>alg</c> <c>ECDH-1PU+A256KW</c>) whose protected header names one in
/// <c>skid</c>; otherwise <c>null</c>, the anonymous partition.
/// </summary>
/// <remarks>
/// The header is read with <see cref="JweParser.PeekRecipients"/>, the call Core makes before it
/// decrypts, so no second header parser exists. What it reads is not authenticated: it only chooses
/// which reservations an unpack counts against, never what is opened or how. Core resolves exactly
/// <c>DidSubjectOf(skid)</c> for an authcrypt layer, so a lookup counted against a sender is a lookup
/// of that sender's own DID. Everything else is anonymous: anoncrypt, an <c>ECDH-ES</c> envelope that
/// carries a <c>skid</c> anyway, authcrypt that names its sender only in <c>apu</c>, a <c>skid</c>
/// over the FR-API-09 key-id limits or one that is not a DID URL, and anything that does not parse.
/// Nothing it throws escapes. Pure: it runs no host code and keeps no state.
/// </remarks>
internal static class SenderPartition
{
    /// <summary>The sender a message's unpack is counted against, or <c>null</c> for the anonymous partition.</summary>
    /// <param name="packed">The delivered message's packed text.</param>
    internal static string? Of(string packed)
    {
        try
        {
            var peek = JweParser.PeekRecipients(packed);
            if (!string.Equals(peek.Algorithm, JoseAlgorithms.Ecdh1PuA256Kw, StringComparison.Ordinal))
                return null;
            if (peek.Skid is not { } skid || !IdentifierLimits.IsWithinKeyIdLimits(skid))
                return null;
            return DidSubject.DidSubjectOf(skid);
        }
        catch
        {
            // Unparsable, or anything else the peek throws: anonymous. Opening the message is not
            // decided here; the unpack reports its own failure.
            return null;
        }
    }
}
