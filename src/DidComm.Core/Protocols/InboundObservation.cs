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
/// <strong>Where the trust metadata comes from.</strong> Normal observer delivery for a result
/// produced by <see cref="DidCommClient.UnpackAsync"/> is materialized entirely from the immutable
/// verified unpack snapshot. The public dispatcher also accepts caller-created synthetic results
/// for compatibility; observer delivery for that path uses a fallback snapshot carrying only the
/// caller's six positional metadata members, with null bindings and
/// <see cref="RecipientAddressing.NotEvaluated"/>. Those synthetic values are caller claims, not
/// cryptographic evidence.
/// <see cref="FromUnpackResult(UnpackResult)"/> distinguishes three states. When a verified
/// snapshot still covers the supplied message content, all six positional trust members
/// (<see cref="Encrypted"/> through <see cref="SignerKid"/>), all three key bindings, and
/// <see cref="RecipientAddressing"/> come from that snapshot. When a verified snapshot exists
/// but the current content has diverged, every trust member is neutralized: all flags are false,
/// both kids and all bindings are null, and addressing is
/// <see cref="RecipientAddressing.NotEvaluated"/>. Only when no verified snapshot ever existed
/// (the compatibility path for a caller-created synthetic result) are the six positional members
/// copied from the caller; bindings remain null and addressing remains <c>NotEvaluated</c>. A
/// record <c>with</c>-clone copies every member verbatim, and direct mutation changes only the
/// mutable <see cref="Message"/>. In either case the stored metadata continues to describe the
/// message the observation was built from, not content substituted afterwards.
/// </para>
/// </remarks>
/// <param name="Message">A deep clone of the unpacked inbound message. Mutating it affects only this observation.</param>
/// <param name="Encrypted">Whether a covering verified snapshot establishes an encryption layer, or the synthetic caller claims one; false after verified-content divergence.</param>
/// <param name="Authenticated">Whether a covering verified snapshot establishes cryptographic sender authentication, or the synthetic caller claims it; false after verified-content divergence. Synthetic claims are not evidence. Observers that act on sender identity MUST require verified provenance.</param>
/// <param name="NonRepudiation">Whether a covering verified snapshot establishes a non-repudiable signature, or the synthetic caller claims one; false after verified-content divergence. Synthetic claims are not evidence.</param>
/// <param name="AnonymousSender">Whether a covering verified snapshot establishes that the envelope hid the sender, or the synthetic caller claims it; false after verified-content divergence.</param>
/// <param name="SenderKid">The snapshot-verified authcrypt sender key id (<c>skid</c>), or a caller-supplied claim on the synthetic compatibility path; null after verified-content divergence.</param>
/// <param name="SignerKid">The snapshot-verified JWS signer key id, or a caller-supplied claim on the synthetic compatibility path; null after verified-content divergence.</param>
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
    /// captured provenance and whose snapshot still covers the current content. Synthetic or
    /// verified-but-diverged results always produce null; caller claims never supply this value.
    /// </summary>
    public VerifiedKeyBinding? SenderKeyBinding { get; internal init; }

    /// <summary>
    /// Same-document signer key evidence from a covering verified snapshot (#56). Synthetic and
    /// verified-but-diverged results always produce null; caller claims never supply this value.
    /// </summary>
    public VerifiedKeyBinding? SignerKeyBinding { get; internal init; }

    /// <summary>
    /// Same-document recipient key evidence from a covering verified snapshot (#56). Synthetic and
    /// verified-but-diverged results always produce null; caller claims never supply this value.
    /// </summary>
    public VerifiedKeyBinding? RecipientKeyBinding { get; internal init; }

    /// <summary>
    /// Outcome of the advisory FR-CONSIST-04 recipient-addressing check (mirrors
    /// <see cref="UnpackResult.RecipientAddressing"/>; #61), populated at construction only from
    /// a verified unpack snapshot that still covers the current content — never from a
    /// caller-supplied result. Synthetic results and verified results whose message has diverged
    /// therefore read <see cref="RecipientAddressing.NotEvaluated"/> whatever they claim. What
    /// is guaranteed is the pairing with <see cref="Message"/>: observer delivery materializes
    /// both from the same snapshot, so observers receive the verified plaintext together with
    /// the outcome computed for exactly that content. A <c>with</c>-clone of an observation
    /// copies this value like any record member, and direct mutation does not recompute it — it
    /// describes the message the observation was built from, not content substituted afterwards.
    /// It is still a warning channel,
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
    /// <see cref="RecipientAddressing"/> — is sourced from the snapshot. If verified content has
    /// diverged, all of those members are neutralized. Only a result that never had a verified
    /// snapshot supplies the six positional members; its bindings remain null and its addressing
    /// remains <see cref="RecipientAddressing.NotEvaluated"/> (#61).
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
        var hasVerifiedSnapshot = InboundMessageSnapshot.TryGetFor(received.Message, out var snapshot);
        var snapshotCoversContent = hasVerifiedSnapshot
            && string.Equals(
                JsonSerializer.Serialize(snapshot.DeserializeMessage(), DidCommJson.Default),
                json,
                StringComparison.Ordinal);
        // When the verified snapshot still covers this content, the trust metadata comes from it
        // too — not just the bindings and addressing. The received result is a record whose flags
        // a with-clone can rewrite, and the addressing/binding evidence below is only safe to act
        // on when read together with the flags computed by the unpack that produced it (#61).
        // Keep verified divergence distinct from the synthetic compatibility path. Once content
        // has diverged, none of the unpack's trust statements cover it, and caller-supplied values
        // must not be allowed to replace them. Explicit state checks rather than ??: a verified
        // null (e.g. no SenderKid) must not fall back to a caller-supplied value.
        return new InboundObservation(
            Message: clone,
            Encrypted: snapshotCoversContent ? snapshot.Encrypted : !hasVerifiedSnapshot && received.Encrypted,
            Authenticated: snapshotCoversContent ? snapshot.Authenticated : !hasVerifiedSnapshot && received.Authenticated,
            NonRepudiation: snapshotCoversContent ? snapshot.NonRepudiation : !hasVerifiedSnapshot && received.NonRepudiation,
            AnonymousSender: snapshotCoversContent ? snapshot.AnonymousSender : !hasVerifiedSnapshot && received.AnonymousSender,
            SenderKid: snapshotCoversContent ? snapshot.SenderKid : hasVerifiedSnapshot ? null : received.SenderKid,
            SignerKid: snapshotCoversContent ? snapshot.SignerKid : hasVerifiedSnapshot ? null : received.SignerKid)
        {
            SenderKeyBinding = snapshotCoversContent ? snapshot.SenderKeyBinding : null,
            SignerKeyBinding = snapshotCoversContent ? snapshot.SignerKeyBinding : null,
            RecipientKeyBinding = snapshotCoversContent ? snapshot.RecipientKeyBinding : null,
            // Deliberately never received.RecipientAddressing: sourcing the value only from the
            // verified snapshot is what keeps a hand-built or mutated result from laundering an
            // addressing outcome into observers (#61).
            RecipientAddressing = snapshotCoversContent
                ? snapshot.RecipientAddressing
                : RecipientAddressing.NotEvaluated,
        };
    }

    /// <summary>
    /// Materialize one observer-private message clone from an immutable inbound snapshot. Normal
    /// receive delivery supplies a verified snapshot; the public dispatcher's synthetic
    /// compatibility path supplies a fallback snapshot whose positional metadata is caller-claimed
    /// and whose bindings/addressing are neutral. This runs on the observer's background pump only,
    /// after item and exact UTF-8 byte admission.
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
