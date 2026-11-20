using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// <see cref="CoordinateMediation.ResponseThreadId"/> through the Coordinate Mediation 3.0 and 2.0
/// handlers (DIDComm v2.1 threading.md L15: a declared <c>thid</c> meets the constraints of an
/// <c>id</c>). Unpack refuses a message whose <c>thid</c> is empty or not unreserved URI characters, so
/// only a host that changes a received message before handing it on can reach a handler with one:
/// the request is refused with <see cref="MalformedMessageException"/> quoting no value, before
/// anything is granted, and is never answered on its <c>id</c> (a thread the requester never named).
/// Through the dispatcher an empty <c>thid</c> never reaches a handler (the thread store refuses it
/// first), so the empty case is driven by a direct handler call.
/// </summary>
public sealed class ResponseThreadIdThroughHandlersTests
{
    private static readonly string[] Types =
    {
        CoordinateMediation.MediateRequestType,
        CoordinateMediation.RecipientQueryType,
        CoordinateMediation20.MediateRequestType,
        CoordinateMediation20.KeylistQueryType,
    };

    public static TheoryData<string, string> DispatchedRequests
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var type in Types)
            {
                foreach (var thid in new[] { " ", "zz9 zz9", "zz9/zz9", "zz9" + (char)0x1B + "[31m" })
                    data.Add(type, thid);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DispatchedRequests))]
    public async Task Through_the_dispatcher_a_request_whose_thid_is_not_a_valid_thread_id_is_refused_not_answered(string type, string thid)
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var received = await fx.ReceiveAsync(HandlerMessages.Raw(type, bob.Did, mediator.Did, new JsonObject(), id: "req-zz9-id"), mediator.Did);
        received.Message.Thid = thid; // a host-side change after unpack: unpack itself refuses this thid

        var ex = await Record.ExceptionAsync(() => fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options));

        ex.Should().BeOfType<MalformedMessageException>();
        ex!.Message.Should().NotContain("zz9", "the refusal quotes neither the thid nor the id");
        fx.Memory.GrantCount.Should().Be(0, "the thread is settled before the request is acted on");
    }

    public static TheoryData<string> AllTypes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var type in Types)
                data.Add(type);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public async Task Called_directly_a_handler_refuses_a_request_whose_thid_is_empty_rather_than_answer_it_on_its_id(string type)
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var received = await fx.ReceiveAsync(HandlerMessages.Raw(type, bob.Did, mediator.Did, new JsonObject(), id: "req-zz9-id"), mediator.Did);
        received.Message.Thid = string.Empty;
        IProtocolHandler handler = type.StartsWith(CoordinateMediation20.ProtocolUri, StringComparison.Ordinal)
            ? fx.Host.Services.GetRequiredService<CoordinateMediation20Handler>()
            : fx.Host.Services.GetRequiredService<CoordinateMediationHandler>();
        var threads = fx.Host.Services.GetRequiredService<IThreadStateStore>();
        var context = new ProtocolContext(received, threads.GetOrCreate(received.Message.Id), fx.Host.Client, fx.Host.Options, threads);

        var ex = await Record.ExceptionAsync(() => handler.HandleAsync(received.Message, context, CancellationToken.None));

        ex.Should().BeOfType<MalformedMessageException>("an empty thid is a declared, invalid thid, not an absent one");
        ex!.Message.Should().NotContain("zz9");
        fx.Memory.GrantCount.Should().Be(0);
    }

    [Fact]
    public async Task Through_the_dispatcher_an_empty_thid_is_refused_by_the_thread_store_before_any_handler()
    {
        // Pins why the empty case above is driven directly: the dispatcher keys its thread state by
        // thid ?? id, and the store refuses an empty key, so nothing is granted or answered.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var received = await fx.ReceiveAsync(HandlerMessages.Raw(CoordinateMediation.MediateRequestType, bob.Did, mediator.Did, new JsonObject(), id: "req-zz9-id"), mediator.Did);
        received.Message.Thid = string.Empty;

        var ex = await Record.ExceptionAsync(() => fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options));

        ex.Should().BeOfType<ArgumentException>();
        fx.Memory.GrantCount.Should().Be(0);
    }
}
