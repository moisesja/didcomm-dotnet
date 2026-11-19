using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Messages;

namespace DidComm.Protocols;

/// <summary>
/// Immutable, library-internal snapshot of inbound plaintext and metadata. Normal unpack creates a
/// verified snapshot whose trust fields were established while unpacking; the public dispatcher's
/// synthetic compatibility path can create a fallback snapshot whose positional fields are caller
/// claims and whose bindings/addressing are neutral. The snapshot deliberately contains no reference
/// to the mutable <see cref="Message"/> used as a verified snapshot's weak-table key.
/// </summary>
internal sealed class InboundMessageSnapshot
{
    private static readonly ConditionalWeakTable<Message, InboundMessageSnapshot> VerifiedSnapshots = new();

    private InboundMessageSnapshot(
        string plaintextJson,
        string id,
        string type,
        string? thid,
        string? pthid,
        string? from,
        IReadOnlyList<string> to,
        bool encrypted,
        bool authenticated,
        bool nonRepudiation,
        bool anonymousSender,
        string? senderKid,
        string? signerKid,
        string? recipientKid,
        VerifiedKeyBinding? senderKeyBinding,
        VerifiedKeyBinding? signerKeyBinding,
        VerifiedKeyBinding? recipientKeyBinding,
        RecipientAddressing recipientAddressing,
        bool verified)
    {
        PlaintextJson = plaintextJson;
        Id = id;
        Type = type;
        Thid = thid;
        Pthid = pthid;
        From = from;
        To = to;
        Encrypted = encrypted;
        Authenticated = authenticated;
        NonRepudiation = nonRepudiation;
        AnonymousSender = anonymousSender;
        SenderKid = senderKid;
        SignerKid = signerKid;
        RecipientKid = recipientKid;
        SenderKeyBinding = senderKeyBinding;
        SignerKeyBinding = signerKeyBinding;
        RecipientKeyBinding = recipientKeyBinding;
        RecipientAddressing = recipientAddressing;
        Verified = verified;
    }

    private int _utf8ByteCount = -1;

    internal string PlaintextJson { get; }

    internal int Utf8ByteCount
    {
        get
        {
            // Lazy: only ObserverDelivery's byte-budget admission reads this; the plain
            // unpack path must not pay an O(plaintext) scan (#53). Benign race by design:
            // GetByteCount is deterministic over the immutable PlaintextJson and int
            // writes are atomic, so concurrent first readers at worst recompute the
            // same value.
            var count = _utf8ByteCount;
            if (count < 0)
                _utf8ByteCount = count = Encoding.UTF8.GetByteCount(PlaintextJson);
            return count;
        }
    }

    internal string Id { get; }
    internal string Type { get; }
    internal string? Thid { get; }

    /// <summary>The parent thread id (<c>pthid</c>): what a problem report about a request carries (FR-PICK-07).</summary>
    internal string? Pthid { get; }

    internal string? From { get; }
    internal IReadOnlyList<string> To { get; }
    internal bool Encrypted { get; }
    internal bool Authenticated { get; }
    internal bool NonRepudiation { get; }
    internal bool AnonymousSender { get; }
    internal string? SenderKid { get; }
    internal string? SignerKid { get; }
    internal string? RecipientKid { get; }
    internal VerifiedKeyBinding? SenderKeyBinding { get; }
    internal VerifiedKeyBinding? SignerKeyBinding { get; }
    internal VerifiedKeyBinding? RecipientKeyBinding { get; }
    internal RecipientAddressing RecipientAddressing { get; }

    /// <summary>
    /// <c>true</c> only for a snapshot registered by unpack (<see cref="RegisterVerified"/>), whose
    /// trust fields the envelope layer established; <c>false</c> for a <see cref="CreateFallback"/>
    /// snapshot, whose fields are the caller's claims. A correlator that completes a pending request
    /// on the strength of the trust fields must require it.
    /// </summary>
    internal bool Verified { get; }

    /// <summary>Associate verified unpack output with its mutable public message by object identity.</summary>
    /// <remarks><paramref name="recipientAddressing"/> is deliberately required, not defaulted: a
    /// registration site that forgets it must fail to compile rather than silently record
    /// <see cref="RecipientAddressing.NotEvaluated"/> and disable the FR-CONSIST-04 signal (#61).</remarks>
    internal static void RegisterVerified(
        Message message,
        string plaintextJson,
        bool encrypted,
        bool authenticated,
        bool nonRepudiation,
        bool anonymousSender,
        string? senderKid,
        string? signerKid,
        string? recipientKid,
        RecipientAddressing recipientAddressing,
        VerifiedKeyBinding? senderKeyBinding = null,
        VerifiedKeyBinding? signerKeyBinding = null,
        VerifiedKeyBinding? recipientKeyBinding = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(plaintextJson);

        VerifiedSnapshots.Add(message, new InboundMessageSnapshot(
            plaintextJson,
            message.Id,
            message.Type,
            message.Thid,
            message.Pthid,
            message.From,
            CopyRecipients(message.To),
            encrypted,
            authenticated,
            nonRepudiation,
            anonymousSender,
            senderKid,
            signerKid,
            recipientKid,
            senderKeyBinding,
            signerKeyBinding,
            recipientKeyBinding,
            recipientAddressing,
            verified: true));
    }

    /// <summary>Look up the verified snapshot associated with an unpacked message.</summary>
    internal static bool TryGetFor(Message message, out InboundMessageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(message);
        return VerifiedSnapshots.TryGetValue(message, out snapshot!);
    }

