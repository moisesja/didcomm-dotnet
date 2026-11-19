using AwesomeAssertions;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The matcher and the <c>to</c> header (#98, owner decision C2). DIDComm v2.1 (message_structure):
/// an absent <c>to</c> means each recipient may assume it is the only one; a recipient SHOULD check a
/// present one for its own identifier, MUST NOT refuse a message when it is missing, and SHOULD warn.
/// Identity stays bound to the keys (transports: trust comes from encryption or signing): the answer
/// must still be the request's mediator's authcrypt, decrypted by the request's own DID. Core's
/// unpack refuses an encrypted message whose <c>to</c> leaves out the decrypting DID (FR-CONSIST-02),
/// so the misaddressed answers here are crafted verified snapshots, the matcher's own rule.
/// </summary>
public sealed class MediatorClientChannelAddressingTests : IAsyncLifetime
{
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    [Fact]
    public async Task An_answer_whose_to_names_only_another_DID_still_completes_its_request_and_logs_one_warning_without_any_DID()
    {
        // Threaded: since #98 an answer without a thread that arrives on its own completes no request.
        var logs = new ClientLogCapture();
        await using var channel = Create(_w, new FakeSend(), time: new ManualTimeProvider(), logs: logs);
        var request = _w.Request();
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, CancellationToken.None).Observed();
        var misaddressed = CraftedSnapshot.Verified(_w.Status(request.Id, count: 3, to: _w.OtherLocal.Did),
            senderKid: _w.Mediator.Privates[0].Kid, recipientKid: _w.Local.Privates[0].Kid);

        var claimed = channel.AsCorrelator().OnInbound(misaddressed);

        claimed.Should().BeTrue("a to that does not name the decrypting DID must not make the client refuse the answer (L124)");
        (await PickupTest.Within(exchange)).To.Should().Equal(_w.OtherLocal.Did);
        var warning = logs.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Formatted.Should().Contain("status").And.Contain("'to'");
        foreach (var identifier in _w.Identifiers.Append("did:"))
            logs.Entries.Should().NotContain(e => e.Everything.Contains(identifier, StringComparison.Ordinal), "no line carries a DID or a key id");
    }

    [Fact]
    public async Task The_addressing_warning_is_rate_limited_like_the_channels_other_lines()
    {
        var logs = new ClientLogCapture();
        await using var channel = Create(_w, new FakeSend(), time: new ManualTimeProvider(), logs: logs);
        // A waiting request keeps the matcher active; the answer threads to no request, so it is offered and unclaimed each time.
        var waiting = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, CancellationToken.None).Observed();
        var misaddressed = CraftedSnapshot.Verified(_w.Status(Guid.NewGuid().ToString(), to: _w.OtherLocal.Did),
            senderKid: _w.Mediator.Privates[0].Kid, recipientKid: _w.Local.Privates[0].Kid);

        for (var i = 0; i < 1000; i++)
            channel.AsCorrelator().OnInbound(misaddressed);

        logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Formatted.Contains("'to'", StringComparison.Ordinal))
            .Should().Be(2, "the first and the 1000th are Warnings, the rest Debug");
        logs.Entries.Count(e => e.Formatted.Contains("'to'", StringComparison.Ordinal)).Should().Be(1000, "every one is logged, most at Debug");
        waiting.IsCompleted.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_answer_decrypted_by_another_of_our_DIDs_is_still_refused_whatever_its_to_says(bool toNamesTheRequester)
    {
        // Real envelopes: the mediator's authcrypt to our other DID. Its `to` names the other DID (as
        // unpack requires) or, crafted, our request's DID. The decrypting key decides the pair.
        var logs = new ClientLogCapture();
        await using var channel = Create(_w, new FakeSend(), time: new ManualTimeProvider(), logs: logs);
        var request = _w.Request();
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, CancellationToken.None).Observed();
        var forOther = toNamesTheRequester
            ? CraftedSnapshot.Verified(_w.Status(request.Id, to: _w.Local.Did), senderKid: _w.Mediator.Privates[0].Kid, recipientKid: _w.OtherLocal.Privates[0].Kid)
            : await _w.SnapshotAsync(_w.Status(request.Id, to: _w.OtherLocal.Did), to: _w.OtherLocal.Did);

        var claimed = channel.AsCorrelator().OnInbound(forOther);
        var genuine = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id, count: 7)));

        claimed.Should().BeFalse("the key that decrypted it is not the request's DID's");
        genuine.Should().BeTrue();
        (await PickupTest.Within(exchange)).To.Should().Equal(_w.Local.Did);
    }
}
