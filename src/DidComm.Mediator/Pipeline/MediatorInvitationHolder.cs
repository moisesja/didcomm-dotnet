namespace DidComm.Mediator.Pipeline;

/// <summary>
/// Holds the mediator's out-of-band invitation once the mediator has started and checked its
/// routing DIDs, for the invitation endpoint to serve. A singleton; reads and writes are
/// thread-safe.
/// </summary>
internal sealed class MediatorInvitationHolder
{
    private volatile string? _plaintextJson;

    /// <summary>The invitation as a packed plaintext message; <c>null</c> until the mediator has started.</summary>
    public string? PlaintextJson
    {
        get => _plaintextJson;
        set => _plaintextJson = value;
    }
}
