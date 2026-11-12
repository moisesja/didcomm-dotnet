using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// An <see cref="IMediationStore"/> over the real <see cref="InMemoryMediationStore"/> that records
/// every call with the DID it was asked about and lets a test replace or break the two reads the
/// pickup role makes (<see cref="GetGrantAsync"/> and <see cref="FindGrantByRecipientDidAsync"/>).
/// </summary>
internal sealed class MessagePickupHandlerMediationStore : IMediationStore
{
    public InMemoryMediationStore Inner { get; } = new(new CoordinateMediationOptions());

    /// <summary>The method names called, in order.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>The reads the pickup role makes, in order, with the DID each was about.</summary>
    public ConcurrentQueue<(string Method, string Did)> Reads { get; } = new();

    public Func<string, CancellationToken, ValueTask<MediationGrant?>>? OnGetGrant { get; set; }
    public Func<string, CancellationToken, ValueTask<MediationGrant?>>? OnFindGrantByRecipientDid { get; set; }

    public void Reset()
    {
        Calls.Clear();
        Reads.Clear();
    }

    public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(GetGrantAsync));
        Reads.Enqueue((nameof(GetGrantAsync), peerDid));
        return OnGetGrant is { } hook ? hook(peerDid, ct) : Inner.GetGrantAsync(peerDid, ct);
    }

    public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(FindGrantByRecipientDidAsync));
        Reads.Enqueue((nameof(FindGrantByRecipientDidAsync), recipientDid));
        return OnFindGrantByRecipientDid is { } hook ? hook(recipientDid, ct) : Inner.FindGrantByRecipientDidAsync(recipientDid, ct);
    }

    public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct)
    {
        Calls.Enqueue(nameof(GrantAsync));
        return Inner.GrantAsync(grant, ct);
    }

    public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(RevokeAsync));
        return Inner.RevokeAsync(peerDid, ct);
    }

    public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct)
    {
        Calls.Enqueue(nameof(UpdateRecipientsAsync));
        return Inner.UpdateRecipientsAsync(peerDid, updates, ct);
    }

    public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct)
    {
        Calls.Enqueue(nameof(ListRecipientsAsync));
        return Inner.ListRecipientsAsync(peerDid, offset, limit, ct);
    }
}

/// <summary>
/// A return-route session whose own members can fail: the identity getters, <see cref="IsLive"/>,
/// <see cref="Closed"/> and the send. By default it is a healthy live session for the pair that
/// accepts and records every message.
/// </summary>
internal sealed class MessagePickupHandlerFaultySession(string peerDid, string localDid) : IReturnRouteSession
{
    private int _attempts;

    public ConcurrentQueue<Message> Sent { get; } = new();
    public int Attempts => Volatile.Read(ref _attempts);

    public Func<string>? OnPeerDid { get; set; }
    public Func<string>? OnLocalDid { get; set; }
    public Func<bool> OnIsLive { get; set; } = () => true;
    public Func<CancellationToken> OnClosed { get; set; } = () => CancellationToken.None;

    public string PeerDid => OnPeerDid is { } hook ? hook() : peerDid;
    public string LocalDid => OnLocalDid is { } hook ? hook() : localDid;
    public ReturnRouteMode Mode => ReturnRouteMode.All;
    public string? Thread => null;
    public ReturnRouteTransport Transport => ReturnRouteTransport.WebSocket;
    public bool IsLive => OnIsLive();
    public CancellationToken Closed => OnClosed();

    public async ValueTask<bool> TrySendAsync(Message message, CancellationToken ct)
        => await TrySendAsync(message, int.MaxValue, ct) == ReturnRouteSendResult.Sent;

    public ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct)
    {
        Interlocked.Increment(ref _attempts);
        Sent.Enqueue(message);
        return new ValueTask<ReturnRouteSendResult>(ReturnRouteSendResult.Sent);
    }
}

/// <summary>
/// A list a store hands back that dies while it is read: at once, or after yielding its first
/// entries (a cursor that failed part-way). The fault carries <see cref="HandlerBoomException.Marker"/>.
/// </summary>
internal sealed class MessagePickupHandlerFaultyList<T>(IReadOnlyList<T> items, int yieldFirst) : IReadOnlyList<T>
{
    public int Count => items.Count;

    public T this[int index] => index < yieldFirst ? items[index] : throw new HandlerBoomException(HandlerBoomException.Marker);

