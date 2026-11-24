using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using Xunit;
using static DidComm.Tests.Facade.MediatedClientTestWorld;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Facade;

/// <summary>
/// #110: a forward layer addressed to this agent is opened once per request. On an endpoint with the
/// routing role, the look-ahead (<see cref="DidCommClient.OpenSelfAddressedMessageAsync"/>) opens
/// such a layer before dispatch. When it leaves the forward to the forward handler, the handler's
/// peel takes the look-ahead's result or refusal instead of opening the layer again. So a
/// self-addressed chain costs what it cost before the look-ahead, and the mediator's timing does
/// not tell such a forward from a direct one more than before (MediatorNextOracleAdvTests).
/// Measured as secret lookups for the layer's key.
/// </summary>
public sealed class SelfAddressedPeelOnceTests
{
    private readonly MediatedClientTestWorld _world = new();

    [Fact]
    public async Task A_layer_the_look_ahead_opened_is_not_opened_again_by_the_forward_handlers_peel()
    {
        var packed = await SelfAddressedAroundAsync(await NextHopForwardAsync());
        var bob = BobSide();
        var received = await bob.Client.UnpackAsync(packed);

        var lookAhead = await bob.Client.OpenSelfAddressedMessageAsync(received, CancellationToken.None);
        var before = bob.Secrets.FindPresentCalls;
        var peeled = await bob.Processor.PeelAsync(received, CancellationToken.None);
        var lookups = bob.Secrets.FindPresentCalls - before;

        lookAhead.Should().BeNull("a forward for the next hop inside is the forward handler's");
        lookups.Should().Be(0, "the look-ahead opened this layer on this request");
        ForwardMessage.TryParse(peeled.Message, out var next, out _).Should().BeTrue();
        next.Should().Be(Carol);
    }

    [Fact]
    public async Task The_same_bytes_unpacked_again_are_another_layer_and_the_peel_opens_it_itself()
    {
        var packed = await SelfAddressedAroundAsync(await NextHopForwardAsync());
        var bob = BobSide();
        var received = await bob.Client.UnpackAsync(packed);
        var peeledOnce = await LookAheadThenPeelAsync(bob, received);

        var again = await bob.Client.UnpackAsync(packed);
        var before = bob.Secrets.FindPresentCalls;
        var peeled = await bob.Processor.PeelAsync(again, CancellationToken.None);

        (bob.Secrets.FindPresentCalls - before).Should().BeGreaterThan(0, "only the request that unpacked a layer finds its opening");
        peeled.Message.Id.Should().Be(peeledOnce.Message.Id);
    }

    [Fact]
    public async Task A_recorded_opening_is_taken_once()
    {
        var packed = await SelfAddressedAroundAsync(await NextHopForwardAsync());
        var bob = BobSide();
        var received = await bob.Client.UnpackAsync(packed);
        await LookAheadThenPeelAsync(bob, received);

        var before = bob.Secrets.FindPresentCalls;
        await bob.Processor.PeelAsync(received, CancellationToken.None);

        (bob.Secrets.FindPresentCalls - before).Should().BeGreaterThan(0, "a second peel of the same layer opens it");
    }

