using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Threading;
using DidComm.Transports;
using FluentAssertions;
using Xunit;

// L-014: alias the DiscoverFeatures static API class to dodge namespace shadowing.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.Tests.Protocols.DiscoverFeatures;

/// <summary>
/// FR-PROTO-05a — the initiator round-trip: <see cref="DiscoverFeaturesClient.QueryFeaturesAsync"/>
/// sends a <c>queries</c> and awaits the <c>thid</c>-correlated <c>disclose</c>. Covers
/// correlation, timeout/cancellation cleanup, concurrency, and the spoofing defenses
/// (unauthenticated or wrong-sender disclosures must neither complete nor cancel a query).
/// </summary>
public sealed class DiscoverFeaturesClientTests
{
    private const string Alice = "did:peer:alice";
    private const string Bob = "did:peer:bob";
    private const string Mallory = "did:peer:mallory";

    private static readonly FeatureQuery ProtocolWildcard = new() { FeatureType = "protocol", Match = "https://didcomm.org/*" };

    /// <summary>Client over a send delegate that records the outgoing queries message.</summary>
    private static DiscoverFeaturesClient Client(out TaskCompletionSource<(Message Message, SendOptions Options)> sent)
    {
        var captured = new TaskCompletionSource<(Message, SendOptions)>(TaskCreationOptions.RunContinuationsAsynchronously);
        sent = captured;
        return new DiscoverFeaturesClient((message, options, _) =>
        {
            captured.TrySetResult((message, options));
            return Task.CompletedTask;
        });
    }

    private static InboundObservation Disclose(
        string thid, string from = Bob, bool authenticated = true, params FeatureDisclosure[] disclosures)
    {
        var msg = DiscoverFeaturesApi.CreateDisclose(from: from, to: Alice, thid: thid, disclosures: disclosures);
        return new InboundObservation(
            Message: msg,
            Encrypted: authenticated,
            Authenticated: authenticated,
            NonRepudiation: false,
            AnonymousSender: !authenticated,
            SenderKid: null,
            SignerKid: null);
    }

    private static Task Feed(DiscoverFeaturesClient client, InboundObservation observation)
        => ((IProtocolObserver)client).OnMessageReceivedAsync(observation, CancellationToken.None);

    [Fact]
    public async Task Round_trip_returns_the_correlated_disclosures_and_sends_authcrypt()
    {
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, options) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        query.Type.Should().Be(DiscoverFeaturesApi.QueriesType);
        options.Recipients.Should().Equal(Bob);
        options.From.Should().Be(Alice, "the query must be authcrypt so the responder knows who is asking");

        await Feed(client, Disclose(query.Id, disclosures: new FeatureDisclosure
        {
            FeatureType = "protocol",
            Id = "https://didcomm.org/trust-ping/2.0",
        }));

