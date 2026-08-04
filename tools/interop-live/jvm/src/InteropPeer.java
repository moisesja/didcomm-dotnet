import com.google.crypto.tink.subtle.Ed25519Sign;
import com.google.crypto.tink.subtle.X25519;
import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import io.ipfs.multibase.Base58;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;
import org.didcommx.didcomm.DIDComm;
import org.didcommx.didcomm.common.AnonCryptAlg;
import org.didcommx.didcomm.common.AuthCryptAlg;
import org.didcommx.didcomm.common.VerificationMaterial;
import org.didcommx.didcomm.common.VerificationMaterialFormat;
import org.didcommx.didcomm.common.VerificationMethodType;
import org.didcommx.didcomm.diddoc.DIDDoc;
import org.didcommx.didcomm.diddoc.DIDDocResolver;
import org.didcommx.didcomm.diddoc.VerificationMethod;
import org.didcommx.didcomm.message.Message;
import org.didcommx.didcomm.message.MessageBuilder;
import org.didcommx.didcomm.model.Metadata;
import org.didcommx.didcomm.model.PackEncryptedParams;
import org.didcommx.didcomm.model.PackPlaintextParams;
import org.didcommx.didcomm.model.PackSignedParams;
import org.didcommx.didcomm.model.UnpackParams;
import org.didcommx.didcomm.model.UnpackResult;
import org.didcommx.didcomm.secret.Secret;
import org.didcommx.didcomm.secret.SecretResolverInMemory;

/**
 * SICPA didcomm-jvm counterpart for the live cross-implementation harness (PRD S13.6,
 * FR-IX-04/05). Drives org.didcommx:didcomm (didcomm-jvm, pinned in fetch-deps.sh) with the
 * same CLI shape as tools/InteropCli and python/interop_peer.py, so run-jvm-leg.sh can
 * round-trip envelopes both ways over did:peer:
 *
 *   mint | gen-message | pack | unpack | assert   (see python/interop_peer.py for semantics;
 *   the identity-file schema, payload shape, normalized unpack output, and per-cell metadata
 *   expectations are identical across all three drivers)
 *
 * did:peer:2 handling is implemented here against the did:peer spec (decode .Ez/.Vz
 * multibase(multicodec) segments, name keys #key-N in order of appearance — the numbering
 * net-did emits) rather than through the 2022-era peerdid package, so kids agree
 * byte-for-byte across implementations. Service (.S) segments are ignored: the harness runs
 * direct, without mediators (forward=false).
 *
 * stdout carries only the artifact; diagnostics go to stderr. Exit 0 on success, 1 on failure.
 */
public final class InteropPeer {

    private static final byte[] MULTICODEC_X25519_PUB = {(byte) 0xEC, 0x01};
    private static final byte[] MULTICODEC_ED25519_PUB = {(byte) 0xED, 0x01};
    private static final Gson GSON = new GsonBuilder().setPrettyPrinting().disableHtmlEscaping().create();

    public static void main(String[] args) {
        try {
            if (args.length == 0) {
                System.err.println("usage: InteropPeer mint|gen-message|pack|unpack|assert [--opt value ...]");
                System.exit(2);
            }
            Map<String, String> opts = parseOpts(args);
            switch (args[0]) {
                case "mint": mint(opts); break;
                case "gen-message": genMessage(opts); break;
                case "pack": pack(opts); break;
                case "unpack": unpack(opts); break;
                case "assert": doAssert(opts); break;
                default:
                    System.err.println("unknown subcommand: " + args[0]);
                    System.exit(2);
            }
        } catch (Exception ex) {
            System.err.println("InteropPeer: " + ex.getClass().getSimpleName() + ": " + ex.getMessage());
            System.exit(1);
        }
    }

