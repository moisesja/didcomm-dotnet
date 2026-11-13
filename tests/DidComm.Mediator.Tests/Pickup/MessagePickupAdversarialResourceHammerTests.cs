using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// ADV-2, goal 2, the randomized half: senders, recipients on live and request-scoped sessions,
/// acknowledgements, live-mode toggles, trimmed batches, sessions that refuse, messages no envelope
/// can carry (JC-19) and — in the second mode — sessions that close, all at once on real threads
/// with jitter inside every store call and every send. The assertions are invariants only (L-044):
/// nothing here depends on who won a race.
/// </summary>
/// <remarks>
/// The invariants:
/// <list type="bullet">
/// <item><description>every accepted message is, at the end, either acknowledged or still stored — none lost, none invented;</description></item>
/// <item><description>only delivered messages were acknowledged, and an acknowledged message is gone;</description></item>
/// <item><description>the store's counters equal a recount, and the bounds were never passed;</description></item>
/// <item><description>while no lease can end (the clock stands still, no session closes) every message is delivered at most once;</description></item>
/// <item><description>when sessions close, a message is delivered again only after a session it was delivered over had begun to close (the documented at-least-once).</description></item>
/// </list>
/// </remarks>
public sealed class MessagePickupAdversarialResourceHammerTests
{
    private const int PoisonSize = 199;
    private const int MaxQueueMessages = 40;
    private const int MaxStoreMessages = 60;
    private const int MaxDeliveryBatch = 4;

    public MessagePickupAdversarialResourceHammerTests() => MessagePickupAdversarialResource.RaiseThreadPoolFloor(48);

