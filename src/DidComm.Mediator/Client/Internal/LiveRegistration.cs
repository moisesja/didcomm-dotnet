using System.Threading.Channels;
using DidComm.Protocols;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// One open live stream's mailbox in the channel's live registry (design D-18, §4.8): the
/// thid-less <c>delivery</c> pushes the matcher routes to the (mediator, local DID) pair, buffered
/// in a bounded queue the receive path writes to without ever waiting. A push that finds the queue
/// full (or the stream ended) is dropped by <see cref="TryPush"/> returning <c>false</c>: it was
/// never acknowledged, so the mediator delivers it again after the session closes or its lease
/// ends, and the stream's next drain picks it up. Disposing ends the queue and removes this exact
/// entry from the registry (the callback receives this instance, so a newer registration for the
/// same pair is never removed). Owner: engineer A (the registry); engineer C reads
/// <see cref="Pushes"/>.
/// </summary>
internal sealed class LiveRegistration : IDisposable
{
    /// <summary>How many pushes one stream buffers before new ones are dropped.</summary>
    internal const int PushCapacity = 8;

    private readonly Channel<InboundMessageSnapshot> _pushes;
    private readonly Action<LiveRegistration>? _onDispose;
    private int _disposed;

    /// <summary>Create a registration.</summary>
    /// <param name="mediatorSubject">The mediator's DID subject.</param>
    /// <param name="localSubject">The local DID subject the stream receives for.</param>
    /// <param name="onDispose">Removes this exact entry from the registry; runs once, on the first dispose.</param>
    /// <param name="capacity">The push buffer bound.</param>
    internal LiveRegistration(string mediatorSubject, string localSubject, Action<LiveRegistration>? onDispose, int capacity = PushCapacity)
    {
        ArgumentNullException.ThrowIfNull(mediatorSubject);
        ArgumentNullException.ThrowIfNull(localSubject);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        MediatorSubject = mediatorSubject;
        LocalSubject = localSubject;
        Key = KeyFor(mediatorSubject, localSubject);
        _onDispose = onDispose;
        // Wait mode with TryWrite only: a full queue makes TryWrite return false (the drop is
        // visible to the writer) and never blocks. Continuations never run on the writer's thread,
        // which is the dispatcher's receive path.
        _pushes = Channel.CreateBounded<InboundMessageSnapshot>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>The registry key of a (mediator, local DID) pair: the two DID subjects joined by a line feed, which no DID contains.</summary>
    /// <param name="mediatorSubject">The mediator's DID subject.</param>
    /// <param name="localSubject">The local DID subject.</param>
    internal static string KeyFor(string mediatorSubject, string localSubject) => mediatorSubject + "\n" + localSubject;

    /// <summary>The registry key (<see cref="KeyFor"/>).</summary>
    internal string Key { get; }

    /// <summary>The mediator's DID subject.</summary>
    internal string MediatorSubject { get; }

    /// <summary>The local DID subject the stream receives for.</summary>
    internal string LocalSubject { get; }

    /// <summary>The buffered pushes, oldest first. Completes when the registration is disposed or <see cref="Complete"/> is called.</summary>
    internal ChannelReader<InboundMessageSnapshot> Pushes => _pushes.Reader;

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Buffer a push for the stream. Never blocks; <c>false</c> when the buffer is full or the stream has ended (the push is dropped, not acknowledged).</summary>
    /// <param name="snapshot">The verified snapshot of the thid-less <c>delivery</c>.</param>
    internal bool TryPush(InboundMessageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return _pushes.Writer.TryWrite(snapshot);
    }

    /// <summary>End the queue without removing the entry (the channel's own disposal, which clears the registry itself); a reader then sees <paramref name="error"/> once the buffer is empty.</summary>
    /// <param name="error">The error readers observe (for example <see cref="ObjectDisposedException"/>), or <c>null</c> for a normal end.</param>
    internal void Complete(Exception? error = null) => _pushes.Writer.TryComplete(error);

    /// <summary>End the queue and remove this exact entry from the registry. Idempotent. Buffered pushes are dropped unacknowledged.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _pushes.Writer.TryComplete();
        _onDispose?.Invoke(this);
    }
}
