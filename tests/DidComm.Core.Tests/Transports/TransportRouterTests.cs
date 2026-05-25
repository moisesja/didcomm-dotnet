using DidComm.Exceptions;
using DidComm.Transports;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Transports;

/// <summary>
/// Phase 5 Checkpoint A — covers <see cref="TransportRouter"/> dispatch + the FR-TRN-01
/// no-transport-handles-scheme error path. The HTTP / WS specifics land in the InteropTests
/// project where real <see cref="System.Net.Http.HttpMessageHandler"/> + TestServer fixtures
/// live.
/// </summary>
public sealed class TransportRouterTests
{
    [Fact]
    public async Task SendAsync_dispatches_by_scheme()
    {
        var http = new FakeTransport("https", expected: new Uri("https://agents.r.us/inbox"));
        var ws = new FakeTransport("wss", expected: new Uri("wss://agents.r.us/socket"));
        var router = new TransportRouter(new IDidCommTransport[] { http, ws });

        var result = await router.SendAsync(NewRequest("https://agents.r.us/inbox"), default);

        result.Accepted.Should().BeTrue();
        http.CallCount.Should().Be(1);
        ws.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_dispatches_to_wss_transport_when_only_wss_matches()
    {
        var http = new FakeTransport("https", expected: new Uri("https://x/y"));
        var ws = new FakeTransport("wss", expected: new Uri("wss://agents.r.us/socket"));
        var router = new TransportRouter(new IDidCommTransport[] { http, ws });

        await router.SendAsync(NewRequest("wss://agents.r.us/socket"), default);

        http.CallCount.Should().Be(0);
        ws.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_throws_TransportException_when_no_transport_handles_scheme()
    {
        var router = new TransportRouter(new IDidCommTransport[]
        {
            new FakeTransport("https", expected: new Uri("https://x/y"))
        });

        var act = async () => await router.SendAsync(NewRequest("libp2p://peer/abc"), default);

        var ex = (await act.Should().ThrowAsync<TransportException>()).Which;
        ex.Message.Should().Contain("libp2p");
        ex.Scheme.Should().Be("libp2p");
        ex.HttpStatusCode.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_picks_transport_case_insensitively()
    {
        // The router itself just calls CanHandle; the case-insensitive comparison is the
        // transport's responsibility. This test pairs with FakeTransport's case-insensitive
        // CanHandle to nail down the contract.
        var http = new FakeTransport("https", expected: new Uri("HTTPS://A.B/c"));
        var router = new TransportRouter(new IDidCommTransport[] { http });

        await router.SendAsync(NewRequest("HTTPS://A.B/c"), default);

        http.CallCount.Should().Be(1);
    }

    [Fact]
    public void Ctor_throws_on_null_transports()
    {
        var act = () => new TransportRouter(transports: null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task SendAsync_throws_on_null_request()
    {
        var router = new TransportRouter(Array.Empty<IDidCommTransport>());
        var act = async () => await router.SendAsync(request: null!, default);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static TransportRequest NewRequest(string uri) =>
        new(new Uri(uri), new byte[] { 1, 2, 3 }, "application/didcomm-encrypted+json");

    private sealed class FakeTransport : IDidCommTransport
    {
        private readonly Uri _expected;
        public FakeTransport(string scheme, Uri expected) { Scheme = scheme; _expected = expected; }
        public string Scheme { get; }
        public int CallCount { get; private set; }

        public bool CanHandle(Uri endpoint) =>
            string.Equals(endpoint.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            request.Endpoint.AbsoluteUri.Should().Be(_expected.AbsoluteUri);
            CallCount++;
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
        }
    }
}
