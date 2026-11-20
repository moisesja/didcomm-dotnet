using System.Text.Json;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Json;

namespace DidComm.Mediator.Pickup;

/// <summary>
/// The one definition of "a DIDComm message in encrypted form", the only kind of message a Message
/// Pickup delivery may carry (3.0 L172 "The ONLY valid type of attachment for this message is a
/// DIDComm v2 Message in encrypted form."; 4.0 L261, which adds v1). Both roles judge by it: the
/// mediator's mailbox refuses to store anything else (<c>PickupDeliveryOutcome.NotEncrypted</c>),
/// and the wallet's pickup client refuses to unpack or acknowledge a delivered attachment that is
/// not (#100 F-2).
/// </summary>
/// <remarks>
/// A message is in encrypted form when its outermost layer is a JWE, anoncrypt or authcrypt: "A
/// DIDComm encrypted message is an encrypted JWM" (DIDComm v2.1 message_structure.md L65), whose
/// media type goes in "the <c>typ</c> property of the JWE" (L71). The judgement is structural and
/// nothing is decrypted: the bytes must be strict UTF-8 JSON (no duplicate member names, nothing
/// after the value) holding an object that <see cref="EnvelopeDetector"/> classifies as
/// <see cref="EnvelopeKind.Encrypted"/>, which means it has a <c>ciphertext</c> member. That is the
/// rule the library's unpack uses to classify a JWE (FR-API-03), so the mailbox, the pickup client
/// and every unpacker agree on what counts. Plaintext (a JSON object without <c>ciphertext</c>), a
/// signed-only message (a JWS, General or Flattened, without <c>ciphertext</c>), any JSON value that
/// is not an object, and bytes that are not UTF-8 JSON are not in encrypted form. Only the outermost
/// layer is judged: a message a mediator re-wrapped is an anoncrypted <c>forward</c>, a JWE,
/// whatever it carries inside.
/// </remarks>
internal static class EncryptedForm
{
    /// <summary>Whether <paramref name="payload"/> is a DIDComm message in encrypted form (its outermost layer is a JWE).</summary>
    /// <param name="payload">The message's bytes, exactly as stored or delivered. The parse reads all of them, so the caller bounds their size first.</param>
    /// <returns><c>true</c> for a JWE; <c>false</c> for anything else, including bytes that do not parse.</returns>
    internal static bool IsEncrypted(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, DidCommJson.StrictDocument);
            return EnvelopeDetector.Detect(document.RootElement) == EnvelopeKind.Encrypted;
        }
        catch (Exception ex) when (ex is JsonException or MalformedMessageException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
