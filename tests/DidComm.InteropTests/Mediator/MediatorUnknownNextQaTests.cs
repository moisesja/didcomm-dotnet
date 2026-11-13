using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), FR-ROUTE-11 / D4 through the real endpoint: a <c>next</c> the mediator will not
/// deliver to — never registered, registered and then removed, registered under a grant that was
/// revoked, the mediator's own DID, a grant holder's own DID it never registered, an over-long value,
/// a value that is not a DID — is answered exactly like a known one (status, headers, body, and
/// response time by the FR-MED-08 method), queues nothing, sends nothing, and leaves no DID in the
/// logs; a DID URL <c>next</c> (fragment, path, query) of a registered DID is delivered to that DID.
/// </summary>
/// <remarks>
/// Runs in a collection with parallelization disabled: under the full suite's parallel load the
/// response times are heavy-tailed (p90 around 100 ms) and the medians of four interleaved classes
/// drift apart by noise alone; alone, all four medians agree within a few percent.
/// </remarks>
[Collection(QaSerialTiming.Name)]
public sealed class MediatorUnknownNextQaTests
{
    public enum NextKind
    {
        NeverRegistered,
        Removed,
        Revoked,
        MediatorsOwnDid,
        GrantHolderNotRegistered,
        UnknownDidUrl,
        OverLong,
        NotADid,
    }

    [Theory]
    [InlineData(NextKind.NeverRegistered)]
    [InlineData(NextKind.Removed)]
    [InlineData(NextKind.Revoked)]
    [InlineData(NextKind.MediatorsOwnDid)]
    [InlineData(NextKind.GrantHolderNotRegistered)]
    [InlineData(NextKind.UnknownDidUrl)]
    [InlineData(NextKind.OverLong)]
    [InlineData(NextKind.NotADid)]
    public async Task A_next_the_mediator_will_not_deliver_to_is_answered_202_empty_and_nothing_is_queued_sent_or_logged(NextKind kind)
    {
        await using var f = await Cast.StartAsync();
        var next = f.NextFor(kind);

        var (packed, forward) = await f.Alice.ForwardAsync(f.Mediator.Did, next, await f.Alice.AuthcryptAsync(f.Alice.Note(f.Bob.Did), f.Bob.Did));
        var response = await f.Mediator.PostAsync(packed);
        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
        response.ContentType.Should().BeNull();
        await f.Mediator.FlushAsync();
        await f.Mediator.PushesIdleAsync();

        f.Mediator.Outcomes.Should().ContainSingle().Which.Should().BeOneOf(ForwardOutcomes.DroppedUnknown, ForwardOutcomes.DroppedMalformed);
        f.Mediator.Store.MessageCount.Should().Be(0);
        f.Mediator.PushOutcomes.Should().BeEmpty();
        f.Directory.Calls.Should().Be(0);
        f.Inbox.Entered.Should().Be(0, "no outbound message");
        f.Mediator.Logs.Entries.Should().Contain(e => e.Contains(forward.Id), "the drop is logged by the forward's id");
        f.Mediator.LeakingLogLines(next, f.Removed.Did, f.RevokedRecipient.Did).Should().BeEmpty();
        f.Mediator.Logs.Entries.Should().NotContain(e => e.Contains("evil.example", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("#key-1")]
    [InlineData("/some/path")]
    [InlineData("?service=x")]
    [InlineData(";param=1")]
    public async Task A_DID_URL_next_of_a_registered_DID_is_delivered_to_that_DID(string suffix)
    {
        await using var f = await Cast.StartAsync(directPush: false);

        var note = await Qa.SendNoteAsync(f.Mediator, f.Alice, f.Bob, next: f.Bob.Did + suffix);

        f.Mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        var delivered = (await f.Mediator.FetchAsync(f.Bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(f.Bob, delivered.Payload, f.Alice)).Message.Id.Should().Be(note.Id);
        f.Mediator.LeakingLogLines(f.Bob.Did + suffix).Should().BeEmpty();
    }

    [Fact]
    public Task In_rewrap_mode_a_key_reference_next_of_a_registered_DID_is_delivered_to_that_DID()
        => RewrapDidUrlNextAsync("#key-1");

    [Theory]
    [InlineData("?service=x")]
    [InlineData("/some/path")]
    public Task In_rewrap_mode_a_DID_URL_next_with_a_path_or_query_of_a_registered_DID_is_delivered_to_that_DID(string suffix)
        => RewrapDidUrlNextAsync(suffix);

    private static async Task RewrapDidUrlNextAsync(string suffix)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();

        var note = await Qa.SendNoteAsync(mediator, alice, bob, next: bob.Did + suffix);

        mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued }, "a next the handler admitted as registered is relayed, whatever the rewrap mode");
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        var peeled = await MediatorRoutesQaTests.PeelAsync(bob, delivered.Payload);
        (await MediatorRecipient.UnpackFromSenderAsync(bob, peeled, alice)).Message.Id.Should().Be(note.Id);
    }