    public IEnumerator<T> GetEnumerator()
    {
        if (yieldFirst == 0)
            throw new HandlerBoomException(HandlerBoomException.Marker);
        return Enumerate();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IEnumerator<T> Enumerate()
    {
        for (var i = 0; i < yieldFirst; i++)
            yield return items[i];
        throw new HandlerBoomException(HandlerBoomException.Marker);
    }
}

/// <summary>
/// The pickup role under a microscope: <see cref="PickupFixture"/> with a recording message store
/// (<see cref="PickupFakeStore"/>) and a recording mediation store
/// (<see cref="MessagePickupHandlerMediationStore"/>), one mediator DID and two peers. Everything
/// goes through the real dispatcher with real envelopes.
/// </summary>
internal sealed class MessagePickupHandlerHarness : IAsyncDisposable
{
    private MessagePickupHandlerHarness(PickupFixture fixture, MessagePickupHandlerMediationStore mediation)
    {
        Fixture = fixture;
        Mediation = mediation;
        Start = fixture.Clock.Now;
    }

    public PickupFixture Fixture { get; }
    public MessagePickupHandlerMediationStore Mediation { get; }
    public PickupFakeStore Store => Fixture.FakeStore!;
    public PickupClock Clock => Fixture.Clock;

    /// <summary>The instant the hand-moved clock started at.</summary>
    public DateTimeOffset Start { get; }

    public string Mediator { get; private set; } = string.Empty;
    public string Alice { get; private set; } = string.Empty;
    public string Bob { get; private set; } = string.Empty;

    /// <summary>When set, every read of the clock throws what this returns (the clock is host code too).</summary>
    public Func<Exception>? ClockFault { get; set; }

    public static async Task<MessagePickupHandlerHarness> CreateAsync(Action<MessagePickupOptions>? pickup = null, bool throwingLogger = false)
    {
        var mediation = new MessagePickupHandlerMediationStore();
        MessagePickupHandlerHarness? harness = null;
        var fixture = PickupFixture.Create(
            pickup: pickup,
            didComm: o =>
            {
                // Runs after the fixture installed its hand-moved clock: wrap it so a test can break it.
                var clock = o.Clock!;
                o.Clock = () => harness?.ClockFault is { } fault ? throw fault() : clock();
            },
            fakeStore: true,
            throwingLogger: throwingLogger,
            services: collection => collection.AddSingleton<IMediationStore>(mediation));
        harness = new MessagePickupHandlerHarness(fixture, mediation);
        harness.Mediator = await harness.NewDidAsync();
        harness.Alice = await harness.NewDidAsync();
        harness.Bob = await harness.NewDidAsync();
        return harness;
    }

    public async Task<string> NewDidAsync() => (await Fixture.NewPeerAsync()).Did;

    /// <summary>Give <paramref name="peerDid"/> a grant and register <paramref name="recipientDids"/> under it.</summary>
    public Task EnrolAsync(string peerDid, params string[] recipientDids) => Fixture.EnrolAsync(peerDid, Mediator, recipientDids);

    /// <summary>Queue a payload of <paramref name="size"/> bytes for a registered recipient DID; returns the queue's id for it.</summary>
    public Task<string> QueueAsync(string recipientDid, int size, byte seed = 1) => Fixture.QueueAsync(recipientDid, PickupFixture.Payload(size, seed));

    /// <summary>A request-scoped session, as the HTTP endpoint opens one: not live.</summary>
    public PickupFakeSession Http(string peerDid, string? localDid = null) => Fixture.Session(peerDid, localDid ?? Mediator, live: false, register: false);

    /// <summary>A live session, as the WebSocket endpoint opens one. Not put in the registry: these tests are about requests.</summary>
    public PickupFakeSession Live(string peerDid, string? localDid = null) => Fixture.Session(peerDid, localDid ?? Mediator, live: true, register: false);

    public Message StatusRequest(string from, string? recipientDid = null, string? thid = null)
        => WithThread(MessagePickup.CreateStatusRequest(from, Mediator, recipientDid), thid);

    public Message DeliveryRequest(string from, int limit = 10, string? recipientDid = null, string? thid = null)
        => WithThread(MessagePickup.CreateDeliveryRequest(from, Mediator, limit, recipientDid), thid);

    public Message MessagesReceived(string from, IReadOnlyList<string> messageIds, string? thid = null)
        => WithThread(MessagePickup.CreateMessagesReceived(from, Mediator, messageIds), thid);

    public Message LiveChange(string from, bool liveDelivery, string? thid = null)
        => WithThread(MessagePickup.CreateLiveDeliveryChange(from, Mediator, liveDelivery), thid);

    /// <summary>A message of any pickup name with any body, as a sloppy or hostile peer writes it.</summary>
    public Message Raw(string name, string from, JsonObject? body, string? id = null, string? thid = null)
        => PickupMessages.Raw(MessagePickup.ProtocolUri + "/" + name, from, Mediator, body, id, thid);

