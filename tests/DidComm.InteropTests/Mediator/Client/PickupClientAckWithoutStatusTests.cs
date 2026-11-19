using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// <see cref="PickupClient.AckAsync"/> against a mediator that applies a <c>messages-received</c>
/// and sends no <c>status</c> back (#98 F-1). Message Pickup 3.0 says the mediator SHOULD send an
/// updated status, not that it must; the Identus mediator v1.2.1 sends none (its
/// <c>PickupExecuter</c> returns <c>NoReply</c>, and over HTTP the response is a 202 with an empty
/// body). The stand-in is this library's mediator on a TestServer with its pickup handler wrapped
/// (<see cref="ReshapedPickupHandler"/>): every request is handled for real, so the acknowledgement
/// is applied, and only the answer is reshaped — dropped, so the endpoint answers 202 with an empty
/// body, or replaced with a problem report. The client must then ask with a <c>status-request</c>
/// and return that status; a mediator that does send its status must get no extra request.
/// </summary>
public sealed class PickupClientAckWithoutStatusTests
{
    private const string MessagesReceived = "messages-received";
    private const string StatusRequest = "status-request";

    [Fact]
    public async Task A_mediator_that_answers_messages_received_with_an_empty_202_is_asked_for_its_status_and_that_status_is_returned()
    {
        // The Identus shape: messages-received applied, answered with nothing.
        var observer = new PickupRequestObserver();
        await using var mediator = await StartAsync(observer, SilentOn(MessagePickup.MessagesReceivedType));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, failures) = await QueueUnopenableAsync(mediator, harness, count: 2);
        mediator.Store.MessageCount.Should().Be(2);
        var before = observer.KindsFrom(bob.Did).Count;

        var status = await harness.Pickup.AckAsync(mediator.Did, bob.Did, failures);

