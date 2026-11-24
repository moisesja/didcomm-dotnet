using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Tests.Facade;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Xunit;
using static DidComm.Tests.Facade.MediatedClientTestWorld;

namespace DidComm.Tests.Transports;

/// <summary>
/// #110 (the Core guard, L-075): a destination host name the name lookup refuses outright (longer
/// than 255 characters) is a <see cref="TransportException"/> from
/// <see cref="OutboundEndpointGuard.Validate"/>, like a name that does not resolve, on every path that
/// checks an endpoint: <see cref="DidCommClient.SendAsync"/> (an endpoint from a DID document),
/// <see cref="DidCommClient.SendPackedAsync"/> and the recipient client's mediated send
/// (<see cref="DidCommClient.ResolveDirectEndpointAsync"/>). It used to escape as an
/// <see cref="ArgumentOutOfRangeException"/>. The refusal's text names no host and no URI. No network:
/// the lookup refuses such a name before it asks a resolver.
/// </summary>
public sealed class OutboundEndpointGuardLookupRefusalTests
{
    /// <summary>A host of 312 characters, each label within the 63-character limit, so it is a valid URI host.</summary>
    private static readonly string LongHost = string.Join('.', Enumerable.Repeat(new string('a', 60), 5)) + ".example";

    private static readonly Uri LongEndpoint = new($"https://{LongHost}/didcomm");

    private static readonly byte[] Packed = Encoding.UTF8.GetBytes("{\"protected\":\"x\",\"recipients\":[],\"iv\":\"x\",\"ciphertext\":\"x\",\"tag\":\"x\"}");

