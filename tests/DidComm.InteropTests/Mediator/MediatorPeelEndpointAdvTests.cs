using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// ADV-1 (#95): the self-addressed peel and hostile forwards through the real
/// <c>MapDidCommMediator</c> HTTP endpoint — the acknowledgement rule holds for a layer addressed to
/// the mediator itself, every hostile shape is a 202 with one typed outcome (never a 500, never
/// <c>Faulted</c>), and the response does not tell the classes of <c>next</c> apart.
/// </summary>
public sealed class MediatorPeelEndpointAdvTests
{
    [Fact]
    public async Task A_self_addressed_forward_is_not_opened_before_its_202_is_committed()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (inner, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        var (outer, _) = await alice.ForwardAsync(mediator.Did, mediator.Did, inner);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;

        var post = Task.Run(() => mediator.PostAsync(outer));
        await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 1, "admitted while the response is held");
        (await ReturnRouteTestHost.WaitUntilAsync(() => mediator.Store.MessageCount > 0 || mediator.Outcomes.Count > 0, TimeSpan.FromMilliseconds(200)))
            .Should().BeFalse("the layer is opened on the pump, which never sees it before the 202 is committed");

        gate.SetResult();
        (await post.WaitAsync(MediatorTestServer.Timeout)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.Store.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_self_addressed_forward_whose_request_is_aborted_before_the_202_is_never_opened_or_relayed()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (inner, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        var (outer, _) = await alice.ForwardAsync(mediator.Did, mediator.Did, inner);
        mediator.Controls.AbortNextPost = true;

        var act = async () => await mediator.PostAsync(outer);
        await act.Should().ThrowAsync<Exception>();

        await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 0, "the abort releases the reservation");
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedUnacknowledged);
        mediator.Store.MessageCount.Should().Be(0);
    }

    public enum Hostile
    {
        NoAttachments,
        TwoAttachments,
        LinksOnly,
        Base64Whitespace,
        Base64Garbage,
        EmptyBody,
        NextNumber,
        NextUnpairedSurrogate,
        NextAtCap,
        NextOverCap,
        PleaseAckAndReturnRoute,
        DelayMinValue,
        DelayNotInteger,
        ExpiresMaxValue,
        SelfAddressedNotAnEnvelope,
        SelfAddressedTwoAttachments,
    }

    [Theory]
    [InlineData(Hostile.NoAttachments)]
    [InlineData(Hostile.TwoAttachments)]
    [InlineData(Hostile.LinksOnly)]
    [InlineData(Hostile.Base64Whitespace)]
    [InlineData(Hostile.Base64Garbage)]
    [InlineData(Hostile.EmptyBody)]
    [InlineData(Hostile.NextNumber)]
    [InlineData(Hostile.NextUnpairedSurrogate)]
    [InlineData(Hostile.NextAtCap)]
    [InlineData(Hostile.NextOverCap)]
    [InlineData(Hostile.PleaseAckAndReturnRoute)]
    [InlineData(Hostile.DelayMinValue)]
    [InlineData(Hostile.DelayNotInteger)]
    [InlineData(Hostile.ExpiresMaxValue)]
    [InlineData(Hostile.SelfAddressedNotAnEnvelope)]
    [InlineData(Hostile.SelfAddressedTwoAttachments)]
    public async Task A_hostile_forward_is_answered_202_with_one_typed_outcome_and_never_a_fault(Hostile hostile)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var payload = await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did);

        var next = hostile is Hostile.SelfAddressedNotAnEnvelope or Hostile.SelfAddressedTwoAttachments ? mediator.Did : bob.Did;
        var (packed, _) = await alice.ForwardAsync(mediator.Did, next, payload, forward => Shape(forward, hostile, bob.Did, payload));
        var response = await mediator.PostAsync(packed);

        response.Status.Should().BeOneOf(new[] { HttpStatusCode.Accepted, HttpStatusCode.BadRequest }, "never a 500 ({0})", hostile);
        response.Body.Should().BeEmpty();
        await mediator.FlushAsync();
        mediator.Outcomes.Should().NotContain(ForwardOutcomes.Faulted, "a hostile forward is the sender's mistake ({0})", hostile);
        mediator.Outcomes.Count.Should().BeLessThanOrEqualTo(1);
        if (response.Status == HttpStatusCode.BadRequest)
            mediator.Outcomes.Should().BeEmpty("the unpack refused it before the handler");
        mediator.Logs.Records.Should().NotContain(r => r.Level >= Microsoft.Extensions.Logging.LogLevel.Error && r.Text.Contains("Forward", StringComparison.Ordinal));
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    private static void Shape(Message forward, Hostile hostile, string bobDid, string payload)
    {
        static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
        switch (hostile)
        {
            case Hostile.NoAttachments:
                forward.Attachments!.Clear();
                break;
            case Hostile.TwoAttachments:
            case Hostile.SelfAddressedTwoAttachments:
                forward.Attachments!.Add(new Attachment { Id = "second", Data = new AttachmentData { Json = JsonNode.Parse(payload) } });
                break;
            case Hostile.LinksOnly:
                forward.Attachments![0].Data = new AttachmentData { Links = new[] { "https://evil.example/x" }, Hash = "abc" };
                break;
            case Hostile.Base64Whitespace:
                forward.Attachments![0].Data = new AttachmentData { Base64 = "    " };
                break;
            case Hostile.Base64Garbage:
                forward.Attachments![0].Data = new AttachmentData { Base64 = "!!!!" };
                break;
            case Hostile.EmptyBody:
                forward.Body = new JsonObject();
                break;
            case Hostile.NextNumber:
                forward.Body!["next"] = 42;
                break;
            case Hostile.NextUnpairedSurrogate:
                forward.Body!["next"] = bobDid + "\uD800";
                break;
            case Hostile.NextAtCap:
                forward.Body!["next"] = bobDid + "#" + new string('k', 4096 - bobDid.Length - 1);
                break;
            case Hostile.NextOverCap:
                forward.Body!["next"] = bobDid + "#" + new string('k', 4096 - bobDid.Length);
                break;
            case Hostile.PleaseAckAndReturnRoute:
                forward.PleaseAck = new[] { string.Empty };
                ReturnRouteHeaderWriter.Write(forward, ReturnRouteMode.All, thread: null);
                break;
            case Hostile.DelayMinValue:
                (forward.AdditionalHeaders ??= new Dictionary<string, JsonElement>())["delay_milli"] = Json(long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case Hostile.DelayNotInteger:
                (forward.AdditionalHeaders ??= new Dictionary<string, JsonElement>())["delay_milli"] = Json("1e300");
                break;
            case Hostile.ExpiresMaxValue:
                forward.ExpiresTime = long.MaxValue;
                break;
            case Hostile.SelfAddressedNotAnEnvelope:
                forward.Attachments![0].Data = new AttachmentData { Json = new JsonObject { ["not"] = "an envelope" } };
                break;
        }
    }
}

