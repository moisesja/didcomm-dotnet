namespace DidComm.Protocols.OutOfBand;

/// <summary>
/// A <c>web_redirect</c> decorator (FR-OOB-05) carried on a concluding ack / problem-report
/// message. After an out-of-band-initiated protocol finishes, the sender can ask the receiver
/// to redirect back to a web destination — e.g. a verifier showing a results page once a
/// present-proof exchange completes.
/// </summary>
/// <param name="Status">Outcome status, e.g. <c>"OK"</c> or <c>"FAIL"</c> (aries-rfc 0700).</param>
/// <param name="RedirectUrl">Absolute URL the receiver may navigate to once the protocol ends.</param>
public sealed record WebRedirect(string Status, string RedirectUrl);
