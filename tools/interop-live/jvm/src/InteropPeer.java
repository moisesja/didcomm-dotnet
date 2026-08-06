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
import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.TreeMap;
import java.util.UUID;
import java.util.stream.Collectors;
import java.util.stream.Stream;
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
import org.didcommx.didcomm.protocols.routing.Routing;
import org.didcommx.didcomm.protocols.routing.UnpackForwardResult;
import org.didcommx.didcomm.protocols.routing.WrapInForwardResult;
import org.didcommx.didcomm.secret.Secret;
import org.didcommx.didcomm.secret.SecretResolver;
import org.didcommx.didcomm.secret.SecretResolverInMemory;

/**
 * SICPA didcomm-jvm counterpart for the live cross-implementation harness (PRD S13.6,
 * FR-IX-04/05/06). Drives org.didcommx:didcomm (didcomm-jvm, pinned in fetch-deps.sh) with the
 * same CLI shape as tools/InteropCli and python/interop_peer.py, so run-jvm-leg.sh can
 * round-trip envelopes both ways:
 *
 *   mint            mint an identity and write {did, secrets: [private JWKs]} — the identity-file
 *                   schema all three drivers share. Default: did:peer:2, X25519 keyAgreement +
 *                   Ed25519 authentication. Matrix dimensions: --ka-crv P-256, --ka-count 3,
 *                   --sig-crv P-256|secp256k1, --method key --key-crv X25519|Ed25519 (did:key),
 *                   and --route mediator.json[,mediator.json] (adds a .S DIDCommMessaging
 *                   service whose routingKeys name the mediators' #key-1, for Forward=true).
 *   gen-message     emit a fresh C.1-style "lets_do_lunch" payload.
 *   pack            pack a payload (plaintext | signed | anoncrypt | authcrypt | anoncrypt-sign
 *                   | anoncrypt-authcrypt); with --route m1.json[,m2.json] the envelope is
 *                   additionally wrapped in routing-2.0 forward layers (m1 outermost).
 *   unpack          unpack an envelope and print {message, metadata}, metadata normalized to
 *                   the shared vocabulary (enc / kw / sig_alg / ...).
 *   unwrap-forward  act as one mediator hop: unpack a forward envelope addressed to --identity
 *                   and print the attached onward envelope.
 *   verify-fixture  FR-IX-06: unpack a PUBLISHED didcomm-dotnet vector (spec Appendix A secrets
 *                   + Appendix B DID documents) and diff the recovered plaintext.
 *   assert          compare an unpack output ({message, metadata}, from EITHER implementation)
 *                   against the original payload + the metadata the mode/enc cell requires.
 *
 * See python/interop_peer.py for the authoritative semantics — the identity-file schema, kid
 * numbering, payload shape, normalized unpack output, and per-cell metadata expectations are
 * identical across all three drivers, and the two counterpart drivers are kept behaviourally
 * interchangeable (either one's minted identities can be handed to the other).
 *
 * did:peer:2 handling is implemented here against the did:peer spec (decode .Ez/.Vz
 * multibase(multicodec) segments, name keys #key-N in order of appearance — the numbering
 * net-did emits) rather than through the 2022-era peerdid package, so kids agree
 * byte-for-byte across implementations. did:key is decoded the same way (single
 * multibase(multicodec) key, kid = did#&lt;multibase&gt;). Service (.S) segments are ignored on
 * RESOLUTION (only didcomm-dotnet's resolver reads them, for outbound forward routing) and do
 * NOT consume a key number, matching net-did.
 *
 * stdout carries only the artifact; diagnostics go to stderr. Exit 0 on success, 1 on failure.
 */
public final class InteropPeer {

    // multicodec varint prefixes for raw public keys (multicodec table: x25519-pub,
    // ed25519-pub, p256-pub = varint(0x1200), secp256k1-pub = varint(0xe7)). EC payloads are
    // SEC1 COMPRESSED points, the form net-did and didcomm-python both encode.
    private static final byte[] MULTICODEC_X25519_PUB = {(byte) 0xEC, 0x01};
    private static final byte[] MULTICODEC_ED25519_PUB = {(byte) 0xED, 0x01};
    private static final byte[] MULTICODEC_P256_PUB = {(byte) 0x80, 0x24};
    private static final byte[] MULTICODEC_SECP256K1_PUB = {(byte) 0xE7, 0x01};

    private static final Gson GSON = new GsonBuilder().setPrettyPrinting().disableHtmlEscaping().create();

