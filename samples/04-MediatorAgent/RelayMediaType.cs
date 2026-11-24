using System.Text.Json;
using DidComm.Messages;

namespace DidComm.Samples.MediatorAgent;

/// <summary>
/// The media type a relay sends a forward's payload under: the media type of the payload's own
/// envelope.
/// </summary>
/// <remarks>
/// <para>
/// A forward's payload is the message for the next hop as its sender packed it. It should be
/// encrypted, but a sender may wrap a signed or a plaintext message (routing.md: the attached
/// message "should be encrypted"). The receiver checks that the media type a message is declared
/// with is its envelope's ("The IANA media type for the POST request MUST be set to the
/// corresponding media type", transports.md), and this library's endpoints answer <c>415</c> when
/// it is not. So a relay must not label every payload as encrypted: it labels each one by its own
/// envelope.
/// </para>
/// <para>
/// The rule is the structural one receivers use: a JSON object with a <c>ciphertext</c> member is
/// encrypted (a JWE); one with a <c>signatures</c> array, or with both <c>signature</c> and
/// <c>payload</c>, is signed (a JWS); any other is plaintext. A payload that is not a JSON object is
/// labelled plaintext too: the receiver refuses it whatever its label, as it refuses any body that
/// is not a DIDComm message. The payload is already bounded by the mediator's receive limit.
/// </para>
/// </remarks>
public static class RelayMediaType
{
    /// <summary>The media type of <paramref name="payload"/>'s own envelope.</summary>
    /// <param name="payload">The onward bytes of a forward, as its sender packed them.</param>
    /// <returns><see cref="MediaTypes.Encrypted"/>, <see cref="MediaTypes.Signed"/> or <see cref="MediaTypes.Plaintext"/>.</returns>
    public static string Of(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("ciphertext", out _))
                    return MediaTypes.Encrypted;
                var general = root.TryGetProperty("signatures", out var signatures) && signatures.ValueKind == JsonValueKind.Array;
                var flattened = root.TryGetProperty("signature", out _) && root.TryGetProperty("payload", out _);
                if (general || flattened)
                    return MediaTypes.Signed;
            }
        }
        catch (JsonException)
        {
            // Not JSON: labelled plaintext below; the receiver refuses it either way.
        }

        return MediaTypes.Plaintext;
    }
}
