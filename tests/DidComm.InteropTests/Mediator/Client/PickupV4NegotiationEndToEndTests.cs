using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// Which Message Pickup version the real client speaks to the real mediator (#100 DC-5, DC-6),
/// over the TestServer: <c>AddMediator()</c> + <c>MapDidCommMediator</c> on one side,
/// <c>AddMediatorClient()</c> on the other. An unpinned client asks with Discover Features and
/// speaks 4.0 to a default host and 3.0 to a host with <c>EnableV4 = false</c>; a client pinned to
/// 4.0 gets no answer from a host that serves 3.0 only (over HTTP it knows at once, over a
/// WebSocket its request times out); and a
/// mediator that answers the question the way Identus or Blocktrust do (they disclose
/// <c>https://didcomm.org/messagepickup/3.0</c> only) is spoken to in 3.0.
/// </summary>
public sealed class PickupV4NegotiationEndToEndTests
{
    [Fact]
    public async Task An_unpinned_client_negotiates_4_0_with_a_default_mediator_and_speaks_it()
    {
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.Negotiate);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "negotiated");

        var status = await harness.Pickup.GetStatusAsync(mediator.Did, bob.Did);
        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
        var drained = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);

        status.MessageCount.Should().Be(1);
        status.LongestWaitedSeconds.Should().BeNull("4.0 has no longest_waited_seconds (L37)");
        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        drained.Drained.Should().BeTrue();
        drained.Status.Should().BeNull("a drained 4.0 fetch is an empty delivery, not a status (L29)");
        PickupV4E2E.NegotiatedVersions(mediator.Logs).Should().Equal(new[] { nameof(MessagePickupVersion.V4) }, "one question, answered once, remembered for the later calls");
        harness.Channel.PickupVersions.TryGet(mediator.Did, out var remembered).Should().BeTrue();
        remembered.Should().Be(MessagePickupVersion.V4);

        // The wire: one Discover Features question, then 4.0 for every step, acknowledgement included.
        await mediator.FlushObserversAsync();
        observer.PickupKindsFrom(bob.Did).Should().Equal("discover-features/queries", "4.0/status-request", "4.0/delivery-request", "4.0/messages-received", "4.0/delivery-request");
        var query = observer.From(bob.Did).Single(r => r.Kind == "discover-features/queries");
        query.Body!["queries"]!.AsArray().Select(q => q!["match"]!.GetValue<string>())
            .Should().Equal(FeatureNegotiation.PickupQueryMatch4, FeatureNegotiation.PickupQueryMatch3);
        mediator.LeakingLogLines(recipient.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unpinned_client_negotiates_3_0_with_a_mediator_that_has_4_0_switched_off()
    {
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.Mediator = m => m.Pickup.EnableV4 = false);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.Negotiate);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "three");

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
        var drained = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);

        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        drained.Status.Should().NotBeNull("a 3.0 mediator answers a drained queue with its status");
        drained.Status!.MessageCount.Should().Be(0);
        PickupV4E2E.NegotiatedVersions(mediator.Logs).Should().Equal(nameof(MessagePickupVersion.V3));
        mediator.App.Services.GetServices<IProtocolHandler>().Should().NotContain(h => h is MessagePickup40Handler, "precondition: the host does not serve 4.0");

        await mediator.FlushObserversAsync();
        observer.PickupKindsFrom(bob.Did).Should().Equal("discover-features/queries", "3.0/delivery-request", "3.0/messages-received", "3.0/delivery-request");
    }

    [Fact]
    public async Task A_client_pinned_to_4_0_fails_at_once_over_HTTP_against_a_mediator_that_serves_3_0_only()
    {
        // The client's clock never moves, so no RequestTimeout can fire: a TimeoutException here can
        // only come from the HTTP response, which carries no answer (the host has no 4.0 handler).
        var time = new QaManualTimeProvider();
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.Mediator = m => m.Pickup.EnableV4 = false);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, configure: o => o.Time = time);
        var (bob, _) = await PickupE2E.EnrolAsync(mediator, harness);

        var thrown = await Record.ExceptionAsync(() => PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did)));

        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("no answer in the HTTP response");
        time.TimersFired.Should().Be(0);
        PickupV4E2E.NegotiatedVersions(mediator.Logs).Should().BeEmpty("a pinned client asks nothing first");
        await mediator.FlushObserversAsync();
        observer.PickupKindsFrom(bob.Did).Should().Equal(new[] { "4.0/status-request" }, "the 4.0 request reached the mediator's dispatcher, which has no handler for it");
    }

    [Fact]
    public async Task A_client_pinned_to_4_0_gets_no_answer_over_a_WebSocket_from_a_mediator_that_serves_3_0_only_and_times_out()
    {
        // Over a WebSocket nothing tells the client that no answer is coming: the request waits for
        // RequestTimeout on the client's clock, which the test moves once it has seen the mediator
        // dispatch the request and answer nothing.
        var time = new QaManualTimeProvider();
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.Mediator = m => m.Pickup.EnableV4 = false);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o => o.Time = time);
        var (bob, _) = await PickupE2E.EnrolAsync(mediator, harness);
        var timeout = harness.Channel.Options.RequestTimeout;

        var mark = time.Mark();
        var call = harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: harness.WebSocketEndpoint);
        await time.WaitForTimerAsync(mark, timeout, MediatorTestServer.Timeout);
        // The record is written when the mediator's dispatch of the request has ended: it found no
        // handler for the 4.0 protocol and sent nothing.
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/status-request", "the mediator dispatched the 4.0 request");
        call.IsCompleted.Should().BeFalse("nothing answers a 4.0 request on a host without the 4.0 handler");
        time.Advance(timeout);
        var thrown = await Record.ExceptionAsync(() => PickupE2E.Within(call));

        thrown.Should().BeOfType<TimeoutException>();
        harness.Connects.Should().Be(1, "precondition: the request went over the WebSocket");
        harness.Channel.PendingCount.Should().Be(0);
        PickupV4E2E.NegotiatedVersions(mediator.Logs).Should().BeEmpty("a pinned client asks nothing first");
        await mediator.FlushObserversAsync();
        observer.PickupKindsFrom(bob.Did).Should().Equal(new[] { "4.0/status-request" }, "the pinned client sent 4.0 and nothing else");
    }

    public static TheoryData<string> ThirdParties => new() { "Identus", "Blocktrust" };

    [Theory]
    [MemberData(nameof(ThirdParties))]
    public async Task A_mediator_that_answers_the_question_like_a_third_party_one_is_spoken_to_in_3_0(string kind)
    {
        // Identus 1.2.1 and Blocktrust disclose messagepickup/3.0 only (Identus in the role
        // "mediator", Blocktrust with an empty role list); both read the two queries their own way.
        // Our mediator, behind that Discover Features responder, serves both versions: what the
        // client hears decides, and it speaks 3.0.
        var discovery = ThirdPartyDiscoverFeatures.Like(kind);
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.PostConfigureServices = services => services.AddSingleton<IProtocolHandler>(discovery));
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.Negotiate);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient, kind);

        var status = await harness.Pickup.GetStatusAsync(mediator.Did, bob.Did);
        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);

        discovery.Matches.Should().Equal(FeatureNegotiation.PickupQueryMatch4, FeatureNegotiation.PickupQueryMatch3);
        discovery.Disclosed.Should().Equal(MessagePickup.ProtocolUri);
        harness.Channel.PickupVersions.TryGet(mediator.Did, out var version).Should().BeTrue();
        version.Should().Be(MessagePickupVersion.V3);
        status.LongestWaitedSeconds.Should().NotBeNull("our mediator writes longest_waited_seconds in 3.0");
        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        await mediator.FlushObserversAsync();
        observer.PickupKindsFrom(bob.Did).Should().Equal("discover-features/queries", "3.0/status-request", "3.0/delivery-request", "3.0/messages-received");
    }
}
