namespace DidComm.Protocols.Routing;

/// <summary>
/// What <see cref="ForwardHandler"/> keeps of an admitted forward between admission and its pump —
/// while it waits for the transport's acknowledgement and while it is queued (#98, L-062). Each
/// unpack outcome is kept as <see cref="Plaintext"/>: its plaintext as the UTF-8 bytes the byte budget
/// charges, plus the result's other members, all small. The parsed message is not kept: it can be
/// many times its plaintext (a JSON attachment of many small entries is a DOM of one object per entry),
/// so keeping it would let the budget's bytes stand for far more memory than they count.
/// <see cref="Restore"/> parses the messages again, on the pump, just before the forward is processed.
/// </summary>
internal sealed class HeldForward
{
    // The context with its Received result removed, and that result as plaintext. Neither shell is
    // ever handed out: Restore puts the parsed results back first.
    private readonly ProtocolContext _context;
    private readonly Plaintext _received;
    private readonly Plaintext? _opened;

    /// <summary>Hold an admitted forward.</summary>
    /// <param name="context">The dispatch context of the received forward.</param>
    /// <param name="received">The received forward, as <see cref="Plaintext.Of"/> took it from <paramref name="context"/>.</param>
    /// <param name="opened">When the received forward was addressed to this mediator itself, the forward found inside it; otherwise <c>null</c>.</param>
    /// <param name="logId">The received forward's id, bounded for logging.</param>
    internal HeldForward(ProtocolContext context, Plaintext received, Plaintext? opened, string logId)
    {
        _context = context with { Received = null! };
        _received = received;
        _opened = opened;
        LogId = logId;
    }

    /// <summary>The received forward's id, bounded (an id over 128 characters is its length): what every log line carries.</summary>
    internal string LogId { get; }

    /// <summary>The received forward's transport acknowledgement (<see cref="ProtocolContext.Acknowledged"/>).</summary>
    internal Task Acknowledged => _context.Acknowledged;

    /// <summary>
    /// Parse the held messages again: the received forward's context as the handler was given it (its
    /// <see cref="ProtocolContext.Received"/> equal to the original but for a fresh <c>Message</c>), and
    /// the opened forward, if any. Each call parses again. Throws only what parsing a plaintext that
    /// was parsed once already can throw.
    /// </summary>
    internal (ProtocolContext Context, Facade.UnpackResult? Opened) Restore()
        => (_context with { Received = _received.Restore() }, _opened?.Restore());

    /// <summary>
    /// One unpack outcome held as its plaintext: the UTF-8 bytes (with the trust fields of the unpack's
    /// verified snapshot, if it has one) and the result's other members, which are bounded.
    /// </summary>
    internal sealed class Plaintext
    {
        private readonly Facade.UnpackResult _withoutMessage;
        private readonly InboundMessageSnapshot.Detached _plaintext;

        private Plaintext(Facade.UnpackResult unpacked, InboundMessageSnapshot.Detached plaintext)
        {
            _withoutMessage = unpacked with { Message = null! };
            _plaintext = plaintext;
        }

        /// <summary>
        /// Take an unpack outcome's plaintext: the exact plaintext the unpack recorded, or — for a
        /// result a host built itself — its message serialized once, as the byte budget has always
        /// sized it. Costs one pass over the plaintext; a serialization failure is thrown.
        /// </summary>
        /// <param name="unpacked">The unpack outcome.</param>
        internal static Plaintext Of(Facade.UnpackResult unpacked)
        {
            var snapshot = InboundMessageSnapshot.TryGetFor(unpacked.Message, out var verified)
                ? verified
                : InboundMessageSnapshot.CreateFallback(unpacked);
            return new Plaintext(unpacked, snapshot.Detach());
        }

        /// <summary>The plaintext's UTF-8 size: what the byte budget charges for this result, and what this holds of it.</summary>
        internal int Utf8ByteCount => _plaintext.Utf8ByteCount;

        /// <summary>The result with a message parsed again from the plaintext.</summary>
        internal Facade.UnpackResult Restore() => _withoutMessage with { Message = _plaintext.Reattach() };
    }
}
