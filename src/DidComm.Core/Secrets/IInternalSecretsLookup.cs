using DataProofsDotnet.Jose.Encryption;

namespace DidComm.Secrets;

/// <summary>
/// Minimal internal contract that the envelope layer uses to fetch recipient private keys for the
/// decrypt path. Identical in shape to DataProofsDotnet.Jose's
/// <see cref="IJweRecipientKeyResolver"/> (<c>TryGet</c> + <c>FindPresent</c>), which it extends so
/// an <see cref="IInternalSecretsLookup"/> can be handed straight to
/// <c>DataProofsDotnet.Jose.Encryption.JweParser.Parse</c> with no adapter. The didcomm-specific
/// type name is retained as the envelope layer's seam (and to keep the sync-over-async bridge in
/// <see cref="SyncSecretsAdapter"/> documented in DIDComm terms).
/// </summary>
internal interface IInternalSecretsLookup : IJweRecipientKeyResolver
{
}
