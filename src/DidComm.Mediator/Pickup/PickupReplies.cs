using DidComm.Exceptions;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Pickup;

/// <summary>
/// What the Message Pickup handlers of both versions (<see cref="MessagePickupHandler"/>, 3.0;
/// <see cref="MessagePickup40Handler"/>, 4.0) do the same way around a reply: the thread it is put
/// on, and the fault boundary of building it. Each handler passes its own logger, so a line keeps
/// the category of the handler that wrote it.
/// </summary>
internal static class PickupReplies
{
    /// <summary>
    /// The thread a reply to <paramref name="request"/> is put on (DD-11): its <c>thid</c> if
    /// present, else its <c>id</c>. Echoed, so bounded: a longer one makes the request malformed.
    /// </summary>
    /// <exception cref="MalformedMessageException">The thread id is over <see cref="PickupSizing.MaxThreadIdLength"/> characters, or not a valid thread id. The text quotes no value.</exception>
    public static string ThreadOf(Message request)
    {
        var thid = MessagePickup.ResponseThreadId(request);
        if (thid.Length > PickupSizing.MaxThreadIdLength)
            throw new MalformedMessageException($"A message-pickup request's thread id exceeds the maximum length of {PickupSizing.MaxThreadIdLength} characters.");
        return thid;
    }

    /// <summary>
    /// Build a reply. Building validates what goes on the wire; values that fail came from a host
    /// store, not from the peer, so any failure here is a server fault: logged by type on
    /// <paramref name="logger"/>, no reply, never thrown through dispatch.
    /// </summary>
    public static Message? Build(ILogger? logger, Message request, Func<Message> build)
    {
        try
        {
            return build();
        }
        catch (Exception ex)
        {
            MediatorLog.Write(logger, LogLevel.Error, "Message-pickup message {MessageId} not answered: the reply could not be built ({ExceptionType}).", MediatorLog.LogId(request.Id), ex.GetType().Name);
            return null;
        }
    }
}
