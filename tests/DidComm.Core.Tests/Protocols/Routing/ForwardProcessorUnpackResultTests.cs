using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using AwesomeAssertions;
using NetCrypto;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// Plan matrix U1 (#90): the <see cref="ForwardProcessor.ProcessAsync(UnpackResult, CancellationToken)"/>
/// overload — equivalence with the string overload on a real anoncrypt forward, the FR-ROUTE-11
/// refusal set and its ordering against the FR-API-09 cap, the FR-ROUTE-09 expiry arithmetic, the
/// FR-ROUTE-10 <c>NotBefore</c> stamp on an injected clock, and the per-recipient
/// <see cref="IRecipientRoutingKeyProvider"/> contract (DID-subject argument, pass-through only,
/// one wrap per key).
/// </summary>
public sealed class ForwardProcessorUnpackResultTests
{
    private const string Mediator = "did:example:mediator";
    private const string Next = "did:example:n";
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";

    private static readonly DateTimeOffset Fixed = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    // ── Overload equivalence ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnpackResult_overload_matches_string_overload_on_a_real_anoncrypt_forward()
    {
        // Relative to the real clock: the string overload unpacks through the FR-API-05 check.
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var (processor, mediator, packed, _) = await AnoncryptedMediatorFor(Next, expiresTime: expires);

        var viaString = await processor.ProcessAsync(packed);
        var viaResult = await processor.ProcessAsync(await mediator.UnpackAsync(packed));

        viaResult.NextHop.Should().Be(viaString.NextHop).And.Be(Next);
        viaResult.OnwardPacked.Should().Equal(viaString.OnwardPacked);
        JsonNode.DeepEquals(JsonNode.Parse(Encoding.UTF8.GetString(viaResult.OnwardPacked)), JsonNode.Parse(Inner))
            .Should().BeTrue("the relayed bytes are the inner payload (member order is canonicalised at pack time)");
        viaResult.ExpiresTime.Should().Be(viaString.ExpiresTime).And.Be(expires);
        viaResult.Delay.Should().BeNull();
        viaString.Delay.Should().BeNull();
        viaResult.NotBefore.Should().BeNull();
        viaString.NotBefore.Should().BeNull();
    }

    // ── FR-ROUTE-11 refusal set ───────────────────────────────────────────────────────────

    [Fact]
    public async Task String_overload_refuses_a_plaintext_forward_with_ConsistencyException()
    {
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(ForwardMessage.Create(Mediator, Next, new[] { Inner }));
        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());

