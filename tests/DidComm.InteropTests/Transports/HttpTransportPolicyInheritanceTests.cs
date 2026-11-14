using System.Net.Http;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #27 (PR #41 review item 2) — proves the HTTP transport's SSRF policy is the single source of
/// truth. With no transport-level policy set, the connect-time guard must inherit
/// <see cref="DidCommOptions.OutboundEndpointPolicy"/> — the symmetric coverage to the WebSocket
/// inherit test. These exercise the REAL <c>SocketsHttpHandler.ConnectCallback</c> (the stubbed-handler
/// tests in <c>HttpTransportSendTests</c> bypass it), so each performs a genuine loopback connect to a
/// closed port; no server is involved.
/// </summary>
public sealed class HttpTransportPolicyInheritanceTests
{
    [Fact]
    public async Task ConnectCallback_inherits_core_OutboundEndpointPolicy_when_transport_policy_unset()
    {
        // Core policy does NOT block private networks; the transport leaves its own policy null. So the
        // ConnectCallback must inherit the permissive core policy: localhost passes the SSRF guard and the
        // connect fails for a DIFFERENT reason (port 9 refused), NOT "private or reserved". With the
        // default (blocking) policy, localhost would be rejected as "private or reserved" (see the
        // complement test below, which proves this assertion isn't vacuous).
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            // AddDidComm requires a secrets resolver + key service to be registered; neither is exercised
            // here (we resolve only the transport), they just satisfy the eager registration check.
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 0;           // connect-refused fails fast; no backoff
                o.OutboundEndpointPolicy = null;  // ← inherit the core policy below
            });
        });
        services.Configure<DidCommOptions>(o =>
            o.OutboundEndpointPolicy = new OutboundEndpointPolicy { BlockPrivateNetworks = false });

        await using var sp = services.BuildServiceProvider();
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var request = new TransportRequest(
            new Uri("http://localhost:9/"), new byte[] { 1 }, "application/didcomm-encrypted+json");

        // The HTTP transport propagates connect-time failures as HttpRequestException (it does not rewrap
        // them; the SSRF guard's TransportException, when it fires, is the inner exception). Here the guard
        // does NOT fire — the connect proceeds and is refused — so the chain must not mention SSRF.
        var ex = (await ((Func<Task>)(() => transport.SendAsync(request, default)))
            .Should().ThrowAsync<HttpRequestException>()).Which;
        FlattenMessages(ex).Should().NotContain("private or reserved",
            "the permissive core policy was inherited, so localhost is not SSRF-blocked at connect time");
    }

    [Fact]
    public async Task ConnectCallback_uses_default_blocking_policy_when_no_policy_is_configured_anywhere()
    {
        // The complement: with no policy set on the transport OR the core options, the default (block
        // private networks) governs, so a loopback target IS refused at connect time. This proves the
        // inherit test above passes for the right reason (the permissive core policy was honored), not
        // because the ConnectCallback guard silently never runs — and it adds the previously-missing
        // baseline coverage that the real HTTP ConnectCallback blocks a private host by default.
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 0;
            });
        });

        await using var sp = services.BuildServiceProvider();
        var transport = sp.GetRequiredService<IDidCommTransport>();

        var request = new TransportRequest(
            new Uri("http://localhost:9/"), new byte[] { 1 }, "application/didcomm-encrypted+json");

        var ex = (await ((Func<Task>)(() => transport.SendAsync(request, default)))
            .Should().ThrowAsync<HttpRequestException>()).Which;
        FlattenMessages(ex).Should().Contain("private or reserved",
            "no policy was configured anywhere, so the default blocking policy refuses loopback");
    }

    // #95 ADV-3 (moved here from the single-attempt wire tests, which left the PR with #110): every
    // spelling of a loopback destination, the IPv4-translated ::ffff:0:a.b.c.d and the RFC 8215
    // 64:ff9b:1::/48 forms of AD-8 included, is refused by the real ConnectCallback before a byte is
    // sent — the transport is called directly, so the facade's pre-send check is not in the way.
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("[::ffff:0:7f00:1]")]
    [InlineData("[::127.0.0.1]")]
    [InlineData("[64:ff9b:1::7f00:1]")]
    [InlineData("127.0.0.1.")]
    [InlineData("2130706433")]
    public async Task ConnectCallback_refuses_every_spelling_of_a_loopback_destination_with_nothing_reaching_it(string host)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var services = new ServiceCollection();
            services.AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(new InMemorySecretsResolver());
                b.UseHttpTransport(o =>
                {
                    o.AllowedSchemes = new[] { "http", "https" };
                    o.MaxRetryAttempts = 0;
                });
            });
            await using var sp = services.BuildServiceProvider();
            var transport = sp.GetRequiredService<IDidCommTransport>();
            var request = new TransportRequest(
                new Uri($"http://{host}:{port}/inbox"), new byte[] { 1 }, "application/didcomm-encrypted+json");

            var ex = (await ((Func<Task>)(() => transport.SendAsync(request, default)))
                .Should().ThrowAsync<HttpRequestException>()).Which;

            FlattenMessages(ex).Should().Contain("private or reserved", host);
            listener.Pending().Should().BeFalse("no connection reached the loopback listener ({0})", host);
        }
        finally
        {
            listener.Stop();
        }
    }

    // Walk the InnerException chain into one string: when the SSRF guard fires, its TransportException is
    // wrapped by HttpRequestException (and a SocketException sits below a plain connect-refused), so assert
    // against the whole chain rather than any single layer's message.
    private static string FlattenMessages(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            sb.Append(e.Message).Append(" | ");
        return sb.ToString();
    }
}
