using DidComm.Consistency;
using DidComm.Jose;
using DidComm.Secrets;

namespace DidComm.Resolution;

/// <summary>
/// Internal lookups that translate a kid into a public JWK by resolving the kid's DID through
/// <see cref="IDidKeyService"/>. Used by the facade to satisfy the envelope layer's
/// <see cref="IInternalSenderKeyLookup"/> contract (authcrypt sender public keys) and the
/// signer-key <c>Func&lt;string, Jwk?&gt;</c> JWS verification slot.
/// </summary>
/// <remarks>
/// <para>
/// Sync-over-async at the seam: the public <see cref="IDidKeyService"/> contract is async, but
/// the JOSE composition layer is sync. The same .NET 10 no-sync-context property that makes
/// <see cref="SyncSecretsAdapter"/> safe applies here.
/// </para>
/// <para>
/// The DID portion of the kid is extracted by <see cref="DidSubject.DidSubjectOf"/>; for a
/// well-formed DID URL with a fragment this is the bare DID subject the relationship list
/// should be resolved against.
/// </para>
/// </remarks>
internal static class DidKeyServiceLookups
{
    public static IInternalSenderKeyLookup SenderKeyLookup(IDidKeyService keyService)
    {
        ArgumentNullException.ThrowIfNull(keyService);
        return new DidKeyServiceSenderLookup(keyService);
    }

    public static Func<string, Jwk?> SignerKeyLookup(IDidKeyService keyService)
    {
        ArgumentNullException.ThrowIfNull(keyService);
        return kid => Lookup(keyService, kid, VerificationRelationship.Authentication);
    }

    private static Jwk? Lookup(IDidKeyService keyService, string kid, VerificationRelationship relationship)
    {
        var did = DidSubject.DidSubjectOf(kid);
        if (did is null) return null;
        var keys = keyService.GetVerificationMethodsAsync(did, relationship)
            .ConfigureAwait(false).GetAwaiter().GetResult();
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
        public Jwk? TryGet(string skid) => Lookup(_keyService, skid, VerificationRelationship.KeyAgreement);
    }
}
