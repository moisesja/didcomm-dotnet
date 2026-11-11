using System.Net;
using DidComm.Messages;
using DidComm.Protocols.TrustPing;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #91 F9 — the HTTP binding of the return-route extension (FR-RR-04) end to end against a
/// TestServer: an authcrypt request carrying <c>return_route: all</c> gets its reply as the
/// <c>200</c> body; without the header the endpoint keeps answering <c>202</c> with no body.
/// </summary>
public sealed class ReturnRouteHttpTests
{
    [Fact]
    public async Task Authcrypt_ping_with_return_route_all_gets_the_ping_response_as_the_200_body()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();

        var ping = host.Stamped(new MessageBuilder()
                .WithType(TrustPing.PingType)
                .WithFrom(host.Alice.Did)
                .WithTo(host.ServerDid)
                .WithReturnRoute(ReturnRouteMode.All))
            .Build();

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, ping));

        response.Status.Should().Be(HttpStatusCode.OK, "an eligible request with return_route: all gets its reply on the response (FR-RR-04)");
        response.ContentType.Should().Be("application/didcomm-encrypted+json", "the media type is exact — no parameters");
        response.Body.Should().NotBeEmpty();

        var reply = await host.Alice.Client.UnpackAsync(response.BodyText);
        reply.Encrypted.Should().BeTrue();
        reply.Authenticated.Should().BeTrue("the reply is authcrypted from the DID that decrypted the ping");
        reply.SenderKid.Should().StartWith(host.ServerDid + "#");
        reply.RecipientKid.Should().StartWith(host.Alice.Did + "#");
        reply.Message.Type.Should().Be(TrustPing.ResponseType);
        reply.Message.From.Should().Be(host.ServerDid);
        reply.Message.To.Should().Equal(host.Alice.Did);
        reply.Message.Thid.Should().Be(ping.Id);

        host.Registry.Count.Should().Be(0, "HTTP sessions are request-scoped and never registered (JC-16)");
        host.LeakingLogLines().Should().BeEmpty("no log line may carry a DID or key id (JC-6)");
    }

    [Fact]
    public async Task The_same_ping_without_the_header_is_answered_202_with_an_empty_body()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();

        var ping = host.Stamped(new MessageBuilder()
                .WithType(TrustPing.PingType)
                .WithFrom(host.Alice.Did)
                .WithTo(host.ServerDid))
            .Build();

        var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, ping));

        response.Status.Should().Be(HttpStatusCode.Accepted, "an absent header means none for this request (FR-RR-01)");
        response.Body.Should().BeEmpty();
        host.Logs.Entries.Should().Contain(e => e.Contains("returned-on-session=False"),
            "the handler still ran and produced its reply, which is logged rather than sent");
        host.LeakingLogLines().Should().BeEmpty();
    }
}