    public static TheoryData<string> Hosts => new() { LongHost, "a\u200Db.example" };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Validate_refuses_a_name_the_lookup_cannot_take_with_a_TransportException_that_names_no_host(string host)
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy());

        var caught = Record.Exception(() => guard.Validate(new Uri($"https://{host}/didcomm")));

        AssertRefusal(caught, "https", host);
    }

    [Fact]
    public void Validate_wraps_any_argument_error_of_the_lookup()
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy(), _ => throw new ArgumentException("not a name"));

        var caught = Record.Exception(() => guard.Validate(new Uri("wss://peer.example/ws")));

        caught.Should().BeOfType<TransportException>().Which.Scheme.Should().Be("wss");
        caught!.InnerException.Should().BeOfType<ArgumentException>();
    }

    [Fact]
    public void Validate_does_not_look_the_name_up_when_the_policy_says_so()
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy { ResolveDnsNames = false });

        var caught = Record.Exception(() => guard.Validate(LongEndpoint));

        caught.Should().BeNull("only IP literals are classified without name lookups");
    }

    [Fact]
    public async Task SendPackedAsync_refuses_it_before_the_router()
    {
        var router = new MediatedClientTestWorld.CapturingRouter();
        var client = new DidCommClient(new NoSecrets(), new NoKeys(), new NoServices(), router, new DidCommOptions());

        var caught = await Record.ExceptionAsync(() => client.SendPackedAsync(Packed, LongEndpoint, MediaTypes.Encrypted));

        AssertRefusal(caught, "https", LongHost);
        router.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task SendAsync_refuses_it_when_the_recipients_DID_document_names_it(string host)
    {
        var world = new MediatedClientTestWorld((Bob, new[] { Entry($"https://{host}/didcomm") }));
        world.BobOptions.OutboundEndpointPolicy = new OutboundEndpointPolicy();

        var caught = await Record.ExceptionAsync(() => world.BobClient().SendAsync(Request(), new SendOptions(new[] { Bob }, From: Carol)));

        AssertRefusal(caught, "https", host);
        world.Router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_mediated_sends_endpoint_resolution_refuses_it()
    {
        var world = new MediatedClientTestWorld((Bob, new[] { Entry(LongEndpoint.ToString()) }));
        world.BobOptions.OutboundEndpointPolicy = new OutboundEndpointPolicy();

        var resolve = await Record.ExceptionAsync(() => world.BobClient().ResolveDirectEndpointAsync(Bob, CancellationToken.None));
        var send = await Record.ExceptionAsync(() => world.BobClient().SendAsync(Request(), new SendOptions(new[] { Bob }, From: Carol) { ReturnRoute = ReturnRouteMode.All }));

        AssertRefusal(resolve, "https", LongHost);
        AssertRefusal(send, "https", LongHost);
        resolve!.Message.Should().Be(send!.Message, "the same guard as SendAsync");
        world.Router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_and_SendPackedAsync_end_with_their_token_while_the_name_is_looked_up()
    {
        var lookups = new SemaphoreSlim(0);
        var policy = new OutboundEndpointPolicy
        {
            ResolveDnsAsync = (_, _) =>
            {
                lookups.Release();
                return new TaskCompletionSource<System.Net.IPAddress[]>().Task;
            },
        };
        var world = new MediatedClientTestWorld((Bob, new[] { Entry("https://bob.example/didcomm") }));
        world.BobOptions.OutboundEndpointPolicy = policy;
        var packedClient = new DidCommClient(new NoSecrets(), new NoKeys(), new NoServices(), world.Router, new DidCommOptions { OutboundEndpointPolicy = policy });
        using var sendToken = new CancellationTokenSource();
        using var packedToken = new CancellationTokenSource();

        var send = world.BobClient().SendAsync(Request(), new SendOptions(new[] { Bob }, From: Carol), sendToken.Token);
        (await lookups.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        await sendToken.CancelAsync();
        var sent = await Record.ExceptionAsync(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
        var packed = packedClient.SendPackedAsync(Packed, new Uri("https://bob.example/didcomm"), MediaTypes.Encrypted, packedToken.Token);
        (await lookups.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        await packedToken.CancelAsync();
        var sentPacked = await Record.ExceptionAsync(() => packed.WaitAsync(TimeSpan.FromSeconds(10)));

        sent.Should().BeAssignableTo<OperationCanceledException>().Which.CancellationToken.Should().Be(sendToken.Token);
        sentPacked.Should().BeAssignableTo<OperationCanceledException>().Which.CancellationToken.Should().Be(packedToken.Token);
        world.Router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveDirectEndpointAsync_ends_with_its_token_while_the_name_is_looked_up()
    {
        var lookups = new SemaphoreSlim(0);
        var world = new MediatedClientTestWorld((Bob, new[] { Entry("https://bob.example/didcomm") }));
        world.BobOptions.OutboundEndpointPolicy = new OutboundEndpointPolicy
        {
            ResolveDnsAsync = (_, _) =>
            {
                lookups.Release();
                return new TaskCompletionSource<System.Net.IPAddress[]>().Task;
            },
        };
        using var token = new CancellationTokenSource();

        var resolve = world.BobClient().ResolveDirectEndpointAsync(Bob, token.Token);
        (await lookups.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        await token.CancelAsync();
        var caught = await Record.ExceptionAsync(() => resolve.WaitAsync(TimeSpan.FromSeconds(10)));

        caught.Should().BeAssignableTo<OperationCanceledException>().Which.CancellationToken.Should().Be(token.Token);
        world.Router.Requests.Should().BeEmpty();
    }

    /// <summary>A <see cref="TransportException"/> with no status, the endpoint's scheme, and no host or URI anywhere in its text or its inner exceptions.</summary>
    private static void AssertRefusal(Exception? caught, string scheme, string host)
    {
        var refusal = caught.Should().BeOfType<TransportException>().Which;
        refusal.HttpStatusCode.Should().BeNull();
        refusal.Scheme.Should().Be(scheme);
        refusal.ToString().Should().NotContain(host.Split('.')[0], "the refusal names no host").And.NotContain("://", "nor a URI");
    }

    private static DidCommServiceInfo Entry(string uri) => new(uri, Array.Empty<string>(), Array.Empty<string>());

    private static Message Request()
        => new MessageBuilder().WithType("https://didcomm.org/messagepickup/3.0/status-request").WithFrom(Carol).WithTo(Bob).WithBody(new JsonObject()).Build();

    private sealed class NoServices : IServiceEndpointResolver
    {
        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default)
            => throw new InvalidOperationException("SendPackedAsync must not resolve anything.");
    }

    private sealed class NoSecrets : ISecretsResolver
    {
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default) => throw new InvalidOperationException("SendPackedAsync must not look up a secret.");

        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => throw new InvalidOperationException("SendPackedAsync must not look up a secret.");
    }

    private sealed class NoKeys : IDidKeyService
    {
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => throw new InvalidOperationException("SendPackedAsync must not resolve a DID.");

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => throw new InvalidOperationException("SendPackedAsync must not resolve a DID.");

        public void RejectUnsupportedMethod(string did) { }
    }
}
