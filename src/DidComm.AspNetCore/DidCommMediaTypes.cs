namespace DidComm.AspNetCore;

/// <summary>
/// Canonical DIDComm v2.1 media-type strings used by the ASP.NET Core integration on both
/// the HTTP and WebSocket receive paths.
/// </summary>
internal static class DidCommMediaTypes
{
    internal const string Encrypted = "application/didcomm-encrypted+json";
    internal const string Signed = "application/didcomm-signed+json";
    internal const string Plain = "application/didcomm-plain+json";
}
