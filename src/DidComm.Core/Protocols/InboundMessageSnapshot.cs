using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Messages;

namespace DidComm.Protocols;

/// <summary>
/// Immutable, library-internal snapshot of a successfully unpacked inbound plaintext and the trust
/// metadata established while unpacking it. The snapshot deliberately contains no reference to the
/// mutable <see cref="Message"/> used as its weak-table key.
/// </summary>
internal sealed class InboundMessageSnapshot
{
    private static readonly ConditionalWeakTable<Message, InboundMessageSnapshot> VerifiedSnapshots = new();

    private InboundMessageSnapshot(
        string plaintextJson,
        string id,
        string type,
        string? thid,
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
        VerifiedKeyBinding? recipientKeyBinding)
    {
        PlaintextJson = plaintextJson;
        Id = id;
        Type = type;
        Thid = thid;
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

    /// <summary>Associate verified unpack output with its mutable public message by object identity.</summary>
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
            recipientKeyBinding));
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
            // verified-at-unpack snapshot itself carries it.
            senderKeyBinding: null,
            signerKeyBinding: null,
            recipientKeyBinding: null);
    }

    /// <summary>Create an independent mutable message instance from the immutable plaintext.</summary>
    internal Message DeserializeMessage()
        => JsonSerializer.Deserialize<Message>(PlaintextJson, DidCommJson.Default)
           ?? throw new JsonException("Inbound plaintext snapshot deserialized to null.");

    private static IReadOnlyList<string> CopyRecipients(IList<string>? recipients)
        => recipients is null || recipients.Count == 0
            ? Array.Empty<string>()
            : Array.AsReadOnly(recipients.ToArray());
}
