using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.ProblemReport;
using DidComm.Threading;
using DidComm.Transports.ReturnRoute;
using DidComm.TestSupport;
using AwesomeAssertions;
using Xunit;

// L-014.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.Tests.Threading;

/// <summary>
/// Cross-thread confusion controls for the case-insensitive id comparison (DIDComm v2.1 threading.md
/// L9): only letter case is ignored. Ids that differ by another letter (<c>abc</c> / <c>abd</c>), by a
/// non-letter (<c>-</c> / <c>_</c> / <c>.</c> / <c>~</c>), by a letter that looks like a digit, or by a
/// trailing non-letter stay distinct at every core site that compares them: the thread store, the
/// cascade budget, the replay guard and the Discover Features correlator. Each pair is compared with
/// one side upper-cased, so a comparison broader than case folding is what would join them.
/// </summary>
public sealed class IdCaseFoldControlTests
{
    public static TheoryData<string, string> DistinctPairs => new()
    {
        { "thread-abc", "THREAD-ABD" },
        { "thread-1", "THREAD_1" },
        { "thread-1", "THREAD.1" },
        { "thread_1", "THREAD~1" },
        { "thread-1", "THREAD-1~" },
        { "thread-o", "THREAD-0" },
        { "thread-l", "THREAD-1" },
    };

    [Theory]
    [MemberData(nameof(DistinctPairs))]
    public void The_thread_store_keeps_ids_that_differ_in_more_than_case_apart(string a, string b)
    {
        var store = new InMemoryThreadStateStore();

        var first = store.GetOrCreate(a);
        var second = store.GetOrCreate(b);

        second.Should().NotBeSameAs(first);
        store.Count.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(DistinctPairs))]
    public void The_cascade_budget_keeps_pthids_that_differ_in_more_than_case_apart(string a, string b)
    {
        var budget = new CascadeBudgetStore();

        budget.RecordErrorReport(a, threshold: 1, repliable: true);
        var second = budget.RecordErrorReport(b, threshold: 1, repliable: true);

        second.Emit.Should().BeFalse("one report on its own thread is within a budget of one");
        budget.Count.Should().Be(2);
        budget.Peek(a).ErrorCount.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(DistinctPairs))]
    public void The_replay_guard_admits_an_id_that_differs_from_an_admitted_one_in_more_than_case(string a, string b)
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions { ClockSkew = TimeSpan.Zero });

        var first = guard.TryAdmit("did:example:alice", a, now.ToUnixTimeSeconds(), now);
        var second = guard.TryAdmit("did:example:alice", b, now.ToUnixTimeSeconds(), now);

        first.Should().Be(ReplayAdmission.Admitted);
        second.Should().Be(ReplayAdmission.Admitted);
        guard.Count.Should().Be(2);
    }

    // ---- the Discover Features correlator (its query ids are generated UUIDs) ----------------

    public static TheoryData<string> Variants => ThreadIdCaseEndToEndTests.OtherThreads;

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task A_disclose_whose_thid_differs_from_the_query_id_in_more_than_case_completes_nothing(string variant)
    {
        const string alice = "did:peer:alice";
        const string bob = "did:peer:bob";
        var sent = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DiscoverFeaturesClient((message, _, _) =>
        {
            sent.TrySetResult(message);
            return Task.CompletedTask;
        }, queryIds: new LetterIds("query")); // an id certain to hold letters (LetterIds)
        var task = client.QueryFeaturesAsync(alice, bob, new[] { new FeatureQuery { FeatureType = "protocol", Match = "*" } }, TimeSpan.FromSeconds(30));
        var query = await sent.Task.WaitAsync(TimeSpan.FromSeconds(30)); // rendezvous only
        var other = ThreadIdCaseEndToEndTests.Variant(query.Id, variant).ToUpperInvariant();

        var otherClaimed = ((IInboundCorrelator)client).OnInbound(InboundMessageSnapshot.CreateFallback(Authcrypt(Disclose(bob, alice, other, "other-thread"))));
        var waitingAfterOther = !task.IsCompleted;
        var genuineClaimed = ((IInboundCorrelator)client).OnInbound(InboundMessageSnapshot.CreateFallback(Authcrypt(Disclose(bob, alice, query.Id, "genuine"))));
        var disclosures = await task;

        otherClaimed.Should().BeFalse();
        waitingAfterOther.Should().BeTrue("a disclose on another thread does not answer the query");
        genuineClaimed.Should().BeTrue();
        disclosures.Should().ContainSingle(d => d.Id == "genuine");
    }

    private static Message Disclose(string from, string to, string thid, string id) => DiscoverFeaturesApi.CreateDisclose(
        from: from, to: to, thid: thid, disclosures: new FeatureDisclosure { FeatureType = "protocol", Id = id });

    private static UnpackResult Authcrypt(Message msg) => new(
        Message: msg,
        Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
        Encrypted: true,
        Authenticated: true,
        NonRepudiation: false,
        AnonymousSender: false,
        ContentEncryption: null,
        KeyWrap: null,
        SignatureAlgorithm: null,
        SignerKid: null,
        SenderKid: $"{msg.From}#key-1",
        RecipientKid: $"{msg.To![0]}#key-1",
        AllRecipientKids: Array.Empty<string>(),
        FromPrior: null);
}
