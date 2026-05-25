namespace DidComm.Protocols.Routing;

/// <summary>
/// Constants for the DIDComm v2.1 Routing Protocol 2.0 (PRD §8, FR-ROUTE-01). Centralised so the
/// <see cref="ForwardMessage"/> builder, the sender-side wrapping path, and the mediator-side
/// processor agree on identifiers without scattering magic strings.
/// </summary>
public static class ForwardConstants
{
    /// <summary>Protocol Identifier URI (PIURI) for Routing Protocol 2.0.</summary>
    public const string ProtocolIdentifier = "https://didcomm.org/routing/2.0";

    /// <summary>
    /// Message Type URI (MTURI) for the <c>forward</c> message — the only message defined in
    /// Routing Protocol 2.0 (spec §Routing Protocol 2.0 / Messages).
    /// </summary>
    public const string ForwardTypeUri = "https://didcomm.org/routing/2.0/forward";

    /// <summary>
    /// Media type used for forward-message attachments carrying packed (encrypted) DIDComm
    /// envelopes. Matches <see cref="Messages.MediaTypes.Encrypted"/>; aliased here so the
    /// routing layer reads as one self-contained set of constants.
    /// </summary>
    public const string PayloadMediaType = Messages.MediaTypes.Encrypted;
}
