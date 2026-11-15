using System.Net;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Transports;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-2 (#96): the issue's acceptance flow end to end on the #95 TestServer, through the library's
/// own clients only (no hand-rolled enrolment): <see cref="MediationClient"/> enrols →
/// <c>DidCommServiceBuilder.ForMediation(grant)</c> DID document → Alice sends → <see cref="PickupClient"/>
/// fetches → acknowledged → <c>status</c> 0, over HTTP, over a WebSocket (a mediator whose DID names a
/// <c>ws://</c> service, no override) and over a WebSocket in live mode. Partial unpack (a message
/// encrypted to someone else, a corrupt payload, an expired inner message) and the HTTP refusals of
/// live delivery (zero requests at the server).
/// </summary>
public sealed class PickupClientAcceptanceQa2Tests
{
    [Fact]
    public async Task Http_enrol_with_the_client_send_fetch_acknowledge_then_status_zero_and_no_id_in_any_log_line()
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient, _) = await Qa2.EnrolWithClientAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var first = await PickupE2E.SendAsync(mediator, alice, recipient, "one");
        var second = await PickupE2E.SendAsync(mediator, alice, recipient, "two");

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);

        var queueIds = batch.Items.Select(i => i.Id).ToArray();
        queueIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        PickupE2E.ShouldBeAlicesNote(batch.Items[0], alice, recipient, first);
        PickupE2E.ShouldBeAlicesNote(batch.Items[1], alice, recipient, second);
        (await harness.Pickup.GetStatusAsync(mediator.Did, bob.Did)).MessageCount.Should().Be(0, "fetch acknowledged both (FR-PICK-11)");
        mediator.Store.MessageCount.Should().Be(0);
        PickupE2E.ShouldAllBeStampedWith(observer, bob.Did, mediator.Clock.Now);

        // No DID, kid, queue id or inner message id in any line of either container.
        mediator.LeakingLogLines(recipient.Did, bob.Did).Should().BeEmpty();
        Qa2.LinesWithAny(mediator, queueIds.Concat(new[] { first.Id, second.Id })).Should().BeEmpty();
    }

    [Fact]
    public async Task WebSocket_from_the_DID_document_enrol_send_fetch_acknowledge_then_status_zero()
    {
        var identity = await MediatorClientHarness.MintMediatorIdentityAsync("ws");
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Identity = identity);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient, _) = await Qa2.EnrolWithClientAsync(mediator, harness);
        harness.Connects.Should().BeGreaterThan(0, "the route is the DID document's ws:// entry");
        var alice = await harness.NewAliceAsync();
        // Alice has only HTTP, so her forward is posted by hand (the mediator's DID names ws://).
        var note = alice.Note(recipient.Did, "over ws");
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await alice.AuthcryptAsync(note, recipient.Did));

        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10));

        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        await Qa2.StoreHoldsAsync(mediator, 0, "fetch acknowledged it");
        (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did))).MessageCount.Should().Be(0);
        var drained = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10));
        drained.Drained.Should().BeTrue();
        mediator.LeakingLogLines(recipient.Did, bob.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task WebSocket_live_queued_ones_by_the_drain_later_ones_by_push_every_item_acknowledged_live_off_at_the_end()
    {
        var identity = await MediatorClientHarness.MintMediatorIdentityAsync("ws");
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer, o => o.Identity = identity);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient, _) = await Qa2.EnrolWithClientAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var queued = new List<Message>();
        for (var i = 0; i < 3; i++)
        {
            var note = alice.Note(recipient.Did, $"queued {i}");
            await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await alice.AuthcryptAsync(note, recipient.Did));
            queued.Add(note);
        }

        var got = new List<string>();
        var queueIds = new List<string>();
        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, ct: cts.Token).GetAsyncEnumerator();
        for (var i = 0; i < 3; i++)
        {
            var item = await PickupE2E.NextAsync(stream);
            queueIds.Add(item.Id);
            got.Add(item.Unpacked!.Message.Id);
        }
        got.Should().Equal(queued.Select(q => q.Id), "what was queued before live mode arrives by the drain, oldest first");

        // Asking for the fourth takes the third; nothing else is ready, so the three are acknowledged.
        var fourth = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "the three drained items were acknowledged once the fourth was asked for");
        // The drain's last round has been answered (status): from here on, arrivals are pushed.
        await observer.WaitForAsync(bob.Did, 4);
        await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 0, "the stream waits for a push");
        var pushed = new List<Message>();
        for (var i = 0; i < 2; i++)
        {
            var note = alice.Note(recipient.Did, $"pushed {i}");
            await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await alice.AuthcryptAsync(note, recipient.Did));
            pushed.Add(note);
        }
        (await PickupE2E.Within(fourth)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, pushed[0]);
        queueIds.Add(stream.Current.Id);
        PickupE2E.ShouldBeAlicesNote(await PickupE2E.NextAsync(stream), alice, recipient, pushed[1]);
        queueIds.Add(stream.Current.Id);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(2, "both arrived after live delivery was on");

        // Ask once more: the last pushed one is taken and acknowledged; then stop.
        var sixth = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "every item was acknowledged");
        await PickupE2E.StopAsync(stream, sixth, cts);

        await MediatorTestServer.WaitUntilAsync(() => observer.KindsFrom(bob.Did).LastOrDefault() == "live-off", "live mode is turned off at the end");
        (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did))).MessageCount.Should().Be(0);
        PickupE2E.ShouldAllBeStampedWith(observer, bob.Did, mediator.Clock.Now);
        mediator.LeakingLogLines(recipient.Did, bob.Did).Should().BeEmpty();
        Qa2.LinesWithAny(mediator, queueIds.Concat(queued.Select(q => q.Id)).Concat(pushed.Select(p => p.Id))).Should().BeEmpty("no queue id or inner message id in any line");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_unpack_returns_the_good_acknowledges_only_them_and_reports_each_bad_one_by_id_so_it_can_be_discarded(bool webSocket)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = webSocket);
        var endpoint = webSocket ? harness.WebSocketEndpoint : null;
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();

        var good1 = await PickupE2E.SendAsync(mediator, alice, recipient, "good 1");
        // 1. Encrypted to someone else.
        await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);
        // 2. Corrupt: not an envelope at all.
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, "{\"protected\":\"not-a-jwe\",\"ciphertext\":\"!!\"}");
        // 3. An inner message that expires before Bob fetches it (the mediator cannot see inside).
        var expiring = alice.Note(recipient.Did, "expiring");
        expiring.ExpiresTime = mediator.Clock.Now.ToUnixTimeSeconds() + 5;
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await alice.AuthcryptAsync(expiring, recipient.Did));
        var good2 = await PickupE2E.SendAsync(mediator, alice, recipient, "good 2");
        mediator.Clock.Advance(TimeSpan.FromMinutes(10));

        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10, serviceEndpointOverride: endpoint));

        batch.Items.Should().HaveCount(5);
        batch.Items.Where(i => i.Unpacked is not null).Select(i => i.Unpacked!.Message.Id).Should().Equal(good1.Id, good2.Id);
        var failures = batch.Items.Where(i => i.Unpacked is null).ToArray();
        failures.Should().HaveCount(3).And.OnlyContain(i => i.Error != null && MediatorRecipient.IsRandomQueueId(i.Id));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(3, "the three bad ones are never acknowledged and still counted");

        var discarded = await PickupE2E.Within(harness.Pickup.AckAsync(mediator.Did, bob.Did, failures.Select(f => f.Id).ToArray(), endpoint));
        discarded.MessageCount.Should().Be(0, "the caller discards the failures with their ids");
        mediator.Store.MessageCount.Should().Be(0);
        mediator.LeakingLogLines(recipient.Did).Should().BeEmpty();
        Qa2.LinesWithAny(mediator, batch.Items.Select(i => i.Id).Append(good1.Id).Append(good2.Id).Append(expiring.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Over_HTTP_live_delivery_and_the_live_stream_are_refused_before_any_request_reaches_the_server()
    {
        var counter = new Qa2RequestCounter();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigurePipeline = counter.Install);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var bob = await harness.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var before = counter.Total;
        var connects = harness.Connects;

        // The DID document's http entry, and an explicit http override.
        foreach (var endpoint in new Uri?[] { null, harness.HttpEndpoint })
        {
            await FluentActions.Awaiting(() => harness.Pickup.SetLiveDeliveryAsync(mediator.Did, bob.Did, true, endpoint)).Should().ThrowAsync<InvalidOperationException>();
            await FluentActions.Awaiting(() => harness.Pickup.SetLiveDeliveryAsync(mediator.Did, bob.Did, false, endpoint)).Should().ThrowAsync<InvalidOperationException>();
            await using var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, endpoint).GetAsyncEnumerator();
            await FluentActions.Awaiting(() => PickupE2E.Within(stream.MoveNextAsync())).Should().ThrowAsync<InvalidOperationException>();
        }

        // Without DI a WebSocket route cannot receive: refused before sending too.
        var bare = new PickupClient(harness.Client);
        await FluentActions.Awaiting(() => bare.SetLiveDeliveryAsync(mediator.Did, bob.Did, true, harness.WebSocketEndpoint)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => bare.FetchAsync(mediator.Did, bob.Did, 1, serviceEndpointOverride: harness.WebSocketEndpoint)).Should().ThrowAsync<InvalidOperationException>();
        await using (var stream = bare.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).GetAsyncEnumerator())
            await FluentActions.Awaiting(() => PickupE2E.Within(stream.MoveNextAsync())).Should().ThrowAsync<InvalidOperationException>();

        counter.Total.Should().Be(before, "every refusal happens before anything is sent");
        harness.Connects.Should().Be(connects, "no WebSocket was opened either");
        harness.Channel.LiveStreamCount.Should().Be(0);
    }
}
