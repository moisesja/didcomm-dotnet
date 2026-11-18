using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Mediator.Tests.Coordination.HandlerMessages;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// Adversarial pass on the Coordinate Mediation 2.0 mediator role (#99, FR-MED-10): a 2.0 peer
/// trying to reach another peer's 3.0 registrations through type spellings, DID variants and a
/// mismatched <c>from</c>; identity timing; wire values in exception text and logs; the cost of
/// hostile <c>paginate</c> numbers and over-bound <c>updates</c>; reply sizes; a hostile host store;
/// and the rule that whatever the 2.0 handler acts on is inside FR-RR-08 replay admission.
/// </summary>
public sealed class CoordinateMediation20Adv99Tests(ITestOutputHelper output)
{
    private const RecipientUpdateOutcome Success = RecipientUpdateOutcome.Success;
    private const RecipientUpdateOutcome ClientError = RecipientUpdateOutcome.ClientError;
    private const RecipientUpdateOutcome ServerError = RecipientUpdateOutcome.ServerError;

    // A value no log line or exception text may carry: a terminal escape, a CR/LF and a marker.
    private const string Marker = "ADV99SECRET";
    private const string Hostile = "\u001b[31m" + Marker + "\u001b[0m\r\nFAKE LOG LINE ‮";

    // ---- 1. cross-version tenant confusion -------------------------------------------------

    /// <summary>Spellings of the two request names a 2.0 peer could try: case, punctuation, minor, look-alike letters, other versions' names, a double slash, a NUL.</summary>
    private static readonly (string Update, string Query)[] SpellingCases =
    {
        ( Piuri20 + "/keylist-update", Piuri20 + "/keylist-query" ),
        ( Piuri20 + "/keylist_update", Piuri20 + "/keylist_query" ),
        ( Piuri20 + "/KEYLIST-UPDATE", Piuri20 + "/KEYLIST-QUERY" ),
        ( "https://didcomm.org/coordinate-mediation/2.9/keylist-update", "https://didcomm.org/coordinate-mediation/2.9/keylist-query" ),
        ( "https://didcomm.org/coordinate_mediation/2.0/keylist-update", "https://didcomm.org/coordinate_mediation/2.0/keylist-query" ),
        ( "HTTPS://DIDCOMM.ORG/Coordinate-Mediation/2.0/KeyList-Update", "HTTPS://DIDCOMM.ORG/Coordinate-Mediation/2.0/KeyList-Query" ),
        ( "https://didcomm.org/coordinatemediation/2.0/keylistupdate", "https://didcomm.org/coordinatemediation/2.0/keylistquery" ),
        ( "https://didcomm.org/coordinate-mediation/2.0/keylıst-update", "https://didcomm.org/coordinate-mediation/2.0/keylıst-query" ),
        ( "https://didcomm.org/coordinate-mediation/2.0/keyliſt-update", "https://didcomm.org/coordinate-mediation/2.0/keyliſt-query" ),
        ( "https://didcomm.org/coordinate-mediation/2.0/Keylist-update", "https://didcomm.org/coordinate-mediation/2.0/Keylist-query" ),
        ( "https://didcomm.org/coordinate-mediation/3.0/keylist-update", "https://didcomm.org/coordinate-mediation/3.0/keylist-query" ),
        ( Piuri20 + "/recipient-update", Piuri20 + "/recipient-query" ),
        ( "https://didcomm.org//coordinate-mediation/2.0/keylist-update", "https://didcomm.org//coordinate-mediation/2.0/keylist-query" ),
        ( "https://didcomm.org/coordinate-mediation/2.0/keylist-update" + "\u0000", "https://didcomm.org/coordinate-mediation/2.0/keylist-query" + "\u0000" ),
    };

    public static TheoryData<int> Spellings => Indexes(SpellingCases.Length);

