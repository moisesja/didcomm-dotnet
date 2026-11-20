using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Tests.Protocols;
using DidComm.Threading;
using DidComm.TestSupport;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Tests.Threading;

/// <summary>
/// DIDComm v2.1 threading (threading.md L9, L15): ids, and so thids, are compared ignoring letter
/// case. On the real path — Alice's authcrypt pack, the host's <c>UnpackAsync</c>, the DI-resolved
/// <see cref="ProtocolDispatcher"/> — a message whose <c>thid</c> is the first message's id in upper
/// case is in the first message's thread: it reaches the same <see cref="DidComm.Threading.ThreadState"/>,
/// so the <c>accept-lang</c> recorded there (FR-I18N-02) and the ACK request the dispatcher recorded
/// (FR-THR-04 rule 3) carry across. Ids that differ by another letter, or by a non-letter, are other
/// threads.
/// </summary>
public sealed class ThreadIdCaseEndToEndTests
{
    private const string Piuri = "https://didcomm.org/case-probe/1.0";

    /// <summary>
    /// The probe protocol: <c>first</c> records the thread's <c>accept-lang</c> and answers with a
    /// reply that requests an ACK; <c>second</c> answers with the bad-lang warning the thread's
    /// recorded preference calls for (none when nothing was recorded on its thread).
    /// </summary>
    private sealed class CaseProbe : IProtocolHandler
    {
        private readonly List<DidComm.Threading.ThreadState> _seen = new();

        public string ProtocolUri => Piuri;

        public IReadOnlyList<DidComm.Threading.ThreadState> Seen
        {
            get
            {
                lock (_seen)
                    return _seen.ToArray();
            }
        }

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            lock (_seen)
                _seen.Add(context.Thread);
            var from = message.To![0];
            if (message.Type == Piuri + "/first")
            {
                context.Thread.AcceptLang = message.AcceptLang?.ToArray();
                return Task.FromResult<Message?>(new MessageBuilder()
                    .WithType(Piuri + "/reply")
                    .WithFrom(from)
                    .WithTo(message.From!)
                    .WithThid(message.Thid ?? message.Id)
                    .WithPleaseAck()
                    .Build());
            }
            return Task.FromResult(ProblemReportApi.CreateBadLangForThread(from, message.From!, context.Thread, new[] { "en" }));
        }
    }

    private static async Task<(HostedAgents Agents, CaseProbe Probe)> StartAsync()
    {
        var probe = new CaseProbe();
        var agents = await HostedAgents.CreateAsync(s => s.AddSingleton<IProtocolHandler>(probe));
        return (agents, probe);
    }

    private static Message First(HostedAgents a)
        => new MessageBuilder(new LetterIds("first")).WithType(Piuri + "/first").WithFrom(a.Alice).WithTo(a.Bob).WithAcceptLang("fr").Build(); // an id certain to hold letters

    private static Message Second(HostedAgents a, string thid)
        => new MessageBuilder().WithType(Piuri + "/second").WithFrom(a.Alice).WithTo(a.Bob).WithThid(thid).Build();

    private static string Upper(string id)
    {
        var upper = id.ToUpperInvariant();
        upper.Should().NotBe(id, "precondition: the id has letters to re-case (it comes from LetterIds)");
        return upper;
    }

    [Fact]
    public async Task An_authcrypted_message_whose_thid_is_the_first_messages_id_upper_cased_reaches_its_thread_and_its_accept_lang()
    {
        var (agents, probe) = await StartAsync();
        await using var _ = agents;
        var first = First(agents);

        var opened = await agents.SendToHostAsync(first);
        var second = await agents.SendToHostAsync(Second(agents, Upper(first.Id)));

        opened.Result.Should().Be(DispatchResult.ReplyProduced);
        probe.Seen.Should().HaveCount(2);
        probe.Seen[1].Should().BeSameAs(probe.Seen[0], "the thid names the first message's thread in other letter case");
        probe.Seen[1].AcceptLang.Should().Equal("fr");
        second.Result.Should().Be(DispatchResult.ReplyProduced, "the French preference recorded on the thread cannot be met in English (FR-I18N-02)");
        ProblemReportApi.ReadCode(second.Reply!).Should().Be(ProblemReportApi.BadLangWarningCode);
        second.Reply!.Pthid.Should().Be(first.Id, "the thread keeps the casing of the message that opened it");
    }

    [Fact]
    public async Task A_pure_ack_on_the_upper_cased_thid_answers_the_ack_request_the_dispatcher_recorded()
    {
        // FR-THR-04 rule 3 on the real path: the host's reply requested an ACK; Alice's pure ACK names
        // the thread in upper case and is the answer, so it is consumed rather than dispatched.
        var (agents, probe) = await StartAsync();
        await using var _ = agents;
        var first = First(agents);
        var opened = await agents.SendToHostAsync(first);
        var ack = Message.Empty().WithFrom(agents.Alice).WithTo(agents.Bob).WithAck(opened.Reply!.Id).WithThid(Upper(first.Id)).Build();

        var outcome = await agents.SendToHostAsync(ack);

        outcome.Result.Should().Be(DispatchResult.DroppedAsAckLoop);
        probe.Seen.Should().ContainSingle("the answering pure ACK reaches no handler");
        agents.Host.GetRequiredService<IThreadStateStore>().Get(first.Id)!.AckRequested.Should().BeFalse("the satisfied ACK request is cleared");
    }

    public static TheoryData<string> OtherThreads => new()
    {
        "another-letter",   // the last hex letter changed (abc / abd)
        "hyphen-to-underscore",
        "hyphen-to-dot",
        "trailing-tilde",
    };

    [Theory]
    [MemberData(nameof(OtherThreads))]
    public async Task A_thid_that_differs_from_the_first_messages_id_in_more_than_case_is_another_thread(string variant)
    {
        // Control for the tests above, through the same real path: each variant is upper-cased too,
        // so only the non-case difference separates it from the first message's id.
        var (agents, probe) = await StartAsync();
        await using var _ = agents;
        var first = First(agents);
        await agents.SendToHostAsync(first);
        var thid = Upper(Variant(first.Id, variant));

        var outcome = await agents.SendToHostAsync(Second(agents, thid));

        probe.Seen.Should().HaveCount(2);
        probe.Seen[1].Should().NotBeSameAs(probe.Seen[0]);
        probe.Seen[1].AcceptLang.Should().BeNull("nothing was recorded on that thread");
        outcome.Result.Should().Be(DispatchResult.NoReply);
    }

    /// <summary>A different id derived from <paramref name="id"/> (a UUID): another letter, or a non-letter swapped or added.</summary>
    internal static string Variant(string id, string variant)
    {
        switch (variant)
        {
            case "another-letter":
            {
                var at = id.LastIndexOfAny("abcdef".ToCharArray());
                at.Should().BeGreaterThanOrEqualTo(0, "precondition: the id has a hex letter");
                var other = id[at] == 'a' ? 'b' : 'a';
                return id[..at] + other + id[(at + 1)..];
            }
            case "hyphen-to-underscore":
                id.Should().Contain("-");
                return ReplaceFirst(id, '-', '_');
            case "hyphen-to-dot":
                id.Should().Contain("-");
                return ReplaceFirst(id, '-', '.');
            case "trailing-tilde":
                return id + "~";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    private static string ReplaceFirst(string s, char from, char to)
    {
        var at = s.IndexOf(from);
        return s[..at] + to + s[(at + 1)..];
    }
}
