using System.Diagnostics;
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
    private GuardedStreamReader<PickupItem>? _stream;

    /// <summary>How long one live read may take before the sample fails instead of hanging.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

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
    /// <param name="what">What is being waited for, for the error message.</param>
    /// <exception cref="TimeoutException">Nothing arrived within <see cref="HangGuard"/>; the read has been cancelled and has ended.</exception>
    public Task<PickupItem> ReceiveNextAsync(string what = "the next live message")
    {
        // An await foreach over ReceiveLiveAsync is the usual way to read the stream; the sample reads
        // one item at a time with a hang guard, and GuardedStreamReader never disposes the stream while
        // a read is still running.
        _stream ??= new GuardedStreamReader<PickupItem>(
            token => _pickup.ReceiveLiveAsync(_mediatorDid, _did, _endpoint, token), HangGuard);
        return _stream.NextAsync(what);
    }

    /// <summary>How long the wallet waits for the mediator to apply its last acknowledgement before it gives up.</summary>
    public static readonly TimeSpan AcknowledgementWait = TimeSpan.FromSeconds(10);

    // How long the wallet pauses between two status checks while it waits.
    private static readonly TimeSpan StatusPollDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Stop receiving, then acknowledge the last message: the stream acknowledges a message only
    /// when the next one is asked for, so the last one would otherwise come back. When the mediator
    /// sends no answer to the acknowledgement, wait until its status no longer counts the message,
    /// so the connection is not closed before the mediator has applied it.
    /// </summary>
    /// <param name="last">The last message the stream returned.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The mediator's status still counted the message after <see cref="AcknowledgementWait"/>.</exception>
    public async Task StopAndAcknowledgeAsync(PickupItem last, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(last);
        if (_stream is { } stream)
        {
            _stream = null;
            await stream.DisposeAsync().ConfigureAwait(false); // the stream turns live delivery off
        }
        // A status counts the messages that were delivered but not acknowledged yet, so this one is
        // still in it (FR-PICK-02).
        var before = await _pickup.GetStatusAsync(_mediatorDid, _did, serviceEndpointOverride: _endpoint, ct: ct).ConfigureAwait(false);

        // The mediator's status comes back only when it answers the acknowledgement with one. Under
        // Message Pickup 4.0 it never does, so the answer is null.
        var answer = await _pickup.AckAsync(_mediatorDid, _did, new[] { last.Id }, _endpoint, ct).ConfigureAwait(false);
        _narrator.Step(answer is null
            ? $"[{_name}] acknowledged the last message; the mediator sent no status in answer"
            : $"[{_name}] acknowledged the last message; status in answer: {answer.MessageCount} message(s)");
        if (answer is not null)
            return; // the mediator's own answer, so it already counts the queue after the acknowledgement

        // Under Message Pickup 4.0 nothing answers an acknowledgement, so the wallet cannot tell
        // when the mediator has applied it. If the connection closed first, the mediator could drop
        // the acknowledgement with the connection and deliver the message again later. So before
        // going offline, the wallet waits until its status stops counting the message.
        var after = await WaitUntilCountedLessThanAsync(before.MessageCount, ct).ConfigureAwait(false);
        _narrator.Step($"[{_name}] the mediator applied the acknowledgement (GetStatusAsync counts {after} message(s))");
    }

    // Ask for the status until it counts fewer than `countBefore` messages; give up after
    // AcknowledgementWait. Returns the count that was lower.
    private async Task<int> WaitUntilCountedLessThanAsync(int countBefore, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var status = await _pickup.GetStatusAsync(_mediatorDid, _did, serviceEndpointOverride: _endpoint, ct: ct).ConfigureAwait(false);
            if (status.MessageCount < countBefore)
                return status.MessageCount;
            if (waited.Elapsed >= AcknowledgementWait)
            {
                throw new InvalidOperationException(
                    $"The mediator had not applied the acknowledgement after {AcknowledgementWait.TotalSeconds:0} seconds: its status still counts {status.MessageCount} message(s).");
            }
            await Task.Delay(StatusPollDelay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Close the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stream is { } stream)
        {
            _stream = null;
            await stream.DisposeAsync().ConfigureAwait(false); // waits for a read still running, first
        }
        await _services.DisposeAsync().ConfigureAwait(false);
    }
}
