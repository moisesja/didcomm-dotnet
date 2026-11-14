namespace DidComm.Mediator.Pipeline;

/// <summary>
/// Registered once by <c>AddMediator()</c>. Its presence tells <c>MapDidCommMediator()</c> that the
/// mediator was registered, and tells a repeated <c>AddMediator()</c> call which registrations are
/// its own.
/// </summary>
internal sealed class MediatorMarker
{
}
