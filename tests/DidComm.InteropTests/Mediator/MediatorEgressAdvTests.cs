using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// ADV-2 (#95): the mediator's single forward pump and what a sender can make its queue hold, attacked
/// over the real endpoints (TestServer, the real HTTP transport, the real did:peer resolver). The
/// egress cases of ADV-2 (one request per push, a failing endpoint and the circuit breaker, redirects,
/// a throwing logging provider) went through the push to a recipient's own endpoint, which left #95
/// (#110); the transport
/// defects they found that are also on <c>main</c> keep their tests in
/// <c>Transports/HttpTransportAcceptedLogTests</c> and <c>Transports/HttpTransportPolicyInheritanceTests</c>.
/// </summary>
public sealed class MediatorEgressAdvTests
{
    private static readonly TimeSpan Wait = MediatorTestServer.Timeout;

    // ---- the single forward pump ------------------------------------------------------------

    // ADV-2 DEFECT 1 (P2, fixed by #95 AD-1 and ADV-3 D2): a stranger's forward addressed to the
    // mediator itself used to be opened on the single forward pump, after the 202, so an
    // attacker-chosen DID resolution held up every other relay. Layers addressed to the mediator are
    // now opened inline, on the sender's own request, and must be an anoncrypt envelope around a
    // plaintext message: the stranger's signed note is refused before its signer is resolved.
    [Fact]
    public async Task A_strangers_forward_addressed_to_the_mediator_cannot_hold_up_other_senders_relays()
    {
        var resolver = new GatedDidResolverHandle();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = s => GatedDidResolverHandle.Install(s, resolver));
        Task<(HttpStatusCode Status, string? ContentType, string Body)>? strangersPost = null;
        try
        {
            var bob = await mediator.NewPeerAsync();
            await mediator.EnrolAsync(bob);
            var alice = await mediator.NewSenderAsync();
            var stranger = await mediator.NewSenderAsync(); // never enrolled, never registered

            // The stranger's DID stands for one whose resolution is slow (a did:web / did:webvh host
            // that answers after the resolver's own timeout, or never): its resolution parks here.
            resolver.Gate(stranger.Did);
            var signed = await stranger.Client.PackSignedAsync(stranger.Note(mediator.Did), stranger.Did);
            var (selfAddressed, _) = await stranger.ForwardAsync(mediator.Did, next: mediator.Did, signed);
            strangersPost = Task.Run(() => mediator.PostAsync(selfAddressed));
            (await strangersPost.WaitAsync(Wait)).Status.Should().Be(HttpStatusCode.Accepted, "answered at once: nothing in it is resolved");

            // An ordinary relay from someone else, for an enrolled recipient.
            var note = alice.Note(bob.Did);
            var (forward, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));
            (await mediator.PostAsync(forward)).Status.Should().Be(HttpStatusCode.Accepted);

