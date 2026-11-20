using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Threading;
using AwesomeAssertions;
using Xunit;

// L-014: alias the DiscoverFeatures static API class to dodge namespace shadowing.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.Tests.Protocols.DiscoverFeatures;

/// <summary>
/// FR-PROTO-05a correlation is <c>thid</c> == the query's <c>id</c>, and DIDComm v2.1 threading
/// (threading.md L9, L15) compares those ignoring letter case: a <c>disclose</c> whose <c>thid</c> is
/// the query id in upper case answers the query, through the inline correlator and through a real
/// dispatcher. An id that differs in more than case still answers nothing.
/// </summary>
public sealed class DiscoverFeaturesClientThreadCaseTests
{
    private const string Alice = "did:peer:alice";
    private const string Bob = "did:peer:bob";

    private static readonly FeatureQuery ProtocolWildcard = new() { FeatureType = "protocol", Match = "https://didcomm.org/*" };

    // Rendezvous budget only (the send delegate runs before QueryFeaturesAsync awaits); never a deadline under test.
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(30);

    private static DiscoverFeaturesClient Client(out TaskCompletionSource<Message> sent)
    {
        var captured = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        sent = captured;
        return new DiscoverFeaturesClient((message, _, _) =>
        {
            captured.TrySetResult(message);
            return Task.CompletedTask;
        });
    }

    private static Message Disclose(string thid, string id) => DiscoverFeaturesApi.CreateDisclose(
        from: Bob, to: Alice, thid: thid,
        disclosures: new FeatureDisclosure { FeatureType = "protocol", Id = id });

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

    [Fact]
    public async Task A_disclose_whose_thid_is_the_query_id_in_upper_case_completes_the_query()
    {
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(30));
        var query = await sent.Task.WaitAsync(SyncTimeout);
        var upper = query.Id.ToUpperInvariant();
        upper.Should().NotBe(query.Id, "precondition: the generated query id has letters to re-case");

        var claimed = ((IInboundCorrelator)client).OnInbound(InboundMessageSnapshot.CreateFallback(Authcrypt(Disclose(upper, "legit"))));
        var disclosures = await task;

        claimed.Should().BeTrue();
        disclosures.Should().ContainSingle(d => d.Id == "legit");
    }

    [Fact]
    public async Task A_disclose_whose_thid_is_the_query_id_in_upper_case_completes_the_query_through_a_real_dispatcher()
    {
        var client = Client(out var sent);
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new DiscoverFeaturesHandler(Array.Empty<IFeatureProvider>()));
        await using var dispatcher = new ProtocolDispatcher(
            registry, new InMemoryThreadStateStore(), logger: null, traceOptions: null,
            observers: null, correlators: new IInboundCorrelator[] { client });
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(30));
        var query = await sent.Task.WaitAsync(SyncTimeout);

        await dispatcher.DispatchAsync(Authcrypt(Disclose(query.Id.ToUpperInvariant(), "via-dispatcher")), client: null, new DidCommOptions());
        var disclosures = await task;

        disclosures.Should().ContainSingle(d => d.Id == "via-dispatcher");
    }

    [Fact]
    public async Task A_disclose_whose_thid_differs_from_the_query_id_in_more_than_case_does_not_complete_it()
    {
        // Control for the tests above: the upper-cased id with one more character is another thread.
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromMilliseconds(500));
        var query = await sent.Task.WaitAsync(SyncTimeout);

        var claimed = ((IInboundCorrelator)client).OnInbound(InboundMessageSnapshot.CreateFallback(
            Authcrypt(Disclose(query.Id.ToUpperInvariant() + "0", "other-thread"))));
        var ex = await Record.ExceptionAsync(() => task);

        claimed.Should().BeFalse();
        ex.Should().BeOfType<TimeoutException>("nothing answered the query, so its own deadline ends it");
    }
}
