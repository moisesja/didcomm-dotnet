using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.ProblemReport;
using DidComm.Threading;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Tests.Protocols.ProblemReport;

/// <summary>
/// The FR-PROTO-10 cascade stop is sent by the DID whose key decrypted the report that tripped it,
/// not by whichever DID the sender listed first in <c>to</c>. DIDComm v2.1 message_structure.md:
/// <c>to</c> "identifies a member of the message's intended audience" and "It is not possible for
/// one recipient to verify that the message was sent to a different recipient", so the first entry
/// may be another tenant of the same host. The Trust Ping and Discover Features responders already
/// answer from the decrypting DID; a plaintext report has no decrypting key and keeps the first entry.
/// </summary>
public sealed class CascadeStopSenderTests
{
    private const string Alice = "did:peer:alice";
    private const string Bob = "did:peer:bob";
    private const string OtherTenant = "did:peer:other-tenant";

    private static ProtocolContext Ctx(Message m, IThreadStateStore store, string? recipientKid)
    {
        var unpacked = new UnpackResult(
            m, Array.Empty<DidComm.Jose.EnvelopeKind>(),
            recipientKid is not null, false, false, false, null, null, null, null, null, recipientKid,
            Array.Empty<string>(), null);
        return new ProtocolContext(unpacked, store.GetOrCreate(m.Thid ?? m.Id), Client: null, new DidCommOptions(), store);
    }

    private static async Task<Message?> TripAsync(string? recipientKid)
    {
        var handler = new ProblemReportHandler(Options.Create(new ProblemReportOptions { CascadeThreshold = 1 }), new CascadeBudgetStore());
        var store = new InMemoryThreadStateStore();
        Message? stop = null;
        for (var i = 0; i < 2; i++)
        {
            var report = new MessageBuilder()
                .WithType(ProblemReportApi.MessageType)
                .WithFrom(Alice)
                .WithTo(OtherTenant, Bob)
                .WithPthid("failing-thread")
                .WithBody(new System.Text.Json.Nodes.JsonObject { ["code"] = "e.p.xfer.cant-use-endpoint" })
                .Build();
            stop = await handler.HandleAsync(report, Ctx(report, store, recipientKid), CancellationToken.None);
        }
        return stop;
    }

    [Fact]
    public async Task The_cascade_stop_is_sent_by_the_DID_that_decrypted_the_report_not_the_first_to_entry()
    {
        var stop = await TripAsync(recipientKid: Bob + "#key-agreement-1");

        stop.Should().NotBeNull("the second error report on the thread breaches a budget of 1");
        ProblemReportApi.ReadCode(stop!).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
        stop!.From.Should().Be(Bob);
        stop.To.Should().Equal(Alice);
        stop.Pthid.Should().Be("failing-thread");
    }

    [Fact]
    public async Task A_plaintext_report_has_no_decrypting_key_and_the_stop_keeps_the_first_to_entry()
    {
        var stop = await TripAsync(recipientKid: null);

        stop.Should().NotBeNull();
        stop!.From.Should().Be(OtherTenant);
        stop.To.Should().Equal(Alice);
    }
}
