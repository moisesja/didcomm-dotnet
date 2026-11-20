using System.Text.Json.Nodes;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.ProblemReport;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Tests.Protocols.ProblemReport;

/// <summary>
/// The FR-PROTO-10 cascade stop on the real path: Alice packs her error reports, the host (Bob and
/// Carol, two tenants of one process) unpacks them and its DI-resolved dispatcher runs the built-in
/// <see cref="ProblemReportHandler"/>. The reports name both tenants in <c>to</c>, Carol first, but
/// only Bob's key can open them. The stop is sent by the DID whose key decrypted the report (Bob),
/// whatever the envelope around it, and once the host packs it from its own <c>from</c> Alice opens
/// it as authenticated by Bob. A report no key decrypted (plaintext, signed only) has no decrypting
/// DID and the stop keeps the first <c>to</c> entry. DIDComm v2.1 message_structure.md: <c>to</c> only
/// names the intended audience, and "It is not possible for one recipient to verify that the message
/// was sent to a different recipient".
/// </summary>
public sealed class CascadeStopEndToEndTests
{
    private const string FailingThread = "failing-thread-7c1e";

    private static Task<HostedAgents> StartAsync()
        => HostedAgents.CreateAsync(s => s.AddOptions<ProblemReportOptions>().Configure(o => o.CascadeThreshold = 1));

    private static Message Report(HostedAgents a)
    {
        var report = new MessageBuilder()
            .WithType(ProblemReportApi.MessageType)
            .WithFrom(a.Alice)
            .WithTo(a.Carol, a.Bob)
            .WithPthid(FailingThread)
            .WithBody(new JsonObject { ["code"] = "e.p.xfer.cant-use-endpoint" })
            .Build();
        return report;
    }

    // Two error reports on one thread with a budget of one: the second trips the guard.
    private static async Task<(DispatchOutcome First, DispatchOutcome Second)> TripAsync(HostedAgents a, AliceEnvelope envelope)
    {
        var first = await a.SendToHostAsync(Report(a), envelope);
        var second = await a.SendToHostAsync(Report(a), envelope);
        return (first, second);
    }

    [Theory]
    [InlineData(AliceEnvelope.Authcrypt)]
    [InlineData(AliceEnvelope.AnoncryptOfAuthcrypt)]
    [InlineData(AliceEnvelope.AuthcryptOfSigned)]
    [InlineData(AliceEnvelope.Anoncrypt)]
    [InlineData(AliceEnvelope.AnoncryptOfSigned)]
    public async Task The_stop_for_a_decrypted_report_is_sent_by_the_decrypting_tenant_and_reaches_Alice_from_it(AliceEnvelope envelope)
    {
        await using var agents = await StartAsync();

        var (first, second) = await TripAsync(agents, envelope);

        first.Result.Should().Be(DispatchResult.NoReply, "one report is within the budget");
        second.Result.Should().Be(DispatchResult.ReplyProduced);
        var stop = second.Reply!;
        ProblemReportApi.ReadCode(stop).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
        stop.From.Should().Be(agents.Bob, "Bob's key opened the report; Carol, first in its to, never saw it");
        stop.To.Should().Equal(agents.Alice);
        stop.Pthid.Should().Be(FailingThread);

        var received = await agents.ReplyToAliceAsync(stop);

        received.Authenticated.Should().BeTrue();
        received.SenderKid.Should().StartWith(agents.Bob + "#");
        received.RecipientKid.Should().StartWith(agents.Alice + "#");
        received.Message.From.Should().Be(agents.Bob);
        received.Message.Pthid.Should().Be(FailingThread);
        ProblemReportApi.ReadCode(received.Message).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
    }

    [Theory]
    [InlineData(AliceEnvelope.Plaintext)]
    [InlineData(AliceEnvelope.SignedOnly)]
    public async Task The_stop_for_a_report_no_key_decrypted_keeps_the_first_to_entry(AliceEnvelope envelope)
    {
        await using var agents = await StartAsync();

        var (_, second) = await TripAsync(agents, envelope);

        second.Result.Should().Be(DispatchResult.ReplyProduced);
        var stop = second.Reply!;
        ProblemReportApi.ReadCode(stop).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
        stop.From.Should().Be(agents.Carol, "nothing was decrypted, so the first to entry is the only sender the report names");
        stop.To.Should().Equal(agents.Alice);
        stop.Pthid.Should().Be(FailingThread);
    }

    [Theory]
    [InlineData("FAILING-THREAD-ABC", true)]
    [InlineData("Failing-Thread-Abc", true)]
    [InlineData("FAILING-THREAD-ABD", false)]
    [InlineData("FAILING_THREAD-ABC", false)]
    public async Task On_the_real_path_a_second_report_whose_pthid_differs_only_in_case_spends_the_same_budget(string secondPthid, bool sameThread)
    {
        // DIDComm v2.1 threading.md L21: a pthid is compared ignoring letter case, so with a budget
        // of one the second report on the same thread (in other letter case) trips the guard; one
        // whose pthid differs by another letter or a non-letter is another thread and does not.
        await using var agents = await StartAsync();
        var first = Report(agents);
        first.Pthid = "failing-thread-abc";
        var second = Report(agents);
        second.Pthid = secondPthid;

        var firstOutcome = await agents.SendToHostAsync(first);
        var secondOutcome = await agents.SendToHostAsync(second);

        firstOutcome.Result.Should().Be(DispatchResult.NoReply);
        if (sameThread)
        {
            secondOutcome.Result.Should().Be(DispatchResult.ReplyProduced);
            ProblemReportApi.ReadCode(secondOutcome.Reply!).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
            secondOutcome.Reply!.Pthid.Should().Be(secondPthid, "the stop names the thread as the report that tripped it did");
            secondOutcome.Reply.From.Should().Be(agents.Bob);
        }
        else
        {
            secondOutcome.Result.Should().Be(DispatchResult.NoReply, "one report on its own thread is within the budget");
        }
    }

    [Fact]
    public async Task A_report_encrypted_to_Carol_alone_is_stopped_by_Carol_even_with_Bob_first_in_to()
    {
        // The mirror image: the key that decrypts decides, not the position in to.
        await using var agents = await StartAsync();
        Message Mirrored()
        {
            var report = Report(agents);
            report.To = new[] { agents.Bob, agents.Carol };
            return report;
        }

        await agents.Dispatcher.DispatchAsync(await agents.ReceiveAsync(Mirrored(), recipient: agents.Carol), agents.HostClient, agents.HostOptions);
        var second = await agents.Dispatcher.DispatchAsync(await agents.ReceiveAsync(Mirrored(), recipient: agents.Carol), agents.HostClient, agents.HostOptions);

        second.Result.Should().Be(DispatchResult.ReplyProduced);
        second.Reply!.From.Should().Be(agents.Carol);
        var received = await agents.ReplyToAliceAsync(second.Reply);
        received.SenderKid.Should().StartWith(agents.Carol + "#");
    }
}