    /// <summary>Forget every recorded store call, so the next assertion sees one request only.</summary>
    public void ResetTraces()
    {
        Mediation.Reset();
        Store.Calls.Clear();
        Store.LeaseRequests.Clear();
        Store.Enqueued.Clear();
        Store.Acknowledged.Clear();
        Store.Released.Clear();
        Store.ReleasedLeaseIds.Clear();
        Store.ReleasedOwners.Clear();
        Store.StatusRequests.Clear();
        Store.PurgedRecipients.Clear();
    }

    /// <summary>The message-store methods called since the last reset, in order.</summary>
    public string[] StoreTrace => Store.Calls.ToArray();

    /// <summary>The mediation-store methods called since the last reset, in order.</summary>
    public string[] MediationTrace => Mediation.Calls.ToArray();

    /// <summary>Every id the message store was asked to release since the last reset.</summary>
    public string[] ReleasedIds => Store.Released.SelectMany(r => r.Ids).ToArray();

    public Task<DispatchOutcome> SendAsync(
        Message message,
        IReturnRouteSession? session = null,
        HandlerEnvelope shape = HandlerEnvelope.Authcrypt,
        string? sender = null,
        CancellationToken ct = default,
        string? mediator = null)
        => Fixture.SendAsync(message, mediator ?? Mediator, session, shape, sender, ct);

    /// <summary>
    /// The ids a delivery-request of <paramref name="grantPeerDid"/> could be handed right now, read
    /// straight from the real store without leaving a lease behind: what is stored, due and not
    /// under a lease.
    /// </summary>
    public async Task<string[]> DeliverableAsync(string grantPeerDid)
    {
        var now = Clock.Now;
        var probe = new MessageLeaseRequest(grantPeerDid, null, int.MaxValue, long.MaxValue, now, new MessageLease(null, now.AddTicks(1)));
        var taken = await Store.Inner.LeaseAsync(probe, CancellationToken.None);
        var ids = taken.Select(m => m.Id).ToArray();
        await Store.Inner.ReleaseAsync(grantPeerDid, ids, probe.Lease.Id, CancellationToken.None);
        return ids;
    }

    /// <summary>How many records the real store holds under <paramref name="grantPeerDid"/> (leased ones included).</summary>
    public async Task<int> StoredAsync(string grantPeerDid)
        => (await Store.Inner.GetStatusAsync(grantPeerDid, null, Clock.Now, CancellationToken.None)).MessageCount;

    /// <summary>Whether live delivery is on for <paramref name="session"/>, as an authenticated <c>status-request</c> of its own pair reports on it.</summary>
    public async Task<bool?> LiveFlagAsync(IReturnRouteSession session)
    {
        var outcome = await Fixture.SendAsync(MessagePickup.CreateStatusRequest(session.PeerDid, session.LocalDid), session.LocalDid, session);
        return outcome.Reply is not null && MessagePickup.TryParseStatus(outcome.Reply, out var status) ? status.LiveDelivery : null;
    }

    public ValueTask DisposeAsync() => Fixture.DisposeAsync();

    private static Message WithThread(Message message, string? thid)
    {
        if (thid is not null)
            message.Thid = thid;
        return message;
    }
}

/// <summary>The three time members of a <c>status</c>, in 3.0 units (whole seconds).</summary>
internal readonly record struct MessagePickupHandlerTimes(long LongestWaitedSeconds, long NewestReceivedTime, long OldestReceivedTime);

/// <summary>What the pickup role must put on the wire, asserted member by member.</summary>
internal static class MessagePickupHandlerAssert
{
    /// <summary>The body as "name=json; name=json", names sorted: exactly which members are present and how each is written.</summary>
    public static string Members(Message message)
        => message.Body is null
            ? "(no body)"
            : string.Join("; ", message.Body.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + (p.Value?.ToJsonString() ?? "null")));

