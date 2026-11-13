using System.Collections.Concurrent;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// A point a test parks a collaborator at: the collaborator signals <see cref="Arrived"/> and then
/// waits until the test calls <see cref="Resume"/>. Every interleaving of the ADV-2 slice is driven
/// through these, never through a sleep (L-044).
/// </summary>
internal sealed class MessagePickupAdversarialResourceGate
{
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the collaborator reached the gate.</summary>
    public Task Arrived => _arrived.Task.WaitAsync(MessagePickupAdversarialResource.Deadline);

    /// <summary>Let the parked collaborator go on.</summary>
    public void Resume() => _resume.TrySetResult();

    /// <summary>Called by the collaborator: announce the arrival and wait for the test.</summary>
    public async Task PassAsync()
    {
        _arrived.TrySetResult();
        await _resume.Task.WaitAsync(MessagePickupAdversarialResource.Deadline);
    }

    /// <summary>The synchronous form, for a collaborator that cannot await (a session's send hook).</summary>
    public void Pass()
    {
        _arrived.TrySetResult();
        if (!_resume.Task.Wait(MessagePickupAdversarialResource.Deadline))
            throw new TimeoutException("The test never resumed a parked collaborator.");
    }
}

/// <summary>
/// An <see cref="IMediationStore"/> over the real <see cref="InMemoryMediationStore"/> whose
/// recipient lookup a test can park after it has computed its answer (the answer is then stale by
/// the time the caller acts on it) or replace altogether.
/// </summary>
internal sealed class MessagePickupAdversarialResourceMediation : IMediationStore
{
    private readonly ConcurrentDictionary<string, MessagePickupAdversarialResourceGate> _parkAfterFind = new(StringComparer.Ordinal);

    public MessagePickupAdversarialResourceMediation(CoordinateMediationOptions? options = null)
        => Inner = new InMemoryMediationStore(options ?? new CoordinateMediationOptions());

    public InMemoryMediationStore Inner { get; }

    /// <summary>Every recipient DID looked up, in order.</summary>
    public ConcurrentQueue<string> Finds { get; } = new();

    /// <summary>When set, replaces the recipient lookup.</summary>
    public Func<string, CancellationToken, ValueTask<MediationGrant?>>? OnFind { get; set; }

    /// <summary>When set, replaces the grant lookup.</summary>
    public Func<string, CancellationToken, ValueTask<MediationGrant?>>? OnGetGrant { get; set; }

    /// <summary>
    /// The NEXT lookup of <paramref name="recipientDid"/> reads the real store, then parks with its
    /// answer in hand until the gate is resumed. Later lookups of the same DID are not parked.
    /// </summary>
    public MessagePickupAdversarialResourceGate ParkNextFindAfterItsAnswer(string recipientDid)
    {
        var gate = new MessagePickupAdversarialResourceGate();
        _parkAfterFind[recipientDid] = gate;
        return gate;
    }

    public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct)
        => OnGetGrant is { } hook ? hook(peerDid, ct) : Inner.GetGrantAsync(peerDid, ct);

    public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct) => Inner.GrantAsync(grant, ct);

    public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct) => Inner.RevokeAsync(peerDid, ct);

    public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct)
        => Inner.UpdateRecipientsAsync(peerDid, updates, ct);

    public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct)
        => Inner.ListRecipientsAsync(peerDid, offset, limit, ct);

    public async ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct)
    {
        Finds.Enqueue(recipientDid);
        if (OnFind is { } hook)
            return await hook(recipientDid, ct);
        var answer = await Inner.FindGrantByRecipientDidAsync(recipientDid, ct);
        if (_parkAfterFind.TryRemove(recipientDid, out var gate))
            await gate.PassAsync();
        return answer;
    }
}

/// <summary>Shared pieces of the ADV-2 slice (resources, bounds, leases, races, host-seam faults).</summary>
internal static class MessagePickupAdversarialResource
{
    /// <summary>How long a rendezvous or a poll waits. Far longer than anything takes; nothing waits this long unless it is broken.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Tests that park a call on a rendezvous keep a thread-pool thread per parked call; CI runners
    /// have two cores, so the floor is raised once (never lowered).
    /// </summary>
    public static void RaiseThreadPoolFloor(int needed = 32)
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        if (worker < needed)
            ThreadPool.SetMinThreads(needed, io);
    }

    /// <summary>Poll <paramref name="condition"/> until it holds; fail at the deadline. Only for effects the product produces on the thread pool with nothing to rendezvous with.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string because)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed > Deadline)
                throw new TimeoutException("Not within the deadline: " + because);
            await Task.Delay(2);
        }
    }

    /// <summary>The ids a <c>delivery-request</c> over a new request-scoped session is handed right now (empty when it is answered <c>status</c> or not at all).</summary>
    public static async Task<IReadOnlyList<string>> FetchIdsAsync(PickupFixture fixture, string peer, string mediator, int limit = 10, string? recipientDid = null)
    {
        var fetched = await fixture.FetchAsync(peer, mediator, fixture.Session(peer, mediator), limit, recipientDid);
        return fetched is null ? Array.Empty<string>() : fetched.Value.Messages.Select(m => m.Id).ToArray();
    }

    /// <summary>A <c>delivery-request</c> with the given limit.</summary>
    public static Message DeliveryRequest(string peer, string mediator, int limit = 10, string? recipientDid = null)
        => MessagePickup.CreateDeliveryRequest(peer, mediator, limit, recipientDid);

    /// <summary>The peer's <c>status</c> over a new request-scoped session.</summary>
    public static Task<PickupStatus> StatusAsync(PickupFixture fixture, string peer, string mediator, string? recipientDid = null)
        => fixture.StatusOfAsync(MessagePickup.CreateStatusRequest(peer, mediator, recipientDid), mediator, fixture.Session(peer, mediator));
}
