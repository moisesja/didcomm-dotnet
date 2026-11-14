using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Samples.Shared;
using NetCrypto;
using DpBase64Url = DataProofsDotnet.Jose.Base64Url;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using JoseAlgorithms = DataProofsDotnet.Jose.JoseAlgorithms;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-1 (#96 red team): a scripted Message Pickup 3.0 mediator behind a <see cref="FakeSend"/>.
/// It keeps one queue per grant DID (the <c>from</c> of the request, as this library's mediator
/// keys a grant), answers a <c>delivery-request</c> with everything queued for that DID (echoing
/// the <c>recipient_did</c> filter) or a <c>status</c> when nothing is, and removes what a
/// <c>messages-received</c> names. Every answer is a real envelope: authcrypted by the mediator to
/// the requesting DID, threaded to the request, unpacked as the recipient — exactly what
/// <c>SendResult.Returned</c> carries over HTTP.
/// </summary>
internal sealed class Adv1Mediator
{
    private readonly ClientWorld _w;
    private readonly ConcurrentDictionary<string, List<(string Id, byte[] Payload)>> _queues = new(StringComparer.Ordinal);

    public Adv1Mediator(ClientWorld world) => _w = world;

    /// <summary>Every acknowledgement received: (grant DID, ids).</summary>
    public ConcurrentQueue<(string Grant, IReadOnlyList<string> Ids)> Acknowledged { get; } = new();

    /// <summary>Queue <paramref name="packed"/> under <paramref name="grantDid"/> with the queue id <paramref name="id"/>.</summary>
    public void Enqueue(string grantDid, string id, string packed)
    {
        var queue = _queues.GetOrAdd(grantDid, _ => new List<(string, byte[])>());
        lock (queue)
            queue.Add((id, Encoding.UTF8.GetBytes(packed)));
    }

    /// <summary>The ids still queued under <paramref name="grantDid"/>.</summary>
    public IReadOnlyList<string> Queued(string grantDid)
    {
        if (!_queues.TryGetValue(grantDid, out var queue))
            return Array.Empty<string>();
        lock (queue)
            return queue.Select(m => m.Id).ToArray();
    }

    /// <summary>Install as the answer of every send of <paramref name="send"/>.</summary>
    public void Serve(FakeSend send) => send.ReturnWith(AnswerAsync);

    private async Task<UnpackResult?> AnswerAsync(Message request)
    {
        var grant = request.From!;
        var queue = _queues.GetOrAdd(grant, _ => new List<(string, byte[])>());
        Message answer;
        switch (request.Type)
        {
            case MessagePickup.DeliveryRequestType:
            {
                var filter = request.Body?[PickupBodies.RecipientDid]?.GetValue<string>();
                DeliveredMessage[] waiting;
                lock (queue)
                    waiting = queue.Select(m => new DeliveredMessage(m.Id, m.Payload)).ToArray();
                answer = waiting.Length == 0
                    ? MessagePickup.CreateStatus(_w.Mediator.Did, grant, new PickupStatus(0, filter), thid: request.Id)
                    : MessagePickup.CreateDelivery(_w.Mediator.Did, grant, waiting, thid: request.Id, recipientDid: filter);
                break;
            }
            case MessagePickup.MessagesReceivedType:
            {
                var ids = request.Body![PickupBodies.MessageIdList]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                Acknowledged.Enqueue((grant, ids));
                int left;
                lock (queue)
                {
                    queue.RemoveAll(m => ids.Contains(m.Id, StringComparer.Ordinal));
                    left = queue.Count;
                }
                answer = MessagePickup.CreateStatus(_w.Mediator.Did, grant, new PickupStatus(left), thid: request.Id);
                break;
            }
            case MessagePickup.StatusRequestType:
            {
                int count;
                lock (queue)
                    count = queue.Count;
                answer = MessagePickup.CreateStatus(_w.Mediator.Did, grant, new PickupStatus(count), thid: request.Id);
                break;
            }
            default:
                throw new InvalidOperationException("The scripted mediator answers Message Pickup requests only.");
        }
        return await _w.ReceiveAsync(answer, AnswerEnvelope.Authcrypt, from: _w.Mediator.Did, to: grant);
    }
}

/// <summary>
/// ADV-1: envelopes a third party can build — a co-recipient re-encrypting a signed message it
/// received, a relay adding a recipient entry to an envelope that carries no <c>apv</c> — from
/// public primitives, so each one is a real JWE the receive path opens for real.
/// </summary>
internal static class Adv1Envelopes
{
    private static readonly JoseCryptoProvider Crypto = new();
    private static readonly IKeyGenerator KeyGenerator = new DefaultKeyGenerator();

    /// <summary>A basic message from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static Message Note(string from, params string[] to)
    {
        return new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(from)
            .WithTo(to)
            .WithBody(new JsonObject { ["content"] = "for your eyes only" })
            .Build();
    }

    /// <summary>The public X25519 key-agreement JWK of <paramref name="peer"/>, optionally under another <paramref name="kid"/> label.</summary>
    public static Jwk KeyAgreement(PeerIdentity peer, string? kid = null)
    {
        var key = peer.Privates.First(k => k.Crv == JoseAlgorithms.CrvX25519);
        return new Jwk { Kty = key.Kty, Crv = key.Crv, X = key.X, Kid = kid ?? key.Kid };
    }

    /// <summary>
    /// What any holder of <paramref name="payload"/> (a co-recipient's signed JWS, for one) can do:
    /// anoncrypt it to <paramref name="recipients"/> in that order, with the <c>apv</c> the DIDComm
    /// recipe computes over exactly those labels.
    /// </summary>
    public static string Anoncrypt(string payload, params Jwk[] recipients)
        => DpEnc.JweBuilder.BuildEcdhEsA256Kw(Encoding.UTF8.GetBytes(payload), recipients, JoseAlgorithms.A256CbcHs512, Crypto, typ: MediaTypes.Encrypted);

    /// <summary>
    /// An <c>ECDH-ES+A256KW</c> / <c>A256CBC-HS512</c> JWE of <paramref name="payload"/> that only
    /// <paramref name="realRecipient"/>'s key opens, followed by one recipient entry per
    /// <paramref name="addedLabels"/> whose <c>encrypted_key</c> is random — what a relay can append
    /// to an envelope whose protected header carries no <c>apv</c> (or, with
    /// <paramref name="apvOverRealOnly"/>, one whose <c>apv</c> commits to the real recipient only).
    /// </summary>
    public static string WithAddedRecipientLabels(string payload, Jwk realRecipient, bool apvOverRealOnly, params string[] addedLabels)
    {
        var ephemeral = KeyGenerator.Generate(KeyType.X25519);
        var apv = apvOverRealOnly ? DpEnc.ApvComputer.ComputeBytes(new[] { realRecipient.Kid! }) : Array.Empty<byte>();
        var header = new JsonObject
        {
            ["typ"] = MediaTypes.Encrypted,
            ["alg"] = JoseAlgorithms.EcdhEsA256Kw,
            ["enc"] = JoseAlgorithms.A256CbcHs512,
            ["epk"] = new JsonObject
            {
                ["kty"] = "OKP",
                ["crv"] = JoseAlgorithms.CrvX25519,
                ["x"] = DpBase64Url.Encode(ephemeral.PublicKey),
            },
        };
        if (apvOverRealOnly)
            header["apv"] = DpBase64Url.Encode(apv);
        var protectedB64u = DpBase64Url.Encode(Encoding.UTF8.GetBytes(header.ToJsonString()));

        var cek = new byte[64];
        var iv = new byte[16];
        Crypto.Fill(cek);
        Crypto.Fill(iv);
        var (ciphertext, tag) = Crypto.AeadEncrypt(JoseAlgorithms.A256CbcHs512, cek, iv, Encoding.ASCII.GetBytes(protectedB64u), Encoding.UTF8.GetBytes(payload));

        var z = Crypto.DeriveSharedSecret(JoseAlgorithms.CrvX25519, ephemeral.PrivateKey, DpBase64Url.Decode(realRecipient.X!));
        var suppPubInfo = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(suppPubInfo, 256);
        var kek = ConcatKdf.DeriveKey(z, Encoding.ASCII.GetBytes(JoseAlgorithms.EcdhEsA256Kw), ReadOnlySpan<byte>.Empty, apv, suppPubInfo, ReadOnlySpan<byte>.Empty, 32);
        var encryptedKey = Crypto.KeyWrap(JoseAlgorithms.A256Kw, kek, cek);

        var recipients = new JsonArray(new JsonObject
        {
            ["header"] = new JsonObject { ["kid"] = realRecipient.Kid },
            ["encrypted_key"] = DpBase64Url.Encode(encryptedKey),
        });
        foreach (var label in addedLabels)
        {
            var garbage = new byte[encryptedKey.Length];
            Crypto.Fill(garbage);
            recipients.Add(new JsonObject
            {
                ["header"] = new JsonObject { ["kid"] = label },
                ["encrypted_key"] = DpBase64Url.Encode(garbage),
            });
        }

        return new JsonObject
        {
            ["protected"] = protectedB64u,
            ["recipients"] = recipients,
            ["iv"] = DpBase64Url.Encode(iv),
            ["ciphertext"] = DpBase64Url.Encode(ciphertext),
            ["tag"] = DpBase64Url.Encode(tag),
        }.ToJsonString();
    }
}
