using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Xunit;
using static DidComm.Tests.Facade.MediatedClientTestWorld;

namespace DidComm.Tests.Facade;

/// <summary>
/// #96 design §3.1 item 3 (D-3, L-041): <see cref="DidCommClient.ResolveDirectEndpointAsync"/>
/// selects the endpoint exactly as <see cref="DidCommClient.SendAsync"/> selects the endpoint of a
/// direct return-route send, and refuses what it refuses with the same exception types. Each case
/// runs both over the same service entries — <c>SendAsync(return_route: all)</c> without an override
/// through a capturing router — and compares what they chose or how they failed.
/// </summary>
public sealed class ResolveDirectEndpointTests
{
    private const string Https = "https://mediator.example/didcomm";
    private const string Wss = "wss://mediator.example/ws";

    private static DidCommServiceInfo Entry(string uri, params string[] routingKeys) => new(uri, routingKeys, Array.Empty<string>());

    public static TheoryData<string, DidCommServiceInfo[]> Chosen => new()
    {
        { "https", new[] { Entry(Https) } },
        { "wss", new[] { Entry(Wss) } },
        { "ws first, https second", new[] { Entry("ws://mediator.example/ws"), Entry(Https) } },
        { "https first, wss second", new[] { Entry(Https), Entry(Wss) } },
        { "first entry wins over a routed second", new[] { Entry(Https), Entry(Wss, Carol + "#x1") } },
    };

    [Theory]
    [MemberData(nameof(Chosen))]
    public async Task It_chooses_the_entry_SendAsync_chooses(string name, DidCommServiceInfo[] entries)
    {
        var world = new MediatedClientTestWorld((Bob, entries));
        var resolver = new MediatedClientTestWorld.StubServiceResolver((Bob, entries));

        var resolved = await world.BobClient(resolver).ResolveDirectEndpointAsync(Bob, CancellationToken.None);
        await world.BobClient().SendAsync(Request(), new SendOptions(new[] { Bob }, From: Carol) { ReturnRoute = ReturnRouteMode.All });

        resolved.Should().Be(world.Router.Requests.Single().Endpoint, name);
        resolved.Should().Be(new Uri(entries[0].Uri));
        resolver.Resolved.Should().Equal(new[] { Bob }, "one read of the service entries");
    }

    [Fact]
    public async Task Sending_with_the_resolved_endpoint_as_the_override_reaches_the_same_endpoint_with_the_same_identities()
    {
        var world = new MediatedClientTestWorld((Bob, new[] { Entry(Https) }));
        var client = world.BobClient();
        var resolved = await client.ResolveDirectEndpointAsync(Bob, CancellationToken.None);

        await client.SendAsync(Request(), new SendOptions(new[] { Bob }, From: Carol) { ReturnRoute = ReturnRouteMode.All });
        await client.SendAsync(Request(), new SendOptions(new[] { Bob }, From: Carol, ServiceEndpointOverride: resolved) { ReturnRoute = ReturnRouteMode.All });

        var requests = world.Router.Requests.ToArray();
        requests[1].Endpoint.Should().Be(requests[0].Endpoint);
        requests[1].PeerDid.Should().Be(requests[0].PeerDid);
        requests[1].LocalDid.Should().Be(requests[0].LocalDid);
        requests[1].ReturnRoute.Should().Be(requests[0].ReturnRoute);
        requests[1].MaxResponseBytes.Should().Be(requests[0].MaxResponseBytes);
    }