/// <summary>
/// ADV-1 (#95): the response to a forward does not tell the classes of <c>next</c> apart — known,
/// addressed to the mediator itself (with a known or an unknown recipient inside), over the
/// FR-API-09 cap, not a DID — by status, headers, body or median time (FR-MED-08 method). Runs
/// alone (serial timing collection).
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class MediatorNextOracleAdvTests
{
    [Fact]
    public async Task Status_headers_body_and_time_do_not_tell_known_self_addressed_over_cap_and_non_DID_next_apart()
    {
        const int WarmUp = 8;
        const int Batches = 6;
        const int PerBatch = 8;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m =>
        {
            m.Pickup.MaxQueueMessages = 10_000;
            m.Pickup.MaxQueueBytes = 512L * 1024 * 1024;
            m.Pickup.MaxStoreMessages = 10_000;
            m.Pickup.MaxStoreBytes = 512L * 1024 * 1024;
        });
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var stranger = await mediator.NewPeerAsync();
        var alice = await mediator.NewSenderAsync();
        var note = await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did);
        var innerKnown = (await alice.ForwardAsync(mediator.Did, bob.Did, note)).Packed;
        var innerUnknown = (await alice.ForwardAsync(mediator.Did, stranger.Did, note)).Packed;

        // (next, attachment) per class; the attachment is the same size class for the direct ones.
        var classes = new (string Next, string Inner)[]
        {
            (bob.Did, note),
            (mediator.Did, innerKnown),
            (mediator.Did, innerUnknown),
            (stranger.Did + "#" + new string('k', 4096), note),
            ("https://evil.example/inbox", note),
        };
        var total = WarmUp + Batches * PerBatch;
        var packed = new List<string>[classes.Length];
        for (var c = 0; c < classes.Length; c++)
        {
            packed[c] = new List<string>(total);
            for (var i = 0; i < total; i++)
                packed[c].Add((await alice.ForwardAsync(mediator.Did, classes[c].Next, classes[c].Inner)).Packed);
        }

        using var client = mediator.Server.CreateClient();
        var times = classes.Select(_ => new List<double>()).ToArray();
        var headers = classes.Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToArray();
        for (var i = 0; i < total; i++)
        {
            for (var k = 0; k < classes.Length; k++)
            {
                var c = (i + k) % classes.Length;
                var r = await Qa.TimedPostAsync(mediator, client, packed[c][i]);
                r.Status.Should().Be(HttpStatusCode.Accepted);
                r.BodyLength.Should().Be(0);
                headers[c].Add(r.Headers);
                if (i >= WarmUp)
                    times[c].Add(r.Millis);
            }
            if (i >= WarmUp && (i - WarmUp) % PerBatch == PerBatch - 1)
                await mediator.FlushAsync();
        }
        await mediator.FlushAsync();

        mediator.Outcome(ForwardOutcomes.Queued).Should().Be(2 * total, "the known next and the self-addressed layer with a known recipient inside");
        foreach (var set in headers)
            set.Should().BeEquivalentTo(headers[0]);
        var medians = times.Select(Qa.Median).ToArray();
        var all = string.Join(", ", medians.Select(m => m.ToString("F3")));
        for (var c = 1; c < classes.Length; c++)
        {
            var ratio = Math.Max(medians[0], medians[c]) / Math.Min(medians[0], medians[c]);
            ratio.Should().BeLessThan(3.0, "class {0} against the known next (medians: {1} ms)", c, all);
        }
        mediator.LeakingLogLines(stranger.Did).Should().BeEmpty();
    }
}