    private static Map<String, String> parseOpts(String[] args) {
        Map<String, String> opts = new HashMap<>();
        for (int i = 1; i < args.length; i += 2) {
            if (!args[i].startsWith("--") || i + 1 >= args.length)
                throw new IllegalArgumentException("expected --option value pairs, got: " + args[i]);
            opts.put(args[i].substring(2), args[i + 1]);
        }
        return opts;
    }

    private static String require(Map<String, String> opts, String name) {
        String value = opts.get(name);
        if (value == null) throw new IllegalArgumentException("missing required option --" + name);
        return value;
    }

    // ── did:peer:2 (spec-conformant, hand-rolled: see class comment) ──────────────────────

    private static String encodeKeySegment(char purpose, byte[] multicodecPrefix, byte[] rawPublicKey) {
        byte[] prefixed = new byte[multicodecPrefix.length + rawPublicKey.length];
        System.arraycopy(multicodecPrefix, 0, prefixed, 0, multicodecPrefix.length);
        System.arraycopy(rawPublicKey, 0, prefixed, multicodecPrefix.length, rawPublicKey.length);
        return "." + purpose + "z" + Base58.encode(prefixed);
    }

    /** Decode a did:peer:2 into a didcomm-jvm DIDDoc; keys named #key-N in order of appearance. */
    static DIDDoc resolvePeerDid2(String did) {
        if (!did.startsWith("did:peer:2"))
            throw new IllegalArgumentException("not a did:peer:2: " + did);

        List<VerificationMethod> methods = new ArrayList<>();
        List<String> authentication = new ArrayList<>();
        List<String> keyAgreement = new ArrayList<>();
        int keyIndex = 0;

        for (String segment : did.substring("did:peer:2".length()).split("\\.")) {
            if (segment.isEmpty()) continue;
            char purpose = segment.charAt(0);
            String encoded = segment.substring(1);
            if (purpose == 'S') continue; // no mediators in the harness: services are irrelevant
            if (purpose != 'E' && purpose != 'V')
                throw new IllegalArgumentException("unsupported did:peer:2 purpose '" + purpose + "' in " + did);
            if (!encoded.startsWith("z"))
                throw new IllegalArgumentException("unsupported multibase '" + encoded.charAt(0) + "' in " + did);

            byte[] decoded = Base58.decode(encoded.substring(1));
            byte[] prefix = Arrays.copyOfRange(decoded, 0, 2);
            byte[] raw = Arrays.copyOfRange(decoded, 2, decoded.length);
            String crv;
            if (Arrays.equals(prefix, MULTICODEC_X25519_PUB)) crv = "X25519";
            else if (Arrays.equals(prefix, MULTICODEC_ED25519_PUB)) crv = "Ed25519";
            else throw new IllegalArgumentException("unsupported multicodec prefix in " + did);

            keyIndex++;
            String kid = did + "#key-" + keyIndex;
            JsonObject jwk = new JsonObject();
            jwk.addProperty("kty", "OKP");
            jwk.addProperty("crv", crv);
            jwk.addProperty("x", b64url(raw));
            methods.add(new VerificationMethod(
                    kid,
                    VerificationMethodType.JSON_WEB_KEY_2020,
                    new VerificationMaterial(VerificationMaterialFormat.JWK, jwk.toString()),
                    did));
            (purpose == 'E' ? keyAgreement : authentication).add(kid);
        }

        return new DIDDoc(did, keyAgreement, authentication, methods, new ArrayList<>());
    }

    private static final DIDDocResolver PEER_RESOLVER = did -> Optional.of(resolvePeerDid2(did));

    // ── identity files (same schema as InteropCli/interop_peer.py) ────────────────────────

    private static DIDComm didCommFor(String identityPath) throws IOException {
        JsonObject identity = readJson(identityPath);
        List<Secret> secrets = new ArrayList<>();
        for (JsonElement entry : identity.getAsJsonArray("secrets")) {
            JsonObject jwk = entry.getAsJsonObject();
            secrets.add(new Secret(
                    jwk.get("kid").getAsString(),
                    VerificationMethodType.JSON_WEB_KEY_2020,
                    new VerificationMaterial(VerificationMaterialFormat.JWK, jwk.toString())));
        }
        return new DIDComm(PEER_RESOLVER, new SecretResolverInMemory(secrets));
    }

