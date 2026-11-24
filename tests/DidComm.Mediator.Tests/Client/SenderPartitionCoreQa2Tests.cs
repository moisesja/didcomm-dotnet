using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Resolution;
using Microsoft.Extensions.DependencyInjection;
using NetDid.Core;
using NetDid.Core.Model;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;
using Xunit;
using DpBase64Url = DataProofsDotnet.Jose.Base64Url;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-2 for #111 (I-14, D-1): the sender a delivered message's unpack is counted against
/// (<see cref="SenderPartition"/>) is never a sender other than the one Core's unpack resolves. Each
/// case takes a real authcrypted message (did:peer identities, real keys) and reshapes its outer
/// envelope; the production unpack step (<see cref="DidCommClient.UnpackDeliveredAsync"/>) then runs
/// over a resolver that records every DID it is asked for, through both kinds of key service. The
/// partition is either anonymous, or the one DID the unpack resolved for a sender (or none, when the
/// unpack refused the envelope before any lookup).
/// </summary>
public sealed class SenderPartitionCoreQa2Tests
{
    /// <summary>Each reshaped outer envelope, with the partition it is expected to count against ("alice", "mallory" or null).</summary>
    public static TheoryData<string, string?> Shapes => new()
    {
        { "as packed (General JSON)", "alice" },
        { "the flattened JSON serialization", null },
        { "skid: another fragment of the sender's DID", "alice" },
        { "skid: a DID URL with a query and a fragment", "alice" },
        { "skid: a DID URL with a path", "alice" },
        { "skid: the bare DID", "alice" },
        { "skid and apu: another sender's key", "mallory" },
        { "apu disagrees with skid", "alice" },
        { "apu only", null },
        { "apu only, naming another sender", null },
        { "ECDH-ES carrying the skid", null },
        { "skid over the key-id limit", null },
        { "skid whose DID part is over the DID limit", null },
        { "two skid members", null },
        { "two alg members", null },
        { "two protected members", null },
        { "skid spelled in upper case", null },
        { "skid that is not a string", null },
        { "the method name in upper case", null },
        { "enc that ECDH-1PU does not allow", "alice" },
        { "anoncrypt(authcrypt)", null },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_partition_is_never_a_sender_other_than_the_one_Core_resolves(string shape, string? expected)
    {
        await using var world = await World.CreateAsync();
        var packed = await world.ShapeAsync(shape);
        var partition = SenderPartition.Of(packed);

        foreach (var bindingService in new[] { true, false })
        {
            var resolved = await world.SendersResolvedAsync(packed, bindingService);

            partition.Should().Be(world.Named(expected), "the partition of '{0}'", shape);
            if (partition is not null)
            {
                resolved.Should().OnlyContain(did => did == partition,
                    "a message counted against a sender resolves that sender, or no one ('{0}', binding service {1}; resolved: {2})",
                    shape, bindingService, string.Join(", ", resolved));
            }
        }
    }

    /// <summary>
    /// The genuine message, opened: its partition is its sender, and the unpack resolves exactly that
    /// sender (the control for the reshaped cases, which fail to decrypt).
    /// </summary>
    [Fact]
    public async Task The_genuine_message_opens_and_resolves_exactly_its_partitions_sender()
    {
        await using var world = await World.CreateAsync();
        var packed = await world.ShapeAsync("as packed (General JSON)");
        var opened = await world.Bob(bindingService: true).UnpackDeliveredAsync(packed, CancellationToken.None);

        opened.Result.Message.From.Should().Be(world.Alice.Did);
        SenderPartition.Of(packed).Should().Be(world.Alice.Did);
        (await world.SendersResolvedAsync(packed, bindingService: true)).Should().Equal(world.Alice.Did);
        (await world.SendersResolvedAsync(packed, bindingService: false)).Should().Equal(world.Alice.Did);
    }

    /// <summary>Three did:peer identities and the clients that pack as Alice or Mallory and open as Bob.</summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly PipelinePeers _peers;
        private readonly ServiceProvider _services;

        private World(PipelinePeers peers, ServiceProvider services, Samples.Shared.PeerIdentity alice, Samples.Shared.PeerIdentity mallory, Samples.Shared.PeerIdentity bob)
        {
            _peers = peers;
            _services = services;
            Alice = alice;
            Mallory = mallory;
            BobIdentity = bob;
        }

        public Samples.Shared.PeerIdentity Alice { get; }

        public Samples.Shared.PeerIdentity Mallory { get; }

        public Samples.Shared.PeerIdentity BobIdentity { get; }

        public static async Task<World> CreateAsync()
        {
            var peers = new PipelinePeers();
            var alice = await peers.NewAsync();
            var mallory = await peers.NewAsync();
            var bob = await peers.NewAsync();
            var services = new ServiceCollection();
            services.AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(peers.Secrets);
            });
            return new World(peers, services.BuildServiceProvider(), alice, mallory, bob);
        }

        public string? Named(string? name) => name switch
        {
            "alice" => Alice.Did,
            "mallory" => Mallory.Did,
            _ => null,
        };

        public DidCommClient Bob(bool bindingService)
        {
            var netDid = new NetDidKeyService(Recorder);
            IDidKeyService keys = bindingService ? netDid : new ListingOnly(netDid);
            return new DidCommClient(_peers.Secrets, keys, new DidCommOptions());
        }

        private RecordingResolver Recorder => _recorder ??= new RecordingResolver(_services.GetRequiredService<IDidResolver>());

        private RecordingResolver? _recorder;

        /// <summary>The DIDs, other than Bob's, the production unpack step resolved while opening <paramref name="packed"/> (its outcome ignored).</summary>
        public async Task<IReadOnlyList<string>> SendersResolvedAsync(string packed, bool bindingService)
        {
            var client = Bob(bindingService);
            Recorder.Clear();
            _ = await Record.ExceptionAsync(() => client.UnpackDeliveredAsync(packed, CancellationToken.None));
            return Recorder.Resolved.Where(did => did != BobIdentity.Did).Distinct().ToArray();
        }

        public async Task<string> ShapeAsync(string shape)
        {
            var client = _services.GetRequiredService<DidCommClient>();
            var aliceToBob = await PackAsync(client, Alice.Did, protectSender: shape == "anoncrypt(authcrypt)");
            if (shape == "anoncrypt(authcrypt)")
                return aliceToBob;
            var malloryKid = HeaderOf(await PackAsync(client, Mallory.Did, protectSender: false))["skid"]!.GetValue<string>();
            var envelope = JsonNode.Parse(aliceToBob)!.AsObject();
            var header = HeaderOf(aliceToBob);
            var aliceKid = header["skid"]!.GetValue<string>();
            var aliceDid = Alice.Did;
            var fragment = aliceKid[aliceKid.IndexOf('#')..];

            switch (shape)
            {
                case "as packed (General JSON)":
                    return aliceToBob;
                case "the flattened JSON serialization":
                {
                    var recipient = envelope["recipients"]!.AsArray()[0]!.AsObject();
                    return new JsonObject
                    {
                        ["protected"] = envelope["protected"]!.DeepClone(),
                        ["header"] = recipient["header"]!.DeepClone(),
                        ["encrypted_key"] = recipient["encrypted_key"]!.DeepClone(),
                        ["iv"] = envelope["iv"]!.DeepClone(),
                        ["ciphertext"] = envelope["ciphertext"]!.DeepClone(),
                        ["tag"] = envelope["tag"]!.DeepClone(),
                    }.ToJsonString();
                }
                case "skid: another fragment of the sender's DID":
                    return WithSkid(envelope, header, aliceDid + "#key-other");
                case "skid: a DID URL with a query and a fragment":
                    return WithSkid(envelope, header, aliceDid + "?versionId=1" + fragment);
                case "skid: a DID URL with a path":
                    return WithSkid(envelope, header, aliceDid + "/some/path" + fragment);
                case "skid: the bare DID":
                    return WithSkid(envelope, header, aliceDid);
                case "skid and apu: another sender's key":
                    return WithSkid(envelope, header, malloryKid);
                case "apu disagrees with skid":
                    header["apu"] = B64(malloryKid);
                    return WithHeader(envelope, header);
                case "apu only":
                    header.Remove("skid");
                    return WithHeader(envelope, header);
                case "apu only, naming another sender":
                    header.Remove("skid");
                    header["apu"] = B64(malloryKid);
                    return WithHeader(envelope, header);
                case "ECDH-ES carrying the skid":
                    header["alg"] = "ECDH-ES+A256KW";
                    return WithHeader(envelope, header);
                case "skid over the key-id limit":
                    return WithSkid(envelope, header, aliceDid + "#" + new string('k', 4100));
                case "skid whose DID part is over the DID limit":
                    return WithSkid(envelope, header, aliceDid + new string('a', 2100) + fragment);
                case "two skid members":
                {
                    header["apu"] = B64(malloryKid);
                    var text = header.ToJsonString();
                    text = text[..^1] + ",\"skid\":\"" + malloryKid + "\"}"; // Alice's first, Mallory's last
                    envelope["protected"] = B64(text);
                    return envelope.ToJsonString();
                }
                case "two alg members":
                {
                    var text = "{\"alg\":\"ECDH-ES+A256KW\"," + header.ToJsonString()[1..]; // ECDH-ES first, ECDH-1PU last
                    envelope["protected"] = B64(text);
                    return envelope.ToJsonString();
                }
                case "two protected members":
                {
                    var other = header.DeepClone().AsObject();
                    other["skid"] = malloryKid;
                    other["apu"] = B64(malloryKid);
                    var text = envelope.ToJsonString();
                    return "{\"protected\":\"" + B64(other.ToJsonString()) + "\"," + text[1..]; // Mallory's first, Alice's last
                }
                case "skid spelled in upper case":
                {
                    var skid = header["skid"]!.GetValue<string>();
                    header.Remove("skid");
                    header["SKID"] = malloryKid;
                    header["apu"] = B64(skid);
                    return WithHeader(envelope, header);
                }
                case "skid that is not a string":
                    header["skid"] = 42;
                    return WithHeader(envelope, header);
                case "the method name in upper case":
                    return WithSkid(envelope, header, "DID" + aliceKid[3..]);
                case "enc that ECDH-1PU does not allow":
                    header["enc"] = "A256GCM";
                    return WithHeader(envelope, header);
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown shape");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _peers.DisposeAsync();
        }

        private async Task<string> PackAsync(DidCommClient client, string from, bool protectSender)
        {
            var message = new MessageBuilder()
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithFrom(from)
                .WithTo(BobIdentity.Did)
                .WithBody(new JsonObject { ["content"] = "hello" })
                .Build();
            return (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { BobIdentity.Did }, From: from, ProtectSender: protectSender))).Message;
        }

        private static JsonObject HeaderOf(string packed)
            => JsonNode.Parse(Encoding.UTF8.GetString(DpBase64Url.Decode(JsonNode.Parse(packed)!["protected"]!.GetValue<string>())))!.AsObject();

        private static string WithSkid(JsonObject envelope, JsonObject header, string skid)
        {
            header["skid"] = skid;
            header["apu"] = B64(skid);
            return WithHeader(envelope, header);
        }

        private static string WithHeader(JsonObject envelope, JsonObject header)
        {
            envelope["protected"] = B64(header.ToJsonString());
            return envelope.ToJsonString();
        }

        private static string B64(string text) => DpBase64Url.Encode(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Records every DID the unpack asks the host to resolve.</summary>
    private sealed class RecordingResolver(IDidResolver inner) : IDidResolver
    {
        private readonly ConcurrentQueue<string> _resolved = new();

        public IReadOnlyList<string> Resolved => _resolved.ToArray();

        public void Clear() => _resolved.Clear();

        public bool CanResolve(string did) => inner.CanResolve(did);

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            _resolved.Enqueue(did);
            return inner.ResolveAsync(did, options, ct);
        }
    }

    /// <summary>A key service that only lists keys (no same-document bindings).</summary>
    private sealed class ListingOnly(NetDidKeyService inner) : IDidKeyService
    {
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.GetVerificationMethodsAsync(did, relationship, ct);

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

        public void RejectUnsupportedMethod(string did) => inner.RejectUnsupportedMethod(did);
    }
}
