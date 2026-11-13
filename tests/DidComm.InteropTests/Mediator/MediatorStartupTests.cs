using System.Text.Json;
using AwesomeAssertions;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// What a mediator host refuses at start-up — <c>app.StartAsync()</c> of the real
/// <c>WebApplication</c>, before it serves anything (JC-9): a delivery budget the receive limit
/// cannot carry; a routing DID senders could not reach (FR-MED-07: its first
/// <c>DIDCommMessaging</c> service must have an absolute http(s) / ws(s) <c>uri</c>, never a DID, and
/// the mediator must hold one of its key-agreement secrets); a forward sink registered after
/// <c>AddMediator()</c>. The refusals never name the DID.
/// </summary>
public sealed class MediatorStartupTests
{
    [Fact]
    public async Task A_receive_limit_too_small_for_one_pickup_delivery_fails_start_up()
        => await StartShouldFailAsync<ArgumentException>(o => o.MaxReceiveBytes = 16 * 1024);

    [Fact]
    public async Task A_routing_DID_whose_service_uri_is_a_DID_fails_start_up_without_naming_it()
    {
        var other = await MediatorCast.MintAsync();
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec(other.Did, Accept: new[] { "didcomm/v2" }));
        await StartShouldFailAsync<InvalidOperationException>(o => o.Identity = identity, identity.Did, other.Did);
    }

    [Fact]
    public async Task A_routing_DID_without_a_service_fails_start_up_without_naming_it()
    {
        var identity = await MediatorCast.MintAsync();
        await StartShouldFailAsync<InvalidOperationException>(o => o.Identity = identity, identity.Did);
    }

    [Fact]
    public async Task A_routing_DID_with_an_unsupported_scheme_fails_start_up_without_naming_it()
    {
        var identity = await MediatorCast.MintWithServiceEntryAsync(RawDidCommService("ftp://mediator.test/"));
        await StartShouldFailAsync<InvalidOperationException>(o => o.Identity = identity, identity.Did);
    }

    [Fact]
    public async Task A_routing_DID_whose_key_agreement_secret_is_not_held_fails_start_up_without_naming_it()
    {
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec("http://mediator.test/", Accept: new[] { "didcomm/v2" }));
        await StartShouldFailAsync<InvalidOperationException>(o =>
        {
            o.Identity = identity;
            o.HoldSecrets = false;
        }, identity.Did);
    }

    [Fact]
    public async Task A_forward_sink_registered_after_AddMediator_fails_start_up()
        => await StartShouldFailAsync<InvalidOperationException>(o =>
            o.PostConfigureServices = services => services.AddSingleton<IForwardSink>(new SilentSink()));

    [Theory]
    [InlineData("http://mediator.test/")]
    [InlineData("https://mediator.test/didcomm")]
    [InlineData("ws://mediator.test/")]
    [InlineData("wss://mediator.test/didcomm")]
    public async Task A_routing_DID_with_an_http_or_ws_service_starts_and_publishes_its_invitation(string uri)
    {
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec(uri, Accept: new[] { "didcomm/v2" }));
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Identity = identity);

        mediator.Invitation.PlaintextJson.Should().NotBeNullOrEmpty();
        mediator.Logs.Entries.Should().Contain(e => e.StartsWith("Mediator ready", StringComparison.Ordinal));
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    private static async Task StartShouldFailAsync<TException>(Action<MediatorTestServerOptions> configure, params string[] neverNamed)
        where TException : Exception
    {
        await using var mediator = await MediatorTestServer.CreateAsync(configure);

        var act = () => mediator.App.StartAsync();

        var refused = (await act.Should().ThrowAsync<TException>()).Which;
        foreach (var value in neverNamed)
            refused.ToString().Should().NotContain(value, "a start-up refusal never names the DID");
        mediator.Invitation.PlaintextJson.Should().BeNull("nothing is advertised by a mediator that did not start");
        mediator.LeakingLogLines(neverNamed).Should().BeEmpty();
    }

    /// <summary>A <c>DIDCommMessaging</c> service entry with exactly this <c>uri</c>, unchecked (the library's builder refuses an ftp URI).</summary>
    private static NetDid.Core.Model.Service RawDidCommService(string uri) => new()
    {
        Id = "#didcomm",
        Type = "DIDCommMessaging",
        ServiceEndpoint = NetDid.Core.Model.ServiceEndpointValue.FromMap(new Dictionary<string, JsonElement>
        {
            ["uri"] = JsonSerializer.SerializeToElement(uri),
        }),
    };

    private sealed class SilentSink : IForwardSink
    {
        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct) => Task.CompletedTask;
    }
}
