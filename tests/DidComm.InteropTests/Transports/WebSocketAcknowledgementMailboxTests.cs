using System.Net;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using E2E = DidComm.InteropTests.Transports.WebSocketDisposeAcknowledgementEndToEndTests;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #100, the reviewer's test of PR #143 review round 3: a wallet that acknowledges under Message Pickup
/// 4.0 over a real Kestrel WebSocket (the real <c>PickupClient</c> and transport, <see cref="KestrelRecipient"/>)
/// and then goes away leaves a mailbox without the acknowledged message. Under 4.0 nothing answers
/// <c>messages-received</c>, so the client is gone while the mediator is still applying it; the
/// mediator's handling is slowed or held at its store (<see cref="AckDelayingStore"/>) below the
/// server's close grace (<see cref="WebSocketReturnRouteSession.DefaultCloseGrace"/>, 5 s, on the host's
/// manual clock, which no case moves), and each case then asserts what the mailbox holds.
/// <list type="bullet">
/// <item>The client disposes its container (a close frame): the acknowledgement is applied <b>when
/// dispose returns</b>. That is OD-7: dispose waits, bounded, for the mediator's close, which the
/// mediator sends only after the messages before it were handled.</item>
/// <item>The client drops the connection without a close frame: the acknowledgement is applied
/// <b>eventually</b>. Only OD-8 covers this: the mediator finishes a message it received in full
/// after the connection ended.</item>
/// </list>
/// </summary>
public sealed class WebSocketAcknowledgementMailboxTests
{
    private static readonly TimeSpan Guard = DeadlineKestrelHost.Guard;

    [Fact]
    public async Task An_AckAsync_then_an_immediate_container_dispose_leaves_the_mailbox_empty_when_dispose_returns()
    {
        var (host, store) = await StartAsync();
        await using var _host = host;
        var (bob, id) = await E2E.QueueAndLeaseOneAsync(host);
        var recipient = KestrelRecipient.Create(host, bob);
        await using var _ = recipient; // also disposed by the test, at the point under test (idempotent)
        (await MailboxAsync(host, store, bob)).Should().Be(1, "precondition: the leased message is still queued");
        var ack = store.DelayNextAcknowledgementOf(id); // 500 ms, below the close grace

        (await recipient.Pickup.AckAsync(host.Did, bob.Did, new[] { id }, host.UriOf(DeadlineEndpoint.Mediator)).WaitAsync(Guard))
            .Should().BeNull("under 4.0 nothing answers the acknowledgement");
        await recipient.DisposeAsync().AsTask().WaitAsync(Guard);

        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue("dispose returned only after the mediator's close, which follows the acknowledgement (OD-7)");
        (await MailboxAsync(host, store, bob)).Should().Be(0, "the acknowledged message is gone from the mailbox");
        ServerLoud(host).Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_FetchAsync_then_an_immediate_container_dispose_leaves_the_mailbox_empty_when_dispose_returns()
    {
        var (host, store) = await StartAsync();
        await using var _host = host;
        var bob = await host.EnrolAsync();
        (await host.PostForwardAsync(bob)).Should().Be(HttpStatusCode.Accepted);
        await host.App.Services.GetRequiredService<ForwardHandler>().FlushAsync(Guard);
        (await MailboxAsync(host, store, bob)).Should().Be(1, "precondition: one message queued for Bob");
        var recipient = KestrelRecipient.Create(host, bob);
        await using var _ = recipient;
        var ack = store.DelayNextAcknowledgement(); // the fetch's own acknowledgement, 500 ms

        var batch = await recipient.Pickup.FetchAsync(host.Did, bob.Did, 10, serviceEndpointOverride: host.UriOf(DeadlineEndpoint.Mediator)).WaitAsync(Guard);
        batch.Items.Should().ContainSingle().Which.Unpacked.Should().NotBeNull("the fetch returned the message and acknowledged it, one way under 4.0");
        await recipient.DisposeAsync().AsTask().WaitAsync(Guard);

        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue("dispose returned only after the mediator's close, which follows the acknowledgement (OD-7)");
        (await MailboxAsync(host, store, bob)).Should().Be(0, "the fetched message is gone from the mailbox");
        ServerLoud(host).Should().BeEmpty();
    }

    [Fact]
    public async Task An_AckAsync_whose_connection_is_then_dropped_without_a_close_frame_leaves_the_mailbox_empty_eventually()
    {
        var (host, store) = await StartAsync();
        await using var _host = host;
        var (bob, id) = await E2E.QueueAndLeaseOneAsync(host);
        var recipient = KestrelRecipient.Create(host, bob);
        await using var _ = recipient;
        (await MailboxAsync(host, store, bob)).Should().Be(1, "precondition: the leased message is still queued");
        var ack = store.HoldNextAcknowledgementOf(id); // released below the close grace

        (await recipient.Pickup.AckAsync(host.Did, bob.Did, new[] { id }, host.UriOf(DeadlineEndpoint.Mediator)).WaitAsync(Guard))
            .Should().BeNull("under 4.0 nothing answers the acknowledgement");
        await ack.Started.Task.WaitAsync(Guard); // the mediator received it in full and is applying it
        var mark = host.Time.Mark();
        recipient.Sockets.Should().ContainSingle().Which.Abort(); // the client drops: no close frame
        await recipient.DisposeAsync().AsTask().WaitAsync(Guard);
        await host.Time.WaitForTimerAsync(mark, WebSocketReturnRouteSession.DefaultCloseGrace, Guard); // the server saw the drop with it in hand
        ack.Release();

        (await ReturnRouteTestHost.WaitUntilAsync(() => ack.Applied.Task.IsCompleted || ack.Cancelled.Task.IsCompleted, Guard)).Should().BeTrue();
        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue("the mediator finishes a message it received in full after the connection ended (OD-8)");
        (await MailboxAsync(host, store, bob)).Should().Be(0, "the acknowledged message is gone from the mailbox");
        ServerLoud(host).Should().BeEmpty();
    }

    private static async Task<(DeadlineKestrelHost Host, AckDelayingStore Store)> StartAsync()
    {
        AckDelayingStore store = null!;
        var host = await DeadlineKestrelHost.StartAsync(o => o.Builder = b => AckDelayingStore.Register(b.Services, x => store = x));
        return (host, store);
    }

    // Bob's mailbox at the mediator: the messages queued for him, leased or not, read at the host's clock.
    private static async Task<int> MailboxAsync(DeadlineKestrelHost host, AckDelayingStore store, TestPeer bob)
        => (await store.Inner.GetStatusAsync(bob.Did, null, host.Time.GetUtcNow(), CancellationToken.None)).MessageCount;

    // The mediator's own lines at Error or above (the recipient logs into the same capture).
    private static IReadOnlyList<string> ServerLoud(DeadlineKestrelHost host)
        => host.Logs.CategorizedRecords
            .Where(r => r.Level >= LogLevel.Error)
            .Where(r => r.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                        || r.Category.StartsWith("DidComm.AspNetCore", StringComparison.Ordinal)
                        || (r.Category.StartsWith("DidComm.Mediator", StringComparison.Ordinal) && !r.Category.StartsWith("DidComm.Mediator.Client", StringComparison.Ordinal)))
            .Select(r => $"{r.Category} [{r.Level}] {r.Text}")
            .ToList();
}