    public static void main(String[] args) {
        try {
            if (args.length == 0) {
                System.err.println("usage: InteropPeer "
                        + "mint|gen-message|pack|unpack|unwrap-forward|verify-fixture|assert [--opt value ...]");
                System.exit(2);
            }
            Map<String, String> opts = parseOpts(args);
            switch (args[0]) {
                case "mint": mint(opts); break;
                case "gen-message": genMessage(opts); break;
                case "pack": pack(opts); break;
                case "unpack": unpack(opts); break;
                case "unwrap-forward": unwrapForward(opts); break;
                case "verify-fixture": verifyFixture(opts); break;
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

    // ── elliptic-curve helpers (short Weierstrass over BigInteger) ────────────────────────

    /**
     * Minimal affine EC arithmetic for P-256 and secp256k1: keygen, SEC1 point compression and
     * SEC1 point DEcompression.
     *
     * WHY hand-rolled instead of JCA/nimbus — two independent reasons, both verified on this
     * harness's JDK:
     *   1. SEC1 compressed points are what multicodec key segments carry, and the JDK exposes
     *      no API to encode or decode them (java.security.spec.ECPoint is plain (x, y)); the
     *      y-recovery below is the same square root didcomm-python gets from `cryptography`.
     *   2. JDK 16 dropped secp256k1 from SunEC, so on the JDK this harness runs (25)
     *      `KeyPairGenerator.getInstance("EC")` with secp256k1 throws
     *      "InvalidAlgorithmParameterException: Curve not supported: secp256k1 (1.3.132.0.10)".
     *      Minting must still succeed, otherwise the ES256K cells would die at SETUP and the
     *      run would never reach — and never report — the real counterpart gap in the crypto
     *      layer (nimbus raises the same "Curve not supported" there).
     *
     * Scope is deliberately narrow: only public-key ENCODING and throwaway harness keygen live
     * here. Every signature, verification and ECDH operation the matrix measures is still
     * didcomm-jvm's. The scalar multiplication is textbook double-and-add and is NOT
     * constant-time — acceptable only because these keys are ephemeral test material that never
     * leaves the work directory.
     */
    private static final class EcCurve {
        private static final BigInteger TWO = BigInteger.valueOf(2);
        private static final BigInteger THREE = BigInteger.valueOf(3);

        final String crv;
        final byte[] multicodec;
        private final BigInteger p, a, b, gx, gy, n;
        private final int size; // field element width in bytes (the JWK x/y coordinate width)

        EcCurve(String crv, byte[] multicodec, String p, String a, String b,
                String gx, String gy, String n, int size) {
            this.crv = crv;
            this.multicodec = multicodec;
            this.p = new BigInteger(p, 16);
            this.a = new BigInteger(a, 16);
            this.b = new BigInteger(b, 16);
            this.gx = new BigInteger(gx, 16);
            this.gy = new BigInteger(gy, 16);
            this.n = new BigInteger(n, 16);
            this.size = size;
        }

        /** Fresh private scalar d in [1, n-1] plus its public point — as a private JWK. */
        JsonObject generatePrivateJwk() {
            SecureRandom random = new SecureRandom();
            BigInteger d;
            do {
                d = new BigInteger(n.bitLength(), random);
            } while (d.signum() == 0 || d.compareTo(n) >= 0);
            BigInteger[] q = multiplyBase(d);

            JsonObject jwk = new JsonObject();
            jwk.addProperty("kty", "EC");
            jwk.addProperty("crv", crv);
            jwk.addProperty("x", b64url(fixedWidth(q[0])));
            jwk.addProperty("y", b64url(fixedWidth(q[1])));
            jwk.addProperty("d", b64url(fixedWidth(d)));
            return jwk;
        }

        /** Public JWK (EC) -> SEC1 compressed point bytes, the multicodec payload. */
        byte[] compress(JsonObject jwk) {
            BigInteger x = new BigInteger(1, b64urlDecode(jwk.get("x").getAsString()));
            BigInteger y = new BigInteger(1, b64urlDecode(jwk.get("y").getAsString()));
            byte[] encoded = new byte[1 + size];
            encoded[0] = (byte) (y.testBit(0) ? 0x03 : 0x02);
            System.arraycopy(fixedWidth(x), 0, encoded, 1, size);
            return encoded;
        }

        /**
         * SEC1 point bytes -> public JWK. Compressed (0x02/0x03) is what the harness emits;
         * uncompressed (0x04) is accepted too because didcomm-python's from_encoded_point does,
         * and a counterpart is free to publish either form.
         */
        JsonObject toPublicJwk(byte[] point, String context) {
            BigInteger x;
            BigInteger y;
            if (point.length == 1 + size && (point[0] == 0x02 || point[0] == 0x03)) {
                x = new BigInteger(1, Arrays.copyOfRange(point, 1, 1 + size));
                // y^2 = x^3 + ax + b; both curves have p = 3 (mod 4), so the square root is
                // alpha^((p+1)/4). Pick the root whose parity matches the 0x02/0x03 prefix.
                BigInteger alpha = x.modPow(THREE, p).add(a.multiply(x)).add(b).mod(p);
                y = alpha.modPow(p.add(BigInteger.ONE).shiftRight(2), p);
                if (y.testBit(0) != (point[0] == 0x03)) y = p.subtract(y);
                if (!y.modPow(TWO, p).equals(alpha))
                    throw new IllegalArgumentException("point not on curve " + crv + " in " + context);
            } else if (point.length == 1 + 2 * size && point[0] == 0x04) {
                x = new BigInteger(1, Arrays.copyOfRange(point, 1, 1 + size));
                y = new BigInteger(1, Arrays.copyOfRange(point, 1 + size, 1 + 2 * size));
            } else {
                throw new IllegalArgumentException("malformed SEC1 " + crv + " point in " + context);
            }

            JsonObject jwk = new JsonObject();
            jwk.addProperty("kty", "EC");
            jwk.addProperty("crv", crv);
            jwk.addProperty("x", b64url(fixedWidth(x)));
            jwk.addProperty("y", b64url(fixedWidth(y)));
            return jwk;
        }

        private byte[] fixedWidth(BigInteger value) {
            byte[] raw = value.toByteArray();
            byte[] padded = new byte[size];
            // toByteArray() is two's-complement and minimal-length: it may carry a leading
            // sign 0x00 or be shorter than the field width. Right-align into a fixed buffer.
            int from = Math.max(0, raw.length - size);
            System.arraycopy(raw, from, padded, size - (raw.length - from), raw.length - from);
            return padded;
        }

        private BigInteger[] multiplyBase(BigInteger k) {
            BigInteger[] result = null; // null is the point at infinity
            BigInteger[] addend = {gx, gy};
            for (int bit = 0; bit < k.bitLength(); bit++) {
                if (k.testBit(bit)) result = addPoints(result, addend);
                addend = doublePoint(addend);
            }
            if (result == null) throw new IllegalStateException("degenerate scalar for " + crv);
            return result;
        }

        private BigInteger[] addPoints(BigInteger[] left, BigInteger[] right) {
            if (left == null) return right;
            if (right == null) return left;
            if (left[0].equals(right[0]))
                return left[1].equals(right[1]) ? doublePoint(left) : null;
            BigInteger lambda = right[1].subtract(left[1])
                    .multiply(right[0].subtract(left[0]).modInverse(p)).mod(p);
            BigInteger x = lambda.multiply(lambda).subtract(left[0]).subtract(right[0]).mod(p);
            return new BigInteger[] {x, lambda.multiply(left[0].subtract(x)).subtract(left[1]).mod(p)};
        }

        private BigInteger[] doublePoint(BigInteger[] point) {
            if (point == null || point[1].signum() == 0) return null;
            BigInteger lambda = point[0].multiply(point[0]).multiply(THREE).add(a)
                    .multiply(point[1].shiftLeft(1).modInverse(p)).mod(p);
            BigInteger x = lambda.multiply(lambda).subtract(point[0].shiftLeft(1)).mod(p);
            return new BigInteger[] {x, lambda.multiply(point[0].subtract(x)).subtract(point[1]).mod(p)};
        }
    }

    private static final EcCurve P256 = new EcCurve("P-256", MULTICODEC_P256_PUB,
            "ffffffff00000001000000000000000000000000ffffffffffffffffffffffff",
            "ffffffff00000001000000000000000000000000fffffffffffffffffffffffc",
            "5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b",
            "6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296",
            "4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5",
            "ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551", 32);

    private static final EcCurve SECP256K1 = new EcCurve("secp256k1", MULTICODEC_SECP256K1_PUB,
            "fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f",
            "0",
            "7",
            "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798",
            "483ada7726a3c4655da4fbfc0e1108a8fd17b448a68554199c47d08ffb10d4b8",
            "fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141", 32);

    private static final EcCurve[] EC_CURVES = {P256, SECP256K1};

    private static EcCurve ecCurve(String crv) {
        for (EcCurve curve : EC_CURVES) if (curve.crv.equals(crv)) return curve;
        return null;
    }

    // ── did:peer:2 / did:key (spec-conformant, hand-rolled: see class comment) ────────────

    /** One did:peer:2 key segment: '.&lt;purpose&gt;z&lt;base58btc(multicodec + raw key)&gt;'. */
    private static String encodeKeySegment(char purpose, byte[] multicodecPrefix, byte[] rawPublicKey) {
        byte[] prefixed = new byte[multicodecPrefix.length + rawPublicKey.length];
        System.arraycopy(multicodecPrefix, 0, prefixed, 0, multicodecPrefix.length);
        System.arraycopy(rawPublicKey, 0, prefixed, multicodecPrefix.length, rawPublicKey.length);
        return "." + purpose + "z" + Base58.encode(prefixed);
    }

    /** multicodec-prefixed key bytes -> public JWK (OKP raw coordinate / EC SEC1 point). */
    private static JsonObject decodeMulticodecKey(byte[] decoded, String context) {
        byte[] prefix = Arrays.copyOfRange(decoded, 0, 2);
        byte[] raw = Arrays.copyOfRange(decoded, 2, decoded.length);
        if (Arrays.equals(prefix, MULTICODEC_X25519_PUB)) return okpJwk("X25519", raw);
        if (Arrays.equals(prefix, MULTICODEC_ED25519_PUB)) return okpJwk("Ed25519", raw);
        for (EcCurve curve : EC_CURVES)
            if (Arrays.equals(prefix, curve.multicodec)) return curve.toPublicJwk(raw, context);
        throw new IllegalArgumentException("unsupported multicodec prefix "
                + String.format("%02x%02x", prefix[0], prefix[1]) + " in " + context);
    }

    private static JsonObject okpJwk(String crv, byte[] raw) {
        JsonObject jwk = new JsonObject();
        jwk.addProperty("kty", "OKP");
        jwk.addProperty("crv", crv);
        jwk.addProperty("x", b64url(raw));
        return jwk;
    }

    /** Public part of a JWK as the raw bytes a multicodec key segment carries. */
    private static byte[] multicodecBytes(JsonObject jwk) {
        if (jwk.get("kty").getAsString().equals("OKP")) return b64urlDecode(jwk.get("x").getAsString());
        return curveOf(jwk).compress(jwk);
    }

    private static byte[] multicodecPrefix(JsonObject jwk) {
        if (jwk.get("kty").getAsString().equals("OKP"))
            return jwk.get("crv").getAsString().equals("X25519")
                    ? MULTICODEC_X25519_PUB : MULTICODEC_ED25519_PUB;
        return curveOf(jwk).multicodec;
    }

    private static EcCurve curveOf(JsonObject jwk) {
        EcCurve curve = ecCurve(jwk.get("crv").getAsString());
        if (curve == null) throw new IllegalArgumentException("unsupported curve " + jwk.get("crv"));
        return curve;
    }

    /**
     * Decode a did:peer:2 into a didcomm-jvm DIDDoc. Keys are named #key-N in order of
     * appearance across ALL key segments (the did:peer spec's transform; .S service segments do
     * not consume a number, matching net-did); each becomes a JsonWebKey2020 verification
     * method so OKP and EC curves flow through one code path.
     */
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
            // Only didcomm-dotnet's resolver consumes services (outbound forward routing); the
            // segment is skipped here WITHOUT advancing keyIndex, so kids stay in agreement.
            if (purpose == 'S') continue;
            if (purpose != 'E' && purpose != 'V')
                throw new IllegalArgumentException("unsupported did:peer:2 purpose '" + purpose + "' in " + did);
            if (!encoded.startsWith("z"))
                throw new IllegalArgumentException("unsupported multibase '" + encoded.charAt(0) + "' in " + did);

            JsonObject jwk = decodeMulticodecKey(Base58.decode(encoded.substring(1)), did);
            keyIndex++;
            String kid = did + "#key-" + keyIndex;
            methods.add(jsonWebKeyMethod(kid, did, jwk));
            (purpose == 'E' ? keyAgreement : authentication).add(kid);
        }

        return new DIDDoc(did, keyAgreement, authentication, methods, new ArrayList<>());
    }

    /**
     * Decode a single-key did:key into a didcomm-jvm DIDDoc; kid = did#&lt;multibase&gt;, the form
     * net-did resolves to. X25519 keys land in keyAgreement, Ed25519 in authentication. (The
     * harness restricts did:key cells to those two shapes; no Ed25519-&gt;X25519 derivation here.)
     */
    static DIDDoc resolveDidKey(String did) {
        String multibase = did.substring("did:key:".length());
        if (!multibase.startsWith("z"))
            throw new IllegalArgumentException("unsupported multibase '"
                    + (multibase.isEmpty() ? "" : multibase.substring(0, 1)) + "' in " + did
                    + " (base58btc 'z' expected)");

        JsonObject jwk = decodeMulticodecKey(Base58.decode(multibase.substring(1)), did);
        String kid = did + "#" + multibase;
        boolean isKeyAgreement = jwk.get("crv").getAsString().equals("X25519");
        List<VerificationMethod> methods = new ArrayList<>();
        methods.add(jsonWebKeyMethod(kid, did, jwk));
        List<String> keyAgreement = new ArrayList<>();
        List<String> authentication = new ArrayList<>();
        (isKeyAgreement ? keyAgreement : authentication).add(kid);
        return new DIDDoc(did, keyAgreement, authentication, methods, new ArrayList<>());
    }

    private static VerificationMethod jsonWebKeyMethod(String kid, String controller, JsonObject jwk) {
        return new VerificationMethod(
                kid,
                VerificationMethodType.JSON_WEB_KEY_2020,
                new VerificationMaterial(VerificationMaterialFormat.JWK, jwk.toString()),
                controller);
    }

    /** Route by DID method, exactly like interop_peer.py's HarnessDidResolver. */
    private static final DIDDocResolver HARNESS_RESOLVER = did -> Optional.of(
            did.startsWith("did:key:") ? resolveDidKey(did) : resolvePeerDid2(did));

    // ── identity files (same schema as InteropCli/interop_peer.py) ────────────────────────

    private static SecretResolver secretsFor(String identityPath) throws IOException {
        return new SecretResolverInMemory(secretsFrom(readJson(identityPath).getAsJsonArray("secrets")));
    }

    private static List<Secret> secretsFrom(JsonArray jwks) {
        List<Secret> secrets = new ArrayList<>();
        for (JsonElement entry : jwks) {
            JsonObject jwk = entry.getAsJsonObject();
            secrets.add(new Secret(
                    jwk.get("kid").getAsString(),
                    VerificationMethodType.JSON_WEB_KEY_2020,
                    new VerificationMaterial(VerificationMaterialFormat.JWK, jwk.toString())));
        }
        return secrets;
    }

    private static DIDComm didCommFor(String identityPath) throws IOException {
        return new DIDComm(HARNESS_RESOLVER, secretsFor(identityPath));
    }

    /** Accept a DID directly or the counterpart's identity file (mirrors the other drivers). */
    private static String loadDid(String didOrPath) throws IOException {
        if (didOrPath.startsWith("did:")) return didOrPath;
        return readJson(didOrPath).get("did").getAsString();
    }

    /** Comma-separated list of DIDs or identity files -> the DIDs they name. */
    private static List<String> loadDids(String commaSeparated) throws IOException {
        List<String> dids = new ArrayList<>();
        for (String entry : commaSeparated.split(",")) dids.add(loadDid(entry));
        return dids;
    }

    // ── subcommands ───────────────────────────────────────────────────────────────────────

    private static JsonObject generatePrivateJwk(String crv) throws Exception {
        if (crv.equals("X25519")) {
            byte[] priv = X25519.generatePrivateKey();
            return privateOkpJwk(crv, X25519.publicFromPrivate(priv), priv);
        }
        if (crv.equals("Ed25519")) {
            Ed25519Sign.KeyPair pair = Ed25519Sign.KeyPair.newKeyPair();
            return privateOkpJwk(crv, pair.getPublicKey(), pair.getPrivateKey());
        }
        EcCurve curve = ecCurve(crv);
        if (curve == null) throw new IllegalArgumentException("unsupported curve " + crv);
        return curve.generatePrivateJwk();
    }

    private static JsonObject privateOkpJwk(String crv, byte[] pub, byte[] priv) {
        JsonObject jwk = okpJwk(crv, pub);
        jwk.addProperty("d", b64url(priv));
        return jwk;
    }

    /**
     * One did:peer:2 .S segment: a DIDCommMessaging service whose serviceEndpoint map names the
     * mediators' keyAgreement kids as routingKeys. The endpoint-map keys are spelled out in full
     * (uri/accept/routingKeys) — the nested form net-did itself emits and didcomm-dotnet's
     * ServiceEndpointParser consumes for Forward=true; only the top-level keys are abbreviated
     * per the did:peer spec (t=type, s=serviceEndpoint). The JSON is compact and key-ordered to
     * byte-match interop_peer.py's encode_service_segment, so a DID minted by either driver is
     * the same string.
     */
    private static String encodeServiceSegment(List<String> routingKids) {
        JsonArray routingKeys = new JsonArray();
        for (String kid : routingKids) routingKeys.add(kid);
        JsonArray accept = new JsonArray();
        accept.add("didcomm/v2");
        JsonObject endpoint = new JsonObject();
        endpoint.addProperty("uri", "https://interop.invalid/forward");
        endpoint.add("accept", accept);
        endpoint.add("routingKeys", routingKeys);
        JsonObject service = new JsonObject();
        service.addProperty("t", "dm");
        service.add("s", endpoint);
        return ".S" + b64url(service.toString().getBytes(StandardCharsets.UTF_8));
    }

    private static void mint(Map<String, String> opts) throws Exception {
        String method = opts.getOrDefault("method", "peer");
        String did;
        JsonArray secrets = new JsonArray();

        if (method.equals("key")) {
            JsonObject jwk = generatePrivateJwk(opts.getOrDefault("key-crv", "X25519"));
            String multibase = "z" + Base58.encode(concat(multicodecPrefix(jwk), multicodecBytes(jwk)));
            did = "did:key:" + multibase;
            jwk.addProperty("kid", did + "#" + multibase);
            secrets.add(jwk);
        } else if (method.equals("peer")) {
            String kaCrv = opts.getOrDefault("ka-crv", "X25519");
            int kaCount = Integer.parseInt(opts.getOrDefault("ka-count", "1"));
            List<JsonObject> keys = new ArrayList<>();
            for (int i = 0; i < kaCount; i++) keys.add(generatePrivateJwk(kaCrv));
            JsonObject sigJwk = generatePrivateJwk(opts.getOrDefault("sig-crv", "Ed25519"));

            // keyAgreement segments first, then the signing segment: the order that makes the
            // #key-N numbering below match what every driver's resolver reconstructs.
            StringBuilder builder = new StringBuilder("did:peer:2");
            for (JsonObject jwk : keys)
                builder.append(encodeKeySegment('E', multicodecPrefix(jwk), multicodecBytes(jwk)));
            builder.append(encodeKeySegment('V', multicodecPrefix(sigJwk), multicodecBytes(sigJwk)));
            keys.add(sigJwk);
            if (opts.containsKey("route")) {
                List<String> routingKids = new ArrayList<>();
                for (String mediator : loadDids(opts.get("route"))) routingKids.add(mediator + "#key-1");
                builder.append(encodeServiceSegment(routingKids));
            }
            did = builder.toString();

            // Kids are assigned against the FINAL DID string (the .S segment is part of it).
            for (int i = 0; i < keys.size(); i++) {
                keys.get(i).addProperty("kid", did + "#key-" + (i + 1));
                secrets.add(keys.get(i));
            }
        } else {
            throw new IllegalArgumentException("unknown --method '" + method + "' (peer|key)");
        }

        JsonObject identity = new JsonObject();
        identity.addProperty("did", did);
        identity.add("secrets", secrets);
        Files.write(Paths.get(require(opts, "out")),
                (GSON.toJson(identity) + "\n").getBytes(StandardCharsets.UTF_8));
        System.err.println("minted " + did);
        System.out.println(did);
    }

    private static void genMessage(Map<String, String> opts) throws IOException {
        JsonObject message = new JsonObject();
        message.addProperty("id", UUID.randomUUID().toString());
        message.addProperty("type", "http://example.com/protocols/lets_do_lunch/1.0/proposal");
        JsonArray to = new JsonArray();
        for (String did : loadDids(require(opts, "to"))) to.add(did);
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
        SecretResolver secrets = secretsFor(require(opts, "identity"));
        DIDComm didComm = new DIDComm(HARNESS_RESOLVER, secrets);
        String ownDid = loadDid(require(opts, "identity"));
        List<String> recipients = loadDids(require(opts, "to"));
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
                // didcomm-jvm's PackEncryptedParams carries a single `to` DID (one recipient
                // DID, whose keyAgreement keys may of course be several). Addressing several
                // DISTINCT DIDs in one envelope — what --to a,b,c asks for — has no API here.
                if (recipients.size() > 1)
                    throw new IllegalArgumentException("didcomm-jvm 0.3.2 packs to a single recipient DID "
                            + "(PackEncryptedParams.to is a String); asked for " + recipients.size()
                            + " — use one DID with several keyAgreement keys (--ka-count) instead");
                PackEncryptedParams.Builder builder = new PackEncryptedParams.Builder(message, recipients.get(0))
                        .forward(false) // any forward onion is built explicitly below, from --route
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

        // Mediated inbound (FR-IX-05 x routing): wrap the envelope in routing-2.0 forward
        // layers with didcomm-jvm's own Routing.wrapInForward. Routing keys are the mediator
        // DIDs, outermost first (routingKeys[0] ends up as the outer layer) — the same
        // ordering interop_peer.py documents for didcomm-python's wrap_in_forward.
        if (opts.containsKey("route")) {
            List<String> mediators = loadDids(opts.get("route"));
            WrapInForwardResult forwarded = new Routing(HARNESS_RESOLVER, secrets).wrapInForward(
                    jsonToMap(JsonParser.parseString(packed).getAsJsonObject()),
                    recipients.get(0),
                    null,       // encAlgAnon: didcomm-jvm's default anoncrypt for the forward layers
                    mediators,
                    null,       // no extra forward headers
                    null, null); // resolvers: the ones the Routing instance was built with
            if (forwarded == null)
                throw new IllegalStateException("didcomm-jvm wrapInForward returned no envelope for "
                        + mediators);
            packed = forwarded.getMsgEncrypted().getPackedMessage();
        }
        writeOutput(opts.get("out"), packed);
    }

    /**
     * NOTE for whoever touches the signing path next: didcomm-jvm 0.3.2 recognizes ONLY the
     * General JWS serialization. A Flattened signed envelope falls through its type detection
     * and is mis-parsed as a plaintext JWM ("The header \"id\" is missing"), and inside an
     * anoncrypt(sign) ciphertext no wire-level shim could reach it at all. The spec (§Message
     * Signing) blesses both forms — "Either the General or Flattened form of a JWS is valid.
     * Message recipients MUST be able to process both forms" — so this is a counterpart gap,
     * but didcomm-dotnet has emitted General at every signer count since DataProofsDotnet.Jose
     * 1.3.0 and the driver therefore hands envelopes to didcomm-jvm untouched. This driver
     * carried a lossless Flattened→General re-serialization shim until that release; it was
     * removed once every leg confirmed nothing reaches it (see README.md, "Known didcomm-dotnet
     * defect (fixed upstream)"). Reintroducing Flattened output would break the signed and
     * anoncrypt(sign) cells here, not just cosmetically.
     */
    private static void unpack(Map<String, String> opts) throws IOException {
        DIDComm didComm = didCommFor(require(opts, "identity"));
        String packed = readInput(opts.getOrDefault("in", "-"));

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

    /** One mediator hop: unpack the forward envelope addressed to --identity, emit the onward envelope. */
    private static void unwrapForward(Map<String, String> opts) throws IOException {
        SecretResolver secrets = secretsFor(require(opts, "identity"));
        String packed = readInput(opts.getOrDefault("in", "-"));

        UnpackForwardResult result = new Routing(HARNESS_RESOLVER, secrets)
                .unpackForward(packed, false, null, null);
        System.err.println("forward hop unwrapped; next: " + result.getForwardMsg().getForwardNext());
        writeOutput(opts.get("out"), GSON.toJsonTree(result.getForwardMsg().getForwardedMsg()).toString());
    }

    // ── FR-IX-06: verify the published didcomm-dotnet vectors with didcomm-jvm ────────────

    /**
     * Resolver over the fixture diddocs (spec Appendix B actors). They are DID-Core JSON —
     * verificationMethod[] carrying publicKeyJwk — which didcomm-jvm has no deserializer for
     * (unlike didcomm-python's DIDDoc.deserialize), so each document is mapped by hand onto
     * DIDDoc / VerificationMethod. Services are dropped: unpacking never consults them.
     * Files are read in sorted order so that two documents sharing an id (bob.json and
     * bob-with-routing.json both describe did:example:bob) resolve deterministically.
     */
    private static DIDDocResolver staticResolver(String diddocsDir) throws IOException {
        Map<String, DIDDoc> docs = new TreeMap<>();
        try (Stream<Path> files = Files.list(Paths.get(diddocsDir))) {
            List<Path> sorted = files.filter(path -> path.toString().endsWith(".json"))
                    .sorted().collect(Collectors.toList());
            for (Path path : sorted) {
                JsonObject doc = readJson(path.toString());
                String did = doc.get("id").getAsString();
                List<VerificationMethod> methods = new ArrayList<>();
                for (JsonElement entry : doc.getAsJsonArray("verificationMethod")) {
                    JsonObject vm = entry.getAsJsonObject();
                    if (!vm.has("publicKeyJwk"))
                        throw new IllegalArgumentException("verification method " + vm.get("id")
                                + " in " + path + " is not publicKeyJwk material");
                    methods.add(jsonWebKeyMethod(
                            vm.get("id").getAsString(),
                            vm.has("controller") ? vm.get("controller").getAsString() : did,
                            vm.getAsJsonObject("publicKeyJwk")));
                }
                docs.put(did, new DIDDoc(did, kidList(doc, "keyAgreement"),
                        kidList(doc, "authentication"), methods, new ArrayList<>()));
            }
        }
        return did -> Optional.ofNullable(docs.get(did));
    }

    private static List<String> kidList(JsonObject doc, String relationship) {
        List<String> kids = new ArrayList<>();
        if (doc.has(relationship))
            for (JsonElement entry : doc.getAsJsonArray(relationship)) kids.add(entry.getAsString());
        return kids;
    }

    /**
     * FR-IX-06: unpack one published didcomm-dotnet vector with didcomm-jvm, resolving the spec
     * Appendix A/B actors from the fixtures tree, and diff the recovered plaintext against the
     * expected payload. The vector goes in exactly as published — no normalization of any kind
     * — so a PASS is didcomm-jvm verifying the bytes we ship.
     */
    private static void verifyFixture(Map<String, String> opts) throws IOException {
        List<Secret> secrets = new ArrayList<>();
        for (String path : require(opts, "secrets").split(","))
            secrets.addAll(secretsFrom(JsonParser.parseString(
                    readInput(path)).getAsJsonArray()));
        DIDComm didComm = new DIDComm(
                staticResolver(require(opts, "diddocs")), new SecretResolverInMemory(secrets));

        String packedPath = require(opts, "packed");
        String packed = readInput(packedPath);
        JsonObject expected = readJson(require(opts, "expected"));

        UnpackResult result = didComm.unpack(new UnpackParams.Builder(packed).build());
        // Compare through didcomm-jvm's own plaintext serialization, so the diff is against the
        // library's JSON view of what it recovered (same convention as `unpack`).
        JsonObject actual = JsonParser.parseString(didComm
                .packPlaintext(new PackPlaintextParams.Builder(result.getMessage()).build())
                .getPackedMessage()).getAsJsonObject();

        List<String> failures = diffMessage(expected, actual);
        if (!failures.isEmpty()) {
            for (String failure : failures) System.err.println("VERIFY FAIL: " + failure);
            System.exit(1);
        }
        System.err.println("verified " + Paths.get(packedPath).getFileName() + " with didcomm-jvm");
    }

    /** The plaintext fields every driver diffs: identity, routing and timing of the message. */
    private static List<String> diffMessage(JsonObject expected, JsonObject actual) {
        List<String> failures = new ArrayList<>();
        for (String key : new String[] {"id", "type", "body", "from", "to", "created_time"})
            if (expected.has(key) && !expected.get(key).equals(actual.get(key)))
                failures.add("message." + key + ": expected " + expected.get(key) + ", got " + actual.get(key));
        return failures;
    }

    /** Same per-cell expectations as interop_peer.py expected_metadata (kept in sync by hand). */
    private static JsonObject expectedMetadata(String mode, String enc, String sigAlg) {
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
                expected.addProperty("sig_alg", sigAlg);
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
                expected.addProperty("sig_alg", sigAlg);
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
        String sigAlg = opts.getOrDefault("sig-alg", "EdDSA");

        List<String> failures = diffMessage(expected, unpacked.getAsJsonObject("message"));

        JsonObject actualMd = unpacked.getAsJsonObject("metadata");
        for (Map.Entry<String, JsonElement> entry : expectedMetadata(mode, enc, sigAlg).entrySet()) {
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

    /** A packed envelope as the Map<String, Object> didcomm-jvm's routing API expects. */
    @SuppressWarnings("unchecked")
    private static Map<String, Object> jsonToMap(JsonObject json) {
        return (Map<String, Object>) toPlain(json);
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
        // Insertion-ordered: the forward attachment is re-serialized from this map, and a
        // stable member order keeps the onward envelope readable when a run is debugged.
        Map<String, Object> map = new LinkedHashMap<>();
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

    private static byte[] concat(byte[] left, byte[] right) {
        byte[] joined = new byte[left.length + right.length];
        System.arraycopy(left, 0, joined, 0, left.length);
        System.arraycopy(right, 0, joined, left.length, right.length);
        return joined;
    }

    private static JsonObject readJson(String path) throws IOException {
        return JsonParser.parseString(readInput(path)).getAsJsonObject();
    }

    /** Read a file, or stdin when the path is "-" (the other drivers accept both). */
    private static String readInput(String path) throws IOException {
        if (path.equals("-"))
            return new String(System.in.readAllBytes(), StandardCharsets.UTF_8);
        return new String(Files.readAllBytes(Paths.get(path)), StandardCharsets.UTF_8);
    }

    private static void writeOutput(String path, String content) throws IOException {
        if (path == null || path.equals("-")) System.out.println(content);
        else Files.write(Paths.get(path),
                (content.endsWith("\n") ? content : content + "\n").getBytes(StandardCharsets.UTF_8));
    }

    private static String b64url(byte[] raw) {
        return Base64.getUrlEncoder().withoutPadding().encodeToString(raw);
    }

    private static byte[] b64urlDecode(String value) {
        return Base64.getUrlDecoder().decode(value);
    }

    private InteropPeer() {}
}