    [Theory]
    [InlineData(false, 11)]
    [InlineData(false, 12)]
    [InlineData(false, 13)]
    [InlineData(true, 21)]
    [InlineData(true, 22)]
    [InlineData(true, 23)]
    public async Task Whatever_senders_recipients_and_closing_sessions_do_at_once_no_message_is_lost_none_is_delivered_twice_within_a_lease_and_the_counters_stay_true(bool sessionsClose, int seed)
    {
        await using var fixture = PickupFixture.Create(
            pickup: o =>
            {
                o.MaxQueueMessages = MaxQueueMessages;
                o.MaxQueueBytes = MaxQueueMessages * 200;
                o.MaxStoreMessages = MaxStoreMessages;
                o.MaxStoreBytes = MaxStoreMessages * 200;
                o.MaxDeliveryBatch = MaxDeliveryBatch;
            },
            fakeStore: true);
        var store = fixture.FakeStore!;
        var memory = store.Inner;

        // Jitter inside every store call the role makes, so thread switches land between its steps.
        store.OnEnqueue = (m, l, now, ct) => { Jitter(); return memory.EnqueueAsync(m, l, now, ct); };
        store.OnLease = (r, ct) => { Jitter(); return memory.LeaseAsync(r, ct); };
        store.OnAcknowledge = (g, ids, ct) => { Jitter(); return memory.AcknowledgeAsync(g, ids, ct); };
        store.OnRelease = (g, ids, lease, ct) => { Jitter(); return memory.ReleaseAsync(g, ids, lease, ct); };
        store.OnReleaseLeases = (owner, ct) => { Jitter(); return memory.ReleaseLeasesAsync(owner, ct); };
        store.OnGetStatus = (g, r, now, ct) => { Jitter(); return memory.GetStatusAsync(g, r, now, ct); };

        var mediator = (await fixture.NewPeerAsync()).Did;
        var peers = new List<(string Peer, string[] Dids)>();
        for (var i = 0; i < 2; i++)
        {
            var peer = (await fixture.NewPeerAsync()).Did;
            var second = (await fixture.NewPeerAsync()).Did;
            await fixture.EnrolAsync(peer, mediator, peer, second);
            peers.Add((peer, new[] { peer, second }));
        }

        long sequence = 0;
        var accepted = new ConcurrentDictionary<string, (string Peer, int Size)>(StringComparer.Ordinal);
        var acknowledged = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var deliveries = new ConcurrentQueue<(long Seq, string Id, PickupFakeSession Session)>();
        var closing = new ConcurrentDictionary<PickupFakeSession, long>();
        var boundBreaches = 0;
        var refused = 0;

        void CheckBounds()
        {
            if (memory.MessageCount > MaxStoreMessages || memory.TotalBytes > MaxStoreMessages * 200)
                Interlocked.Increment(ref boundBreaches);
        }

        PickupFakeSession NewSession(string peer, bool live, int carries, ConcurrentQueue<string> inbox, Random random)
        {
            var session = fixture.Session(peer, mediator, live);
            var refuseEvery = random.Next(8, 24);
            var sends = 0;
            session.OnSend = (message, bound) =>
            {
                Jitter();
                if (bound < 1 || !MessagePickup.TryParseDelivery(message, out var delivered, out string? _) || delivered is null)
                    return ReturnRouteSendResult.NotSent;
                // Over this "envelope": more attachments than it carries, or one no envelope carries.
                if (delivered.Count > carries || delivered.Any(m => m.Payload.Length == PoisonSize))
                    return ReturnRouteSendResult.TooLarge;
                if (Interlocked.Increment(ref sends) % refuseEvery == 0)
                    return ReturnRouteSendResult.NotSent;
                var at = Interlocked.Increment(ref sequence);
                foreach (var m in delivered)
                {
                    deliveries.Enqueue((at, m.Id, session));
                    inbox.Enqueue(m.Id);
                }
                return ReturnRouteSendResult.Sent;
            };
            return session;
        }

        using var stop = new CancellationTokenSource();
        var workers = new List<Task>();

        // Senders: anyone may send to any registered DID; a few messages are ones no envelope carries.
        for (var s = 0; s < 3; s++)
        {
            var random = new Random(seed * 100 + s);
            workers.Add(Task.Run(async () =>
            {
                for (var i = 0; i < 400 && !stop.IsCancellationRequested; i++)
                {
                    var (peer, dids) = peers[random.Next(peers.Count)];
                    var size = random.Next(20) == 0 ? PoisonSize : random.Next(10, 150);
                    var result = await fixture.Queue.DeliverAsync(dids[random.Next(dids.Length)], PickupFixture.Payload(size, (byte)i));
                    CheckBounds();
                    if (result.Outcome is PickupDeliveryOutcome.Queued or PickupDeliveryOutcome.PushedLive)
                        accepted.TryAdd(result.MessageId!, (peer, size)).Should().BeTrue("every retained message has its own id");
                    else if (result.Outcome == PickupDeliveryOutcome.QueueFull)
                        Interlocked.Increment(ref refused);
                    else
                        throw new InvalidOperationException("Unexpected outcome " + result.Outcome);
                }
            }));
        }

        // Recipients: two per peer, each with a live session of its own and request-scoped ones.
        for (var r = 0; r < peers.Count * 2; r++)
        {
            var (peer, dids) = peers[r % peers.Count];
            var random = new Random(seed * 1000 + r);
            workers.Add(Task.Run(async () =>
            {
                var inbox = new ConcurrentQueue<string>();
                var mine = new List<string>();
                var live = NewSession(peer, live: true, carries: random.Next(1, 5), inbox, random);
                for (var i = 0; i < 300 && !stop.IsCancellationRequested; i++)
                {
                    switch (random.Next(sessionsClose ? 12 : 11))
                    {
                        case 0 or 1 or 2:
                            await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator, random.Next(0, 7), random.Next(3) == 0 ? dids[random.Next(dids.Length)] : null), mediator, live);
                            break;
                        case 3 or 4 or 5:
                            await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator, random.Next(0, 7), random.Next(3) == 0 ? dids[random.Next(dids.Length)] : null), mediator,
                                NewSession(peer, live: false, carries: random.Next(1, 5), inbox, random));
                            break;
                        case 6 or 7:
                        {
                            while (inbox.TryDequeue(out var got))
                                mine.Add(got);
                            if (mine.Count == 0)
                                break;
                            var take = Math.Min(mine.Count, random.Next(1, 9));
                            var ids = mine.GetRange(0, take).Append("no-such-id").ToArray();
                            mine.RemoveRange(0, take);
                            var outcome = await fixture.SendAsync(MessagePickup.CreateMessagesReceived(peer, mediator, ids), mediator, fixture.Session(peer, mediator));
                            outcome.Reply.Should().NotBeNull();
                            var at = Interlocked.Increment(ref sequence);
                            foreach (var id in ids.Take(take))
                                acknowledged.TryAdd(id, at);
                            break;
                        }
                        case 8 or 9:
                            await fixture.SendAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, random.Next(4) != 0), mediator, live);
                            break;
                        case 10:
                            await fixture.SendAsync(MessagePickup.CreateStatusRequest(peer, mediator), mediator, live);
                            break;
                        default:
                            // The connection drops: the mark is taken before the close begins.
                            closing[live] = Interlocked.Increment(ref sequence);
                            live.Close();
                            fixture.Sessions.Unregister(live);
                            live = NewSession(peer, live: true, carries: random.Next(1, 5), inbox, random);
                            break;
                    }
                    CheckBounds();
                }
            }));
        }

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        // ---- the invariants ---------------------------------------------------------------
        boundBreaches.Should().Be(0, "the store-wide bounds hold at every instant");
        accepted.Should().NotBeEmpty();

        var delivered = deliveries.ToArray();
        var deliveredIds = delivered.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        deliveredIds.Should().BeSubsetOf(accepted.Keys, "only what was accepted is ever delivered");
        acknowledged.Keys.Should().BeSubsetOf(deliveredIds, "a recipient acknowledges what it was handed");

        foreach (var group in delivered.GroupBy(d => d.Id, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(d => d.Seq).ToArray();
            if (!sessionsClose)
            {
                ordered.Should().HaveCount(1, "no lease can end while the clock stands still and no session closes, so nothing is delivered twice");
                continue;
            }
            for (var i = 1; i < ordered.Length; i++)
            {
                // Delivered again: one of the sessions that carried it had begun to close by then —
                // one that carried it before, or the very session of this delivery. The second case
                // is a push that was already inside a session's send when that session closed: the
                // close released its lease, another connection was handed the message, and only
                // then did the closing session's send report it as taken (its number is assigned
                // after the send's own delay, so it sorts last although it started first). Looking
                // only at the earlier deliveries made this check depend on which of the two sends
                // was numbered first (it failed once on the Windows runner; L-044).
                var freed = ordered.Take(i + 1).Any(d => closing.TryGetValue(d.Session, out var closedAt) && closedAt < ordered[i].Seq);
                freed.Should().BeTrue("a message is delivered again only after a lease on it ended, and here leases end only when their session closes");
            }
        }

        // What must still be stored: accepted and not acknowledged. Counters first, then the records.
        var remaining = accepted.Where(a => !acknowledged.ContainsKey(a.Key)).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        memory.MessageCount.Should().Be(remaining.Count, "every accepted message is either acknowledged or still stored");
        memory.TotalBytes.Should().Be(remaining.Values.Sum(v => (long)v.Size));

        // Every lease ends by time; then drain over sessions that carry anything, acknowledging as we go.
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        foreach (var (peer, _) in peers)
        {
            var drained = new HashSet<string>(StringComparer.Ordinal);
            for (var round = 0; round < 4 * MaxQueueMessages; round++)
            {
                var ids = await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator, MaxDeliveryBatch);
                if (ids.Count == 0)
                    break;
                foreach (var id in ids)
                    drained.Add(id).Should().BeTrue("a drained message was acknowledged and is not handed out again");
                await fixture.SendAsync(MessagePickup.CreateMessagesReceived(peer, mediator, ids), mediator, fixture.Session(peer, mediator));
            }
            drained.Should().BeEquivalentTo(remaining.Where(x => x.Value.Peer == peer).Select(x => x.Key));
            drained.Should().NotIntersectWith(acknowledged.Keys);
        }
        memory.MessageCount.Should().Be(0);
        memory.TotalBytes.Should().Be(0, "the byte counter returns to zero with the last record");
        refused.Should().BeGreaterThanOrEqualTo(0);
    }

    private static void Jitter()
    {
        switch (Random.Shared.Next(6))
        {
            case 0:
                Thread.Yield();
                break;
            case 1:
                Thread.SpinWait(Random.Shared.Next(200));
                break;
            case 2:
                Thread.Sleep(0);
                break;
        }
    }
}
