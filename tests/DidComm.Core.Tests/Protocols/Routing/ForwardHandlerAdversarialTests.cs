using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NetCrypto;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;
using Harness = DidComm.Tests.Protocols.Routing.ForwardHandlerTests.Harness;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// Red-team pass over the mediator routing core (#90): hostile <c>next</c> shapes, expiry and
/// <c>delay_milli</c> arithmetic at the extremes, a clock that misbehaves, the plaintext byte
/// budget behind the 202, accounting under concurrent dispatch / flush / dispose, and the
/// re-wrap paths fed non-JSON bytes. Every wire-driven failure must be a drop (handler) or the
/// documented exception type (processor) — never a crash, never a leak, never an echoed value.
/// </summary>
public sealed class ForwardHandlerAdversarialTests
{
    private const string Mediator = "did:example:m";
    private const string Bob = "did:example:bob";
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
    /// <summary>Embedded in every hostile value: finding it in a log line means the value was echoed.</summary>
    private const string Marker = "ZZZMARKERZZZ";
    private static readonly TimeSpan Flush = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Fixed = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    // ── Probe 2: hostile `next` ───────────────────────────────────────────────────────────

    public static TheoryData<string> HostileNextValues => new()
    {
        LengthCap.KidOfLength("did:example:" + Marker, LengthCap.KeyIdCap + 1),            // 4097 in all
        "did:example:" + Marker + new string('k', LengthCap.DidCap) + "#k",                // DID part over 2048, total in cap
        "did:example:bob" + Marker + "\0",                                                  // null byte
        "did:example:bób" + Marker,                                                    // non-ASCII idchar
        "did:example:bob " + Marker,                                                        // whitespace
        "DID:EXAMPLE:bob" + Marker,                                                         // upper-case scheme
        "did:" + Marker,                                                                    // no method-specific id
        "did:example:" + new string('a', LengthCap.DidCap - 12) + "?" + Marker + new string('q', 1900), // huge query, in cap, unregistered
        "did:example:bob#" + Marker + new string('f', LengthCap.KeyIdCap),                // registered subject, fragment pushes the total over the cap
    };

    [Theory]
    [MemberData(nameof(HostileNextValues))]
    public async Task Hostile_next_is_dropped_without_throwing_and_never_echoed(string next)
    {
        await using var h = new Harness();
        var forward = ForwardHandlerTests.Forward(next);

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        h.Handler.Outstanding.Should().Be(0);
        h.Handler.OutstandingBytes.Should().Be(0);
        AssertNothingEchoes(h.Logger, Marker);
    }

    public static TheoryData<string> NonStringNextShapes => new()
    {
        "42", "true", "null", "\"\"", "{\"a\":\"" + Marker + "\"}", "[\"" + Marker + "\"]",
    };

    [Theory]
    [MemberData(nameof(NonStringNextShapes))]
    public async Task Non_string_next_is_dropped_as_malformed_without_throwing(string nextJson)
    {
        await using var h = new Harness();
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(Mediator)
            .WithBody(new JsonObject { ["next"] = JsonNode.Parse(nextJson) })
            .WithAttachment(new Attachment { Data = new AttachmentData { Json = JsonNode.Parse(Inner) } })
            .Build();

        var outcome = await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        h.Registry.Calls.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("malformed forward");
        AssertNothingEchoes(h.Logger, Marker);
    }

    // ── Probe 4: expiry arithmetic at the extremes ────────────────────────────────────────

    [Fact]
    public void Expiry_arithmetic_never_throws_on_extreme_inputs()
    {
        var now = Fixed;
        var nowSeconds = now.ToUnixTimeSeconds();

        ForwardProcessingResult.IsExpiredAt(long.MinValue, now, TimeSpan.Zero).Should().BeTrue("an impossible past expiry is expired");
        ForwardProcessingResult.IsExpiredAt(long.MaxValue, now, TimeSpan.Zero).Should().BeFalse("an impossible future expiry never expires");
        ForwardProcessingResult.IsExpiredAt(nowSeconds, now, TimeSpan.FromSeconds(-1)).Should().BeTrue("a negative skew is stricter, not an overflow");
        ForwardProcessingResult.IsExpiredAt(nowSeconds, now.AddSeconds(1), TimeSpan.FromSeconds(0.9)).Should().BeTrue("skew is floored to whole seconds: 0.9 s → 0");
        ForwardProcessingResult.IsExpiredAt(nowSeconds, now.AddSeconds(1), TimeSpan.FromSeconds(1)).Should().BeFalse();
        ForwardProcessingResult.IsExpiredAt(nowSeconds, DateTimeOffset.MaxValue, TimeSpan.MaxValue).Should().BeFalse("MaxValue skew subtracts ~2.9e11 s without overflow");
        ForwardProcessingResult.IsExpiredAt(nowSeconds, DateTimeOffset.MinValue, TimeSpan.MinValue).Should().BeTrue("MinValue skew adds ~2.9e11 s without overflow");
        ForwardProcessingResult.IsExpiredAt(long.MinValue, DateTimeOffset.MinValue, TimeSpan.MaxValue).Should().BeTrue();
        ForwardProcessingResult.IsExpiredAt(long.MaxValue, DateTimeOffset.MaxValue, TimeSpan.MinValue).Should().BeFalse();
    }

