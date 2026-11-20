using System.Text;

namespace DidComm.Protocols.Routing;

/// <summary>
/// What <see cref="ForwardHandler"/> keeps of an admitted forward between admission and its pump —
/// while it waits for the transport's acknowledgement and while it is queued (#98, L-062) — and what
/// the byte budget charges for it: the same thing, by construction. Each unpack outcome is kept as
/// <see cref="Unpacked"/>: the plaintext as UTF-8 bytes and the envelope's recipient kid list in one
/// string, plus the result's other members, which FR-API-09 caps. The context keeps its thread state,
/// whose thread id comes off the wire uncapped and is charged too. The parsed message is not kept: it
/// can be many times its plaintext (a JSON attachment of many small entries is a DOM of one object per
/// entry), so keeping it would let the budget's bytes stand for far more memory than they count.
/// <see cref="Restore"/> parses the messages again, on the pump, just before the forward is processed.
/// </summary>
/// <remarks>
/// Kept and not charged, because each is bounded: the forward's id for logs (at most 128
/// characters); the kids the unpack resolved or holds, the <c>from_prior</c> claims and the key
/// bindings (FR-API-09: a key id at most 4 096 characters, a DID at most 2 048); the algorithm names
/// and the envelope stack (a handful); the parsed <c>return_route</c> header (its thread at most
/// 2 048); and the context's shared services. Their total is a few hundred bytes for a typical
/// forward and under about 100 KB at the caps (authcrypt sender, signer, <c>from_prior</c> and their
/// bindings all at the limits), per forward, under the item cap.
/// </remarks>
internal sealed class HeldForward
{
    // The context with its Received result removed, and that result held. Neither shell is ever
    // handed out: Restore puts the rebuilt results back first.
    private readonly ProtocolContext _context;
    private readonly Unpacked _received;
    private readonly Unpacked? _opened;

    /// <summary>Hold an admitted forward.</summary>
    /// <param name="context">The dispatch context of the received forward.</param>
    /// <param name="received">The received forward, as <see cref="Unpacked.Of"/> took it from <paramref name="context"/>.</param>
    /// <param name="opened">When the received forward was addressed to this mediator itself, the forward found inside it; otherwise <c>null</c>.</param>
    /// <param name="logId">The received forward's id, bounded for logging.</param>
    internal HeldForward(ProtocolContext context, Unpacked received, Unpacked? opened, string logId)
    {
        _context = context with { Received = null! };
        _received = received;
        _opened = opened;
        LogId = logId;
        // The thread state is the dispatcher's, shared with its thread store; holding it keeps its
        // thread id alive (the message's thid, or its id): at most 512 characters after unpack
        // (FR-MSG-02/11), but a direct caller's may be longer, so it is charged as it is: a string,
        // 2 bytes a character.
        var threadBytes = 2L * (context.Thread?.Thid?.Length ?? 0);
        Bytes = (int)Math.Min(int.MaxValue, (long)received.Bytes + (opened?.Bytes ?? 0) + threadBytes);
    }

    /// <summary>
    /// The byte budget's charge for this forward: the bytes it keeps of what the wire sizes — each
    /// plaintext as UTF-8, and each recipient kid list and the thread id as strings (2 bytes a
    /// character, plus 4 bytes for each kid's length). Saturates at <see cref="int.MaxValue"/>.
    /// </summary>
    internal int Bytes { get; }

    /// <summary>The received forward's id, bounded (an id over 128 characters is its length): what every log line carries.</summary>
    internal string LogId { get; }

    /// <summary>The received forward's transport acknowledgement (<see cref="ProtocolContext.Acknowledged"/>).</summary>
    internal Task Acknowledged => _context.Acknowledged;