    /** Accept a DID directly or the counterpart's identity file (mirrors the other drivers). */
    private static String loadDid(String didOrPath) throws IOException {
        if (didOrPath.startsWith("did:")) return didOrPath;
        return readJson(didOrPath).get("did").getAsString();
    }

    // ── subcommands ───────────────────────────────────────────────────────────────────────

    private static void mint(Map<String, String> opts) throws Exception {
        byte[] kxPriv = X25519.generatePrivateKey();
        byte[] kxPub = X25519.publicFromPrivate(kxPriv);
        Ed25519Sign.KeyPair authPair = Ed25519Sign.KeyPair.newKeyPair();

        String did = "did:peer:2"
                + encodeKeySegment('E', MULTICODEC_X25519_PUB, kxPub)
                + encodeKeySegment('V', MULTICODEC_ED25519_PUB, authPair.getPublicKey());

        JsonArray secrets = new JsonArray();
        secrets.add(privateJwk("X25519", kxPub, kxPriv, did + "#key-1"));
        secrets.add(privateJwk("Ed25519", authPair.getPublicKey(), authPair.getPrivateKey(), did + "#key-2"));

        JsonObject identity = new JsonObject();
        identity.addProperty("did", did);
        identity.add("secrets", secrets);
        Files.write(Paths.get(require(opts, "out")),
                (GSON.toJson(identity) + "\n").getBytes(StandardCharsets.UTF_8));
        System.err.println("minted " + did);
        System.out.println(did);
    }

    private static JsonObject privateJwk(String crv, byte[] pub, byte[] priv, String kid) {
        JsonObject jwk = new JsonObject();
        jwk.addProperty("kid", kid);
        jwk.addProperty("kty", "OKP");
        jwk.addProperty("crv", crv);
        jwk.addProperty("x", b64url(pub));
        jwk.addProperty("d", b64url(priv));
        return jwk;
    }

    private static void genMessage(Map<String, String> opts) throws IOException {
        JsonObject message = new JsonObject();
        message.addProperty("id", UUID.randomUUID().toString());
        message.addProperty("type", "http://example.com/protocols/lets_do_lunch/1.0/proposal");
        JsonArray to = new JsonArray();
        to.add(loadDid(require(opts, "to")));
        message.add("to", to);
        message.addProperty("created_time", System.currentTimeMillis() / 1000L);
        JsonObject body = new JsonObject();
        body.addProperty("messagespecificattribute", "and its value");
        message.add("body", body);
        if (opts.containsKey("from"))
            message.addProperty("from", loadDid(opts.get("from")));
        writeOutput(opts.get("out"), GSON.toJson(message));
    }

    private static void pack(Map<String, String> opts) throws IOException {
        DIDComm didComm = didCommFor(require(opts, "identity"));
        String ownDid = loadDid(require(opts, "identity"));
        String to = loadDid(require(opts, "to"));
        String mode = require(opts, "mode");
        String enc = opts.getOrDefault("enc", "A256CBC-HS512");
        Message message = messageFromJson(readJson(require(opts, "message")));

        String packed;
        switch (mode) {
            case "plaintext":
                packed = didComm.packPlaintext(new PackPlaintextParams.Builder(message).build()).getPackedMessage();
                break;
            case "signed":
                packed = didComm.packSigned(new PackSignedParams.Builder(message, ownDid).build()).getPackedMessage();
                break;
            case "anoncrypt":
            case "authcrypt":
            case "anoncrypt-sign":
            case "anoncrypt-authcrypt": {
                if ((mode.equals("authcrypt") || mode.equals("anoncrypt-authcrypt")) && !enc.equals("A256CBC-HS512"))
                    throw new IllegalArgumentException("didcomm-jvm authcrypt supports A256CBC-HS512 only, not " + enc);
                PackEncryptedParams.Builder builder = new PackEncryptedParams.Builder(message, to)
                        .forward(false) // direct exchange, no mediator in the loop
                        .encAlgAnon(anonAlg(enc))
                        .encAlgAuth(AuthCryptAlg.A256CBC_HS512_ECDH_1PU_A256KW);
                if (mode.equals("authcrypt") || mode.equals("anoncrypt-authcrypt")) builder.from(ownDid);
                if (mode.equals("anoncrypt-sign")) builder.signFrom(ownDid);
                if (mode.equals("anoncrypt-authcrypt")) builder.protectSenderId(true);
                packed = didComm.packEncrypted(builder.build()).getPackedMessage();
                break;
            }
            default:
                throw new IllegalArgumentException("unknown --mode " + mode);
        }
        writeOutput(opts.get("out"), packed);
    }