    /// <summary>
    /// Held: Alice enrols through 3.0 and registers a DID. Bob, enrolled through 2.0, sends a
    /// remove and an add of that DID and a query under every spelling of the 2.0 names; nothing he
    /// sends removes, steals or lists Alice's registration, and whatever the 2.0 handler answers is a
    /// type inside FR-RR-08 replay admission.
    /// </summary>
    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task No_spelling_of_a_2_0_request_reaches_another_peers_3_0_registration(int index)
    {
        var (updateType, queryType) = SpellingCases[index];
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var alice = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var target = (await fx.NewPeerAsync()).Did;

        await fx.EnrolAsync(alice.Did, mediator.Did);
        (await fx.UpdateAsync(alice.Did, mediator.Did, Add(target))).Should().Equal(Added(target, Success));
        await fx.Enrol20Async(bob.Did, mediator.Did);
        var aliceGrant = await fx.Memory.FindGrantByRecipientDidAsync(target, default);

        Message update;
        Message query;
        try
        {
            update = Raw(updateType, bob.Did, mediator.Did, UpdatesBody((target, "remove"), (target, "add")));
            query = Raw(queryType, bob.Did, mediator.Did, new JsonObject());
        }
        catch (Exception ex) when (ex is MalformedMessageException or ProtocolException)
        {
            output.WriteLine($"{Escape(updateType)}: Core refuses to build it ({ex.GetType().Name}).");
            return;
        }

        foreach (var message in new[] { update, query })
        {
            DispatchOutcome? outcome = null;
            var thrown = await Record.ExceptionAsync(async () => outcome = await fx.SendAsync(message, mediator.Did));
            output.WriteLine($"{Escape(message.Type)}: {(thrown is null ? $"{outcome!.Result} via {outcome.Handler?.GetType().Name}" : thrown.GetType().Name)}");
            if (outcome?.Reply is not { } reply)
                continue;

            MediatorServiceProtocols.IsMediatorServiceType(message.Type).Should().BeTrue("a type a mediator handler answers is in the FR-RR-08 scope");
            if (CoordinateMediation20.TryParseKeylistUpdateResponse(reply, out var updated))
                updated.Select(u => u.Result).Should().AllBeEquivalentTo(ClientError, "Bob holds neither entry");
            if (CoordinateMediation20.TryParseKeylist(reply, out var keys, out var pagination))
            {
                keys.Should().BeEmpty();
                pagination!.Remaining.Should().Be(0, "nothing about Alice's list leaks into Bob's page");
            }
        }

        (await fx.RegisteredAsync(alice.Did)).Should().Equal(target);
        (await fx.RegisteredAsync(bob.Did)).Should().BeEmpty();
        (await fx.Memory.FindGrantByRecipientDidAsync(target, default)).Should().BeSameAs(aliceGrant);
    }

    /// <summary>
    /// Held: Bob (2.0) registers variants of a DID Alice registered through 3.0. DID URLs are
    /// refused; the rest, if registered at all, are separate keys: the forward directory still sends
    /// Alice's DID to Alice's grant, and none of Bob's keys is reachable by a forward whose
    /// <c>next</c> is Alice's DID.
    /// </summary>
    [Fact]
    public async Task Variants_of_another_peers_did_never_shadow_it_in_the_store_or_the_forward_directory()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var alice = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var target = (await fx.NewPeerAsync()).Did;
        await fx.EnrolAsync(alice.Did, mediator.Did);
        (await fx.UpdateAsync(alice.Did, mediator.Did, Add(target))).Should().Equal(Added(target, Success));
        await fx.Enrol20Async(bob.Did, mediator.Did);
        var aliceGrant = await fx.Memory.FindGrantByRecipientDidAsync(target, default);

        var i = target.IndexOf('a', 8);
        var variants = new[]
        {
            target + "#key-1", target + "?service=x", target + "/path", target + ";service=x",
            "DID" + target[3..], target.ToUpperInvariant(),
            i > 0 ? target[..i] + "а" + target[(i + 1)..] : target + "x", // Cyrillic a
            target + "​", target + " ", " " + target, target + "\u0000", "‮" + target,
            target.Replace(":", "%3A", StringComparison.Ordinal),
        };

        var results = await fx.Update20Async(bob.Did, mediator.Did, variants.Select(Add).ToArray());
        for (var n = 0; n < variants.Length; n++)
            output.WriteLine($"{Escape(variants[n])[..Math.Min(40, Escape(variants[n]).Length)]}... -> {results[n].Result}");