    /// <summary>
    /// Compatibility path for callers that construct <see cref="UnpackResult"/> themselves instead
    /// of obtaining it from <see cref="DidCommClient.UnpackAsync"/>. Callers must guard failures.
    /// </summary>
    internal static InboundMessageSnapshot CreateFallback(UnpackResult received)
    {
        ArgumentNullException.ThrowIfNull(received);
        ArgumentNullException.ThrowIfNull(received.Message);

        var json = JsonSerializer.Serialize(received.Message, DidCommJson.Default);
        // Derive headers from the serialized snapshot, not by rereading the mutable caller object.
        // Even if another thread mutates Message during this compatibility path, the fixed fields
        // used for correlation and the JSON parsed by the requester remain one logical version.
        var cloned = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)
                     ?? throw new JsonException("Synthetic inbound snapshot deserialized to null.");
        return new InboundMessageSnapshot(
            json,
            cloned.Id,
            cloned.Type,
            cloned.Thid,
            cloned.Pthid,
            cloned.From,
            CopyRecipients(cloned.To),
            received.Encrypted,
            received.Authenticated,
            received.NonRepudiation,
            received.AnonymousSender,
            received.SenderKid,
            received.SignerKid,
            received.RecipientKid,
            // Deliberately no key bindings (#56): this synthetic path never saw the packed bytes,
            // and the caller-supplied result's message may have diverged from whatever unpack (if
            // any) produced its binding evidence. Strong provenance is only attached when the
            // verified-at-unpack snapshot itself carries it. The result's claimed
            // RecipientAddressing is refused for the same reason (#61): no unpack evaluated this
            // message's 'to', so a hand-built result cannot launder an Addressed/NotAddressed
            // outcome into observers.
            senderKeyBinding: null,
            signerKeyBinding: null,
            recipientKeyBinding: null,
            recipientAddressing: RecipientAddressing.NotEvaluated,
            verified: false);
    }

    /// <summary>Create an independent mutable message instance from the immutable plaintext.</summary>
    internal Message DeserializeMessage()
        => JsonSerializer.Deserialize<Message>(PlaintextJson, DidCommJson.Default)
           ?? throw new JsonException("Inbound plaintext snapshot deserialized to null.");

    /// <summary>
    /// This snapshot in the form kept by a holder that must keep only bounded values while it waits
    /// (L-062; the forward handler between admission and its pump): the plaintext as UTF-8 bytes,
    /// which is exactly what a byte budget charges, and the trust fields. The headers parsed from the
    /// plaintext are not kept. <see cref="Detached.Reattach"/> rebuilds the message from those bytes.
    /// </summary>
    internal Detached Detach() => new(this);

    /// <summary>
    /// A snapshot taken apart by <see cref="Detach"/>: the plaintext as UTF-8 bytes and the trust fields
    /// unpack established for it (or, for a <see cref="CreateFallback"/> snapshot, no trust at all).
    /// </summary>
    internal sealed class Detached
    {
        private readonly byte[] _utf8Plaintext;
        private readonly bool _verified;
        private readonly bool _encrypted;
        private readonly bool _authenticated;
        private readonly bool _nonRepudiation;
        private readonly bool _anonymousSender;
        private readonly string? _senderKid;
        private readonly string? _signerKid;
        private readonly string? _recipientKid;
        private readonly VerifiedKeyBinding? _senderKeyBinding;
        private readonly VerifiedKeyBinding? _signerKeyBinding;
        private readonly VerifiedKeyBinding? _recipientKeyBinding;
        private readonly RecipientAddressing _recipientAddressing;

        internal Detached(InboundMessageSnapshot snapshot)
        {
            _utf8Plaintext = Encoding.UTF8.GetBytes(snapshot.PlaintextJson);
            _verified = snapshot.Verified;
            _encrypted = snapshot.Encrypted;
            _authenticated = snapshot.Authenticated;
            _nonRepudiation = snapshot.NonRepudiation;
            _anonymousSender = snapshot.AnonymousSender;
            _senderKid = snapshot.SenderKid;
            _signerKid = snapshot.SignerKid;
            _recipientKid = snapshot.RecipientKid;
            _senderKeyBinding = snapshot.SenderKeyBinding;
            _signerKeyBinding = snapshot.SignerKeyBinding;
            _recipientKeyBinding = snapshot.RecipientKeyBinding;
            _recipientAddressing = snapshot.RecipientAddressing;
        }

        /// <summary>The plaintext's UTF-8 size: the same count as <see cref="InboundMessageSnapshot.Utf8ByteCount"/>, and all the plaintext this holds.</summary>
        internal int Utf8ByteCount => _utf8Plaintext.Length;

        /// <summary>
        /// Parse a fresh message from the plaintext. For a verified snapshot, register the same trust
        /// fields for it: the plaintext is the one unpack verified, unchanged, so the fresh message is
        /// what unpack would have produced, and observers read the same facts from it. A fallback
        /// snapshot's message gets no snapshot, as the message it was taken from had none.
        /// </summary>
        internal Message Reattach()
        {
            var plaintextJson = Encoding.UTF8.GetString(_utf8Plaintext);
            var message = JsonSerializer.Deserialize<Message>(plaintextJson, DidCommJson.Default)
                          ?? throw new JsonException("Inbound plaintext snapshot deserialized to null.");
            if (_verified)
            {
                RegisterVerified(
                    message,
                    plaintextJson,
                    _encrypted,
                    _authenticated,
                    _nonRepudiation,
                    _anonymousSender,
                    _senderKid,
                    _signerKid,
                    _recipientKid,
                    _recipientAddressing,
                    _senderKeyBinding,
                    _signerKeyBinding,
                    _recipientKeyBinding);
            }

            return message;
        }
    }

    private static IReadOnlyList<string> CopyRecipients(IList<string>? recipients)
        => recipients is null || recipients.Count == 0
            ? Array.Empty<string>()
            : Array.AsReadOnly(recipients.ToArray());
}