    [Fact]
    public async Task Clock_running_backwards_between_the_inline_and_pump_checks_still_relays()
    {
        await using var h = new Harness();
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);

        var queued = ForwardHandlerTests.Forward(Bob, expires: Fixed.AddSeconds(5).ToUnixTimeSeconds());
        await h.Dispatch(AsDecryptedByMediator(queued));
        h.Now = Fixed.AddMinutes(-10); // the clock steps back while the forward is queued
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(2, "a clock going backwards can only make a forward look younger");
        h.Handler.Outstanding.Should().Be(0);
    }

    [Fact]
    public async Task Clock_that_throws_drops_inline_and_in_the_pump_without_escaping()
    {
        await using var h = new Harness();

        // Inline: the host's clock faults during the FR-ROUTE-09 check → drop, not a 400.
        h.Options.Clock = () => throw new InvalidOperationException("clock down");
        var outcome = await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Handler.Outstanding.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("inline screening failed").And.Contain("InvalidOperationException");

        // Pump: admitted with a working clock, the clock faults before the processor stamps NotBefore.
        h.Options.Clock = () => h.Now;
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);
        var second = ForwardHandlerTests.Forward(Bob);
        await h.Dispatch(AsDecryptedByMediator(second));
        h.Options.Clock = () => throw new InvalidOperationException("clock down");
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);

        h.Sink.Count.Should().Be(1, "the second forward failed in the pump");
        h.Logger.Warnings.Should().Contain(w => w.Formatted.Contains("not relayed") && w.Formatted.Contains(second.Id));
        h.Handler.Outstanding.Should().Be(0);
        h.Handler.OutstandingBytes.Should().Be(0);

        // And the pump keeps serving once the clock is back.
        h.Options.Clock = () => h.Now;
        await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        await h.Handler.FlushAsync(Flush);
        h.Sink.Count.Should().Be(2);
    }

    // ── Probe 5: delay_milli abuse ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("9223372036854775807")]   // long.MaxValue
    [InlineData("-9223372036854775808")]  // long.MinValue
    [InlineData("9223372036854775808")]   // long.MaxValue + 1: a JSON number that is not an Int64
    [InlineData("-0")]
    [InlineData("1e18")]
    [InlineData("1.5")]
    [InlineData("\"9999999999999999999999\"")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1]")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task Delay_milli_abuse_never_throws_and_the_sink_sees_a_bounded_hold(string delayJson)
    {
        await using var h = new Harness();
        var forward = ForwardHandlerTests.Forward(Bob);
        forward.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse(delayJson).RootElement.Clone(),
        };

        await h.Dispatch(AsDecryptedByMediator(forward));
        await h.Handler.FlushAsync(Flush);

        var result = h.Sink.Results.Should().ContainSingle("the hint never blocks the relay").Which;
        var cap = TimeSpan.FromMilliseconds(int.MaxValue);
        if (result.Delay is TimeSpan delay)
        {
            delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero).And.BeLessThanOrEqualTo(cap);
            result.NotBefore.Should().Be(Fixed + delay);
        }
        else
        {
            result.NotBefore.Should().BeNull();
        }
        h.Logger.Warnings.Should().BeEmpty();
    }

    // ── Probe 7: the plaintext byte budget behind the 202 ─────────────────────────────────

    [Fact]
    public async Task Byte_budget_drops_the_forward_that_would_exceed_it_and_recovers_when_slots_drain()
    {
        var sample = AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob));
        var size = InboundMessageSnapshot.CreateFallback(sample).Utf8ByteCount;
        await using var h = new Harness(capacity: 100, byteBudget: 2L * size);
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);
        await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        h.Handler.Outstanding.Should().Be(2);
        h.Handler.OutstandingBytes.Should().Be(2L * size, "the in-flight and the queued forward both hold their charge");

        var third = ForwardHandlerTests.Forward(Bob);
        var outcome = await h.Dispatch(AsDecryptedByMediator(third));

        outcome.Result.Should().Be(DispatchResult.NoReply, "a budget drop is silent toward the sender");
        h.Handler.Outstanding.Should().Be(2, "the item cap had room; the byte budget refused it");
        var saturated = h.Logger.Warnings.Should().ContainSingle(w => w.Formatted.Contains("saturated")).Which;
        saturated.Formatted.Should().Contain(third.Id).And.NotContain(Bob);
        saturated.State["ByteBudget"].Should().Be(2L * size);
        saturated.State["Capacity"].Should().Be(100);

        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);
        h.Sink.Count.Should().Be(2);
        h.Handler.OutstandingBytes.Should().Be(0, "every charge is released after the sink");

        await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        await h.Handler.FlushAsync(Flush);
        h.Sink.Count.Should().Be(3, "admission resumes once the budget frees");
    }

    [Fact]
    public async Task A_forward_larger_than_the_whole_budget_is_always_dropped()
    {
        var sample = AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob));
        var size = InboundMessageSnapshot.CreateFallback(sample).Utf8ByteCount;
        await using var h = new Harness(byteBudget: size - 1);

        var outcome = await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        await h.Handler.FlushAsync(Flush);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        h.Sink.Count.Should().Be(0);
        h.Handler.Outstanding.Should().Be(0);
        h.Handler.OutstandingBytes.Should().Be(0);
        h.Logger.Warnings.Should().ContainSingle().Which.Formatted.Should().Contain("saturated");
    }

    [Fact]
    public async Task Byte_charge_is_released_after_a_pump_failure_and_on_dispose()
    {
        var h = new Harness(capacity: 8);

        // Pump failure: two attachments are refused by the processor after the charge was taken.
        await h.Dispatch(AsDecryptedByMediator(ForwardMessage.Create(Mediator, Bob, new[] { Inner, Inner })));
        await h.Handler.FlushAsync(Flush);
        h.Sink.Count.Should().Be(0);
        h.Handler.OutstandingBytes.Should().Be(0, "a processor refusal releases the charge");

        // Dispose with queued forwards: the drain releases every charge.
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        await h.Sink.Started.Task.WaitAsync(Flush);
        await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
        h.Handler.OutstandingBytes.Should().BeGreaterThan(0);

        await h.Handler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await h.Handler.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));

        h.Handler.Outstanding.Should().Be(0);
        h.Handler.OutstandingBytes.Should().Be(0);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task A_really_unpacked_forward_is_charged_its_exact_plaintext_length_without_re_serialization()
    {
        // The verified-at-unpack snapshot carries the exact plaintext; the handler must use it (no
        // second serialization on the request path) and charge precisely that many UTF-8 bytes.
        var mediatorKey = TestKeyMaterial.Generate(KeyType.X25519, Mediator + "#ka-1");
        var resolver = new SpyDidResolver(LengthCap.Doc(Mediator, keyAgreement: mediatorKey.PublicJwk));
        var mediator = new DidCommClient(new SpySecretsResolver(mediatorKey.PrivateJwk), new NetDidKeyService(resolver), new DidCommOptions());
        var packed = (await mediator.PackEncryptedAsync(
            ForwardMessage.Create(Mediator, Bob, new[] { Inner }),
            new PackEncryptedOptions(Recipients: new[] { Mediator }))).Message;
        var unpacked = await mediator.UnpackAsync(packed);
        InboundMessageSnapshot.TryGetFor(unpacked.Message, out var snapshot).Should().BeTrue();

        await using var h = new Harness();
        h.Sink.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Dispatch(unpacked);
        await h.Sink.Started.Task.WaitAsync(Flush);

        h.Handler.OutstandingBytes.Should().Be(Encoding.UTF8.GetByteCount(snapshot.PlaintextJson));
        h.Sink.Gate.TrySetResult();
        await h.Handler.FlushAsync(Flush);
        h.Handler.OutstandingBytes.Should().Be(0);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Ctor_rejects_a_non_positive_byte_budget(long byteBudget)
    {
        var act = () => new ForwardHandler(NewProcessor(), new ForwardHandlerTests.CapturingSink(), new InMemoryForwardRecipientRegistry(), null, 4, null, byteBudget);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("byteBudget");
    }

    // ── Probe 8: accounting under concurrent dispatch / flush / dispose ───────────────────

    [Fact]
    public async Task Parallel_dispatch_flush_and_dispose_never_throw_and_leave_no_accounting_behind()
    {
        var h = new Harness(capacity: 16, shutdownGrace: TimeSpan.FromSeconds(5));
        var registry = h.Recipients;
        var stop = new CancellationTokenSource();

        var producers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var outcome = await h.Dispatch(AsDecryptedByMediator(ForwardHandlerTests.Forward(Bob)));
                outcome.Result.Should().Be(DispatchResult.NoReply);
            }
        })).ToArray();
        var churn = Task.Run(async () =>
        {
            // Register / unregister the recipient while forwards are being screened.
            while (!stop.IsCancellationRequested)
            {
                registry.Unregister(Bob);
                registry.Register(Bob);
                await Task.Yield();
            }
        });
        var flushers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try { await h.Handler.FlushAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or TimeoutException) { }
        })).ToArray();
        var disposer = Task.Run(async () =>
        {
            await Task.Delay(20);
            h.Handler.Dispose(); // synchronous shutdown while producers are still dispatching
        });

        await Task.WhenAll(producers.Concat(flushers).Append(disposer)).WaitAsync(TimeSpan.FromSeconds(30));
        stop.Cancel();
        await churn;
        await h.Handler.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));

        h.Handler.Outstanding.Should().Be(0, "every admitted item released its slot exactly once");
        h.Handler.OutstandingBytes.Should().Be(0);
        h.Logger.Entries.Should().NotContain(e => e.Formatted.Contains("pump faulted"));
        await h.DisposeAsync();
    }

    // ── Probe 11: the re-wrap paths fed wire bytes that are not JSON ──────────────────────

    [Fact]
    public async Task Non_JSON_attachment_with_extra_routing_keys_is_refused_as_malformed_not_as_an_argument_fault()
    {
        var routingKey = TestKeyMaterial.Generate(KeyType.X25519, "did:example:r#k").PublicJwk;
        var processor = NewProcessor(new ForwardProcessorOptions { ExtraRecipientRoutingKeys = new[] { routingKey } });
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(Mediator)
            .WithBody(new JsonObject { ["next"] = Bob })
            .WithAttachment(new Attachment { Data = new AttachmentData { Base64 = DidComm.Jose.Base64Url.EncodeUtf8("not json " + Marker) } })
            .Build();

        var act = async () => await processor.ProcessAsync(AsDecryptedByMediator(forward));

        var failure = (await act.Should().ThrowAsync<MalformedMessageException>()).Which;
        failure.InnerException.Should().BeOfType<ArgumentException>();
        failure.ShouldNotQuoteTheValue();
        failure.Message.Should().NotContain(Marker);
    }

    [Fact]
    public async Task Rewrap_of_a_next_with_no_DID_part_is_refused_as_malformed_not_as_an_argument_fault()
    {
        // The handler drops a non-DID `next` before the processor runs; the string overload and a
        // host calling the processor directly do not, so the processor must still fail typed.
        var processor = NewProcessor(new ForwardProcessorOptions { Mode = RewrapMode.ReanoncryptToNext });
        var forward = ForwardMessage.Create(Mediator, "#" + Marker, new[] { Inner });

        var act = async () => await processor.ProcessAsync(AsDecryptedByMediator(forward));

        var failure = (await act.Should().ThrowAsync<MalformedMessageException>()).Which;
        failure.Message.Should().NotContain(Marker);
    }

    [Fact]
    public async Task Pass_through_without_extra_keys_still_relays_non_JSON_bytes_opaquely()
    {
        // Pins that the re-embed mapping does not reach into the verbatim path.
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithTo(Mediator)
            .WithBody(new JsonObject { ["next"] = Bob })
            .WithAttachment(new Attachment { Data = new AttachmentData { Base64 = DidComm.Jose.Base64Url.EncodeUtf8("opaque") } })
            .Build();

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be("opaque");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static void AssertNothingEchoes(ForwardHandlerTests.CapturingLogger logger, string marker)
    {
        foreach (var entry in logger.Entries)
        {
            entry.Formatted.Should().NotContain(marker);
            foreach (var value in entry.State.Values)
                (value?.ToString() ?? string.Empty).Should().NotContain(marker);
            for (var e = entry.Exception; e is not null; e = e.InnerException)
                e.Message.Should().NotContain(marker);
        }
    }
}
