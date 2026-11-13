using System.Diagnostics;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// The <see cref="IMediationStore"/> contract as <see cref="InMemoryMediationStore"/> keeps it
/// (FR-MED-05/08): one grant per peer, one grant per recipient DID decided atomically, bounds
/// reported as outcomes, registration-order paging, and a forward lookup that does the same work
/// for a registered and an unregistered DID.
/// </summary>
public sealed class InMemoryMediationStoreTests(ITestOutputHelper output)
{
    private const string Bob = "did:example:bob";
    private const string Mallory = "did:example:mallory";
    private static readonly string[] Routing = { "did:example:mediator" };
    private static readonly CancellationToken None = CancellationToken.None;

    private static InMemoryMediationStore NewStore(int maxGrants = 100, int perGrant = 100, int total = 1000)
        => new(new CoordinateMediationOptions { MaxGrants = maxGrants, MaxRecipientsPerGrant = perGrant, MaxRecipients = total });

    private static RecipientUpdate Add(string did) => new(did, RecipientUpdateAction.Add);

    private static RecipientUpdate Remove(string did) => new(did, RecipientUpdateAction.Remove);

    private static async Task<InMemoryMediationStore> WithGrantsAsync(InMemoryMediationStore store, params string[] peers)
    {
        foreach (var peer in peers)
            (await store.GrantAsync(new MediationGrant(peer, Routing), None)).Should().NotBeNull();
        return store;
    }

    private static async Task<IReadOnlyList<string>> AllAsync(InMemoryMediationStore store, string peer)
        => (await store.ListRecipientsAsync(peer, 0, int.MaxValue, None)).Dids;

    // ---- grants ---------------------------------------------------------------------------

    [Fact]
    public async Task Grant_is_get_or_add_and_an_existing_grant_is_returned_unchanged()
    {
        var store = NewStore();
        (await store.GetGrantAsync(Bob, None)).Should().BeNull();

        var first = await store.GrantAsync(new MediationGrant(Bob, new[] { "did:example:m1" }), None);
        first!.PeerDid.Should().Be(Bob);
        first.RoutingDids.Should().Equal("did:example:m1");

        // A second grant for the same peer does not replace the first, whatever it carries.
        var second = await store.GrantAsync(new MediationGrant(Bob, new[] { "did:example:m2" }), None);
        second.Should().BeSameAs(first);
        (await store.GetGrantAsync(Bob, None)).Should().BeSameAs(first);
        store.GrantCount.Should().Be(1);
    }

    [Fact]
    public async Task A_stored_grant_cannot_be_changed_through_the_callers_list()
    {
        var store = NewStore();
        var mine = new List<string> { "did:example:m1" };
        var stored = await store.GrantAsync(new MediationGrant(Bob, mine), None);

        mine[0] = "did:example:evil";
        mine.Add("did:example:evil2");

        (await store.GetGrantAsync(Bob, None))!.RoutingDids.Should().Equal("did:example:m1");
        stored!.RoutingDids.Should().Equal("did:example:m1");
        var cast = () => ((IList<string>)stored.RoutingDids)[0] = "x";
        cast.Should().Throw<NotSupportedException>("the stored list is read-only");
    }

    [Fact]
    public async Task At_MaxGrants_a_new_peer_gets_null_and_an_existing_peer_still_gets_its_grant()
    {
        var store = NewStore(maxGrants: 2);
        await WithGrantsAsync(store, "did:example:p1", "did:example:p2");

        (await store.GrantAsync(new MediationGrant("did:example:p3", Routing), None)).Should().BeNull();
        (await store.GetGrantAsync("did:example:p3", None)).Should().BeNull();
        (await store.GrantAsync(new MediationGrant("did:example:p1", Routing), None)).Should().NotBeNull("get-or-add returns the existing grant also when the store is full");
        store.GrantCount.Should().Be(2);

        // Revoking frees a slot.
        (await store.RevokeAsync("did:example:p2", None)).Should().BeTrue();
        (await store.GrantAsync(new MediationGrant("did:example:p3", Routing), None)).Should().NotBeNull();
    }