    /// <summary>
    /// Rebuild the held results: the received forward's context as the handler was given it, except
    /// that its <see cref="ProtocolContext.Received"/> is an equal result rebuilt from what was held —
    /// a new <c>Message</c> of the same content and a new recipient kid list of the same kids — and the
    /// opened forward, if any. Each call rebuilds again. Throws only what parsing a plaintext that was
    /// parsed once already can throw.
    /// </summary>
    internal (ProtocolContext Context, Facade.UnpackResult? Opened) Restore()
        => (_context with { Received = _received.Restore() }, _opened?.Restore());

    /// <summary>
    /// One unpack outcome as it is held: the plaintext as UTF-8 bytes (with the trust fields of the
    /// unpack's verified snapshot, if it has one), the recipient kid list in one string, and the
    /// result's other members, which are bounded.
    /// </summary>
    internal sealed class Unpacked
    {
        private readonly Facade.UnpackResult _shell;
        private readonly InboundMessageSnapshot.Detached _plaintext;
        private readonly Kids? _kids;

        private Unpacked(Facade.UnpackResult unpacked, InboundMessageSnapshot.Detached plaintext)
        {
            _shell = unpacked with { Message = null!, AllRecipientKids = null! };
            _plaintext = plaintext;
            _kids = unpacked.AllRecipientKids is { } kids ? new Kids(kids) : null;
            Bytes = (int)Math.Min(int.MaxValue, (long)plaintext.Utf8ByteCount + (_kids?.Bytes ?? 0));
        }

        /// <summary>
        /// Take an unpack outcome as it will be held: the exact plaintext the unpack recorded (or — for
        /// a result a host built itself — its message serialized once) and the recipient kid list.
        /// Costs one pass over each; a serialization failure is thrown.
        /// </summary>
        /// <param name="unpacked">The unpack outcome.</param>
        internal static Unpacked Of(Facade.UnpackResult unpacked)
        {
            var snapshot = InboundMessageSnapshot.TryGetFor(unpacked.Message, out var verified)
                ? verified
                : InboundMessageSnapshot.CreateFallback(unpacked);
            return new Unpacked(unpacked, snapshot.Detach());
        }

        /// <summary>The plaintext's UTF-8 size plus the kid list's held size: the charge for this result, and what this holds of the wire.</summary>
        internal int Bytes { get; }

        /// <summary>The result rebuilt: a message parsed again from the plaintext, and the kid list decoded again.</summary>
        internal Facade.UnpackResult Restore()
            => _shell with { Message = _plaintext.Reattach(), AllRecipientKids = _kids?.Restore()! };
    }

    /// <summary>
    /// The envelope's recipient kid list in compact form: the kids end to end in one string, and each
    /// kid's length. A JWE may name any number of recipients, of any length, and the list is the
    /// sender's verbatim, so it is held as one string rather than one string object per kid; the
    /// characters are kept as they are, so the list rebuilt is the list received.
    /// </summary>
    private sealed class Kids
    {
        private readonly string _chars;
        private readonly int[] _lengths; // -1 for a null entry (a host-built list)

        internal Kids(IReadOnlyList<string> kids)
        {
            _lengths = new int[kids.Count];
            var all = new StringBuilder();
            for (var i = 0; i < kids.Count; i++)
            {
                _lengths[i] = kids[i]?.Length ?? -1;
                all.Append(kids[i]);
            }
            _chars = all.ToString();
        }

        /// <summary>What this holds: 2 bytes for each character and 4 bytes for each kid's length.</summary>
        internal long Bytes => 2L * _chars.Length + 4L * _lengths.Length;

        internal IReadOnlyList<string> Restore()
        {
            if (_lengths.Length == 0)
                return Array.Empty<string>();
            var kids = new string[_lengths.Length];
            var offset = 0;
            for (var i = 0; i < kids.Length; i++)
            {
                if (_lengths[i] < 0)
                {
                    kids[i] = null!;
                    continue;
                }
                kids[i] = _chars.Substring(offset, _lengths[i]);
                offset += _lengths[i];
            }
            return kids;
        }
    }
}
