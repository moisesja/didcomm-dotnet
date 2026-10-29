using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using AwesomeAssertions;
using NetCrypto;
using Xunit;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// Pins the key-id bound on a forward's <c>body.next</c> (FR-API-09, #80). In pass-through mode the
/// value goes to the host unchanged as <see cref="ForwardProcessingResult.NextHop"/>, and the host
/// resolves it, so an unbounded <c>next</c> passes the DID-parse cost on to the host. In rewrap mode
/// the mediator strips the fragment and resolves the DID itself. <c>next</c> may be a DID URL naming
/// a routing key, so the hostile value is a short DID plus a long fragment, and the next hop's
/// document is served: without the bound, rewrap mode would resolve it and re-encrypt.
/// </summary>
public sealed class ForwardNextLengthCapTests
{
    private const string Mediator = "did:example:mediator";
    private const string NextDid = "did:example:next";
    private const string InnerPayload = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(1_000_000)]
    public void TryParse_refuses_next_over_the_cap_without_quoting_it(int length)
    {
        var forward = ForwardMessage.Create(Mediator, LengthCap.KidOfLength(NextDid, length), new[] { InnerPayload });

        Action act = () => ForwardMessage.TryParse(forward, out _, out _);

        var failure = act.Should().Throw<MalformedMessageException>().Which;
        failure.Message.Should().Contain("'next'").And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
    }

    [Fact]
    public void TryParse_accepts_next_at_exactly_the_cap()
    {
        var next = LengthCap.KidOfLength(NextDid, LengthCap.KeyIdCap);
        var forward = ForwardMessage.Create(Mediator, next, new[] { InnerPayload });

        ForwardMessage.TryParse(forward, out var parsed, out var payloads).Should().BeTrue();

        parsed.Should().Be(next);
        payloads.Should().ContainSingle();
    }

    [Theory]
    // A bare DID: no delimiter, so all of it is DID part.
    [InlineData(LengthCap.DidCap, "", true)]
    [InlineData(LengthCap.DidCap + 1, "", false)]
    // A DID URL naming a routing key.
    [InlineData(LengthCap.DidCap, "#key-1", true)]
    [InlineData(LengthCap.DidCap + 1, "#key-1", false)]
    public void TryParse_holds_the_DID_part_of_next_to_the_DID_cap(int didPartLength, string suffix, bool accepted)
    {
        // Every case is within the 4096 total; only the DID part decides.
        var next = LengthCap.KidWithDidPart(didPartLength, suffix);
        next.Length.Should().BeLessThan(LengthCap.KeyIdCap);
        var forward = ForwardMessage.Create(Mediator, next, new[] { InnerPayload });

        Action act = () => ForwardMessage.TryParse(forward, out _, out _);

        if (accepted)
        {
            act.Should().NotThrow();
            ForwardMessage.TryParse(forward, out var parsed, out _).Should().BeTrue();
            parsed.Should().Be(next);
        }
        else
        {
            var failure = act.Should().Throw<MalformedMessageException>().Which;
            failure.Message.Should().Contain("'next' has a DID part longer than the maximum of 2048");
            failure.ShouldNotQuoteTheValue();
        }
    }

    [Theory]
    [InlineData(RewrapMode.PassThrough)]
    [InlineData(RewrapMode.ReanoncryptToNext)]
    public async Task ProcessAsync_refuses_a_bare_DID_next_over_the_DID_cap_in_every_mode(RewrapMode mode)
    {
        // The next hop's document is served, so without the rule rewrap mode would resolve the
        // 2049-character DID, and pass-through would hand it to the host to resolve.
        var nextDid = LengthCap.DidOfLength(LengthCap.DidCap + 1);
        var nextKey = TestKeyMaterial.Generate(KeyType.X25519, nextDid + "#ka-1");
        var resolver = new SpyDidResolver(LengthCap.Doc(nextDid, keyAgreement: nextKey.PublicJwk));
        var (processor, packed) = await MediatorFor(nextDid, mode, resolver);

        var failure = (await processor.Invoking(p => p.ProcessAsync(packed))
            .Should().ThrowAsync<MalformedMessageException>()).Which;

        failure.Message.Should().Contain("'next' has a DID part longer than the maximum of 2048");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAsync_passes_a_bare_DID_next_of_exactly_the_DID_cap_through_to_the_host()
    {
        var nextDid = LengthCap.DidOfLength(LengthCap.DidCap);
        var (processor, packed) = await MediatorFor(nextDid, RewrapMode.PassThrough, new SpyDidResolver());

        var result = await processor.ProcessAsync(packed);

        result.NextHop.Should().Be(nextDid);
    }

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1, RewrapMode.PassThrough)]
    [InlineData(1_000_000, RewrapMode.PassThrough)]
    [InlineData(LengthCap.KeyIdCap + 1, RewrapMode.ReanoncryptToNext)]
    [InlineData(1_000_000, RewrapMode.ReanoncryptToNext)]
    public async Task ProcessAsync_refuses_next_over_the_cap_in_every_mode(int length, RewrapMode mode)
    {
        var nextKey = TestKeyMaterial.Generate(KeyType.X25519, NextDid + "#ka-1");
        var resolver = new SpyDidResolver(LengthCap.Doc(NextDid, keyAgreement: nextKey.PublicJwk));
        var (processor, packed) = await MediatorFor(LengthCap.KidOfLength(NextDid, length), mode, resolver);

        var failure = (await processor.Invoking(p => p.ProcessAsync(packed))
            .Should().ThrowAsync<MalformedMessageException>()).Which;

        failure.Message.Should().Contain("'next'").And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty("the forward is refused before the next hop is resolved");
    }

    [Fact]
    public async Task ProcessAsync_passes_next_at_exactly_the_cap_through_to_the_host()
    {
        var next = LengthCap.KidOfLength(NextDid, LengthCap.KeyIdCap);
        var (processor, packed) = await MediatorFor(next, RewrapMode.PassThrough, new SpyDidResolver());

        var result = await processor.ProcessAsync(packed);

        result.NextHop.Should().Be(next);
    }

    [Fact]
    public async Task ProcessAsync_rewraps_to_next_at_exactly_the_cap()
    {
        var nextKey = TestKeyMaterial.Generate(KeyType.X25519, NextDid + "#ka-1");
        var resolver = new SpyDidResolver(LengthCap.Doc(NextDid, keyAgreement: nextKey.PublicJwk));
        var next = LengthCap.KidOfLength(NextDid, LengthCap.KeyIdCap);
        var (processor, packed) = await MediatorFor(next, RewrapMode.ReanoncryptToNext, resolver);

        var result = await processor.ProcessAsync(packed);

        result.NextHop.Should().Be(next);
        resolver.Requested.Should().Contain(NextDid, "rewrap addresses the DID part of 'next'");
        var nextHop = new DidCommClient(
            new SpySecretsResolver(nextKey.PrivateJwk), new NetDidKeyService(resolver), new DidCommOptions());
        var unwrapped = await nextHop.UnpackAsync(Encoding.UTF8.GetString(result.OnwardPacked));
        unwrapped.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri);
    }

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(700_000)]
    public async Task ProcessAsync_refuses_next_over_the_cap_in_an_anoncrypted_forward(int length)
    {
        // The shape a sender really produces: the forward is anoncrypted to the mediator, and
        // 'next' only appears once the mediator has decrypted it.
        var (processor, packed, resolver) = await AnoncryptedMediatorFor(LengthCap.KidOfLength(NextDid, length), new ForwardProcessorOptions());

        var failure = (await processor.Invoking(p => p.ProcessAsync(packed))
            .Should().ThrowAsync<MalformedMessageException>()).Which;

        failure.Message.Should().Contain("'next'").And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().NotContain(NextDid, "the next hop is never resolved");
        resolver.Requested.Should().NotContain(d => d.Length > LengthCap.DidCap);
    }

    [Fact]
    public async Task ProcessAsync_passes_next_at_exactly_the_cap_through_from_an_anoncrypted_forward()
    {
        var next = LengthCap.KidOfLength(NextDid, LengthCap.KeyIdCap);
        var (processor, packed, _) = await AnoncryptedMediatorFor(next, new ForwardProcessorOptions());

        var result = await processor.ProcessAsync(packed);

        result.NextHop.Should().Be(next);
    }

    [Fact]
    public async Task ProcessAsync_refuses_next_over_the_cap_when_extra_routing_keys_are_configured()
    {
        // The third transformation: the mediator wraps the payload for pre-configured extra routing
        // keys addressed to 'next'. TryParse runs first, so the bound holds here too.
        var extraKey = TestKeyMaterial.Generate(KeyType.X25519, "did:example:extra#ka-1");
        var resolver = new SpyDidResolver();
        var (processor, packed) = await MediatorFor(
            LengthCap.KidOfLength(NextDid, LengthCap.KeyIdCap + 1), RewrapMode.PassThrough, resolver,
            extraRoutingKeys: new[] { extraKey.PublicJwk });

        var failure = (await processor.Invoking(p => p.ProcessAsync(packed))
            .Should().ThrowAsync<MalformedMessageException>()).Which;

        failure.Message.Should().Contain("'next'");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty();
    }

    /// <summary>A mediator holding its own key-agreement key, and a forward anoncrypted to it.</summary>
    private static async Task<(ForwardProcessor Processor, string Packed, SpyDidResolver Resolver)> AnoncryptedMediatorFor(
        string next, ForwardProcessorOptions options)
    {
        var mediatorKey = TestKeyMaterial.Generate(KeyType.X25519, Mediator + "#ka-1");
        var nextKey = TestKeyMaterial.Generate(KeyType.X25519, NextDid + "#ka-1");
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Mediator, keyAgreement: mediatorKey.PublicJwk),
            LengthCap.Doc(NextDid, keyAgreement: nextKey.PublicJwk));
        var keyService = new NetDidKeyService(resolver);
        var mediator = new DidCommClient(new SpySecretsResolver(mediatorKey.PrivateJwk), keyService, new DidCommOptions());
        var packed = (await mediator.PackEncryptedAsync(
            ForwardMessage.Create(Mediator, next, new[] { InnerPayload }),
            new PackEncryptedOptions(Recipients: new[] { Mediator }))).Message;
        return (new ForwardProcessor(mediator, keyService, options), packed.ShouldFitTheReceiveLimit(), resolver);
    }

    /// <summary>
    /// A mediator over <paramref name="resolver"/> and a plaintext forward to it. A plaintext forward
    /// keeps the test on the <c>next</c> check: the processor reads <c>next</c> the same way whatever
    /// envelope carried it.
    /// </summary>
    private static async Task<(ForwardProcessor Processor, string Packed)> MediatorFor(
        string next, RewrapMode mode, SpyDidResolver resolver, IReadOnlyList<Jwk>? extraRoutingKeys = null)
    {
        var keyService = new NetDidKeyService(resolver);
        var mediator = new DidCommClient(new SpySecretsResolver(), keyService, new DidCommOptions());
        var packed = await mediator.PackPlaintextAsync(ForwardMessage.Create(Mediator, next, new[] { InnerPayload }));
        var processor = new ForwardProcessor(mediator, keyService, new ForwardProcessorOptions(Mode: mode, ExtraRecipientRoutingKeys: extraRoutingKeys));
        return (processor, packed.ShouldFitTheReceiveLimit());
    }
}