        status.MessageCount.Should().Be(0, "the status the client asked for reports the acknowledgement as applied");
        Sent(observer, bob, before).Should().Equal(MessagesReceived, StatusRequest);
        mediator.Store.MessageCount.Should().Be(0, "the mediator applied the acknowledgement");
    }

    [Fact]
    public async Task Two_chunks_neither_answered_are_followed_by_one_status_request_after_the_last()
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await StartAsync(observer, SilentOn(MessagePickup.MessagesReceivedType));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, failures) = await QueueUnopenableAsync(mediator, harness, count: 2);

        var before = observer.KindsFrom(bob.Did).Count;

        var status = await harness.Pickup.AckAsync(mediator.Did, bob.Did, AcrossTwoChunks(failures));

        status.MessageCount.Should().Be(0, "both chunks were applied, and the status after the last one says so");
        Sent(observer, bob, before).Should().Equal(MessagesReceived, MessagesReceived, StatusRequest);
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task When_only_the_first_chunk_is_unanswered_the_last_chunks_status_is_returned_and_nothing_more_is_asked()
    {
        var observer = new PickupRequestObserver();
        var acknowledgements = 0;
        await using var mediator = await StartAsync(observer, (request, reply) =>
            Is(request, MessagePickup.MessagesReceivedType) && Interlocked.Increment(ref acknowledgements) == 1 ? null : reply);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, failures) = await QueueUnopenableAsync(mediator, harness, count: 2);

        var before = observer.KindsFrom(bob.Did).Count;

        var status = await harness.Pickup.AckAsync(mediator.Did, bob.Did, AcrossTwoChunks(failures));

        status.MessageCount.Should().Be(0);
        Sent(observer, bob, before).Should().Equal(MessagesReceived, MessagesReceived);
    }

    [Fact]
    public async Task When_only_the_last_chunk_is_unanswered_one_status_request_follows_it()
    {
        var observer = new PickupRequestObserver();
        var acknowledgements = 0;
        await using var mediator = await StartAsync(observer, (request, reply) =>
            Is(request, MessagePickup.MessagesReceivedType) && Interlocked.Increment(ref acknowledgements) == 2 ? null : reply);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, failures) = await QueueUnopenableAsync(mediator, harness, count: 2);

        var before = observer.KindsFrom(bob.Did).Count;

        var status = await harness.Pickup.AckAsync(mediator.Did, bob.Did, AcrossTwoChunks(failures));

        status.MessageCount.Should().Be(0);
        Sent(observer, bob, before).Should().Equal(MessagesReceived, MessagesReceived, StatusRequest);
    }

    [Fact]
    public async Task A_mediator_that_sends_its_status_gets_exactly_one_request_per_chunk_and_no_status_request()
    {
        // This library's mediator as it is: it answers every messages-received with its status.
        var observer = new PickupRequestObserver();
        await using var mediator = await StartAsync(observer, answer: null);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, failures) = await QueueUnopenableAsync(mediator, harness, count: 2);

        var before = observer.KindsFrom(bob.Did).Count;

        var status = await harness.Pickup.AckAsync(mediator.Did, bob.Did, AcrossTwoChunks(failures));

        status.MessageCount.Should().Be(0);
        Sent(observer, bob, before).Should().Equal(MessagesReceived, MessagesReceived);
    }

    [Fact]
    public async Task When_the_status_request_is_unanswered_too_the_call_fails_with_a_TimeoutException_after_that_one_request()
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await StartAsync(observer, SilentOn(MessagePickup.MessagesReceivedType, MessagePickup.StatusRequestType));
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, failures) = await QueueUnopenableAsync(mediator, harness, count: 1);

        var before = observer.KindsFrom(bob.Did).Count;

        var thrown = await Record.ExceptionAsync(() => harness.Pickup.AckAsync(mediator.Did, bob.Did, failures));

        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(StatusRequest).And.NotContain(bob.Did);
        Sent(observer, bob, before).Should().Equal(MessagesReceived, StatusRequest);
        mediator.Store.MessageCount.Should().Be(0, "the acknowledgement was applied; only the confirmation is missing");
    }

    [Fact]
    public async Task A_problem_report_answering_messages_received_is_a_MediationProtocolException_and_no_status_request_follows()
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await StartAsync(observer, (request, reply) => Is(request, MessagePickup.MessagesReceivedType)
            ? ProblemReports.Create(request.To![0], request.From!, "e.p.req.refused-for-test", request.Id)
            : reply);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, failures) = await QueueUnopenableAsync(mediator, harness, count: 1);

        var before = observer.KindsFrom(bob.Did).Count;

        var thrown = await Record.ExceptionAsync(() => harness.Pickup.AckAsync(mediator.Did, bob.Did, failures));

        thrown.Should().BeOfType<MediationProtocolException>().Which.ProblemCode.Should().Be("e.p.req.refused-for-test");
        Sent(observer, bob, before).Should().Equal(MessagesReceived);
    }

    // ---- helpers ------------------------------------------------------------------------------

    // A mediator whose pickup answers are reshaped by `answer` (null: this library's mediator as it is).
    private static Task<MediatorTestServer> StartAsync(PickupRequestObserver observer, Func<Message, Message?, Message?>? answer)
        => PickupE2E.StartMediatorAsync(observer, o =>
        {
            if (answer is not null)
            {
                // Registered after AddMediator(): the protocol registry is last-registration-wins.
                o.PostConfigureServices = services => services.AddSingleton<IProtocolHandler>(
                    sp => new ReshapedPickupHandler(sp.GetRequiredService<MessagePickupHandler>(), answer));
            }
        });

    // Requests of these types are handled and then answered with nothing.
    private static Func<Message, Message?, Message?> SilentOn(params string[] types)
        => (request, reply) => types.Any(t => Is(request, t)) ? null : reply;

    // What the mediator received from Bob after the first `before` requests (each is observed before
    // it is answered, so everything a finished call sent is there).
    private static IReadOnlyList<string> Sent(PickupRequestObserver observer, TestPeer bob, int before)
        => observer.KindsFrom(bob.Did).Skip(before).ToArray();

    private static bool Is(Message message, string type) => string.Equals(message.Type, type, StringComparison.Ordinal);

    // Bob enrols, `count` messages he cannot open are queued for him, and a fetch returns them as
    // failures (never acknowledged): their ids are what the tests acknowledge.
    private static async Task<(TestPeer Bob, string[] Failures)> QueueUnopenableAsync(MediatorTestServer mediator, MediatorClientHarness harness, int count)
    {
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        for (var i = 0; i < count; i++)
            await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);
        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
        batch.Items.Should().HaveCount(count).And.OnlyContain(item => item.Error != null);
        return (bob, batch.Items.Select(item => item.Id).ToArray());
    }

    // 150 ids, two messages-received: the first failure's id in the first chunk, the second's in the
    // second, the rest ids the mediator does not hold.
    private static string[] AcrossTwoChunks(string[] failures)
    {
        var ids = Enumerable.Range(0, 150).Select(i => $"not-queued-{i}").ToArray();
        ids[0] = failures[0];
        ids[149] = failures[1];
        return ids;
    }
}

/// <summary>
/// Message Pickup 3.0 for the stand-in mediator: every request goes to this library's
/// <see cref="MessagePickupHandler"/> (so it is applied), and <c>answer</c> decides what is sent back
/// given the request and the handler's reply. Returning <c>null</c> makes the HTTP endpoint answer
/// 202 with an empty body, the way the Identus mediator answers a <c>messages-received</c>.
/// </summary>
internal sealed class ReshapedPickupHandler(MessagePickupHandler inner, Func<Message, Message?, Message?> answer) : IProtocolHandler
{
    public string ProtocolUri => inner.ProtocolUri;

    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        => answer(message, await inner.HandleAsync(message, context, ct));
}