        var disclosures = await task;
        disclosures.Should().HaveCount(1);
        disclosures[0].FeatureType.Should().Be("protocol");
        disclosures[0].Id.Should().Be("https://didcomm.org/trust-ping/2.0");
    }

    [Fact]
    public async Task Round_trip_completes_through_a_real_dispatcher()
    {
        // End-to-end through the FR-PROTO-12 seam: the built-in handler still owns the PIURI
        // and treats the inbound disclose as a terminal leaf (NoReply); the observer side
        // channel is what completes the waiting initiator.
        var client = Client(out var sent);
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new DiscoverFeaturesHandler(Array.Empty<IFeatureProvider>()));
        var dispatcher = new ProtocolDispatcher(
            registry, new InMemoryThreadStateStore(), observers: new IProtocolObserver[] { client });

        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disclose = DiscoverFeaturesApi.CreateDisclose(from: Bob, to: Alice, thid: query.Id,
            disclosures: new FeatureDisclosure { FeatureType = "protocol", Id = "https://didcomm.org/empty/1.0" });
        var outcome = await dispatcher.DispatchAsync(Authcrypt(disclose), client: null, new DidCommOptions());

        outcome.Result.Should().Be(DispatchResult.NoReply, "the responder-side handler still drops a disclose (terminal leaf)");
        var disclosures = await task;
        disclosures.Should().ContainSingle(d => d.Id == "https://didcomm.org/empty/1.0");
    }

    [Fact]
    public async Task Timeout_throws_an_actionable_TimeoutException()
    {
        var client = Client(out _);

        var act = async () => await client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromMilliseconds(50));

        (await act.Should().ThrowAsync<TimeoutException>())
            .Which.Message.Should().Contain("no correlated 'disclose'");
    }

    [Fact]
    public async Task Unauthenticated_disclose_neither_completes_nor_cancels_the_query()
    {
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A forged anoncrypt/plaintext disclose that guessed the query id must be ignored…
        await Feed(client, Disclose(query.Id, authenticated: false, disclosures: Forged()));
        task.IsCompleted.Should().BeFalse("an unauthenticated disclose must not complete a pending query");

        // …and must not deny the legitimate response either.
        await Feed(client, Disclose(query.Id, disclosures: Legit()));
        (await task).Should().ContainSingle(d => d.Id == "legit");
    }

    [Fact]
    public async Task Disclose_from_a_sender_other_than_the_queried_did_is_ignored()
    {
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Feed(client, Disclose(query.Id, from: Mallory, disclosures: Forged()));
        task.IsCompleted.Should().BeFalse("even an authenticated third party must not answer for the queried responder");

        await Feed(client, Disclose(query.Id, from: Bob, disclosures: Legit()));
        (await task).Should().ContainSingle(d => d.Id == "legit");
    }

    [Fact]
    public async Task Unsolicited_disclose_is_ignored_without_error()
    {
        var client = Client(out _);
        var act = async () => await Feed(client, Disclose("no-such-pending-query"));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Concurrent_queries_correlate_independently()
    {
        var sentMessages = new List<Message>();
        var client = new DiscoverFeaturesClient((message, _, _) =>
        {
            lock (sentMessages) sentMessages.Add(message);
            return Task.CompletedTask;
        });

        var first = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var second = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        sentMessages.Should().HaveCount(2);

        // Answer out of order to prove correlation is by thid, not arrival order.
        await Feed(client, Disclose(sentMessages[1].Id, disclosures: new FeatureDisclosure { FeatureType = "protocol", Id = "for-second" }));
        await Feed(client, Disclose(sentMessages[0].Id, disclosures: new FeatureDisclosure { FeatureType = "protocol", Id = "for-first" }));

        (await first).Should().ContainSingle(d => d.Id == "for-first");
        (await second).Should().ContainSingle(d => d.Id == "for-second");
    }

    [Fact]
    public async Task Cancellation_abandons_the_pending_query()
    {
        var client = Client(out var sent);
        using var cts = new CancellationTokenSource();
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(30), ct: cts.Token);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();

        await ((Func<Task>)(async () => await task)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Empty_disclosures_reply_is_meaningful_and_returned_as_empty_list()
    {
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Feed(client, Disclose(query.Id));

        (await task).Should().BeEmpty("per FR-PROTO-05 an empty disclosures array asserts 'no matches', not 'unsupported'");
    }

    [Fact]
    public async Task Non_positive_timeout_is_rejected()
    {
        var client = Client(out _);
        var act = async () => await client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.Zero);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static FeatureDisclosure[] Forged() => new[] { new FeatureDisclosure { FeatureType = "protocol", Id = "forged" } };
    private static FeatureDisclosure[] Legit() => new[] { new FeatureDisclosure { FeatureType = "protocol", Id = "legit" } };

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
        SenderKid: $"{Bob}#key-1",
        RecipientKid: null,
        AllRecipientKids: Array.Empty<string>(),
        FromPrior: null);
}
