using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA-1 (#95): start-up checks a host identity can trip (a null list, a null entry, repeats), the
/// FR-MED-07 validator with each collaborator returning nothing, garbage, throwing or hanging, the
/// forward settings in effect, the purge round with a store whose cancellation is not the stop,
/// and a delayed message for a recipient in live mode.
/// </summary>
public sealed class HostedServiceQaTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // ── the host's identity ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_identity_that_returns_a_null_list_fails_start_up_as_having_no_routing_did()
    {
        await using var fixture = await HostedFixture.CreateAsync(routingDid: string.Empty, before: s => s.AddSingleton<IMediatorIdentity>(new ListIdentity(null)));

        var failure = await StartFailsAsync(fixture);

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("no routing DID");
    }

    [Fact]
    public async Task An_identity_with_a_null_entry_fails_start_up_by_index()
    {
        await using var fixture = await HostedFixture.CreateAsync(routingDid: string.Empty, before: s => s.AddSingleton<IMediatorIdentity>(new ListIdentity(new[] { "did:example:qa-ok", null! })));

        var failure = await StartFailsAsync(fixture);

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("index 1").And.NotContain("qa-ok");
    }

    [Fact]
    public async Task An_identity_that_repeats_its_routing_did_starts_and_issues_the_invitation_from_it()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        await using var fixture = await HostedFixture.CreateAsync(
            routingDid: string.Empty,
            before: s => s.AddSingleton<IMediatorIdentity>(new ListIdentity(new[] { mediator.Did, mediator.Did })),
            mintMediator: false,
            didComm: _ => { });
        // The fixture's container holds its own secrets: give it the mediator's.
        foreach (var jwk in mediator.Privates)
            fixture.Peers.Secrets.Add(jwk);

        await fixture.StartingAsync();

        fixture.Invitation.PlaintextJson.Should().Contain(mediator.Did);
        PipelineLogs.Leaking(fixture.Logs, mediator.Did).Should().BeEmpty();
    }

    // ── the validator, each collaborator misbehaving ──────────────────────────────────────

    [Fact]
    public async Task A_resolver_that_returns_null_is_no_service()
    {
        var refusal = await RefusedAsync(resolve: (_, _) => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(null!));
        refusal.Message.Should().Contain("no DIDCommMessaging service");
    }

    [Fact]
    public async Task A_null_first_entry_is_refused()
    {
        var refusal = await RefusedAsync(resolve: (_, _) => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(new DidCommServiceInfo[] { null!, Entry("https://qa-host.example/d") }));
        refusal.Message.Should().Contain("has no uri");
    }

    [Theory]
    [InlineData("DID:example:qa-other")]
    [InlineData("Did:example:qa-other")]
    [InlineData(" did:example:qa-other")]
    public async Task A_DID_uri_in_another_spelling_is_still_refused(string uri)
    {
        await RefusedAsync(resolve: (_, _) => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(new[] { Entry(uri) }));
    }

    [Theory]
    [InlineData("https://qa-user:qa-secret-pw@qa-host.example/d")]
    [InlineData("http://[::1]:8080/d")]
    [InlineData("WS://QA-HOST.example/d")]
    public async Task A_usable_transport_uri_in_any_shape_passes_and_is_never_logged(string uri)
    {
        var (validator, logs, _) = Validator(resolve: (_, _) => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(new[] { Entry(uri) }));

        await validator.Invoking(v => v.ValidateAsync(new[] { QaDid }, CancellationToken.None)).Should().NotThrowAsync();

        PipelineLogs.Leaking(logs, "qa-secret-pw", "qa-user", "[::1]", "QA-HOST", QaDid).Should().BeEmpty();
    }

    [Fact]
    public async Task Key_agreement_keys_without_a_usable_kid_are_no_keys()
    {
        var refusal = await RefusedAsync(keys: (_, _) => Task.FromResult<IReadOnlyList<Jwk>>(new[] { Key(null), Key(string.Empty), null! }));
        refusal.Message.Should().Contain("no keyAgreement key");
    }

    [Fact]
    public async Task A_key_service_that_returns_null_is_no_keys()
    {
        var refusal = await RefusedAsync(keys: (_, _) => Task.FromResult<IReadOnlyList<Jwk>>(null!));
        refusal.Message.Should().Contain("no keyAgreement key");
    }

    [Fact]
    public async Task A_key_service_that_throws_its_own_cancellation_is_unresolvable_by_type()
    {
        var refusal = await RefusedAsync(keys: (_, _) => throw new OperationCanceledException(HandlerBoomException.Marker));
        refusal.Message.Should().Contain("could not be resolved").And.Contain(nameof(OperationCanceledException)).And.NotContain(HandlerBoomException.Marker);
    }

    [Fact]
    public async Task A_secrets_resolver_that_returns_null_or_other_kids_holds_none()
    {
        (await RefusedAsync(present: _ => null!)).Message.Should().Contain("holds the private key of none");
        (await RefusedAsync(present: _ => new[] { QaKid.ToUpperInvariant(), "did:example:qa-elsewhere#k" })).Message.Should().Contain("holds the private key of none");
    }

    [Fact]
    public async Task A_secrets_resolver_that_answers_one_of_several_kids_passes()
    {
        var (validator, _, _) = Validator(
            keys: (_, _) => Task.FromResult<IReadOnlyList<Jwk>>(new[] { Key("did:example:qa-mediator#a"), Key("did:example:qa-mediator#b"), Key("did:example:qa-mediator#a") }),
            present: kids => kids.Contains("did:example:qa-mediator#b") ? new[] { "did:example:qa-mediator#b" } : Array.Empty<string>());

        await validator.Invoking(v => v.ValidateAsync(new[] { QaDid }, CancellationToken.None)).Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("keys")]
    [InlineData("secrets")]
    public async Task A_key_service_or_secrets_resolver_that_hangs_is_refused_after_the_bound_on_the_injected_clock(string which)
    {
        var hangKeys = new TaskCompletionSource<IReadOnlyList<Jwk>>();
        var hangSecrets = new TaskCompletionSource<IReadOnlyList<string>>();
        var (validator, _, time) = Validator(
            keys: which == "keys" ? (_, _) => hangKeys.Task : null,
            presentAsync: which == "secrets" ? _ => hangSecrets.Task : null,
            timeout: TimeSpan.FromSeconds(5));

        var validation = validator.ValidateAsync(new[] { QaDid }, CancellationToken.None);
        await time.WaitForScheduledTimersAsync(1, Wait);
        time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        validation.IsCompleted.Should().BeFalse();
        time.Advance(TimeSpan.FromTicks(1));

        (await validation.Awaiting(v => v).Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("index 0").And.NotContain(QaDid);
        hangKeys.TrySetException(new HandlerBoomException(HandlerBoomException.Marker));
        hangSecrets.TrySetException(new HandlerBoomException(HandlerBoomException.Marker));
    }

    [Fact]
    public async Task A_start_token_cancelled_while_the_resolver_hangs_leaves_as_cancellation()
    {
        var (validator, _, time) = Validator(resolve: async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<DidCommServiceInfo>();
        });
        using var cts = new CancellationTokenSource();

        var validation = validator.ValidateAsync(new[] { QaDid }, cts.Token);
        await time.WaitForScheduledTimersAsync(1, Wait);
        cts.Cancel();

        await validation.Awaiting(v => v).Should().ThrowAsync<OperationCanceledException>();
    }

    // ── the forward settings in effect at start-up ────────────────────────────────────────

    [Fact]
    public async Task A_host_ForwardProcessorOptions_registered_after_AddMediator_today_replaces_the_delay_cap_unnoticed()
    {
        // Same root cause as DEFECT 6: the start-up check validates MediatorOptions, not the
        // forward settings the processor actually got. Here the one-hour cap (FR-PICK-10's MUST bound)
        // is replaced by the library bound of about 24.8 days, and the mediator starts.
        await using var fixture = await HostedFixture.CreateAsync(after: s => s.AddSingleton(new ForwardProcessorOptions()));

        await fixture.StartingAsync();

        var forward = ForwardMessage.Create("did:example:qa-m", "did:example:qa-r", new[] { """{"protected":"a","ciphertext":"x","iv":"","tag":"","recipients":[]}""" });
        forward.AdditionalHeaders = new Dictionary<string, JsonElement> { ["delay_milli"] = JsonDocument.Parse(long.MaxValue.ToString(CultureInfo.InvariantCulture)).RootElement.Clone() };
        fixture.Services.GetRequiredService<ForwardProcessor>().ResolveDelay(forward).Should().Be(TimeSpan.FromMilliseconds(int.MaxValue));
    }

    // ── the purge round ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_store_cancellation_that_is_not_the_stop_is_a_logged_fault_and_the_schedule_goes_on()
    {
        var store = new ScriptedMessageStore();
        var calls = 0;
        store.OnPurgeExpired = (now, ct) => Interlocked.Increment(ref calls) == 1
            ? throw new OperationCanceledException(HandlerBoomException.Marker)
            : store.Inner.PurgeExpiredAsync(now, ct);
        await using var fixture = await HostedFixture.CreateAsync(before: s => s.AddSingleton<IMessageStore>(store));
        await fixture.StartAsync();
        await fixture.Time.WaitForScheduledTimersAsync(1, Wait);

        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await store.WaitForPurgesAsync(1, Wait);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await store.WaitForPurgesAsync(2, Wait);

        fixture.ServiceLines.Should().ContainSingle(e => e.Everything.Contains(nameof(OperationCanceledException), StringComparison.Ordinal));
        PipelineLogs.Leaking(fixture.Logs, HandlerBoomException.Marker).Should().BeEmpty();
        await fixture.StopAsync();
    }

    [Fact]
    public async Task A_purge_round_that_removes_nothing_records_no_measurement()
    {
        await using var fixture = await HostedFixture.CreateAsync();
        await fixture.StartingAsync();

        await fixture.Service.PurgeOnceAsync(CancellationToken.None);
        await fixture.Service.PurgeOnceAsync(CancellationToken.None);

        fixture.Recorder.Measurements.Where(m => m.Instrument == MediatorMetrics.QueuePurgedInstrument).Should().BeEmpty();
    }

    // ── a delayed message for a recipient in live mode ────────────────────────────────────

    [Fact]
    public async Task A_delayed_message_for_a_live_recipient_is_queued_and_not_pushed_once_it_falls_due()
    {
        // Observation: issue #95 scope 2.4 asked for a scheduler that pushes a delayed message live
        // once it is due; as built (and as the XML documents) it waits for the recipient to poll.
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did, configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(peer.Did, "did:example:qa-live-recipient");
        var session = new LiveTestSession(peer.Did, mediator.Did);
        fixture.Services.GetRequiredService<IReturnRouteSessionRegistry>().Register(session);
        var client = fixture.Services.GetRequiredService<DidCommClient>();
        var change = MessagePickup.CreateLiveDeliveryChange(peer.Did, mediator.Did, liveDelivery: true);
        var packed = await client.PackEncryptedAsync(change, new PackEncryptedOptions(Recipients: new[] { mediator.Did }, From: peer.Did));
        await fixture.Services.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, fixture.Options, Task.CompletedTask, session, CancellationToken.None);
        var before = session.Sent.Count;

        var result = await fixture.Queue.DeliverAsync("did:example:qa-live-recipient", PushFixture.Payload(40), new PickupDeliveryOptions(NotBefore: fixture.Time.GetUtcNow().AddMilliseconds(500)));
        fixture.Time.Advance(TimeSpan.FromSeconds(1));

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        session.Sent.Count.Should().Be(before, "nothing pushes it when it falls due");
        (await fixture.Store.GetStatusAsync(peer.Did, null, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(1);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private const string QaDid = "did:example:qa-mediator";
    private const string QaKid = "did:example:qa-mediator#key-x25519-1";

    private static DidCommServiceInfo Entry(string uri) => new(uri, Array.Empty<string>(), new[] { "didcomm/v2" });

    private static Jwk Key(string? kid) => new() { Kty = "OKP", Crv = "X25519", X = "stub", Kid = kid };

    private static async Task<Exception> StartFailsAsync(HostedFixture fixture)
        => (await FluentActions.Awaiting(fixture.StartingAsync).Should().ThrowAsync<Exception>()).Which;

    private static async Task<InvalidOperationException> RefusedAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<DidCommServiceInfo>>>? resolve = null,
        Func<string, CancellationToken, Task<IReadOnlyList<Jwk>>>? keys = null,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? present = null)
    {
        var (validator, logs, _) = Validator(resolve, keys, present);
        var refusal = (await validator.Invoking(v => v.ValidateAsync(new[] { QaDid }, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>()).Which;
        refusal.Message.Should().Contain("index 0").And.NotContain(QaDid).And.NotContain("qa-host");
        refusal.InnerException.Should().BeNull();
        PipelineLogs.Leaking(logs, QaDid, HandlerBoomException.Marker).Should().BeEmpty();
        return refusal;
    }

    private static (RoutingDidValidator Validator, HandlerLogSink Logs, ManualTimeProvider Time) Validator(
        Func<string, CancellationToken, Task<IReadOnlyList<DidCommServiceInfo>>>? resolve = null,
        Func<string, CancellationToken, Task<IReadOnlyList<Jwk>>>? keys = null,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? present = null,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? presentAsync = null,
        TimeSpan? timeout = null)
    {
        var logs = new HandlerLogSink();
        var time = new ManualTimeProvider();
        var validator = new RoutingDidValidator(
            new ScriptedServiceResolver(resolve ?? ((_, _) => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(new[] { Entry("https://qa-host.example/d") }))),
            new ScriptedKeys(keys ?? ((_, _) => Task.FromResult<IReadOnlyList<Jwk>>(new[] { Key(QaKid) }))),
            new ScriptedSecrets(presentAsync ?? (kids => Task.FromResult(present is null ? kids : present(kids)))),
            time,
            timeout ?? RoutingDidValidator.DefaultTimeout,
            new HandlerCapturingLogger<MediatorHostedService>(logs));
        return (validator, logs, time);
    }

    private sealed class ListIdentity(IReadOnlyList<string>? dids) : IMediatorIdentity
    {
        public IReadOnlyList<string> RoutingDids => dids!;
    }

    private sealed class ScriptedKeys(Func<string, CancellationToken, Task<IReadOnlyList<Jwk>>> get) : IDidKeyService
    {
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default) => get(did, ct);
        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default) => Task.FromResult(false);
        public void RejectUnsupportedMethod(string did) { }
    }

    private sealed class ScriptedSecrets(Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>> present) : ISecretsResolver
    {
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default) => Task.FromResult<Jwk?>(null);
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default) => present(kids.ToArray());
    }
}