    /**
     * KNOWN DEVIATION (documented in tools/interop-live/README.md): like didcomm-python,
     * didcomm-jvm 0.3.2 only recognizes the General JWS serialization — a Flattened signed
     * envelope falls through its type detection and is mis-parsed as a plaintext JWM ("The
     * header \"id\" is missing"). The DIDComm v2.1 spec (§Message Signing) says "Either the
     * General or Flattened form of a JWS is valid. Message recipients MUST be able to process
     * both forms." didcomm-dotnet emits the spec-blessed Flattened form (PRD FR-SIG-02), so
     * before handing a STANDALONE signed envelope to didcomm-jvm we reshape Flattened →
     * General — a lossless RFC 7515 re-serialization (payload, protected header, and signature
     * bytes stay byte-identical; all verification below is still didcomm-jvm's). The same gap
     * makes the outbound anoncrypt(sign) cell N-A: the inner Flattened JWS sits inside the
     * ciphertext where no wire-level normalization can reach it.
     */
    private static String normalizeFlattenedJws(String packed) {
        JsonObject env;
        try {
            env = JsonParser.parseString(packed).getAsJsonObject();
        } catch (RuntimeException ex) {
            return packed;
        }
        if (!env.has("payload") || !env.has("signature") || env.has("signatures")) return packed;

        JsonObject signature = new JsonObject();
        if (env.has("protected")) signature.add("protected", env.get("protected"));
        if (env.has("header")) signature.add("header", env.get("header"));
        signature.add("signature", env.get("signature"));
        JsonArray signatures = new JsonArray();
        signatures.add(signature);
        JsonObject general = new JsonObject();
        general.add("payload", env.get("payload"));
        general.add("signatures", signatures);
        System.err.println("note: normalized spec-valid Flattened JWS to General for didcomm-jvm");
        return general.toString();
    }

