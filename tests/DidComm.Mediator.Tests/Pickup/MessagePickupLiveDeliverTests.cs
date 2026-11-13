using System.Runtime.InteropServices;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// <see cref="MessagePickupQueue.DeliverAsync"/>, the pipeline's entry into the pickup queue
/// (FR-PICK-05, -08, -09, -10; JC-1, -12, -14): every outcome, the order of its checks (each bound
/// before the work it bounds, L-055), what a faulting collaborator turns into (L-054), the argument
/// and cancellation contract, and one record per recipient for a message addressed to several.
/// </summary>
public sealed class MessagePickupLiveDeliverTests
{
    private const PickupDeliveryOutcome Queued = PickupDeliveryOutcome.Queued;
    private const PickupDeliveryOutcome PushedLive = PickupDeliveryOutcome.PushedLive;
    private const PickupDeliveryOutcome Expired = PickupDeliveryOutcome.Expired;

    // ---- Queued ---------------------------------------------------------------------------

    [Fact]
    public async Task A_message_for_a_registered_recipient_without_a_session_is_queued_and_picked_up_with_its_id_and_bytes()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var payload = PickupFixture.Payload(77, 5);

        var result = await fixture.Queue.DeliverAsync(peer, payload);

        result.Outcome.Should().Be(Queued);
        result.GrantPeerDid.Should().Be(peer);
        result.MessageId.Should().NotBeNullOrEmpty().And.MatchRegex("^[A-Za-z0-9._~-]{1,64}$", "an attachment id is unreserved characters, at most 64 (JC-12)");
        fixture.Memory.MessageCount.Should().Be(1);
        fixture.Memory.TotalBytes.Should().Be(77);

        var delivered = (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Subject;
        delivered.Id.Should().Be(result.MessageId);
        delivered.Payload.ToArray().Should().Equal(payload);
    }

    public static TheoryData<string> NoLiveTarget => new()
    {
        "a registered session that is not live",
        "a session that is not live, asked for live delivery and was refused",
        "a live session that never turned live delivery on",
        "a live session that turned live delivery on and off again",
        "live delivery on, but the caller said AllowLive: false",
        "live delivery on over a session the registry does not hold",
    };

