using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

// L-014: a namespace import must not shadow the same-named static class.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// How <c>AddMessagePickup()</c> (and <c>AddMediator()</c>) register the Message Pickup 4.0
/// mediator role through the real container (FR-PICK-12, #100, plan DC-5): on by default next to
/// 3.0, each version's types resolving to its own typed singleton, both disclosed with
/// <c>roles: ["mediator"]</c>; off with <c>EnableV4 = false</c> (nothing resolves, nothing is
/// disclosed, a 4.0 request finds no handler); repeated calls; settings that disagree with what was
/// registered fail when either handler is first built, naming the setting and no DID; a 4.0-only
/// host registered by hand; and both versions over one queue.
/// </summary>
public sealed class MessagePickup40DiTests
{
    private const string Sentinel = "did:example:SENTINEL-routing";
    private const string V3 = "https://didcomm.org/messagepickup/3.0";
    private const string V4 = "https://didcomm.org/message-pickup/4.0";

    private static readonly string[] FourZeroTypes =
    {
        MessagePickup40.StatusRequestType, MessagePickup40.StatusType, MessagePickup40.DeliveryRequestType,
        MessagePickup40.DeliveryType, MessagePickup40.MessagesReceivedType, MessagePickup40.LiveDeliveryChangeType,
        MessagePickup40.ProblemReportType,
    };

    private static readonly string[] ThreeZeroTypes =
    {
        MessagePickup.StatusRequestType, MessagePickup.StatusType, MessagePickup.DeliveryRequestType,
        MessagePickup.DeliveryType, MessagePickup.MessagesReceivedType, MessagePickup.LiveDeliveryChangeType,
    };