        results.Take(4).Select(r => r.Result).Should().AllBeEquivalentTo(ClientError, "a DID URL is refused, never normalized");
        (await fx.Memory.FindGrantByRecipientDidAsync(target, default)).Should().BeSameAs(aliceGrant);
        var forward = fx.Host.Services.GetRequiredService<IForwardRecipientRegistry>();
        (await forward.IsRegisteredAsync(target, default)).Should().BeTrue();
        foreach (var registered in await fx.RegisteredAsync(bob.Did))
        {
            DidComm.Consistency.DidSubject.DidSubjectOf(registered).Should().Be(registered, "only bare DIDs are stored");
            registered.Should().NotBe(target);
        }
        (await fx.RegisteredAsync(alice.Did)).Should().Equal(target);
    }

    /// <summary>
    /// Held: a <c>keylist-update</c> whose plaintext <c>from</c> names Alice, authcrypted by Bob, is
    /// refused by Core (or, if it got through, would act for Bob, the sender key's DID); Alice's
    /// registrations are untouched.
    /// </summary>
    [Fact]
    public async Task A_plaintext_from_that_differs_from_the_authcrypt_sender_cannot_act_for_the_from()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var alice = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var target = (await fx.NewPeerAsync()).Did;
        await fx.EnrolAsync(alice.Did, mediator.Did);
        await fx.UpdateAsync(alice.Did, mediator.Did, Add(target));
        await fx.Enrol20Async(bob.Did, mediator.Did);

        var forged = CoordinateMediation20.CreateKeylistUpdate(alice.Did, mediator.Did, new[] { Remove(target) });
        DispatchOutcome? outcome = null;
        var thrown = await Record.ExceptionAsync(async () => outcome = await fx.SendAsync(forged, mediator.Did, sender: bob.Did));
        output.WriteLine(thrown is null ? $"dispatched: {outcome!.Result}" : $"refused: {thrown.GetType().Name}");

        if (outcome?.Reply is { } reply)
        {
            reply.To.Should().Equal(bob.Did);
            CoordinateMediation20.TryParseKeylistUpdateResponse(reply, out var updated).Should().BeTrue();
            updated![0].Result.Should().Be(ClientError);
        }
        (await fx.RegisteredAsync(alice.Did)).Should().Equal(target);
    }

    /// <summary>
    /// Residual, pinned (D5, one grant per DID; the same in 3.0): a 2.0 peer learns whether any DID
    /// is registered at this mediator by anyone, from the result of an <c>add</c>
    /// (<c>client_error</c> when another grant holds it, <c>success</c> when nobody does, then a
    /// <c>remove</c> to leave no trace). The <c>remove</c> path hides it; the <c>add</c> path cannot
    /// without breaking D5. The default policy also lets a peer register a DID it does not control
    /// first (squatting): the victim's later add is <c>client_error</c>.
    /// </summary>
    [Fact]
    public async Task Residual_a_2_0_add_tells_whether_another_grant_holds_a_did_and_lets_a_peer_squat_one()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var alice = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var carol = await fx.NewPeerAsync();
        var held = (await fx.NewPeerAsync()).Did;
        var free = (await fx.NewPeerAsync()).Did;
        await fx.EnrolAsync(alice.Did, mediator.Did);
        await fx.UpdateAsync(alice.Did, mediator.Did, Add(held));
        await fx.Enrol20Async(bob.Did, mediator.Did);

        (await fx.Update20Async(bob.Did, mediator.Did, Remove(held), Remove(free)))
            .Select(r => r.Result).Should().Equal(ClientError, ClientError);
        (await fx.Update20Async(bob.Did, mediator.Did, Add(held), Add(free), Remove(free)))
            .Select(r => r.Result).Should().Equal(new[] { ClientError, Success, Success }, "the add answers differ: membership oracle");

        // Squatting: Bob registers Carol's own DID before Carol enrols.
        (await fx.Update20Async(bob.Did, mediator.Did, Add(carol.Did))).Single().Result.Should().Be(Success);
        await fx.EnrolAsync(carol.Did, mediator.Did);
        (await fx.UpdateAsync(carol.Did, mediator.Did, Add(carol.Did))).Single().Result.Should().Be(ClientError);
        (await fx.Memory.FindGrantByRecipientDidAsync(carol.Did, default))!.PeerDid.Should().Be(bob.Did, "forwards for Carol now queue for Bob");
    }

    // ---- 3. identity oracles (timing) ------------------------------------------------------

    /// <summary>
    /// Measurement, in process, of the handler alone over one reused unpack (20 000 interleaved
    /// trials per case): a 2.0 <c>remove</c> of a DID another grant holds against one nobody holds
    /// (both <c>client_error</c>; the ratio of medians must stay near 1), and an <c>add</c> of the
    /// mediator's own DID against one another grant holds (both <c>client_error</c>; reported: the
    /// own-DID refusal skips the policy and the store, about 0.5% / 80 ns here).
    /// </summary>
    [Fact]
    public async Task Identity_refusals_have_no_measurable_early_exit_between_held_and_unheld()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var alice = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var held = (await fx.NewPeerAsync()).Did;
        var unheld = (await fx.NewPeerAsync()).Did;
        await fx.EnrolAsync(alice.Did, mediator.Did);
        await fx.UpdateAsync(alice.Did, mediator.Did, Add(held));
        await fx.Enrol20Async(bob.Did, mediator.Did);

        // The handler itself, with the context the dispatcher would build: the dispatcher's own
        // overhead (thread state, async hops) would otherwise drown a difference of a few calls.
        var received = await fx.ReceiveAsync(CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { Remove(unheld) }), mediator.Did);
        var handler = fx.Host.Services.GetRequiredService<CoordinateMediation20Handler>();
        var messages = new Dictionary<RecipientUpdate, (Message Message, ProtocolContext Context)>();
        (Message, ProtocolContext) Prepared(RecipientUpdate update)
        {
            if (!messages.TryGetValue(update, out var prepared))
            {
                var message = CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { update });
                var context = new ProtocolContext(received with { Message = message }, new DidComm.Threading.ThreadState(message.Id), fx.Host.Client, fx.Host.Options, new DidComm.Threading.InMemoryThreadStateStore());
                messages[update] = prepared = (message, context);
            }
            return prepared;
        }
        async Task<long> Time(RecipientUpdate update)
        {
            var (message, context) = Prepared(update);
            var start = Stopwatch.GetTimestamp();
            var reply = await handler.HandleAsync(message, context, default);
            var elapsed = Stopwatch.GetTimestamp() - start;
            CoordinateMediation20.TryParseKeylistUpdateResponse(reply!, out var updated);
            updated![0].Result.Should().Be(ClientError);
            return elapsed;
        }

        var cases = new (string Name, RecipientUpdate Update)[]
        {
            ("remove: held by Alice", Remove(held)),
            ("remove: held by nobody", Remove(unheld)),
            ("add: the mediator's own DID", Add(mediator.Did)),
            ("add: held by Alice", Add(held)),
        };
        var samples = cases.Select(_ => new List<long>()).ToArray();
        for (var warm = 0; warm < 2000; warm++)
            foreach (var c in cases)
                await Time(c.Update);
        for (var trial = 0; trial < 20000; trial++)
            for (var k = 0; k < cases.Length; k++)
                samples[(k + trial) % cases.Length].Add(await Time(cases[(k + trial) % cases.Length].Update));

        var medians = samples.Select(s => s.Order().ElementAt(s.Count / 2)).ToArray();
        for (var k = 0; k < cases.Length; k++)
            output.WriteLine($"{cases[k].Name}: median {medians[k] * 1e9 / Stopwatch.Frequency:F0} ns, p10 {samples[k].Order().ElementAt(samples[k].Count / 10) * 1e9 / Stopwatch.Frequency:F0} ns");
        var removeRatio = (double)medians[0] / medians[1];
        var addRatio = (double)medians[3] / medians[2];
        output.WriteLine($"remove held/unheld = {removeRatio:F3}; add held-by-other/own = {addRatio:F3}");

        removeRatio.Should().BeInRange(0.8, 1.25, "a remove must not tell another grant's DID from nobody's");
    }

    // ---- 4. error-text echo -----------------------------------------------------------------

    private static readonly (string Name, string Body)[] EchoCases =
    {
        ( "keylist-update", $$"""{"updates":[{"recipient_did":"{{new string('a', 2049)}}{{J(Hostile)}}","action":"add"}]}""" ),
        ( "keylist-update", $$"""{"updates":[{"recipient_did":"did:example:x","action":"{{J(Hostile)}}"}]}""" ),
        ( "keylist-update", "{\"updates\":[{\"recipient_did\":{\"" + J(Hostile) + "\":1},\"action\":\"add\"}]}" ),
        ( "keylist-update", $$"""{"updates":"{{J(Hostile)}}"}""" ),
        ( "keylist-update", "{\"" + J(Hostile) + "\":[]}" ),
        ( "keylist-update", $$"""{"updates":[{"recipient_did":"did:example:{{J(Hostile)}}","action":"add"},{"recipient_did":"did:example:y","action":"{{J(Hostile)}}"}]}""" ),
        ( "keylist-query", "{\"paginate\":{\"limit\":\"" + J(Hostile) + "\",\"offset\":0}}" ),
        ( "keylist-query", $$"""{"paginate":"{{J(Hostile)}}"}""" ),
        ( "keylist-query", "{\"paginate\":{\"limit\":1.5,\"offset\":0,\"" + J(Hostile) + "\":1}}" ),
        ( "keylist-query", """{"paginate":{"limit":1,"offset":-1}}""" ),
    };

    public static TheoryData<int> EchoBodies => Indexes(EchoCases.Length);

    /// <summary>
    /// Held: hostile values (terminal escapes, CR/LF, bidi override, a marker) in every member of a
    /// 2.0 request reach neither the exception chain that leaves dispatch nor any log line of any
    /// category at any level. (A value echoed in a <c>keylist-update-response</c> goes back only to
    /// the peer that sent it.)
    /// </summary>
    [Theory]
    [MemberData(nameof(EchoBodies))]
    public async Task No_wire_value_reaches_exception_text_or_any_log_line(int index)
    {
        var (name, body) = EchoCases[index];
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);

        var message = Raw(Piuri20 + "/" + name, bob.Did, mediator.Did, JsonNode.Parse(body)!.AsObject());
        var thrown = await Record.ExceptionAsync(() => fx.SendAsync(message, mediator.Did));

        for (var ex = thrown; ex is not null; ex = ex.InnerException)
        {
            output.WriteLine($"{ex.GetType().Name}: {Escape(ex.Message)}");
            ex.Message.Should().NotContain(Marker).And.NotContain("\u001b").And.NotContain(new string('a', 64));
        }
        foreach (var line in fx.Logs.Entries)
            line.Everything.Should().NotContain(Marker).And.NotContain("\u001b");
    }

    /// <summary>
    /// Held: a <c>type</c> under the 2.0 PIURI whose message name carries escapes and the marker is
    /// routed to the 2.0 handler, ignored, and logged by id only.
    /// </summary>
    [Fact]
    public async Task A_hostile_message_name_under_the_2_0_piuri_is_ignored_and_never_logged()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);

        var type = Piuri20 + "/keylist-update" + Hostile.Replace("/", string.Empty, StringComparison.Ordinal);
        Message message;
        try
        {
            message = Raw(type, bob.Did, mediator.Did, UpdatesBody(("did:example:x", "add")));
        }
        catch (MalformedMessageException ex)
        {
            output.WriteLine($"Core refuses the type at build: {Escape(ex.Message)}");
            ex.Message.Should().NotContain(Marker, "Core's FR-MSG-05 text quotes the type (pre-existing, not #99)");
            return;
        }

        var thrown = await Record.ExceptionAsync(async () =>
        {
            var outcome = await fx.SendAsync(message, mediator.Did);
            output.WriteLine($"{outcome.Result} via {outcome.Handler?.GetType().Name}");
            outcome.Reply.Should().BeNull();
        });
        for (var ex = thrown; ex is not null; ex = ex.InnerException)
            output.WriteLine($"{ex.GetType().Name}: {Escape(ex.Message)}");
        foreach (var line in fx.Logs.Entries)
            line.Everything.Should().NotContain(Marker);
        (await fx.RegisteredAsync(bob.Did)).Should().BeEmpty();
    }

    // ---- 5. resource bounds -----------------------------------------------------------------

    private static readonly (string Limit, string Offset, string Expected)[] PagingCases =
    {
        ( "1e400", "0", "page" ),
        ( "-0", "0", "page" ),
        ( "-0.0", "-0.0", "page" ),
        ( "1", "1e400", "page" ),
        ( "1e99999999999999999999", "0", "page" ),
        ( "-1e400", "0", "page" ),
        ( "1" + new string('0', 300_000), "0", "page" ),
        ( "1." + new string('0', 300_000), "0", "page" ),
        ( "1.5", "0", "malformed" ),
        ( "1e-400", "0", "malformed" ),
        ( "0." + new string('0', 300_000) + "1", "0", "malformed" ),
        ( "{\"a\":1}", "0", "malformed" ),
        ( "[1]", "0", "malformed" ),
        ( "null", "0", "malformed" ),
        ( "100", "-1e400", "malformed" ),
    };

    public static TheoryData<int> Paging => Indexes(PagingCases.Length);

    /// <summary>
    /// Held: <c>paginate</c> numbers of any size or spelling cost one pass over their text; the store
    /// is asked for at most <c>MaxPageSize</c> entries from a clamped offset; fractions, non-numbers
    /// and negative offsets are malformed without a store call.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paging))]
    public async Task Hostile_paginate_numbers_cost_one_pass_and_never_widen_the_page(int index)
    {
        var (limit, offset, expected) = PagingCases[index];
        await using var fx = HandlerFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        await fx.Update20Async(bob.Did, mediator.Did, Add((await fx.NewPeerAsync()).Did));
        fx.FakeStore!.ResetCalls();

        var body = JsonNode.Parse("{\"paginate\":{\"limit\":" + limit + ",\"offset\":" + offset + "}}")!.AsObject();
        var message = Raw(Piuri20 + "/keylist-query", bob.Did, mediator.Did, body);
        var received = await fx.ReceiveAsync(message, mediator.Did);

        var start = Stopwatch.GetTimestamp();
        DispatchOutcome? outcome = null;
        var thrown = await Record.ExceptionAsync(async () => outcome = await fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options));
        var elapsed = Stopwatch.GetElapsedTime(start);
        output.WriteLine($"limit {limit[..Math.Min(20, limit.Length)]} offset {offset}: {(thrown?.GetType().Name ?? outcome!.Result.ToString())} in {elapsed.TotalMilliseconds:F1} ms; store {string.Join(",", fx.FakeStore.ListCalls.Select(c => $"({c.Offset},{c.Limit})"))}");

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        if (expected == "malformed")
        {
            thrown.Should().BeOfType<MalformedMessageException>();
            fx.FakeStore.ListCalls.Should().BeEmpty();
            return;
        }
        thrown.Should().BeNull();
        fx.FakeStore.ListCalls.Should().ContainSingle().Which.Limit.Should().BeInRange(0, 100);
        fx.FakeStore.ListCalls.Single().Offset.Should().BeGreaterThanOrEqualTo(0);
        CoordinateMediation20.TryParseKeylist(outcome!.Reply!, out var keys, out var pagination).Should().BeTrue();
        keys!.Count.Should().BeLessThanOrEqualTo(100);
        pagination!.Remaining.Should().BeGreaterThanOrEqualTo(0);
    }

    private static readonly string[] PoisonCases =
    {
        "\"" + new string('x', 400_000) + "\"",
        new string('[', 60) + new string(']', 60),
        "{\"recipient_did\":" + new string('[', 55) + new string(']', 55) + ",\"action\":\"add\"}",
        "{\"recipient_did\":\"" + new string('d', 400_000) + "\",\"action\":\"add\"}",
        "1e999999999",
    };

    public static TheoryData<int> Poisons => Indexes(PoisonCases.Length);

    /// <summary>
    /// Held (L-055): with <c>MaxUpdatesPerMessage</c> = 4, a fifth entry of any shape — an enormous
    /// string, deep nesting, an over-long DID — is never read: the list is refused from its length,
    /// with no policy or store call and no value in the text.
    /// </summary>
    [Theory]
    [MemberData(nameof(Poisons))]
    public async Task A_poisoned_entry_beyond_the_update_bound_is_never_looked_at(int index)
    {
        var poison = PoisonCases[index];
        await using var fx = HandlerFixture.Create(o => o.MaxUpdatesPerMessage = 4, fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        fx.FakeStore!.ResetCalls();

        var valid = string.Join(",", Enumerable.Range(0, 4).Select(n => $$"""{"recipient_did":"did:example:v{{n}}","action":"add"}"""));
        JsonObject body;
        try
        {
            body = JsonNode.Parse($$"""{"updates":[{{valid}},{{poison}}]}""")!.AsObject();
        }
        catch (JsonException)
        {
            output.WriteLine("the poison is not representable in a JsonNode (depth); skipped");
            return;
        }
        var message = Raw(Piuri20 + "/keylist-update", bob.Did, mediator.Did, body);

        Exception? thrown;
        try
        {
            thrown = await Record.ExceptionAsync(() => fx.SendAsync(message, mediator.Did));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        output.WriteLine($"{thrown?.GetType().Name}: {Escape(thrown?.Message ?? "(none)")}");
        thrown.Should().NotBeNull();
        if (thrown is MalformedMessageException)
            thrown.Message.Should().Contain("more than 4");
        fx.FakeStore.UpdateCalls.Should().BeEmpty();
        fx.Policy!.Registrations.Should().BeEmpty();
    }

    /// <summary>
    /// Measurement: a 2.0 <c>keylist</c> page of 100 DIDs of 2048 characters, authcrypted to the
    /// peer, fits the peer's default <c>MaxReceiveBytes</c>; a <c>keylist-update-response</c>
    /// answering a request that just fits the mediator's limit is reported for comparison (the
    /// answer goes back only to the peer that asked).
    /// </summary>
    [Fact]
    public async Task A_full_keylist_page_of_maximum_length_dids_fits_the_peers_receive_limit()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);

        var dids = Enumerable.Range(0, 100).Select(n => "did:example:" + n.ToString("D4") + new string('z', 2048 - 16)).ToArray();
        dids.Should().OnlyContain(d => d.Length == 2048);
        (await fx.Update20Async(bob.Did, mediator.Did, dids.Select(Add).ToArray())).Should().OnlyContain(r => r.Result == Success);

        var outcome = await fx.SendAsync(CoordinateMediation20.CreateKeylistQuery(bob.Did, mediator.Did), mediator.Did);
        var packed = await fx.Host.Client.PackEncryptedAsync(outcome.Reply!, new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: mediator.Did));
        output.WriteLine($"keylist page of 100 x 2048: {packed.Message.Length} packed characters (limit {fx.Host.Options.MaxReceiveBytes})");
        packed.Message.Length.Should().BeLessThan(fx.Host.Options.MaxReceiveBytes);

        // The update answer is about 1% larger than the request it answers (each entry gains a result).
        var removes = Enumerable.Range(0, 360).Select(n => Remove("did:example:" + n.ToString("D4") + new string('q', 2048 - 16))).ToArray();
        var request = CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, removes);
        var requestPacked = await fx.Host.Client.PackEncryptedAsync(request, new PackEncryptedOptions(Recipients: new[] { mediator.Did }, From: bob.Did));
        var answer = await fx.SendAsync(request, mediator.Did);
        var answerPacked = await fx.Host.Client.PackEncryptedAsync(answer.Reply!, new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: mediator.Did));
        output.WriteLine($"keylist-update of 360 x 2048: request {requestPacked.Message.Length}, answer {answerPacked.Message.Length} packed characters ({(double)answerPacked.Message.Length / requestPacked.Message.Length:F4}x)");
    }

    // ---- 6. hostile host store; replay scope ------------------------------------------------

    public static TheoryData<string> StoreFaults => new()
    {
        "grant: null routing DID", "grant: no routing DID", "grant: 3000-char routing DID", "grant: null list", "grant: throws",
        "page: null DID", "page: 3000-char DID", "page: more than asked", "page: negative total", "page: throws",
        "update: unknown outcome", "update: null list", "update: short list", "update: throws",
    };

    /// <summary>
    /// Held: whatever a hostile host store returns or throws (text carrying the marker), dispatch of a
    /// 2.0 request never throws, never replies with the bad value, and no log line carries the text.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreFaults))]
    public async Task A_hostile_host_store_never_throws_through_2_0_dispatch(string fault)
    {
        await using var fx = HandlerFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.Enrol20Async(bob.Did, mediator.Did);
        var store = fx.FakeStore!;
        var boom = new HandlerBoomException(Marker + Hostile);
        MediationGrant Grant(IReadOnlyList<string> dids) => new(bob.Did, dids);

        Message request;
        switch (fault.Split(':')[0])
        {
            case "grant":
                store.OnGetGrant = fault switch
                {
                    "grant: null routing DID" => (_, _) => new(Grant(new string[] { null! })),
                    "grant: no routing DID" => (_, _) => new(Grant(Array.Empty<string>())),
                    "grant: 3000-char routing DID" => (_, _) => new(Grant(new[] { "did:example:" + new string('r', 3000) + Marker })),
                    "grant: null list" => (_, _) => new(Grant(null!)),
                    _ => (_, _) => throw boom,
                };
                request = CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did);
                break;
            case "page":
                store.OnListRecipients = fault switch
                {
                    "page: null DID" => (_, _, _, _) => new(new RecipientPage(new string[] { null! }, 1)),
                    "page: 3000-char DID" => (_, _, _, _) => new(new RecipientPage(new[] { "did:example:" + new string('p', 3000) + Marker }, 1)),
                    "page: more than asked" => (_, _, limit, _) => new(new RecipientPage(Enumerable.Repeat("did:example:" + Marker, limit + 1).ToArray(), limit + 1)),
                    "page: negative total" => (_, _, _, _) => new(new RecipientPage(new[] { "did:example:" + Marker }, -5)),
                    _ => (_, _, _, _) => throw boom,
                };
                request = CoordinateMediation20.CreateKeylistQuery(bob.Did, mediator.Did);
                break;
            default:
                store.OnUpdateRecipients = fault switch
                {
                    "update: unknown outcome" => (_, u, _) => new(u.Select(_ => (RecipientStoreOutcome)12345).ToArray()),
                    "update: null list" => (_, _, _) => new((IReadOnlyList<RecipientStoreOutcome>)null!),
                    "update: short list" => (_, _, _) => new(Array.Empty<RecipientStoreOutcome>()),
                    _ => (_, _, _) => throw boom,
                };
                request = CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did, new[] { Add("did:example:ok") });
                break;
        }

        DispatchOutcome? outcome = null;
        var thrown = await Record.ExceptionAsync(async () => outcome = await fx.SendAsync(request, mediator.Did));
        output.WriteLine($"{fault}: {(thrown is null ? outcome!.Result.ToString() : thrown.GetType().Name + " " + Escape(thrown.Message))}");

        thrown.Should().BeNull();
        if (outcome!.Reply is { } reply)
        {
            JsonSerializer.Serialize(reply.Body).Should().NotContain(Marker);
            if (CoordinateMediation20.TryParseKeylistUpdateResponse(reply, out var updated))
                updated.Single().Result.Should().Be(ServerError);
        }
        foreach (var line in fx.Logs.Entries)
            line.Everything.Should().NotContain(Marker);
    }

    /// <summary>
    /// Held (FR-RR-08): over a few thousand spellings of the seven 2.0 names — case, <c>-</c>/<c>_</c>,
    /// minors 0–12, Unicode case-folding look-alikes (dotless i, long s, Kelvin sign), doc-URI
    /// variants — every type the registry routes to the 2.0 handler and that the handler would act on
    /// is a mediator-service type, and an authcrypted one is inside replay admission without a
    /// <c>return_route</c>.
    /// </summary>
    [Fact]
    public async Task Every_type_the_2_0_handler_acts_on_is_inside_replay_admission()
    {
        await using var fx = HandlerFixture.Create();
        var h20 = fx.Host.Services.GetRequiredService<CoordinateMediation20Handler>();
        var h30 = fx.Host.Services.GetRequiredService<CoordinateMediationHandler>();
        var registry = new ProtocolHandlerRegistry();
        registry.Register(h30);
        registry.Register(h20);

        var acting = new[] { CoordinateMediation20.MediateRequestUri, CoordinateMediation20.KeylistUpdateUri, CoordinateMediation20.KeylistQueryUri };
        var docs = new[] { "https://didcomm.org", "HTTPS://DIDCOMM.ORG", "https://didcomm.org/", "http://didcomm.org", "https://didcomm.org.", "https://DIDComm.org" };
        var protocols = new[] { "coordinate-mediation", "coordinate_mediation", "COORDINATE-MEDIATION", "coordinatemediation", "coordınate-mediation", "coordinate-medıation", "-coordinate-mediation-", "coordinate--mediation" };
        var names = new[] { "mediate-request", "keylist-update", "keylist-query", "MEDIATE_REQUEST", "keylıst-update", "keyliſt-query", "Keylist-update", "mediaterequest", "keylist-update-", "_keylist_query_" };

        var routed = 0;
        var acted = 0;
        var unicode = 0;
        foreach (var doc in docs)
            foreach (var protocol in protocols)
                for (var minor = 0; minor <= 12; minor++)
                    foreach (var name in names)
                    {
                        var type = $"{doc}/{protocol}/2.{minor}/{name}";
                        if (!registry.TryResolve(type, out var handler) || handler is not CoordinateMediation20Handler)
                            continue;
                        routed++;
                        MediatorServiceProtocols.IsMediatorServiceType(type).Should().BeTrue(type);
                        if (!MessageTypeUri.TryParse(type, out var parsed) || !acting.Any(a => a.Matches(parsed!)))
                            continue;
                        acted++;
                        if (type.Any(c => c > 127))
                            unicode++;
                    }
        output.WriteLine($"routed to 2.0: {routed}; acted on: {acted}; of which with non-ASCII look-alikes: {unicode}");
        routed.Should().BeGreaterThan(0);

        // One end-to-end: a look-alike spelling, authcrypted, no return_route, is in the replay scope.
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var lookalike = Raw("https://didcomm.org/coordinate-mediation/2.0/keylıst-query", bob.Did, mediator.Did, new JsonObject());
        var received = await fx.ReceiveAsync(lookalike, mediator.Did);
        InboundReplayGate.IsInScope(received, ReturnRouteHeader.None).Should().Be(
            registry.TryResolve(lookalike.Type, out var h) && h is CoordinateMediation20Handler,
            "routed to the 2.0 handler exactly when replay admission applies");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static TheoryData<int> Indexes(int count)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < count; i++)
            data.Add(i);
        return data;
    }

    private static string J(string value) => JsonEncodedText.Encode(value).ToString();

    private static string Escape(string value)
        => string.Concat(value.Select(c => c < 32 || c is > '~' and < ' ' || c == '‮' ? $"\\u{(int)c:X4}" : c.ToString()));
}
