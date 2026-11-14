using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The one correlation channel of a container (design D-2, §3.2, §4.1–4.3): the bounded pending
/// map, the matcher (identity before lookup, D-7), route resolution, the live-stream registry, the
/// duplicate filter, the version cache and the unpack limiter. It is the
/// <see cref="IInboundCorrelator"/> the dispatcher hands every verified inbound to (DI only), and
/// the <see cref="IMediatorExchange"/> both facades are built over. Owner: engineer A.
/// </summary>
/// <remarks>
/// <para>
/// <strong>DI.</strong> <c>AddMediatorClient()</c> registers one instance as a singleton and as an
/// <see cref="IInboundCorrelator"/>; the constructor resolves nothing (L-017). The container
/// disposes it once per registration it was resolved through, so disposal is idempotent.
/// </para>
/// <para>
/// <strong>Non-DI.</strong> Each public facade constructor builds a private instance that is not a
/// correlator (<c>receivesInbound: false</c>): HTTP works, a WebSocket route is refused (D-4).
/// </para>
/// </remarks>
internal sealed class MediatorClientChannel : IInboundCorrelator, IMediatorExchange, IDisposable, IAsyncDisposable
{
    /// <summary>The bound on open live streams per channel.</summary>
    internal const int MaxLiveStreams = 16;

    private int _disposed;

    /// <summary>Build a channel; resolves and starts nothing.</summary>
    /// <param name="client">The facade requests are sent through and deliveries unpacked with.</param>
    /// <param name="options">The settings; copied and validated here.</param>
    /// <param name="receivesInbound">Whether answers can reach this channel through the dispatcher: <c>true</c> under DI with an <c>IInboundFrameConsumer</c> registered (<c>UseInboundDispatch()</c>). A WebSocket route is refused when <c>false</c>.</param>
    /// <param name="time">The clock of every deadline and interval.</param>
    /// <param name="loggerFactory">The host's logger factory, or <c>null</c> for no logging.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/>, <paramref name="options"/> or <paramref name="time"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting of <paramref name="options"/> is outside its range.</exception>
    internal MediatorClientChannel(DidCommClient client, MediatorClientOptions options, bool receivesInbound, TimeProvider time, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        Client = client;
        Options = options.ValidatedCopy();
        ReceivesInbound = receivesInbound;
        Time = time;
        Logger = loggerFactory?.CreateLogger<MediatorClientChannel>();
        Duplicates = new DuplicateFilter(Options.DedupeCapacity);
        Versions = new NegotiatedVersionCache(time);
        Unpacker = new DeliveredMessageUnpacker(client.UnpackDeliveredAsync, Options.UnpackTimeout, time);
    }

    /// <summary>The facade requests are sent through.</summary>
    internal DidCommClient Client { get; }

    /// <summary>Whether answers can reach this channel through the dispatcher (see the constructor).</summary>
    internal bool ReceivesInbound { get; }

    /// <summary>The channel's logger (category <c>DidComm.Mediator.Client.Internal.MediatorClientChannel</c>), or <c>null</c>.</summary>
    internal ILogger? Logger { get; }

    /// <summary>The unpack limiter behind <see cref="UnpackDeliveredAsync"/> and <see cref="UnpackDeliveredBatchAsync"/>.</summary>
    internal DeliveredMessageUnpacker Unpacker { get; }

    /// <summary>Whether <see cref="Dispose"/> or <see cref="DisposeAsync"/> has run.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc/>
    public MediatorClientOptions Options { get; }

    /// <inheritdoc/>
    public TimeProvider Time { get; }

    /// <inheritdoc/>
    public DuplicateFilter Duplicates { get; }

    /// <inheritdoc/>
    public NegotiatedVersionCache Versions { get; }

    /// <inheritdoc/>
    public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
        => throw new NotImplementedException("#96 engineer A: MediatorClientChannel.ResolveRouteAsync (design §4.3).");

    /// <inheritdoc/>
    public Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
        => throw new NotImplementedException("#96 engineer A: MediatorClientChannel.ExchangeAsync (design §4.1).");

    /// <inheritdoc/>
    public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid)
        => throw new NotImplementedException("#96 engineer A: MediatorClientChannel.RegisterLiveStream (design D-18).");

    /// <inheritdoc/>
    public Task<DeliveredOutcome> UnpackDeliveredAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => throw new NotImplementedException("#96 engineer A: MediatorClientChannel.UnpackDeliveredAsync (design D-19).");

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
        => throw new NotImplementedException("#96 engineer A: MediatorClientChannel.UnpackDeliveredBatchAsync (design D-19).");

    /// <summary>
    /// The matcher's unbound entry point (design §4.2): fast path when idle, identity before any
    /// lookup, then complete the pending request the snapshot answers or hand a thid-less delivery
    /// to its live stream. Never awaits, never parses a body.
    /// </summary>
    /// <param name="received">The verified snapshot the dispatcher created at unpack.</param>
    /// <returns><c>true</c> when the snapshot completed a pending request or reached a live stream.</returns>
    bool IInboundCorrelator.OnInbound(InboundMessageSnapshot received)
        => throw new NotImplementedException("#96 engineer A: MediatorClientChannel.OnInbound (design §4.2).");

    /// <summary>
    /// Stop: pending requests fail with <see cref="ObjectDisposedException"/> and live streams end.
    /// Idempotent (the container disposes this instance once per registration it was resolved
    /// through).
    /// </summary>
    public void Dispose()
    {
        // #96 engineer A: fail pending requests with ObjectDisposedException, complete every live
        // registration. The stub only records the call: the container disposes the singleton, so a
        // throwing stub would break every container that registers the client.
        Interlocked.Exchange(ref _disposed, 1);
    }

    /// <summary>Stop, as <see cref="Dispose"/> does.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