    [Fact]
    public async Task Status_headers_body_and_response_time_do_not_tell_known_unknown_removed_and_revoked_next_apart()
    {
        // FR-MED-08 method: warm-up, interleaved rounds over the real endpoint (rotating which class
        // goes first), a flush between batches so relay work never overlaps the measurement, medians,
        // ratio < 3 between every class and the known one.
        const int WarmUp = 8;
        const int Batches = 6;
        const int PerBatch = 8;
        await using var f = await Cast.StartAsync(directPush: false, raiseQuotas: true);
        var classes = new[] { f.Bob.Did, f.Stranger.Did, f.Removed.Did, f.RevokedRecipient.Did };
        var inner = await f.Alice.AuthcryptAsync(f.Alice.Note(f.Bob.Did), f.Bob.Did);
        var total = WarmUp + Batches * PerBatch;
        var packed = new List<string>[classes.Length];
        for (var c = 0; c < classes.Length; c++)
        {
            packed[c] = new List<string>(total);
            for (var i = 0; i < total; i++)
                packed[c].Add((await f.Alice.ForwardAsync(f.Mediator.Did, classes[c], inner)).Packed);
        }

        using var client = f.Mediator.Server.CreateClient();
        var times = classes.Select(_ => new List<double>()).ToArray();
        var headers = classes.Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToArray();
        for (var i = 0; i < total; i++)
        {
            for (var k = 0; k < classes.Length; k++)
            {
                var c = (i + k) % classes.Length;
                var r = await Qa.TimedPostAsync(f.Mediator, client, packed[c][i]);
                r.Status.Should().Be(HttpStatusCode.Accepted);
                r.BodyLength.Should().Be(0);
                headers[c].Add(r.Headers);
                if (i >= WarmUp)
                    times[c].Add(r.Millis);
            }
            if (i >= WarmUp && (i - WarmUp) % PerBatch == PerBatch - 1)
                await f.Mediator.FlushAsync();
        }
        await f.Mediator.FlushAsync();

        f.Mediator.Outcome(ForwardOutcomes.Queued).Should().Be(total);
        f.Mediator.Outcome(ForwardOutcomes.DroppedUnknown).Should().Be(3 * total);
        foreach (var set in headers)
            set.Should().BeEquivalentTo(headers[0], "the response headers are the same for every class");
        var medians = times.Select(Qa.Median).ToArray();
        var all = string.Join(", ", medians.Select(m => m.ToString("F3")));
        for (var c = 1; c < classes.Length; c++)
        {
            var ratio = Math.Max(medians[0], medians[c]) / Math.Min(medians[0], medians[c]);
            ratio.Should().BeLessThan(3.0, "class {0} against the known one (medians known, unknown, removed, revoked: {1} ms)", c, all);
        }
        f.Mediator.LeakingLogLines(f.Removed.Did, f.RevokedRecipient.Did).Should().BeEmpty();
    }

