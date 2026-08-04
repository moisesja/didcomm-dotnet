using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Method.Peer;
using JwkConversion = DataProofsDotnet.Jose.JwkConversion;

namespace InteropCli;

/// <summary>
/// Live cross-implementation interop CLI (PRD §13.6, FR-IX-04/05). Three subcommands drive the
/// real production pipeline — <see cref="DidCommClient"/> over the net-did composite resolver
/// (did:key + did:peer), exactly what <c>UseNetDidResolver()</c> wires for applications:
/// <list type="bullet">
/// <item><c>mint --out id.json</c> — mint a did:peer:2 (X25519 keyAgreement + Ed25519
/// authentication via net-did, mirroring samples/_shared PeerIdentityFactory) and write
/// <c>{did, secrets: [private JWKs]}</c>.</item>
/// <item><c>pack --identity id.json --to &lt;did|id.json&gt; --mode &lt;composition&gt;
/// [--enc alg] [--message file] [--out file]</c> — pack a message and emit the envelope.</item>
/// <item><c>unpack --identity id.json [--in file] [--out file]</c> — unpack an envelope and
/// print <c>{message, metadata}</c> JSON so a harness script can diff and assert.</item>
/// </list>
/// stdout carries only the artifact (envelope / unpack JSON); diagnostics go to stderr, and the
/// exit code is 0 on success, 1 on failure, 2 on usage errors — so shell harnesses can pipe.
/// </summary>
internal static class Program
{
    /// <summary>Envelope/unpack output: indented, JOSE names like <c>ECDH-ES+A256KW</c> kept literal.</summary>
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                return Usage();

