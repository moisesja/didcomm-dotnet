using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.TrustPing;
using DidComm.Threading;
using AwesomeAssertions;
using Xunit;

// L-014: alias the static API classes.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.Tests.Protocols;

/// <summary>
/// #138: the built-in responders answer in the thread the request declares. DIDComm v2.1
/// threading.md L15: a request that declares no <c>thid</c> has its <c>id</c> as its <c>thid</c>;
/// L17: every later message in the thread MUST carry the request's <c>thid</c>, and a message with
/// another <c>thid</c> is not part of the thread. Each case runs for the Trust Ping
/// <c>ping-response</c> and the Discover Features <c>disclose</c>, on every way a reply is built:
/// the handler called directly (with and without the decrypting key), the dispatcher, and the
/// public <c>TrustPing.CreateResponse</c>.
/// </summary>
public sealed class ResponderReplyThreadTests
{
    private const string Requester = "did:peer:alice";
    private const string Responder = "did:peer:bob";
    private const string RequestId = "request-id-138";

    public enum Kind { TrustPing, DiscoverFeatures }

    public enum Path { HandlerPlaintext, HandlerDecrypted, Dispatcher, PublicCreateResponse }

    public enum Case { NoThid, ThidDiffersFromId, ThidEqualsId, PthidWithThid, PthidWithoutThid }

    private static Message Request(Kind kind, string? thid, string? pthid)
    {
        var request = kind == Kind.TrustPing
            ? TrustPingApi.CreatePing(Requester, Responder)
            : DiscoverFeaturesApi.CreateQuery(Requester, Responder, new FeatureQuery { FeatureType = "protocol", Match = "*" });
        request.Id = RequestId;
        request.Thid = thid;
        request.Pthid = pthid;
        return request;
    }

    private static IProtocolHandler Handler(Kind kind) => kind == Kind.TrustPing
        ? new TrustPingHandler()
        : new DiscoverFeaturesHandler(Array.Empty<IFeatureProvider>());

    private static UnpackResult Unpacked(Message message, string? recipientKid) => new(
        message, Array.Empty<DidComm.Jose.EnvelopeKind>(),
        recipientKid is not null, false, false, false, null, null, null, null, null, recipientKid,
        Array.Empty<string>(), null);

    // The context's thread state is harness only (a ThreadState cannot be keyed by an empty string);
    // what the reply carries is read from the request by the responder under test.
    private static ProtocolContext Context(Message message, string? recipientKid) => new(
        Unpacked(message, recipientKid), new DidComm.Threading.ThreadState(string.IsNullOrEmpty(message.Thid) ? message.Id : message.Thid),
        Client: null, new DidCommOptions(), new InMemoryThreadStateStore());

    private static async Task<Message?> ReplyAsync(Kind kind, Path path, Message request)
    {
        switch (path)
        {
            case Path.HandlerPlaintext:
                return await Handler(kind).HandleAsync(request, Context(request, null), CancellationToken.None);
            case Path.HandlerDecrypted:
                return await Handler(kind).HandleAsync(request, Context(request, Responder + "#key-agreement-1"), CancellationToken.None);
            case Path.Dispatcher:
                var registry = new ProtocolHandlerRegistry();
                registry.Register(Handler(kind));
                var dispatcher = new ProtocolDispatcher(registry, new InMemoryThreadStateStore());
                var outcome = await dispatcher.DispatchAsync(Unpacked(request, null), client: null, new DidCommOptions());
                return outcome.Reply;
            case Path.PublicCreateResponse:
                return TrustPingApi.CreateResponse(request);
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }
    }

    public static TheoryData<Kind, Path, Case> Matrix()
    {
        var data = new TheoryData<Kind, Path, Case>();
        foreach (var kind in Enum.GetValues<Kind>())
            foreach (var path in Enum.GetValues<Path>())
            {
                if (path == Path.PublicCreateResponse && kind != Kind.TrustPing)
                    continue; // Discover Features' public builder takes the thid as an argument.
                foreach (var @case in Enum.GetValues<Case>())
                    data.Add(kind, path, @case);
            }
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task The_reply_carries_the_thid_the_request_declares_else_its_id(Kind kind, Path path, Case @case)
    {
        var (thid, pthid, expected) = @case switch
        {
            Case.NoThid => ((string?)null, (string?)null, RequestId),
            Case.ThidDiffersFromId => ("thread-the-requester-named", null, "thread-the-requester-named"),
            Case.ThidEqualsId => (RequestId, null, RequestId),
            Case.PthidWithThid => ("child-thread", "parent-thread", "child-thread"),
            Case.PthidWithoutThid => (null, "parent-thread", RequestId),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };
        var request = Request(kind, thid, pthid);

        var reply = await ReplyAsync(kind, path, request);

        reply.Should().NotBeNull();
        reply!.Thid.Should().Be(expected);
        if (thid is not null && thid != RequestId)
            reply.Thid.Should().NotBe(request.Id, "a reply on the request's id is not in the thread the request declared (threading.md L17)");
        // Two-party protocols: the request is the first message of any child thread, so the reply
        // need not repeat the parent (threading.md L31). Pinned so it cannot change by accident.
        reply.Pthid.Should().BeNull();
        reply.Id.Should().NotBe(request.Id);
        reply.From.Should().Be(Responder);
        reply.To.Should().Equal(Requester);
    }

    [Theory]
    [InlineData(Kind.TrustPing)]
    [InlineData(Kind.DiscoverFeatures)]
    public async Task Through_the_dispatcher_the_reply_and_the_handler_share_the_declared_thread(Kind kind)
    {
        var store = new InMemoryThreadStateStore();
        var registry = new ProtocolHandlerRegistry();
        registry.Register(Handler(kind));
        var dispatcher = new ProtocolDispatcher(registry, store);
        var request = Request(kind, "thread-the-requester-named", null);

        var outcome = await dispatcher.DispatchAsync(Unpacked(request, null), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Reply!.Thid.Should().Be("thread-the-requester-named");
        store.Get("thread-the-requester-named").Should().NotBeNull();
        store.Get(RequestId).Should().BeNull();
    }

    public static TheoryData<Kind, Path, string> InvalidThids()
    {
        var values = new[] { "", " ", "zz9 zz9", "zz9/zz9", "zz9" + (char)0x1B + "[31m", "zz9" + (char)0xE9 };
        var data = new TheoryData<Kind, Path, string>();
        foreach (var value in values)
        {
            data.Add(Kind.TrustPing, Path.HandlerPlaintext, value);
            data.Add(Kind.TrustPing, Path.HandlerDecrypted, value);
            data.Add(Kind.TrustPing, Path.PublicCreateResponse, value);
            data.Add(Kind.DiscoverFeatures, Path.HandlerPlaintext, value);
            data.Add(Kind.DiscoverFeatures, Path.HandlerDecrypted, value);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidThids))]
    public async Task A_hand_built_request_with_an_invalid_thid_is_refused_never_answered_on_its_id(Kind kind, Path path, string thid)
    {
        // Unpack refuses such a thid (Message.Validate). A message built by hand and passed straight
        // to a responder is not validated: it is refused too, with a message that quotes no value.
        var request = Request(kind, thid: null, pthid: null);
        request.Thid = thid;

        var ex = await Record.ExceptionAsync(() => ReplyAsync(kind, path, request));

        ex.Should().BeOfType<MalformedMessageException>();
        ex!.Message.Should().NotContain("zz9");
        ex.Message.Should().NotContain(RequestId);
    }
}
