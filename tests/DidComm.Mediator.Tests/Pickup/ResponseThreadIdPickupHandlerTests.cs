using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// <see cref="MessagePickup.ResponseThreadId"/> through <see cref="MessagePickupHandler"/> (DIDComm
/// v2.1 threading.md L15: a declared <c>thid</c> meets the constraints of an <c>id</c>). Unpack
/// refuses a message whose <c>thid</c> is empty or not unreserved URI characters, so only a host that
/// changes a received request before handing it on can reach the handler with one: it is refused with
/// <see cref="MalformedMessageException"/> quoting no value, before the store is touched or anything
/// is pushed, and is never answered on its <c>id</c>. Through the dispatcher an empty <c>thid</c>
/// never reaches the handler (the thread store refuses it first), so the empty case is driven by a
/// direct handler call.
/// </summary>
public sealed class ResponseThreadIdPickupHandlerTests
{
    private static readonly string[] Names = { "status-request", "delivery-request", "messages-received", "live-delivery-change" };

    public static TheoryData<string, string> DispatchedRequests
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var name in Names)
            {
                foreach (var thid in new[] { " ", "zz9 zz9", "zz9#zz9", "zz9" + (char)0x1B + "[31m" })
                    data.Add(name, thid);
            }
            return data;
        }
    }

    public static TheoryData<string> AllNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Names)
                data.Add(name);
            return data;
        }
    }

    private static async Task<(MessagePickupHandlerHarness H, Message Request)> ArrangeAsync(string name)
    {
        var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.QueueAsync(h.Alice, 10);
        var request = name switch
        {
            "status-request" => h.StatusRequest(h.Alice),
            "delivery-request" => h.DeliveryRequest(h.Alice, 10),
            "messages-received" => h.MessagesReceived(h.Alice, new[] { "zz9-message-id" }),
            _ => h.LiveChange(h.Alice, true),
        };
        return (h, request);
    }

    [Theory]
    [MemberData(nameof(DispatchedRequests))]
    public async Task Through_the_dispatcher_a_request_whose_thid_is_not_a_valid_thread_id_is_refused_not_answered(string name, string thid)
    {
        var (h, request) = await ArrangeAsync(name);
        await using var _ = h;
        var session = h.Live(h.Alice);
        var received = await h.Fixture.ReceiveAsync(request, h.Mediator);
        received.Message.Thid = thid; // a host-side change after unpack: unpack itself refuses this thid
        h.ResetTraces();

        var ex = await Record.ExceptionAsync(() => h.Fixture.Host.Dispatcher.DispatchAsync(received, h.Fixture.Host.Client, h.Fixture.Host.Options, Task.CompletedTask, session, CancellationToken.None));

        ex.Should().BeOfType<MalformedMessageException>();
        ex!.Message.Should().NotContain("zz9", "the refusal quotes neither the thid nor the id");
        h.StoreTrace.Should().BeEmpty("the thread is settled before the store is asked anything");
        session.Sent.Should().BeEmpty("nothing is delivered on a thread nobody named");
    }

    [Theory]
    [MemberData(nameof(AllNames))]
    public async Task Called_directly_the_handler_refuses_a_request_whose_thid_is_empty_rather_than_answer_it_on_its_id(string name)
    {
        var (h, request) = await ArrangeAsync(name);
        await using var _ = h;
        var session = h.Live(h.Alice);
        var received = await h.Fixture.ReceiveAsync(request, h.Mediator);
        received.Message.Thid = string.Empty;
        var handler = h.Fixture.Host.Services.GetRequiredService<MessagePickupHandler>();
        var threads = h.Fixture.Host.Services.GetRequiredService<IThreadStateStore>();
        var context = new ProtocolContext(received, threads.GetOrCreate(received.Message.Id), h.Fixture.Host.Client, h.Fixture.Host.Options, threads) { Session = session };
        h.ResetTraces();

        var ex = await Record.ExceptionAsync(() => handler.HandleAsync(received.Message, context, CancellationToken.None));

        ex.Should().BeOfType<MalformedMessageException>("an empty thid is a declared, invalid thid, not an absent one");
        ex!.Message.Should().NotContain("zz9");
        h.StoreTrace.Should().BeEmpty();
        session.Sent.Should().BeEmpty();
    }
}
