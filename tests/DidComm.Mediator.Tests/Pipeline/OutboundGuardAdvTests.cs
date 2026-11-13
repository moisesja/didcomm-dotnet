using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Transports;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// ADV-2 (#95): the outbound endpoint guard every direct push goes through
/// (<c>DidCommClient.SendPackedAsync</c> → <see cref="OutboundEndpointGuard.Validate"/>), fed the
/// spellings of loopback and the cloud-metadata address a recipient's endpoint could use.
/// </summary>
public sealed class OutboundGuardAdvTests
{
    [Theory]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("http://[::169.254.169.254]/")]
    [InlineData("http://[64:ff9b::a9fe:a9fe]/")]
    [InlineData("http://[2002:a9fe:a9fe::1]/")]
    [InlineData("http://0xa9.0xfe.0xa9.0xfe/")]
    [InlineData("http://2852039166/")]
    [InlineData("http://user:pw@127.0.0.1/")]
    [InlineData("http://127.1/")]
    [InlineData("http://017700000001/")]
    [InlineData("http://[0:0:0:0:0:ffff:7f00:1]/")]
    [InlineData("http://[fe80::1%25en0]/")]
    [InlineData("http://[::]/")]
    [InlineData("http://127.0.0.1./")]
    public void Every_spelling_of_loopback_and_the_metadata_address_is_refused(string endpoint)
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy(), _ => throw new InvalidOperationException("no DNS for IP literals"));

        FluentActions.Invoking(() => guard.Validate(new Uri(endpoint))).Should().Throw<TransportException>(endpoint);
    }

    [Theory]
    [InlineData("http://[::ffff:0:a9fe:a9fe]/")]
    [InlineData("http://[::ffff:0:7f00:1]/")]
    [InlineData("http://[64:ff9b:1::a9fe:a9fe]/")]
    public void Translated_spellings_of_the_metadata_address_and_loopback_are_refused(string endpoint)
    {
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy(), _ => throw new InvalidOperationException("no DNS for IP literals"));

        FluentActions.Invoking(() => guard.Validate(new Uri(endpoint))).Should().Throw<TransportException>(endpoint);
    }
}
