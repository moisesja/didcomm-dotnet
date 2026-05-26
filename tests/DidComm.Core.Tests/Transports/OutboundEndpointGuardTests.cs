using System.Net;
using System.Net.Sockets;
using DidComm.Exceptions;
using DidComm.Transports;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Transports;

/// <summary>
/// SSRF-defense unit tests for <see cref="OutboundEndpointGuard"/>: address classification plus the
/// pre-send <see cref="OutboundEndpointGuard.Validate"/> gate (literal hosts, DNS-name resolution
/// via the injectable seam, allowlist bypass, and the block opt-out). No real network I/O.
/// </summary>
public sealed class OutboundEndpointGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]              // loopback
    [InlineData("127.9.9.9")]              // loopback /8
    [InlineData("10.0.0.1")]              // RFC 1918
    [InlineData("172.16.0.1")]           // RFC 1918 lower bound
    [InlineData("172.31.255.255")]       // RFC 1918 upper bound
    [InlineData("192.168.1.1")]          // RFC 1918
    [InlineData("169.254.169.254")]      // link-local + cloud metadata
    [InlineData("100.64.0.1")]           // CGNAT (RFC 6598)
    [InlineData("0.0.0.0")]              // unspecified
    [InlineData("224.0.0.1")]            // multicast
    [InlineData("::1")]                   // IPv6 loopback
    [InlineData("fe80::1")]              // IPv6 link-local
    [InlineData("fc00::1")]              // IPv6 unique-local
    [InlineData("fd12:3456:789a::1")]   // IPv6 unique-local
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped metadata must not dodge the IPv4 rules
    [InlineData("::ffff:10.0.0.1")]     // IPv4-mapped RFC 1918
    public void IsPrivateOrReserved_blocks_non_public(string ip)
    {
        OutboundEndpointGuard.IsPrivateOrReserved(IPAddress.Parse(ip)).Should().BeTrue();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("9.9.9.9")]
    [InlineData("93.184.216.34")]            // example.com
    [InlineData("2606:4700:4700::1111")]     // public IPv6 (Cloudflare)
    public void IsPrivateOrReserved_allows_public(string ip)
    {
        OutboundEndpointGuard.IsPrivateOrReserved(IPAddress.Parse(ip)).Should().BeFalse();
    }

    [Fact]
    public void Validate_throws_for_literal_private_host()
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy());

        guard.Invoking(g => g.Validate(new Uri("https://169.254.169.254/inbox")))
            .Should().Throw<TransportException>()
            .WithMessage("*private or reserved*");
    }

    [Fact]
    public void Validate_throws_for_literal_ipv6_loopback()
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy());

        guard.Invoking(g => g.Validate(new Uri("https://[::1]/inbox")))
            .Should().Throw<TransportException>();
    }

    [Fact]
    public void Validate_allows_literal_public_host()
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy());

        guard.Invoking(g => g.Validate(new Uri("https://8.8.8.8/inbox"))).Should().NotThrow();
    }

    [Fact]
    public void Validate_resolves_hostname_and_blocks_internal()
    {
        var guard = new OutboundEndpointGuard(
            new OutboundEndpointPolicy(),
            resolveDns: _ => new[] { IPAddress.Parse("10.0.0.5") });

        guard.Invoking(g => g.Validate(new Uri("https://mediator.internal/inbox")))
            .Should().Throw<TransportException>();
    }

    [Fact]
    public void Validate_resolves_hostname_and_allows_public()
    {
        var guard = new OutboundEndpointGuard(
            new OutboundEndpointPolicy(),
            resolveDns: _ => new[] { IPAddress.Parse("93.184.216.34") });

        guard.Invoking(g => g.Validate(new Uri("https://example.com/inbox"))).Should().NotThrow();
    }

    [Fact]
    public void Validate_blocks_when_any_resolved_address_is_private()
    {
        // DNS-rebinding shape: one public + one internal answer must still be rejected.
        var guard = new OutboundEndpointGuard(
            new OutboundEndpointPolicy(),
            resolveDns: _ => new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("127.0.0.1") });

        guard.Invoking(g => g.Validate(new Uri("https://rebind.example/inbox")))
            .Should().Throw<TransportException>();
    }

    [Fact]
    public void Validate_allowlisted_host_bypasses_block()
    {
        var policy = new OutboundEndpointPolicy();
        policy.AllowedHosts.Add("169.254.169.254");
        var guard = new OutboundEndpointGuard(policy);

        guard.Invoking(g => g.Validate(new Uri("https://169.254.169.254/inbox"))).Should().NotThrow();
    }

    [Fact]
    public void Validate_block_disabled_allows_private()
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy { BlockPrivateNetworks = false });

        guard.Invoking(g => g.Validate(new Uri("https://10.0.0.5/inbox"))).Should().NotThrow();
    }

    [Fact]
    public void Validate_with_ResolveDnsNames_false_skips_hostname_resolution()
    {
        var resolved = false;
        var guard = new OutboundEndpointGuard(
            new OutboundEndpointPolicy { ResolveDnsNames = false },
            resolveDns: _ => { resolved = true; return Array.Empty<IPAddress>(); });

        guard.Invoking(g => g.Validate(new Uri("https://mediator.internal/inbox"))).Should().NotThrow();
        resolved.Should().BeFalse();
    }

    [Fact]
    public void Validate_with_ResolveDnsNames_false_still_blocks_literal_private_ip()
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy { ResolveDnsNames = false });

        guard.Invoking(g => g.Validate(new Uri("https://127.0.0.1/inbox")))
            .Should().Throw<TransportException>();
    }

    [Fact]
    public async Task ConnectAsync_throws_for_private_destination_by_default()
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy());

        await guard.Invoking(g => g.ConnectAsync(new DnsEndPoint("127.0.0.1", 9), default).AsTask())
            .Should().ThrowAsync<TransportException>()
            .WithMessage("*private or reserved*");
    }

    [Fact]
    public async Task ConnectAsync_connects_to_loopback_when_block_disabled()
    {
        // Connect-time path (used by the HTTP SocketsHttpHandler.ConnectCallback): with the block
        // opted out, the guard resolves + connects a real socket to the vetted address.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy { BlockPrivateNetworks = false });

            using var socket = await guard.ConnectAsync(new DnsEndPoint("127.0.0.1", port), default);

            socket.Connected.Should().BeTrue();
        }
        finally
        {
            listener.Stop();
        }
    }
}