    /// <summary>What <see cref="Members"/> gives for a <c>status</c> with these values.</summary>
    public static string StatusMembers(int count, long bytes, bool live, string? recipientDid = null, MessagePickupHandlerTimes? times = null)
    {
        var members = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["message_count"] = count.ToString(CultureInfo.InvariantCulture),
            ["total_bytes"] = bytes.ToString(CultureInfo.InvariantCulture),
            ["live_delivery"] = live ? "true" : "false",
        };
        if (recipientDid is not null)
            members["recipient_did"] = JsonValue.Create(recipientDid).ToJsonString();
        if (times is { } t)
        {
            members["longest_waited_seconds"] = t.LongestWaitedSeconds.ToString(CultureInfo.InvariantCulture);
            members["newest_received_time"] = t.NewestReceivedTime.ToString(CultureInfo.InvariantCulture);
            members["oldest_received_time"] = t.OldestReceivedTime.ToString(CultureInfo.InvariantCulture);
        }
        return string.Join("; ", members.Select(p => p.Key + "=" + p.Value));
    }

    /// <summary>
    /// The reply is a <c>status</c> on the request's thread, from the DID that decrypted the request
    /// to the peer, with exactly these body members (JC-7): the count, the bytes and the live flag
    /// always, <c>recipient_did</c> only when given, the three time members only when given.
    /// </summary>
    public static void ShouldBeStatus(this DispatchOutcome outcome, Message request, string mediator, string peer, int count, long bytes, bool live, string? recipientDid = null, MessagePickupHandlerTimes? times = null)
    {
        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Handler.Should().BeOfType<MessagePickupHandler>();
        var reply = outcome.Reply!;
        reply.Type.Should().Be("https://didcomm.org/messagepickup/3.0/status");
        reply.Thid.Should().Be(request.Thid ?? request.Id, "thid = the request's thid if present, else its id (JC-8)");
        reply.Pthid.Should().BeNull();
        reply.From.Should().Be(mediator, "the reply comes from the DID that decrypted the request");
        reply.To.Should().Equal(peer);
        reply.Id.Should().NotBe(request.Id);
        reply.CreatedTime.Should().BeNull("the session stamps created_time when it sends; the handler does not");
        (reply.Attachments?.Count ?? 0).Should().Be(0);
        Members(reply).Should().Be(StatusMembers(count, bytes, live, recipientDid, times));
    }

    /// <summary>Nothing came back from the pickup handler: no reply for the dispatcher to send.</summary>
    public static void ShouldBeNoReply(this DispatchOutcome outcome)
    {
        outcome.Result.Should().Be(DispatchResult.NoReply);
        outcome.Reply.Should().BeNull();
        outcome.Handler.Should().BeOfType<MessagePickupHandler>();
    }

    /// <summary>The reply is the problem report of FR-PICK-07, with the exact strings of the protocol page.</summary>
    public static void ShouldBeLiveModeNotSupported(this DispatchOutcome outcome, Message request, string mediator, string peer)
    {
        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Handler.Should().BeOfType<MessagePickupHandler>();
        var report = outcome.Reply!;
        report.Type.Should().Be("https://didcomm.org/report-problem/2.0/problem-report");
        report.Pthid.Should().Be(request.Thid ?? request.Id, "pthid = the request's thid if present, else its id (JC-8)");
        report.From.Should().Be(mediator);
        report.To.Should().Equal(peer);
        report.Id.Should().NotBe(request.Id);
        ProblemReports.ReadCode(report).Should().Be("e.m.live-mode-not-supported");
        ProblemReports.ReadComment(report).Should().Be("Connection does not support Live Delivery");
        Members(report).Should().Be("code=\"e.m.live-mode-not-supported\"; comment=\"Connection does not support Live Delivery\"");
    }

    /// <summary>
    /// The pushed message is a <c>delivery</c> on the request's thread carrying exactly these
    /// messages in this order, and <c>recipient_did</c> only when the request had one.
    /// </summary>
    public static void ShouldBeDelivery(this Message pushed, Message request, string mediator, string peer, string? recipientDid, IReadOnlyList<string> ids, IReadOnlyList<byte[]>? payloads = null)
    {
        pushed.Type.Should().Be("https://didcomm.org/messagepickup/3.0/delivery");
        pushed.Thid.Should().Be(request.Thid ?? request.Id, "thid = the request's thid if present, else its id (JC-8)");
        pushed.Pthid.Should().BeNull();
        pushed.From.Should().Be(mediator);
        pushed.To.Should().Equal(peer);
        pushed.Id.Should().NotBe(request.Id);
        pushed.CreatedTime.Should().BeNull("the session stamps created_time when it sends; the handler does not");
        Members(pushed).Should().Be(recipientDid is null ? string.Empty : "recipient_did=" + JsonValue.Create(recipientDid).ToJsonString());
        MessagePickup.TryParseDelivery(pushed, out var messages, out var echoed).Should().BeTrue();
        echoed.Should().Be(recipientDid);
        messages!.Select(m => m.Id).Should().Equal(ids);
        if (payloads is not null)
        {
            for (var i = 0; i < payloads.Count; i++)
                messages![i].Payload.ToArray().Should().Equal(payloads[i]);
        }
    }

    /// <summary>The parsed <c>status</c> of a reply that must be one.</summary>
    public static PickupStatus Status(this DispatchOutcome outcome)
    {
        outcome.Reply.Should().NotBeNull();
        MessagePickup.TryParseStatus(outcome.Reply!, out var status).Should().BeTrue();
        return status!;
    }
}
