using System.Net;
using DidComm.Consistency;
using DidComm.Messages;
using DidComm.Protocols.TrustPing;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #92 G5 — the return-route client over HTTP, end to end against the #91 TestServer: a client built
/// with <c>AddDidComm</c> + <c>UseHttpTransport</c> sends with <see cref="DidComm.Transports.SendOptions.ReturnRoute"/>
/// and gets the server's reply in <see cref="DidComm.Transports.SendResult.Returned"/>, and the client's
/// FR-RR-09 <c>created_time</c> stamps are what make the server's FR-RR-08 admission accept the
/// in-scope messages (no caller timestamps anywhere). The receiver side of JC-17: a mediator-type reply
/// the server packs carries <c>created_time</c>. Every scenario ends with the log scan: no DID or key id
/// in any captured line, server or client.
/// </summary>
public sealed class ReturnRouteClientHttpTests
{
    [Fact]
    public async Task Default_client_ping_with_All_gets_the_authenticated_ping_response_in_Returned()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteClientHarness.Create(host);
        var ping = TrustPing.CreatePing(host.Alice.Did, host.ServerDid);

        var result = await client.Client.SendAsync(ping, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        result.Transport.HttpStatusCode.Should().Be(200);
        var returned = result.Returned;
        returned.Should().NotBeNull("the server answered the ping on the return route (FR-RR-04/07)");
        returned!.Message.Type.Should().Be(TrustPing.ResponseType);
        returned.Message.Thid.Should().Be(ping.Id);
        returned.Authenticated.Should().BeTrue();
        DidSubject.DidSubjectOf(returned.SenderKid).Should().Be(host.ServerDid);
        returned.Message.To.Should().Equal(host.Alice.Did);
        returned.Message.CreatedTime.Should().BeNull("an ordinary reply is outside the FR-RR-09 scope and is not stamped");
        ping.CreatedTime.Should().BeNull("the caller's ping was not stamped; the sent copy was");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Builder_set_all_with_SendOptions_unset_is_stamped_and_returns_the_reply()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteClientHarness.Create(host);
        var ping = new MessageBuilder()
            .WithType(TrustPing.PingType)
            .WithFrom(host.Alice.Did)
            .WithTo(host.ServerDid)
            .WithReturnRoute(ReturnRouteMode.All)
            .Build();

        var result = await client.Client.SendAsync(ping, client.ToServer());

        result.Returned.Should().NotBeNull("FR-RR-09: the builder-set `all` puts the ping in scope, so SendAsync stamped it and the server admitted it");
        result.Returned!.Message.Thid.Should().Be(ping.Id);
        host.DropLines(ping.Id).Should().BeEmpty();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_created_time_10_minutes_old_is_sent_unchanged_and_gets_202_no_reply()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteClientHarness.Create(host);
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var old = host.RecordingMessage(host.Alice).WithCreatedTime(host.Clock.Now.AddMinutes(-10).ToUnixTimeSeconds()).Build();

        var result = await client.Client.SendAsync(old, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        result.Transport.HttpStatusCode.Should().Be(202, "the server judged the caller's own timestamp and refused it");
        result.Returned.Should().BeNull();
        host.Recorder.Count.Should().Be(0);
        host.DropLines(old.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.TooOld));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Pickup_3_0_and_header_less_pickup_4_0_messages_received_are_stamped_and_admitted()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.ConfigureDidComm = b =>
        {
            b.AddProtocol<PickupRecordingHandler>();
            b.AddProtocol<Pickup40RecordingHandler>();
        });
        await using var client = ReturnRouteClientHarness.Create(host);
        var statusRequest = new MessageBuilder()
            .WithType(PickupRecordingHandler.ProtocolUri + "/status-request")
            .WithFrom(host.Alice.Did)
            .WithTo(host.ServerDid)
            .Build();
        var messagesReceived = new MessageBuilder()
            .WithType(Pickup40RecordingHandler.ProtocolUri + "/messages-received")
            .WithFrom(host.Alice.Did)
            .WithTo(host.ServerDid)
            .Build();

        await client.Client.SendAsync(statusRequest, client.ToServer());
        await client.Client.SendAsync(messagesReceived, client.ToServer());

