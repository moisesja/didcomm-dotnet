using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.ProblemReport;
using DidComm.Protocols.TrustPing;
using DidComm.Threading;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;

// L-014: alias the static API classes.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.Tests.Protocols;

/// <summary>
/// Who a built-in responder answers as, and when it cannot answer. Our end is the DID whose key
/// decrypted the request, else (plaintext, a direct call) the first <c>to</c> entry. DIDComm v2.1
/// message_structure.md, <c>to</c>: "If the header is omitted, each recipient SHOULD assume they are
/// the only recipient", so a decrypted request without <c>to</c> is answered. A request without
/// <c>from</c> has nobody to answer, and a plaintext one without <c>to</c> names none of our DIDs:
/// both get no reply, from every responder alike (the Trust Ping handler used to throw).
/// </summary>
public sealed class ResponderEndsTests
{
    private const string Alice = "did:peer:alice";
    private const string Bob = "did:peer:bob";
    private const string BobKid = Bob + "#key-agreement-1";

    public enum Kind { TrustPing, DiscoverFeatures }

    private static Message Request(Kind kind, bool withFrom, bool withTo)
    {
        var builder = new MessageBuilder();
        if (kind == Kind.TrustPing)
            builder.WithType(TrustPingApi.PingType).WithBody(new JsonObject { [TrustPingApi.ResponseRequestedField] = true });
        else
            builder.WithType(DiscoverFeaturesApi.QueriesType)
                .WithBody(new JsonObject { ["queries"] = new JsonArray(new JsonObject { ["feature-type"] = "protocol", ["match"] = "*" }) });
        if (withFrom)
            builder.WithFrom(Alice);
        if (withTo)
            builder.WithTo(Bob);
        return builder.Build();
    }

    private static IProtocolHandler Handler(Kind kind) => kind == Kind.TrustPing
        ? new TrustPingHandler()
        : new DiscoverFeaturesHandler(Array.Empty<IFeatureProvider>());

    private static ProtocolContext Ctx(Message m, string? recipientKid, IThreadStateStore? store = null)
    {
        store ??= new InMemoryThreadStateStore();
        var unpacked = new UnpackResult(
            m, Array.Empty<DidComm.Jose.EnvelopeKind>(),
            recipientKid is not null, recipientKid is not null, false, false, null, null, null, null, null, recipientKid,
            Array.Empty<string>(), null);
        return new ProtocolContext(unpacked, store.GetOrCreate(m.Thid ?? m.Id), Client: null, new DidCommOptions(), store);
    }

    [Theory]
    [InlineData(Kind.TrustPing)]
    [InlineData(Kind.DiscoverFeatures)]
    public async Task A_decrypted_request_without_to_is_answered_from_the_decrypting_DID(Kind kind)
    {
        var request = Request(kind, withFrom: true, withTo: false);

        var reply = await Handler(kind).HandleAsync(request, Ctx(request, BobKid), CancellationToken.None);

        reply.Should().NotBeNull();
        reply!.From.Should().Be(Bob);
        reply.To.Should().Equal(Alice);
        reply.Thid.Should().Be(request.Id);
    }

    [Theory]
    [InlineData(Kind.TrustPing, true, false)]
    [InlineData(Kind.TrustPing, false, true)]
    [InlineData(Kind.TrustPing, false, false)]
    [InlineData(Kind.DiscoverFeatures, true, false)]
    [InlineData(Kind.DiscoverFeatures, false, true)]
    [InlineData(Kind.DiscoverFeatures, false, false)]
    public async Task A_plaintext_request_without_to_or_any_request_without_from_gets_no_reply_and_does_not_throw(Kind kind, bool withFrom, bool withTo)
    {
        var request = Request(kind, withFrom, withTo);

        Message? reply = null;
        var ex = await Record.ExceptionAsync(async () => reply = await Handler(kind).HandleAsync(request, Ctx(request, null), CancellationToken.None));

        ex.Should().BeNull();
        reply.Should().BeNull();
    }

    [Theory]
    [InlineData(Kind.TrustPing)]
    [InlineData(Kind.DiscoverFeatures)]
    public async Task A_decrypted_request_without_from_gets_no_reply_and_does_not_throw(Kind kind)
    {
        var request = Request(kind, withFrom: false, withTo: true);

        Message? reply = null;
        var ex = await Record.ExceptionAsync(async () => reply = await Handler(kind).HandleAsync(request, Ctx(request, BobKid), CancellationToken.None));

        ex.Should().BeNull();
        reply.Should().BeNull();
    }

    private static Message ErrorReport(bool withTo)
    {
        var builder = new MessageBuilder()
            .WithType(ProblemReportApi.MessageType)
            .WithFrom(Alice)
            .WithPthid("failing-thread")
            .WithBody(new JsonObject { ["code"] = "e.p.xfer.cant-use-endpoint" });
        if (withTo)
            builder.WithTo(Bob);
        return builder.Build();
    }

    private static async Task<Message?> TripWithoutToAsync(string? recipientKid)
    {
        var handler = new ProblemReportHandler(Options.Create(new ProblemReportOptions { CascadeThreshold = 1 }), new CascadeBudgetStore());
        var store = new InMemoryThreadStateStore();
        var first = ErrorReport(withTo: false);
        var second = ErrorReport(withTo: false);
        await handler.HandleAsync(first, Ctx(first, recipientKid, store), CancellationToken.None);
        return await handler.HandleAsync(second, Ctx(second, recipientKid, store), CancellationToken.None);
    }

    [Fact]
    public async Task A_decrypted_report_without_to_trips_a_cascade_stop_sent_from_the_decrypting_DID()
    {
        var stop = await TripWithoutToAsync(BobKid);

        stop.Should().NotBeNull("the second error report on the thread breaches a budget of 1");
        stop!.From.Should().Be(Bob);
        stop.To.Should().Equal(Alice);
    }

    [Fact]
    public async Task A_plaintext_report_without_to_trips_no_cascade_stop()
    {
        var stop = await TripWithoutToAsync(recipientKid: null);

        stop.Should().BeNull("a plaintext report without `to` names none of our DIDs, so there is nobody to send the stop as");
    }
}
