using System.Net;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// FR-RR-08 replay admission at the registry-aware HTTP endpoint, keyed on (sender DID subject,
/// message id): DIDComm v2.1 threading.md L9 says an <c>id</c> "MUST be compared case-insensitively",
/// so a second request from the same sender whose id differs from an admitted one only in letter case
/// is a replay — answered 202 with nothing, dispatched never, logged by id and outcome. The sender
/// subject stays an exact key: another sender with that same id is admitted. A genuinely different
/// id — another letter, or a non-letter — is admitted.
/// </summary>
public sealed class ReplayMessageIdCaseEndpointTests
{
    private static async Task<HttpPostResult> PostAsync(ReturnRouteTestHost host, TestPeer from, string id)
    {
        var inbound = host.Stamped(host.RecordingMessage(from).WithId(id).WithReturnRoute(ReturnRouteMode.All)).Build();
        return await host.PostAsync(await host.AuthcryptAsync(from, inbound));
    }

    private static async Task<Message> OpenAsync(TestPeer peer, HttpPostResult response)
        => (await peer.Client.UnpackAsync(response.BodyText)).Message;

    [Fact]
    public async Task A_request_whose_id_differs_from_an_admitted_one_only_in_case_is_dropped_while_another_sender_and_other_ids_are_admitted()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, message.From!));
        var lower = "rr-case-" + Guid.NewGuid().ToString("N") + "-abc";
        var upper = lower.ToUpperInvariant();
        var anotherLetter = lower[..^1] + "d"; // abc / abd
        var anotherNonLetter = lower.Replace("rr-case-", "rr_case-", StringComparison.Ordinal);

        var admitted = await PostAsync(host, host.Alice, lower);
        var recased = await PostAsync(host, host.Alice, upper);
        var mixed = await PostAsync(host, host.Alice, "Rr-CaSe-" + lower["rr-case-".Length..].ToUpperInvariant());
        var otherSender = await PostAsync(host, host.Carol, upper);
        var otherLetter = await PostAsync(host, host.Alice, anotherLetter.ToUpperInvariant());
        var otherNonLetter = await PostAsync(host, host.Alice, anotherNonLetter.ToUpperInvariant());

        admitted.Status.Should().Be(HttpStatusCode.OK);
        (await OpenAsync(host.Alice, admitted)).Thid.Should().Be(lower);
        recased.Status.Should().Be(HttpStatusCode.Accepted, "the same id in other letter case is a replay (FR-RR-08)");
        recased.Body.Should().BeEmpty();
        mixed.Status.Should().Be(HttpStatusCode.Accepted);
        mixed.Body.Should().BeEmpty();
        otherSender.Status.Should().Be(HttpStatusCode.OK, "the sender subject is an exact key: Carol's message is not Alice's");
        (await OpenAsync(host.Carol, otherSender)).Thid.Should().Be(upper, "the reply names the id as Carol sent it");
        otherLetter.Status.Should().Be(HttpStatusCode.OK, "an id that differs by another letter is another message");
        otherNonLetter.Status.Should().Be(HttpStatusCode.OK, "an id that differs by a non-letter is another message");
        host.Recorder.Invocations.Should().Be(4, "the two case variants re-execute nothing");
        host.Recorder.Seen.Select(r => r.Message.Id).Should().Equal(lower, upper, anotherLetter.ToUpperInvariant(), anotherNonLetter.ToUpperInvariant());
        host.DropLines(upper).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task An_admitted_upper_case_id_makes_its_lower_case_spelling_a_replay_too()
    {
        // The other direction: the first spelling the guard sees is upper case.
        await using var host = await ReturnRouteTestHost.StartAsync();
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, message.From!));
        var upper = "RR-CASE-" + Guid.NewGuid().ToString("N").ToUpperInvariant();

        var admitted = await PostAsync(host, host.Alice, upper);
        var lowered = await PostAsync(host, host.Alice, upper.ToLowerInvariant());

        admitted.Status.Should().Be(HttpStatusCode.OK);
        lowered.Status.Should().Be(HttpStatusCode.Accepted);
        lowered.Body.Should().BeEmpty();
        host.Recorder.Invocations.Should().Be(1);
        host.LeakingLogLines().Should().BeEmpty();
    }
}
