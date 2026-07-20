using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Threading;
using DidComm.Transports;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    private static UnpackResult Disclose(
        string thid, string from = Bob, bool authenticated = true,
        string? senderKid = "did:peer:bob#key-1", string? signerKid = null,
        string to = Alice, string? recipientKid = null, bool? encrypted = null,
        params FeatureDisclosure[] disclosures)
    {
        var msg = DiscoverFeaturesApi.CreateDisclose(from: from, to: to, thid: thid, disclosures: disclosures);
        var isEncrypted = encrypted ?? (authenticated && senderKid is not null);
        return new UnpackResult(
            Message: msg,
            Stack: Array.Empty<DidComm.Jose.EnvelopeKind>(),
            Encrypted: isEncrypted,
            Authenticated: authenticated,
            NonRepudiation: authenticated && signerKid is not null,
            AnonymousSender: isEncrypted && !authenticated,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: authenticated ? signerKid : null,
            SenderKid: authenticated ? senderKid : null,
            RecipientKid: isEncrypted ? recipientKid ?? $"{to}#key-1" : null,
            AllRecipientKids: Array.Empty<string>(),
            FromPrior: null);
    }

    // Correlation is a synchronous internal correlator (IInboundCorrelator), invoked inline by the
    // dispatcher; here we call it directly with the unpacked disclose.
    private static Task Feed(DiscoverFeaturesClient client, UnpackResult disclose)
    {
        ((IInboundCorrelator)client).OnInbound(InboundMessageSnapshot.CreateFallback(disclose));
        return Task.CompletedTask;
    }

    private sealed class MutatingHandler(Action<Message> mutate, bool throws = false) : IProtocolHandler
    {
        public string ProtocolUri => "https://didcomm.org/discover-features/2.0";

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
        {
            mutate(message);
            return throws
                ? Task.FromException<Message?>(new InvalidOperationException("handler exploded"))
                : Task.FromResult<Message?>(null);
        }
    }

    private sealed class CountingLogger<T> : ILogger<T>
    {
        private int _warnings;
        public int Warnings => Volatile.Read(ref _warnings);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Interlocked.Increment(ref _warnings);
        }
    }

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
        // End-to-end: the built-in handler still owns the PIURI and treats the inbound disclose as a
        // terminal leaf (NoReply); the inline correlator is what completes the waiting initiator —
        // synchronously, so `await task` is already complete when DispatchAsync returns.
        var client = Client(out var sent);
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new DiscoverFeaturesHandler(Array.Empty<IFeatureProvider>()));
        await using var dispatcher = new ProtocolDispatcher(
            registry, new InMemoryThreadStateStore(), logger: null, traceOptions: null,
            observers: null, correlators: new IInboundCorrelator[] { client });

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
    public async Task Correlation_uses_the_pre_handler_snapshot_even_when_the_handler_mutates_then_throws()
    {
        var client = Client(out var sent);
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new MutatingHandler(message =>
        {
            message.From = Mallory;
            message.To = new[] { Mallory };
            message.Thid = "rewritten";
            message.Body = null;
        }, throws: true));
        await using var dispatcher = new ProtocolDispatcher(
            registry, new InMemoryThreadStateStore(), logger: null, traceOptions: null,
            observers: null, correlators: new IInboundCorrelator[] { client });

        var queryTask = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var inbound = Authcrypt(DiscoverFeaturesApi.CreateDisclose(Bob, Alice, query.Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "original" }));

        var dispatch = async () => await dispatcher.DispatchAsync(inbound, client: null, new DidCommOptions());
        await dispatch.Should().ThrowAsync<InvalidOperationException>();

        (await queryTask).Should().ContainSingle(d => d.Id == "original",
            "correlation must complete from the immutable pre-handler message even when handler code mutates and fails");
    }

    [Fact]
    public async Task Handler_cannot_rewrite_a_wrong_responder_into_the_expected_identity()
    {
        var client = Client(out var sent);
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new MutatingHandler(message => message.From = Bob));
        await using var dispatcher = new ProtocolDispatcher(
            registry, new InMemoryThreadStateStore(), logger: null, traceOptions: null,
            observers: null, correlators: new IInboundCorrelator[] { client });

        var queryTask = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forged = DiscoverFeaturesApi.CreateDisclose(Mallory, Alice, query.Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "forged" });

        await dispatcher.DispatchAsync(Authcrypt(forged), client: null, new DidCommOptions());
        queryTask.IsCompleted.Should().BeFalse(
            "the responder check must use the immutable pre-handler sender, not the handler-rewritten Message.From");

        var legitimate = DiscoverFeaturesApi.CreateDisclose(Bob, Alice, query.Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "legit" });
        await dispatcher.DispatchAsync(Authcrypt(legitimate), client: null, new DidCommOptions());
        (await queryTask).Should().ContainSingle(d => d.Id == "legit");
    }

    [Fact]
    public async Task Synthetic_snapshot_failure_is_guarded_and_cannot_clobber_dispatch()
    {
        var client = Client(out _);
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new MutatingHandler(_ => { }));
        await using var dispatcher = new ProtocolDispatcher(
            registry, new InMemoryThreadStateStore(), logger: null, traceOptions: null,
            observers: null, correlators: new IInboundCorrelator[] { client });
        var message = DiscoverFeaturesApi.CreateDisclose(Bob, Alice, "no-pending-query");
        message.AdditionalHeaders = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["undefined-cannot-serialize"] = default,
        };

        var dispatch = async () => await dispatcher.DispatchAsync(Authcrypt(message), client: null, new DidCommOptions());

        var result = await dispatch.Should().NotThrowAsync(
            "an optional internal snapshot must not turn a valid handler outcome into a dispatcher failure");
        result.Which.Result.Should().Be(DispatchResult.NoReply);
    }

    [Fact]
    public async Task Timeout_throws_an_actionable_TimeoutException()
    {
        var client = Client(out _);

        var act = async () => await client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromMilliseconds(50));

        (await act.Should().ThrowAsync<TimeoutException>())
            .Which.Message.Should().Contain("did not complete");
    }

    [Fact]
    public async Task The_timeout_bounds_the_send_too_not_just_the_wait()
    {
        // A send that blocks until its (linked) token cancels proves the one deadline covers send +
        // wait: with a short timeout the whole call fails fast even though no disclose is ever fed and
        // the send never returns on its own.
        var client = new DiscoverFeaturesClient(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // blocks until the operation deadline cancels it
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var act = async () => await client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromMilliseconds(200));

        await act.Should().ThrowAsync<TimeoutException>();
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the deadline cancelled the blocked send rather than waiting indefinitely");
    }

    [Fact]
    public async Task A_transport_send_failure_propagates_unchanged_not_mislabeled_as_timeout()
    {
        // Regression for the broad catch(TimeoutException): a transport/send failure must surface as
        // itself, not be relabeled "no correlated disclose".
        var boom = new InvalidOperationException("transport exploded");
        var client = new DiscoverFeaturesClient((_, _, _) => throw boom);

        var act = async () => await client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
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
    public async Task Disclose_not_addressed_to_the_query_requester_is_ignored()
    {
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Feed(client, Disclose(query.Id, to: Mallory, disclosures: Forged()));
        task.IsCompleted.Should().BeFalse("a response for another local identity cannot answer Alice's query");

        await Feed(client, Disclose(query.Id, disclosures: Legit()));
        (await task).Should().ContainSingle(d => d.Id == "legit");
    }

    [Fact]
    public async Task Encrypted_disclose_decrypted_for_another_local_identity_is_ignored()
    {
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Feed(client, Disclose(query.Id, recipientKid: $"{Mallory}#key-1", disclosures: Forged()));
        task.IsCompleted.Should().BeFalse("message.to alone is insufficient in a multi-DID secret store; the decrypting recipient must be Alice");

        await Feed(client, Disclose(query.Id, disclosures: Legit()));
        (await task).Should().ContainSingle(d => d.Id == "legit");
    }

    [Fact]
    public async Task Authenticated_disclose_without_a_sender_or_signer_key_id_is_ignored()
    {
        // Defense in depth (F4): `Authenticated` must be backed by an actual authenticating key id
        // (skid or signer kid) — that binding is what makes `from` trustworthy. An envelope that
        // reports authenticated but carries neither must not complete the waiter.
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Feed(client, Disclose(query.Id, senderKid: null, signerKid: null, disclosures: Forged()));
        task.IsCompleted.Should().BeFalse("an authenticated-but-keyless envelope must not complete a pending query");

        await Feed(client, Disclose(query.Id, disclosures: Legit()));
        (await task).Should().ContainSingle(d => d.Id == "legit");
    }

    [Fact]
    public async Task Signed_only_disclose_with_a_signer_kid_completes()
    {
        // Authenticated via a verified JWS signer (no authcrypt skid) is a valid authenticated
        // envelope — the F4 key-id gate accepts a SignerKid too.
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Feed(client, Disclose(query.Id, senderKid: null, signerKid: "did:peer:bob#sig-1",
            disclosures: new FeatureDisclosure { FeatureType = "protocol", Id = "signed-ok" }));

        (await task).Should().ContainSingle(d => d.Id == "signed-ok");
    }

    [Fact]
    public async Task Authenticating_key_ids_must_match_the_claimed_responder_did_subject()
    {
        var client = Client(out var sent);
        var task = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var (query, _) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Feed(client, Disclose(query.Id, senderKid: $"{Mallory}#key-1", disclosures: Forged()));
        task.IsCompleted.Should().BeFalse("a Bob plaintext cannot be authenticated by Mallory's authcrypt key");

        await Feed(client, Disclose(query.Id, senderKid: $"{Bob}#key-1", signerKid: $"{Mallory}#sig-1",
            disclosures: Forged()));
        task.IsCompleted.Should().BeFalse("every present authenticating key must agree with the claimed sender");

        await Feed(client, Disclose(query.Id, senderKid: "not-a-did-key", disclosures: Forged()));
        task.IsCompleted.Should().BeFalse("an unparseable key id cannot back an authenticated sender claim");

        await Feed(client, Disclose(query.Id, disclosures: Legit()));
        (await task).Should().ContainSingle(d => d.Id == "legit");
    }

    // NOTE: correlation compares DID *subjects* (PRD §4.3, `DidSubject.SameDidSubject`), which is a
    // strict superset of a raw-string compare and only ever accepts MORE than raw equality. The
    // remaining tests exercise the equal-subject accept and the different-subject reject paths. The
    // narrow "differs only in DID-URL path/query but same subject" case is deliberately NOT unit-tested
    // here: it hinges on net-did's DID-URL suffix parser, which returns a different subject for a
    // `?query` form on Windows vs. Linux (an upstream cross-platform inconsistency, filed separately) —
    // it is NOT covered by the bare-DID round-trip. In practice `from`/`to` are bare DIDs, so the
    // production behavior is unaffected either way.

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
    public async Task Disclosure_parsing_runs_after_the_inline_correlator_returns()
    {
        var sent = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseParser = new ManualResetEventSlim();
        var client = new DiscoverFeaturesClient(
            (message, _, _) => { sent.TrySetResult(message); return Task.CompletedTask; },
            snapshot =>
            {
                parserStarted.TrySetResult();
                releaseParser.Wait();
                return DiscoverFeaturesApi.ReadDisclosures(snapshot.DeserializeMessage());
            });
        var queryTask = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var query = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = InboundMessageSnapshot.CreateFallback(Disclose(query.Id, disclosures: Legit()));

        var correlate = Task.Run(() => ((IInboundCorrelator)client).OnInbound(snapshot));
        await parserStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await correlate.WaitAsync(TimeSpan.FromSeconds(5));
        queryTask.IsCompleted.Should().BeFalse("the requester continuation is blocked in its parser, not the receive correlator");

        releaseParser.Set();
        (await queryTask).Should().ContainSingle(d => d.Id == "legit");
    }

    [Fact]
    public async Task Reentrant_send_response_never_parses_on_the_calling_transport_thread()
    {
        DiscoverFeaturesClient? client = null;
        var parserThread = 0;
        client = new DiscoverFeaturesClient(
            (query, _, _) =>
            {
                var response = InboundMessageSnapshot.CreateFallback(Disclose(query.Id, disclosures: Legit()));
                ((IInboundCorrelator)client!).OnInbound(response);
                return Task.CompletedTask;
            },
            snapshot =>
            {
                parserThread = Environment.CurrentManagedThreadId;
                return DiscoverFeaturesApi.ReadDisclosures(snapshot.DeserializeMessage());
            });

        var callerThread = 0;
        var completed = new TaskCompletionSource<IReadOnlyList<FeatureDisclosure>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            try
            {
                var result = client.QueryFeaturesAsync(
                    Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                completed.TrySetResult(result);
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
            }
        });
        thread.Start();

        var disclosures = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
        disclosures.Should().ContainSingle(d => d.Id == "legit");
        parserThread.Should().NotBe(callerThread,
            "even an already-completed response must force parsing off the reentrant send/transport stack");
    }

    [Fact]
    public async Task Duplicate_valid_disclosures_have_one_atomic_winner_and_parse_once()
    {
        var sent = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parses = 0;
        var client = new DiscoverFeaturesClient(
            (message, _, _) => { sent.TrySetResult(message); return Task.CompletedTask; },
            snapshot =>
            {
                Interlocked.Increment(ref parses);
                return DiscoverFeaturesApi.ReadDisclosures(snapshot.DeserializeMessage());
            });
        var queryTask = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var query = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var correlator = (IInboundCorrelator)client;

        correlator.OnInbound(InboundMessageSnapshot.CreateFallback(Disclose(query.Id,
            disclosures: new FeatureDisclosure { FeatureType = "protocol", Id = "winner" })));
        correlator.OnInbound(InboundMessageSnapshot.CreateFallback(Disclose(query.Id,
            disclosures: new FeatureDisclosure { FeatureType = "protocol", Id = "duplicate" })));

        (await queryTask).Should().ContainSingle(d => d.Id == "winner");
        parses.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_known_thread_logging_is_rate_limited()
    {
        var sent = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CountingLogger<DiscoverFeaturesClient>();
        var client = new DiscoverFeaturesClient(
            (message, _, _) => { sent.TrySetResult(message); return Task.CompletedTask; }, logger);
        var queryTask = client.QueryFeaturesAsync(Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(5));
        var query = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forged = InboundMessageSnapshot.CreateFallback(Disclose(query.Id, authenticated: false, disclosures: Forged()));
        var correlator = (IInboundCorrelator)client;

        for (var i = 0; i < 10_000; i++)
            correlator.OnInbound(forged);

        logger.Warnings.Should().Be(11, "only rejection 1 and each multiple of 1000 should log");
        correlator.OnInbound(InboundMessageSnapshot.CreateFallback(Disclose(query.Id, disclosures: Legit())));
        (await queryTask).Should().ContainSingle(d => d.Id == "legit");
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
    public async Task Response_and_cancellation_have_one_terminal_winner_and_never_parse_after_cancellation_wins()
    {
        // Exercise the race repeatedly. The response and cancellation callbacks both compete on the
        // PendingQuery CAS: success parses exactly once; cancellation parses zero times. A late token
        // signal cannot retroactively replace a response that already won the terminal transition.
        for (var i = 0; i < 256; i++)
        {
            var sent = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
            var parses = 0;
            var client = new DiscoverFeaturesClient(
                (message, _, _) => { sent.TrySetResult(message); return Task.CompletedTask; },
                snapshot =>
                {
                    Interlocked.Increment(ref parses);
                    return DiscoverFeaturesApi.ReadDisclosures(snapshot.DeserializeMessage());
                });
            using var cts = new CancellationTokenSource();
            var queryTask = client.QueryFeaturesAsync(
                Alice, Bob, new[] { ProtocolWildcard }, TimeSpan.FromSeconds(30), ct: cts.Token);
            var query = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var response = InboundMessageSnapshot.CreateFallback(Disclose(query.Id, disclosures: Legit()));
            using var start = new ManualResetEventSlim();

            var complete = Task.Run(() =>
            {
                start.Wait();
                ((IInboundCorrelator)client).OnInbound(response);
            });
            var cancel = Task.Run(() =>
            {
                start.Wait();
                cts.Cancel();
            });
            start.Set();
            await Task.WhenAll(complete, cancel).WaitAsync(TimeSpan.FromSeconds(5));

            try
            {
                (await queryTask.WaitAsync(TimeSpan.FromSeconds(5))).Should()
                    .ContainSingle(disclosure => disclosure.Id == "legit");
                Volatile.Read(ref parses).Should().Be(1);
            }
            catch (OperationCanceledException)
            {
                Volatile.Read(ref parses).Should().Be(0,
                    "a cancellation-winning terminal transition cancels the TCS before parsing");
            }
        }
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
        SenderKid: $"{msg.From}#key-1",
        RecipientKid: $"{msg.To![0]}#key-1",
        AllRecipientKids: Array.Empty<string>(),
        FromPrior: null);
}