    /// <summary>
    /// A mediator (direct push on, every grant mapped to the inbox so any outbound would show) with:
    /// Bob (registered, his own DID), Carol (registered C, then removed it), Dave (registered D, then
    /// his grant revoked and purged), Erin (granted, registered nothing), a stranger, and Alice.
    /// </summary>
    private sealed class Cast : IAsyncDisposable
    {
        public required MediatorTestServer Mediator { get; init; }
        public required InboxServer Inbox { get; init; }
        public required TestEndpointDirectory Directory { get; init; }
        public required TestPeer Bob { get; init; }
        public required TestPeer Removed { get; init; }
        public required TestPeer RevokedRecipient { get; init; }
        public required TestPeer Erin { get; init; }
        public required TestPeer Stranger { get; init; }
        public required MediatorSender Alice { get; init; }

        public string NextFor(NextKind kind) => kind switch
        {
            NextKind.NeverRegistered => Stranger.Did,
            NextKind.Removed => Removed.Did,
            NextKind.Revoked => RevokedRecipient.Did,
            NextKind.MediatorsOwnDid => Mediator.Did,
            NextKind.GrantHolderNotRegistered => Erin.Did,
            NextKind.UnknownDidUrl => Stranger.Did + "#key-1",
            NextKind.OverLong => Stranger.Did + "#" + new string('k', 2100),
            NextKind.NotADid => "https://evil.example/inbox",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        public static async Task<Cast> StartAsync(bool directPush = true, bool raiseQuotas = false)
        {
            var network = new MediatorNetwork();
            var inbox = await InboxServer.StartAsync(network);
            var directory = new TestEndpointDirectory();
            var mediator = await MediatorTestServer.StartAsync(o =>
            {
                o.Network = network;
                o.Mediator = m =>
                {
                    m.DirectPush = directPush;
                    if (raiseQuotas)
                    {
                        m.Pickup.MaxQueueMessages = 10_000;
                        m.Pickup.MaxQueueBytes = 512L * 1024 * 1024;
                        m.Pickup.MaxStoreMessages = 10_000;
                        m.Pickup.MaxStoreBytes = 512L * 1024 * 1024;
                    }
                };
                o.ConfigureServices = s => s.AddSingleton<IRecipientEndpointDirectory>(directory);
            });
            var bob = await mediator.NewPeerAsync();
            await mediator.EnrolAsync(bob);

            var carol = await mediator.NewPeerAsync();
            var removed = await mediator.NewPeerAsync();
            await mediator.EnrolAsync(carol, removed.Did);
            var update = await mediator.AskAsync(carol, CoordinateMediation.CreateRecipientUpdate(carol.Did, mediator.Did,
                new[] { new RecipientUpdate(removed.Did, RecipientUpdateAction.Remove) }));
            CoordinateMediation.TryParseRecipientUpdateResponse(update.Message, out var results).Should().BeTrue();
            results!.Should().ContainSingle().Which.Result.Should().Be(RecipientUpdateOutcome.Success);

            var dave = await mediator.NewPeerAsync();
            var revoked = await mediator.NewPeerAsync();
            await mediator.EnrolAsync(dave, revoked.Did);
            (await mediator.Queue.RevokeAndPurgeAsync(dave.Did)).Revoked.Should().BeTrue();

            var erin = await mediator.NewPeerAsync();
            (await mediator.RequestMediationAsync(erin)).Should().Equal(mediator.Did);

            var stranger = await mediator.NewPeerAsync();
            foreach (var peer in new[] { bob, carol, dave, erin, stranger })
                directory.Map(peer.Did, inbox.Endpoint("/inbox"));
            var alice = await mediator.NewSenderAsync();
            return new Cast
            {
                Mediator = mediator, Inbox = inbox, Directory = directory, Bob = bob, Removed = removed,
                RevokedRecipient = revoked, Erin = erin, Stranger = stranger, Alice = alice,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Mediator.DisposeAsync();
            await Inbox.DisposeAsync();
        }
    }
}
