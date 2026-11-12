using System.Text.Json;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

// L-014: a namespace import must not shadow the same-named static class.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// <c>AddMessagePickup()</c> through the real container (FR-PICK-01, JC-17): what it registers,
/// Discover Features disclosure, independence from the order of <c>AddCoordinateMediation()</c>,
/// the failure that names the fix when no mediation store exists, host overrides, idempotence,
/// fail-fast option validation, the L-017 cycle checks, and how the delivery budget follows
/// <c>DidCommOptions.MaxReceiveBytes</c>.
/// </summary>
public sealed class MessagePickupDiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string Alice = "did:example:alice";

    private static ServiceProvider Build(Action<DidCommBuilder> configure, Action<IServiceCollection>? before = null, InMemorySecretsResolver? secrets = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets ?? new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            configure(b);
        });
        return services.BuildServiceProvider();
    }

    /// <summary>Enrol <see cref="Alice"/> with her own DID registered, straight through the mediation store (no envelope needed).</summary>
    private static async Task EnrolAliceAsync(IServiceProvider sp)
    {
        var mediation = sp.GetRequiredService<IMediationStore>();
        (await mediation.GrantAsync(new MediationGrant(Alice, new[] { "did:example:mediator" }), CancellationToken.None)).Should().NotBeNull();
        (await mediation.UpdateRecipientsAsync(Alice, new[] { new RecipientUpdate(Alice, RecipientUpdateAction.Add) }, CancellationToken.None))
            .Should().Equal(RecipientStoreOutcome.Added);
    }

    private static async Task<DispatchOutcome> DispatchAsync(IServiceProvider sp, Message message, string to)
    {
        var client = sp.GetRequiredService<DidCommClient>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }, From: message.From));
        return await sp.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, options);
    }

    // ---- registrations --------------------------------------------------------------------

    [Fact]
    public void AddMessagePickup_registers_the_handler_the_queue_and_the_in_memory_store()
    {
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup();
        });

        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        var handler = sp.GetRequiredService<MessagePickupHandler>();
        foreach (var type in new[]
                 {
                     MessagePickup.StatusRequestType, MessagePickup.StatusType, MessagePickup.DeliveryRequestType,
                     MessagePickup.DeliveryType, MessagePickup.MessagesReceivedType, MessagePickup.LiveDeliveryChangeType,
                 })
        {
            registry.TryResolve(type, out var resolved).Should().BeTrue(type);
            resolved.Should().BeSameAs(handler, "the registry entry and the typed singleton are one instance");
        }
        registry.TryResolve("https://didcomm.org/messagepickup/4.0/status-request", out _).Should().BeFalse("4.0 is a separate handler (#100)");

        handler.ProtocolUri.Should().Be("https://didcomm.org/messagepickup/3.0");
        handler.Roles.Should().Equal("mediator");
        sp.GetRequiredService<IMessageStore>().Should().BeOfType<InMemoryMessageStore>();
        sp.GetRequiredService<MessagePickupQueue>().MaxAdmissiblePayloadBytes.Should().BePositive();
        sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value.MaxDeliveryBatch.Should().Be(10);
    }

    [Fact]
    public void An_agent_that_did_not_ask_for_it_registers_none_of_it()
    {
        using var ordinary = Build(_ => { });
        using var mediationOnly = Build(b => b.AddCoordinateMediation());

        foreach (var sp in new[] { ordinary, mediationOnly })
        {
            sp.GetService<MessagePickupHandler>().Should().BeNull();
            sp.GetService<MessagePickupQueue>().Should().BeNull();
            sp.GetService<IMessageStore>().Should().BeNull();
            sp.GetRequiredService<ProtocolHandlerRegistry>().TryResolve(MessagePickup.StatusRequestType, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void The_queue_and_the_handler_are_singletons()
    {
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup();
        });

        var queue = sp.GetRequiredService<MessagePickupQueue>();
        var handler = sp.GetRequiredService<MessagePickupHandler>();
        using var scope = sp.CreateScope();

        sp.GetRequiredService<MessagePickupQueue>().Should().BeSameAs(queue);
        scope.ServiceProvider.GetRequiredService<MessagePickupQueue>().Should().BeSameAs(queue);
        sp.GetRequiredService<MessagePickupHandler>().Should().BeSameAs(handler);
        scope.ServiceProvider.GetRequiredService<MessagePickupHandler>().Should().BeSameAs(handler);
        sp.GetServices<IProtocolHandler>().OfType<MessagePickupHandler>().Should().ContainSingle().Which.Should().BeSameAs(handler);
        scope.ServiceProvider.GetRequiredService<IMessageStore>().Should().BeSameAs(sp.GetRequiredService<IMessageStore>());
    }

    [Fact]
    public void AddMessagePickup_refuses_a_null_builder()
    {
        var act = () => MessagePickupBuilderExtensions.AddMessagePickup(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- Discover Features (FR-PICK-01) ---------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Discover_features_discloses_message_pickup_3_0_with_roles_mediator_only_when_registered(bool pickupMediator)
    {
        var secrets = new InMemorySecretsResolver();
        await using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            if (pickupMediator)
                b.AddMessagePickup();
        }, secrets: secrets);
        var alice = await DiPeers.NewAsync(sp, secrets);
        var mediator = await DiPeers.NewAsync(sp, secrets);

        var query = DiscoverFeaturesApi.CreateQuery(alice.Did, mediator.Did,
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "https://didcomm.org/messagepickup/*" });
        var outcome = await DispatchAsync(sp, query, mediator.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        var disclosures = DiscoverFeaturesApi.ReadDisclosures(outcome.Reply!);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default));
        var entries = wire.RootElement.GetProperty("body").GetProperty("disclosures").EnumerateArray().ToList();

        if (pickupMediator)
        {
            var disclosed = disclosures.Should().ContainSingle().Which;
            disclosed.Id.Should().Be("https://didcomm.org/messagepickup/3.0");
            disclosed.Roles.Should().Equal("mediator");
            var entry = entries.Should().ContainSingle().Which;
            entry.GetProperty("feature-type").GetString().Should().Be("protocol");
            entry.GetProperty("id").GetString().Should().Be("https://didcomm.org/messagepickup/3.0");
            entry.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("mediator");
        }
        else
        {
            disclosures.Should().BeEmpty("a mediator that does not offer pickup does not advertise it");
            entries.Should().BeEmpty();
        }
    }

    // ---- order against AddCoordinateMediation (JC-17) -------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task It_works_whether_it_is_called_before_or_after_AddCoordinateMediation_and_the_handler_serves_the_queue_the_host_resolves(bool pickupFirst)
    {
        var secrets = new InMemorySecretsResolver();
        await using var sp = Build(b =>
        {
            if (pickupFirst)
            {
                b.AddMessagePickup();
                b.AddCoordinateMediation();
            }
            else
            {
                b.AddCoordinateMediation();
                b.AddMessagePickup();
            }
        }, secrets: secrets);
        var alice = await DiPeers.NewAsync(sp, secrets);
        var mediator = await DiPeers.NewAsync(sp, secrets);
        var mediation = sp.GetRequiredService<IMediationStore>();
        mediation.Should().BeOfType<InMemoryMediationStore>();
        await mediation.GrantAsync(new MediationGrant(alice.Did, new[] { mediator.Did }), CancellationToken.None);
        await mediation.UpdateRecipientsAsync(alice.Did, new[] { new RecipientUpdate(alice.Did, RecipientUpdateAction.Add) }, CancellationToken.None);

        // In: the queue as a delivery pipeline resolves it. Out: the handler the dispatcher reaches.
        var delivered = await sp.GetRequiredService<MessagePickupQueue>().DeliverAsync(alice.Did, new byte[] { 1, 2, 3 });
        var outcome = await DispatchAsync(sp, MessagePickup.CreateStatusRequest(alice.Did, mediator.Did), mediator.Did);

        delivered.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        outcome.Handler.Should().BeSameAs(sp.GetRequiredService<MessagePickupHandler>());
        MessagePickup.TryParseStatus(outcome.Reply!, out var status).Should().BeTrue();
        status!.MessageCount.Should().Be(1);
        status.TotalBytes.Should().Be(3);
        ((InMemoryMessageStore)sp.GetRequiredService<IMessageStore>()).MessageCount.Should().Be(1);
    }

    public static TheoryData<string> FirstResolutions => new() { "the queue", "the handler", "the handler registry", "the dispatcher" };

    [Theory]
    [MemberData(nameof(FirstResolutions))]
    public void Without_a_mediation_store_registration_succeeds_and_the_first_resolution_names_AddCoordinateMediation(string what)
    {
        using var sp = Build(b => b.AddMessagePickup());
        Action resolve = what switch
        {
            "the queue" => () => sp.GetRequiredService<MessagePickupQueue>(),
            "the handler" => () => sp.GetRequiredService<MessagePickupHandler>(),
            "the handler registry" => () => sp.GetRequiredService<ProtocolHandlerRegistry>(),
            _ => () => sp.GetRequiredService<ProtocolDispatcher>(),
        };

        var first = resolve.Should().Throw<InvalidOperationException>().Which;

        first.Message.Should().Contain("AddCoordinateMediation()").And.Contain("IMediationStore");
        // A second attempt tells the same story: it neither succeeds nor turns into something else.
        resolve.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("AddCoordinateMediation()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_host_mediation_store_is_enough_without_AddCoordinateMediation_wherever_it_is_registered(bool registeredFirst)
    {
        var mediation = new InMemoryMediationStore();
        await using var sp = Build(
            b =>
            {
                b.AddMessagePickup();
                if (!registeredFirst)
                    b.Services.AddSingleton<IMediationStore>(mediation);
            },
            before: s =>
            {
                if (registeredFirst)
                    s.AddSingleton<IMediationStore>(mediation);
            });
        await EnrolAliceAsync(sp);

        (await sp.GetRequiredService<MessagePickupQueue>().DeliverAsync(Alice, new byte[] { 1 })).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        sp.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
        sp.GetService<CoordinateMediationHandler>().Should().BeNull("pickup needs the store, not the protocol");
    }

    // ---- host overrides, idempotence ------------------------------------------------------

    [Fact]
    public async Task A_host_message_store_registered_first_wins_and_is_the_store_the_queue_writes_to()
    {
        var store = new InMemoryMessageStore();
        await using var sp = Build(
            b =>
            {
                b.AddCoordinateMediation();
                b.AddMessagePickup(o => o.MaxQueueMessages = 1);
            },
            before: s => s.AddSingleton<IMessageStore>(store));
        await EnrolAliceAsync(sp);

        sp.GetRequiredService<IMessageStore>().Should().BeSameAs(store);
        sp.GetServices<IMessageStore>().Should().ContainSingle("AddMessagePickup's default is a TryAdd and stays out");
        var queue = sp.GetRequiredService<MessagePickupQueue>();
        (await queue.DeliverAsync(Alice, new byte[] { 1, 2 })).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await queue.DeliverAsync(Alice, new byte[] { 3 })).Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the host's store has its own bounds; the options only size the default store");
        store.MessageCount.Should().Be(2);
    }

    [Fact]
    public async Task Calling_it_again_registers_one_of_each_and_composes_the_settings()
    {
        await using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup(o => o.MaxDeliveryBatch = 3);
            b.AddMessagePickup();
            b.AddMessagePickup(o => o.MaxQueueMessages = 7);
            b.AddMessagePickup(o => o.LeaseTimeout = TimeSpan.FromSeconds(5));
        });

        sp.GetServices<IProtocolHandler>().OfType<MessagePickupHandler>().Should().HaveCount(1);
        sp.GetServices<MessagePickupHandler>().Should().HaveCount(1);
        sp.GetServices<MessagePickupQueue>().Should().HaveCount(1);
        sp.GetServices<IMessageStore>().Should().HaveCount(1);

        var options = sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value;
        options.MaxDeliveryBatch.Should().Be(3);
        options.MaxQueueMessages.Should().Be(7);
        options.LeaseTimeout.Should().Be(TimeSpan.FromSeconds(5));

        // The default store was built from the composed settings: seven fit, the eighth does not.
        await EnrolAliceAsync(sp);
        var queue = sp.GetRequiredService<MessagePickupQueue>();
        for (var i = 0; i < 7; i++)
            (await queue.DeliverAsync(Alice, new byte[] { (byte)i })).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await queue.DeliverAsync(Alice, new byte[] { 7 })).Outcome.Should().Be(PickupDeliveryOutcome.QueueFull);
    }

    // ---- option validation ----------------------------------------------------------------

    public static TheoryData<string, string, Action<MessagePickupOptions>> BadSettings => new()
    {
        { "MaxDeliveryBatch 0", nameof(MessagePickupOptions.MaxDeliveryBatch), o => o.MaxDeliveryBatch = 0 },
        { "MaxDeliveryBatch negative", nameof(MessagePickupOptions.MaxDeliveryBatch), o => o.MaxDeliveryBatch = -1 },
        { "MaxQueueMessages 0", nameof(MessagePickupOptions.MaxQueueMessages), o => o.MaxQueueMessages = 0 },
        { "MaxQueueBytes 0", nameof(MessagePickupOptions.MaxQueueBytes), o => o.MaxQueueBytes = 0 },
        { "MaxStoreMessages 0", nameof(MessagePickupOptions.MaxStoreMessages), o => o.MaxStoreMessages = 0 },
        { "MaxStoreBytes 0", nameof(MessagePickupOptions.MaxStoreBytes), o => o.MaxStoreBytes = 0 },
        { "MaxAckIds below MaxDeliveryBatch", nameof(MessagePickupOptions.MaxAckIds), o => { o.MaxDeliveryBatch = 10; o.MaxAckIds = 9; } },
        { "LeaseTimeout zero", nameof(MessagePickupOptions.LeaseTimeout), o => o.LeaseTimeout = TimeSpan.Zero },
        { "LeaseTimeout negative", nameof(MessagePickupOptions.LeaseTimeout), o => o.LeaseTimeout = TimeSpan.FromSeconds(-1) },
        { "LeaseTimeout over 24 hours", nameof(MessagePickupOptions.LeaseTimeout), o => o.LeaseTimeout = TimeSpan.FromHours(24) + TimeSpan.FromTicks(1) },
        { "MaxStoreMessages below MaxQueueMessages", nameof(MessagePickupOptions.MaxStoreMessages), o => { o.MaxQueueMessages = 10; o.MaxStoreMessages = 9; } },
        { "MaxStoreBytes below MaxQueueBytes", nameof(MessagePickupOptions.MaxStoreBytes), o => { o.MaxQueueBytes = 1000; o.MaxStoreBytes = 999; } },
        { "MaxDeliveryBytes below the envelope overhead", nameof(MessagePickupOptions.MaxDeliveryBytes), o => o.MaxDeliveryBytes = 32 * 1024 },
        { "MaxDeliveryBytes 0", nameof(MessagePickupOptions.MaxDeliveryBytes), o => o.MaxDeliveryBytes = 0 },
        { "MaxDeliveryBytes negative", nameof(MessagePickupOptions.MaxDeliveryBytes), o => o.MaxDeliveryBytes = -1 },
    };

    [Theory]
    [MemberData(nameof(BadSettings))]
    public void Bad_settings_fail_at_registration_and_name_the_setting(string what, string setting, Action<MessagePickupOptions> configure)
    {
        var act = () => Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup(configure);
        });

        act.Should().Throw<ArgumentException>(what).Which.ParamName.Should().Be(setting);

        // The same check guards the options object itself and the store built from it.
        var options = new MessagePickupOptions();
        configure(options);
        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new InMemoryMessageStore(options)).Should().Throw<ArgumentException>();
    }

    public static TheoryData<string, Action<MessagePickupOptions>> EdgeSettings => new()
    {
        { "every bound at 1", o => { o.MaxDeliveryBatch = 1; o.MaxQueueMessages = 1; o.MaxQueueBytes = 1; o.MaxStoreMessages = 1; o.MaxStoreBytes = 1; } },
        { "MaxAckIds equal to MaxDeliveryBatch", o => { o.MaxDeliveryBatch = 10; o.MaxAckIds = 10; } },
        { "LeaseTimeout one tick", o => o.LeaseTimeout = TimeSpan.FromTicks(1) },
        { "LeaseTimeout exactly 24 hours", o => o.LeaseTimeout = TimeSpan.FromHours(24) },
        { "store-wide bounds equal to the per-grant bounds", o => { o.MaxQueueMessages = 50; o.MaxStoreMessages = 50; o.MaxQueueBytes = 4096; o.MaxStoreBytes = 4096; } },
    };

    [Theory]
    [MemberData(nameof(EdgeSettings))]
    public void Settings_at_the_edge_of_what_is_valid_are_accepted(string what, Action<MessagePickupOptions> configure)
    {
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup(configure);
        });

        sp.GetRequiredService<MessagePickupQueue>().Should().NotBeNull(what);
        sp.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
    }

    [Fact]
    public void Settings_that_only_become_invalid_when_composed_fail_when_the_queue_is_first_built()
    {
        // Each delegate passes on its own scratch instance; together they do not.
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup(o => o.MaxAckIds = 12);
            b.AddMessagePickup(o => o.MaxDeliveryBatch = 20);
        });

        FluentActions.Invoking(() => sp.GetRequiredService<MessagePickupQueue>()).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => sp.GetRequiredService<ProtocolDispatcher>()).Should().Throw<ArgumentException>();
    }

    // ---- L-017: dependency cycles ---------------------------------------------------------

    [Theory]
    [InlineData("type", true)]
    [InlineData("type", false)]
    [InlineData("factory", true)]
    public async Task A_forward_sink_that_takes_the_queue_in_its_constructor_closes_no_cycle(string shape, bool pickupBeforeRouting)
    {
        var sp = Build(
            b =>
            {
                b.AddCoordinateMediation();
                if (pickupBeforeRouting)
                    b.AddMessagePickup();
                b.AddRoutingMediator();
                if (!pickupBeforeRouting)
                    b.AddMessagePickup();
            },
            before: s =>
            {
                if (shape == "type")
                    s.AddSingleton<IForwardSink, DiQueueSink>();
                else
                    s.AddSingleton<IForwardSink>(p => new DiQueueSink(p.GetRequiredService<MessagePickupQueue>()));
            });

        var (dispatcher, forward, queue, handler) = await Task.Run(() => (
                sp.GetRequiredService<ProtocolDispatcher>(),
                sp.GetRequiredService<ForwardHandler>(),
                sp.GetRequiredService<MessagePickupQueue>(),
                sp.GetRequiredService<MessagePickupHandler>()))
            .WaitAsync(Timeout);

        dispatcher.Should().NotBeNull();
        forward.Should().NotBeNull();
        handler.Should().NotBeNull();
        sp.GetRequiredService<IForwardSink>().Should().BeOfType<DiQueueSink>().Which.Queue.Should().BeSameAs(queue, "the sink writes to the queue the handler reads");
        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task The_full_mediator_composition_builds_resolves_and_disposes_without_hanging()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            b.AddCoordinateMediation();
            b.AddMessagePickup();
            b.AddRoutingMediator();
        });
        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var dispatcher = await Task.Run(() => sp.GetRequiredService<ProtocolDispatcher>()).WaitAsync(Timeout);

        dispatcher.Should().NotBeNull();
        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        registry.TryResolve(MessagePickup.DeliveryRequestType, out _).Should().BeTrue();
        registry.TryResolve(CoordinateMediation.MediateRequestType, out _).Should().BeTrue();
        registry.TryResolve("https://didcomm.org/routing/2.0/forward", out _).Should().BeTrue();
        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_message_store_that_takes_the_dispatcher_in_its_constructor_fails_fast_instead_of_hanging()
    {
        await using var sp = Build(
            b =>
            {
                b.AddCoordinateMediation();
                b.AddMessagePickup();
            },
            before: s => s.AddSingleton<IMessageStore, DiDispatcherTakingStore>());

        var act = () => Task.Run(() => sp.GetRequiredService<ProtocolDispatcher>()).WaitAsync(Timeout);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("circular dependency");
    }

    [Fact]
    public async Task A_message_store_that_resolves_the_dispatcher_lazily_is_fine()
    {
        await using var sp = Build(
            b =>
            {
                b.AddCoordinateMediation();
                b.AddMessagePickup();
            },
            before: s => s.AddSingleton<IMessageStore, DiLazyStore>());

        var dispatcher = await Task.Run(() => sp.GetRequiredService<ProtocolDispatcher>()).WaitAsync(Timeout);

        ((DiLazyStore)sp.GetRequiredService<IMessageStore>()).Dispatcher.Should().BeSameAs(dispatcher);
    }

    [Fact]
    public void A_forward_sink_that_takes_the_pickup_handler_is_refused_when_the_routing_mediator_is_registered()
    {
        // Why a sink takes the queue and not the handler: a handler in its constructor closes the cycle.
        var act = () => Build(
            b =>
            {
                b.AddCoordinateMediation();
                b.AddMessagePickup();
                b.AddRoutingMediator();
            },
            before: s => s.AddSingleton<IForwardSink, DiHandlerSink>());

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(nameof(MessagePickupHandler));
    }

    // ---- the delivery budget follows DidCommOptions.MaxReceiveBytes -----------------------

    [Fact]
    public void With_MaxDeliveryBytes_unset_the_budget_follows_MaxReceiveBytes()
    {
        const int smaller = 256 * 1024;
        using var atDefaults = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup();
        });
        using var followed = Build(b =>
        {
            b.Configure(o => o.MaxReceiveBytes = smaller);
            b.AddCoordinateMediation();
            b.AddMessagePickup();
        });
        using var configuredLater = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup();
            b.Configure(o => o.MaxReceiveBytes = smaller);
        });
        using var statedOutright = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup(o => o.MaxDeliveryBytes = smaller);
        });

        var atOneMebibyte = atDefaults.GetRequiredService<MessagePickupQueue>().MaxAdmissiblePayloadBytes;
        var atAQuarter = followed.GetRequiredService<MessagePickupQueue>().MaxAdmissiblePayloadBytes;

        atOneMebibyte.Should().BeInRange(500 * 1024, 600 * 1024 - 1, "at the 1 MiB default a 500 KiB message is admitted and a 600 KiB one is not (FR-PICK-03)");
        atAQuarter.Should().BeInRange(1, smaller * 9 / 16, "a delivered message is base64-encoded twice: at most 9/16 of the budget is payload");
        atAQuarter.Should().BeLessThan(atOneMebibyte);
        configuredLater.GetRequiredService<MessagePickupQueue>().MaxAdmissiblePayloadBytes.Should().Be(atAQuarter, "options are read when the queue is built, not when the method is called");
        statedOutright.GetRequiredService<MessagePickupQueue>().MaxAdmissiblePayloadBytes.Should().Be(atAQuarter, "null means exactly MaxReceiveBytes");
    }

    [Fact]
    public void The_largest_admissible_message_is_never_more_than_MaxReceiveBytes()
    {
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup(o => o.MaxDeliveryBytes = 8 * 1024 * 1024);
        });

        sp.GetRequiredService<MessagePickupQueue>().MaxAdmissiblePayloadBytes.Should().Be(1024 * 1024, "nothing larger than MaxReceiveBytes is ever retained (D7), however large a delivery may be");
    }

    [Fact]
    public async Task With_MaxDeliveryBytes_unset_a_live_delivery_is_bounded_by_MaxReceiveBytes()
    {
        const int smaller = 256 * 1024;
        await using var fixture = PickupFixture.Create(didComm: o => o.MaxReceiveBytes = smaller);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true, pack: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);

        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(fixture.Queue.MaxAdmissiblePayloadBytes))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);

        var attempt = socket.Attempts.Should().ContainSingle().Subject;
        attempt.MaxPackedBytes.Should().Be(smaller);
        attempt.PackedBytes.Should().BeLessThanOrEqualTo(smaller);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(32 * 1024)]
    public void A_MaxReceiveBytes_too_small_to_carry_any_delivery_fails_when_the_queue_is_first_built(int maxReceiveBytes)
    {
        // Registration cannot know: MaxDeliveryBytes is unset, and DidCommOptions are not final yet.
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup();
            b.Configure(o => o.MaxReceiveBytes = maxReceiveBytes);
        });

        FluentActions.Invoking(() => sp.GetRequiredService<MessagePickupQueue>()).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => sp.GetRequiredService<ProtocolDispatcher>()).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_MaxReceiveBytes_that_admits_nothing_fails_when_the_queue_is_first_built_even_with_a_stated_delivery_budget(int maxReceiveBytes)
    {
        using var sp = Build(b =>
        {
            b.Configure(o => o.MaxReceiveBytes = maxReceiveBytes);
            b.AddCoordinateMediation();
            b.AddMessagePickup(o => o.MaxDeliveryBytes = 1024 * 1024);
        });

        FluentActions.Invoking(() => sp.GetRequiredService<MessagePickupQueue>()).Should().Throw<ArgumentException>("a queue that can admit no message is a configuration error, not a silent drop of everything");
    }

    // ---- the startup log line -------------------------------------------------------------

    [Fact]
    public async Task The_queue_logs_its_sizing_once_at_Information_and_no_line_names_a_did()
    {
        await using var fixture = PickupFixture.Create(pickup: o =>
        {
            o.MaxDeliveryBytes = 64 * 1024;
            o.MaxDeliveryBatch = 4;
        });

        var queue = fixture.Queue;
        _ = fixture.Host.Dispatcher;
        _ = fixture.Host.Services.GetRequiredService<MessagePickupHandler>();
        _ = fixture.Queue;

        var line = MessagePickupLive.QueueLogs(fixture, LogLevel.Information).Should().ContainSingle("the queue is built once").Subject;
        line.State.Should().Contain("MaxDeliveryBytes", 64 * 1024).And.Contain("MaxDeliveryBatch", 4).And.Contain("MaxAdmissiblePayloadBytes", queue.MaxAdmissiblePayloadBytes);
        line.Formatted.Should().Contain(queue.MaxAdmissiblePayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Traffic with real DIDs adds no second sizing line, and no line at any level carries a DID or a queue id.
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(64));
        var queued = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(64), new PickupDeliveryOptions(AllowLive: false));
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(queue.MaxAdmissiblePayloadBytes + 1))).Outcome.Should().Be(PickupDeliveryOutcome.PayloadTooLarge);
        (await fixture.Queue.DeliverAsync(peer + "#key-1", PickupFixture.Payload(64))).Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle();
        (await MessagePickupLive.AckAsync(fixture, peer, mediator, pushed.MessageId!, queued.MessageId!)).MessageCount.Should().Be(0);

        MessagePickupLive.QueueLogs(fixture, LogLevel.Information).Should().ContainSingle();
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator, pushed.MessageId, queued.MessageId);
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>What #95's sink will be: a forward sink that hands payloads to the pickup queue.</summary>
    private sealed class DiQueueSink(MessagePickupQueue queue) : IForwardSink
    {
        public MessagePickupQueue Queue { get; } = queue;

        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>The L-017 mistake for a sink: a protocol handler in the constructor.</summary>
    private sealed class DiHandlerSink(MessagePickupHandler handler) : IForwardSink
    {
        public MessagePickupHandler Handler { get; } = handler;

        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private class DiDelegatingStore : IMessageStore
    {
        private readonly InMemoryMessageStore _inner = new();

        public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => _inner.EnqueueAsync(message, lease, now, ct);

        public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => _inner.LeaseAsync(request, ct);

        public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => _inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);

        public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => _inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);

        public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => _inner.ReleaseLeasesAsync(leaseOwner, ct);

        public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => _inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);

        public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => _inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);

        public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => _inner.PurgeGrantAsync(grantPeerDid, ct);

        public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => _inner.PurgeExpiredAsync(now, ct);
    }

    /// <summary>The L-017 mistake for a store: it needs the dispatcher at construction.</summary>
    private sealed class DiDispatcherTakingStore(ProtocolDispatcher dispatcher) : DiDelegatingStore
    {
        public ProtocolDispatcher Dispatcher { get; } = dispatcher;
    }

    /// <summary>The documented way: resolve it when first needed.</summary>
    private sealed class DiLazyStore(IServiceProvider services) : DiDelegatingStore
    {
        public ProtocolDispatcher Dispatcher => services.GetRequiredService<ProtocolDispatcher>();
    }
}