    [Theory]
    [MemberData(nameof(NoLiveTarget))]
    public async Task Without_a_registered_live_session_that_turned_live_delivery_on_the_message_is_queued_and_not_leased(string situation)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var options = new PickupDeliveryOptions();
        PickupFakeSession session;
        switch (situation)
        {
            case "a registered session that is not live":
                session = fixture.Session(peer, mediator);
                fixture.Sessions.Register(session).Registered.Should().BeTrue();
                break;
            case "a session that is not live, asked for live delivery and was refused":
                session = fixture.Session(peer, mediator);
                fixture.Sessions.Register(session).Registered.Should().BeTrue();
                (await fixture.SendAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, session)).Reply!.Type
                    .Should().Be("https://didcomm.org/report-problem/2.0/problem-report");
                break;
            case "a live session that never turned live delivery on":
                session = fixture.Session(peer, mediator, live: true);
                (await MessagePickupLive.StatusAsync(fixture, peer, mediator, session)).LiveDelivery.Should().BeFalse("live mode is off until the recipient turns it on");
                break;
            case "a live session that turned live delivery on and off again":
                session = fixture.Session(peer, mediator, live: true);
                (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, session)).LiveDelivery.Should().BeTrue();
                (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, session, on: false)).LiveDelivery.Should().BeFalse();
                break;
            case "live delivery on, but the caller said AllowLive: false":
                session = fixture.Session(peer, mediator, live: true);
                (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, session)).LiveDelivery.Should().BeTrue();
                options = new PickupDeliveryOptions(AllowLive: false);
                break;
            default:
                session = fixture.Session(peer, mediator, live: true, register: false);
                (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, session)).LiveDelivery.Should().BeTrue();
                break;
        }

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(33), options);

        result.Outcome.Should().Be(Queued, situation);
        result.MessageId.Should().NotBeNull();
        session.Sent.Should().BeEmpty("nothing is pushed");
        session.Attempts.Should().BeEmpty("no push is even tried");
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("a queued message is under no lease")
            .Which.Id.Should().Be(result.MessageId);
    }

    [Fact]
    public async Task A_message_that_is_not_yet_due_is_queued_becomes_deliverable_once_due_and_is_never_pushed_later_on_its_own()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket)).LiveDelivery.Should().BeTrue();

        var notBefore = fixture.Clock.Now + TimeSpan.FromSeconds(60);
        var delayed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40), new PickupDeliveryOptions(NotBefore: notBefore));

        delayed.Outcome.Should().Be(Queued, "a message that is not yet due is never pushed live (JC-14)");
        socket.Attempts.Should().BeEmpty();
        fixture.Memory.MessageCount.Should().Be(1, "it is retained, and counts against the budget");
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, socket)).MessageCount.Should().Be(0, "not reported until due (JC-7)");
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty("not deliverable until due");

        fixture.Clock.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromMilliseconds(1));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty("one millisecond early is early");

        // Due. Nothing pushes it: not the passing of time, not a request that reads the queue, not
        // the next arrival (which is pushed alone).
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, socket)).MessageCount.Should().Be(1);
        socket.Attempts.Should().BeEmpty();

        var arrival = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(41, 3));
        arrival.Outcome.Should().Be(PushedLive);
        MessagePickup.TryParseDelivery(socket.Sent.Should().ContainSingle().Subject, out var pushed, out _).Should().BeTrue();
        pushed!.Should().ContainSingle().Which.Id.Should().Be(arrival.MessageId);

        // A delivery-request gets it, under the id it was accepted with.
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(delayed.MessageId);
        socket.Sent.Should().ContainSingle("and still nothing more was pushed");
    }

    public static TheoryData<string> DueInstants => new() { "now", "one tick ago", "a day ago", "the smallest instant" };

    [Theory]
    [MemberData(nameof(DueInstants))]
    public async Task A_not_before_that_has_been_reached_does_not_hold_a_message_back_from_a_live_session(string when)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var notBefore = when switch
        {
            "now" => fixture.Clock.Now,
            "one tick ago" => fixture.Clock.Now - TimeSpan.FromTicks(1),
            "a day ago" => fixture.Clock.Now - TimeSpan.FromDays(1),
            _ => DateTimeOffset.MinValue,
        };

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(12), new PickupDeliveryOptions(NotBefore: notBefore));

        result.Outcome.Should().Be(PushedLive);
        socket.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task A_not_before_at_the_end_of_time_is_queued_without_complaint()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(12), new PickupDeliveryOptions(NotBefore: DateTimeOffset.MaxValue));

        result.Outcome.Should().Be(Queued);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(0);
        fixture.Memory.MessageCount.Should().Be(1);
    }

    // ---- NotRegistered --------------------------------------------------------------------

    public static TheoryData<string> UnregisteredValues => new()
    {
        "a DID nobody registered",
        "a registered DID with a fragment",
        "a registered DID with a query",
        "a registered DID with a path",
        "a registered DID with a query and a fragment",
        "a registered DID with a parameter",
        "a registered DID in upper case",
        "a registered DID with a leading space",
        "a registered DID with a trailing space",
        "not a DID",
        "an empty string",
    };

    [Theory]
    [MemberData(nameof(UnregisteredValues))]
    public async Task A_value_that_is_not_a_registered_bare_did_is_NotRegistered_and_nothing_is_stored(string what)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (_, peer) = await fixture.EnrolledAsync();
        var value = what switch
        {
            "a DID nobody registered" => "did:example:nobody",
            "a registered DID with a fragment" => peer + "#key-1",
            "a registered DID with a query" => peer + "?service=didcomm",
            "a registered DID with a path" => peer + "/inbox",
            "a registered DID with a query and a fragment" => peer + "?versionId=1#key-1",
            "a registered DID with a parameter" => peer + ";service=didcomm",
            "a registered DID in upper case" => peer.ToUpperInvariant(),
            "a registered DID with a leading space" => " " + peer,
            "a registered DID with a trailing space" => peer + " ",
            "not a DID" => "https://example.com/inbox",
            _ => string.Empty,
        };
        if (what != "a DID nobody registered")
        {
            // Even when a careless store holds the very value: a DID URL is not a recipient DID, and
            // it is not normalised into one.
            await fixture.Host.Store.UpdateRecipientsAsync(peer, new[] { new RecipientUpdate(value, RecipientUpdateAction.Add) }, CancellationToken.None);
        }

        var result = await fixture.Queue.DeliverAsync(value, PickupFixture.Payload(20));

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotRegistered, null, null), what);
        fixture.FakeStore!.Calls.Should().BeEmpty("nothing is stored, leased or released for it");
        fixture.Memory.MessageCount.Should().Be(0);
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer);
    }

    [Fact]
    public async Task A_did_at_the_length_limit_is_looked_up_and_one_character_more_is_refused_without_asking_the_mediation_store()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true, fakeMediation: true);
        var (_, peer) = await fixture.EnrolledAsync();
        var longest = "did:example:" + new string('a', 2048 - "did:example:".Length);
        var tooLong = longest + "a";
        // Both are in the store, so a lookup would find a grant for either.
        await fixture.Host.Store.UpdateRecipientsAsync(peer, new[] { new RecipientUpdate(longest, RecipientUpdateAction.Add), new RecipientUpdate(tooLong, RecipientUpdateAction.Add) }, CancellationToken.None);
        fixture.FakeMediation!.ResetCalls();

        (await fixture.Queue.DeliverAsync(longest, PickupFixture.Payload(20))).Outcome.Should().Be(Queued, "2048 characters is a DID (FR-API-09)");
        fixture.FakeMediation.Calls.Should().Equal(nameof(IMediationStore.FindGrantByRecipientDidAsync));
        fixture.FakeMediation.ResetCalls();
        fixture.FakeStore!.Calls.Clear();

        var result = await fixture.Queue.DeliverAsync(tooLong, PickupFixture.Payload(20));

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotRegistered, null, null));
        fixture.FakeMediation.Calls.Should().BeEmpty("an over-long value is never looked up");
        fixture.FakeStore.Calls.Should().BeEmpty();
        fixture.Memory.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_did_is_NotRegistered_again_once_it_is_removed_and_every_did_of_a_grant_once_the_grant_is_revoked()
    {
        await using var fixture = PickupFixture.Create();
        const string first = "did:example:first";
        const string second = "did:example:second";
        var (_, peer) = await fixture.EnrolledAsync(first, second);

        (await fixture.Queue.DeliverAsync(first, PickupFixture.Payload(10))).Outcome.Should().Be(Queued);
        (await fixture.Queue.DeliverAsync(second, PickupFixture.Payload(10))).Outcome.Should().Be(Queued);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(10))).Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered, "enrolled is not registered: the peer never added its own DID");

        await fixture.Host.Store.UpdateRecipientsAsync(peer, new[] { new RecipientUpdate(first, RecipientUpdateAction.Remove) }, CancellationToken.None);
        (await fixture.Queue.DeliverAsync(first, PickupFixture.Payload(10))).Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotRegistered, null, null));
        (await fixture.Queue.DeliverAsync(second, PickupFixture.Payload(10))).Outcome.Should().Be(Queued, "the other DID of the grant is untouched");

        (await fixture.Host.Store.RevokeAsync(peer, CancellationToken.None)).Should().BeTrue();
        (await fixture.Queue.DeliverAsync(second, PickupFixture.Payload(10))).Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotRegistered, null, null));
        fixture.Memory.MessageCount.Should().Be(3, "what was accepted before stays until it is purged; nothing new is accepted");
    }

    public static TheoryData<string> UnusableGrantKeys => new() { "a DID URL", "not a DID", "empty", "over-long", "null" };

    [Theory]
    [MemberData(nameof(UnusableGrantKeys))]
    public async Task A_grant_whose_key_is_not_a_bare_did_is_treated_as_no_grant(string key)
    {
        var mediation = new MessagePickupLiveMediationStore();
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton<IMediationStore>(mediation));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var grantKey = key switch
        {
            "a DID URL" => peer + "#key-1",
            "not a DID" => "queue-7",
            "empty" => string.Empty,
            "over-long" => "did:example:" + new string('a', 2048),
            _ => null!,
        };
        mediation.OnFind = (_, _) => new ValueTask<MediationGrant?>(new MediationGrant(grantKey, new[] { mediator }));

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotRegistered, null, null));
        fixture.FakeStore!.Calls.Should().BeEmpty("a record is never filed under a key no peer can authenticate as");
    }

    // ---- Expired (FR-ROUTE-09) ------------------------------------------------------------

    public static TheoryData<double, long, PickupDeliveryOutcome> ExpiryBoundaries => new()
    {
        // skew in seconds; the clock relative to expires_time, in milliseconds; the outcome
        { 0, -5_000, Queued },
        { 0, 0, Queued },        // nowSeconds - skew == expires: still accepted
        { 0, 999, Queued },      // the same second
        { 0, 1_000, Expired },   // one second later
        { 30, 0, Queued },
        { 30, 30_000, Queued },  // nowSeconds - skew == expires
        { 30, 30_999, Queued },
        { 30, 31_000, Expired },
        { 30.9, 30_999, Queued }, // the skew counts in whole seconds, floored
        { 30.9, 31_000, Expired },
        { 0.9, 999, Queued },    // a sub-second skew counts as zero
        { 0.9, 1_000, Expired },
    };

    [Theory]
    [MemberData(nameof(ExpiryBoundaries))]
    public async Task Expiry_is_judged_in_whole_seconds_with_the_configured_skew_like_the_forward_path(double skewSeconds, long clockOffsetMilliseconds, PickupDeliveryOutcome expected)
    {
        await using var fixture = PickupFixture.Create(didComm: o => o.ExpiresClockSkew = TimeSpan.FromSeconds(skewSeconds), fakeStore: true);
        var (_, peer) = await fixture.EnrolledAsync();
        var expires = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        fixture.Clock.Now = expires + TimeSpan.FromMilliseconds(clockOffsetMilliseconds);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(32), new PickupDeliveryOptions(ExpiresTime: expires.ToUnixTimeSeconds()));

        result.Outcome.Should().Be(expected);
        if (expected == Expired)
        {
            result.MessageId.Should().BeNull();
            fixture.FakeStore!.Calls.Should().BeEmpty("an expired message is not accepted");
        }
        fixture.Memory.MessageCount.Should().Be(expected == Queued ? 1 : 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task A_message_that_expires_while_queued_is_never_delivered_and_no_longer_counted(int skewSeconds)
    {
        await using var fixture = PickupFixture.Create(didComm: o => o.ExpiresClockSkew = TimeSpan.FromSeconds(skewSeconds));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var expires = fixture.Clock.Now + TimeSpan.FromSeconds(10);
        var shortLived = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(50), new PickupDeliveryOptions(ExpiresTime: expires.ToUnixTimeSeconds()));
        var keeper = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(70, 9));
        shortLived.Outcome.Should().Be(Queued);

        // The last instant it is still good: the whole second (expires + skew).
        fixture.Clock.Now = expires + TimeSpan.FromSeconds(skewSeconds) + TimeSpan.FromMilliseconds(999);
        var before = await MessagePickupLive.StatusAsync(fixture, peer, mediator);
        before.MessageCount.Should().Be(2);
        before.TotalBytes.Should().Be(120);

        // One second later the first touch is a delivery-request: it must not hand the message out.
        fixture.Clock.Now = expires + TimeSpan.FromSeconds(skewSeconds + 1);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(keeper.MessageId);
        var after = await MessagePickupLive.StatusAsync(fixture, peer, mediator);
        after.MessageCount.Should().Be(1);
        after.TotalBytes.Should().Be(70);

        // Acknowledging it now is a harmless miss.
        (await MessagePickupLive.AckAsync(fixture, peer, mediator, shortLived.MessageId!)).MessageCount.Should().Be(1);
    }

    public static TheoryData<string> Skews => new() { "none", "30 seconds", "the largest time span", "the smallest time span" };

    [Theory]
    [MemberData(nameof(Skews))]
    public async Task Extreme_expires_time_values_never_throw_and_are_judged_by_the_same_rule(string skewName)
    {
        var skew = skewName switch
        {
            "none" => TimeSpan.Zero,
            "30 seconds" => TimeSpan.FromSeconds(30),
            "the largest time span" => TimeSpan.MaxValue,
            _ => TimeSpan.MinValue,
        };
        await using var fixture = PickupFixture.Create(didComm: o => o.ExpiresClockSkew = skew, pickup: o => o.MaxQueueMessages = 100);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var nowSeconds = fixture.Clock.Now.ToUnixTimeSeconds();
        long[] values =
        {
            long.MinValue, long.MinValue + 1, -62_135_596_801, -62_135_596_800, -1, 0, 1,
            nowSeconds - 31, nowSeconds - 30, nowSeconds - 1, nowSeconds, nowSeconds + 1,
            253_402_300_798, 253_402_300_799, 253_402_300_800, long.MaxValue - 1, long.MaxValue,
        };

        // FR-ROUTE-09 as the forward path states it: expired once (now − skew) > expires_time, in whole seconds.
        var skewSeconds = (long)Math.Floor(skew.TotalSeconds);
        var accepted = 0;
        foreach (var expires in values)
        {
            var expected = nowSeconds - skewSeconds > expires ? Expired : Queued;

            var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(8), new PickupDeliveryOptions(ExpiresTime: expires));

            result.Outcome.Should().Be(expected, "expires_time {0} with a skew of {1} s at second {2}", expires, skewSeconds, nowSeconds);
            if (expected == Queued)
                accepted++;
        }

        // What was accepted is in the queue and reading it does not throw either.
        fixture.Memory.MessageCount.Should().Be(accepted);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(accepted);
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0, "nothing that was just accepted has expired at the same instant");
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator, limit: 10)).Should().HaveCount(Math.Min(10, accepted));
    }

    // ---- PayloadTooLarge (FR-PICK-03/10, L-055) -------------------------------------------

    [Fact]
    public async Task The_largest_admissible_message_is_accepted_and_one_byte_more_is_refused_before_any_store_is_asked()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxDeliveryBytes = 64 * 1024, fakeStore: true, fakeMediation: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var max = fixture.Queue.MaxAdmissiblePayloadBytes;
        max.Should().BeInRange(1, 64 * 1024);

        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(max), new PickupDeliveryOptions(AllowLive: false))).Outcome.Should().Be(Queued);
        fixture.FakeMediation!.ResetCalls();
        fixture.FakeStore!.Calls.Clear();

        var tooLarge = PickupFixture.Payload(max + 1);
        var refused = await fixture.Queue.DeliverAsync(peer, tooLarge);
        var unregistered = await fixture.Queue.DeliverAsync("did:example:nobody", tooLarge);
        var notADid = await fixture.Queue.DeliverAsync("not a did", tooLarge);
        var expired = await fixture.Queue.DeliverAsync(peer, tooLarge, new PickupDeliveryOptions(ExpiresTime: 1));

        refused.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.PayloadTooLarge, null, null));
        unregistered.Should().Be(refused, "the size is refused before the recipient is looked at");
        notADid.Should().Be(refused);
        expired.Should().Be(refused);
        fixture.FakeMediation.Calls.Should().BeEmpty("the bound comes before the lookup it bounds (L-055)");
        fixture.FakeStore.Calls.Should().BeEmpty();
        socket.Attempts.Should().BeEmpty("a message that is not admitted is not pushed");
        fixture.Memory.MessageCount.Should().Be(1);

        // Refusals any sender can cause: the first at Warning, the rest at Debug; sizes only.
        MessagePickupLive.QueueLogs(fixture, LogLevel.Warning).Should().ContainSingle()
            .Which.State.Should().Contain("Bytes", max + 1).And.Contain("MaxAdmissiblePayloadBytes", max);
        MessagePickupLive.QueueLogs(fixture, LogLevel.Debug).Should().HaveCount(3);
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator, "did:example:nobody", "not a did");
    }

    // ---- QueueFull (FR-PICK-10, JC-1) -----------------------------------------------------

    public static TheoryData<string> StoreLimits => new() { "the grant's message limit", "the grant's byte limit", "the store's message limit", "the store's byte limit" };

    [Theory]
    [MemberData(nameof(StoreLimits))]
    public async Task A_message_past_a_store_limit_is_QueueFull_and_nothing_of_it_is_retained(string limit)
    {
        await using var fixture = PickupFixture.Create(pickup: o =>
        {
            switch (limit)
            {
                case "the grant's message limit":
                    o.MaxQueueMessages = 2;
                    break;
                case "the grant's byte limit":
                    o.MaxQueueBytes = 100;
                    break;
                case "the store's message limit":
                    o.MaxQueueMessages = 2;
                    o.MaxStoreMessages = 2;
                    break;
                default:
                    o.MaxQueueBytes = 100;
                    o.MaxStoreBytes = 100;
                    break;
            }
        });
        var mediator = (await fixture.NewPeerAsync()).Did;
        var alice = (await fixture.NewPeerAsync()).Did;
        var bob = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(alice, mediator, alice);
        await fixture.EnrolAsync(bob, mediator, bob);
        var socket = fixture.Session(alice, mediator, live: true);

        // Up to the limit, exactly. The store-wide cases leave Alice room in her own quota.
        string first;
        var storeWide = limit.StartsWith("the store", StringComparison.Ordinal);
        if (limit.EndsWith("message limit", StringComparison.Ordinal))
        {
            first = await fixture.QueueAsync(alice, PickupFixture.Payload(10));
            await fixture.QueueAsync(storeWide ? bob : alice, PickupFixture.Payload(10));
        }
        else
        {
            first = await fixture.QueueAsync(alice, PickupFixture.Payload(60));
            await fixture.QueueAsync(storeWide ? bob : alice, PickupFixture.Payload(40));
        }
        await MessagePickupLive.SetLiveAsync(fixture, alice, mediator, socket);
        var (count, bytes) = (fixture.Memory.MessageCount, fixture.Memory.TotalBytes);

        var refused = await fixture.Queue.DeliverAsync(alice, PickupFixture.Payload(1));

        refused.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.QueueFull, null, alice), "a full queue is an outcome, not an exception (JC-1)");
        fixture.Memory.MessageCount.Should().Be(count);
        fixture.Memory.TotalBytes.Should().Be(bytes);
        socket.Attempts.Should().BeEmpty("a message that cannot be admitted is not pushed (FR-PICK-10)");

        var expectedReason = limit switch
        {
            "the grant's message limit" => nameof(EnqueueOutcome.GrantMessageLimitReached),
            "the grant's byte limit" => nameof(EnqueueOutcome.GrantByteLimitReached),
            _ => nameof(EnqueueOutcome.StoreLimitReached),
        };
        MessagePickupLive.QueueLogs(fixture, LogLevel.Warning).Should().ContainSingle().Which.State.Should().Contain("Outcome", expectedReason);

        // One grant's quota is not another's; the store's bounds are everyone's.
        var other = await fixture.Queue.DeliverAsync(bob, PickupFixture.Payload(1));
        other.Outcome.Should().Be(storeWide ? PickupDeliveryOutcome.QueueFull : Queued);
        MessagePickupLive.QueueLogs(fixture, LogLevel.Warning).Should().ContainSingle("the second refusal is logged at Debug");

        // The budget counts what is retained: once a message is gone there is room again.
        (await fixture.Queue.RemoveAsync(alice, first)).Should().BeTrue();
        (await fixture.Queue.DeliverAsync(alice, PickupFixture.Payload(1))).Outcome.Should().Be(PushedLive);
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, alice, bob, mediator, first);
    }

    // ---- Faulted (L-054, JC-12) -----------------------------------------------------------

    public static TheoryData<string> Faults => new() { "throws", "answers a faulted task", "throws its own OperationCanceledException" };

    private static ValueTask<T> Fault<T>(string how) => how switch
    {
        "throws" => throw new HandlerBoomException(HandlerBoomException.Marker),
        "answers a faulted task" => new ValueTask<T>(Task.FromException<T>(new HandlerBoomException(HandlerBoomException.Marker))),
        _ => throw new OperationCanceledException(HandlerBoomException.Marker),
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task A_mediation_store_that_fails_gives_Faulted_and_the_message_store_is_never_reached(string how)
    {
        var mediation = new MessagePickupLiveMediationStore();
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton<IMediationStore>(mediation));
        var (mediator, peer) = await fixture.EnrolledAsync();
        mediation.OnFind = (_, _) => Fault<MediationGrant?>(how);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.Faulted, null, null), "a fault is an outcome; only cancellation of the caller's token is thrown");
        fixture.FakeStore!.Calls.Should().BeEmpty();
        var line = MessagePickupLive.QueueLogs(fixture, LogLevel.Error).Should().ContainSingle().Subject;
        line.State["ExceptionType"].Should().Be(how == "throws its own OperationCanceledException" ? nameof(OperationCanceledException) : nameof(HandlerBoomException));
        line.Exception.Should().BeNull("the exception object is not handed to the logger: its text is host data");
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator);
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task A_message_store_that_fails_to_enqueue_gives_Faulted_and_nothing_is_pushed(string how)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        fixture.FakeStore!.OnEnqueue = (_, _, _, _) => Fault<EnqueueOutcome>(how);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.Faulted, null, peer));
        socket.Attempts.Should().BeEmpty("what was not admitted is not pushed");
        fixture.Memory.MessageCount.Should().Be(0);
        fixture.FakeStore.CountOf(nameof(IMessageStore.EnqueueAsync)).Should().Be(1, "a fault is not retried");
        MessagePickupLive.QueueLogs(fixture, LogLevel.Error).Should().ContainSingle();
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator);
    }

    [Fact]
    public async Task A_store_that_answers_outside_its_contract_gives_Faulted()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (_, peer) = await fixture.EnrolledAsync();
        fixture.FakeStore!.OnEnqueue = (_, _, _, _) => new ValueTask<EnqueueOutcome>((EnqueueOutcome)42);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.Faulted, null, peer));
    }

    [Fact]
    public async Task An_id_generator_that_throws_gives_Faulted_before_the_store_is_called()
    {
        var ids = new MessagePickupLiveIdGenerator { Next = _ => throw new HandlerBoomException(HandlerBoomException.Marker) };
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton(ids.Source));
        var (mediator, peer) = await fixture.EnrolledAsync();

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.Faulted, null, peer));
        ids.Calls.Should().Be(1);
        fixture.FakeStore!.Calls.Should().BeEmpty();
        MessagePickupLive.QueueLogs(fixture, LogLevel.Error).Should().ContainSingle().Which.State["ExceptionType"].Should().Be(nameof(HandlerBoomException));
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator);
    }

    public static TheoryData<string> UnusableIds => new()
    {
        "null", "empty", "65 characters", "a space", "a slash", "a hash", "a colon", "a percent sign", "a plus sign", "a line break", "a non-ASCII letter",
    };

    [Theory]
    [MemberData(nameof(UnusableIds))]
    public async Task An_id_that_cannot_be_an_attachment_id_gives_Faulted_before_the_store_is_called(string what)
    {
        var id = what switch
        {
            "null" => null,
            "empty" => string.Empty,
            "65 characters" => new string('q', 65),
            "a space" => "queued message",
            "a slash" => "queued/message",
            "a hash" => "queued#message",
            "a colon" => "urn:queued-message",
            "a percent sign" => "queued%20message",
            "a plus sign" => "queued+message",
            "a line break" => "queued\nmessage",
            _ => "queued-méssage",
        };
        var ids = new MessagePickupLiveIdGenerator { Next = _ => id };
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton(ids.Source));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.Faulted, null, peer), what);
        ids.Calls.Should().Be(1, "an unusable id is a fault of the generator, not a collision to retry");
        fixture.FakeStore!.CountOf(nameof(IMessageStore.EnqueueAsync)).Should().Be(0);
        socket.Attempts.Should().BeEmpty();
        MessagePickupLive.QueueLogs(fixture, LogLevel.Error).Should().ContainSingle();
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator, id);
    }

    [Fact]
    public async Task The_longest_id_of_every_unreserved_kind_is_used_as_it_is()
    {
        var id = "Az09-._~" + new string('k', 56);
        id.Length.Should().Be(64);
        var ids = new MessagePickupLiveIdGenerator { Next = _ => id };
        await using var fixture = PickupFixture.Create(services: s => s.AddSingleton(ids.Source));
        var (mediator, peer) = await fixture.EnrolledAsync();

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Should().Be(new PickupDeliveryResult(Queued, id, peer));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task A_duplicate_id_is_retried_once_with_a_fresh_id()
    {
        var ids = new MessagePickupLiveIdGenerator { Next = call => call <= 2 ? "id-7f3a91" : "fresh-" + call };
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton(ids.Source));
        var (mediator, peer) = await fixture.EnrolledAsync();

        var first = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 1));
        var second = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(21, 2));

        first.Should().Be(new PickupDeliveryResult(Queued, "id-7f3a91", peer));
        second.Should().Be(new PickupDeliveryResult(Queued, "fresh-3", peer));
        ids.Calls.Should().Be(3);
        fixture.FakeStore!.Enqueued.Select(e => e.Message.Id).Should().Equal("id-7f3a91", "id-7f3a91", "fresh-3");
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Select(m => (m.Id, m.Payload.Length)).Should().Equal(("id-7f3a91", 20), ("fresh-3", 21));
    }

    [Fact]
    public async Task A_second_duplicate_id_gives_Faulted_and_the_message_already_held_is_untouched()
    {
        var ids = new MessagePickupLiveIdGenerator { Next = _ => "id-7f3a91" };
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton(ids.Source));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 1))).Should().Be(new PickupDeliveryResult(Queued, "id-7f3a91", peer));
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);

        var second = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(21, 2));

        second.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.Faulted, null, peer));
        ids.Calls.Should().Be(3, "one retry, not a loop");
        socket.Attempts.Should().BeEmpty();
        fixture.Memory.MessageCount.Should().Be(1);
        MessagePickupLive.QueueLogs(fixture, LogLevel.Error).Should().ContainSingle().Which.State.Should().Contain("Outcome", nameof(EnqueueOutcome.DuplicateId));
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator, "id-7f3a91");
        var held = (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Subject;
        held.Id.Should().Be("id-7f3a91");
        held.Payload.ToArray().Should().Equal(PickupFixture.Payload(20, 1));
    }

    [Fact]
    public async Task A_logging_provider_that_is_down_changes_no_outcome()
    {
        await using var fixture = PickupFixture.Create(pickup: o => { o.MaxQueueMessages = 2; o.MaxDeliveryBytes = 64 * 1024; }, fakeStore: true, throwingLogger: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var max = fixture.Queue.MaxAdmissiblePayloadBytes;

        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(max + 1))).Outcome.Should().Be(PickupDeliveryOutcome.PayloadTooLarge);
        (await fixture.Queue.DeliverAsync("did:example:nobody", PickupFixture.Payload(5))).Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(5), new PickupDeliveryOptions(ExpiresTime: 1))).Outcome.Should().Be(Expired);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(5))).Outcome.Should().Be(PushedLive);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(5), new PickupDeliveryOptions(AllowLive: false))).Outcome.Should().Be(Queued);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(5))).Outcome.Should().Be(PickupDeliveryOutcome.QueueFull);

        fixture.FakeStore!.OnEnqueue = (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(5))).Outcome.Should().Be(PickupDeliveryOutcome.Faulted);
        fixture.FakeStore.OnAcknowledge = (_, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
        (await fixture.Queue.RemoveAsync(peer, "no-such-id")).Should().BeFalse();
    }

    // ---- arguments and cancellation -------------------------------------------------------

    [Fact]
    public async Task Null_did_and_empty_payload_are_argument_errors_and_null_options_mean_the_defaults()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (_, peer) = await fixture.EnrolledAsync();
        var queue = fixture.Queue;

        (await FluentActions.Awaiting(async () => await queue.DeliverAsync(null!, PickupFixture.Payload(5))).Should().ThrowAsync<ArgumentNullException>())
            .Which.ParamName.Should().Be("recipientDid");
        foreach (var empty in new[] { ReadOnlyMemory<byte>.Empty, new ReadOnlyMemory<byte>(Array.Empty<byte>()), new ReadOnlyMemory<byte>(new byte[8], 3, 0) })
        {
            (await FluentActions.Awaiting(async () => await queue.DeliverAsync(peer, empty)).Should().ThrowExactlyAsync<ArgumentException>())
                .Which.ParamName.Should().Be("payload");
        }
        fixture.FakeStore!.Calls.Should().BeEmpty();

        (await queue.DeliverAsync(peer, PickupFixture.Payload(5), options: null)).Outcome.Should().Be(Queued);
        (await queue.DeliverAsync(peer, new ReadOnlyMemory<byte>(new byte[8], 3, 1))).Outcome.Should().Be(Queued, "one byte is a message");
        fixture.Memory.TotalBytes.Should().Be(6);
    }

    [Fact]
    public async Task A_token_cancelled_beforehand_throws_OperationCanceledException_and_touches_nothing()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true, fakeMediation: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        fixture.FakeMediation!.ResetCalls();
        fixture.FakeStore!.Calls.Clear();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await FluentActions.Awaiting(async () => await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(5), ct: cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await fixture.Queue.RemoveAsync(peer, "any-id", cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await fixture.Queue.PurgeExpiredAsync(cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();

        fixture.FakeMediation.Calls.Should().BeEmpty();
        fixture.FakeStore.Calls.Where(c => c != nameof(IMessageStore.PurgeExpiredAsync)).Should().BeEmpty();
        socket.Attempts.Should().BeEmpty();
        fixture.Memory.MessageCount.Should().Be(0);
    }

    public static TheoryData<string> LateCancellations => new()
    {
        "the token is cancelled as the store accepts the record",
        "the session throws OperationCanceledException as the token is cancelled",
        "the session throws something else as the token is cancelled",
    };

    [Theory]
    [MemberData(nameof(LateCancellations))]
    public async Task Cancellation_after_the_store_accepted_the_message_leaves_it_stored_and_releases_its_lease(string how)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        using var cts = new CancellationTokenSource();
        if (how == "the token is cancelled as the store accepts the record")
        {
            fixture.FakeStore!.OnEnqueue = async (message, lease, now, ct) =>
            {
                var outcome = await fixture.FakeStore.Inner.EnqueueAsync(message, lease, now, ct);
                cts.Cancel();
                return outcome;
            };
        }
        else
        {
            socket.OnSend = (_, _) =>
            {
                cts.Cancel();
                throw how == "the session throws OperationCanceledException as the token is cancelled"
                    ? new OperationCanceledException(cts.Token)
                    : new HandlerBoomException(HandlerBoomException.Marker);
            };
        }
        var payload = PickupFixture.Payload(25, 4);

        var thrown = (await FluentActions.Awaiting(async () => await fixture.Queue.DeliverAsync(peer, payload, ct: cts.Token)).Should().ThrowAsync<OperationCanceledException>()).Which;

        thrown.Message.Should().NotContain(HandlerBoomException.Marker, "when the caller's token is cancelled, cancellation is what leaves — never a collaborator's own error");
        socket.Sent.Should().BeEmpty();
        fixture.Memory.MessageCount.Should().Be(1, "cancelling after the message was stored leaves it stored");
        fixture.FakeStore!.Enqueued.Should().ContainSingle().Which.Lease.Should().NotBeNull("it was admitted under a lease for the live session");

        // Its lease is gone: a delivery-request gets it at once, with the clock where it was.
        var held = (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Subject;
        held.Payload.ToArray().Should().Equal(payload);
        var released = fixture.FakeStore.Released.Should().ContainSingle().Subject;
        released.GrantPeerDid.Should().Be(peer);
        released.Ids.Should().Equal(held.Id);
    }

    [Fact]
    public async Task Cancellation_as_the_store_accepts_a_message_with_no_live_session_never_loses_or_duplicates_it()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        using var cts = new CancellationTokenSource();
        fixture.FakeStore!.OnEnqueue = async (message, lease, now, ct) =>
        {
            var outcome = await fixture.FakeStore.Inner.EnqueueAsync(message, lease, now, ct);
            cts.Cancel();
            return outcome;
        };

        // The call may report the message as queued or report the cancellation; it may not do anything else.
        PickupDeliveryResult? result = null;
        try
        {
            result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(25), ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        if (result is { } reported)
            reported.Outcome.Should().Be(Queued);
        fixture.Memory.MessageCount.Should().Be(1);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_store_fault_under_a_cancelled_token_leaves_as_cancellation_not_as_Faulted()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (_, peer) = await fixture.EnrolledAsync();
        using var cts = new CancellationTokenSource();
        fixture.FakeStore!.OnEnqueue = (_, _, _, _) =>
        {
            cts.Cancel();
            throw new HandlerBoomException(HandlerBoomException.Marker);
        };

        var delivering = (await FluentActions.Awaiting(async () => await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(25), ct: cts.Token)).Should().ThrowAsync<OperationCanceledException>()).Which;
        delivering.Message.Should().NotContain(HandlerBoomException.Marker);

        using var second = new CancellationTokenSource();
        fixture.FakeStore.OnAcknowledge = (_, _, _) =>
        {
            second.Cancel();
            throw new HandlerBoomException(HandlerBoomException.Marker);
        };
        var removing = (await FluentActions.Awaiting(async () => await fixture.Queue.RemoveAsync(peer, "any-id", second.Token)).Should().ThrowAsync<OperationCanceledException>()).Which;
        removing.Message.Should().NotContain(HandlerBoomException.Marker);
    }

    // ---- several recipients (FR-PICK-05) --------------------------------------------------

    [Fact]
    public async Task One_call_per_recipient_of_one_grant_stores_one_record_each_over_the_same_buffer_acknowledged_separately()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        string[] recipients = { "did:example:phone", "did:example:laptop", "did:example:tablet" };
        var (mediator, peer) = await fixture.EnrolledAsync(recipients);
        var payload = PickupFixture.Payload(90, 6);

        var results = new List<PickupDeliveryResult>();
        foreach (var recipient in recipients)
            results.Add(await fixture.Queue.DeliverAsync(recipient, payload));

        results.Should().OnlyContain(r => r.Outcome == Queued && r.GrantPeerDid == peer);
        var ids = results.Select(r => r.MessageId!).ToArray();
        ids.Should().OnlyHaveUniqueItems("each recipient's copy has its own id");
        fixture.Memory.MessageCount.Should().Be(3);
        fixture.Memory.TotalBytes.Should().Be(270, "each copy counts against the quota");
        fixture.FakeStore!.Enqueued.Select(e => e.Message.RecipientDid).Should().Equal(recipients);
        foreach (var (message, _) in fixture.FakeStore.Enqueued)
        {
            MemoryMarshal.TryGetArray(message.Payload, out var segment).Should().BeTrue();
            segment.Array.Should().BeSameAs(payload, "the payload memory is kept as given, not copied");
        }

        // Each DID sees its own copy.
        for (var i = 0; i < recipients.Length; i++)
            (await MessagePickupLive.StatusAsync(fixture, peer, mediator, recipientDid: recipients[i])).MessageCount.Should().Be(1);

        // The phone's copy is delivered and acknowledged: the laptop's and the tablet's are still held.
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator, recipientDid: recipients[0])).Should().ContainSingle().Which.Id.Should().Be(ids[0]);
        (await MessagePickupLive.AckAsync(fixture, peer, mediator, ids[0])).MessageCount.Should().Be(2);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, recipientDid: recipients[0])).MessageCount.Should().Be(0);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, recipientDid: recipients[1])).MessageCount.Should().Be(1);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, recipientDid: recipients[2])).MessageCount.Should().Be(1);

        var rest = await MessagePickupLive.FetchNowAsync(fixture, peer, mediator);
        rest.Select(m => m.Id).Should().Equal(ids[1], ids[2]);
        foreach (var copy in rest)
            copy.Payload.ToArray().Should().Equal(payload);
        (await MessagePickupLive.AckAsync(fixture, peer, mediator, ids[1])).MessageCount.Should().Be(1);
        (await MessagePickupLive.AckAsync(fixture, peer, mediator, ids[2])).MessageCount.Should().Be(0);
        fixture.Memory.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task One_call_per_recipient_of_different_grants_stores_independent_copies_and_no_acknowledgement_crosses_a_grant()
    {
        await using var fixture = PickupFixture.Create();
        var mediator = (await fixture.NewPeerAsync()).Did;
        var peers = new[] { (await fixture.NewPeerAsync()).Did, (await fixture.NewPeerAsync()).Did, (await fixture.NewPeerAsync()).Did };
        foreach (var peer in peers)
            await fixture.EnrolAsync(peer, mediator, peer);
        var payload = PickupFixture.Payload(64, 2);

        var results = new List<PickupDeliveryResult>();
        foreach (var peer in peers)
            results.Add(await fixture.Queue.DeliverAsync(peer, payload));

        results.Select(r => r.Outcome).Should().OnlyContain(o => o == Queued);
        results.Select(r => r.GrantPeerDid).Should().Equal(peers);
        var ids = results.Select(r => r.MessageId!).ToArray();
        ids.Should().OnlyHaveUniqueItems();

        // The first recipient acknowledges every id it could ever have learned: only its own copy goes.
        (await MessagePickupLive.AckAsync(fixture, peers[0], mediator, ids)).MessageCount.Should().Be(0);
        fixture.Memory.MessageCount.Should().Be(2);
        (await fixture.Queue.RemoveAsync(peers[0], ids[1])).Should().BeFalse("an id means something only under its own grant");

        for (var i = 1; i < peers.Length; i++)
        {
            (await MessagePickupLive.StatusAsync(fixture, peers[i], mediator)).MessageCount.Should().Be(1);
            var own = (await MessagePickupLive.FetchNowAsync(fixture, peers[i], mediator)).Should().ContainSingle().Subject;
            own.Id.Should().Be(ids[i]);
            own.Payload.ToArray().Should().Equal(payload);
            (await MessagePickupLive.AckAsync(fixture, peers[i], mediator, ids[i])).MessageCount.Should().Be(0);
        }
        fixture.Memory.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Copies_that_happen_to_share_an_id_across_grants_are_still_separate_records()
    {
        var ids = new MessagePickupLiveIdGenerator { Next = _ => "same-id-everywhere" };
        await using var fixture = PickupFixture.Create(services: s => s.AddSingleton(ids.Source));
        var mediator = (await fixture.NewPeerAsync()).Did;
        var alice = (await fixture.NewPeerAsync()).Did;
        var bob = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(alice, mediator, alice);
        await fixture.EnrolAsync(bob, mediator, bob);

        (await fixture.Queue.DeliverAsync(alice, PickupFixture.Payload(10, 1))).Should().Be(new PickupDeliveryResult(Queued, "same-id-everywhere", alice));
        (await fixture.Queue.DeliverAsync(bob, PickupFixture.Payload(11, 2))).Should().Be(new PickupDeliveryResult(Queued, "same-id-everywhere", bob), "ids are scoped to the grant (FR-PICK-04)");

        (await MessagePickupLive.AckAsync(fixture, alice, mediator, "same-id-everywhere")).MessageCount.Should().Be(0);
        var bobs = (await MessagePickupLive.FetchNowAsync(fixture, bob, mediator)).Should().ContainSingle("Alice's acknowledgement removed only her record").Subject;
        bobs.Payload.ToArray().Should().Equal(PickupFixture.Payload(11, 2));
    }

    [Fact]
    public async Task One_recipient_live_and_one_not_each_get_their_copy_their_own_way()
    {
        await using var fixture = PickupFixture.Create();
        var mediator = (await fixture.NewPeerAsync()).Did;
        var alice = (await fixture.NewPeerAsync()).Did;
        var bob = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(alice, mediator, alice);
        await fixture.EnrolAsync(bob, mediator, bob);
        var socket = fixture.Session(alice, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, alice, mediator, socket);
        var payload = PickupFixture.Payload(48, 8);

        var toAlice = await fixture.Queue.DeliverAsync(alice, payload);
        var toBob = await fixture.Queue.DeliverAsync(bob, payload);

        toAlice.Outcome.Should().Be(PushedLive);
        toBob.Outcome.Should().Be(Queued);
        toAlice.MessageId.Should().NotBe(toBob.MessageId);
        MessagePickup.TryParseDelivery(socket.Sent.Should().ContainSingle().Subject, out var pushed, out _).Should().BeTrue();
        pushed!.Should().ContainSingle().Which.Id.Should().Be(toAlice.MessageId);

        (await MessagePickupLive.AckAsync(fixture, alice, mediator, toAlice.MessageId!, toBob.MessageId!)).MessageCount.Should().Be(0);
        (await MessagePickupLive.FetchNowAsync(fixture, bob, mediator)).Should().ContainSingle().Which.Id.Should().Be(toBob.MessageId);
    }

    // ---- the result -----------------------------------------------------------------------

    [Fact]
    public async Task The_result_prints_its_outcome_and_neither_the_id_nor_the_did()
    {
        await using var fixture = PickupFixture.Create();
        var (_, peer) = await fixture.EnrolledAsync();

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(5));

        var text = result.ToString();
        text.Should().Contain(nameof(PickupDeliveryOutcome.Queued));
        text.Should().NotContain(result.MessageId!).And.NotContain(peer);
        $"{result}".Should().Be(text);
    }
}
