using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Xunit;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// This library's mediator (<c>AddMediator()</c> + <c>MapDidCommMediator</c>) over real HTTP answers a
/// request that declares a <c>thid</c> written in upper case on exactly that <c>thid</c>, byte for
/// byte: Coordinate Mediation 3.0 and 2.0 (<see cref="CoordinateMediationHandler"/>,
/// <see cref="CoordinateMediation20Handler"/>) and Message Pickup 3.0
/// (<see cref="MessagePickupHandler"/>), whose problem report carries it as <c>pthid</c>. DIDComm v2.1
/// threading.md L9, L17: ids are compared ignoring case but SHOULD be written in lower case by the
/// party that mints them; a reply carries the request's thid, and a responder never re-cases it. The
/// thid/id rule itself on these paths is covered by <c>CoordinateMediationHandlerTests</c>,
/// <c>CoordinateMediation20HandlerTests</c> and <c>MessagePickupHandlerDecisionTableTests</c>
/// (through the dispatcher) and <c>MediatorCoordinateMediation20Tests</c> /
/// <c>MediatorClientChannelEndToEndTests</c> (over HTTP).
/// </summary>
public sealed class MediatorUpperCaseThidTests
{
    private static string Thread() => "UPPER-THREAD-" + Guid.NewGuid().ToString("N").ToUpperInvariant();

    private static async Task<Message> AskOnThreadAsync(MediatorTestServer mediator, TestPeer peer, Message request, string thid)
    {
        request.Thid = thid;
        return (await mediator.AskAsync(peer, request)).Message;
    }

    [Fact]
    public async Task Coordinate_Mediation_3_0_answers_every_request_on_its_upper_case_thid_verbatim()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var recipient = await mediator.NewRecipientAsync();
        var threads = new[] { Thread(), Thread(), Thread() };

        var grant = await AskOnThreadAsync(mediator, bob, CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), threads[0]);
        var updated = await AskOnThreadAsync(mediator, bob, CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did,
            new[] { new RecipientUpdate(recipient.Did, RecipientUpdateAction.Add) }), threads[1]);
        var listed = await AskOnThreadAsync(mediator, bob, CoordinateMediation.CreateRecipientQuery(bob.Did, mediator.Did), threads[2]);

        grant.Type.Should().Be(CoordinateMediation.MediateGrantType);
        updated.Type.Should().Be(CoordinateMediation.RecipientUpdateResponseType);
        listed.Type.Should().Be(CoordinateMediation.RecipientType);
        new[] { grant.Thid, updated.Thid, listed.Thid }.Should().Equal(threads);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Coordinate_Mediation_2_0_answers_every_request_on_its_upper_case_thid_verbatim()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var recipient = await mediator.NewRecipientAsync();
        var threads = new[] { Thread(), Thread(), Thread() };

        var grant = await AskOnThreadAsync(mediator, bob, CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), threads[0]);
        var updated = await AskOnThreadAsync(mediator, bob, CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did,
            new[] { new RecipientUpdate(recipient.Did, RecipientUpdateAction.Add) }), threads[1]);
        var listed = await AskOnThreadAsync(mediator, bob, CoordinateMediation20.CreateKeylistQuery(bob.Did, mediator.Did), threads[2]);

        grant.Type.Should().Be(CoordinateMediation20.MediateGrantType);
        updated.Type.Should().Be(CoordinateMediation20.KeylistUpdateResponseType);
        listed.Type.Should().Be(CoordinateMediation20.KeylistType);
        new[] { grant.Thid, updated.Thid, listed.Thid }.Should().Equal(threads);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Message_Pickup_answers_every_request_on_its_upper_case_thid_verbatim_and_its_problem_report_carries_it_as_pthid()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var threads = new[] { Thread(), Thread(), Thread(), Thread() };

        var status = await AskOnThreadAsync(mediator, bob, MessagePickup.CreateStatusRequest(bob.Did, mediator.Did), threads[0]);
        var emptyDelivery = await AskOnThreadAsync(mediator, bob, MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10), threads[1]);
        var acknowledged = await AskOnThreadAsync(mediator, bob, MessagePickup.CreateMessagesReceived(bob.Did, mediator.Did, new[] { "no-such-message" }), threads[2]);
        var liveRefused = await AskOnThreadAsync(mediator, bob, MessagePickup.CreateLiveDeliveryChange(bob.Did, mediator.Did, liveDelivery: true), threads[3]);

        new[] { status.Type, emptyDelivery.Type, acknowledged.Type }.Should().AllBe(MessagePickup.StatusType, "an empty queue answers a delivery-request with a status");
        new[] { status.Thid, emptyDelivery.Thid, acknowledged.Thid }.Should().Equal(threads[0], threads[1], threads[2]);
        liveRefused.Type.Should().Be(ProblemReportApi.MessageType, "live delivery needs a connection that can carry pushes");
        ProblemReportApi.ReadCode(liveRefused).Should().Be(MessagePickup.LiveModeNotSupportedCode);
        liveRefused.Pthid.Should().Be(threads[3]);
        mediator.LeakingLogLines().Should().BeEmpty();
    }
}
