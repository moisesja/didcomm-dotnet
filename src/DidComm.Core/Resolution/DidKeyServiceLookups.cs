using DidComm.Consistency;
using DidComm.Jose;
using DidComm.Secrets;

namespace DidComm.Resolution;

/// <summary>
/// Internal lookups that translate a kid into a public JWK by resolving the kid's DID through
/// <see cref="IDidKeyService"/>. Used by the facade, for key services without
/// <see cref="IDidKeyBindingService"/>, to satisfy the envelope layer's
/// <see cref="IInternalSenderKeyLookup"/> contract (authcrypt sender public keys) and its JWS
/// signer-key lookup.
/// </summary>
/// <remarks>
/// <para>
/// Both lookups are called with the unpack's cancellation token, and neither is ever waited for
/// inside a callback of DataProofs' parsers, which are synchronous. The sender-key lookup is awaited
/// before the authcrypt sender-key callback (<c>IJweSenderKeyResolver.TryGet</c>) runs; the callback
/// answers from the resolved key. The signer-key lookup is called for each signature in turn. An
/// answer it has at once, when the JWS verify (<c>JwsParser.Parse</c>) asks for the key, is handed
/// to the parser. From the first one that has to be waited for, each lookup is awaited outside the
/// parser, which is then answered from the resolved key (see <c>EnvelopeReader</c>). Each call
/// resolves once; nothing is kept between calls.
/// </para>
/// <para>
/// The DID portion of the kid is extracted by <see cref="DidSubject.DidSubjectOf"/>; for a
/// well-formed DID URL with a fragment this is the bare DID subject the relationship list
/// should be resolved against.
/// </para>
/// </remarks>
internal static class DidKeyServiceLookups
{
    /// <summary>The authcrypt sender-key lookup (<c>keyAgreement</c>) over <paramref name="keyService"/>.</summary>
    /// <param name="keyService">The registered key service.</param>
    public static IInternalSenderKeyLookup SenderKeyLookup(IDidKeyService keyService)
    {
        ArgumentNullException.ThrowIfNull(keyService);
        return new DidKeyServiceSenderLookup(keyService);
    }

    /// <summary>The JWS signer-key lookup (<c>authentication</c>) over <paramref name="keyService"/>.</summary>
    /// <param name="keyService">The registered key service.</param>
    public static Func<string, CancellationToken, ValueTask<Jwk?>> SignerKeyLookup(IDidKeyService keyService)
    {
        ArgumentNullException.ThrowIfNull(keyService);
        return (kid, ct) => LookupAsync(keyService, kid, VerificationRelationship.Authentication, ct);
    }

    private static async ValueTask<Jwk?> LookupAsync(IDidKeyService keyService, string kid, VerificationRelationship relationship, CancellationToken ct)
    {
        var did = DidSubject.DidSubjectOf(kid);
        if (did is null) return null;
        var keys = await keyService.GetVerificationMethodsAsync(did, relationship, ct).ConfigureAwait(false);
        foreach (var key in keys)
        {
            if (string.Equals(key.Kid, kid, StringComparison.Ordinal))
                return key;
        }
        return null;
    }

    private sealed class DidKeyServiceSenderLookup : IInternalSenderKeyLookup
    {
        private readonly IDidKeyService _keyService;
        public DidKeyServiceSenderLookup(IDidKeyService keyService) => _keyService = keyService;
        public ValueTask<Jwk?> ResolveAsync(string skid, CancellationToken ct) => LookupAsync(_keyService, skid, VerificationRelationship.KeyAgreement, ct);
    }
}
