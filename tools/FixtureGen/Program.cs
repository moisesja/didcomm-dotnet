using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.TestSupport;

namespace FixtureGen;

/// <summary>
/// FR-IX-06 generator: packs the canonical Appendix C.1 "lets_do_lunch" payload from Alice to
/// Bob across every composition/algorithm didcomm-dotnet supports (PRD §13.5) and writes
/// <c>packed/didcomm-dotnet/&lt;name&gt;.json</c> + <c>manifest/didcomm-dotnet/&lt;name&gt;.json</c>
/// into the fixtures tree passed via <c>--fixtures</c>. Every vector is unpacked again before
/// it is written (self-verification), and the manifest's expected metadata is captured from
/// that unpack so the published expectations can never drift from the library's behavior.
/// The InteropTests runner re-verifies each published vector on every run (direction=outbound
/// + operation=unpack/verify manifests are self-verified inbound-style to guard bit-rot).
/// </summary>
internal static class Program
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string PayloadRelPath = "payloads/c1-lets-do-lunch.json";

    /// <summary>Frozen clock inside the C.1 payload's validity window (created 1516269022, expires 1516385931) so facade expiry checks are deterministic.</summary>
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1516270000);

    /// <summary>JOSE identifiers used by the outbound matrix (mirrors DidComm.Core's internal JoseAlgorithms constants).</summary>
    private static class Alg
    {
        public const string EdDSA = "EdDSA";
        public const string ES256 = "ES256";
        public const string ES256K = "ES256K";
        public const string A256CbcHs512 = "A256CBC-HS512";
        public const string A256Gcm = "A256GCM";
        public const string XC20P = "XC20P";
        public const string CrvX25519 = "X25519";
        public const string CrvEd25519 = "Ed25519";
        public const string CrvP256 = "P-256";
        public const string CrvP384 = "P-384";
        public const string CrvP521 = "P-521";
        public const string CrvSecp256k1 = "secp256k1";
    }

    /// <summary>Manifest serialization: indented, and JOSE names like <c>ECDH-ES+A256KW</c> kept literal (no <c>+</c> escapes) to match the hand-written manifests in the suite.</summary>
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> Main(string[] args)
    {
        string? fixturesRoot = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--fixtures")
                fixturesRoot = args[i + 1];
        }

        if (fixturesRoot is null)
        {
            Console.Error.WriteLine("Usage: FixtureGen --fixtures <fixtures-root>");
            return 2;
        }

        fixturesRoot = Path.GetFullPath(fixturesRoot);
        var actors = Actors.Load(fixturesRoot);
        var version = LibraryVersion();
        var sourceRef = $"didcomm-dotnet@{version} tools/FixtureGen (spec Appendix A/B actors, Appendix C.1 payload)";

        Directory.CreateDirectory(Path.Combine(fixturesRoot, "packed", "didcomm-dotnet"));
        Directory.CreateDirectory(Path.Combine(fixturesRoot, "manifest", "didcomm-dotnet"));

        foreach (var composition in Compositions())
        {
            var packed = await composition.PackAsync(actors).ConfigureAwait(false);

            // Self-verify before publishing: unpack with the unfiltered client and let the
            // composition assert its own shape (flags, algorithms, kids).
            var result = await actors.NewClient().UnpackAsync(packed).ConfigureAwait(false);
            Require(result.Message.Id == actors.Payload.Id, $"{composition.Id}: unpacked message id mismatch");
            Require(result.Message.Type == actors.Payload.Type, $"{composition.Id}: unpacked message type mismatch");
            composition.Sanity(result);

            var packedRel = $"packed/didcomm-dotnet/{composition.Name}.json";
            File.WriteAllText(Path.Combine(fixturesRoot, packedRel), packed + "\n");
            var manifest = BuildManifest(composition, result, packedRel, sourceRef);
            File.WriteAllText(
                Path.Combine(fixturesRoot, "manifest", "didcomm-dotnet", $"{composition.Name}.json"),
                manifest.ToJsonString(ManifestJsonOptions) + "\n");

            Console.WriteLine($"wrote {packedRel} + manifest/didcomm-dotnet/{composition.Name}.json");
        }

        Console.WriteLine($"done (source_ref: {sourceRef})");
        return 0;
    }

    /// <summary>
    /// The §13.5 outbound matrix didcomm-dotnet supports with the spec actors. Authcrypt P-384
    /// is not derivable: Appendix A gives Alice no P-384 keyAgreement key (the spec's own C.3
    /// authcrypt vectors are X25519 / P-256 / P-521), so P-521 covers the NIST-curve authcrypt
    /// cells alongside P-256.
    /// </summary>
    private static IReadOnlyList<Composition> Compositions() =>
    [
        new("plaintext", "dotnet-plaintext", "unpack",
            "didcomm-dotnet outbound: the Appendix C.1 payload as a plaintext JWM (application/didcomm-plain+json).",
            ["plaintext"],
            a => a.NewClient().PackPlaintextAsync(a.Payload),
            r => Require(!r.Encrypted && !r.NonRepudiation, "plaintext must be neither encrypted nor signed")),

        new("signed-eddsa", "dotnet-signed-eddsa", "verify",
            "didcomm-dotnet outbound: C.1 signed by Alice with EdDSA (did:example:alice#key-1).",
            ["signed", "EdDSA"],
            a => a.NewClient(authCrv: Alg.CrvEd25519).PackSignedAsync(a.Payload, Alice),
            r => Require(r.SignatureAlgorithm == Alg.EdDSA && r.SignerKid == $"{Alice}#key-1",
                "expected an EdDSA signature by alice#key-1")),

        new("signed-es256", "dotnet-signed-es256", "verify",
            "didcomm-dotnet outbound: C.1 signed by Alice with ES256 (did:example:alice#key-2).",
            ["signed", "ES256"],
            a => a.NewClient(authCrv: Alg.CrvP256).PackSignedAsync(a.Payload, Alice),
            r => Require(r.SignatureAlgorithm == Alg.ES256 && r.SignerKid == $"{Alice}#key-2",
                "expected an ES256 signature by alice#key-2")),

        new("signed-es256k", "dotnet-signed-es256k", "verify",
            "didcomm-dotnet outbound: C.1 signed by Alice with ES256K (did:example:alice#key-3).",
            ["signed", "ES256K"],
            a => a.NewClient(authCrv: Alg.CrvSecp256k1).PackSignedAsync(a.Payload, Alice),
            r => Require(r.SignatureAlgorithm == Alg.ES256K && r.SignerKid == $"{Alice}#key-3",
                "expected an ES256K signature by alice#key-3")),

        AnonComposition("anon-x25519-a256cbc", Alg.CrvX25519, ContentEncryptionAlgorithm.A256CbcHs512, Alg.A256CbcHs512),
        AnonComposition("anon-x25519-xc20p", Alg.CrvX25519, ContentEncryptionAlgorithm.XC20P, Alg.XC20P),
        AnonComposition("anon-x25519-a256gcm", Alg.CrvX25519, ContentEncryptionAlgorithm.A256Gcm, Alg.A256Gcm),
        AnonComposition("anon-p256-a256cbc", Alg.CrvP256, ContentEncryptionAlgorithm.A256CbcHs512, Alg.A256CbcHs512),
        AnonComposition("anon-p384-a256cbc", Alg.CrvP384, ContentEncryptionAlgorithm.A256CbcHs512, Alg.A256CbcHs512),
        AnonComposition("anon-p521-a256gcm", Alg.CrvP521, ContentEncryptionAlgorithm.A256Gcm, Alg.A256Gcm),

        AuthComposition("auth-x25519-a256cbc", Alg.CrvX25519, "x25519"),
        AuthComposition("auth-p256-a256cbc", Alg.CrvP256, "p256"),
        AuthComposition("auth-p521-a256cbc", Alg.CrvP521, "p521"),

        new("anon-sign-eddsa-x25519", "dotnet-anon-sign-eddsa-x25519", "unpack",
            "didcomm-dotnet outbound: anoncrypt(sign(C.1)) — inner EdDSA JWS by alice#key-1, outer anoncrypt to Bob over X25519 with A256CBC-HS512.",
            ["anoncrypt", "sign-then-encrypt", "ECDH-ES+A256KW", "X25519", "A256CBC-HS512", "EdDSA-inner"],
            async a => (await a.NewClient(Alg.CrvX25519, Alg.CrvEd25519).PackEncryptedAsync(
                a.Payload,
                new PackEncryptedOptions(Recipients: [Bob], SignFrom: Alice)).ConfigureAwait(false)).Message,
            r => Require(
                r.Encrypted && r.AnonymousSender && r.NonRepudiation
                    && r.SignatureAlgorithm == Alg.EdDSA && r.SignerKid == $"{Alice}#key-1",
                "expected anoncrypt(sign) with an inner EdDSA JWS by alice#key-1")),

        new("anon-auth-x25519-a256cbc", "dotnet-anon-auth-x25519-a256cbc", "unpack",
            "didcomm-dotnet outbound: anoncrypt(authcrypt(C.1)) — inner ECDH-1PU authcrypt from alice#key-x25519-1, outer anoncrypt hides the skid from mediators (ProtectSender).",
            ["protect-sender", "anoncrypt", "authcrypt", "ECDH-1PU+A256KW", "ECDH-ES+A256KW", "X25519", "A256CBC-HS512"],
            async a => (await a.NewClient(Alg.CrvX25519).PackEncryptedAsync(
                a.Payload,
                new PackEncryptedOptions(Recipients: [Bob], From: Alice, ProtectSender: true)).ConfigureAwait(false)).Message,
            r => Require(r.Encrypted && r.Authenticated && r.AnonymousSender && r.SenderKid == $"{Alice}#key-x25519-1",
                "expected anoncrypt(authcrypt) with alice#key-x25519-1 as skid")),

        new("auth-x25519-a256cbc-3rcpt", "dotnet-auth-x25519-a256cbc-3rcpt", "unpack",
            "didcomm-dotnet outbound: authcrypt C.1 from alice#key-x25519-1 to all three of Bob's X25519 kids in one JWE (multi-recipient, mirroring the spec C.3 shape).",
            ["authcrypt", "ECDH-1PU+A256KW", "X25519", "A256CBC-HS512", "multi-recipient"],
            a => MultiRecipientPacker.PackAuthcryptAsync(
                a.Payload,
                [a.Public($"{Bob}#key-x25519-1"), a.Public($"{Bob}#key-x25519-2"), a.Public($"{Bob}#key-x25519-3")],
                a.Public($"{Alice}#key-x25519-1"),
                a.Secrets),
            r => Require(r.Encrypted && r.Authenticated && r.AllRecipientKids.Count == 3,
                "expected a 3-recipient authcrypt envelope")),
    ];

    private static Composition AnonComposition(string name, string crv, ContentEncryptionAlgorithm enc, string encName)
    {
        var crvSlug = crv.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return new Composition(
            name, $"dotnet-{name}", "unpack",
            $"didcomm-dotnet outbound: anoncrypt C.1 to Bob over {crv} with {encName} content encryption.",
            ["anoncrypt", "ECDH-ES+A256KW", crv, encName],
            async a => (await a.NewClient(crv).PackEncryptedAsync(
                a.Payload,
                new PackEncryptedOptions(Recipients: [Bob], Enc: enc)).ConfigureAwait(false)).Message,
            r => Require(
                r.Encrypted && r.AnonymousSender && !r.Authenticated
                    && r.ContentEncryption == encName
                    && r.RecipientKid?.Contains(crvSlug, StringComparison.Ordinal) == true,
                $"expected anoncrypt over {crv} with {encName}"));
    }

    private static Composition AuthComposition(string name, string crv, string kidSlug)
    {
        return new Composition(
            name, $"dotnet-{name}", "unpack",
            $"didcomm-dotnet outbound: authcrypt C.1 from Alice to Bob over {crv} with A256CBC-HS512 (the FR-ENC-09 authcrypt-safe content encryption).",
            ["authcrypt", "ECDH-1PU+A256KW", crv, "A256CBC-HS512"],
            async a => (await a.NewClient(crv).PackEncryptedAsync(
                a.Payload,
                new PackEncryptedOptions(Recipients: [Bob], From: Alice)).ConfigureAwait(false)).Message,
            r => Require(
                r.Encrypted && r.Authenticated && !r.AnonymousSender
                    && r.ContentEncryption == Alg.A256CbcHs512
                    && r.SenderKid == $"{Alice}#key-{kidSlug}-1",
                $"expected authcrypt over {crv} from alice#key-{kidSlug}-1"));
    }

    private static JsonObject BuildManifest(Composition composition, UnpackResult result, string packedRel, string sourceRef)
    {
        var metadata = new JsonObject
        {
            ["encrypted"] = result.Encrypted,
            ["authenticated"] = result.Authenticated,
            ["non_repudiation"] = result.NonRepudiation,
            ["anonymous_sender"] = result.AnonymousSender,
        };
        if (result.ContentEncryption is not null) metadata["enc"] = result.ContentEncryption;
        if (result.KeyWrap is not null) metadata["kw"] = result.KeyWrap;
        if (result.SignatureAlgorithm is not null) metadata["sig_alg"] = result.SignatureAlgorithm;
        if (result.SignerKid is not null) metadata["signer_kid"] = result.SignerKid;
        if (result.SenderKid is not null) metadata["sender_kid"] = result.SenderKid;

        return new JsonObject
        {
            ["schema"] = "didcomm-fixture/v1",
            ["id"] = composition.Id,
            ["description"] = composition.Description,
            ["source"] = "didcomm-dotnet",
            ["source_ref"] = sourceRef,
            ["direction"] = "outbound",
            ["operation"] = composition.Operation,
            ["tags"] = new JsonArray(composition.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["input"] = new JsonObject { ["packed"] = packedRel },
            ["expected"] = new JsonObject
            {
                ["outcome"] = "success",
                ["plaintext"] = PayloadRelPath,
                ["metadata"] = metadata,
                ["match"] = "roundtrip",
            },
        };
    }

    private static string LibraryVersion()
    {
        var informational = typeof(DidCommClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-verification failed: {message}");
    }

    /// <param name="Name">Output file base name (packed + manifest).</param>
    /// <param name="Id">Stable fixture id.</param>
    /// <param name="Operation">Runner operation (<c>unpack</c>, or <c>verify</c> for signed-only vectors, matching the suite's convention).</param>
    /// <param name="Description">Human-readable manifest description.</param>
    /// <param name="Tags">Manifest filter tags.</param>
    /// <param name="PackAsync">Produces the packed envelope from the shared actors.</param>
    /// <param name="Sanity">Shape assertions run against the self-verification unpack before publishing.</param>
    private sealed record Composition(
        string Name,
        string Id,
        string Operation,
        string Description,
        string[] Tags,
        Func<Actors, Task<string>> PackAsync,
        Action<UnpackResult> Sanity);

    /// <summary>Appendix A/B actor material: secrets resolver, public JWKs by kid, DID docs, and the C.1 payload.</summary>
    private sealed class Actors
    {
        public required InMemorySecretsResolver Secrets { get; init; }
        public required IReadOnlyDictionary<string, Jwk> Publics { get; init; }
        public required StaticDidResolver Docs { get; init; }
        public required Message Payload { get; init; }

        public Jwk Public(string kid) => Publics.TryGetValue(kid, out var jwk)
            ? jwk
            : throw new InvalidOperationException($"No public JWK loaded for kid '{kid}'.");

        /// <summary>New facade over the static resolver; optional curve filters steer pack-time key selection.</summary>
        public DidCommClient NewClient(string? kaCrv = null, string? authCrv = null)
        {
            IDidKeyService keyService = new NetDidKeyService(Docs);
            if (kaCrv is not null || authCrv is not null)
                keyService = new CurveFilteredKeyService(keyService, kaCrv, authCrv);
            return new DidCommClient(Secrets, keyService, new DidCommOptions { Clock = () => FixedNow });
        }

        public static Actors Load(string fixturesRoot)
        {
            var secrets = new InMemorySecretsResolver();
            var publics = new Dictionary<string, Jwk>(StringComparer.Ordinal);
            foreach (var actor in new[] { "alice", "bob" })
            {
                var path = Path.Combine(fixturesRoot, "secrets", $"{actor}.json");
                var entries = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(path))
                    ?? throw new InvalidDataException($"Secrets file did not parse: {path}");
                foreach (var entry in entries)
                {
                    var kid = entry.GetProperty("kid").GetString()!;
                    secrets.Add(new Jwk
                    {
                        Kid = kid,
                        Kty = entry.GetProperty("kty").GetString()!,
                        Crv = entry.TryGetProperty("crv", out var c) ? c.GetString() : null,
                        X = entry.TryGetProperty("x", out var x) ? x.GetString() : null,
                        Y = entry.TryGetProperty("y", out var y) ? y.GetString() : null,
                        D = entry.TryGetProperty("d", out var d) ? d.GetString() : null,
                    });
                    publics[kid] = new Jwk
                    {
                        Kid = kid,
                        Kty = entry.GetProperty("kty").GetString()!,
                        Crv = entry.TryGetProperty("crv", out var c2) ? c2.GetString() : null,
                        X = entry.TryGetProperty("x", out var x2) ? x2.GetString() : null,
                        Y = entry.TryGetProperty("y", out var y2) ? y2.GetString() : null,
                    };
                }
            }

            var payloadJson = File.ReadAllText(Path.Combine(fixturesRoot, PayloadRelPath.Replace('/', Path.DirectorySeparatorChar)));
            var payload = JsonSerializer.Deserialize<Message>(payloadJson)
                ?? throw new InvalidDataException("C.1 payload deserialized to null.");

            return new Actors
            {
                Secrets = secrets,
                Publics = publics,
                Docs = StaticDidResolver.LoadFromDirectory(Path.Combine(fixturesRoot, "diddocs", "spec")),
                Payload = payload,
            };
        }
    }
}
