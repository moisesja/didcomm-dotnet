using System.Net;
using DidComm.AspNetCore;
using DidComm.Protocols.OutOfBand;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

// L-014: alias the static API class to dodge namespace shadowing.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace DidComm.InteropTests.Protocols;

/// <summary>
/// End-to-end FR-OOB-04 short-URL retrieval: the sender stores an invitation under an id and
/// hosts <c>MapDidCommOobEndpoint</c>; a recipient does an HTTP GET against the <c>?_oobid=</c>
/// URL and recovers the full invitation, then parses it with <see cref="OutOfBand.FromPlaintext"/>.
/// </summary>
public sealed class OutOfBandEndpointTests
{
    private static async Task<(TestServer Server, IOobInvitationStore Store)> BuildServerAsync()
    {
        var store = new InMemoryOobInvitationStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        app.UseRouting();
        app.MapDidCommOobEndpoint("/oob", store);

        await app.StartAsync();
        return (app.GetTestServer(), store);
    }

    [Fact]
    public async Task GET_with_known_oobid_returns_the_stored_invitation()
    {
        var (server, store) = await BuildServerAsync();

        var invitation = OutOfBandApi.CreateInvitation(from: "did:example:alice", goal: "Connect", goalCode: "connect");
        // InteropTests has InternalsVisibleTo from DidComm.Core, so we can serialize the
        // invitation to plaintext directly (a real sender uses DidCommClient.PackPlaintextAsync).
        var plaintext = DidComm.Composition.EnvelopeWriter.PackPlaintext(invitation.Message);
        var oobId = "5f0e3ffb-3f92-4648-9868-0d6f8889e6f3";
        store.Store(oobId, plaintext);

        using var client = server.CreateClient();
        var shortUrl = OutOfBandApi.ToShortUrl("/oob", oobId);
        var response = await client.GetAsync(shortUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/didcomm-plain+json");

        var body = await response.Content.ReadAsStringAsync();
        var recovered = OutOfBandApi.FromPlaintext(body);
        recovered.Id.Should().Be(invitation.Id);
        recovered.From.Should().Be("did:example:alice");
        recovered.GoalCode.Should().Be("connect");
    }

    [Fact]
    public async Task GET_with_unknown_oobid_returns_404()
    {
        var (server, _) = await BuildServerAsync();

        using var client = server.CreateClient();
        var response = await client.GetAsync("/oob?_oobid=does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_without_oobid_returns_400()
    {
        var (server, _) = await BuildServerAsync();

        using var client = server.CreateClient();
        var response = await client.GetAsync("/oob");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
