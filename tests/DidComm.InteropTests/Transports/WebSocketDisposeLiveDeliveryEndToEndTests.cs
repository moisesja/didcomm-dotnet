using System.Net;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.InteropTests.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #100 OD-7: a message the mediator pushes live while the client's dispose is waiting for the
/// mediator's close is dropped by the client without being acknowledged, so under Message Pickup 4.0
/// the mediator keeps it queued for a later pickup. The real <c>PickupClient</c> live stream over a
/// real <see cref="ClientWebSocket"/> to this library's mediator on Kestrel. The mediator handles a
/// connection's messages in order; holding the stream's acknowledgement of the first message at its
/// store keeps it from reading dispose's close, so the second push arrives during the close wait.
/// </summary>
public sealed class WebSocketDisposeLiveDeliveryEndToEndTests
{
    private static readonly TimeSpan Guard = DeadlineKestrelHost.Guard;

    [Fact]
    public async Task A_live_push_that_arrives_during_disposes_close_wait_is_never_acknowledged_and_stays_queued()
    {
        AckDelayingStore store = null!;
        var observer = new PickupWireObserver();
        await using var host = await DeadlineKestrelHost.StartAsync(o => o.Builder = b =>
        {
            AckDelayingStore.Register(b.Services, x => store = x);
            b.Services.AddSingleton<IProtocolObserver>(observer);
        });
        var bob = await host.EnrolAsync();
        var closeSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recipient = KestrelRecipient.Create(host, bob, o =>
        {
            var factory = o.WebSocketFactory!;
            o.WebSocketFactory = () => new CloseObservingWebSocket((ClientWebSocket)factory(), closeSent);
            o.Connect = (socket, uri, ct) => ((CloseObservingWebSocket)socket).Inner.ConnectAsync(uri, ct);
        });
        await using var _ = recipient; // also disposed by the test, at the point under test (idempotent)
        var transport = recipient.Transport;

        // The live stream: live delivery on, its first drain empty; then a first message pushed live.
        var stream = recipient.Pickup.ReceiveLiveAsync(host.Did, bob.Did, host.UriOf(DeadlineEndpoint.Mediator)).GetAsyncEnumerator();
        var first = Observed(stream.MoveNextAsync().AsTask());
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/delivery-request", "live delivery is on and the stream's drain asked once");
        (await host.PostForwardAsync(bob)).Should().Be(HttpStatusCode.Accepted);
        (await first.WaitAsync(Guard)).Should().BeTrue();
        var firstId = stream.Current.Id;

        // Asking for the next item acknowledges the first; the mediator holds that acknowledgement at
        // its store, and so reads nothing more on the connection until it is released.
        var hold = store.HoldNextAcknowledgementOf(firstId);
        var next = Observed(stream.MoveNextAsync().AsTask());
        await hold.Started.Task.WaitAsync(Guard);

        // Dispose sends its close frame and waits for the mediator's; the mediator has not read it.
        var dropped = transport.InboundMessagesDropped;
        var disposing = recipient.DisposeAsync().AsTask();
        await closeSent.Task.WaitAsync(Guard);
        // A second message, pushed live on the connection during the close wait.
        (await host.PostForwardAsync(bob)).Should().Be(HttpStatusCode.Accepted);
        await MediatorTestServer.WaitUntilAsync(() => transport.InboundMessagesDropped == dropped + 1,
            "the push reached the client, which read it whole and dropped it");
        StoreCount(host).Should().Be(2, "precondition: the first message's acknowledgement is still held");

        hold.Release();
        await disposing.WaitAsync(Guard);
        await next.ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(Guard); // ended by the disposal

        (await hold.Applied.Task.WaitAsync(Guard)).Should().Be(1, "the first message's acknowledgement was applied");
        await host.App.Services.GetRequiredService<ProtocolDispatcher>().FlushObserversAsync(Guard);
        var acknowledged = observer.From(bob.Did).Where(r => r.Kind == "4.0/messages-received")
            .SelectMany(r => r.Body!["message_id_list"]!.AsArray().Select(n => n!.GetValue<string>()))
            .ToList();
        acknowledged.Should().Equal(new[] { firstId }, "the dropped push was never acknowledged: only the first message was");
        StoreCount(host).Should().Be(1, "the mediator still holds the pushed message for a later pickup");
        transport.InboundMessagesDropped.Should().Be(dropped + 1);
    }

    private static int StoreCount(DeadlineKestrelHost host)
        => ((IQa2WrappingStore)host.App.Services.GetRequiredService<IMessageStore>()).Inner.MessageCount;

    // Observe a failure the test does not await (the stream's pending step ends with the disposal).
    private static Task<T> Observed<T>(Task<T> task) => PickupV4AckOrder.Observed(task);

    /// <summary>A client socket that forwards to a real <see cref="ClientWebSocket"/> and completes a task once its close frame is sent.</summary>
    private sealed class CloseObservingWebSocket(ClientWebSocket inner, TaskCompletionSource closeSent) : WebSocket
    {
        public ClientWebSocket Inner { get; } = inner;

        public override WebSocketCloseStatus? CloseStatus => Inner.CloseStatus;
        public override string? CloseStatusDescription => Inner.CloseStatusDescription;
        public override WebSocketState State => Inner.State;
        public override string? SubProtocol => Inner.SubProtocol;

        public override void Abort() => Inner.Abort();
        public override void Dispose() => Inner.Dispose();

        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            await Inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
            closeSent.TrySetResult();
            await Inner.CloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
        }

        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            await Inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
            closeSent.TrySetResult();
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => Inner.ReceiveAsync(buffer, cancellationToken);
        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => Inner.ReceiveAsync(buffer, cancellationToken);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken) => Inner.SendAsync(buffer, messageType, messageFlags, cancellationToken);
    }
}
