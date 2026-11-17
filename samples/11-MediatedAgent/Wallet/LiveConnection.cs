using DidComm.Mediator.Client;
using DidComm.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace DidComm.Samples.MediatedAgent.Wallet;

/// <summary>
/// The wallet's WebSocket connection to its mediator: the mediator pushes messages over it as they
/// arrive while live delivery is on. Dispose it to close the connection.
/// </summary>
public sealed class LiveConnection : IAsyncDisposable
{
    private readonly string _name;
    private readonly Narrator _narrator;
    private readonly ServiceProvider _services;
    private readonly PickupClient _pickup;
    private readonly string _mediatorDid;
    private readonly string _did;
    private readonly Uri _endpoint;
    private readonly CancellationTokenSource _stop = new();
    private IAsyncEnumerator<PickupItem>? _stream;

    internal LiveConnection(string name, Narrator narrator, ServiceProvider services, string mediatorDid, string did, Uri endpoint)
    {
        _name = name;
        _narrator = narrator;
        _services = services;
        _mediatorDid = mediatorDid;
        _did = did;
        _endpoint = endpoint;
        _pickup = services.GetRequiredService<PickupClient>();
    }

    /// <summary>
    /// Turn live delivery on, then off again, to show the switch. <c>ReceiveLiveAsync</c> does this
    /// itself; call <c>SetLiveDeliveryAsync</c> only when you handle the pushes some other way.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task ShowLiveSwitchAsync(CancellationToken ct = default)
    {
        // The mediator's DID document lists its HTTP address first, so pass its WebSocket address
        // explicitly: live delivery needs a connection that stays open.
        var on = await _pickup.SetLiveDeliveryAsync(_mediatorDid, _did, true, _endpoint, ct).ConfigureAwait(false);
        _narrator.Step($"[{_name}] live delivery = {on.LiveDelivery}");
        var off = await _pickup.SetLiveDeliveryAsync(_mediatorDid, _did, false, _endpoint, ct).ConfigureAwait(false);
        _narrator.Step($"[{_name}] live delivery = {off.LiveDelivery} again (ReceiveLiveAsync turns it on and off itself)");
    }

    /// <summary>
    /// The next message, live. The first call starts the stream: it turns live delivery on, takes
    /// what is already queued, then waits for what the mediator pushes. Asking for the next message
    /// acknowledges the previous one.
    /// </summary>
    public async Task<PickupItem> ReceiveNextAsync()
    {
        _stream ??= _pickup.ReceiveLiveAsync(_mediatorDid, _did, _endpoint, _stop.Token).GetAsyncEnumerator(_stop.Token);
        if (!await _stream.MoveNextAsync().ConfigureAwait(false))
            throw new InvalidOperationException("The live stream ended.");
        return _stream.Current;
    }

    /// <summary>
    /// Stop receiving, then acknowledge the last message: the stream acknowledges a message only
    /// when the next one is asked for, so the last one would otherwise come back.
    /// </summary>
    /// <param name="last">The last message the stream returned.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StopAndAcknowledgeAsync(PickupItem last, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(last);
        if (_stream is { } stream)
        {
            _stream = null;
            await stream.DisposeAsync().ConfigureAwait(false); // the stream turns live delivery off
        }
        var status = await _pickup.AckAsync(_mediatorDid, _did, new[] { last.Id }, _endpoint, ct).ConfigureAwait(false);
        _narrator.Step($"[{_name}] acknowledged the last message; status after ack: {status.MessageCount} message(s)");
    }

    /// <summary>Close the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_stream is { } stream)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped on purpose.
            }
        }
        await _services.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
