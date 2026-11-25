using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.InteropTests.AspNetCore;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #111 acceptance, end to end against this library's mediator on a TestServer: a loop that fetches
/// until drained reaches an honest message queued behind messages that never open, within the
/// bound the client's memory of slow messages gives (one budget per learning fetch, each learning up
/// to <c>min(limit, 16)</c> of them, and fetches that take no time over the ones it remembers). The
/// client's clock is a manual one, moved one timer at a time by <see cref="SlowFetchLoop"/>; the
/// mediator's clock moves by the client's elapsed time only. The unpack step is the real one, with
/// the waits of <see cref="SlowSenderSeam"/> around it.
/// </summary>
public sealed class PickupClientSlowSenderAcceptanceTests(ITestOutputHelper output)
{
    private static readonly TimeSpan DefaultLease = new MessagePickupOptions().LeaseTimeout;

    /// <summary>The A2 matrix: limit × messages that never open in front × the mediator's lease.</summary>
    public static TheoryData<int, int, int> Matrix()
    {
        var data = new TheoryData<int, int, int>();
        foreach (var limit in new[] { 4, 10 })
        {
            foreach (var slow in new[] { 1, 16, 31, 99 })
            {
                foreach (var lease in new[] { (int)DefaultLease.TotalSeconds, 10 })
                    data.Add(limit, slow, lease);
            }
        }
        return data;
    }

    /// <summary>
    /// A2: <paramref name="slow"/> messages whose sender's DID never resolves (each anoncrypted around
    /// its authcrypt, so they are counted in the mailbox's anonymous partition of 16, which no
    /// delivery here fills), then Alice's message. A loop of <c>FetchAsync(limit)</c> reaches Alice's
    /// message within <c>ceil(slow / min(limit, 16))</c> fetches that take time, each at most one
    /// budget; and every message that never opens is opened exactly once.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task A_fetch_until_drained_loop_reaches_the_message_behind_ones_that_never_open_within_the_bound(int limit, int slow, int leaseSeconds)
    {
        var lease = TimeSpan.FromSeconds(leaseSeconds);
        var (mediator, store) = await StartMediatorAsync(lease);
        await using var _ = mediator;
        var time = new QaManualTimeProvider();
        var seam = new SlowSenderSeam(time);
        await using var harness = await SlowFetchLoop.HarnessAsync(mediator, time, seam);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var mallory = await harness.NewAliceAsync();
        var alice = await harness.NewAliceAsync();
        for (var i = 0; i < slow; i++)
            await SendAnoncryptedAsync(mediator, mallory, recipient, "slow");
        var honest = await PickupE2E.SendAsync(mediator, alice, recipient, "honest");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(slow + 1, "everything fits the mediator's quota (MaxQueueMessages)");
        var budget = harness.Channel.Options.UnpackTimeout;
        var bound = Bound(slow, limit);
        var loop = new SlowFetchLoop(mediator, harness, time, store, seam, bob.Did);

        var reached = await loop.FetchUntilAsync(honest.Id, limit, bound);

        output.WriteLine($"limit {limit}, {slow} never open, lease {leaseSeconds} s: {loop.LearningFetches} learning fetches of {loop.Fetches}, {loop.Elapsed.TotalSeconds} s = {loop.Elapsed / lease:0.##} leases; bound {bound} fetches, {(budget * bound).TotalSeconds} s");
        reached.Should().BeTrue("Alice's message is reached within {0} learning fetches; after {1} ({2} fetches, {3}) it was not", bound, loop.LearningFetches, loop.Fetches, loop.Elapsed);
        loop.LearningFetches.Should().BeLessThanOrEqualTo(bound);
        loop.Elapsed.Should().BeLessThanOrEqualTo(budget * bound, "each learning fetch takes at most one budget, and the others take none");
        Math.Ceiling(loop.Elapsed / lease).Should().BeLessThanOrEqualTo(Math.Ceiling(budget * bound / lease), "in the mediator's lease periods");
        seam.Calls.Count(sender => sender is null).Should().Be(slow, "each message that never opens is opened once, then held back while its pause lasts");
        loop.Items.Where(i => i.Error is not null).Should().OnlyContain(i => i.Error is TimeoutException, "a message that never opens fails when its delivery's budget ends, or at once while it is held back");
    }

