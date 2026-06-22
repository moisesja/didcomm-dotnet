using DidComm.Secrets;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Composition;

/// <summary>
/// Test-only synchronous bridge to the async <see cref="EnvelopeReader.UnpackAsync"/>. The
/// envelope-layer unit / interop tests drive the reader directly with a dictionary-backed
/// <see cref="ISecretsResolver"/>; this wraps it in a <see cref="KeyOperationResolver"/> (extractable
/// path — opaque handles are exercised end-to-end through the facade) and blocks on the async unpack
/// so those tests stay synchronous. Production never blocks: the facade <c>await</c>s
/// <see cref="EnvelopeReader.UnpackAsync"/> directly.
/// </summary>
internal static class EnvelopeReaderTestRunner
{
    public static UnpackResult Unpack(
        string packed,
        ISecretsResolver secrets,
        IInternalSenderKeyLookup? senderLookup,
        Func<string, Jwk?>? signerLookup,
        JoseCryptoProvider crypto,
        Func<string, string, string, bool>? resolverCheck = null)
    {
        var keyOps = new KeyOperationResolver(secrets, secrets as IOpaqueKeyResolver, crypto);
        Func<string, string, string, CancellationToken, Task<bool>>? asyncCheck =
            resolverCheck is null ? null : (a, b, c, _) => Task.FromResult(resolverCheck(a, b, c));
        return EnvelopeReader
            .UnpackAsync(packed, keyOps, senderLookup, signerLookup, crypto, asyncCheck)
            .GetAwaiter().GetResult();
    }
}