    private static ServiceCollection Services(Action<DidCommBuilder> configure, Action<IServiceCollection>? after = null, InMemorySecretsResolver? secrets = null)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets ?? new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            configure(b);
        });
        after?.Invoke(services);
        return services;
    }

    private static ServiceProvider Build(Action<DidCommBuilder> configure, Action<IServiceCollection>? after = null, InMemorySecretsResolver? secrets = null)
        => Services(configure, after, secrets).BuildServiceProvider();

    // ---- on by default ------------------------------------------------------------------------

    [Fact]
    public void By_default_the_4_0_types_resolve_to_the_typed_4_0_singleton_and_the_3_0_types_to_the_3_0_one()
    {
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup();
        });

        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        var v4 = sp.GetRequiredService<MessagePickup40Handler>();
        var v3 = sp.GetRequiredService<MessagePickupHandler>();
        foreach (var type in FourZeroTypes)
        {
            registry.TryResolve(type, out var resolved).Should().BeTrue(type);
            resolved.Should().BeSameAs(v4, "the registry entry and the typed singleton are one instance");
        }
        foreach (var type in ThreeZeroTypes)
        {
            registry.TryResolve(type, out var resolved).Should().BeTrue(type);
            resolved.Should().BeSameAs(v3);
        }

        v4.ProtocolUri.Should().Be(V4);
        v4.Roles.Should().Equal(MessagePickup.MediatorRole);
        sp.GetServices<IProtocolHandler>().OfType<MessagePickup40Handler>().Should().ContainSingle().Which.Should().BeSameAs(v4);
        sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value.EnableV4.Should().BeTrue("4.0 is on unless switched off");
        new MessagePickupOptions().EnableV4.Should().BeTrue();
    }

    [Fact]
    public async Task By_default_discover_features_discloses_both_versions_with_roles_mediator()
    {
        var secrets = new InMemorySecretsResolver();
        await using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup();
        }, secrets: secrets);

        (await DisclosedPickupAsync(sp, secrets)).Should().BeEquivalentTo(new[] { V3, V4 });
    }

    [Fact]
    public async Task AddMediator_registers_and_discloses_both_versions_over_one_queue()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        await using var sp = Build(b => b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add(mediator.Did)), secrets: peers.Secrets);

        var handlers = sp.GetServices<IProtocolHandler>().ToArray();
        handlers.Should().ContainSingle(h => h is MessagePickupHandler);
        handlers.Should().ContainSingle(h => h is MessagePickup40Handler);
        sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value.EnableV4.Should().BeTrue();
        (await DisclosedPickupAsync(sp, peers.Secrets)).Should().BeEquivalentTo(new[] { V3, V4 });
    }

    // ---- off ----------------------------------------------------------------------------------

    [Fact]
    public async Task With_EnableV4_false_no_4_0_handler_resolves_and_discover_features_discloses_only_3_0()
    {
        var secrets = new InMemorySecretsResolver();
        await using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup(o => o.EnableV4 = false);
        }, secrets: secrets);

        sp.GetService<MessagePickup40Handler>().Should().BeNull();
        sp.GetServices<IProtocolHandler>().OfType<MessagePickup40Handler>().Should().BeEmpty();
        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        foreach (var type in FourZeroTypes)
            registry.TryResolve(type, out _).Should().BeFalse(type);
        foreach (var type in ThreeZeroTypes)
            registry.TryResolve(type, out _).Should().BeTrue("3.0 is unaffected: " + type);

        (await DisclosedPickupAsync(sp, secrets)).Should().BeEquivalentTo(new[] { V3 });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_4_0_status_request_is_answered_only_while_4_0_is_enabled(bool enableV4)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o => o.EnableV4 = enableV4);
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.QueueAsync(h.Alice, 10);
        h.ResetTraces();

        var outcome = await h.SendAsync(h.StatusRequest40(h.Alice), h.Http(h.Alice));

        if (enableV4)
        {
            outcome.Status40().MessageCount.Should().Be(1);
        }
        else
        {
            outcome.Result.Should().Be(DispatchResult.NoHandler, "with 4.0 off nothing claims the protocol");
            outcome.Reply.Should().BeNull();
            h.StoreTrace.Should().BeEmpty();
            h.MediationTrace.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task AddMediator_with_EnableV4_false_registers_no_4_0_handler_and_discloses_only_3_0()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        await using var sp = Build(b => b.AddMediator(o =>
        {
            o.CoordinateMediation.RoutingDids.Add(mediator.Did);
            o.Pickup.EnableV4 = false;
        }), secrets: peers.Secrets);

        sp.GetService<MessagePickup40Handler>().Should().BeNull();
        sp.GetServices<IProtocolHandler>().OfType<MessagePickup40Handler>().Should().BeEmpty();
        sp.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
        (await DisclosedPickupAsync(sp, peers.Secrets)).Should().BeEquivalentTo(new[] { V3 });
    }

    // ---- repeated calls -----------------------------------------------------------------------

    [Fact]
    public void Two_calls_that_leave_4_0_on_register_one_4_0_handler()
    {
        var services = Services(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup();
            b.AddMessagePickup(o => o.EnableV4 = true);
        });
        services.Count(d => d.ServiceType == typeof(MessagePickup40Handler)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(MessagePickupHandler)).Should().Be(1);

        using var sp = services.BuildServiceProvider();
        sp.GetServices<MessagePickup40Handler>().Should().ContainSingle();
        sp.GetServices<IProtocolHandler>().OfType<MessagePickup40Handler>().Should().ContainSingle();
        sp.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
    }

    [Fact]
    public void Two_calls_that_switch_4_0_off_register_none_and_the_dispatcher_builds()
    {
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddMessagePickup(o => o.EnableV4 = false);
            b.AddMessagePickup(o => o.EnableV4 = false);
        });

        sp.GetService<MessagePickup40Handler>().Should().BeNull();
        sp.GetServices<IProtocolHandler>().OfType<MessagePickup40Handler>().Should().BeEmpty();
        sp.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
        sp.GetRequiredService<MessagePickupHandler>().Should().NotBeNull();
    }

    // ---- settings that disagree with what was registered --------------------------------------

    public static TheoryData<string> Disagreements => new()
    {
        "registered on, then Configure sets false",
        "registered off, then Configure sets true",
        "a default call, then a call that sets false",
        "a call that sets false, then AddMediator without EnableV4",
    };

    private static ServiceProvider BuildDisagreement(string what) => what switch
    {
        "registered on, then Configure sets false" => Build(
            b =>
            {
                b.AddCoordinateMediation(o => o.RoutingDids.Add(Sentinel));
                b.AddMessagePickup();
            },
            after: s => s.Configure<MessagePickupOptions>(o => o.EnableV4 = false)),
        "registered off, then Configure sets true" => Build(
            b =>
            {
                b.AddCoordinateMediation(o => o.RoutingDids.Add(Sentinel));
                b.AddMessagePickup(o => o.EnableV4 = false);
            },
            after: s => s.Configure<MessagePickupOptions>(o => o.EnableV4 = true)),
        "a default call, then a call that sets false" => Build(b =>
        {
            b.AddCoordinateMediation(o => o.RoutingDids.Add(Sentinel));
            b.AddMessagePickup();
            b.AddMessagePickup(o => o.EnableV4 = false);
        }),
        "a call that sets false, then AddMediator without EnableV4" => Build(b =>
        {
            b.AddMessagePickup(o => o.EnableV4 = false);
            b.AddMediator(m => m.CoordinateMediation.RoutingDids.Add(Sentinel));
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(what)),
    };

    [Theory]
    [MemberData(nameof(Disagreements))]
    public void Settings_that_disagree_with_the_registration_fail_when_either_handler_is_first_built_naming_EnableV4_and_no_did(string what)
    {
        using var sp = BuildDisagreement(what);

        // The dispatcher builds every handler; each pickup handler's factory checks on its own.
        var resolutions = new List<(string Name, Func<object?> Resolve)>
        {
            ("dispatcher", () => sp.GetRequiredService<ProtocolDispatcher>()),
            ("3.0 handler", () => sp.GetRequiredService<MessagePickupHandler>()),
        };
        if (sp.GetRequiredService<IServiceProviderIsService>().IsService(typeof(MessagePickup40Handler)))
            resolutions.Add(("4.0 handler", () => sp.GetRequiredService<MessagePickup40Handler>()));

        foreach (var (name, resolve) in resolutions)
        {
            var refusal = FluentActions.Invoking(resolve).Should().Throw<InvalidOperationException>($"{what}: {name}").Which;
            refusal.Message.Should().Contain("EnableV4", name).And.NotContain("SENTINEL", name).And.NotContain("did:", name);
        }
    }

    [Fact]
    public void The_registered_off_disagreement_has_no_4_0_handler_to_resolve_and_the_registered_on_one_has()
    {
        // Pins which handlers the theory above exercises: both factories are covered.
        using var on = BuildDisagreement("registered on, then Configure sets false");
        using var off = BuildDisagreement("registered off, then Configure sets true");

        on.GetRequiredService<IServiceProviderIsService>().IsService(typeof(MessagePickup40Handler)).Should().BeTrue();
        off.GetRequiredService<IServiceProviderIsService>().IsService(typeof(MessagePickup40Handler)).Should().BeFalse();
    }

    [Fact]
    public async Task The_host_refuses_to_start_a_mediator_whose_EnableV4_setting_disagrees_with_its_registration()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(peers.Secrets);
            b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add(mediator.Did));
        });
        builder.Services.Configure<MessagePickupOptions>(o => o.EnableV4 = false);
        using var host = builder.Build();

        var failure = (await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<InvalidOperationException>()).Which;

        failure.Message.Should().Contain("EnableV4").And.NotContain(mediator.Did);
        host.Services.GetRequiredService<MediatorInvitationHolder>().PlaintextJson.Should().BeNull("a mediator that did not start issues no invitation");
    }

    // ---- 4.0 only, by hand --------------------------------------------------------------------

    [Fact]
    public async Task A_host_that_registers_the_4_0_handler_by_hand_over_its_own_queue_serves_and_discloses_4_0_only()
    {
        var secrets = new InMemorySecretsResolver();
        await using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.Services.AddSingleton<IMessageStore>(new InMemoryMessageStore());
            b.Services.AddSingleton<MessagePickupQueue>();
            b.AddProtocol<MessagePickup40Handler>();
        }, secrets: secrets);

        sp.GetService<MessagePickupHandler>().Should().BeNull();
        var handler = sp.GetRequiredService<MessagePickup40Handler>();
        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        registry.TryResolve(MessagePickup40.StatusRequestType, out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(handler);
        registry.TryResolve(MessagePickup.StatusRequestType, out _).Should().BeFalse("3.0 is not served");
        (await DisclosedPickupAsync(sp, secrets)).Should().BeEquivalentTo(new[] { V4 });

        // It works: the queue the host registered is the one it serves.
        var alice = await DiPeers.NewAsync(sp, secrets);
        var mediator = await DiPeers.NewAsync(sp, secrets);
        var mediation = sp.GetRequiredService<IMediationStore>();
        await mediation.GrantAsync(new MediationGrant(alice.Did, new[] { mediator.Did }), CancellationToken.None);
        await mediation.UpdateRecipientsAsync(alice.Did, new[] { new RecipientUpdate(alice.Did, RecipientUpdateAction.Add) }, CancellationToken.None);
        (await sp.GetRequiredService<MessagePickupQueue>().DeliverAsync(alice.Did, new byte[] { 1, 2, 3 })).Outcome.Should().Be(PickupDeliveryOutcome.Queued);

        var outcome = await DispatchAsync(sp, MessagePickup40.CreateStatusRequest(alice.Did, mediator.Did), mediator.Did);

        outcome.Handler.Should().BeSameAs(handler);
        outcome.Status40().MessageCount.Should().Be(1);
        outcome.Reply!.Type.Should().Be(MessagePickup40.StatusType);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task<DispatchOutcome> DispatchAsync(IServiceProvider sp, Message message, string to)
    {
        var client = sp.GetRequiredService<DidCommClient>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }, From: message.From));
        return await sp.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, options);
    }

    /// <summary>The pickup PIURIs Discover Features discloses, each required to carry roles <c>["mediator"]</c>.</summary>
    private static async Task<IReadOnlyList<string>> DisclosedPickupAsync(IServiceProvider sp, InMemorySecretsResolver secrets)
    {
        var alice = await DiPeers.NewAsync(sp, secrets);
        var mediator = await DiPeers.NewAsync(sp, secrets);

        // The two versions spell the protocol name differently, so one prefix query cannot find both.
        var query = DiscoverFeaturesApi.CreateQuery(alice.Did, mediator.Did,
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "https://didcomm.org/messagepickup/*" },
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "https://didcomm.org/message-pickup/*" });
        var outcome = await DispatchAsync(sp, query, mediator.Did);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        var disclosures = DiscoverFeaturesApi.ReadDisclosures(outcome.Reply!);
        disclosures.Should().OnlyContain(d => d.Roles != null && d.Roles.SequenceEqual(new[] { MessagePickup.MediatorRole }), "every Message Pickup version is disclosed with roles [\"mediator\"]");
        return disclosures.Select(d => d.Id).ToArray();
    }
}