            var opts = ParseOptions(args.Skip(1));
            return args[0] switch
            {
                "mint" => await MintAsync(opts).ConfigureAwait(false),
                "pack" => await PackAsync(opts).ConfigureAwait(false),
                "unpack" => await UnpackAsync(opts).ConfigureAwait(false),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"InteropCli: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            Usage:
              InteropCli mint --out <identity.json> [--endpoint <uri>]
              InteropCli pack --identity <identity.json> --to <did|identity.json>
                              --mode plaintext|signed|anoncrypt|authcrypt|anoncrypt-sign|anoncrypt-authcrypt
                              [--enc A256CBC-HS512|A256GCM|XC20P] [--message <file|->] [--out <file>]
              InteropCli unpack --identity <identity.json> [--in <file|->] [--out <file>]
            """);
        return 2;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        string? pending = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null)
                    throw new ArgumentException($"Option '{pending}' is missing a value.");
                pending = arg[2..];
            }
            else if (pending is not null)
            {
                opts[pending] = arg;
                pending = null;
            }
            else
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }
        }

        if (pending is not null)
            throw new ArgumentException($"Option '{pending}' is missing a value.");
        return opts;
    }

    private static string Require(Dictionary<string, string> opts, string name)
        => opts.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"Missing required option '--{name}'.");

    // ── mint ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mint a fresh did:peer:2 with an X25519 keyAgreement pair and an Ed25519 authentication
    /// pair (the shape the SICPA demo family exchanges) and persist the DID plus the matching
    /// private JWKs. Mirrors samples/_shared PeerIdentityFactory, but implemented here so the
    /// tool depends only on net-did + the library.
    /// </summary>
    private static async Task<int> MintAsync(Dictionary<string, string> opts)
    {
        var outPath = Require(opts, "out");
        opts.TryGetValue("endpoint", out var endpoint);

        var secrets = new InMemorySecretsResolver();
        await using var sp = BuildProvider(secrets);
        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<NetCrypto.ICryptoProvider>();

        var kxPair = keyGen.Generate(KeyType.X25519);
        var authPair = keyGen.Generate(KeyType.Ed25519);

        var options = new DidPeerCreateOptions
        {
            Numalgo = PeerNumalgo.Two,
            Keys = new[]
            {
                new PeerKeyPurpose(new KeyPairSigner(kxPair, crypto), PeerPurpose.KeyAgreement),
                new PeerKeyPurpose(new KeyPairSigner(authPair, crypto), PeerPurpose.Authentication),
            },
            Services = endpoint is null ? null : new[] { BuildDidCommService(endpoint) },
        };

        var result = await manager.CreateAsync(options).ConfigureAwait(false);
        var did = result.Did.Value
            ?? throw new InvalidOperationException("DID manager returned a DID with no Value.");

        var identity = new JsonObject
        {
            ["did"] = did,
            ["secrets"] = new JsonArray(
                JwkToNode(BuildPrivateJwk(kxPair, did, result.DidDocument)),
                JwkToNode(BuildPrivateJwk(authPair, did, result.DidDocument))),
        };

        File.WriteAllText(outPath, identity.ToJsonString(OutputJson) + "\n");
        Console.Error.WriteLine($"minted {did}");
        Console.WriteLine(did);
        return 0;
    }

    /// <summary>DID-Core <c>DIDCommMessaging</c> service entry (object-form serviceEndpoint) for minted identities that must advertise a transport.</summary>
    private static Service BuildDidCommService(string endpointUri) => new()
    {
        Id = "#didcomm",
        Type = "DIDCommMessaging",
        ServiceEndpoint = ServiceEndpointValue.FromMap(new Dictionary<string, JsonElement>
        {
            ["uri"] = JsonSerializer.SerializeToElement(endpointUri),
            ["accept"] = JsonSerializer.SerializeToElement(new[] { "didcomm/v2" }),
        }),
    };

    /// <summary>Map a generated key pair to its verification method in the resolved document (numalgo 2 emits relative <c>#key-N</c> ids; the envelope layer keys by absolute DID URL).</summary>
    private static Jwk BuildPrivateJwk(KeyPair keyPair, string did, DidDocument doc)
    {
        var multibase = keyPair.MultibasePublicKey;
        var match = doc.VerificationMethod?.FirstOrDefault(vm =>
                        string.Equals(vm.PublicKeyMultibase, multibase, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"KeyPair (multibase={multibase}) is not represented in the resolved DID Document for {did}.");
        var kid = match.Id.StartsWith('#') ? did + match.Id : match.Id;
        return JwkConversion.ToPrivateJwk(keyPair, kid);
    }

    // ── pack ────────────────────────────────────────────────────────────────────────────

    private static async Task<int> PackAsync(Dictionary<string, string> opts)
    {
        var identity = Identity.Load(Require(opts, "identity"));
        var to = Require(opts, "to");
        if (File.Exists(to)) // convenience: accept the counterpart's identity file
            to = Identity.Load(to).Did;

        var mode = Require(opts, "mode");
        var enc = ParseEnc(opts.GetValueOrDefault("enc", "A256CBC-HS512"));
        var message = LoadOrDefaultMessage(opts.GetValueOrDefault("message"), identity.Did, to, mode);

        await using var sp = BuildProvider(identity.Secrets);
        var client = sp.GetRequiredService<DidCommClient>();

        var packed = mode switch
        {
            "plaintext" => await client.PackPlaintextAsync(message).ConfigureAwait(false),
            "signed" => await client.PackSignedAsync(message, identity.Did).ConfigureAwait(false),
            "anoncrypt" => (await client.PackEncryptedAsync(message,
                new PackEncryptedOptions(Recipients: [to], Enc: enc)).ConfigureAwait(false)).Message,
            "authcrypt" => (await client.PackEncryptedAsync(message,
                new PackEncryptedOptions(Recipients: [to], From: identity.Did, Enc: enc)).ConfigureAwait(false)).Message,
            "anoncrypt-sign" => (await client.PackEncryptedAsync(message,
                new PackEncryptedOptions(Recipients: [to], SignFrom: identity.Did, Enc: enc)).ConfigureAwait(false)).Message,
            "anoncrypt-authcrypt" => (await client.PackEncryptedAsync(message,
                new PackEncryptedOptions(Recipients: [to], From: identity.Did, ProtectSender: true)).ConfigureAwait(false)).Message,
            _ => throw new ArgumentException($"Unknown --mode '{mode}'."),
        };

        WriteOutput(opts.GetValueOrDefault("out"), packed);
        return 0;
    }

    private static ContentEncryptionAlgorithm ParseEnc(string enc) => enc switch
    {
        "A256CBC-HS512" => ContentEncryptionAlgorithm.A256CbcHs512,
        "A256GCM" => ContentEncryptionAlgorithm.A256Gcm,
        "XC20P" => ContentEncryptionAlgorithm.XC20P,
        _ => throw new ArgumentException($"Unknown --enc '{enc}'."),
    };

    /// <summary>
    /// The harness payload: an explicit <c>--message</c> file wins; otherwise a C.1-style
    /// "lets_do_lunch" proposal minted fresh (new id, current created_time, no expires_time so
    /// nothing goes stale mid-run). <c>from</c> is set only when the composition proves a sender
    /// (signed / authcrypt — FR-SIG-03 binds the signer to <c>from</c>); pure anoncrypt stays
    /// anonymous end-to-end.
    /// </summary>
    private static Message LoadOrDefaultMessage(string? messagePath, string ownDid, string to, string mode)
    {
        if (messagePath is not null)
        {
            var json = messagePath == "-" ? Console.In.ReadToEnd() : File.ReadAllText(messagePath);
            return JsonSerializer.Deserialize<Message>(json)
                ?? throw new InvalidDataException($"Message file deserialized to null: {messagePath}");
        }

        var builder = new MessageBuilder()
            .WithId(Guid.NewGuid().ToString("D"))
            .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
            .WithTo(to)
            .WithCreatedTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .WithBody(new JsonObject { ["messagespecificattribute"] = "and its value" });

        if (mode is "plaintext" or "signed" or "authcrypt" or "anoncrypt-sign" or "anoncrypt-authcrypt")
            builder = builder.WithFrom(ownDid);

        return builder.Build();
    }

    // ── unpack ──────────────────────────────────────────────────────────────────────────

    private static async Task<int> UnpackAsync(Dictionary<string, string> opts)
    {
        var identity = Identity.Load(Require(opts, "identity"));
        var inPath = opts.GetValueOrDefault("in", "-");
        var packed = inPath == "-" ? Console.In.ReadToEnd() : File.ReadAllText(inPath);

        await using var sp = BuildProvider(identity.Secrets);
        var client = sp.GetRequiredService<DidCommClient>();
        var result = await client.UnpackAsync(packed).ConfigureAwait(false);

        var output = new JsonObject
        {
            ["message"] = JsonSerializer.SerializeToNode(result.Message, OutputJson),
            ["metadata"] = new JsonObject
            {
                ["encrypted"] = result.Encrypted,
                ["authenticated"] = result.Authenticated,
                ["non_repudiation"] = result.NonRepudiation,
                ["anonymous_sender"] = result.AnonymousSender,
                ["enc"] = result.ContentEncryption,
                ["kw"] = result.KeyWrap,
                ["sig_alg"] = result.SignatureAlgorithm,
                ["signer_kid"] = result.SignerKid,
                ["sender_kid"] = result.SenderKid,
                ["recipient_kid"] = result.RecipientKid,
            },
        };

        WriteOutput(opts.GetValueOrDefault("out"), output.ToJsonString(OutputJson));
        return 0;
    }

    // ── shared plumbing ─────────────────────────────────────────────────────────────────

    /// <summary>The production wiring (FR-DID-01): net-did composite resolver (did:key + did:peer) behind NetDidKeyService, exactly what applications get from <c>UseNetDidResolver()</c>.</summary>
    private static ServiceProvider BuildProvider(InMemorySecretsResolver secrets)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(secrets));
        return services.BuildServiceProvider();
    }

    private static void WriteOutput(string? outPath, string content)
    {
        if (outPath is null or "-")
            Console.WriteLine(content);
        else
            File.WriteAllText(outPath, content + "\n");
    }

    private static JsonNode JwkToNode(Jwk jwk)
    {
        var node = new JsonObject
        {
            ["kid"] = jwk.Kid,
            ["kty"] = jwk.Kty,
            ["crv"] = jwk.Crv,
            ["x"] = jwk.X,
        };
        if (jwk.Y is not null) node["y"] = jwk.Y;
        if (jwk.D is not null) node["d"] = jwk.D;
        return node;
    }

    /// <summary>An identity file: the did:peer plus the private JWKs it owns, loaded into an in-memory secrets resolver.</summary>
    private sealed class Identity
    {
        public required string Did { get; init; }
        public required InMemorySecretsResolver Secrets { get; init; }

        public static Identity Load(string path)
        {
            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                ?? throw new InvalidDataException($"Identity file did not parse: {path}");
            var did = root["did"]?.GetValue<string>()
                ?? throw new InvalidDataException($"Identity file has no 'did': {path}");

            var secrets = new InMemorySecretsResolver();
            foreach (var entry in root["secrets"]?.AsArray()
                ?? throw new InvalidDataException($"Identity file has no 'secrets': {path}"))
            {
                var jwk = entry?.AsObject()
                    ?? throw new InvalidDataException($"Identity file has a null secret entry: {path}");
                secrets.Add(new Jwk
                {
                    Kid = jwk["kid"]?.GetValue<string>(),
                    Kty = jwk["kty"]?.GetValue<string>() ?? "OKP",
                    Crv = jwk["crv"]?.GetValue<string>(),
                    X = jwk["x"]?.GetValue<string>(),
                    Y = jwk["y"]?.GetValue<string>(),
                    D = jwk["d"]?.GetValue<string>(),
                });
            }

            return new Identity { Did = did, Secrets = secrets };
        }
    }
}
