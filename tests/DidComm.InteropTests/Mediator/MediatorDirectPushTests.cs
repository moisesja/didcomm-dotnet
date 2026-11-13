using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>A host's <see cref="IRecipientEndpointDirectory"/> the tests fill: grant peer DID → push endpoint; can run code on each lookup.</summary>
internal sealed class TestEndpointDirectory : IRecipientEndpointDirectory
{
    private readonly ConcurrentDictionary<string, Uri> _endpoints = new(StringComparer.Ordinal);
    private int _calls;

    /// <summary>Runs on each lookup, on the push worker, before the answer is given.</summary>
    public Action<string>? OnFind { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public void Map(string grantPeerDid, Uri endpoint) => _endpoints[grantPeerDid] = endpoint;

    public Uri? Find(string grantPeerDid)
    {
        Interlocked.Increment(ref _calls);
        OnFind?.Invoke(grantPeerDid);
        return _endpoints.GetValueOrDefault(grantPeerDid);
    }
}

/// <summary>
/// #95 acceptance, "the same with <c>DirectPush</c> and an <c>IRecipientEndpointDirectory</c> →
/// delivered to Bob's inbox (sample 04 parity)", and the rules of the push (JC-4, JC-5): the message
/// is queued first; a 2xx from the recipient's own http(s) endpoint removes the queued copy; anything
/// else leaves it queued for pickup; a WebSocket endpoint and a message not yet due are never pushed;
/// a registration that ended before the push is not pushed; and a slow endpoint holds no one else's
/// delivery (L-059).
/// </summary>
public sealed class MediatorDirectPushTests
{
    [Fact]
    public async Task Direct_push_delivers_to_the_recipients_inbox_and_removes_the_queued_copy()
    {
        await using var fixture = await PushFixture.StartAsync();
        var (mediator, inbox, bob, b, alice) = (fixture.Mediator, fixture.Inbox, fixture.Bob, fixture.B, fixture.Alice);
        fixture.Directory.Map(bob.Did, inbox.Endpoint("/bob-inbox"));

        var note = alice.Note(b.Did);
        (await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        var received = inbox.Received.Should().ContainSingle().Subject;
        received.Path.Should().Be("/bob-inbox");
        (await MediatorRecipient.UnpackFromSenderAsync(b, received.Body, alice)).Message.Id.Should().Be(note.Id);
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.PushOutcomes.Should().Equal(PushOutcomes.Pushed);
        mediator.Store.MessageCount.Should().Be(0, "a 2xx from the recipient's endpoint removes the queued copy");
        (await mediator.FetchAsync(bob)).Should().BeEmpty();
        fixture.Directory.Calls.Should().Be(1);
        ShouldNotLogTheEndpoint(mediator, b);
    }

    [Fact]
    public async Task A_push_the_endpoint_refuses_leaves_the_message_queued_for_pickup()
    {
        await using var fixture = await PushFixture.StartAsync();
        var (mediator, inbox, bob, b, alice) = (fixture.Mediator, fixture.Inbox, fixture.Bob, fixture.B, fixture.Alice);
        fixture.Directory.Map(bob.Did, inbox.Endpoint("/bob-inbox"));
        inbox.Status = 500;

        var note = alice.Note(b.Did);
        (await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        inbox.Received.Should().ContainSingle("one attempt per message");
        mediator.PushOutcomes.Should().Equal(PushOutcomes.Failed);
        mediator.Store.MessageCount.Should().Be(1, "a failed push never loses an accepted message");
        var delivered = await mediator.FetchAsync(bob);
        (await MediatorRecipient.UnpackFromSenderAsync(b, delivered.Should().ContainSingle().Subject.Payload, alice)).Message.Id.Should().Be(note.Id);
        (await mediator.AcknowledgeAsync(bob, delivered[0].Id)).MessageCount.Should().Be(0);
        ShouldNotLogTheEndpoint(mediator, b);
    }

    [Fact]
    public async Task A_WebSocket_endpoint_is_never_pushed_to_and_the_message_waits_for_pickup()
    {
        await using var fixture = await PushFixture.StartAsync();
        var (mediator, inbox, bob, b, alice) = (fixture.Mediator, fixture.Inbox, fixture.Bob, fixture.B, fixture.Alice);
        fixture.Directory.Map(bob.Did, new Uri($"ws://{InboxServer.Host}/bob-inbox"));

        (await alice.Client.SendAsync(alice.Note(b.Did), new SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        mediator.PushOutcomes.Should().Equal(PushOutcomes.SkippedScheme);
        inbox.Entered.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(1);
        (await mediator.FetchAsync(bob)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_message_not_yet_due_is_not_pushed_and_is_picked_up_once_due()
    {
        await using var fixture = await PushFixture.StartAsync();
        var (mediator, inbox, bob, b, alice) = (fixture.Mediator, fixture.Inbox, fixture.Bob, fixture.B, fixture.Alice);
        fixture.Directory.Map(bob.Did, inbox.Endpoint("/bob-inbox"));

        var note = alice.Note(b.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, b.Did, await alice.AuthcryptAsync(note, b.Did),
            forward => (forward.AdditionalHeaders ??= new Dictionary<string, JsonElement>())["delay_milli"] = JsonSerializer.SerializeToElement(500));
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.PushOutcomes.Should().BeEmpty("a message that is not due is never pushed early");
        inbox.Entered.Should().Be(0);
        fixture.Directory.Calls.Should().Be(0);
        (await mediator.FetchAsync(bob)).Should().BeEmpty("not due yet");

        mediator.Clock.Advance(TimeSpan.FromMilliseconds(500));
        var delivered = await mediator.FetchAsync(bob);
        (await MediatorRecipient.UnpackFromSenderAsync(b, delivered.Should().ContainSingle().Subject.Payload, alice)).Message.Id.Should().Be(note.Id);
    }

    [Fact]
    public async Task A_registration_revoked_between_queueing_and_pushing_is_not_pushed()
    {
        await using var fixture = await PushFixture.StartAsync();
        var (mediator, inbox, bob, b, alice) = (fixture.Mediator, fixture.Inbox, fixture.Bob, fixture.B, fixture.Alice);
        fixture.Directory.Map(bob.Did, inbox.Endpoint("/bob-inbox"));
        // The host revokes the grant while the push is under way: after the message was stored, the
        // push job taken and its registration checked, when the worker asks the directory.
        // L-020 (PR #109 review R1-2): the job now checks the registration and tries the live session
        // before it asks the directory (D6), so this hook runs after the check (which the dispatcher
        // tests cover with Push-Skipped-Unregistered) and it is the purge that keeps the push away:
        // the take by id finds nothing (Push-Skipped-NotHeld), and nothing is sent either way.
        GrantRevocation? revocation = null;
        fixture.Directory.OnFind = grant => revocation = mediator.Queue.RevokeAndPurgeAsync(grant).AsTask().GetAwaiter().GetResult();

        (await alice.Client.SendAsync(alice.Note(b.Did), new SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        revocation.Should().Be(new GrantRevocation(Revoked: true, Purged: 1));
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.PushOutcomes.Should().Equal(PushOutcomes.SkippedNotHeld);
        inbox.Entered.Should().Be(0, "nothing reaches a recipient whose registration ended");
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_slow_push_endpoint_holds_no_other_recipients_delivery()
    {
        // L-059: one push worker, parked on a gated endpoint. Forwards for another recipient keep
        // being accepted, queued and picked up while it hangs.
        await using var fixture = await PushFixture.StartAsync(m => m.PushConcurrency = 1);
        var (mediator, inbox, bob, b, alice) = (fixture.Mediator, fixture.Inbox, fixture.Bob, fixture.B, fixture.Alice);
        fixture.Directory.Map(bob.Did, inbox.Endpoint("/bob-inbox"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inbox.Gate = gate;
        var carol = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(carol);

        (await alice.Client.SendAsync(alice.Note(b.Did), new SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await MediatorTestServer.WaitUntilAsync(() => inbox.Entered == 1, "the only push worker is parked in Bob's endpoint");

        var forCarol = alice.Note(carol.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, carol.Did, await alice.AuthcryptAsync(forCarol, carol.Did));
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);
        var delivered = await mediator.FetchAsync(carol);
        (await MediatorRecipient.UnpackFromSenderAsync(carol, delivered.Should().ContainSingle().Subject.Payload, alice)).Message.Id.Should().Be(forCarol.Id);
        gate.Task.IsCompleted.Should().BeFalse("Carol's message was delivered while Bob's push was still hanging");
        mediator.Pushes.Outstanding.Should().BeGreaterThanOrEqualTo(1);

        gate.SetResult();
        await mediator.PushesIdleAsync();
        mediator.PushOutcomes.Should().BeEquivalentTo(new[] { PushOutcomes.Pushed, PushOutcomes.SkippedNoEndpoint });
        mediator.Store.MessageCount.Should().Be(1, "Bob's copy was removed by the push; Carol's waits for her acknowledgement");
    }

    // No DID or key id anywhere in the logs. The push endpoint never appears in the mediator's own
    // lines; the framework's IHttpClientFactory logging ("Start processing HTTP request POST <url>",
    // category System.Net.Http.HttpClient.didcomm.*) does write it at Information — host logging
    // configuration, outside the mediator.
    private static void ShouldNotLogTheEndpoint(MediatorTestServer mediator, TestPeer b)
    {
        mediator.LeakingLogLines(b.Did).Should().BeEmpty();
        mediator.Logs.Entries.Where(e => e.Contains("push", StringComparison.OrdinalIgnoreCase))
            .Should().NotContain(e => e.Contains(InboxServer.Host, StringComparison.Ordinal));
    }

    /// <summary>A mediator with <c>DirectPush</c> on and a directory, an inbox server, Bob (enrolled) with recipient DID B, and Alice.</summary>
    private sealed class PushFixture : IAsyncDisposable
    {
        public required MediatorTestServer Mediator { get; init; }
        public required InboxServer Inbox { get; init; }
        public required TestEndpointDirectory Directory { get; init; }
        public required TestPeer Bob { get; init; }
        public required TestPeer B { get; init; }
        public required MediatorSender Alice { get; init; }

        public static async Task<PushFixture> StartAsync(Action<MediatorOptions>? mediator = null)
        {
            var network = new MediatorNetwork();
            var inbox = await InboxServer.StartAsync(network);
            var directory = new TestEndpointDirectory();
            var server = await MediatorTestServer.StartAsync(o =>
            {
                o.Network = network;
                o.Mediator = m =>
                {
                    m.DirectPush = true;
                    mediator?.Invoke(m);
                };
                o.ConfigureServices = services => services.AddSingleton<IRecipientEndpointDirectory>(directory);
            });
            var bob = await server.NewPeerAsync();
            (await server.RequestMediationAsync(bob)).Should().Equal(server.Did);
            var b = await server.NewRecipientAsync();
            await server.AddRecipientsAsync(bob, new[] { b.Did });
            var alice = await server.NewSenderAsync();
            return new PushFixture { Mediator = server, Inbox = inbox, Directory = directory, Bob = bob, B = b, Alice = alice };
        }

        public async ValueTask DisposeAsync()
        {
            await Mediator.DisposeAsync();
            await Inbox.DisposeAsync();
        }
    }
}
