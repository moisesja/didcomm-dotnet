using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT3 (#111, fix round 2) and S3 (fix round 3): how often the live stream sends its cycle requests
/// (live-on, then the drain's delivery requests), counted over ten simulated minutes
/// (<see cref="SimClock"/>, <see cref="StreamDriver"/>); nothing sleeps.
/// <para>
/// While every cycle request fails, under mixes <see cref="LiveStreamRt2CTests"/> does not set up
/// (acknowledgements that are refused and accepted in turn, pushes that bring only messages the
/// stream was already handed): the cycle requests are never closer together than the back-off
/// schedule (1, 2, 4, 8, 16, then 30 s), so never more than 24 in ten minutes.
/// </para>
/// <para>
/// While every cycle request is answered, what each cycle request follows is read from the log of
/// requests and pinned as measured: the stream's start, one
/// <see cref="MediatorClientOptions.LivePollInterval"/> after the drain before it ended, or the
/// first back-off (1 s) after an acknowledgement that was refused. With acknowledgements refused
/// and accepted in turn, each refused one follows a push, so the pushes set the rate; without
/// further pushes one cycle per poll interval is left; and with every acknowledgement refused and
/// no further push, no cycle request follows the first: the acknowledgement is sent again after
/// each back-off, and the stream ends with its refusal once the give-up alarm has fired.
/// </para>
/// </summary>
public sealed class LiveStreamBackoffRt3Tests(ITestOutputHelper output)
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Every live-on fails at once; a push brings a new message every P seconds and the consumer
    /// takes each; the mediator refuses every second acknowledgement and accepts the others. A
    /// refused acknowledgement is one more failed step: it lengthens the back-off, and no cycle
    /// request is sent before the back-off it armed has passed. The stream goes on (each new push
    /// and each accepted acknowledgement lets the give-up alarm go).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(45)]
    public async Task With_every_cycle_failing_and_acknowledgements_refused_and_accepted_in_turn_cycles_are_never_closer_than_the_back_off(int pushEverySeconds)
    {
        var exchange = new SimExchange();
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r => !r.Live ? Status(r, live: false) : throw new TimeoutException("no answer (test)"));
        var acks = 0;
        exchange.Inner.On(MessagePickup.MessagesReceivedType, r => Interlocked.Increment(ref acks) % 2 == 1 ? throw new TimeoutException("no answer (test)") : Status(r));
        var pushes = 0;
        var every = TimeSpan.FromSeconds(pushEverySeconds);
        using var pusher = exchange.Clock.CreateTimer(_ =>
        {
            var n = Interlocked.Increment(ref pushes);
            exchange.Inner.Registration?.TryPush(Push(($"p{n}", $"ok:p{n}")));
        }, null, every + TimeSpan.FromMilliseconds(500), every);
        await using var driver = new StreamDriver(exchange);

        await driver.RunAsync(() => false, TenMinutes);

        var liveOns = exchange.Log.Where(r => r.At <= TenMinutes && r.Type == LiveOn).Select(r => r.At).ToArray();
        output.WriteLine($"push every {pushEverySeconds} s: {liveOns.Length} live-on in 10 min, {exchange.Log.Count(r => r.At <= TenMinutes && r.Type == Ack)} acknowledgements sent, {exchange.Inner.AcknowledgedIds.Count} ids; ended at {driver.EndedAt?.ToString() ?? "never"}");
        output.WriteLine(string.Join(", ", liveOns.Select(t => t.TotalSeconds)));
        driver.EndedAt.Should().BeNull("each push with a new message lets the give-up alarm go");
        liveOns.Should().NotBeEmpty();
        liveOns.Length.Should().BeLessThanOrEqualTo(24, "the schedule allows 24 cycle requests in ten minutes");
        NeverCloserThanTheSchedule(liveOns);
    }

    /// <summary>
    /// Every live-on fails at once, and every second a push brings the same message again (the
    /// consumer was handed it once and it is not acknowledged, as the acknowledgements get no
    /// answer). Nothing lets the give-up alarm go after the first push, so the stream ends with the
    /// failure once the alarm has fired; until then its cycle requests follow the schedule.
    /// </summary>
    [Fact]
    public async Task With_every_request_failing_and_pushes_that_bring_nothing_new_cycles_follow_the_back_off_and_the_stream_gives_up()
    {
        var exchange = new SimExchange();
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r => !r.Live ? Status(r, live: false) : throw new TimeoutException("no answer (test)"));
        exchange.Inner.On(MessagePickup.MessagesReceivedType, r => r.OneWay ? Status(r) : throw new TimeoutException("no answer (test)"));
        using var pusher = exchange.Clock.CreateTimer(_ => exchange.Inner.Registration?.TryPush(Push(("same", "ok:same"))), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
        await using var driver = new StreamDriver(exchange);

        await driver.RunAsync(() => false, TenMinutes);

        var liveOns = exchange.Log.Where(r => r.Type == LiveOn).Select(r => r.At).ToArray();
        var giveUp = exchange.Options.RequestTimeout + LiveDeliveryStream.GiveUpMargin;
        output.WriteLine($"{liveOns.Length} live-on; ended at {driver.EndedAt?.ToString() ?? "never"} with {driver.EndedWith?.GetType().Name ?? "nothing"}; {driver.Items.Count} items");
        output.WriteLine(string.Join(", ", exchange.Log.Take(30).Select(l => $"{l.At.TotalSeconds}:{l.Type}")));
        driver.Items.Count(i => i.Item.Id == "same").Should().Be(1, "a push that brings back only a message the stream holds opens nothing");
        driver.EndedWith.Should().BeOfType<TimeoutException>("the requests kept failing and nothing new came");
        driver.EndedAt.Should().NotBeNull();
        driver.EndedAt!.Value.Should().BeGreaterThanOrEqualTo(giveUp, "the stream gives up only once its give-up alarm has fired");
        driver.EndedAt.Value.Should().BeLessThanOrEqualTo(giveUp + giveUp + LiveDeliveryStream.MaxBackoff, "and at the first failed step after it, one back-off later at most (the alarm was armed again once, by the first push)");
        NeverCloserThanTheSchedule(liveOns);
    }

    /// <summary>
    /// The pin of what RT3 measured (the coordinator's judgment: it does not reopen owner decision
    /// F, whose sentence is about cycle requests that fail; here they are answered). Every cycle
    /// request is answered (live-on with a status, each drain round with "nothing queued"); a push
    /// brings a new message every P seconds and the consumer takes each; the mediator refuses every
    /// second acknowledgement and accepts the others. Measured, for P = 1 s and for P = 2 s alike:
    /// 300 live-on requests in ten minutes, each with one drain round.
    /// </summary>
    /// <remarks>
    /// What drives them, as the log of requests shows and the test asserts: after the stream's
    /// first, every live-on is sent exactly one second (the first back-off) after an
    /// acknowledgement that was refused, one for one; and every refused acknowledgement was sent at
    /// the instant of a push and names the message that push brought. A refused acknowledgement is a
    /// failed step: the stream backs off, and a cycle follows a failed step once its back-off has
    /// passed. The back-off stays at one second because the drain of each of these cycles ends,
    /// which starts the back-off again. So the mediator's pushes set the rate:
    /// <list type="bullet">
    /// <item>
    /// P = 1 s: pushes at 1.5, 2.5, ..., 599.5 s, one acknowledgement each (599); the 300 sent at
    /// 1.5, 3.5, ..., 599.5 s are refused, and a live-on follows at 2.5, 4.5, ..., 598.5 s (299; the
    /// 300th would be at 600.5 s). With the stream's first: 300.
    /// </item>
    /// <item>
    /// P = 2 s: pushes at 2.5, 4.5, ..., 598.5 s (299); the acknowledgement of each is refused when
    /// first sent and accepted one second later (598 sent), and a live-on follows that second
    /// attempt at 3.5, 5.5, ..., 599.5 s (299). With the stream's first: 300.
    /// </item>
    /// </list>
    /// Without further pushes the same stream runs one cycle per poll interval:
    /// <see cref="With_cycles_answered_and_no_push_after_the_first_one_cycle_per_poll_interval_is_left"/>.
    /// </remarks>
    [Theory]
    [InlineData(1, 300, 599)]
    [InlineData(2, 300, 598)]
    public async Task With_cycles_answered_and_acknowledgements_refused_and_accepted_in_turn_one_cycle_follows_each_refused_acknowledgement(int pushEverySeconds, int measuredLiveOns, int measuredAcknowledgements)
    {
        var exchange = new SimExchange();
        var (refusals, pushes, timer) = RefuseEverySecondAcknowledgement(exchange, TimeSpan.FromSeconds(pushEverySeconds), pushes: int.MaxValue);
        using var pusher = timer;
        await using var driver = new StreamDriver(exchange);

        await driver.RunAsync(() => false, TenMinutes);

        var log = exchange.Log.Where(r => r.At <= TenMinutes).ToArray();
        var liveOns = log.Where(r => r.Type == LiveOn).Select(r => r.At).ToArray();
        var refused = Snapshot(refusals).Where(r => r.At <= TenMinutes).ToArray();
        var pushed = Snapshot(pushes).Where(p => p.At <= TenMinutes).ToArray();
        output.WriteLine($"push every {pushEverySeconds} s: {liveOns.Length} live-on and {log.Count(r => r.Type == Drain)} drain rounds in 10 min; {pushed.Length} pushes; {log.Count(r => r.Type == Ack)} acknowledgements sent, {refused.Length} of them refused; {driver.Stream.TakenCount} waiting; ended at {driver.EndedAt?.ToString() ?? "never"}");
        output.WriteLine("the first requests: " + string.Join(", ", log.Take(24).Select(l => $"{l.At.TotalSeconds}:{l.Type}")));
        output.WriteLine("pushes: " + string.Join(", ", pushed.Take(8).Select(p => $"{p.At.TotalSeconds}:{p.Id}")) + ", ...");
        output.WriteLine("refused acknowledgements: " + string.Join(", ", refused.Take(8).Select(r => $"{r.At.TotalSeconds}:[{string.Join(" ", r.Ids)}]")) + ", ...");
        output.WriteLine("live-on at: " + string.Join(", ", liveOns.Take(8).Select(t => t.TotalSeconds)) + ", ...");

        driver.EndedAt.Should().BeNull("each push with a new message and each accepted acknowledgement lets the give-up alarm go");
        liveOns.Should().HaveCount(measuredLiveOns, "as measured");
        log.Count(r => r.Type == Drain).Should().Be(measuredLiveOns, "the drain of each cycle is one round, answered with nothing queued");
        log.Count(r => r.Type == Ack).Should().Be(measuredAcknowledgements, "as measured");
        liveOns[0].Should().Be(TimeSpan.Zero, "the stream's first cycle");
        refused.Should().OnlyContain(r => pushed.Any(p => p.At == r.At && r.Ids.Contains(p.Id)),
            "every refused acknowledgement was sent at the instant of a push and names the message that push brought");
        liveOns.Skip(1).Should().Equal(refused.Select(r => r.At + LiveDeliveryStream.FirstBackoff).Where(t => t <= TenMinutes),
            "every other cycle follows a refused acknowledgement, one for one, by the first back-off");
    }

    /// <summary>
    /// The same stream and the same acknowledgements (every second one refused), with one push and
    /// none after it: the cycles fall from 300 to 11 in ten minutes. The push's acknowledgement is
    /// refused when first sent and accepted one second later, and one cycle follows it; after
    /// that, nothing is left to acknowledge and every cycle is sent one poll interval (60 s) after
    /// the drain before it ended. For a first push at 1.5 s: live-on at 0 s, at 2.5 s, then at 62.5,
    /// 122.5, ..., 542.5 s (the next would be at 602.5 s); for one at 2.5 s, each one second later.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task With_cycles_answered_and_no_push_after_the_first_one_cycle_per_poll_interval_is_left(int pushAfterSeconds)
    {
        var exchange = new SimExchange();
        var (refusals, pushes, timer) = RefuseEverySecondAcknowledgement(exchange, TimeSpan.FromSeconds(pushAfterSeconds), pushes: 1);
        using var pusher = timer;
        await using var driver = new StreamDriver(exchange);

        await driver.RunAsync(() => false, TenMinutes);

        var log = exchange.Log.Where(r => r.At <= TenMinutes).ToArray();
        var liveOns = log.Where(r => r.Type == LiveOn).Select(r => r.At).ToArray();
        var pushed = Snapshot(pushes).Should().ContainSingle("one push, none after it").Subject.At;
        output.WriteLine($"one push at {pushed.TotalSeconds} s: {liveOns.Length} live-on in 10 min; {log.Count(r => r.Type == Ack)} acknowledgements sent; ended at {driver.EndedAt?.ToString() ?? "never"}");
        output.WriteLine(string.Join(", ", log.Select(l => $"{l.At.TotalSeconds}:{l.Type}")));

        driver.EndedAt.Should().BeNull();
        Snapshot(refusals).Select(r => r.At).Should().Equal(new[] { pushed }, "the push's acknowledgement was refused when first sent");
        var afterTheRefusal = pushed + LiveDeliveryStream.FirstBackoff;
        log.Where(r => r.Type == Ack).Select(r => r.At).Should().Equal(new[] { pushed, afterTheRefusal }, "and accepted one back-off later; nothing is acknowledged after that");
        var interval = exchange.Options.LivePollInterval;
        var perPollInterval = Enumerable.Range(1, 600).Select(k => afterTheRefusal + k * interval).TakeWhile(t => t <= TenMinutes);
        liveOns.Should().Equal(new[] { TimeSpan.Zero, afterTheRefusal }.Concat(perPollInterval),
            "the stream's first cycle, the one that follows the refused acknowledgement, then one per poll interval");
        liveOns.Should().HaveCount(11, "as measured");
    }

    /// <summary>
    /// The measurement of fix round 3 (Message Pickup 3.0, where an acknowledgement is a request
    /// the mediator answers): every cycle request is answered, one delivery brings three messages
    /// the consumer takes, the mediator refuses every acknowledgement, and nothing is pushed after
    /// that delivery. Measured: one live-on request in ten minutes, the one the stream starts
    /// with, and one drain round. No cycle request follows it: the stream ends with the refusal.
    /// </summary>
    /// <remarks>
    /// The schedule, as the log of requests shows and the test asserts. The delivery comes at 0 s
    /// (as the answer of the first drain round, or as a push right after that drain), and its
    /// acknowledgement is sent at once. Each refusal is a failed step, so the stream backs off,
    /// and what it sends when a back-off has passed is the acknowledgement again (what was taken
    /// is acknowledged before a cycle starts), never a cycle request. The first refusal arms the
    /// give-up alarm for <see cref="MediatorClientOptions.RequestTimeout"/> plus
    /// <see cref="LiveDeliveryStream.GiveUpMargin"/> (60 s); nothing lets it go (no drain ends, no
    /// round or push brings something new, no acknowledgement is accepted), and the first refusal
    /// after it has fired ends the stream with that refusal's exception. At its end the stream
    /// sends the acknowledgement once more, and live-off.
    /// <list type="bullet">
    /// <item>
    /// Refused at once: the acknowledgement is sent at 0 s, then after back-offs of 1, 2, 4, 8, 16
    /// and 30 s at 1, 3, 7, 15, 31 and 61 s; the alarm fired at 60 s, so the stream ends at 61 s.
    /// </item>
    /// <item>
    /// Refused after <see cref="MediatorClientOptions.RequestTimeout"/> (30 s without an answer):
    /// sent at 0 s and refused at 30 s (the alarm is armed then, and fires at 90 s), sent at 31 s
    /// and refused at 61 s, sent at 63 s and refused at 93 s, where the stream ends; its last
    /// acknowledgement takes another 30 s, so the enumeration returns at 123 s.
    /// </item>
    /// </list>
    /// A host that enumerates the stream again starts a new one; that is the host's loop, and not
    /// measured here.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task With_cycles_answered_every_acknowledgement_refused_and_no_push_after_the_first_delivery_no_cycle_request_follows_the_first_and_the_stream_ends_with_the_refusal(bool deliveredByAPush, bool refusedAfterTheRequestTimeout)
    {
        var exchange = new SimExchange();
        var refusal = refusedAfterTheRequestTimeout ? exchange.Options.RequestTimeout : TimeSpan.Zero;
        var messages = new[] { ("m1", "ok:m1"), ("m2", "ok:m2"), ("m3", "ok:m3") };
        exchange.Inner.On(MessagePickup.MessagesReceivedType, (Func<SentRequest, InboundMessageSnapshot>)(_ =>
        {
            if (refusal > TimeSpan.Zero)
                exchange.Clock.Advance(refusal); // how long the mediator leaves it unanswered
            throw new TimeoutException("no answer (test)");
        }));
        var rounds = 0;
        if (!deliveredByAPush)
            exchange.Inner.On(MessagePickup.DeliveryRequestType, r => Interlocked.Increment(ref rounds) == 1 ? Delivery(r, messages) : Status(r));
        await using var driver = new StreamDriver(exchange);
        var pushed = !deliveredByAPush;

        await driver.RunAsync(() => false, TenMinutes, onWait: () =>
        {
            if (pushed)
                return false;
            pushed = true; // the first wait: the first drain has ended with nothing queued
            return exchange.Inner.Registration!.TryPush(Push(messages));
        });
        var endedAt = driver.EndedAt;
        var sentWhenItEnded = exchange.Log.Count;
        var timersLeft = exchange.Clock.LiveTimers;
        exchange.Clock.Advance(TenMinutes); // the rest of the ten minutes, and more: nothing is left to send

        var log = exchange.Log;
        var liveOns = log.Where(r => r.Type == LiveOn).Select(r => r.At).ToArray();
        output.WriteLine($"delivered by a {(deliveredByAPush ? "push" : "drain round")}, each acknowledgement refused after {refusal.TotalSeconds} s: {liveOns.Length} live-on and {log.Count(r => r.Type == Drain)} drain round(s) in 10 min; {log.Count(r => r.Type == Ack)} acknowledgements sent; the enumeration ended at {endedAt?.TotalSeconds.ToString() ?? "never"} s with {driver.EndedWith?.GetType().Name ?? "nothing"}; {driver.Items.Count} items");
        output.WriteLine(string.Join(", ", log.Select(l => $"{l.At.TotalSeconds}:{l.Type}")));

        driver.Items.Select(i => (i.Item.Id, i.At)).Should().Equal(messages.Select(m => (m.Item1, TimeSpan.Zero)), "the consumer took the three messages of the one delivery");
        liveOns.Should().Equal(new[] { TimeSpan.Zero }, "as measured: the one cycle request the stream starts with, and none after it");
        log.Count(r => r.Type == Drain).Should().Be(1, "as measured: the one round of that cycle");

        // The acknowledgement: at once, then one back-off after each refusal, until one is refused
        // once the give-up alarm (armed at the first refusal) has fired.
        var giveUp = exchange.Options.RequestTimeout + LiveDeliveryStream.GiveUpMargin;
        var sent = new List<TimeSpan> { TimeSpan.Zero };
        while (sent[^1] + refusal < refusal + giveUp)
            sent.Add(sent[^1] + refusal + LiveDeliveryStream.Backoff(sent.Count));
        var endsAt = sent[^1] + refusal;
        var expected = new List<(TimeSpan, string)> { (TimeSpan.Zero, LiveOn), (TimeSpan.Zero, Drain) };
        expected.AddRange(sent.Select(at => (at, Ack)));
        expected.Add((endsAt, Ack)); // the stream's end: what was taken, once more
        expected.Add((endsAt + refusal, LiveOff));
        log.Should().Equal(expected, "every request after the first cycle is the acknowledgement, sent again one back-off after each refusal");
        sent.Select(t => t.TotalSeconds).Should().Equal(refusedAfterTheRequestTimeout ? new[] { 0.0, 31, 63 } : new[] { 0.0, 1, 3, 7, 15, 31, 61 }, "as measured");
        driver.EndedWith.Should().BeOfType<TimeoutException>("the refusal that came once the give-up alarm had fired");
        endedAt.Should().Be(endsAt + refusal, "the enumeration returns once the stream's end has sent its acknowledgement and live-off");
        endedAt!.Value.TotalSeconds.Should().Be(refusedAfterTheRequestTimeout ? 123 : 61, "as measured");
        timersLeft.Should().Be(0, "the stream left no timer behind");
        log.Should().HaveCount(sentWhenItEnded, "nothing was sent in the rest of the ten minutes");
    }

    /// <summary>
    /// The same under Message Pickup 4.0, where an acknowledgement is sent one way: nothing answers
    /// it, so a mediator cannot refuse it, and the stream counts it as acknowledged once it is
    /// sent. What can fail is the send itself; here it fails with a transport failure. One push
    /// brings three messages the consumer takes; nothing is pushed after it; every cycle request is
    /// answered.
    /// <list type="bullet">
    /// <item>
    /// Every send of the acknowledgement fails at once: measured as under 3.0. One live-on in ten
    /// minutes; the acknowledgement is sent at 0, 1, 3, 7, 15, 31 and 61 s, and the stream ends at
    /// 61 s with the send's failure.
    /// </item>
    /// <item>
    /// The sends go through, whatever the mediator does with them: one acknowledgement, and one
    /// cycle per poll interval, at 0, 60, ..., 600 s: 11 live-on requests, counting the one at
    /// exactly ten minutes.
    /// </item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(true, 1, 8)]
    [InlineData(false, 11, 1)]
    public async Task Under_4_0_an_acknowledgement_whose_send_fails_is_retried_as_under_3_0_and_one_that_is_sent_counts_as_acknowledged(bool everySendFails, int measuredLiveOns, int measuredAcknowledgements)
    {
        var exchange = new SimExchange();
        if (everySendFails)
            exchange.Inner.OneWay = _ => Task.FromException(new TransportException("the send failed (test)"));
        var messages = new[] { ("m1", "ok:m1"), ("m2", "ok:m2"), ("m3", "ok:m3") };
        var pushed = false;

        var (items, endedWith, endedAt) = await RunPickup40Async(exchange, TenMinutes, onWait: () =>
        {
            if (pushed)
                return false;
            pushed = true; // the first wait: the first drain has ended with nothing queued
            return exchange.Inner.Registration!.TryPush(Push40(messages));
        });

        var log = Requests40(exchange).Where(r => r.At <= TenMinutes).ToArray();
        var liveOns = log.Where(r => r.Type == LiveOn).Select(r => r.At.TotalSeconds).ToArray();
        var acks = log.Where(r => r.Type == Ack).Select(r => r.At.TotalSeconds).ToArray();
        output.WriteLine($"4.0, every send of the acknowledgement fails: {everySendFails}: {liveOns.Length} live-on and {log.Count(r => r.Type == Drain)} drain round(s) in 10 min; {acks.Length} acknowledgements sent; ended at {endedAt?.TotalSeconds.ToString() ?? "never"} s with {endedWith?.GetType().Name ?? "nothing"}; {items.Count} items");
        output.WriteLine(string.Join(", ", log.Select(l => $"{l.At.TotalSeconds}:{l.Type}")));

        items.Select(i => i.Item.Id).Should().Equal(messages.Select(m => m.Item1), "the consumer took the three messages of the one push");
        exchange.Inner.Sent.Where(s => s.Is(MessagePickup40.MessagesReceivedType)).Should().OnlyContain(s => s.OneWay, "under 4.0 an acknowledgement is sent one way");
        liveOns.Should().HaveCount(measuredLiveOns, "as measured");
        acks.Should().HaveCount(measuredAcknowledgements, "as measured");
        if (everySendFails)
        {
            liveOns.Should().Equal(new[] { 0.0 }, "no cycle request follows the first");
            acks.Should().Equal(new[] { 0.0, 1, 3, 7, 15, 31, 61, 61 }, "sent again one back-off after each failed send; the last is the stream's end");
            endedWith.Should().BeOfType<TransportException>("the failed send that came once the give-up alarm had fired");
            endedAt.Should().Be(TimeSpan.FromSeconds(61));
        }
        else
        {
            acks.Should().Equal(new[] { 0.0 }, "sent once, which is all the stream waits for");
            liveOns.Should().Equal(Enumerable.Range(0, 11).Select(k => k * exchange.Options.LivePollInterval.TotalSeconds), "one cycle per poll interval");
            endedAt.Should().BeNull("the stream goes on");
        }
    }

    // The mediator of the two tests above it: it answers every cycle request, refuses every second
    // acknowledgement (the first, the third, ...) and accepts the others, and pushes a new message
    // every `every`, the first one half a second after that, `pushes` times. The lists record, with
    // the simulated time of each, the acknowledgements that were refused (and the ids they named)
    // and the pushes made (and the id of the message each brought).
    private static (List<(TimeSpan At, string[] Ids)> Refusals, List<(TimeSpan At, string Id)> Pushes, ITimer Pusher) RefuseEverySecondAcknowledgement(SimExchange exchange, TimeSpan every, int pushes)
    {
        var refusals = new List<(TimeSpan At, string[] Ids)>();
        var pushed = new List<(TimeSpan At, string Id)>();
        var acks = 0;
        exchange.Inner.On(MessagePickup.MessagesReceivedType, r =>
        {
            if (Interlocked.Increment(ref acks) % 2 == 0)
                return Status(r);
            lock (refusals)
                refusals.Add((exchange.Clock.Now, r.Ids.ToArray()));
            throw new TimeoutException("no answer (test)");
        });
        var made = 0;
        ITimer? pusher = null;
        pusher = exchange.Clock.CreateTimer(_ =>
        {
            var n = Interlocked.Increment(ref made);
            lock (pushed)
                pushed.Add((exchange.Clock.Now, $"p{n}"));
            exchange.Inner.Registration!.TryPush(Push(($"p{n}", $"ok:p{n}")));
            // After the last push the timer is stopped: the driver moves the clock from one timer
            // to the next and expects each to wake the stream.
            if (n >= pushes)
                pusher!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }, null, every + TimeSpan.FromMilliseconds(500), every);
        return (refusals, pushed, pusher);
    }

    private static T[] Snapshot<T>(List<T> recorded)
    {
        lock (recorded)
            return recorded.ToArray();
    }

    // Every request of a 4.0 stream, with its time: SimExchange records the times (and, for a 4.0
    // request, only its type), the exchange inside it the requests, both in the order sent.
    private static IReadOnlyList<(TimeSpan At, string Type)> Requests40(SimExchange exchange)
    {
        var times = exchange.Log;
        var sent = exchange.Inner.Sent;
        sent.Should().HaveCount(times.Count, "both logs record every request");
        return sent.Select((s, i) => (times[i].At, s.Type switch
        {
            MessagePickup40.LiveDeliveryChangeType => s.Live ? LiveOn : LiveOff,
            MessagePickup40.DeliveryRequestType => Drain,
            MessagePickup40.MessagesReceivedType => Ack,
            _ => s.Type,
        })).ToArray();
    }

    // One live stream of Message Pickup 4.0 in simulated time (StreamDriver opens its stream with
    // 3.0), driven the way StreamDriver.RunAsync drives one: the consumer asks for the next item at
    // once; each time the stream waits, `onWait` may act (true: the clock is not moved), otherwise
    // the clock moves to its next timer, until it reaches `until`, the stream ends, or the stream
    // waits with no timer scheduled. Returns the items, and what the enumeration ended with and
    // when (both null when it was left waiting); the stream is then cancelled and disposed.
    private static async Task<(List<(PickupItem Item, TimeSpan At)> Items, Exception? EndedWith, TimeSpan? EndedAt)> RunPickup40Async(SimExchange exchange, TimeSpan until, Func<bool> onWait)
    {
        var gate = new object();
        var waits = 0;
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnWaitPublished()
        {
            TaskCompletionSource signal;
            lock (gate)
            {
                waits++;
                signal = waited;
                waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            signal.TrySetResult();
        }
        int Waits()
        {
            lock (gate)
                return waits;
        }
        async Task WaitBeyond(int count)
        {
            while (true)
            {
                Task changed;
                lock (gate)
                {
                    if (waits > count)
                        return;
                    changed = waited.Task;
                }
                await changed.ConfigureAwait(false);
            }
        }

        using var caller = new CancellationTokenSource();
        var stream = LiveDeliveryStream.ReceiveAsync(exchange, new PickupLog(logger: null), M, L, serviceEndpointOverride: null,
            static (_, _, _, _) => Task.FromResult(MessagePickupVersion.V4), caller.Token, OnWaitPublished).GetAsyncEnumerator(caller.Token);
        var items = new List<(PickupItem Item, TimeSpan At)>();
        var mark = Waits();
        var move = stream.MoveNextAsync().AsTask();
        var ended = false;
        try
        {
            while (true)
            {
                var first = await Task.WhenAny(move, WaitBeyond(mark)).WaitAsync(RealTime).ConfigureAwait(false);
                if (first == move)
                {
                    bool moved;
                    try
                    {
                        moved = await move.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        ended = true;
                        return (items, ex, exchange.Clock.Now);
                    }
                    if (!moved)
                    {
                        ended = true;
                        return (items, null, exchange.Clock.Now);
                    }
                    items.Add((stream.Current, exchange.Clock.Now));
                    mark = Waits();
                    move = stream.MoveNextAsync().AsTask();
                    continue;
                }
                mark = Waits();
                if (onWait())
                    continue;
                if (exchange.Clock.Now >= until || !exchange.Clock.AdvanceToNextDue())
                    return (items, null, null); // left waiting
            }
        }
        finally
        {
            await caller.CancelAsync().ConfigureAwait(false);
            if (!ended)
            {
                try
                {
                    await move.WaitAsync(RealTime).ConfigureAwait(false); // the move left waiting ends with the cancellation
                }
                catch (OperationCanceledException)
                {
                }
            }
            await stream.DisposeAsync().AsTask().WaitAsync(RealTime).ConfigureAwait(false);
        }
    }

    // The k-th cycle request comes no sooner after the one before it than the k-th back-off of the
    // schedule: 1, 2, 4, 8, 16, then 30 s.
    private static void NeverCloserThanTheSchedule(IReadOnlyList<TimeSpan> liveOns)
    {
        for (var k = 1; k < liveOns.Count; k++)
        {
            var gap = liveOns[k] - liveOns[k - 1];
            gap.Should().BeGreaterThanOrEqualTo(LiveDeliveryStream.Backoff(k), "cycle request {0} follows the one before it by at least the back-off after {0} failures in a row (at {1} and {2})", k, liveOns[k - 1], liveOns[k]);
        }
    }
}
