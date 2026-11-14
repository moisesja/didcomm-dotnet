using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-2 (#96): <see cref="PickupClient.ReceiveLiveAsync"/> against the real #95 mediator over a
/// WebSocket. R-2 checked on the mediator's store (an exception in the loop body and a cancellation
/// in the loop body leave the last item queued; it comes back); the session socket evicted by the
/// mediator (the stream re-arms live mode at its next cycle and keeps delivering); acknowledgements
/// lost or failing (nothing lost, nothing yielded twice); a second stream for the same pair (refused,
/// nothing sent); two local DIDs streaming from one mediator; a slow consumer under many pushes
/// (bounded buffering, nothing acknowledged before it was yielded, everything delivered once).
/// </summary>
public sealed class PickupClientLiveRobustnessQa2Tests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task An_exception_out_of_the_loop_body_leaves_that_item_on_the_mediator_and_it_comes_back()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var m1 = await PickupE2E.SendAsync(mediator, alice, recipient, "m1");
        var m2 = await PickupE2E.SendAsync(mediator, alice, recipient, "m2");

        var seen = new List<string>();
        var consume = async () =>
        {
            await foreach (var item in harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint))
            {
                seen.Add(item.Unpacked!.Message.Id);
                if (seen.Count == 2)
                    throw new ApplicationException("the application failed on its second message");
            }
        };
        await FluentActions.Awaiting(() => consume().WaitAsync(MediatorTestServer.Timeout)).Should().ThrowAsync<ApplicationException>();

        seen.Should().Equal(m1.Id, m2.Id);
        mediator.Store.MessageCount.Should().Be(1, "m1 was taken when m2 was asked for and acknowledged at the end; m2 was not");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);
        harness.Channel.LiveStreamCount.Should().Be(0, "the enumeration's end closed the stream");

        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        await using var again = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).GetAsyncEnumerator();
        PickupE2E.ShouldBeAlicesNote(await PickupE2E.NextAsync(again), alice, recipient, m2);
    }

    [Fact]
    public async Task A_token_cancelled_in_the_loop_body_leaves_that_item_on_the_mediator_and_it_comes_back()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var m1 = await PickupE2E.SendAsync(mediator, alice, recipient, "m1");
        var m2 = await PickupE2E.SendAsync(mediator, alice, recipient, "m2");

        using var cts = new CancellationTokenSource();
        var seen = new List<string>();
        var consume = async () =>
        {
            await foreach (var item in harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).WithCancellation(cts.Token))
            {
                seen.Add(item.Unpacked!.Message.Id);
                if (seen.Count == 2)
                    cts.Cancel();
            }
        };
        await FluentActions.Awaiting(() => consume().WaitAsync(MediatorTestServer.Timeout)).Should().ThrowAsync<OperationCanceledException>();

        seen.Should().Equal(m1.Id, m2.Id);
        mediator.Store.MessageCount.Should().Be(1, "a token cancelled while the body ran leaves that item unacknowledged");
        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, serviceEndpointOverride: harness.WebSocketEndpoint));
        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, m2);
    }

    [Fact]
    public async Task After_the_mediator_evicts_the_session_socket_the_stream_re_arms_live_mode_at_its_next_cycle_and_keeps_delivering()
    {
        var time = new QaManualTimeProvider();
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer, o =>
            o.ConfigureServices = s => s.AddOptions<ReturnRouteSessionOptions>().Configure(x => x.MaxSessionsPerPeer = 1));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.Client = c => c.LivePollInterval = Poll;
        });
        var transport = harness.Services.GetRequiredService<WebSocketDidCommTransport>();
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await observer.WaitForAsync(bob.Did, 2);
        var m1 = await PickupE2E.SendAsync(mediator, alice, recipient, "m1");
        (await PickupE2E.Within(first)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, m1);

        // Another socket of Bob's takes the only session slot: the client's socket is evicted and closed.
        using var raw = await mediator.ConnectAsync();
        (await mediator.AskOverSocketAsync(raw, bob, MessagePickup.CreateStatusRequest(bob.Did, mediator.Did))).Message.Type.Should().Be(MessagePickup.StatusType);
        await MediatorTestServer.WaitUntilAsync(() => transport.LiveSessionSocketCount == 0, "the client noticed its session socket was closed");

        var m2 = await PickupE2E.SendAsync(mediator, alice, recipient, "m2");
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1, "no live session is left for Bob: m2 waits in the queue");

        // Asking for the next item acknowledges m1 on a fresh socket; the next cycle re-arms and drains m2.
        var next = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 1, "m1 was acknowledged");
        await Qa2.StreamIdleAsync(harness, time);
        next.IsCompleted.Should().BeFalse();
        time.Advance(Poll);
        (await PickupE2E.Within(next)).Should().BeTrue("the periodic cycle re-armed live mode and drained what waited");
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, m2);

        // And pushes flow again on the new session.
        var third = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "m2 was acknowledged");
        var m3 = await PickupE2E.SendAsync(mediator, alice, recipient, "m3");
        (await PickupE2E.Within(third)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, m3);
        var last = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "m3 was acknowledged");
        await PickupE2E.StopAsync(stream, last, cts);
    }

    [Fact]
    public async Task A_lost_acknowledgement_brings_the_message_back_after_the_lease_where_it_is_acknowledged_again_and_not_yielded_twice()
    {
        var time = new QaManualTimeProvider();
        var store = new Qa2Store(new InMemoryMessageStore(new MessagePickupOptions()));
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer, o => o.ConfigureServices = s => s.AddSingleton<IMessageStore>(store));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.Client = c => c.LivePollInterval = Poll;
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var m1 = await PickupE2E.SendAsync(mediator, alice, recipient, "m1");

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var item = await PickupE2E.NextAsync(stream);
        PickupE2E.ShouldBeAlicesNote(item, alice, recipient, m1);

        store.LoseNext();
        var next = stream.MoveNextAsync().AsTask();
        await MediatorTestServer.WaitUntilAsync(() => store.Acks.Count == 1, "m1's acknowledgement reached the store (and was lost)");
        Qa2.MessageCount(mediator).Should().Be(1);

        // After the lease and at the next cycle, m1 comes back: acknowledged again, not yielded.
        await Qa2.StreamIdleAsync(harness, time);
        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        time.Advance(Poll);
        await MediatorTestServer.WaitUntilAsync(() => store.Acks.Count == 2, "the repeat was acknowledged");
        await Qa2.StoreHoldsAsync(mediator, 0, "the second acknowledgement removed it");
        store.AckedIds.Should().Equal(item.Id, item.Id);
        next.IsCompleted.Should().BeFalse("a message already handed to the application is not yielded again");

        var m2 = await PickupE2E.SendAsync(mediator, alice, recipient, "m2");
        (await PickupE2E.Within(next)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, m2);
        var last = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "m2 was acknowledged");
        await PickupE2E.StopAsync(stream, last, cts);
    }

    [Fact]
    public async Task A_failing_acknowledgement_is_retried_after_the_back_off_and_nothing_is_lost_or_yielded_twice()
    {
        var time = new QaManualTimeProvider();
        var store = new Qa2Store(new InMemoryMessageStore(new MessagePickupOptions()));
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IMessageStore>(store));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.Client = c =>
            {
                c.LivePollInterval = Poll;
                c.RequestTimeout = TimeSpan.FromSeconds(30);
            };
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var m1 = await PickupE2E.SendAsync(mediator, alice, recipient, "m1");

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var item = await PickupE2E.NextAsync(stream);
        PickupE2E.ShouldBeAlicesNote(item, alice, recipient, m1);

        store.FailNext();
        var next = stream.MoveNextAsync().AsTask();
        await MediatorTestServer.WaitUntilAsync(() => store.Acks.Count == 1, "the acknowledgement reached the store, which failed it");
        // The mediator answers nothing; the acknowledgement times out on the client's clock.
        await time.WaitForScheduledTimersAsync(1, MediatorTestServer.Timeout);
        time.Advance(TimeSpan.FromSeconds(30));
        await MediatorTestServer.WaitUntilAsync(() => mediator.Logs.Entries.Any(e => e.Contains("(messages-received) failed (TimeoutException)")), "the failed acknowledgement put the stream into its back-off");
        await Qa2.StreamIdleAsync(harness, time);
        time.Advance(LiveBackoff);
        await MediatorTestServer.WaitUntilAsync(() => store.Acks.Count >= 2, "the acknowledgement was sent again after the back-off");
        await Qa2.StoreHoldsAsync(mediator, 0, "the retried acknowledgement removed m1");
        store.AckedIds.Should().OnlyContain(id => id == item.Id);
        next.IsCompleted.Should().BeFalse();

        var m2 = await PickupE2E.SendAsync(mediator, alice, recipient, "m2");
        (await PickupE2E.Within(next)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, m2);
        var last = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "m2 was acknowledged");
        await PickupE2E.StopAsync(stream, last, cts);
    }

    private static readonly TimeSpan LiveBackoff = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_second_stream_for_the_same_pair_is_refused_with_nothing_sent_and_the_first_keeps_working()
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();

        using var cts = new CancellationTokenSource();
        var first = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var pending = first.MoveNextAsync().AsTask();
        await observer.WaitForAsync(bob.Did, 2);
        var sent = observer.Seen.Count;

        await using (var second = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).GetAsyncEnumerator())
            await FluentActions.Awaiting(() => PickupE2E.Within(second.MoveNextAsync())).Should().ThrowAsync<InvalidOperationException>();
        observer.Seen.Count.Should().Be(sent, "the second stream sent nothing, not even live-off at its end");

        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "still live");
        (await PickupE2E.Within(pending)).Should().BeTrue("the first stream still receives pushes");
        PickupE2E.ShouldBeAlicesNote(first.Current, alice, recipient, note);
        var last = first.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "acknowledged");
        await PickupE2E.StopAsync(first, last, cts);
    }

    [Fact]
    public async Task Two_local_DIDs_stream_from_one_mediator_each_gets_only_its_own_and_both_are_acknowledged()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob1, r1) = await PickupE2E.EnrolAsync(mediator, harness);
        var (bob2, r2) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var q1 = await PickupE2E.SendAsync(mediator, alice, r1, "queued for 1");
        var q2 = await PickupE2E.SendAsync(mediator, alice, r2, "queued for 2");

        using var cts = new CancellationTokenSource();
        var s1 = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob1.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var s2 = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob2.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        PickupE2E.ShouldBeAlicesNote(await PickupE2E.NextAsync(s1), alice, r1, q1);
        PickupE2E.ShouldBeAlicesNote(await PickupE2E.NextAsync(s2), alice, r2, q2);
        harness.Channel.LiveStreamCount.Should().Be(2);

        var n1 = s1.MoveNextAsync().AsTask();
        var n2 = s2.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "both drained items were acknowledged");
        var p2 = await PickupE2E.SendAsync(mediator, alice, r2, "pushed for 2");
        var p1 = await PickupE2E.SendAsync(mediator, alice, r1, "pushed for 1");
        (await PickupE2E.Within(n1)).Should().BeTrue();
        (await PickupE2E.Within(n2)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(s1.Current, alice, r1, p1);
        PickupE2E.ShouldBeAlicesNote(s2.Current, alice, r2, p2);

        var l1 = s1.MoveNextAsync().AsTask();
        var l2 = s2.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "both pushed items were acknowledged");
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => PickupE2E.Within(l1)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => PickupE2E.Within(l2)).Should().ThrowAsync<OperationCanceledException>();
        await s1.DisposeAsync();
        await s2.DisposeAsync();
        harness.Channel.LiveStreamCount.Should().Be(0);
        mediator.LeakingLogLines(r1.Did, r2.Did, bob1.Did, bob2.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_slow_consumer_under_many_pushes_keeps_8_drops_the_rest_unacknowledged_and_they_come_back_after_the_lease()
    {
        const int Pushed = 20;
        var yielded = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var early = new ConcurrentQueue<string>();
        var store = new Qa2Store(new InMemoryMessageStore(new MessagePickupOptions()));
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IMessageStore>(new WatchingStore(store, yielded, early)));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var notes = new List<string> { (await PickupE2E.SendAsync(mediator, alice, recipient, "first")).Id };

        var got = new List<string>();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).GetAsyncEnumerator();
        var item = await PickupE2E.NextAsync(stream);
        yielded[item.Id] = 0;
        got.Add(item.Unpacked!.Message.Id);

        // The consumer holds the first item while 20 messages are pushed: its buffer keeps 8.
        for (var i = 0; i < Pushed; i++)
            notes.Add((await PickupE2E.SendAsync(mediator, alice, recipient, $"p{i}")).Id);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(Pushed);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Logs.Entries.Count(e => e.Contains("Dropped a live delivery push")) == Pushed - LiveRegistrationCapacity,
            "the pushes beyond the stream's buffer were dropped");

        // The consumer takes the 8 buffered ones, then stops (the last one is not taken).
        for (var i = 0; i < LiveRegistrationCapacity; i++)
        {
            item = await PickupE2E.NextAsync(stream);
            item.Error.Should().BeNull();
            yielded[item.Id] = 0;
            got.Add(item.Unpacked!.Message.Id);
        }
        var untaken = item.Unpacked!.Message.Id;
        await stream.DisposeAsync();
        var earlyWhileLive = early.ToArray();
        Qa2.MessageCount(mediator).Should().Be(Pushed - LiveRegistrationCapacity + 1, "the dropped pushes and the last yielded item are not acknowledged");

        // After the lease, everything left is fetched; nothing comes twice.
        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        PickupBatch batch;
        do
        {
            batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, serviceEndpointOverride: harness.WebSocketEndpoint));
            foreach (var fetched in batch.Items)
            {
                fetched.Error.Should().BeNull();
                yielded[fetched.Id] = 0;
                got.Add(fetched.Unpacked!.Message.Id);
            }
        }
        while (!batch.Drained);

        // R-2: the last yielded item was never taken, so it comes back once (at-least-once); the
        // filter does not suppress it. Every other message reaches the application exactly once.
        got.Count(id => id == untaken).Should().Be(2);
        got.Where(id => id != untaken).Should().OnlyHaveUniqueItems("no other message reaches the application twice");
        got.Distinct().Should().BeEquivalentTo(notes, "no message is lost");
        Qa2.MessageCount(mediator).Should().Be(0);
        // (FetchAsync acknowledges before it returns, by design; only the stream is checked here.)
        earlyWhileLive.Should().BeEmpty("the stream acknowledges no id before it was handed to the application");
    }

    [Fact]
    public async Task A_stream_that_fell_more_than_8_behind_recovers_every_message_once_through_its_cycles()
    {
        const int Pushed = 20;
        var time = new QaManualTimeProvider();
        var yielded = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var early = new ConcurrentQueue<string>();
        var store = new Qa2Store(new InMemoryMessageStore(new MessagePickupOptions()));
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IMessageStore>(new WatchingStore(store, yielded, early)));
        // The container's channel is built over a counting unpack step (the channel's own test seam),
        // so the test can tell "the stream is parked in its wait" from "the stream is unpacking".
        var unpacking = 0;
        var settings = new MediatorClientOptions
        {
            LivePollInterval = Poll,
            RequestTimeout = TimeSpan.FromMinutes(10),
            UnpackTimeout = TimeSpan.FromMinutes(10),
        };
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.ConfigureServices = services => services.AddSingleton(sp =>
            {
                var client = sp.GetRequiredService<DidComm.Facade.DidCommClient>();
                return new MediatorClientChannel(client, settings, receivesInbound: true, time, sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                    send: null, resolveEndpoint: null, unpack: async (text, ct) =>
                    {
                        Interlocked.Increment(ref unpacking);
                        try
                        {
                            return await client.UnpackDeliveredAsync(text, ct);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref unpacking);
                        }
                    });
            });
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var notes = new List<string> { (await PickupE2E.SendAsync(mediator, alice, recipient, "first")).Id };

        using var cts = new CancellationTokenSource();
        var got = new List<string>();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var item = await PickupE2E.NextAsync(stream);
        yielded[item.Id] = 0;
        got.Add(item.Unpacked!.Message.Id);
        for (var i = 0; i < Pushed; i++)
            notes.Add((await PickupE2E.SendAsync(mediator, alice, recipient, $"p{i}")).Id);
        await mediator.PushesIdleAsync();

        var rounds = 0;
        while (got.Count < notes.Count)
        {
            var next = stream.MoveNextAsync().AsTask();
            // Either an item, or the stream is idle (no request waiting, its buffer empty, the
            // mediator's pushes done): then move both clocks past the lease and the poll.
            while (!next.IsCompleted)
            {
                await MediatorTestServer.WaitUntilAsync(() => next.IsCompleted || (harness.Channel.PendingCount == 0 && Volatile.Read(ref unpacking) == 0 && Mailbox(harness.Channel) == 0 && time.ScheduledTimers >= 1), "an item, or an idle stream");
                if (next.IsCompleted)
                    break;
                await mediator.PushesIdleAsync();
                if (next.IsCompleted || Mailbox(harness.Channel) > 0 || Volatile.Read(ref unpacking) > 0 || harness.Channel.PendingCount > 0)
                    continue;
                rounds++;
                if (rounds >= 20)
                {
                    var trail = string.Join("\n", mediator.Logs.CategorizedRecords
                        .Where(r => r.Category.StartsWith("DidComm.Mediator.Client", StringComparison.Ordinal) || r.Category.Contains("Pickup") || r.Category.Contains("Push"))
                        .Select(r => $"{r.Category} {r.Level} {r.Text}").TakeLast(80));
                    throw new Xunit.Sdk.XunitException($"DEBUG rounds={rounds} got={got.Count}/{notes.Count} store={Qa2.MessageCount(mediator)} pushed={mediator.PushOutcome(PushOutcomes.PushedLive)} outcomes={string.Join(",", mediator.PushOutcomes.Distinct())} pending={harness.Channel.PendingCount} timers={time.ScheduledTimers}\n{trail}");
                }
                mediator.Clock.Advance(TimeSpan.FromSeconds(61));
                time.Advance(Poll);
            }
            (await PickupE2E.Within(next)).Should().BeTrue();
            item = stream.Current;
            item.Error.Should().BeNull();
            yielded[item.Id] = 0;
            got.Add(item.Unpacked!.Message.Id);
        }
        var last = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "everything was acknowledged");
        await PickupE2E.StopAsync(stream, last, cts);

        got.Should().OnlyHaveUniqueItems("no message is yielded twice").And.BeEquivalentTo(notes, "no message is lost");
        early.Should().BeEmpty("no id is acknowledged before the stream yielded it");
    }

    // How many pushes wait in the live streams' buffers of the channel.
    private static int Mailbox(MediatorClientChannel channel)
    {
        var field = typeof(MediatorClientChannel).GetField("_live", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var live = (ConcurrentDictionary<string, LiveRegistration>)field.GetValue(channel)!;
        return live.Values.Sum(r => r.Pushes.Count);
    }

    private const int LiveRegistrationCapacity = 8;

    /// <summary>A store that checks, at every acknowledgement, that each id was yielded to the consumer before.</summary>
    private sealed class WatchingStore(Qa2Store inner, ConcurrentDictionary<string, byte> yielded, ConcurrentQueue<string> early) : IMessageStore, IQa2WrappingStore
    {
        public InMemoryMessageStore Inner => inner.Inner;

        public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
        {
            foreach (var id in messageIds)
            {
                if (!yielded.ContainsKey(id))
                    early.Enqueue(id);
            }
            return inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
        }

        public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => inner.EnqueueAsync(message, lease, now, ct);
        public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => inner.LeaseAsync(request, ct);
        public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
        public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
        public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => inner.ReleaseLeasesAsync(leaseOwner, ct);
        public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
        public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
        public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => inner.PurgeGrantAsync(grantPeerDid, ct);
        public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => inner.PurgeExpiredAsync(now, ct);
    }
}
