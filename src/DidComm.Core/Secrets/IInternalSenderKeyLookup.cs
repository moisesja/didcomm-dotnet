using DataProofsDotnet.Jose.Encryption;

namespace DidComm.Secrets;

/// <summary>
/// Minimal internal contract that the envelope layer uses to fetch the *public* key of a
/// remote sender — needed for authcrypt unpack (Zs = ECDH(local_priv, sender_pub)). Identical in
/// shape to DataProofsDotnet.Jose's <see cref="IJweSenderKeyResolver"/> (<c>TryGet</c>), which it
/// extends so an <see cref="IInternalSenderKeyLookup"/> can be passed straight to
/// <c>DataProofsDotnet.Jose.Encryption.JweParser.Parse</c> with no adapter.
/// </summary>
internal interface IInternalSenderKeyLookup : IJweSenderKeyResolver
{
}