    private static void unpack(Map<String, String> opts) throws IOException {
        DIDComm didComm = didCommFor(require(opts, "identity"));
        String packed = normalizeFlattenedJws(new String(
                Files.readAllBytes(Paths.get(require(opts, "in"))), StandardCharsets.UTF_8));

        UnpackResult result = didComm.unpack(new UnpackParams.Builder(packed).build());
        Metadata md = result.getMetadata();

        // Serialize the unpacked plaintext through didcomm-jvm itself (packPlaintext), so the
        // reported message is exactly the library's own JSON view of what it recovered.
        String plaintext = didComm
                .packPlaintext(new PackPlaintextParams.Builder(result.getMessage()).build())
                .getPackedMessage();

        // Normalize to the shared metadata vocabulary (see interop_peer.py cmd_unpack): for
        // protect_sender envelopes report the (inner) authcrypt algorithms, like the flags do.
        String encAlg = null;
        String kw = null;
        if (md.getEncAlgAuth() != null) {
            encAlg = "A256CBC-HS512";
            kw = "ECDH-1PU+A256KW";
        } else if (md.getEncAlgAnon() != null) {
            switch (md.getEncAlgAnon()) {
                case A256CBC_HS512_ECDH_ES_A256KW: encAlg = "A256CBC-HS512"; break;
                case A256GCM_ECDH_ES_A256KW: encAlg = "A256GCM"; break;
                case XC20P_ECDH_ES_A256KW: encAlg = "XC20P"; break;
            }
            kw = "ECDH-ES+A256KW";
        }
        String sigAlg = null;
        if (md.getSignAlg() != null) {
            switch (md.getSignAlg()) {
                case ED25519: sigAlg = "EdDSA"; break;
                case ES256: sigAlg = "ES256"; break;
                case ES256K: sigAlg = "ES256K"; break;
            }
        }

        JsonObject metadata = new JsonObject();
        metadata.addProperty("encrypted", md.getEncrypted());
        metadata.addProperty("authenticated", md.getAuthenticated());
        metadata.addProperty("non_repudiation", md.getNonRepudiation());
        metadata.addProperty("anonymous_sender", md.getAnonymousSender());
        metadata.addProperty("enc", encAlg);
        metadata.addProperty("kw", kw);
        metadata.addProperty("sig_alg", sigAlg);
        metadata.addProperty("signer_kid", md.getSignFrom());
        metadata.addProperty("sender_kid", md.getEncryptedFrom());
        metadata.addProperty("recipient_kid", (String) null); // didcomm-jvm reports the target list, not the hit

        JsonObject output = new JsonObject();
        output.add("message", JsonParser.parseString(plaintext).getAsJsonObject());
        output.add("metadata", metadata);
        writeOutput(opts.get("out"), GSON.toJson(output));
    }

    /** Same per-cell expectations as interop_peer.py expected_metadata (kept in sync by hand). */
    private static JsonObject expectedMetadata(String mode, String enc) {
        JsonObject expected = new JsonObject();
        switch (mode) {
            case "plaintext":
                expected.addProperty("encrypted", false);
                expected.addProperty("authenticated", false);
                expected.addProperty("non_repudiation", false);
                break;
            case "signed":
                expected.addProperty("encrypted", false);
                expected.addProperty("authenticated", true);
                expected.addProperty("non_repudiation", true);
                expected.addProperty("sig_alg", "EdDSA");
                break;
            case "anoncrypt":
                expected.addProperty("encrypted", true);
                expected.addProperty("authenticated", false);
                expected.addProperty("anonymous_sender", true);
                expected.addProperty("non_repudiation", false);
                expected.addProperty("enc", enc);
                expected.addProperty("kw", "ECDH-ES+A256KW");
                break;
            case "authcrypt":
                expected.addProperty("encrypted", true);
                expected.addProperty("authenticated", true);
                expected.addProperty("anonymous_sender", false);
                expected.addProperty("non_repudiation", false);
                expected.addProperty("enc", "A256CBC-HS512");
                expected.addProperty("kw", "ECDH-1PU+A256KW");
                break;
            case "anoncrypt-sign":
                expected.addProperty("encrypted", true);
                expected.addProperty("authenticated", true);
                expected.addProperty("anonymous_sender", true);
                expected.addProperty("non_repudiation", true);
                expected.addProperty("sig_alg", "EdDSA");
                expected.addProperty("enc", enc);
                break;
            case "anoncrypt-authcrypt":
                // enc/kw are per-layer and implementations report different layers; the flag
                // triple below is the composition's fingerprint, so assert exactly that.
                expected.addProperty("encrypted", true);
                expected.addProperty("authenticated", true);
                expected.addProperty("anonymous_sender", true);
                break;
            default:
                throw new IllegalArgumentException("unknown mode " + mode);
        }
        return expected;
    }

