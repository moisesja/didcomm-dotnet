using System.Net;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Transports;
using Xunit;

namespace DidComm.Tests.Transports;

/// <summary>
/// ADV-3 (#95): the outbound endpoint guard after AD-8 (IPv4-translated <c>::ffff:0:a.b.c.d</c> and
/// the RFC 8215 <c>64:ff9b:1::/48</c> prefix). Other spellings of a private destination that a
/// recipient's push endpoint (or a DID document's service endpoint) could use, through
/// <see cref="OutboundEndpointGuard.Validate"/> exactly as <c>SendPackedAsync</c> calls it; and the
/// public addresses the new rules must not refuse.
/// </summary>
public sealed class OutboundEndpointGuardAdv3Tests
{
    [Theory]
    // IPv4-translated (RFC 2765/6052), every spelling .NET's Uri accepts.
    [InlineData("http://[::ffff:0:127.0.0.1]/")]
    [InlineData("http://[0:0:0:0:ffff:0:7f00:1]/")]
    [InlineData("http://[::FFFF:0:A9FE:A9FE]/")]
    [InlineData("http://[0000:0000:0000:0000:FFFF:0000:0A00:0001]/")]
    [InlineData("http://[::ffff:0:192.168.0.1]:8443/inbox")]
    // RFC 8215 local-use NAT64 at every RFC 6052 prefix length inside the /48.
    [InlineData("http://[64:ff9b:1::a9fe:a9fe]/")]
    [InlineData("http://[64:ff9b:1:a9fe:a9:fe00::]/")]
    [InlineData("http://[64:ff9b:1:7f00:0:100::]/")]
    [InlineData("http://[64:FF9B:1:FFFF:FFFF:FFFF:FFFF:FFFF]/")]
    // Zone ids (percent-encoded in a URI) on loopback, the unspecified address and link-local.
    [InlineData("http://[::1%251]/")]
    [InlineData("http://[::%251]/")]
    [InlineData("http://[fe80::a9fe:a9fe%25eth0]/")]
    [InlineData("http://[fe80::1%251]/")]
    // IPv4-compatible, 6to4, Teredo, NAT64, unique-local (AWS IMDS over IPv6).
    [InlineData("http://[::10.0.0.1]/")]
    [InlineData("http://[2002:c0a8:101::1]/")]
    [InlineData("http://[2002:7F00:1::]/")]
    [InlineData("http://[2001:0:4136:e378:8000:63bf:3fff:fdd2]/")]
    [InlineData("http://[64:ff9b::127.0.0.1]/")]
    [InlineData("http://[fd00:ec2::254]/")]
    // IPv4 spellings Uri canonicalises, userinfo, trailing dot.
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://127.000.000.001/")]
    [InlineData("http://0/")]
    [InlineData("http://evil.example@169.254.169.254/")]
    [InlineData("http://[::ffff:127.0.0.1]./")]
    public void Every_spelling_of_a_private_destination_is_refused(string endpoint)
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy(), _ => throw new InvalidOperationException("an IP literal needs no DNS"));

        FluentActions.Invoking(() => guard.Validate(new Uri(endpoint))).Should().Throw<TransportException>(endpoint);
    }

    [Theory]
    [InlineData("LOCALHOST.")]
    [InlineData("metadata.google.internal.")]
    [InlineData("169.254.169.254.nip.io")]
    public void A_name_that_resolves_to_a_private_address_is_refused_whatever_its_spelling(string host)
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy(), name => name.StartsWith("169", StringComparison.Ordinal) || name.StartsWith("metadata", StringComparison.Ordinal)
            ? new[] { IPAddress.Parse("169.254.169.254") }
            : new[] { IPAddress.Loopback, IPAddress.IPv6Loopback });

        FluentActions.Invoking(() => guard.Validate(new Uri($"https://{host}/inbox"))).Should().Throw<TransportException>(host);
    }

    [Theory]
    [InlineData("https://8.8.8.8/")]
    [InlineData("https://100.63.255.255/")]
    [InlineData("https://100.128.0.1/")]
    [InlineData("https://172.15.255.255/")]
    [InlineData("https://172.32.0.1/")]
    [InlineData("https://223.255.255.254/")]
    [InlineData("https://[2606:4700:4700::1111]/")]
    [InlineData("https://[2001:4860:4860::8888]/")]
    [InlineData("https://[2002:808:808::1]/")]
    [InlineData("https://[64:ff9b::808:808]/")]
    [InlineData("https://[64:ff9b:2::1]/")]
    [InlineData("https://[::ffff:0:808:808]/")]
    [InlineData("https://[::ffff:8.8.8.8]/")]
    [InlineData("https://[::8.8.8.8]/")]
    public void A_public_destination_is_not_refused_by_the_new_rules(string endpoint)
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy(), _ => throw new InvalidOperationException("an IP literal needs no DNS"));

        FluentActions.Invoking(() => guard.Validate(new Uri(endpoint))).Should().NotThrow(endpoint);
    }

    [Theory]
    [InlineData("[::1]")]
    [InlineData("[::ffff:0:127.0.0.1]")]
    [InlineData("[64:ff9b:1::7f00:1]")]
    [InlineData("[::1%251]")]
    [InlineData("127.0.0.1.")]
    public async Task The_connect_time_check_refuses_the_same_spellings_as_the_handler_passes_them(string host)
    {
        // SocketsHttpHandler hands the connect callback the host bracketed (and percent-encoded
        // zone ids) for an IPv6 literal.
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy());

        (await guard.Awaiting(g => g.ConnectAsync(new DnsEndPoint(host, 9), default).AsTask()).Should().ThrowAsync<TransportException>(host)).Which.Message.Should().Contain("private or reserved");
    }
}
