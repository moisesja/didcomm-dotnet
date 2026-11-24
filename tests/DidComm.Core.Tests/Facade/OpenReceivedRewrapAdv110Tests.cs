using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Tests.Facade.MediatedClientTestWorld;

namespace DidComm.Tests.Facade;

/// <summary>
/// #110 red team (pass 1), Low: what opening one re-wrapped layer allocates on a plain receiving
/// endpoint. The layer's <c>data.json</c> is written out again before the next unpack checks its
/// size, and the default JSON writer escapes characters such as <c>&lt;</c> six times longer
/// (<c><</c>), so a member the inner envelope ignores is inflated before the size check refuses
/// it. Measured against the same layer with a junk member the writer does not escape. The count is the
/// process's, so the probe runs alone (other tests' allocations would blur it).
/// </summary>
[Collection(DidComm.Tests.Protocols.Routing.ForwardRetentionProbeCollection.Name)]
public sealed class OpenReceivedRewrapAdv110Tests(ITestOutputHelper output)
{
    private readonly MediatedClientTestWorld _world = new();

    [Fact]
    public async Task Opening_a_layer_allocates_no_more_for_junk_the_writer_escapes_than_for_junk_it_does_not()
    {
        var plain = await MeasureAsync('a');
        var escaped = await MeasureAsync('<');

        output.WriteLine($"plain junk:   body {plain.Body:N0} B, unpack {plain.Unpack:N0} B, open {plain.Open:N0} B, refused: {plain.Refused}");
        output.WriteLine($"escaped junk: body {escaped.Body:N0} B, unpack {escaped.Unpack:N0} B, open {escaped.Open:N0} B, refused: {escaped.Refused}");
        output.WriteLine($"open / body: plain {(double)plain.Open / plain.Body:F1}x, escaped {(double)escaped.Open / escaped.Body:F1}x");

        // Written back with the library's own encoder, the layer is no longer inflated: escaped and
        // plain junk are handled alike (the pin first asserted the inflated layer's refusal, which
        // only the inflation caused).
        escaped.Refused.Should().Be(plain.Refused);
        escaped.Open.Should().BeLessThan(2 * plain.Open, "a layer's data.json is written out without inflating characters the inner envelope never reads");
    }

    private async Task<(long Body, long Unpack, long Open, bool Refused)> MeasureAsync(char junk)
    {
        var inner = await _world.AuthcryptAsync(Note());
        var jwe = JsonNode.Parse(inner)!.AsObject();
        jwe["x"] = new string(junk, 740_000);
        var forward = Forward(Bob, Bob, new Attachment
        {
            Id = Guid.NewGuid().ToString("N"),
            MediaType = ForwardConstants.PayloadMediaType,
            Data = new AttachmentData { Json = jwe },
        });
        var packed = await _world.AnoncryptAsync(forward);
        var client = _world.BobClient();
        // Warm the paths once so JIT and pools do not count.
        var warm = await client.UnpackAsync(packed);
        try { await client.OpenReceivedRewrapAsync(warm, packed, CancellationToken.None); } catch (DidCommException) { }

        GC.Collect();
        var start = GC.GetTotalAllocatedBytes(precise: true);
        var received = await client.UnpackAsync(packed);
        var afterUnpack = GC.GetTotalAllocatedBytes(precise: true);
        var refused = false;
        try
        {
            await client.OpenReceivedRewrapAsync(received, packed, CancellationToken.None);
        }
        catch (MalformedMessageException)
        {
            refused = true;
        }
        var afterOpen = GC.GetTotalAllocatedBytes(precise: true);
        return (System.Text.Encoding.UTF8.GetByteCount(packed), afterUnpack - start, afterOpen - afterUnpack, refused);
    }
}

/// <summary>
/// #110 red team (pass 1), Low: on an endpoint with the routing role, a layer addressed to this agent
/// that does not decrypt should be tried once by <see cref="DidCommClient.OpenSelfAddressedMessageAsync"/>;
/// today the restricted unpack and then the full one each try it, before the forward handler tries
/// it a third time. Measured as secret lookups for the layer's key.
/// </summary>
public sealed class OpenSelfAddressedAttemptsAdv110Tests(ITestOutputHelper output)
{
    private readonly MediatedClientTestWorld _world = new();

    [Fact]
    public async Task A_layer_that_does_not_decrypt_is_tried_once_before_the_forward_handler()
    {
        var good = await _world.AnoncryptAsync(Note(from: null));
        var jwe = JsonNode.Parse(good)!.AsObject();
        var tag = jwe["tag"]!.GetValue<string>();
        jwe["tag"] = (tag[0] == 'A' ? "B" : "A") + tag[1..];
        var broken = jwe.ToJsonString();
        var forward = await _world.AnoncryptAsync(ForwardMessage.Create(Bob, Bob, new[] { broken }));

        var secrets = new SpySecretsResolver(_world.BobKey.PrivateJwk);
        var client = new DidCommClient(secrets, new NetDidKeyService(_world.Resolver), _world.Services, _world.Router, _world.BobOptions, new DataProofsDotnet.Jose.JoseCryptoProvider());
        var received = await client.UnpackAsync(forward);

        var before = secrets.FindPresentCalls;
        var opened = await client.OpenSelfAddressedMessageAsync(received, CancellationToken.None);
        var attempts = secrets.FindPresentCalls - before;

        before = secrets.FindPresentCalls;
        var direct = await Record.ExceptionAsync(() => client.UnpackAsync(broken));
        var directAttempts = secrets.FindPresentCalls - before;

        output.WriteLine($"self-addressed opening: {attempts} lookups (then the forward handler's peel); direct: {directAttempts}");
        opened.Should().BeNull();
        direct.Should().NotBeNull();
        attempts.Should().Be(directAttempts, "a layer the restricted unpack cannot decrypt is not decrypted again by the full one");
    }
}
