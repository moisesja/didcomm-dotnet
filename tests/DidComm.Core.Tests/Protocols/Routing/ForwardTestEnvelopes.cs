using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Secrets;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// Synthetic <see cref="UnpackResult"/>s for the mediator-side routing tests (#90). The FR-ROUTE-11
/// posture is judged on envelope metadata (<c>Encrypted</c>, <c>RecipientKid</c>), so a test that
/// is about the processor's structural contract — pass-through bytes, expiry, delay, attachments —
/// builds the forward <see cref="Message"/> and wraps it "as decrypted by the mediator" instead of
/// running real crypto. Real anoncrypt round trips live in <c>AliceMediatorBobRoundTripTests</c>.
/// </summary>
internal static class ForwardTestEnvelopes
{
    /// <summary>The recipient kid every <see cref="AsDecryptedByMediator"/> result reports by default.</summary>
    public const string MediatorKid = "did:example:m#key-1";

    /// <summary>The signer kid <see cref="AsSignedOnly"/> reports.</summary>
    public const string SignerKid = "did:example:s#key-1";

    /// <summary>The shape an anoncrypt forward has after the mediator decrypted it with one of its own keys.</summary>
    public static UnpackResult AsDecryptedByMediator(Message message, string recipientKid = MediatorKid)
        => new(
            Message: message,
            Stack: new[] { EnvelopeKind.Encrypted, EnvelopeKind.Plaintext },
            Encrypted: true,
            Authenticated: false,
            NonRepudiation: false,
            AnonymousSender: true,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: null,
            SenderKid: null,
            RecipientKid: recipientKid,
            AllRecipientKids: new[] { recipientKid },
            FromPrior: null);

    /// <summary>A forward that arrived as a plaintext envelope: no protection at all.</summary>
    public static UnpackResult AsPlaintext(Message message)
        => new(
            Message: message,
            Stack: new[] { EnvelopeKind.Plaintext },
            Encrypted: false,
            Authenticated: false,
            NonRepudiation: false,
            AnonymousSender: false,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: null,
            SenderKid: null,
            RecipientKid: null,
            AllRecipientKids: Array.Empty<string>(),
            FromPrior: null);

    /// <summary>A forward that arrived signed but not encrypted.</summary>
    public static UnpackResult AsSignedOnly(Message message)
        => new(
            Message: message,
            Stack: new[] { EnvelopeKind.Signed, EnvelopeKind.Plaintext },
            Encrypted: false,
            Authenticated: false,
            NonRepudiation: true,
            AnonymousSender: false,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: "EdDSA",
            SignerKid: SignerKid,
            SenderKid: null,
            RecipientKid: null,
            AllRecipientKids: Array.Empty<string>(),
            FromPrior: null);

    /// <summary>
    /// A result that claims encryption but names no decrypting key — cannot come out of a real
    /// unpack, so it pins that the processor checks the kid and not just the flag.
    /// </summary>
    public static UnpackResult AsEncryptedWithoutRecipientKid(Message message)
        => AsDecryptedByMediator(message) with { RecipientKid = null, AllRecipientKids = Array.Empty<string>() };

    /// <summary>A facade over empty secrets and an empty key service: unpacks plaintext, resolves nothing.</summary>
    public static DidCommClient NewClient(DidCommOptions? options = null)
        => new(new EmptySecretsResolver(), new EmptyKeyService(), options ?? new DidCommOptions());

    /// <summary>
    /// A processor over <see cref="NewClient"/>. <paramref name="routingKeys"/> defaults to the
    /// static provider over the options' extras; <paramref name="didCommOptions"/> defaults to
    /// the system clock; <paramref name="keyService"/> defaults to <see cref="EmptyKeyService"/>.
    /// </summary>
    public static ForwardProcessor NewProcessor(
        ForwardProcessorOptions? options = null,
        IRecipientRoutingKeyProvider? routingKeys = null,
        DidCommOptions? didCommOptions = null,
        IDidKeyService? keyService = null)
    {
        var resolved = options ?? new ForwardProcessorOptions();
        var clock = didCommOptions ?? new DidCommOptions();
        return new ForwardProcessor(
            NewClient(clock),
            keyService ?? new EmptyKeyService(),
            resolved,
            routingKeys ?? new StaticRecipientRoutingKeyProvider(resolved.ExtraRecipientRoutingKeys),
            clock);
    }

    /// <summary>Holds no secrets.</summary>
    public sealed class EmptySecretsResolver : ISecretsResolver
    {
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default) => Task.FromResult<Jwk?>(null);
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    /// <summary>Resolves every DID to no keys — a rewrap against it throws <c>DidResolutionException</c>.</summary>
    public sealed class EmptyKeyService : IDidKeyService
    {
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Jwk>>(Array.Empty<Jwk>());
        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(false);
        public void RejectUnsupportedMethod(string did) { }
    }

    /// <summary>A key service whose every lookup throws <see cref="Failure"/>.</summary>
    public sealed class ThrowingKeyService(Exception failure) : IDidKeyService
    {
        public Exception Failure { get; } = failure;

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromException<IReadOnlyList<Jwk>>(Failure);

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromException<bool>(Failure);

        public void RejectUnsupportedMethod(string did) { }
    }
}