        var act = async () => await processor.ProcessAsync(packed);

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("FR-ROUTE-11").And.NotContain(Next);
    }

    [Fact]
    public async Task UnpackResult_overload_refuses_Encrypted_false()
    {
        var act = async () => await NewProcessor().ProcessAsync(AsPlaintext(Forward()));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("not encrypted").And.Contain("FR-ROUTE-11");
    }

    [Fact]
    public async Task UnpackResult_overload_refuses_signed_only()
    {
        var act = async () => await NewProcessor().ProcessAsync(AsSignedOnly(Forward()));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("signed-only").And.NotContain(SignerKid);
    }

    [Fact]
    public async Task UnpackResult_overload_refuses_null_RecipientKid()
    {
        var act = async () => await NewProcessor().ProcessAsync(AsEncryptedWithoutRecipientKid(Forward()));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("not decrypted with a key this mediator holds");
    }

    [Fact]
    public async Task UnpackResult_overload_refuses_empty_RecipientKid()
    {
        // An empty kid is not "held": the check is IsNullOrEmpty, not a null test.
        var act = async () => await NewProcessor().ProcessAsync(AsDecryptedByMediator(Forward(), recipientKid: ""));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("not decrypted with a key this mediator holds");
    }

    [Fact]
    public async Task Non_forward_is_refused_with_ConsistencyException_that_does_not_echo_the_type()
    {
        var nonForward = new MessageBuilder()
            .WithType("https://didcomm.org/trust-ping/2.0/ping")
            .WithBody(new JsonObject())
            .Build();

        var act = async () => await NewProcessor().ProcessAsync(AsDecryptedByMediator(nonForward));

        var failure = (await act.Should().ThrowAsync<ConsistencyException>()).Which;
        failure.Message.Should().Contain("non-forward message").And.Contain(ForwardConstants.ForwardTypeUri);
        failure.Message.Should().NotContain("trust-ping", "the inbound type is a wire value (FR-API-09 posture)");
    }

    // ── Ordering: FR-API-09 cap and FR-ROUTE-01 structure before the posture check ────────

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(100_000)]
    public async Task Malformed_next_throws_MalformedMessageException_before_the_posture_checks(int length)
    {
        // Plaintext input: if the posture check ran first this would be a ConsistencyException.
        var forward = ForwardMessage.Create(Mediator, LengthCap.KidOfLength(Next, length), new[] { Inner });

        var act = async () => await NewProcessor().ProcessAsync(AsPlaintext(forward));

        var failure = (await act.Should().ThrowAsync<MalformedMessageException>()).Which;
        failure.Message.Should().Contain("'next'");
        failure.ShouldNotQuoteTheValue();
    }

    [Fact]
    public async Task Missing_attachments_throw_MalformedMessageException_before_the_posture_checks()
    {
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithBody(new JsonObject { ["next"] = Next })
            .Build();

        var act = async () => await NewProcessor().ProcessAsync(AsPlaintext(forward));

        (await act.Should().ThrowAsync<MalformedMessageException>())
            .Which.Message.Should().Contain("attachments");
    }

    [Fact]
    public async Task Non_string_next_throws_MalformedMessageException_before_the_posture_checks()
    {
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithBody(new JsonObject { ["next"] = 42 })
            .WithAttachment(new Attachment { Data = new AttachmentData { Json = JsonNode.Parse(Inner) } })
            .Build();

        var act = async () => await NewProcessor().ProcessAsync(AsPlaintext(forward));

        (await act.Should().ThrowAsync<MalformedMessageException>())
            .Which.Message.Should().Contain("'next'");
    }

    [Fact]
    public async Task Posture_is_checked_before_the_attachment_count()
    {
        // Two attachments on a plaintext forward: the posture refusal wins, not the count refusal.
        var forward = ForwardMessage.Create(Mediator, Next, new[] { Inner, Inner });

        var act = async () => await NewProcessor().ProcessAsync(AsPlaintext(forward));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("not encrypted");
    }

    // ── FR-ROUTE-09 expiry arithmetic ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(100L, 100L, 0.0, false)]  // now == expires: not yet expired
    [InlineData(100L, 101L, 0.0, true)]   // one second past
    [InlineData(100L, 101L, 1.0, false)]  // skew absorbs one second
    [InlineData(100L, 102L, 1.0, true)]   // skew exhausted
    [InlineData(100L, 101L, 1.9, false)]  // fractional skew floors to 1 s, still absorbs
    [InlineData(100L, 102L, 1.9, true)]   // ...but not two
    [InlineData(0L, 1L, 0.0, true)]       // expires_time 0 is the epoch, long gone
    [InlineData(-5L, 0L, 0.0, true)]      // negative expiry is already past
    [InlineData(null, 101L, 0.0, false)]  // no expiry never expires
    [InlineData(null, 253402300799L, 0.0, false)] // the last representable second (year 9999)
    public void IsExpired_mirrors_the_FR_API_05_arithmetic(long? expires, long nowSeconds, double skewSeconds, bool expected)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(nowSeconds);
        var skew = TimeSpan.FromSeconds(skewSeconds);
        var result = new ForwardProcessingResult(Next, Array.Empty<byte>(), expires, null);

        result.IsExpired(now, skew).Should().Be(expected);
        ForwardProcessingResult.IsExpiredAt(expires, now, skew).Should().Be(expected);
    }

    [Fact]
    public async Task ExpiresTime_is_propagated_from_the_inbound_forward()
    {
        var expires = Fixed.AddMinutes(5).ToUnixTimeSeconds();
        var forward = ForwardMessage.Create(Mediator, Next, new[] { Inner }, expiresTimeEpochSeconds: expires);

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.ExpiresTime.Should().Be(expires);
        result.IsExpired(Fixed, TimeSpan.Zero).Should().BeFalse();
        result.IsExpired(Fixed.AddMinutes(6), TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public async Task Processor_does_not_enforce_expiry_itself()
    {
        // The processor is a pure transformation; the handler (or host) judges IsExpired. An
        // already-expired forward still processes and reports its expiry for the caller to act on.
        var forward = ForwardMessage.Create(Mediator, Next, new[] { Inner }, expiresTimeEpochSeconds: 1);

        var result = await NewProcessor(didCommOptions: new DidCommOptions { Clock = () => Fixed }).ProcessAsync(AsDecryptedByMediator(forward));

        result.ExpiresTime.Should().Be(1);
        result.IsExpired(Fixed, TimeSpan.Zero).Should().BeTrue();
    }

    // ── FR-ROUTE-10 NotBefore ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotBefore_is_now_plus_delay_on_the_injected_clock()
    {
        var processor = NewProcessor(didCommOptions: new DidCommOptions { Clock = () => Fixed });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(), "500")));

        result.Delay.Should().Be(TimeSpan.FromMilliseconds(500));
        result.NotBefore.Should().Be(Fixed.AddMilliseconds(500));
    }

    [Fact]
    public async Task NotBefore_for_negative_delay_lies_within_now_and_now_plus_magnitude()
    {
        var processor = NewProcessor(didCommOptions: new DidCommOptions { Clock = () => Fixed });

        for (var run = 0; run < 25; run++)
        {
            var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(), "-500")));

            result.Delay.Should().NotBeNull();
            result.Delay!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
            result.NotBefore.Should().NotBeNull();
            result.NotBefore!.Value.Should().BeOnOrAfter(Fixed).And.BeOnOrBefore(Fixed.AddMilliseconds(500));
            result.NotBefore.Should().Be(Fixed + result.Delay.Value, "NotBefore is the same sample as Delay");
        }
    }

    [Fact]
    public async Task NotBefore_is_null_when_delay_is_absent()
    {
        var processor = NewProcessor(didCommOptions: new DidCommOptions { Clock = () => Fixed });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(Forward()));

        result.Delay.Should().BeNull();
        result.NotBefore.Should().BeNull();
    }

    [Fact]
    public async Task Zero_delay_yields_NotBefore_equal_to_now()
    {
        var processor = NewProcessor(didCommOptions: new DidCommOptions { Clock = () => Fixed });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(), "0")));

        result.Delay.Should().Be(TimeSpan.Zero);
        result.NotBefore.Should().Be(Fixed);
    }

    [Theory]
    [InlineData("\"500\"")]       // a string, not a number
    [InlineData("1.5")]           // a float: not an integer millisecond count
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[500]")]
    [InlineData("{\"ms\":500}")]
    public async Task Non_integer_delay_milli_is_ignored(string json)
    {
        var processor = NewProcessor(didCommOptions: new DidCommOptions { Clock = () => Fixed });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(), json)));

        result.Delay.Should().BeNull();
        result.NotBefore.Should().BeNull();
        result.NextHop.Should().Be(Next, "a bad hint never blocks the relay");
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1_000_000_000_000_000L)] // ~31,700 years: fits a TimeSpan, not a DateTimeOffset
    [InlineData((long)int.MaxValue + 1)]
    public async Task Huge_positive_delay_milli_is_capped_and_never_throws(long huge)
    {
        // Symmetric with the negative path: a hostile hint must not become an OverflowException /
        // ArgumentOutOfRangeException out of the processor (the sink only ever sees a bounded hold).
        var processor = NewProcessor(didCommOptions: new DidCommOptions { Clock = () => Fixed });
        var forward = WithDelay(Forward(), huge.ToString(CultureInfo.InvariantCulture));

        var result = await processor.ProcessAsync(AsDecryptedByMediator(forward));

        result.Delay.Should().NotBeNull();
        result.Delay!.Value.TotalMilliseconds.Should().BeInRange(0, int.MaxValue);
        result.NotBefore.Should().Be(Fixed + result.Delay.Value);
    }

    [Fact]
    public async Task Delay_at_int_MaxValue_milliseconds_is_honoured_exactly()
    {
        var processor = NewProcessor(didCommOptions: new DidCommOptions { Clock = () => Fixed });

        var result = await processor.ProcessAsync(AsDecryptedByMediator(WithDelay(Forward(), int.MaxValue.ToString(CultureInfo.InvariantCulture))));

        result.Delay.Should().Be(TimeSpan.FromMilliseconds(int.MaxValue));
        result.NotBefore.Should().Be(Fixed.AddMilliseconds(int.MaxValue));
    }

    // ── IRecipientRoutingKeyProvider contract ─────────────────────────────────────────────

    [Theory]
    [InlineData("did:example:n#key-1", "did:example:n")]
    [InlineData("did:example:n", "did:example:n")]
    [InlineData("did:example:n/path?query=1", "did:example:n")]
    [InlineData("did:example:n;service=x#frag", "did:example:n")]
    public async Task Routing_key_provider_receives_the_DID_subject_of_next_in_PassThrough(string next, string expectedSubject)
    {
        var provider = new SpyRoutingKeyProvider();
        var forward = ForwardMessage.Create(Mediator, next, new[] { Inner });

        var result = await NewProcessor(routingKeys: provider).ProcessAsync(AsDecryptedByMediator(forward));

        provider.Requested.Should().ContainSingle().Which.Should().Be(expectedSubject);
        result.NextHop.Should().Be(next, "the host receives next unchanged");
        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(Inner, "no keys → verbatim pass-through");
    }

    [Fact]
    public async Task Routing_key_provider_is_not_called_for_a_non_DID_next_in_PassThrough()
    {
        // J6: within the FR-API-09 cap but not a DID — the lookup is skipped and the bytes pass through.
        var provider = new SpyRoutingKeyProvider();
        var forward = ForwardMessage.Create(Mediator, "not-a-did", new[] { Inner });

        var result = await NewProcessor(routingKeys: provider).ProcessAsync(AsDecryptedByMediator(forward));

        provider.Requested.Should().BeEmpty();
        result.NextHop.Should().Be("not-a-did");
        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(Inner);
    }

    [Fact]
    public async Task Routing_key_provider_is_not_called_in_rewrap_mode()
    {
        var nextKey = TestKeyMaterial.Generate(KeyType.X25519, Next + "#ka-1");
        var resolver = new SpyDidResolver(LengthCap.Doc(Next, keyAgreement: nextKey.PublicJwk));
        var provider = new SpyRoutingKeyProvider(nextKey.PublicJwk);
        var processor = NewProcessor(
            new ForwardProcessorOptions(Mode: RewrapMode.ReanoncryptToNext),
            routingKeys: provider,
            keyService: new NetDidKeyService(resolver));

        var result = await processor.ProcessAsync(AsDecryptedByMediator(Forward()));

        provider.Requested.Should().BeEmpty("rewrap and extra wraps are mutually exclusive");
        resolver.Requested.Should().Contain(Next);
        var nextHop = new DidCommClient(new SpySecretsResolver(nextKey.PrivateJwk), new NetDidKeyService(resolver), new DidCommOptions());
        var unwrapped = await nextHop.UnpackAsync(Encoding.UTF8.GetString(result.OnwardPacked));
        unwrapped.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri);
    }

    [Fact]
    public async Task Routing_key_provider_is_not_called_when_rewrap_mode_faults()
    {
        var provider = new SpyRoutingKeyProvider();
        var processor = NewProcessor(new ForwardProcessorOptions(Mode: RewrapMode.ReanoncryptToNext), routingKeys: provider);

        var act = async () => await processor.ProcessAsync(AsDecryptedByMediator(Forward()));

        await act.Should().ThrowAsync<DidResolutionException>();
        provider.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_keys_wrap_the_payload_once_per_key()
    {
        var extra = TestKeyMaterial.Generate(KeyType.X25519, "did:example:extra#ka-1");
        var provider = new SpyRoutingKeyProvider(extra.PublicJwk);
        var forward = ForwardMessage.Create(Mediator, Next + "#key-1", new[] { Inner });

        var result = await NewProcessor(routingKeys: provider).ProcessAsync(AsDecryptedByMediator(forward));

        result.NextHop.Should().Be(Next + "#key-1");
        var layer = await UnwrapWith(extra, "did:example:extra", result.OnwardPacked);
        layer.Encrypted.Should().BeTrue();
        layer.AnonymousSender.Should().BeTrue("forward layers are anoncrypt");
        layer.RecipientKid.Should().Be("did:example:extra#ka-1");
        layer.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri);
        layer.Message.To.Should().Equal("did:example:extra");
        layer.Message.Body!["next"]!.GetValue<string>().Should().Be(Next + "#key-1", "the innermost wrap names the original next");
        // The wrap re-serialises the JWE with canonical member order; compare as JSON, not text.
        JsonNode.DeepEquals(layer.Message.Attachments.Should().ContainSingle().Which.Data.Json, JsonNode.Parse(Inner))
            .Should().BeTrue("the innermost wrap carries the inner payload unchanged");
        layer.Message.AdditionalHeaders.Should().Match<IDictionary<string, JsonElement>?>(h => h == null || !h.ContainsKey("please_ack"));
    }

    [Fact]
    public async Task Two_provider_keys_wrap_outer_to_inner_in_list_order()
    {
        var outer = TestKeyMaterial.Generate(KeyType.X25519, "did:example:outer#ka-1");
        var inner = TestKeyMaterial.Generate(KeyType.X25519, "did:example:inner#ka-1");
        var provider = new SpyRoutingKeyProvider(outer.PublicJwk, inner.PublicJwk);

        var result = await NewProcessor(routingKeys: provider).ProcessAsync(AsDecryptedByMediator(Forward()));

        var outerLayer = await UnwrapWith(outer, "did:example:outer", result.OnwardPacked);
        outerLayer.Message.Body!["next"]!.GetValue<string>().Should().Be("did:example:inner");
        var innerPacked = outerLayer.Message.Attachments!.Single().Data.Json!.ToJsonString();

        var innerLayer = await UnwrapWith(inner, "did:example:inner", Encoding.UTF8.GetBytes(innerPacked));
        innerLayer.Message.Body!["next"]!.GetValue<string>().Should().Be(Next);
        JsonNode.DeepEquals(innerLayer.Message.Attachments!.Single().Data.Json, JsonNode.Parse(Inner)).Should().BeTrue();
    }

    [Fact]
    public async Task Three_arg_ctor_uses_the_static_provider_from_options()
    {
        var extra = TestKeyMaterial.Generate(KeyType.X25519, "did:example:extra#ka-1");
        var processor = new ForwardProcessor(
            NewClient(), new EmptyKeyService(),
            new ForwardProcessorOptions(ExtraRecipientRoutingKeys: new[] { extra.PublicJwk }));

        var result = await processor.ProcessAsync(AsDecryptedByMediator(Forward()));

        var layer = await UnwrapWith(extra, "did:example:extra", result.OnwardPacked);
        layer.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri);
        layer.Message.Body!["next"]!.GetValue<string>().Should().Be(Next);
    }

    [Fact]
    public async Task Five_arg_ctor_ignores_ExtraRecipientRoutingKeys_in_favour_of_the_provider()
    {
        // Documented on the ctor: the provider is the source of extra wraps on this path.
        var ignored = TestKeyMaterial.Generate(KeyType.X25519, "did:example:ignored#ka-1");
        var processor = new ForwardProcessor(
            NewClient(), new EmptyKeyService(),
            new ForwardProcessorOptions(ExtraRecipientRoutingKeys: new[] { ignored.PublicJwk }),
            new SpyRoutingKeyProvider(),
            new DidCommOptions());

        var result = await processor.ProcessAsync(AsDecryptedByMediator(Forward()));

        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(Inner);
    }

    [Fact]
    public async Task Provider_fault_propagates_in_PassThrough()
    {
        var processor = NewProcessor(routingKeys: new ThrowingRoutingKeyProvider());

        var act = async () => await processor.ProcessAsync(AsDecryptedByMediator(Forward()));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider down");
    }

    // ── FR-ROUTE-07 and next shape ────────────────────────────────────────────────────────

    [Fact]
    public async Task Please_ack_on_a_forward_is_ignored_FR_ROUTE_07()
    {
        var forward = Forward();
        forward.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["please_ack"] = JsonDocument.Parse("""["RECEIPT"]""").RootElement.Clone(),
        };

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.NextHop.Should().Be(Next);
        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(Inner, "the ack request is neither honoured nor propagated");
    }

    [Theory]
    [InlineData("did:example:n")]
    [InlineData("did:example:n#key-1")]
    [InlineData("did:example:n/p?q=1#key-1")]
    public async Task Next_passes_through_unchanged_as_NextHop(string next)
    {
        var forward = ForwardMessage.Create(Mediator, next, new[] { Inner });

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.NextHop.Should().Be(next);
    }

    [Fact]
    public async Task Forward_addressed_to_someone_else_is_still_processed_because_posture_is_kid_based()
    {
        // The `to` header is attacker-settable on anoncrypt; RecipientKid is the proof of a held key.
        var forward = ForwardMessage.Create("did:example:someone-else", Next, new[] { Inner });

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.NextHop.Should().Be(Next);
    }

    [Fact]
    public async Task Null_UnpackResult_throws_ArgumentNullException()
    {
        var act = async () => await NewProcessor().ProcessAsync((UnpackResult)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static Message Forward() => ForwardMessage.Create(Mediator, Next, new[] { Inner });

    private static Message WithDelay(Message forward, string delayJson)
    {
        forward.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = JsonDocument.Parse(delayJson).RootElement.Clone(),
        };
        return forward;
    }

    /// <summary>Unpack one forward layer addressed to <paramref name="did"/> with <paramref name="key"/>.</summary>
    private static async Task<UnpackResult> UnwrapWith(TestKeyMaterial key, string did, byte[] packed)
    {
        var resolver = new SpyDidResolver(LengthCap.Doc(did, keyAgreement: key.PublicJwk));
        var client = new DidCommClient(new SpySecretsResolver(key.PrivateJwk), new NetDidKeyService(resolver), new DidCommOptions());
        return await client.UnpackAsync(Encoding.UTF8.GetString(packed));
    }

    /// <summary>A mediator holding its own key-agreement key, and a forward anoncrypted to it.</summary>
    private static async Task<(ForwardProcessor Processor, DidCommClient Mediator, string Packed, SpyDidResolver Resolver)> AnoncryptedMediatorFor(
        string next, long? expiresTime = null)
    {
        var mediatorKey = TestKeyMaterial.Generate(KeyType.X25519, Mediator + "#ka-1");
        var resolver = new SpyDidResolver(LengthCap.Doc(Mediator, keyAgreement: mediatorKey.PublicJwk));
        var keyService = new NetDidKeyService(resolver);
        var mediator = new DidCommClient(new SpySecretsResolver(mediatorKey.PrivateJwk), keyService, new DidCommOptions());
        var packed = (await mediator.PackEncryptedAsync(
            ForwardMessage.Create(Mediator, next, new[] { Inner }, expiresTimeEpochSeconds: expiresTime),
            new PackEncryptedOptions(Recipients: new[] { Mediator }))).Message;
        return (new ForwardProcessor(mediator, keyService, new ForwardProcessorOptions()), mediator, packed.ShouldFitTheReceiveLimit(), resolver);
    }

    private sealed class SpyRoutingKeyProvider : IRecipientRoutingKeyProvider
    {
        private readonly IReadOnlyList<Jwk> _keys;
        public SpyRoutingKeyProvider(params Jwk[] keys) => _keys = keys;
        public ConcurrentQueue<string> Requested { get; } = new();
        public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct)
        {
            Requested.Enqueue(recipientDid);
            return ValueTask.FromResult(_keys);
        }
    }

    private sealed class ThrowingRoutingKeyProvider : IRecipientRoutingKeyProvider
    {
        public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct)
            => throw new InvalidOperationException("provider down");
    }
}