    public static TheoryData<string, DidCommServiceInfo[], Type> Refused => new()
    {
        { "routing keys", new[] { Entry(Https, Carol + "#x1") }, typeof(InvalidOperationException) },
        { "bare-DID routing key", new[] { Entry(Https, Carol) }, typeof(InvalidOperationException) },
        { "mediator as the endpoint", new[] { Entry(Mediator) }, typeof(InvalidOperationException) },
        { "queue URI", new[] { Entry(QueueTransport.Uri) }, typeof(TransportException) },
        { "relative URI", new[] { Entry("inbox/didcomm") }, typeof(TransportException) },
        { "private address", new[] { Entry("https://10.0.0.7/didcomm") }, typeof(TransportException) },
        { "loopback address", new[] { Entry("http://127.0.0.1:8080/") }, typeof(TransportException) },
        { "no entries", Array.Empty<DidCommServiceInfo>(), typeof(DidResolutionException) },
        { "routed first, direct second", new[] { Entry(Https, Carol + "#x1"), Entry(Wss) }, typeof(InvalidOperationException) },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task It_refuses_what_SendAsync_refuses_with_the_same_exception_type(string name, DidCommServiceInfo[] entries, Type expected)
    {
        var world = new MediatedClientTestWorld((Bob, entries));

        var resolve = await Capture(() => world.BobClient().ResolveDirectEndpointAsync(Bob, CancellationToken.None));
        var send = await Capture(() => world.BobClient().SendAsync(Request(), new SendOptions(new[] { Bob }, From: Carol) { ReturnRoute = ReturnRouteMode.All }));

        resolve.Should().BeOfType(expected, name);
        send.Should().BeOfType(expected, "SendAsync refuses the same entry the same way");
        world.Router.Requests.Should().BeEmpty("nothing is sent");
        if (expected == typeof(InvalidOperationException))
            resolve!.Message.Should().Be(DidCommClient.RoutedReturnRouteRefusal).And.Be(send!.Message);
        if (name == "queue URI")
            resolve!.Message.Should().Be(send!.Message);
        if (name is "private address" or "loopback address")
            resolve!.Message.Should().Be(send!.Message, "the same guard");
    }

    [Fact]
    public async Task A_relative_URI_is_not_quoted()
    {
        var world = new MediatedClientTestWorld((Bob, new[] { Entry("inbox-secret-path") }));

        var act = () => world.BobClient().ResolveDirectEndpointAsync(Bob, CancellationToken.None);

        (await act.Should().ThrowAsync<TransportException>()).Which.Message.Should().NotContain("inbox-secret-path");
    }

    [Fact]
    public async Task Without_a_service_resolver_both_refuse_with_the_same_text()
    {
        var world = new MediatedClientTestWorld();
        var client = world.BobClient(noResolver: true);

        var resolve = await Capture(() => client.ResolveDirectEndpointAsync(Bob, CancellationToken.None));
        var send = await Capture(() => client.SendAsync(Request(), new SendOptions(new[] { Bob }, From: Carol) { ReturnRoute = ReturnRouteMode.All }));

        resolve.Should().BeOfType<InvalidOperationException>();
        send.Should().BeOfType<InvalidOperationException>();
        resolve!.Message.Should().Be(send!.Message);
    }

    [Fact]
    public async Task A_did_web_mediator_is_refused_by_both_before_any_service_read()
    {
        const string web = "did:web:mediator.example";
        var resolver = new MediatedClientTestWorld.StubServiceResolver((web, new[] { Entry(Https) }));
        var world = new MediatedClientTestWorld();

        var resolve = await Capture(() => world.BobClient(resolver).ResolveDirectEndpointAsync(web, CancellationToken.None));
        var send = await Capture(() => world.BobClient(resolver).SendAsync(Request(web), new SendOptions(new[] { web }, From: Carol) { ReturnRoute = ReturnRouteMode.All }));

        resolve.Should().BeOfType<UnsupportedDidMethodException>();
        send.Should().BeOfType<UnsupportedDidMethodException>();
        resolver.Resolved.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("did:example:bob/path", typeof(ArgumentException))]
    [InlineData("did:example:bob#", typeof(ArgumentException))]
    [InlineData("not a did", typeof(DidResolutionException))]
    public async Task A_malformed_DID_is_refused_as_SendAsync_refuses_it_before_any_read(string did, Type expected)
    {
        var world = new MediatedClientTestWorld((Bob, new[] { Entry(Https) }));

        var resolve = await Capture(() => world.BobClient().ResolveDirectEndpointAsync(did, CancellationToken.None));
        var send = await Capture(() => world.BobClient().SendAsync(Request(), new SendOptions(new[] { did }, From: Carol) { ReturnRoute = ReturnRouteMode.All }));

        resolve.Should().BeOfType(expected);
        send.Should().BeOfType(expected);
        world.Services.Resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_DID_URL_with_a_fragment_resolves_the_DIDs_entries_as_SendAsync_does()
    {
        var world = new MediatedClientTestWorld((Bob, new[] { Entry(Https) }));

        var resolved = await world.BobClient().ResolveDirectEndpointAsync(Bob + "#x1", CancellationToken.None);

        resolved.Should().Be(new Uri(Https));
        world.Services.Resolved.Should().Equal(Bob);
    }

    [Fact]
    public async Task With_the_guard_off_a_private_address_is_returned()
    {
        var world = new MediatedClientTestWorld((Bob, new[] { Entry("http://10.0.0.7/didcomm") }));
        world.BobOptions.OutboundEndpointPolicy = new OutboundEndpointPolicy { BlockPrivateNetworks = false };

        var resolved = await world.BobClient().ResolveDirectEndpointAsync(Bob, CancellationToken.None);

        resolved.Should().Be(new Uri("http://10.0.0.7/didcomm"));
    }

    private static Message Request(string to = Bob)
        => new MessageBuilder().WithType("https://didcomm.org/messagepickup/3.0/status-request").WithFrom(Carol).WithTo(to).WithBody(new System.Text.Json.Nodes.JsonObject()).Build();

    private static async Task<Exception?> Capture(Func<Task> act)
    {
        try
        {
            await act();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
