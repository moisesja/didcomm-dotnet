using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-2 (#96) support: a host <see cref="IMessageStore"/> around the product's in-memory store that
/// records every acknowledgement and can lose or fail the next ones, a request counter in front of
/// the mediator's endpoint, and builders for the hostile payloads a sender can put in a forward.
/// </summary>
internal interface IQa2WrappingStore
{
    InMemoryMessageStore Inner { get; }
}

internal sealed class Qa2Store(InMemoryMessageStore inner) : IMessageStore, IQa2WrappingStore
{
    private readonly ConcurrentQueue<string[]> _acks = new();
    private int _lose;
    private int _fail;

    public InMemoryMessageStore Inner { get; } = inner;

    /// <summary>Every <c>AcknowledgeAsync</c> call's ids, in order (lost and failed ones included).</summary>
    public IReadOnlyList<string[]> Acks => _acks.ToArray();

    /// <summary>Every id ever acknowledged, flattened.</summary>
    public IReadOnlyList<string> AckedIds => _acks.SelectMany(a => a).ToArray();

    /// <summary>The next <paramref name="count"/> acknowledgements remove nothing (and say so).</summary>
    public void LoseNext(int count = 1) => Volatile.Write(ref _lose, count);

    /// <summary>The next <paramref name="count"/> acknowledgements throw (the mediator then answers nothing).</summary>
    public void FailNext(int count = 1) => Volatile.Write(ref _fail, count);

    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
    {
        _acks.Enqueue(messageIds.ToArray());
        if (Interlocked.Decrement(ref _fail) >= 0)
            throw new InvalidOperationException("QA-2: the store failed the acknowledgement.");
        Interlocked.Exchange(ref _fail, 0);
        if (Interlocked.Decrement(ref _lose) >= 0)
            return ValueTask.FromResult(0);
        Interlocked.Exchange(ref _lose, 0);
        return Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => Inner.EnqueueAsync(message, lease, now, ct);
    public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => Inner.LeaseAsync(request, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);
}

/// <summary>Counts what reaches the mediator's HTTP pipeline (POSTs and WebSocket upgrades).</summary>
internal sealed class Qa2RequestCounter
{
    private int _posts;
    private int _upgrades;

    public int Posts => Volatile.Read(ref _posts);

    public int Upgrades => Volatile.Read(ref _upgrades);

    public int Total => Posts + Upgrades;

    public void Install(WebApplication app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        if (context.WebSockets.IsWebSocketRequest)
            Interlocked.Increment(ref _upgrades);
        else if (HttpMethods.IsPost(context.Request.Method))
            Interlocked.Increment(ref _posts);
        await next(context);
    });
}

/// <summary>QA-2 steps.</summary>
internal static class Qa2
{
    /// <summary>Anoncrypt <paramref name="message"/> to <paramref name="to"/> (no sender; an anonymous onward payload).</summary>
    public static async Task<string> AnoncryptAsync(DidCommClient packer, Message message, string to)
        => (await packer.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }, From: null, Forward: false))).Message;

    /// <summary>A basic message with an explicit id (the same id can then come from two senders).</summary>
    public static Message NoteWithId(string id, string? from, string to, string content)
    {
        var note = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithTo(to)
            .WithBody(new System.Text.Json.Nodes.JsonObject { ["content"] = content })
            .Build();
        note.Id = id;
        if (from is not null)
            note.From = from;
        return note;
    }

    /// <summary>Wrap <paramref name="inner"/> in a forward for the mediator (next = <paramref name="next"/>), post it and wait until it is stored.</summary>
    public static async Task PostForwardAsync(MediatorTestServer mediator, MediatorSender alice, string next, string inner, Action<Message>? shape = null)
    {
        var (packed, _) = await alice.ForwardAsync(mediator.Did, next, inner, shape);
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
    }

    /// <summary>
    /// <paramref name="layers"/> forward layers around <paramref name="inner"/>, each anoncrypted to
    /// <paramref name="recipient"/> with <c>next</c> = <paramref name="recipient"/>: exactly what a
    /// re-wrapping mediator would produce, built by a sender. <paramref name="extraAttachment"/> adds a
    /// second attachment to the outermost layer.
    /// </summary>
    public static async Task<string> SelfAddressedLayersAsync(DidCommClient packer, string recipient, string inner, int layers, bool extraAttachment = false)
    {
        var text = inner;
        for (var i = 0; i < layers; i++)
        {
            var payloads = extraAttachment && i == layers - 1 ? new[] { text, text } : new[] { text };
            var forward = ForwardMessage.Create(recipient, recipient, payloads);
            text = (await packer.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { recipient }, From: null, Forward: false))).Message;
        }
        return text;
    }

    /// <summary>A forward layer addressed to <paramref name="recipient"/> but naming <paramref name="next"/>, packed by <paramref name="packer"/> as authcrypt from <paramref name="from"/> when given, else anoncrypt.</summary>
    public static async Task<string> LayerAsync(DidCommClient packer, string recipient, string next, string inner, string? from = null)
    {
        var forward = ForwardMessage.Create(recipient, next, new[] { inner });
        return (await packer.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { recipient }, From: from, Forward: false))).Message;
    }

    /// <summary>Enrol through the library's own <see cref="MediationClient"/> (not hand-rolled): request mediation, mint a DID whose service is <c>ForMediation(grant)</c>, register it.</summary>
    public static async Task<(TestPeer Bob, TestPeer Recipient, MediationGrant Grant)> EnrolWithClientAsync(MediatorTestServer mediator, MediatorClientHarness harness, Uri? endpoint = null)
    {
        var bob = await harness.NewPeerAsync();
        var grant = await harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did, endpoint);
        grant.Should().NotBeNull("the default policy grants");
        var recipient = await PickupE2E.NewRecipientForAsync(mediator, harness, grant!);
        var results = await harness.Mediation.UpdateRecipientsAsync(mediator.Did, bob.Did, new[] { new RecipientUpdate(recipient.Did, RecipientUpdateAction.Add) }, endpoint);
        results.Should().ContainSingle().Which.Result.Should().Be(RecipientUpdateOutcome.Success);
        return (bob, recipient, grant!);
    }

    /// <summary>Log lines (every container sharing the capture) that contain any of <paramref name="values"/>.</summary>
    public static IReadOnlyList<string> LinesWithAny(MediatorTestServer mediator, IEnumerable<string> values)
    {
        var sensitive = values.Where(v => !string.IsNullOrEmpty(v)).Distinct().ToArray();
        return mediator.Logs.Entries.Where(line => sensitive.Any(v => line.Contains(v, StringComparison.Ordinal))).ToList();
    }

    /// <summary>The client's own log lines (categories under <c>DidComm.Mediator.Client</c>) that contain any of <paramref name="values"/>.</summary>
    public static IReadOnlyList<string> ClientLinesWithAny(MediatorTestServer mediator, IEnumerable<string> values)
    {
        var sensitive = values.Where(v => !string.IsNullOrEmpty(v)).Distinct().ToArray();
        return mediator.Logs.CategorizedRecords
            .Where(r => r.Category.StartsWith("DidComm.Mediator.Client", StringComparison.Ordinal))
            .Select(r => r.Text)
            .Where(line => sensitive.Any(v => line.Contains(v, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>How many messages the mediator's store holds (through a QA-2 wrapping store too).</summary>
    public static int MessageCount(MediatorTestServer mediator)
        => mediator.App.Services.GetRequiredService<IMessageStore>() switch
        {
            InMemoryMessageStore store => store.MessageCount,
            IQa2WrappingStore wrapping => wrapping.Inner.MessageCount,
            var other => throw new InvalidOperationException($"Unexpected store {other.GetType().Name}."),
        };

    /// <summary>
    /// Wait until the client's live stream is parked in its wait: no request waiting for an answer
    /// and a timer scheduled on the client's clock (the wait for the next cycle). Only then may a
    /// test move that clock without racing the stream's own reading of it.
    /// </summary>
    public static Task StreamIdleAsync(MediatorClientHarness harness, QaManualTimeProvider time)
        => MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 0 && time.ScheduledTimers >= 1, "the live stream is waiting for its next cycle");

    /// <summary>Wait until the mediator's store holds exactly <paramref name="count"/> messages.</summary>
    public static Task StoreHoldsAsync(MediatorTestServer mediator, int count, string because)
        => MediatorTestServer.WaitUntilAsync(() => MessageCount(mediator) == count, because);
}
