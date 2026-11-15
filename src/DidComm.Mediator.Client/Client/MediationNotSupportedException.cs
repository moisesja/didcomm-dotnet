namespace DidComm.Mediator.Client;

/// <summary>
/// The mediator's Discover Features answer named neither Coordinate Mediation 3.0 nor 2.0 in the
/// mediator role, so <see cref="MediationClient"/> has no version to speak to it (FR-MED-09,
/// FR-MED-10). When you know which version a mediator speaks, pin it with
/// <see cref="MediatorClientOptions.CoordinateMediationVersion"/> and no query is sent.
/// </summary>
/// <remarks>
/// What the mediator did disclose is on <see cref="DisclosedProtocols"/>, cleaned; the message text
/// carries only a count, never a value the mediator sent.
/// </remarks>
public sealed class MediationNotSupportedException : MediationProtocolException
{
    /// <summary>Initialize with the protocols the mediator disclosed.</summary>
    /// <param name="message">What went wrong, without any value from the wire.</param>
    /// <param name="requestId">The id of our Discover Features query.</param>
    /// <param name="disclosedProtocols">The disclosed protocol ids, already cleaned (see <see cref="DisclosedProtocols"/>). The caller cleans them.</param>
    internal MediationNotSupportedException(string message, string requestId, IReadOnlyList<string> disclosedProtocols)
        : base(message, requestId)
    {
        DisclosedProtocols = disclosedProtocols;
    }

    /// <summary>
    /// The protocols the mediator disclosed, cleaned: at most 32 distinct entries, each at most
    /// 256 printable ASCII characters and a valid protocol identifier URI, whatever role it was
    /// disclosed in (so a Coordinate Mediation version disclosed only in another role than mediator
    /// is listed). Anything else the mediator sent is left out.
    /// </summary>
    public IReadOnlyList<string> DisclosedProtocols { get; }
}
