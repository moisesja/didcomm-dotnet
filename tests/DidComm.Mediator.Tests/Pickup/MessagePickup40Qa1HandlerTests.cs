using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// QA-1 for #100 (Message Pickup 4.0), the mediator role: the boundaries of the times a 4.0
/// <c>status</c> writes (UTC epoch milliseconds, L39, L164; never negative, because no reader of
/// this library accepts a negative time), and the start-up refusal of a mediator whose
/// <see cref="MessagePickupOptions.EnableV4"/> setting disagrees with its registration in the
/// direction the existing host test does not cover (registered off, then switched on).
/// </summary>
public sealed class MessagePickup40Qa1HandlerTests
{
    public static TheoryData<string, long, long, long, long> TimeBoundaries => new()
    {
        // what the store reports (ms since the epoch)    → what the status says (ms)
        { "the epoch itself", 0, 0, 0, 0 },
        { "before the epoch: written as the epoch, not refused or negative", -1_500, 999, 0, 999 },
        { "sub-second instants are not rounded", 1_739_420_343_001, 1_739_420_343_823, 1_739_420_343_001, 1_739_420_343_823 },
        { "the last instant a DateTimeOffset holds", 253_402_300_799_999, 253_402_300_799_999, 253_402_300_799_999, 253_402_300_799_999 },
    };

    [Theory]
    [MemberData(nameof(TimeBoundaries))]
    public async Task A_4_0_status_writes_the_stores_instants_in_milliseconds_and_never_a_negative_one(string because, long oldestAt, long newestAt, long oldest, long newest)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        h.Store.OnGetStatus = (_, _, _, _) => new ValueTask<QueueStatus>(new QueueStatus(2, 10, DateTimeOffset.FromUnixTimeMilliseconds(oldestAt), DateTimeOffset.FromUnixTimeMilliseconds(newestAt)));

        var request = h.StatusRequest40(h.Alice);
        var outcome = await h.SendAsync(request, h.Http(h.Alice));

        outcome.ShouldBeStatus40(request, h.Mediator, h.Alice, 2, 10, live: false, times: new MessagePickup40Times(newest, oldest));
        outcome.Status40().ToPickupStatus().Should().Be(new PickupStatus(2, null, null, newest / 1000, oldest / 1000, 10, false), because);
    }

    [Fact]
    public async Task The_host_refuses_to_start_a_mediator_registered_without_4_0_whose_settings_then_switch_it_on()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(peers.Secrets);
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(mediator.Did);
                o.Pickup.EnableV4 = false;
            });
        });
        builder.Services.Configure<MessagePickupOptions>(o => o.EnableV4 = true);
        using var host = builder.Build();

        var failure = (await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<InvalidOperationException>()).Which;

        failure.Message.Should().Contain("EnableV4").And.NotContain(mediator.Did).And.NotContain("did:");
        host.Services.GetService<MessagePickup40Handler>().Should().BeNull("AddMediator read EnableV4 = false and registered no 4.0 handler");
        host.Services.GetRequiredService<MediatorInvitationHolder>().PlaintextJson.Should().BeNull("a mediator that did not start issues no invitation");
    }
}