    /// <summary>
    /// A3: Mallory sends sixteen messages whose sender lookups never end and ignore their token, then
    /// Alice (another sender) sends hers. Mallory's partition holds four host calls: they are the
    /// only ones of hers that ever run, and her fifth and later messages fail without host code. In
    /// the fetch that starts her first four, those four keep the four workers until the budget ends
    /// (a message has no time limit of its own: #111, owner decision E), so the messages behind them
    /// in that fetch are out of time before their turn; in the fetches after it, each of her
    /// messages is refused at once as "unpack capacity exhausted" for the partition. Alice's message
    /// is reached within one of the mediator's leases. Once the lookups end, every reservation is
    /// given back.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(10)]
    public async Task One_sender_whose_lookups_ignore_their_token_is_held_to_its_partition_and_another_senders_message_is_reached_within_one_lease(int limit)
    {
        var (mediator, store) = await StartMediatorAsync(DefaultLease);
        await using var _ = mediator;
        var time = new QaManualTimeProvider();
        var seam = new SlowSenderSeam(time);
        await using var harness = await SlowFetchLoop.HarnessAsync(mediator, time, seam);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var mallory = await harness.NewAliceAsync();
        var alice = await harness.NewAliceAsync();
        seam.LookupNeverEnds(mallory.Did);
        const int fromMallory = 16;
        for (var i = 0; i < fromMallory; i++)
            await PickupE2E.SendAsync(mediator, mallory, recipient, "mallory");
        var honest = await PickupE2E.SendAsync(mediator, alice, recipient, "honest");
        var loop = new SlowFetchLoop(mediator, harness, time, store, seam, bob.Did);
        try
        {
            var reached = await loop.FetchUntilAsync(honest.Id, limit, learningBound: 1);

            output.WriteLine($"limit {limit}: {loop.LearningFetches} learning fetches of {loop.Fetches}, {loop.Elapsed.TotalSeconds} s");
            reached.Should().BeTrue();
            loop.Elapsed.Should().BeLessThan(DefaultLease, "Alice's message is reached within one lease");
            seam.Calls.Count(sender => sender == mallory.Did).Should().Be(UnpackReservations.MaxPerSender, "only the partition's four host calls of Mallory's ever run");
            var refused = loop.Items.Where(i => i.Error?.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal) == true).ToArray();
            var notStarted = loop.Items.Where(i => i.Error?.Message.Contains("before its turn came", StringComparison.Ordinal) == true).ToArray();
            notStarted.Should().HaveCount(limit - UnpackReservations.MaxPerSender, "in the first fetch her first four keep the four workers, and the rest of that fetch is out of time before its turn");
            refused.Should().HaveCount(fromMallory - limit, "her messages in the fetches after it are refused at once");
            refused.Should().OnlyContain(i => i.Error is TimeoutException && i.Error.Message.Contains("its sender in this mailbox", StringComparison.Ordinal), "for her partition, not for the container");
            harness.Channel.Unpacker.Reserved.Should().Be(UnpackReservations.MaxPerSender, "the four lookups still hold their reservations");
        }
        finally
        {
            seam.End();
        }
        SpinWait.SpinUntil(() => harness.Channel.Unpacker.Reserved == 0, MediatorTestServer.Timeout).Should().BeTrue("each reservation is given back when its lookup ends");
    }

    /// <summary>
    /// D-13: Sam is an honest sender whose DID takes <paramref name="lookupSeconds"/> to resolve.
    /// Sixteen messages of junk name Sam's key in their <c>skid</c> (Sam's own envelopes with their
    /// ciphertext changed: Sam's DID is resolved, then they fail to decrypt), then Sam's own message
    /// follows. Sam's message reaches the application within the bound of sixteen messages that
    /// never open, no fetch refuses any message for Sam's partition, and each piece of junk is opened
    /// once: at the short lease the junk comes back before the loop is past it, and is held back.
    /// </summary>
    [Theory]
    [InlineData(4, 2, 60)]
    [InlineData(4, 5, 60)]
    [InlineData(10, 2, 60)]
    [InlineData(10, 5, 60)]
    [InlineData(4, 2, 10)]
    [InlineData(4, 5, 10)]
    [InlineData(10, 2, 10)]
    [InlineData(10, 5, 10)]
    public async Task An_honest_slow_senders_message_behind_junk_naming_its_key_reaches_the_application_within_the_bound(int limit, int lookupSeconds, int leaseSeconds)
    {
        var (mediator, store) = await StartMediatorAsync(TimeSpan.FromSeconds(leaseSeconds));
        await using var _ = mediator;
        var time = new QaManualTimeProvider();
        var seam = new SlowSenderSeam(time);
        await using var harness = await SlowFetchLoop.HarnessAsync(mediator, time, seam);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var sam = await harness.NewAliceAsync();
        var mallory = await harness.NewAliceAsync();
        seam.LookupTakes(sam.Did, TimeSpan.FromSeconds(lookupSeconds));
        const int junk = 16;
        for (var i = 0; i < junk; i++)
            await PostJunkNamingAsync(mediator, mallory, sam, recipient);
        var real = await PickupE2E.SendAsync(mediator, sam, recipient, "from sam");
        var budget = harness.Channel.Options.UnpackTimeout;
        var bound = Bound(junk, limit);
        var loop = new SlowFetchLoop(mediator, harness, time, store, seam, bob.Did);

        // One more fetch than the junk needs may take time: the one that opens Sam's message, which
        // costs Sam's lookup.
        var reached = await loop.FetchUntilAsync(real.Id, limit, bound + 1);

        output.WriteLine($"limit {limit}, lookup {lookupSeconds} s, lease {leaseSeconds} s: {loop.LearningFetches} fetches that took time, of {loop.Fetches}, {loop.Elapsed.TotalSeconds} s; bound {bound} learning fetches, {(budget * bound).TotalSeconds} s");
        reached.Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(loop.Items.Single(i => i.Unpacked?.Message.Id == real.Id), sam, recipient, real);
        loop.LearningFetches.Should().BeLessThanOrEqualTo(bound + 1, "the junk is learned within {0} fetches, and the fetch that opens Sam's message costs its lookup", bound);
        loop.Elapsed.Should().BeLessThanOrEqualTo(budget * bound, "the junk is learned at least as fast as messages that never open");
        loop.Items.Should().NotContain(i => i.Error != null && i.Error.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal), "no message, Sam's own included, is refused for Sam's partition");
        seam.Calls.Count(sender => sender == sam.Did).Should().Be(junk + 1, "each piece of junk is opened once, then held back while its pause lasts");
    }

    // The learning fetches the bound allows: ceil(slow / min(limit, 16)).
    private static int Bound(int slow, int limit)
    {
        var perFetch = Math.Min(limit, 16);
        return (slow + perFetch - 1) / perFetch;
    }

    private static async Task<(MediatorTestServer Mediator, LeaseRecordingStore Store)> StartMediatorAsync(TimeSpan lease)
    {
        LeaseRecordingStore? store = null;
        var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m => m.Pickup.LeaseTimeout = lease;
            o.ConfigureServices = services => LeaseRecordingStore.Register(services, s => store = s);
        });
        // The store is built when the mediator first resolves it; ask for it now.
        _ = mediator.App.Services.GetRequiredService<IMessageStore>();
        return (mediator, store!);
    }

    // A note anoncrypted around its authcrypt (SendOptions.ProtectSender): the outer envelope names
    // no sender, so the client counts it in the mailbox's anonymous partition.
    private static async Task SendAnoncryptedAsync(MediatorTestServer mediator, MediatorSender sender, TestPeer recipient, string content)
    {
        var note = sender.Note(recipient.Did, content);
        (await sender.Client.SendAsync(note, new SendOptions(new[] { recipient.Did }, sender.Did, ProtectSender: true))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();
    }

    // Sam's own authcrypted note to the recipient with its ciphertext changed, forwarded by Mallory:
    // the outer envelope names Sam's key in skid, Sam's DID is resolved to open it, and it fails to
    // decrypt.
    private static async Task PostJunkNamingAsync(MediatorTestServer mediator, MediatorSender mallory, MediatorSender sam, TestPeer recipient)
    {
        var envelope = JsonNode.Parse(await sam.AuthcryptAsync(sam.Note(recipient.Did, "junk"), recipient.Did))!.AsObject();
        var ciphertext = envelope["ciphertext"]!.GetValue<string>();
        envelope["ciphertext"] = (ciphertext[0] == 'A' ? "B" : "A") + ciphertext[1..];
        var (packed, _) = await mallory.ForwardAsync(mediator.Did, recipient.Did, envelope.ToJsonString());
        (await mediator.PostAsync(packed)).Status.Should().Be(System.Net.HttpStatusCode.Accepted);
        await mediator.FlushAsync();
    }
}
