namespace DidComm.Profiles;

/// <summary>
/// Well-known DIDComm profile identifiers. A profile is a bundle of envelope / signing /
/// plaintext / routing choices advertised in a service endpoint's <c>accept</c> array and
/// negotiated by <see cref="ProfileNegotiator"/> (FR-PROF-01).
/// </summary>
public static class Profiles
{
    /// <summary>The DIDComm Messaging v2 profile — the only one this library emits.</summary>
    public const string DidCommV2 = "didcomm/v2";

    /// <summary>The legacy DIDComm v1 profile — recognized on receive only, never advertised.</summary>
    public const string DidCommAip1 = "didcomm/aip1";

    /// <summary>The DIDComm v2 profile advertised by Aries / Hyperledger libraries; treated as an alias of <see cref="DidCommV2"/>.</summary>
    public const string DidCommAip2Env10 = "didcomm/aip2;env=rfc587";
}
