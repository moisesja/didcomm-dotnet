using System.Text;
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
/// <para>
/// <strong>Where the trust metadata comes from.</strong> Observations delivered to observers are
/// materialized entirely from the immutable verified unpack snapshot.
/// <see cref="FromUnpackResult(UnpackResult)"/> likewise sources the six trust-metadata members
/// (<see cref="Encrypted"/> through <see cref="SignerKid"/>), the key bindings, and
/// <see cref="RecipientAddressing"/> from that snapshot whenever it still covers the supplied
/// result's message content — the caller's result supplies those values only when no snapshot
/// covers the content (a synthetic or content-diverged result). A record <c>with</c>-clone copies
/// every member verbatim, so a clone's values describe the message the observation was built
/// from, not a <see cref="Message"/> substituted afterwards.
/// </para>
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
    /// Same-document sender key evidence (mirrors <see cref="UnpackResult.SenderKeyBinding"/>; #56).
    /// Present only when the observation derives from a real verified unpack whose key service
    /// captured provenance — a synthetic <see cref="UnpackResult"/> cannot manufacture it.
    /// </summary>
    public VerifiedKeyBinding? SenderKeyBinding { get; internal init; }

    /// <summary>Same-document signer key evidence (mirrors <see cref="UnpackResult.SignerKeyBinding"/>; #56).</summary>
    public VerifiedKeyBinding? SignerKeyBinding { get; internal init; }

    /// <summary>Same-document recipient key evidence (mirrors <see cref="UnpackResult.RecipientKeyBinding"/>; #56).</summary>
    public VerifiedKeyBinding? RecipientKeyBinding { get; internal init; }

    /// <summary>
    /// Outcome of the advisory FR-CONSIST-04 recipient-addressing check (mirrors
    /// <see cref="UnpackResult.RecipientAddressing"/>; #61), populated at construction only from
    /// the verified unpack snapshot — never from a caller-supplied result, so a synthetic
    /// <see cref="UnpackResult"/> reads <see cref="RecipientAddressing.NotEvaluated"/> whatever
    /// it claims. What is guaranteed is the pairing with <see cref="Message"/>: observer
    /// delivery materializes both from the same snapshot, so observers receive the verified
    /// plaintext together with the outcome computed for exactly that content, and
    /// <see cref="FromUnpackResult(UnpackResult)"/> resets to <c>NotEvaluated</c> when the live
    /// message no longer matches the verified content. A <c>with</c>-clone of an observation
    /// copies this value like any record member — it describes the message the observation was
    /// built from, not a <c>Message</c> transplanted afterwards. It is still a warning channel,
    /// not a trust signal: the <c>to</c> header is sender-authored, so act on
    /// <see cref="RecipientAddressing.NotAddressed"/> but never treat
    /// <see cref="RecipientAddressing.Addressed"/> as authorization — see
    /// <see cref="Facade.RecipientAddressing"/>'s trust-boundary remarks.
    /// </summary>
    public RecipientAddressing RecipientAddressing { get; internal init; }

    /// <summary>
    /// Build an observation from an unpack result, deep-cloning the message (serialize →
    /// deserialize through the DIDComm JSON options, so extension headers and attachments
    /// survive intact) so the observer can never reach the pipeline's live instance. When the
    /// verified unpack snapshot still covers the result's message content, the observation's
    /// trust metadata — the six positional members, the key bindings, and
    /// <see cref="RecipientAddressing"/> — is sourced from the snapshot; the supplied result's
    /// values apply only when no snapshot covers the content (#61).
    /// </summary>
    /// <param name="received">The unpack result for the inbound message.</param>
    public static InboundObservation FromUnpackResult(UnpackResult received)
        => FromUnpackResult(received, out _);

    /// <summary>
    /// As <see cref="FromUnpackResult(UnpackResult)"/>, also reporting the message clone's approximate
    /// exact UTF-8 size in bytes — retained as an internal compatibility helper. Normal observer
    /// delivery uses the verified unpack snapshot's exact plaintext byte count.
    /// </summary>
    /// <param name="received">The unpack result for the inbound message.</param>
    /// <param name="approxBytes">The serialized clone's exact UTF-8 byte count.</param>
    internal static InboundObservation FromUnpackResult(UnpackResult received, out int approxBytes)
    {
        ArgumentNullException.ThrowIfNull(received);
        // Serialize to a string once (measure it), then deserialize the snapshot: an immutable copy
        // taken AT ENQUEUE, so a handler or caller mutating the live message after dispatch cannot
        // change what an observer later sees.
        var json = JsonSerializer.Serialize(received.Message, DidCommJson.Default);
        approxBytes = Encoding.UTF8.GetByteCount(json);
        var clone = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;

        // Key bindings ride along only when this exact Message instance is still the one the unpack
        // pipeline verified AND its content still matches what was verified (#56). Object identity
        // alone is not enough: Message is mutable, and an in-place edit keeps the identity the
        // weak-table snapshot is keyed by — so a caller could rewrite a verified message's body or
        // 'from' and hand an observer content that Alice's binding never covered. Comparing the
        // re-serialized snapshot against the current serialization (same serializer both sides, so
        // only real content differences show) drops the evidence exactly when it stopped applying.
        InboundMessageSnapshot.TryGetFor(received.Message, out var snapshot);
        if (snapshot is not null
            && !string.Equals(JsonSerializer.Serialize(snapshot.DeserializeMessage(), DidCommJson.Default), json, StringComparison.Ordinal))
        {
            snapshot = null;
        }
        // When the verified snapshot still covers this content, the trust metadata comes from it
        // too — not just the bindings and addressing. The received result is a record whose flags
        // a with-clone can rewrite, and the addressing/binding evidence below is only safe to act
        // on when read together with the flags computed by the unpack that produced it (#61).
        // Explicit null-checks rather than ??: a verified null (e.g. no SenderKid) must not fall
        // back to a caller-supplied value.
        return new InboundObservation(
            Message: clone,
            Encrypted: snapshot is null ? received.Encrypted : snapshot.Encrypted,
            Authenticated: snapshot is null ? received.Authenticated : snapshot.Authenticated,
            NonRepudiation: snapshot is null ? received.NonRepudiation : snapshot.NonRepudiation,
            AnonymousSender: snapshot is null ? received.AnonymousSender : snapshot.AnonymousSender,
            SenderKid: snapshot is null ? received.SenderKid : snapshot.SenderKid,
            SignerKid: snapshot is null ? received.SignerKid : snapshot.SignerKid)
        {
            SenderKeyBinding = snapshot?.SenderKeyBinding,
            SignerKeyBinding = snapshot?.SignerKeyBinding,
            RecipientKeyBinding = snapshot?.RecipientKeyBinding,
            // Deliberately never received.RecipientAddressing: sourcing the value only from the
            // verified snapshot is what keeps a hand-built or mutated result from laundering an
            // addressing outcome into observers (#61).
            RecipientAddressing = snapshot?.RecipientAddressing ?? RecipientAddressing.NotEvaluated,
        };
    }

    /// <summary>
    /// Materialize one observer-private message clone from an immutable verified inbound snapshot.
    /// This runs on the observer's background pump only, after item and exact UTF-8 byte admission.
    /// </summary>
    internal static InboundObservation FromSnapshot(InboundMessageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new InboundObservation(
            Message: snapshot.DeserializeMessage(),
            Encrypted: snapshot.Encrypted,
            Authenticated: snapshot.Authenticated,
            NonRepudiation: snapshot.NonRepudiation,
            AnonymousSender: snapshot.AnonymousSender,
            SenderKid: snapshot.SenderKid,
            SignerKid: snapshot.SignerKid)
        {
            SenderKeyBinding = snapshot.SenderKeyBinding,
            SignerKeyBinding = snapshot.SignerKeyBinding,
            RecipientKeyBinding = snapshot.RecipientKeyBinding,
            RecipientAddressing = snapshot.RecipientAddressing,
        };
    }
}
