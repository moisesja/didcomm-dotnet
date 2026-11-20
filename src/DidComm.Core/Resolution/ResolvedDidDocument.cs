using DidComm.Exceptions;
using NetDid.Core.Model;

namespace DidComm.Resolution;

/// <summary>
/// The one check every net-did adapter applies to a resolver's answer before reading anything from
/// it: there is a document, and it is the document of the DID that was asked for (FR-CONSIST-09).
/// <see cref="NetDidKeyService"/> and <see cref="NetDidServiceEndpointResolver"/> both use it, so
/// the keys and the service endpoints of a DID come from the same kind of answer.
/// </summary>
internal static class ResolvedDidDocument
{
    /// <summary>
    /// The document of <paramref name="result"/>, once it is known to describe
    /// <paramref name="did"/>. Everything read from it is attributed to <paramref name="did"/> (the
    /// keys it authorizes, the endpoints a sender posts to), so a resolver that answers with another
    /// subject's document — a cross-wired composite resolver, a cache keyed wrongly, a hostile
    /// implementation — is refused here, before any key or service is read. The document's own
    /// <c>id</c> is the resolver's answer to "whose document is this"; W3C DID Core §3.1 requires it
    /// to be the DID that was resolved.
    /// </summary>
    /// <param name="did">The DID that was asked for.</param>
    /// <param name="result">The resolver's answer for <paramref name="did"/>.</param>
    /// <exception cref="DidResolutionException">The answer has no document, or its document's <c>id</c> is not <paramref name="did"/>.</exception>
    internal static DidDocument Require(string did, DidResolutionResult result)
    {
        if (result.DidDocument is null)
        {
            var error = result.ResolutionMetadata?.Error ?? "resolver returned no document";
            throw new DidResolutionException(did, error);
        }

        // One ordinal comparison, on every path that returns a document. It may stop at the first
        // character that differs: the requested DID is our own input and the document is the
        // resolver's public answer, so neither is a secret and the comparison's time reveals nothing.
        var documentId = result.DidDocument.Id.Value;
        if (!string.Equals(documentId, did, StringComparison.Ordinal))
        {
            throw new DidResolutionException(
                did,
                $"resolver returned a document whose id is '{documentId}'; refusing to attribute another subject's document to '{did}'");
        }

        return result.DidDocument;
    }
}
