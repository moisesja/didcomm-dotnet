using System.Text.Json;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Messages;

namespace DidComm.Protocols;

/// <summary>
/// The read-only view of one inbound message handed to an <see cref="IProtocolObserver"/>. It
/// carries a defensive deep clone of the message plus the envelope authentication metadata an
/// observer needs to judge trust — deliberately not the live <see cref="Message"/> instance, the
/// <see cref="DidCommClient"/> facade, or the thread-state store.
/// </summary>
/// <remarks>
/// <strong>What the narrow payload does and does not guarantee.</strong> The deep clone means a
/// mutation an observer makes to <see cref="Message"/> cannot reach the dispatch pipeline, the
/// handler, a reply, or another observer — that isolation IS enforced. It is <em>not</em> a
/// capability sandbox: observers are trusted in-process host code (registered at DI composition),
/// so they can inject <see cref="DidCommClient"/> or the thread store themselves if they need to
/// act — this library's own <c>DiscoverFeaturesClient</c> injects the client. Omitting the facade
/// from this payload keeps the observation surface minimal and read-only; it does not, and cannot,
/// prevent host code from sending as the agent. See <see cref="IProtocolObserver"/>'s trust model.
/// </remarks>
/// <param name="Message">A deep clone of the unpacked inbound message. Mutating it affects only this observation.</param>
/// <param name="Encrypted">Whether the envelope had an encryption layer (mirrors <see cref="UnpackResult.Encrypted"/>).</param>
/// <param name="Authenticated">Whether the sender is cryptographically authenticated — authcrypt or a verified signature (mirrors <see cref="UnpackResult.Authenticated"/>). Observers that act on sender identity (e.g. correlating a reply to a request) MUST require this.</param>
/// <param name="NonRepudiation">Whether a verified non-repudiable signature was present (mirrors <see cref="UnpackResult.NonRepudiation"/>).</param>
/// <param name="AnonymousSender">Whether the envelope hid the sender (anoncrypt; mirrors <see cref="UnpackResult.AnonymousSender"/>).</param>
/// <param name="SenderKid">The authcrypt sender key id (<c>skid</c>), when present.</param>
/// <param name="SignerKid">The verified JWS signer key id, when present.</param>
public sealed record InboundObservation(
    Message Message,
    bool Encrypted,
    bool Authenticated,
    bool NonRepudiation,
    bool AnonymousSender,
    string? SenderKid,
    string? SignerKid)
{
    /// <summary>
    /// Build an observation from an unpack result, deep-cloning the message (serialize →
    /// deserialize through the DIDComm JSON options, so extension headers and attachments
    /// survive intact) so the observer can never reach the pipeline's live instance.
    /// </summary>
    /// <param name="received">The unpack result for the inbound message.</param>
    public static InboundObservation FromUnpackResult(UnpackResult received)
    {
        ArgumentNullException.ThrowIfNull(received);
        var clone = JsonSerializer.SerializeToNode(received.Message, DidCommJson.Default)!
            .Deserialize<Message>(DidCommJson.Default)!;
        return new InboundObservation(
            Message: clone,
            Encrypted: received.Encrypted,
            Authenticated: received.Authenticated,
            NonRepudiation: received.NonRepudiation,
            AnonymousSender: received.AnonymousSender,
            SenderKid: received.SenderKid,
            SignerKid: received.SignerKid);
    }
}
