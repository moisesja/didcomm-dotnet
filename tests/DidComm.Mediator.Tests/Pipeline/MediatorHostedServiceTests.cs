using System.Text.Json;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Protocols.OutOfBand;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// <see cref="MediatorHostedService"/> (JC-9, design §7): the start-up checks and what they refuse,
/// the invitation, the instruments, the purge schedule on a manual clock, the counter reports, fault
/// tolerance and a prompt stop. No real sleeps: the schedule moves only with the manual clock.
/// </summary>
public sealed class MediatorHostedServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<Exception> StartFailsAsync(HostedFixture fixture)
    {
        var failure = (await fixture.Invoking(f => f.StartingAsync()).Should().ThrowAsync<Exception>()).Which;
        foreach (var did in fixture.Dids)
            failure.ToString().Should().NotContain(did, "a start-up failure names no DID");
        return failure;
    }

    // ---- a good start ------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_did_peer_routing_did_starts_and_the_invitation_is_issued_from_it()
    {
        await using var fixture = await HostedFixture.CreateAsync();

        await fixture.StartingAsync();

        var json = fixture.Invitation.PlaintextJson.Should().NotBeNull().And.Subject!;
        using var invitation = JsonDocument.Parse(json);
        var root = invitation.RootElement;
        root.GetProperty("type").GetString().Should().Be(OutOfBand.InvitationType);
        root.GetProperty("from").GetString().Should().Be(fixture.Mediator!.Did);
        root.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        var body = root.GetProperty("body");
        body.GetProperty("goal_code").GetString().Should().Be("request-mediate");
        body.GetProperty("goal").GetString().Should().Be("RequestMediate");
        body.GetProperty("accept").EnumerateArray().Select(e => e.GetString()).Should().Equal("didcomm/v2");

        var ready = fixture.ServiceLines.Should().ContainSingle(e => e.Level == LogLevel.Information).Which;
        ready.Everything.Should().Contain("Mediator ready").And.Contain("1 routing DID");
        PipelineLogs.Leaking(fixture.Logs, fixture.Dids).Should().BeEmpty();
    }

    [Fact]
    public async Task A_wss_endpoint_passes()
    {
        await using var fixture = await HostedFixture.CreateAsync(new DidCommServiceSpec("wss://mediator-host.example/didcomm"));

        await fixture.Invoking(f => f.StartingAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Routing_keys_on_the_mediators_own_service_start_with_a_warning()
    {
        await using var fixture = await HostedFixture.CreateAsync(new DidCommServiceSpec("https://mediator-host.example/didcomm", RoutingKeys: new[] { "did:example:upstream#key-1" }));

        await fixture.StartingAsync();

        fixture.ServiceLines.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Everything.Should().Contain("routing keys");
        PipelineLogs.Leaking(fixture.Logs, fixture.Dids.Append("upstream").ToArray()).Should().BeEmpty();
    }

    [Fact]
    public async Task Direct_push_without_a_directory_starts_with_a_warning()
    {
        await using var fixture = await HostedFixture.CreateAsync(configure: o => o.DirectPush = true);

        await fixture.StartingAsync();

        fixture.ServiceLines.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Everything.Should().Contain("IRecipientEndpointDirectory");
    }

    [Fact]
    public async Task Direct_push_with_a_directory_starts_quietly()
    {
        await using var fixture = await HostedFixture.CreateAsync(
            configure: o => o.DirectPush = true,
            before: s => s.AddSingleton<IRecipientEndpointDirectory>(new ScriptedDirectory(_ => null)));

        await fixture.StartingAsync();

        fixture.ServiceLines.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_host_identity_with_several_routing_dids_is_checked_in_order()
    {
        await using var peers = new PipelinePeers();
        var second = await peers.NewAsync(HostedFixture.HttpsService);
        HostedFixture? fixture = null;
        try
        {
            fixture = await HostedFixture.CreateAsync(configure: o => o.CoordinateMediation.RoutingDids.Add(second.Did));
            foreach (var jwk in second.Privates)
                fixture.Peers.Secrets.Add(jwk);

            await fixture.StartingAsync();

            fixture.ServiceLines.Should().ContainSingle(e => e.Level == LogLevel.Information).Which.Everything.Should().Contain("2 routing DID");
            JsonDocument.Parse(fixture.Invitation.PlaintextJson!).RootElement.GetProperty("from").GetString().Should().Be(fixture.Mediator!.Did, "the first routing DID is the primary one");
        }
        finally
        {
            if (fixture is not null)
                await fixture.DisposeAsync();
        }
    }

    // ---- what start-up refuses ---------------------------------------------------------------

    [Fact]
    public async Task A_host_identity_with_no_routing_did_fails_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(
            routingDid: string.Empty,
            before: s => s.AddSingleton<IMediatorIdentity>(new FixedIdentity()));

        var failure = await StartFailsAsync(fixture);

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("no routing DID");
    }

    [Fact]
    public async Task A_host_identity_with_a_did_url_fails_start_up_by_index()
    {
        await using var fixture = await HostedFixture.CreateAsync(
            routingDid: string.Empty,
            before: s => s.AddSingleton<IMediatorIdentity>(new FixedIdentity("did:example:ok", "did:example:secret-mediator#key-1")));

        var failure = await StartFailsAsync(fixture);

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("index 1").And.NotContain("secret-mediator");
    }

    [Fact]
    public async Task A_throwing_host_identity_fails_start_up_by_type()
    {
        await using var fixture = await HostedFixture.CreateAsync(
            routingDid: string.Empty,
            before: s => s.AddSingleton<IMediatorIdentity>(new ThrowingIdentity()));

        var failure = await StartFailsAsync(fixture);

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain(nameof(HandlerBoomException)).And.NotContain(HandlerBoomException.Marker);
    }

    [Fact]
    public async Task A_service_uri_that_is_a_did_fails_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(new DidCommServiceSpec("did:example:another-mediator"));

        (await StartFailsAsync(fixture)).Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("index 0").And.Contain("names a DID");
    }

    [Fact]
    public async Task No_service_fails_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(noService: true);

        (await StartFailsAsync(fixture)).Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("no DIDCommMessaging service");
    }

    [Fact]
    public async Task An_unsupported_scheme_fails_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(new DidCommServiceSpec("ftp://mediator-host.example/didcomm"));

        (await StartFailsAsync(fixture)).Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("index 0").And.NotContain("mediator-host");
    }

    [Fact]
    public async Task An_unresolvable_method_fails_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(mintMediator: false, routingDid: "did:example:unresolvable-mediator");

        var failure = await StartFailsAsync(fixture);

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("index 0").And.NotContain("unresolvable-mediator");
    }

    [Fact]
    public async Task No_held_key_agreement_secret_fails_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(holdSecrets: false);

        (await StartFailsAsync(fixture)).Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("private key");
    }

    [Fact]
    public async Task An_unsatisfiable_delivery_budget_fails_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(didComm: o => o.MaxReceiveBytes = 16 * 1024);

        var failure = await StartFailsAsync(fixture);

        failure.Should().BeAssignableTo<ArgumentException>().Which.ParamName.Should().Be(nameof(MessagePickupOptions.MaxDeliveryBytes),
            "the pickup queue, built while every handler is resolved, finds that no message fits its delivery budget");
        fixture.Invitation.PlaintextJson.Should().BeNull("nothing is advertised by a mediator that did not start");
    }

    [Fact]
    public async Task A_forward_sink_registered_after_AddMediator_fails_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(after: s => s.AddSingleton<IForwardSink, NullForwardSink>());

        (await StartFailsAsync(fixture)).Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("IForwardSink");
    }

    [Fact]
    public async Task No_did_resolution_fails_start_up_naming_UseNetDidResolver()
    {
        await using var fixture = await HostedFixture.CreateAsync(after: s => s.RemoveAll<IServiceEndpointResolver>());

        (await StartFailsAsync(fixture)).Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("UseNetDidResolver()");
    }

    [Fact]
    public async Task A_rule_across_roles_is_checked_on_the_values_in_effect()
    {
        await using var fixture = await HostedFixture.CreateAsync(after: s => s.Configure<MessagePickupOptions>(p => p.MaxMessageAge = TimeSpan.FromMinutes(30)));

        (await StartFailsAsync(fixture)).Should().BeAssignableTo<ArgumentException>().Which.ParamName.Should().Be("Pickup.MaxMessageAge");
    }

    [Fact]
    public async Task Bad_replay_settings_registered_by_the_host_fail_start_up()
    {
        await using var fixture = await HostedFixture.CreateAsync(after: s => s.Configure<ReplayProtectionOptions>(r => r.MaxEntries = 0));

        (await StartFailsAsync(fixture)).Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task A_resolver_that_hangs_fails_start_up_after_30_seconds_on_the_injected_clock()
    {
        var hang = new TaskCompletionSource<IReadOnlyList<DidCommServiceInfo>>();
        await using var fixture = await HostedFixture.CreateAsync(after: s =>
        {
            s.RemoveAll<IServiceEndpointResolver>();
            s.AddSingleton<IServiceEndpointResolver>(new ScriptedServiceResolver((_, _) => hang.Task));
        });

        var starting = fixture.StartingAsync();
        await fixture.Time.WaitForScheduledTimersAsync(1, Wait);
        fixture.Time.Advance(TimeSpan.FromSeconds(30));

        (await starting.Awaiting(t => t).Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("30 seconds");
    }

    // ---- instruments -------------------------------------------------------------------------

    [Fact]
    public async Task The_observable_instruments_report_the_in_memory_counters()
    {
        await using var fixture = await HostedFixture.CreateAsync();
        await fixture.StartingAsync();
        await fixture.EnrolAsync("did:example:peer", "did:example:recipient");
        await fixture.Queue.DeliverAsync("did:example:recipient", PushFixture.Payload(50));

        fixture.Recorder.Observe();

        fixture.Recorder.Sum(MediatorMetrics.QueueMessagesInstrument).Should().Be(1);
        fixture.Recorder.Sum(MediatorMetrics.QueueBytesInstrument).Should().Be(50);
        fixture.Recorder.Measurements.Should().Contain(m => m.Instrument == MediatorMetrics.ReplaySaturatedInstrument);
        fixture.Recorder.Measurements.Should().Contain(m => m.Instrument == MediatorMetrics.ReplayClockBehindInstrument);
        fixture.Recorder.Measurements.Should().Contain(m => m.Instrument == MediatorMetrics.DroppedNotificationsInstrument);
    }

    // ---- the purge schedule ------------------------------------------------------------------

    [Fact]
    public async Task The_purge_runs_every_interval_on_the_injected_clock_and_removes_expired_messages()
    {
        var store = new ScriptedMessageStore();
        await using var fixture = await HostedFixture.CreateAsync(before: s => s.AddSingleton<IMessageStore>(store));
        await fixture.StartAsync();
        await fixture.EnrolAsync("did:example:peer", "did:example:recipient");
        var expires = fixture.Time.GetUtcNow().ToUnixTimeSeconds() + 10;
        (await fixture.Queue.DeliverAsync("did:example:recipient", PushFixture.Payload(40), new PickupDeliveryOptions(ExpiresTime: expires))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        await fixture.Time.WaitForScheduledTimersAsync(1, Wait);

        fixture.Time.Advance(TimeSpan.FromSeconds(59));
        store.Purges.Should().Be(0, "the interval has not passed");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await store.WaitForPurgesAsync(1, Wait);

        store.Inner.MessageCount.Should().Be(0);
        fixture.Recorder.Sum(MediatorMetrics.QueuePurgedInstrument).Should().Be(1);
        fixture.ServiceLines.Should().ContainSingle(e => e.Everything.Contains("1 expired or aged-out", StringComparison.Ordinal));

        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await store.WaitForPurgesAsync(2, Wait);
        fixture.ServiceLines.Count(e => e.Everything.Contains("expired or aged-out", StringComparison.Ordinal)).Should().Be(1, "a round that removes nothing says nothing");

        await fixture.StopAsync();
    }

    [Fact]
    public async Task A_store_fault_is_logged_by_type_and_the_schedule_goes_on()
    {
        var store = new ScriptedMessageStore();
        var calls = 0;
        store.OnPurgeExpired = (now, ct) => Interlocked.Increment(ref calls) == 1
            ? throw new HandlerBoomException(HandlerBoomException.Marker)
            : store.Inner.PurgeExpiredAsync(now, ct);
        await using var fixture = await HostedFixture.CreateAsync(before: s => s.AddSingleton<IMessageStore>(store));
        await fixture.StartAsync();
        await fixture.Time.WaitForScheduledTimersAsync(1, Wait);

        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await store.WaitForPurgesAsync(1, Wait);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await store.WaitForPurgesAsync(2, Wait);

        var warning = fixture.ServiceLines.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which;
        warning.Everything.Should().Contain(nameof(HandlerBoomException)).And.NotContain(HandlerBoomException.Marker);
        await fixture.StopAsync();
    }

    [Fact]
    public async Task Stopping_is_prompt_and_ends_the_schedule()
    {
        var store = new ScriptedMessageStore();
        await using var fixture = await HostedFixture.CreateAsync(before: s => s.AddSingleton<IMessageStore>(store));
        await fixture.StartAsync();
        await fixture.Time.WaitForScheduledTimersAsync(1, Wait);

        await fixture.StopAsync().WaitAsync(Wait);

        fixture.Time.ScheduledTimers.Should().Be(0, "the purge timer is gone");
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        store.Purges.Should().Be(0);
    }

    [Fact]
    public async Task Starting_the_schedule_twice_runs_one_loop_and_stopping_before_start_is_harmless()
    {
        var store = new ScriptedMessageStore();
        await using var fixture = await HostedFixture.CreateAsync(before: s => s.AddSingleton<IMessageStore>(store));
        await fixture.Service.StopAsync(CancellationToken.None);

        await using var second = await HostedFixture.CreateAsync(before: s => s.AddSingleton<IMessageStore>(new ScriptedMessageStore()));
        await second.StartAsync();
        await second.Service.StartedAsync(CancellationToken.None);
        await second.Time.WaitForScheduledTimersAsync(1, Wait);
        second.Time.ScheduledTimers.Should().Be(1);
        await second.StopAsync();
    }

    // ---- the counter reports -----------------------------------------------------------------

    [Fact]
    public async Task Replay_saturation_is_reported_once_per_change_with_the_remedy()
    {
        await using var fixture = await HostedFixture.CreateAsync(configure: o => o.Replay.MaxEntriesPerPeer = 1);
        await fixture.StartingAsync();
        var guard = (InMemoryReplayGuard)fixture.Services.GetRequiredService<IInboundReplayGuard>();
        var now = fixture.Time.GetUtcNow();
        guard.TryAdmit("did:example:flooder", "id-1", now.ToUnixTimeSeconds(), now);
        guard.TryAdmit("did:example:flooder", "id-2", now.ToUnixTimeSeconds(), now).Should().Be(ReplayAdmission.Saturated);

        await fixture.Service.PurgeOnceAsync(CancellationToken.None);
        await fixture.Service.PurgeOnceAsync(CancellationToken.None);
        guard.TryAdmit("did:example:flooder", "id-3", now.ToUnixTimeSeconds(), now);
        await fixture.Service.PurgeOnceAsync(CancellationToken.None);

        var reports = fixture.ServiceLines.Where(e => e.Everything.Contains("memory was full", StringComparison.Ordinal)).ToArray();
        reports.Should().HaveCount(2, "one report per change, none when nothing moved");
        reports.Should().OnlyContain(e => e.Level == LogLevel.Warning && e.Everything.Contains("MaxEntries", StringComparison.Ordinal));
        reports[0].State["Delta"].Should().Be(1L);
        reports[1].State["Total"].Should().Be(2L);
        PipelineLogs.Leaking(fixture.Logs, "flooder").Should().BeEmpty();
    }

    [Fact]
    public async Task A_clock_stepped_back_is_reported_with_the_remedy()
    {
        await using var fixture = await HostedFixture.CreateAsync();
        await fixture.StartingAsync();
        var guard = (InMemoryReplayGuard)fixture.Services.GetRequiredService<IInboundReplayGuard>();
        var now = fixture.Time.GetUtcNow();
        guard.TryAdmit("did:example:peer", "id-1", now.ToUnixTimeSeconds(), now);
        var behind = now - TimeSpan.FromMinutes(10);
        guard.TryAdmit("did:example:peer", "id-2", behind.ToUnixTimeSeconds(), behind).Should().Be(ReplayAdmission.TooOld);
        guard.ClockBehindRefusalCount.Should().Be(1);

        await fixture.Service.PurgeOnceAsync(CancellationToken.None);

        fixture.ServiceLines.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Everything.Contains("stepped back", StringComparison.Ordinal));
        fixture.ServiceLines.Should().NotContain(e => e.Everything.Contains("memory was full", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_purge_round_leaves_as_cancellation()
    {
        var store = new ScriptedMessageStore { OnPurgeExpired = (_, ct) => ValueTask.FromCanceled<int>(ct) };
        await using var fixture = await HostedFixture.CreateAsync(before: s => s.AddSingleton<IMessageStore>(store));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await fixture.Service.Invoking(s => s.PurgeOnceAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FixedIdentity(params string[] dids) : IMediatorIdentity
    {
        public IReadOnlyList<string> RoutingDids { get; } = dids;
    }

    private sealed class ThrowingIdentity : IMediatorIdentity
    {
        public IReadOnlyList<string> RoutingDids => throw new HandlerBoomException(HandlerBoomException.Marker);
    }
}