    [Fact]
    public async Task A_layer_that_does_not_decrypt_is_refused_by_the_peel_without_being_tried_again()
    {
        var good = await _world.AnoncryptAsync(Note(from: null));
        var jwe = JsonNode.Parse(good)!.AsObject();
        var tag = jwe["tag"]!.GetValue<string>();
        jwe["tag"] = (tag[0] == 'A' ? "B" : "A") + tag[1..];
        var packed = await SelfAddressedAroundAsync(jwe.ToJsonString());
        var bob = BobSide();
        var received = await bob.Client.UnpackAsync(packed);
        var fresh = await bob.Client.UnpackAsync(packed);
        var direct = await Record.ExceptionAsync(() => bob.Processor.PeelAsync(fresh, CancellationToken.None));

        var lookAhead = await bob.Client.OpenSelfAddressedMessageAsync(received, CancellationToken.None);
        var before = bob.Secrets.FindPresentCalls;
        var refused = await Record.ExceptionAsync(() => bob.Processor.PeelAsync(received, CancellationToken.None));
        var lookups = bob.Secrets.FindPresentCalls - before;

        lookAhead.Should().BeNull();
        direct.Should().NotBeNull();
        refused.Should().NotBeNull().And.BeOfType(direct!.GetType(), "the peel throws what refused the look-ahead's opening");
        lookups.Should().Be(0, "the layer was tried once, by the look-ahead");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_layer_refused_for_its_shape_is_refused_by_the_peel_as_before(bool signedInside)
    {
        // A forward inside that is not anoncrypted plaintext: the restricted opening refuses it for
        // its shape (an authcrypt envelope by its header; a signed message inside an anoncrypt one
        // once decrypted), the look-ahead gives it its one full unpack, finds a forward and leaves
        // it to the handler.
        var nextHop = ForwardMessage.Create(Bob, Carol, new[] { await _world.AnoncryptAsync(Note(to: Carol), to: Carol) });
        var inside = signedInside ? await _world.AnoncryptSignedAsync(nextHop, signFrom: Alice) : await _world.AuthcryptAsync(nextHop);
        var packed = await SelfAddressedAroundAsync(inside);
        var bob = BobSide();
        var received = await bob.Client.UnpackAsync(packed);
        var fresh = await bob.Client.UnpackAsync(packed);
        var direct = await Record.ExceptionAsync(() => bob.Processor.PeelAsync(fresh, CancellationToken.None));

        var lookAhead = await bob.Client.OpenSelfAddressedMessageAsync(received, CancellationToken.None);
        var before = bob.Secrets.FindPresentCalls;
        var refused = await Record.ExceptionAsync(() => bob.Processor.PeelAsync(received, CancellationToken.None));
        var lookups = bob.Secrets.FindPresentCalls - before;

        lookAhead.Should().BeNull("a forward inside is the forward handler's");
        direct.Should().BeOfType<MalformedMessageException>().Which.RefusedShape.Should().BeTrue();
        refused.Should().NotBeNull().And.BeOfType(direct!.GetType());
        refused!.Message.Should().Be(direct.Message);
        lookups.Should().Be(0, "the restricted opening of this layer ran once, in the look-ahead");
    }

    [Fact]
    public async Task A_peel_through_another_facade_opens_the_layer_itself()
    {
        var packed = await SelfAddressedAroundAsync(await NextHopForwardAsync());
        var bob = BobSide();
        var other = BobSide();
        var received = await bob.Client.UnpackAsync(packed);
        await bob.Client.OpenSelfAddressedMessageAsync(received, CancellationToken.None);

        var before = other.Secrets.FindPresentCalls;
        var peeled = await other.Processor.PeelAsync(received, CancellationToken.None);

        (other.Secrets.FindPresentCalls - before).Should().BeGreaterThan(0, "an opening is taken only through the facade that recorded it");
        ForwardMessage.TryParse(peeled.Message, out var next, out _).Should().BeTrue();
        next.Should().Be(Carol);
    }

    private static async Task<UnpackResult> LookAheadThenPeelAsync((DidCommClient Client, SpySecretsResolver Secrets, ForwardProcessor Processor) bob, UnpackResult received)
    {
        await bob.Client.OpenSelfAddressedMessageAsync(received, CancellationToken.None);
        var before = bob.Secrets.FindPresentCalls;
        var peeled = await bob.Processor.PeelAsync(received, CancellationToken.None);
        (bob.Secrets.FindPresentCalls - before).Should().Be(0, "precondition: the look-ahead's opening was taken");
        return peeled;
    }

    /// <summary>An anoncrypted forward to Bob whose next is Carol: a forward for the next hop.</summary>
    private async Task<string> NextHopForwardAsync()
        => await _world.AnoncryptAsync(ForwardMessage.Create(Bob, Carol, new[] { await _world.AnoncryptAsync(Note(to: Carol), to: Carol) }));

    /// <summary>A layer re-wrapped for Bob around <paramref name="innerPacked"/>: next is the DID that decrypts it.</summary>
    private async Task<string> SelfAddressedAroundAsync(string innerPacked)
        => await _world.AnoncryptAsync(ForwardMessage.Create(Bob, Bob, new[] { innerPacked }));

    /// <summary>Bob's facade over a counting secrets resolver, and a forward processor over that facade.</summary>
    private (DidCommClient Client, SpySecretsResolver Secrets, ForwardProcessor Processor) BobSide()
    {
        var secrets = new SpySecretsResolver(_world.BobKey.PrivateJwk);
        var keyService = new NetDidKeyService(_world.Resolver);
        var client = new DidCommClient(secrets, keyService, _world.Services, _world.Router, _world.BobOptions, new JoseCryptoProvider());
        var processor = new ForwardProcessor(client, keyService, new ForwardProcessorOptions(), new StaticRecipientRoutingKeyProvider(null), _world.BobOptions);
        return (client, secrets, processor);
    }
}
