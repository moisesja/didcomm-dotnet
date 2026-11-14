namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The refusal of a request because <see cref="MediatorClientOptions.MaxPendingRequests"/> requests
/// are already waiting for an answer: nothing was sent. It is an <see cref="InvalidOperationException"/>
/// to callers (the documented type); its own type lets the live stream tell this transient refusal
/// apart from the send path's configuration refusals, which no retry fixes.
/// </summary>
internal sealed class MediatorBusyException : InvalidOperationException
{
    /// <summary>Create the refusal.</summary>
    /// <param name="message">The text: our own request id, the bound and the exchange name only.</param>
    internal MediatorBusyException(string message)
        : base(message)
    {
    }
}