    // ---- ownership (D5) -------------------------------------------------------------------

    [Fact]
    public async Task A_did_belongs_to_the_first_grant_that_registers_it()
    {
        var store = await WithGrantsAsync(NewStore(), Bob, Mallory);

        (await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:r1") }, None)).Should().Equal(RecipientStoreOutcome.Added);
        (await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:r1") }, None)).Should().Equal(RecipientStoreOutcome.AlreadyRegistered);
        (await store.UpdateRecipientsAsync(Mallory, new[] { Add("did:example:r1") }, None)).Should().Equal(RecipientStoreOutcome.OwnedByAnotherGrant);
        (await store.UpdateRecipientsAsync(Mallory, new[] { Remove("did:example:r1") }, None)).Should().Equal(RecipientStoreOutcome.NotRegistered);

        (await AllAsync(store, Bob)).Should().Equal("did:example:r1");
        (await AllAsync(store, Mallory)).Should().BeEmpty();
        (await store.FindGrantByRecipientDidAsync("did:example:r1", None))!.PeerDid.Should().Be(Bob);
        store.RecipientCount.Should().Be(1);
    }

    [Fact]
    public async Task Remove_gives_the_same_answer_for_someone_elses_did_and_for_nobodys()
    {
        var store = await WithGrantsAsync(NewStore(), Bob, Mallory);
        await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:bobs") }, None);

        var outcomes = await store.UpdateRecipientsAsync(Mallory, new[] { Remove("did:example:bobs"), Remove("did:example:nobodys") }, None);

        outcomes.Should().Equal(RecipientStoreOutcome.NotRegistered, RecipientStoreOutcome.NotRegistered);
        (await AllAsync(store, Bob)).Should().Equal("did:example:bobs");
    }

    [Fact]
    public async Task After_the_owner_removes_a_did_another_peer_can_register_it()
    {
        var store = await WithGrantsAsync(NewStore(), Bob, Mallory);
        await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:r1") }, None);

        (await store.UpdateRecipientsAsync(Bob, new[] { Remove("did:example:r1") }, None)).Should().Equal(RecipientStoreOutcome.Removed);
        (await store.FindGrantByRecipientDidAsync("did:example:r1", None)).Should().BeNull();
        (await store.UpdateRecipientsAsync(Mallory, new[] { Add("did:example:r1") }, None)).Should().Equal(RecipientStoreOutcome.Added);
        (await store.FindGrantByRecipientDidAsync("did:example:r1", None))!.PeerDid.Should().Be(Mallory);
    }

    [Fact]
    public async Task Without_a_grant_every_entry_is_NoGrant_and_nothing_is_stored()
    {
        var store = NewStore();
        var outcomes = await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:a"), Remove("did:example:b"), Add("did:example:c") }, None);

        outcomes.Should().Equal(RecipientStoreOutcome.NoGrant, RecipientStoreOutcome.NoGrant, RecipientStoreOutcome.NoGrant);
        store.RecipientCount.Should().Be(0);
        (await store.FindGrantByRecipientDidAsync("did:example:a", None)).Should().BeNull();
    }

    [Fact]
    public async Task A_batch_is_applied_in_order_and_reported_in_order()
    {
        var store = await WithGrantsAsync(NewStore(), Bob, Mallory);
        await store.UpdateRecipientsAsync(Mallory, new[] { Add("did:example:taken") }, None);

        var outcomes = await store.UpdateRecipientsAsync(Bob, new[]
        {
            Add("did:example:x"),        // Added
            Add("did:example:x"),        // AlreadyRegistered (duplicate in the batch)
            Remove("did:example:x"),     // Removed
            Remove("did:example:x"),     // NotRegistered
            Add("did:example:x"),        // Added again
            Add("did:example:taken"),    // OwnedByAnotherGrant
            Remove("did:example:taken"), // NotRegistered
            Add("did:example:y"),        // Added
        }, None);

        outcomes.Should().Equal(
            RecipientStoreOutcome.Added,
            RecipientStoreOutcome.AlreadyRegistered,
            RecipientStoreOutcome.Removed,
            RecipientStoreOutcome.NotRegistered,
            RecipientStoreOutcome.Added,
            RecipientStoreOutcome.OwnedByAnotherGrant,
            RecipientStoreOutcome.NotRegistered,
            RecipientStoreOutcome.Added);
        (await AllAsync(store, Bob)).Should().Equal("did:example:x", "did:example:y");
        store.RecipientCount.Should().Be(3);
    }

    [Fact]
    public async Task An_empty_batch_returns_an_empty_list()
    {
        var store = await WithGrantsAsync(NewStore(), Bob);
        (await store.UpdateRecipientsAsync(Bob, Array.Empty<RecipientUpdate>(), None)).Should().BeEmpty();
        (await store.UpdateRecipientsAsync("did:example:nobody", Array.Empty<RecipientUpdate>(), None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Dids_are_compared_ordinally()
    {
        var store = await WithGrantsAsync(NewStore(), Bob, Mallory);
        await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:Alice") }, None);

        (await store.UpdateRecipientsAsync(Mallory, new[] { Add("did:example:alice") }, None)).Should().Equal(RecipientStoreOutcome.Added); // DIDs are case-sensitive
        (await store.FindGrantByRecipientDidAsync("did:example:ALICE", None)).Should().BeNull();
    }

    // ---- bounds (D7, FR-MED-08) -----------------------------------------------------------

    [Fact]
    public async Task The_per_grant_bound_refuses_further_adds_until_one_is_removed()
    {
        var store = await WithGrantsAsync(NewStore(perGrant: 2), Bob);
        (await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:1"), Add("did:example:2"), Add("did:example:3"), Add("did:example:1") }, None))
            .Should().Equal(RecipientStoreOutcome.Added, RecipientStoreOutcome.Added, RecipientStoreOutcome.GrantLimitReached, RecipientStoreOutcome.AlreadyRegistered);

        (await store.UpdateRecipientsAsync(Bob, new[] { Remove("did:example:1"), Add("did:example:3") }, None))
            .Should().Equal(RecipientStoreOutcome.Removed, RecipientStoreOutcome.Added);
        (await AllAsync(store, Bob)).Should().Equal("did:example:2", "did:example:3");
    }

    [Fact]
    public async Task The_total_bound_refuses_adds_from_every_grant_until_space_is_freed()
    {
        var store = await WithGrantsAsync(NewStore(perGrant: 10, total: 3), Bob, Mallory);
        await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:b1"), Add("did:example:b2") }, None);

        (await store.UpdateRecipientsAsync(Mallory, new[] { Add("did:example:m1"), Add("did:example:m2") }, None))
            .Should().Equal(RecipientStoreOutcome.Added, RecipientStoreOutcome.StoreLimitReached);
        (await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:b3"), Add("did:example:b1") }, None))
            .Should().Equal(RecipientStoreOutcome.StoreLimitReached, RecipientStoreOutcome.AlreadyRegistered); // a full store still answers for what it holds
        store.RecipientCount.Should().Be(3);

        await store.RevokeAsync(Bob, None);
        store.RecipientCount.Should().Be(1);
        (await store.UpdateRecipientsAsync(Mallory, new[] { Add("did:example:m2"), Add("did:example:b1") }, None))
            .Should().Equal(RecipientStoreOutcome.Added, RecipientStoreOutcome.Added);
    }

    [Fact]
    public async Task Ten_thousand_adds_stop_at_the_default_per_grant_bound()
    {
        // #93 acceptance: 10k recipient-update adds hit MaxRecipientsPerGrant, memory bounded.
        var store = await WithGrantsAsync(new InMemoryMediationStore(), Bob);
        var updates = Enumerable.Range(0, 10_000).Select(i => Add($"did:example:r{i}")).ToArray();

        var outcomes = await store.UpdateRecipientsAsync(Bob, updates, None);

        var bound = new CoordinateMediationOptions().MaxRecipientsPerGrant;
        outcomes.Take(bound).Should().OnlyContain(o => o == RecipientStoreOutcome.Added);
        outcomes.Skip(bound).Should().OnlyContain(o => o == RecipientStoreOutcome.GrantLimitReached);
        store.RecipientCount.Should().Be(bound);
        (await store.ListRecipientsAsync(Bob, 0, int.MaxValue, None)).Total.Should().Be(bound);
    }

    [Fact]
    public void Default_bounds_are_the_documented_ones_and_bad_bounds_are_refused()
    {
        var defaults = new CoordinateMediationOptions();
        defaults.MaxGrants.Should().Be(1_000);
        defaults.MaxRecipientsPerGrant.Should().Be(256);
        defaults.MaxRecipients.Should().Be(10_000);
        defaults.MaxPageSize.Should().Be(100);
        defaults.MaxUpdatesPerMessage.Should().Be(1024);
        defaults.UseAsForwardRecipientRegistry.Should().BeTrue();
        defaults.RoutingDids.Should().BeEmpty();

        var zero = () => new InMemoryMediationStore(new CoordinateMediationOptions { MaxGrants = 0 });
        zero.Should().Throw<ArgumentException>();
        var nullOptions = () => new InMemoryMediationStore(null!);
        nullOptions.Should().Throw<ArgumentNullException>();
    }

    // ---- paging (FR-MED-06) ---------------------------------------------------------------

    [Fact]
    public async Task Pages_are_in_registration_order_and_report_the_total()
    {
        var store = await WithGrantsAsync(NewStore(), Bob);
        await store.UpdateRecipientsAsync(Bob, Enumerable.Range(0, 5).Select(i => Add($"did:example:{i}")).ToArray(), None);

        (await store.ListRecipientsAsync(Bob, 0, 2, None)).Should().BeEquivalentTo(new RecipientPage(new[] { "did:example:0", "did:example:1" }, 5), o => o.WithStrictOrdering());
        (await store.ListRecipientsAsync(Bob, 2, 2, None)).Dids.Should().Equal("did:example:2", "did:example:3");
        (await store.ListRecipientsAsync(Bob, 4, 2, None)).Dids.Should().Equal("did:example:4");
        (await store.ListRecipientsAsync(Bob, 0, 100, None)).Dids.Should().HaveCount(5);
        (await store.ListRecipientsAsync(Bob, 0, int.MaxValue, None)).Dids.Should().HaveCount(5);

        foreach (var (offset, limit) in new[] { (5, 2), (6, 2), (int.MaxValue, 2), (0, 0), (3, 0), (int.MaxValue, int.MaxValue) })
        {
            var page = await store.ListRecipientsAsync(Bob, offset, limit, None);
            page.Dids.Should().BeEmpty($"offset {offset}, limit {limit}");
            page.Total.Should().Be(5);
        }
    }

    [Fact]
    public async Task Removal_keeps_the_order_of_the_rest_and_a_re_added_did_goes_to_the_end()
    {
        var store = await WithGrantsAsync(NewStore(), Bob);
        await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:a"), Add("did:example:b"), Add("did:example:c") }, None);

        await store.UpdateRecipientsAsync(Bob, new[] { Remove("did:example:a"), Add("did:example:a") }, None);

        (await AllAsync(store, Bob)).Should().Equal("did:example:b", "did:example:c", "did:example:a");
    }

    [Fact]
    public async Task A_peer_without_a_grant_lists_nothing()
    {
        var store = await WithGrantsAsync(NewStore(), Bob);
        await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:a") }, None);

        var page = await store.ListRecipientsAsync(Mallory, 0, 10, None);
        page.Dids.Should().BeEmpty();
        page.Total.Should().Be(0);
    }

    [Fact]
    public async Task A_returned_page_is_a_snapshot()
    {
        var store = await WithGrantsAsync(NewStore(), Bob);
        await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:a") }, None);
        var page = await store.ListRecipientsAsync(Bob, 0, 10, None);

        await store.UpdateRecipientsAsync(Bob, new[] { Remove("did:example:a"), Add("did:example:b") }, None);

        page.Dids.Should().Equal("did:example:a");
    }

    // ---- revoke ---------------------------------------------------------------------------

    [Fact]
    public async Task Revoke_ends_the_grant_and_frees_every_did_under_it()
    {
        var store = await WithGrantsAsync(NewStore(), Bob, Mallory);
        await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:a"), Add("did:example:b") }, None);
        await store.UpdateRecipientsAsync(Mallory, new[] { Add("did:example:m") }, None);

        (await store.RevokeAsync("did:example:nobody", None)).Should().BeFalse();
        (await store.RevokeAsync(Bob, None)).Should().BeTrue();
        (await store.RevokeAsync(Bob, None)).Should().BeFalse("it is already gone");

        (await store.GetGrantAsync(Bob, None)).Should().BeNull();
        (await store.FindGrantByRecipientDidAsync("did:example:a", None)).Should().BeNull();
        (await store.FindGrantByRecipientDidAsync("did:example:b", None)).Should().BeNull();
        (await store.FindGrantByRecipientDidAsync("did:example:m", None))!.PeerDid.Should().Be(Mallory, "another grant is untouched");
        store.GrantCount.Should().Be(1);
        store.RecipientCount.Should().Be(1);
        (await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:a") }, None)).Should().Equal(RecipientStoreOutcome.NoGrant);
        (await store.UpdateRecipientsAsync(Mallory, new[] { Add("did:example:a") }, None)).Should().Equal(RecipientStoreOutcome.Added);

        // Enrolling again starts from nothing.
        await WithGrantsAsync(store, Bob);
        (await AllAsync(store, Bob)).Should().BeEmpty();
    }

    // ---- arguments and cancellation -------------------------------------------------------

    [Fact]
    public async Task Null_arguments_are_refused_before_anything_changes()
    {
        var store = await WithGrantsAsync(NewStore(), Bob);

        await FluentActions.Awaiting(async () => await store.GetGrantAsync(null!, None)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await store.GrantAsync(null!, None)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await store.GrantAsync(new MediationGrant(null!, Routing), None)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await store.GrantAsync(new MediationGrant("did:example:x", null!), None)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await store.RevokeAsync(null!, None)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await store.UpdateRecipientsAsync(null!, Array.Empty<RecipientUpdate>(), None)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await store.UpdateRecipientsAsync(Bob, null!, None)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await store.ListRecipientsAsync(null!, 0, 1, None)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await store.ListRecipientsAsync(Bob, -1, 1, None)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(async () => await store.ListRecipientsAsync(Bob, 0, -1, None)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(async () => await store.FindGrantByRecipientDidAsync(null!, None)).Should().ThrowAsync<ArgumentNullException>();

        // A bad entry anywhere in a batch fails the whole call before the first entry is applied
        // (the contract: no throw after a partial apply).
        await FluentActions.Awaiting(async () => await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:first"), null! }, None)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:first"), Add(null!) }, None)).Should().ThrowAsync<ArgumentNullException>();
        (await AllAsync(store, Bob)).Should().BeEmpty("nothing of a refused batch was applied");
        store.GrantCount.Should().Be(1);
    }

    [Fact]
    public async Task A_cancelled_token_stops_every_mutating_and_listing_call()
    {
        var store = await WithGrantsAsync(NewStore(), Bob);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await FluentActions.Awaiting(async () => await store.GetGrantAsync(Bob, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await store.GrantAsync(new MediationGrant("did:example:new", Routing), cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await store.RevokeAsync(Bob, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:a") }, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await store.ListRecipientsAsync(Bob, 0, 1, cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        store.GrantCount.Should().Be(1);
        store.RecipientCount.Should().Be(0);
    }

    // ---- concurrency ----------------------------------------------------------------------

    [Fact]
    public async Task Peers_racing_for_one_did_get_exactly_one_Added()
    {
        const int peers = 8;
        const int rounds = 200;
        var store = NewStore(maxGrants: peers, perGrant: rounds, total: rounds * peers);
        var names = Enumerable.Range(0, peers).Select(i => $"did:example:peer{i}").ToArray();
        await WithGrantsAsync(store, names);

        for (var round = 0; round < rounds; round++)
        {
            var did = $"did:example:contested{round}";
            using var start = new ManualResetEventSlim();
            var racers = names.Select(peer => Task.Run(async () =>
            {
                start.Wait();
                return (await store.UpdateRecipientsAsync(peer, new[] { Add(did) }, None))[0];
            })).ToArray();
            start.Set();
            var outcomes = await Task.WhenAll(racers);

            // Which racer wins is not asserted (L-044): only that exactly one does.
            outcomes.Count(o => o == RecipientStoreOutcome.Added).Should().Be(1);
            outcomes.Count(o => o == RecipientStoreOutcome.OwnedByAnotherGrant).Should().Be(peers - 1);
            var owner = await store.FindGrantByRecipientDidAsync(did, None);
            (await AllAsync(store, owner!.PeerDid)).Should().Contain(did);
        }

        store.RecipientCount.Should().Be(rounds);
    }

    [Fact]
    public async Task Concurrent_adds_removes_revokes_and_lookups_keep_the_indexes_consistent()
    {
        const int peers = 6;
        const int perPeer = 400;
        var store = NewStore(maxGrants: peers, perGrant: 50, total: 120);
        var names = Enumerable.Range(0, peers).Select(i => $"did:example:peer{i}").ToArray();
        var pool = Enumerable.Range(0, 40).Select(i => $"did:example:shared{i}").ToArray();

        var workers = names.Select((peer, index) => Task.Run(async () =>
        {
            var random = new Random(index * 7919 + 1);
            for (var i = 0; i < perPeer; i++)
            {
                var did = pool[random.Next(pool.Length)];
                switch (random.Next(10))
                {
                    case 0:
                        await store.RevokeAsync(peer, None);
                        break;
                    case 1:
                        await store.GrantAsync(new MediationGrant(peer, Routing), None);
                        break;
                    case 2 or 3:
                        await store.UpdateRecipientsAsync(peer, new[] { Remove(did) }, None);
                        break;
                    case 4:
                        var page = await store.ListRecipientsAsync(peer, random.Next(5), random.Next(10), None);
                        page.Dids.Should().OnlyHaveUniqueItems();
                        break;
                    case 5:
                        var found = await store.FindGrantByRecipientDidAsync(did, None);
                        (found is null || names.Contains(found.PeerDid)).Should().BeTrue();
                        break;
                    default:
                        await store.UpdateRecipientsAsync(peer, new[] { Add(did), Add(pool[random.Next(pool.Length)]) }, None);
                        break;
                }
            }
        })).ToArray();
        await Task.WhenAll(workers);

        // Invariants, checked once everything is quiet.
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        var grants = 0;
        foreach (var peer in names)
        {
            if (await store.GetGrantAsync(peer, None) is null)
            {
                (await AllAsync(store, peer)).Should().BeEmpty();
                continue;
            }
            grants++;
            var mine = await AllAsync(store, peer);
            mine.Should().OnlyHaveUniqueItems();
            mine.Count.Should().BeLessThanOrEqualTo(50);
            foreach (var did in mine)
                listed.TryAdd(did, peer).Should().BeTrue($"a DID is listed under exactly one grant");
        }

        store.GrantCount.Should().Be(grants);
        store.RecipientCount.Should().Be(listed.Count).And.BeLessThanOrEqualTo(120);
        foreach (var did in pool)
        {
            var owner = await store.FindGrantByRecipientDidAsync(did, None);
            (owner?.PeerDid).Should().Be(listed.GetValueOrDefault(did), "the lookup index and the lists agree");
        }
    }

    // ---- forward lookup: constant work (FR-MED-08 / FR-ROUTE-11) --------------------------

    [Fact]
    public async Task Lookup_returns_the_grant_for_a_hit_and_null_for_a_miss_without_blocking_on_writers()
    {
        var store = await WithGrantsAsync(NewStore(), Bob);
        await store.UpdateRecipientsAsync(Bob, new[] { Add("did:example:a") }, None);

        var hit = store.FindGrantByRecipientDidAsync("did:example:a", None);
        var miss = store.FindGrantByRecipientDidAsync("did:example:z", None);

        // Structural half of "constant work": both answers are produced synchronously by the same
        // single probe — no awaiting, and (unlike every other method) no cancellation check that
        // could make one path differ.
        hit.IsCompletedSuccessfully.Should().BeTrue();
        miss.IsCompletedSuccessfully.Should().BeTrue();
        (await hit)!.PeerDid.Should().Be(Bob);
        (await miss).Should().BeNull();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        (await store.FindGrantByRecipientDidAsync("did:example:a", cancelled.Token)).Should().NotBeNull("the lookup is a pure read");
    }

    [Fact]
    public async Task Lookup_time_does_not_tell_a_registered_did_from_an_unregistered_one()
    {
        // Statistical half (#93 acceptance). Same-length keys, a store filled to a realistic size,
        // interleaved batches so drift hits both sides alike, medians of many batches, and a
        // generous bound: a registration oracle would be a second lookup or an early return —
        // a factor, not a few percent. The bound must hold on a loaded 2-core CI runner.
        const int registeredCount = 2_000;
        const int batch = 2_000;
        const int batches = 201;
        var store = new InMemoryMediationStore(new CoordinateMediationOptions { MaxGrants = 100, MaxRecipientsPerGrant = registeredCount, MaxRecipients = registeredCount * 2 });
        await WithGrantsAsync(store, Bob);
        var registered = Enumerable.Range(0, registeredCount).Select(i => $"did:example:registered-{i:D8}").ToArray();
        var unregistered = Enumerable.Range(0, registeredCount).Select(i => $"did:example:unregistrd-{i:D8}").ToArray();
        registered[0].Length.Should().Be(unregistered[0].Length);
        (await store.UpdateRecipientsAsync(Bob, registered.Select(Add).ToArray(), None)).Should().OnlyContain(o => o == RecipientStoreOutcome.Added);

        long Measure(string[] keys)
        {
            var hits = 0;
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < batch; i++)
            {
                if (store.FindGrantByRecipientDidAsync(keys[i % keys.Length], None).Result is not null)
                    hits++;
            }
            var elapsed = Stopwatch.GetTimestamp() - started;
            GC.KeepAlive(hits);
            return elapsed;
        }

        for (var i = 0; i < 20; i++) // warm-up: JIT and caches
        {
            Measure(registered);
            Measure(unregistered);
        }

        var hitTimes = new long[batches];
        var missTimes = new long[batches];
        for (var i = 0; i < batches; i++)
        {
            if (i % 2 == 0)
            {
                hitTimes[i] = Measure(registered);
                missTimes[i] = Measure(unregistered);
            }
            else
            {
                missTimes[i] = Measure(unregistered);
                hitTimes[i] = Measure(registered);
            }
        }

        Array.Sort(hitTimes);
        Array.Sort(missTimes);
        var hitMedian = hitTimes[batches / 2];
        var missMedian = missTimes[batches / 2];
        var ratio = (double)Math.Max(hitMedian, missMedian) / Math.Max(1, Math.Min(hitMedian, missMedian));
        output.WriteLine($"median ticks per {batch} lookups: registered {hitMedian}, unregistered {missMedian}, ratio {ratio:F2}");

        ratio.Should().BeLessThan(3.0, "a hit and a miss are one probe each; neither may cost a multiple of the other");
    }
}