    private static void doAssert(Map<String, String> opts) throws IOException {
        JsonObject expected = readJson(require(opts, "expected"));
        JsonObject unpacked = readJson(require(opts, "unpacked"));
        String mode = require(opts, "mode");
        String enc = opts.getOrDefault("enc", "A256CBC-HS512");

        List<String> failures = new ArrayList<>();
        JsonObject actualMessage = unpacked.getAsJsonObject("message");
        for (String key : new String[] {"id", "type", "body", "from", "to", "created_time"}) {
            if (expected.has(key) && !expected.get(key).equals(actualMessage.get(key)))
                failures.add("message." + key + ": expected " + expected.get(key) + ", got " + actualMessage.get(key));
        }

        JsonObject actualMd = unpacked.getAsJsonObject("metadata");
        for (Map.Entry<String, JsonElement> entry : expectedMetadata(mode, enc).entrySet()) {
            JsonElement actual = actualMd.get(entry.getKey());
            if (!entry.getValue().equals(actual))
                failures.add("metadata." + entry.getKey() + ": expected " + entry.getValue() + ", got " + actual);
        }

        if (!failures.isEmpty()) {
            for (String failure : failures) System.err.println("ASSERT FAIL: " + failure);
            System.exit(1);
        }
    }

    // ── plumbing ──────────────────────────────────────────────────────────────────────────

    private static Message messageFromJson(JsonObject json) {
        Map<String, Object> body = new HashMap<>();
        JsonObject jsonBody = json.getAsJsonObject("body");
        if (jsonBody != null)
            for (Map.Entry<String, JsonElement> entry : jsonBody.entrySet())
                body.put(entry.getKey(), toPlain(entry.getValue()));

        MessageBuilder builder = new MessageBuilder(
                json.get("id").getAsString(), body, json.get("type").getAsString());
        if (json.has("from")) builder.from(json.get("from").getAsString());
        if (json.has("to")) {
            List<String> to = new ArrayList<>();
            for (JsonElement entry : json.getAsJsonArray("to")) to.add(entry.getAsString());
            builder.to(to);
        }
        if (json.has("created_time")) builder.createdTime(json.get("created_time").getAsLong());
        if (json.has("expires_time")) builder.expiresTime(json.get("expires_time").getAsLong());
        return builder.build();
    }

    private static Object toPlain(JsonElement element) {
        if (element.isJsonNull()) return null;
        if (element.isJsonPrimitive()) {
            if (element.getAsJsonPrimitive().isBoolean()) return element.getAsBoolean();
            if (element.getAsJsonPrimitive().isNumber()) return element.getAsNumber();
            return element.getAsString();
        }
        if (element.isJsonArray()) {
            List<Object> list = new ArrayList<>();
            for (JsonElement item : element.getAsJsonArray()) list.add(toPlain(item));
            return list;
        }
        Map<String, Object> map = new HashMap<>();
        for (Map.Entry<String, JsonElement> entry : element.getAsJsonObject().entrySet())
            map.put(entry.getKey(), toPlain(entry.getValue()));
        return map;
    }

    private static AnonCryptAlg anonAlg(String enc) {
        switch (enc) {
            case "A256CBC-HS512": return AnonCryptAlg.A256CBC_HS512_ECDH_ES_A256KW;
            case "A256GCM": return AnonCryptAlg.A256GCM_ECDH_ES_A256KW;
            case "XC20P": return AnonCryptAlg.XC20P_ECDH_ES_A256KW;
            default: throw new IllegalArgumentException("unknown --enc " + enc);
        }
    }

    private static JsonObject readJson(String path) throws IOException {
        return JsonParser.parseString(
                new String(Files.readAllBytes(Paths.get(path)), StandardCharsets.UTF_8)).getAsJsonObject();
    }

    private static void writeOutput(String path, String content) throws IOException {
        if (path == null || path.equals("-")) System.out.println(content);
        else Files.write(Paths.get(path),
                (content.endsWith("\n") ? content : content + "\n").getBytes(StandardCharsets.UTF_8));
    }

    private static String b64url(byte[] raw) {
        return Base64.getUrlEncoder().withoutPadding().encodeToString(raw);
    }

    private InteropPeer() {}
}