            // Property: Bob's message is queued whatever the stranger's forward asked for.
            await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 1, "a stranger's forward must not hold every other relay behind a DID resolution it chose");
            resolver.Entered.IsCompleted.Should().BeFalse("the stranger's DID is never resolved: a layer addressed to the mediator must be an anoncrypt of a plaintext");
        }
        finally
        {
            resolver.Release();
        }

        await mediator.FlushAsync();
        mediator.Store.MessageCount.Should().Be(1, "the stranger's forward stored nothing");
        mediator.Outcomes.Should().Contain(ForwardOutcomes.DroppedMalformed);
    }

    [Fact]
    public async Task A_strangers_self_addressed_forward_never_stores_anything_and_ends_with_one_outcome()
    {
        // What does hold: with a resolver that answers, the signed message inside is opened, found
        // not to be a forward, and dropped; one outcome, nothing stored, nothing pushed.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var stranger = await mediator.NewSenderAsync();

        var signed = await stranger.Client.PackSignedAsync(stranger.Note(mediator.Did), stranger.Did);
        var (selfAddressed, _) = await stranger.ForwardAsync(mediator.Did, next: mediator.Did, signed);
        (await mediator.PostAsync(selfAddressed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedMalformed);
        mediator.Store.MessageCount.Should().Be(0);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    // ---- what a sender can make the queue hold -------------------------------------------------

    [Fact]
    public async Task A_sender_can_hide_a_recipients_full_quota_for_at_most_the_delay_cap()
    {
        // FR-PICK-10 / FR-ROUTE-10 (owner-accepted residual, bounded): delayed messages count against
        // the quota unseen, for at most MaxForwardDelay; a hint past the cap, or the most negative
        // one, never holds longer.
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxQueueMessages = 3);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var stranger = await mediator.NewSenderAsync();
        var alice = await mediator.NewSenderAsync();

        foreach (var hint in new[] { long.MaxValue, 3_600_001L, long.MinValue })
        {
            var (packed, _) = await stranger.ForwardAsync(mediator.Did, bob.Did, await stranger.AuthcryptAsync(stranger.Note(bob.Did), bob.Did),
                forward => (forward.AdditionalHeaders ??= new Dictionary<string, System.Text.Json.JsonElement>())["delay_milli"] = System.Text.Json.JsonSerializer.SerializeToElement(hint));
            (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        }
        await mediator.FlushAsync();
        (await mediator.StatusAsync(bob)).MessageCount.Should().BeInRange(0, 1, "only the random draw of the negative hint can already be due");
        mediator.Store.MessageCount.Should().Be(3, "the quota is full");

        await alice.SendViaMediatorAsync(mediator, bob.Did);
        await mediator.FlushAsync();
        mediator.Outcome(ForwardOutcomes.DroppedQuota).Should().Be(1, "while the quota is held, Bob's other mail is refused");

        mediator.Clock.Advance(TimeSpan.FromHours(1));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(3, "at the cap every held message is due and visible");
        (await mediator.FetchAsync(bob)).Should().HaveCount(3);
    }

    [Fact]
    public async Task A_replayed_captured_forward_is_bounded_by_the_recipients_quota_when_nothing_is_pushed()
    {
        // FR-RR-08 exempts forward from replay admission (documented, owner-accepted): the number of
        // copies a captured forward can park is the grant's quota, no more.
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxQueueMessages = 5);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (captured, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));

        for (var i = 0; i < 8; i++)
            (await mediator.PostAsync(captured)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();

        mediator.Store.MessageCount.Should().Be(5);
        mediator.Outcome(ForwardOutcomes.Queued).Should().Be(5);
        mediator.Outcome(ForwardOutcomes.DroppedQuota).Should().Be(3);
    }
}

internal static class AdvSenderExtensions
{
    /// <summary>A note from <paramref name="sender"/> to <paramref name="recipientDid"/>, authcrypted, wrapped in a forward to the mediator and posted.</summary>
    public static async Task SendViaMediatorAsync(this MediatorSender sender, MediatorTestServer mediator, string recipientDid)
    {
        var (forward, _) = await sender.ForwardAsync(mediator.Did, recipientDid, await sender.AuthcryptAsync(sender.Note(recipientDid), recipientDid));
        (await mediator.PostAsync(forward)).Status.Should().Be(HttpStatusCode.Accepted);
    }
}

/// <summary>
/// Wraps the container's net-did <see cref="IDidResolver"/>: the resolution of one chosen DID parks
/// until released (a networked DID method whose host is slow); everything else passes through.
/// </summary>
internal sealed class GatedDidResolverHandle
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? _gated;
    private int _calls;

    public Task Entered => _entered.Task;

    /// <summary>How many resolutions of the gated DID started.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The token the first gated resolution was handed.</summary>
    public CancellationToken FirstToken { get; private set; }

    public void Gate(string did) => _gated = did;

    public void Release() => _release.TrySetResult();

    /// <summary>Replace the registered <see cref="IDidResolver"/> with a gated wrapper around it.</summary>
    public static void Install(IServiceCollection services, GatedDidResolverHandle handle)
    {
        var original = services.Last(d => d.ServiceType == typeof(IDidResolver) && !d.IsKeyedService);
        services.Remove(original);
        services.AddSingleton<IDidResolver>(sp => new Gated(Build(original, sp), handle));
    }

    private static IDidResolver Build(ServiceDescriptor descriptor, IServiceProvider sp)
        => descriptor.ImplementationInstance as IDidResolver
           ?? (descriptor.ImplementationFactory is { } factory
               ? (IDidResolver)factory(sp)
               : (IDidResolver)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));

    private sealed class Gated(IDidResolver inner, GatedDidResolverHandle handle) : IDidResolver
    {
        public bool CanResolve(string did) => inner.CanResolve(did);

        public async Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            if (string.Equals(did, handle._gated, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref handle._calls) == 1)
                    handle.FirstToken = ct;
                handle._entered.TrySetResult();
                await handle._release.Task.ConfigureAwait(false);
            }
            return await inner.ResolveAsync(did, options, ct).ConfigureAwait(false);
        }
    }
}
