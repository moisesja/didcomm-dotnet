namespace DidComm.Transports.WebSocket;

/// <summary>
/// Lifecycle event surfaced by <see cref="WebSocketDidCommTransport"/> for observability
/// (FR-TRN-11 "Expose lifecycle events"). Use this with the
/// <see cref="WebSocketDidCommTransport.Lifecycle"/> event to integrate with the host
/// application's logging or metrics pipeline.
/// </summary>
public enum WebSocketLifecycleEventKind
{
    /// <summary>The transport opened a new WebSocket to the endpoint.</summary>
    Connected,
    /// <summary>The transport disconnected (clean close or drop).</summary>
    Disconnected,
    /// <summary>The transport failed a send and will retry per the reconnect policy.</summary>
    SendFailed,
    /// <summary>The transport recovered after one or more reconnect attempts.</summary>
    Reconnected,
}

/// <summary>Payload for the <see cref="WebSocketDidCommTransport.Lifecycle"/> event.</summary>
public sealed class WebSocketLifecycleEventArgs : EventArgs
{
    /// <summary>What happened.</summary>
    public WebSocketLifecycleEventKind Kind { get; }

    /// <summary>The endpoint URI the event relates to.</summary>
    public Uri Endpoint { get; }

    /// <summary>Optional exception attached to the event (e.g. on SendFailed).</summary>
    public Exception? Exception { get; }

    /// <summary>Initialize.</summary>
    /// <param name="kind">The lifecycle event kind.</param>
    /// <param name="endpoint">The endpoint URI.</param>
    /// <param name="exception">Optional underlying exception.</param>
    public WebSocketLifecycleEventArgs(WebSocketLifecycleEventKind kind, Uri endpoint, Exception? exception = null)
    {
        Kind = kind;
        Endpoint = endpoint;
        Exception = exception;
    }
}