        var seen = host.Recorder.Seen.ToList();
        seen.Select(r => r.Message.Id).Should().Equal(statusRequest.Id, messagesReceived.Id);
        seen.Should().OnlyContain(r => r.Message.CreatedTime == host.Clock.Now.ToUnixTimeSeconds(),
            "the mediator-service PIURI clause of FR-RR-09 stamped both, so FR-RR-08 admitted them");
        seen.Should().OnlyContain(r => r.ReturnRoute == ReturnRouteHeader.None, "both were sent header-less");
        host.DropLines(statusRequest.Id).Should().BeEmpty();
        host.DropLines(messagesReceived.Id).Should().BeEmpty();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Application_message_without_return_route_arrives_without_created_time()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteClientHarness.Create(host);
        var message = host.RecordingMessage(host.Alice).Build();

        var result = await client.Client.SendAsync(message, client.ToServer());

        result.Transport.HttpStatusCode.Should().Be(202);
        result.Returned.Should().BeNull();
        host.Recorder.Seen.Should().ContainSingle().Which.Message.CreatedTime.Should().BeNull("ordinary traffic is not stamped");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Anoncrypt_All_gets_202_and_no_Returned()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteClientHarness.Create(host);
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var message = new MessageBuilder().WithType(RecordingHandler.ProtocolUri + "/anon").WithTo(host.ServerDid).Build();

        var result = await client.Client.SendAsync(message, client.ToServer() with { From = null, ReturnRoute = ReturnRouteMode.All });

        result.Transport.HttpStatusCode.Should().Be(202, "an anoncrypt request can open no session (FR-RR-03)");
        result.Returned.Should().BeNull();
        var seen = host.Recorder.Seen.Should().ContainSingle().Which;
        seen.Session.Should().BeNull();
        seen.ReturnRoute.Mode.Should().Be(ReturnRouteMode.All);
        seen.Message.CreatedTime.Should().Be(host.Clock.Now.ToUnixTimeSeconds(), "JC-5: stamped and sent all the same");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Thread_gets_202_and_no_Returned()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteClientHarness.Create(host);
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var thread = "thr-" + Guid.NewGuid().ToString("N");
        var message = host.RecordingMessage(host.Alice).Build();

        var result = await client.Client.SendAsync(message, client.ToServer() with { ReturnRoute = ReturnRouteMode.Thread, ReturnRouteThread = thread });

        result.Transport.HttpStatusCode.Should().Be(202, "the HTTP binding returns only for `all` (FR-RR-04)");
        result.Returned.Should().BeNull();
        var seen = host.Recorder.Seen.Should().ContainSingle().Which;
        seen.ReturnRoute.Should().Be(new ReturnRouteHeader(ReturnRouteMode.Thread, thread));
        seen.Message.CreatedTime.Should().Be(host.Clock.Now.ToUnixTimeSeconds(), "`thread` is in the FR-RR-08 scope, so it was stamped and admitted");
        host.LeakingLogLines(thread).Should().BeEmpty();
    }

    [Fact]
    public async Task A_mediator_type_reply_pushed_by_the_server_carries_created_time()
    {
        // JC-17, the receiver side: SessionOutbound stamps a reply of a mediator-service type, so a
        // client applying FR-RR-08 to it would admit it.
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteClientHarness.Create(host);
        host.Recorder.Behavior = (message, _) => (
            new[]
            {
                new MessageBuilder()
                    .WithType(PickupRecordingHandler.ProtocolUri + "/status")
                    .WithFrom(host.ServerDid)
                    .WithTo(host.Alice.Did)
                    .WithThid(message.Id)
                    .Build(),
            },
            null);
        var request = host.RecordingMessage(host.Alice).Build();

        var result = await client.Client.SendAsync(request, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        host.Recorder.Seen.Should().ContainSingle().Which.PushResults.Should().Equal(true);
        result.Returned.Should().NotBeNull();
        result.Returned!.Message.Type.Should().Be(PickupRecordingHandler.ProtocolUri + "/status");
        result.Returned.Message.CreatedTime.Should().Be(host.Clock.Now.ToUnixTimeSeconds());
        result.Returned.Message.Thid.Should().Be(request.Id);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_caller_created_time_on_a_server_push_is_kept()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        await using var client = ReturnRouteClientHarness.Create(host);
        var preset = host.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds();
        host.Recorder.Behavior = (message, _) => (
            new[]
            {
                new MessageBuilder()
                    .WithType(PickupRecordingHandler.ProtocolUri + "/status")
                    .WithFrom(host.ServerDid)
                    .WithTo(host.Alice.Did)
                    .WithThid(message.Id)
                    .WithCreatedTime(preset)
                    .Build(),
            },
            null);

        var result = await client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        result.Returned!.Message.CreatedTime.Should().Be(preset, "FR-RR-09 never refreshes a value the sender set");
    }
}
