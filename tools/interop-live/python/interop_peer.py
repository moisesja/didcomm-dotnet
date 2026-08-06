#!/usr/bin/env python3
"""
SICPA didcomm-python counterpart for the live cross-implementation harness (PRD S13.6,
FR-IX-04/05).

Drives `didcomm` (sicpa-dlab/didcomm-python, pinned in requirements.txt) directly with the
same CLI shape as tools/InteropCli, so run-python-leg.sh can round-trip envelopes both ways
over did:peer:

  mint         mint an identity and write {did, secrets: [private JWKs]} -- the identity-file
               schema InteropCli uses. Default: did:peer:2, X25519 keyAgreement + Ed25519
               authentication. Matrix dimensions: --ka-crv P-256, --sig-crv P-256|secp256k1,
               --ka-count 3, --method key --key-crv X25519|Ed25519 (did:key), and
               --route mediator.json[,mediator.json] (adds a DIDCommMessaging service whose
               routingKeys point at the mediators' #key-1, for didcomm-dotnet's Forward=true).
  gen-message  emit a fresh C.1-style "lets_do_lunch" payload (the harness feeds the SAME
               payload file to whichever side packs, then diffs it against the unpack).
  pack         pack a payload with didcomm-python (plaintext | signed | anoncrypt | authcrypt
               | anoncrypt-sign | anoncrypt-authcrypt) and print the envelope. With
               --route m1.json[,m2.json] the packed envelope is additionally wrapped in
               routing-2.0 forward layers (didcomm-python's wrap_in_forward; m1 outermost).
  unpack       unpack an envelope with didcomm-python and print {message, metadata} with the
               metadata keys normalized to InteropCli's vocabulary (enc / kw / sig_alg / ...).
  unwrap-forward  act as one mediator hop: unpack a forward envelope addressed to --identity
               (didcomm-python's unpack_forward) and print the attached onward envelope.
  verify-fixture  FR-IX-06: unpack a PUBLISHED didcomm-dotnet vector (spec Appendix A/B
               actors) with didcomm-python and diff the recovered plaintext.
  assert       compare an unpack output ({message, metadata}, from EITHER implementation)
               against the original payload + the metadata the mode/enc cell requires.

did:peer:2 handling is implemented here against the did:peer spec rather than through the
(2022-era) `peerdid` package: keys are decoded from the DID's .Ez/.Vz multibase(multicodec)
segments and named #key-N in order of appearance -- the numbering the current did:peer spec
defines and net-did emits -- so kids agree byte-for-byte across both implementations.
did:key is decoded the same way (single multibase(multicodec) key, kid = did#<multibase>).
Service (.S) segments are ignored on RESOLUTION here (only didcomm-dotnet's resolver reads
them, for outbound forward routing); key numbering skips them, matching net-did.

stdout carries only the artifact; diagnostics go to stderr. Exit 0 on success, 1 on failure.
"""

import argparse
import asyncio
import base64
import glob
import json
import os
import sys
import time
import uuid

import base58
from authlib.jose import ECKey, OKPKey
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.serialization import Encoding, PublicFormat

from didcomm.common.algorithms import AnonCryptAlg, AuthCryptAlg
from didcomm.common.resolvers import ResolversConfig
from didcomm.common.types import (
    VerificationMaterial,
    VerificationMaterialFormat,
    VerificationMethodType,
)
from didcomm.did_doc.did_doc import DIDDoc
from didcomm.did_doc.did_resolver import DIDResolver
from didcomm.pack_encrypted import PackEncryptedConfig, pack_encrypted
from didcomm.pack_plaintext import pack_plaintext
from didcomm.pack_signed import pack_signed
from didcomm.protocols.routing.forward import unpack_forward, wrap_in_forward
from didcomm.secrets.secrets_resolver import Secret
from didcomm.secrets.secrets_resolver_in_memory import SecretsResolverInMemory
from didcomm.unpack import unpack

# multicodec varint prefixes for raw public keys (multicodec table: x25519-pub, ed25519-pub,
# p256-pub = varint(0x1200), secp256k1-pub = varint(0xe7)). EC payloads are SEC1 compressed.
MULTICODEC_X25519_PUB = bytes([0xEC, 0x01])
MULTICODEC_ED25519_PUB = bytes([0xED, 0x01])
MULTICODEC_P256_PUB = bytes([0x80, 0x24])
MULTICODEC_SECP256K1_PUB = bytes([0xE7, 0x01])

EC_CURVES = {"P-256": ec.SECP256R1(), "secp256k1": ec.SECP256K1()}
EC_MULTICODEC = {"P-256": MULTICODEC_P256_PUB, "secp256k1": MULTICODEC_SECP256K1_PUB}

ANON_ALG_BY_ENC = {
    "A256CBC-HS512": AnonCryptAlg.A256CBC_HS512_ECDH_ES_A256KW,
    "A256GCM": AnonCryptAlg.A256GCM_ECDH_ES_A256KW,
    "XC20P": AnonCryptAlg.XC20P_ECDH_ES_A256KW,
}

MODES = ("plaintext", "signed", "anoncrypt", "authcrypt", "anoncrypt-sign", "anoncrypt-authcrypt")


def b64url_no_pad(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def b64url_decode(value: str) -> bytes:
    return base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))


# --- did:peer:2 / did:key (spec-conformant, hand-rolled: see module docstring) -------------


def encode_key_segment(purpose: str, multicodec_prefix: bytes, raw_public_key: bytes) -> str:
    """One did:peer:2 key segment: '.<purpose>z<base58btc(multicodec + raw key)>'."""
    return "." + purpose + "z" + base58.b58encode(multicodec_prefix + raw_public_key).decode("ascii")


def decode_multicodec_key(decoded: bytes, context: str) -> dict:
    """multicodec-prefixed raw key bytes -> public JWK dict (OKP raw / EC SEC1 compressed)."""
    prefix, raw = decoded[:2], decoded[2:]
    if prefix == MULTICODEC_X25519_PUB:
        return {"kty": "OKP", "crv": "X25519", "x": b64url_no_pad(raw)}
    if prefix == MULTICODEC_ED25519_PUB:
        return {"kty": "OKP", "crv": "Ed25519", "x": b64url_no_pad(raw)}
    for crv, codec in EC_MULTICODEC.items():
        if prefix == codec:
            point = ec.EllipticCurvePublicKey.from_encoded_point(EC_CURVES[crv], raw)
            numbers = point.public_numbers()
            size = (point.curve.key_size + 7) // 8
            return {
                "kty": "EC",
                "crv": crv,
                "x": b64url_no_pad(numbers.x.to_bytes(size, "big")),
                "y": b64url_no_pad(numbers.y.to_bytes(size, "big")),
            }
    raise ValueError(f"unsupported multicodec prefix {prefix.hex()} in {context}")


def compressed_public_bytes(jwk: dict) -> bytes:
    """Public JWK (EC) -> SEC1 compressed point bytes, for multicodec encoding."""
    numbers = ec.EllipticCurvePublicNumbers(
        int.from_bytes(b64url_decode(jwk["x"]), "big"),
        int.from_bytes(b64url_decode(jwk["y"]), "big"),
        EC_CURVES[jwk["crv"]],
    )
    return numbers.public_key().public_bytes(Encoding.X962, PublicFormat.CompressedPoint)


def resolve_peer_did_2(did: str) -> DIDDoc:
    """
    Decode a did:peer:2 into a didcomm-python DIDDoc. Keys are named #key-N in order of
    appearance across ALL key segments (the did:peer spec's transform; .S service segments do
    not consume a number, matching net-did); each becomes a JsonWebKey2020 verification method
    so OKP and EC curves flow through one code path.
    """
    if not did.startswith("did:peer:2"):
        raise ValueError(f"not a did:peer:2: {did}")

    verification_methods = []
    authentication = []
    key_agreement = []
    key_index = 0

    for segment in did[len("did:peer:2"):].split("."):
        if not segment:
            continue
        purpose, encoded = segment[0], segment[1:]
        if purpose == "S":
            continue  # only didcomm-dotnet's resolver consumes services (outbound forward routing)
        if purpose not in ("E", "V"):
            raise ValueError(f"unsupported did:peer:2 purpose '{purpose}' in {did}")
        if not encoded.startswith("z"):
            raise ValueError(f"unsupported multibase '{encoded[:1]}' in {did} (base58btc 'z' expected)")

        jwk = decode_multicodec_key(base58.b58decode(encoded[1:]), did)
        key_index += 1
        kid = f"{did}#key-{key_index}"
        verification_methods.append(
            {
                "id": kid,
                "type": VerificationMethodType.JSON_WEB_KEY_2020,
                "controller": did,
                "publicKeyJwk": jwk,
            }
        )
        (key_agreement if purpose == "E" else authentication).append(kid)

    return DIDDoc.deserialize(
        {
            "id": did,
            "verificationMethod": verification_methods,
            "authentication": authentication,
            "keyAgreement": key_agreement,
        }
    )


def resolve_did_key(did: str) -> DIDDoc:
    """
    Decode a single-key did:key into a didcomm-python DIDDoc; kid = did#<multibase>, the form
    net-did resolves to. X25519 keys land in keyAgreement, Ed25519 in authentication. (The
    harness restricts did:key cells to those two shapes; no Ed25519->X25519 derivation here.)
    """
    multibase = did[len("did:key:"):]
    if not multibase.startswith("z"):
        raise ValueError(f"unsupported multibase '{multibase[:1]}' in {did} (base58btc 'z' expected)")
    jwk = decode_multicodec_key(base58.b58decode(multibase[1:]), did)
    kid = f"{did}#{multibase}"
    is_key_agreement = jwk["crv"] == "X25519"
    return DIDDoc.deserialize(
        {
            "id": did,
            "verificationMethod": [
                {
                    "id": kid,
                    "type": VerificationMethodType.JSON_WEB_KEY_2020,
                    "controller": did,
                    "publicKeyJwk": jwk,
                }
            ],
            "authentication": [] if is_key_agreement else [kid],
            "keyAgreement": [kid] if is_key_agreement else [],
        }
    )


class HarnessDidResolver(DIDResolver):
    async def resolve(self, did):
        did = str(did)
        if did.startswith("did:key:"):
            return resolve_did_key(did)
        return resolve_peer_did_2(did)


# --- identity files (same schema as InteropCli: {did, secrets: [private JWKs]}) ------------


def load_resolvers(identity_path: str) -> ResolversConfig:
    with open(identity_path, encoding="utf-8") as f:
        identity = json.load(f)
    secrets = [
        Secret(
            kid=jwk["kid"],
            type=VerificationMethodType.JSON_WEB_KEY_2020,
            verification_material=VerificationMaterial(
                format=VerificationMaterialFormat.JWK, value=json.dumps(jwk)
            ),
        )
        for jwk in identity["secrets"]
    ]
    return ResolversConfig(
        secrets_resolver=SecretsResolverInMemory(secrets), did_resolver=HarnessDidResolver()
    )


def load_did(did_or_identity_path: str) -> str:
    """Accept a DID directly or the counterpart's identity file (mirrors InteropCli --to)."""
    if did_or_identity_path.startswith("did:"):
        return did_or_identity_path
    with open(did_or_identity_path, encoding="utf-8") as f:
        return json.load(f)["did"]


# --- subcommands ---------------------------------------------------------------------------


def generate_private_jwk(crv: str) -> dict:
    if crv in ("X25519", "Ed25519"):
        return OKPKey.generate_key(crv, is_private=True).as_dict(is_private=True)
    if crv in EC_CURVES:
        return ECKey.generate_key(crv, is_private=True).as_dict(is_private=True)
    raise ValueError(f"unsupported curve {crv}")


def multicodec_bytes(jwk: dict) -> bytes:
    """Public part of a JWK as the raw bytes a multicodec key segment carries."""
    if jwk["kty"] == "OKP":
        return b64url_decode(jwk["x"])
    return compressed_public_bytes(jwk)


def multicodec_prefix(jwk: dict) -> bytes:
    if jwk["kty"] == "OKP":
        return MULTICODEC_X25519_PUB if jwk["crv"] == "X25519" else MULTICODEC_ED25519_PUB
    return EC_MULTICODEC[jwk["crv"]]


def encode_service_segment(routing_kids: list) -> str:
    """
    One did:peer:2 .S segment: a DIDCommMessaging service whose serviceEndpoint map names the
    mediators' keyAgreement kids as routingKeys. The endpoint-map keys are spelled out in full
    (uri/accept/routingKeys) — the nested form net-did itself emits and didcomm-dotnet's
    ServiceEndpointParser consumes for Forward=true; only the top-level keys are abbreviated
    per the did:peer spec (t=type, s=serviceEndpoint).
    """
    service = {
        "t": "dm",
        "s": {
            "uri": "https://interop.invalid/forward",
            "accept": ["didcomm/v2"],
            "routingKeys": routing_kids,
        },
    }
    encoded = b64url_no_pad(json.dumps(service, separators=(",", ":")).encode("utf-8"))
    return ".S" + encoded


def cmd_mint(args):
    if args.method == "key":
        jwk = generate_private_jwk(args.key_crv)
        did = "did:key:z" + base58.b58encode(
            multicodec_prefix(jwk) + multicodec_bytes(jwk)
        ).decode("ascii")
        jwk["kid"] = f"{did}#{did[len('did:key:'):]}"
        secrets = [jwk]
    else:
        ka_jwks = [generate_private_jwk(args.ka_crv) for _ in range(args.ka_count)]
        sig_jwk = generate_private_jwk(args.sig_crv)

        did = "did:peer:2"
        for jwk in ka_jwks:
            did += encode_key_segment("E", multicodec_prefix(jwk), multicodec_bytes(jwk))
        did += encode_key_segment("V", multicodec_prefix(sig_jwk), multicodec_bytes(sig_jwk))
        if args.route:
            routing_kids = [load_did(path) + "#key-1" for path in args.route.split(",")]
            did += encode_service_segment(routing_kids)

        secrets = ka_jwks + [sig_jwk]
        for index, jwk in enumerate(secrets, start=1):
            jwk["kid"] = f"{did}#key-{index}"

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump({"did": did, "secrets": secrets}, f, indent=2)
        f.write("\n")
    print(f"minted {did}", file=sys.stderr)
    print(did)


def cmd_gen_message(args):
    message = {
        "id": str(uuid.uuid4()),
        "type": "http://example.com/protocols/lets_do_lunch/1.0/proposal",
        "to": [load_did(entry) for entry in args.to.split(",")],
        "created_time": int(time.time()),
        "body": {"messagespecificattribute": "and its value"},
    }
    if args.frm:
        message["from"] = load_did(args.frm)
    write_output(args.out, json.dumps(message, indent=2))


def cmd_pack(args):
    resolvers = load_resolvers(args.identity)
    own_did = load_did(args.identity)
    to = load_did(args.to)
    with open(args.message, encoding="utf-8") as f:
        message = json.load(f)

    async def run():
        if args.mode == "plaintext":
            return (await pack_plaintext(resolvers, message)).packed_msg
        if args.mode == "signed":
            return (await pack_signed(resolvers, message, sign_frm=own_did)).packed_msg

        if args.mode in ("authcrypt", "anoncrypt-authcrypt") and args.enc != "A256CBC-HS512":
            raise ValueError(f"didcomm-python authcrypt supports A256CBC-HS512 only, not {args.enc}")
        config = PackEncryptedConfig(
            enc_alg_anon=ANON_ALG_BY_ENC[args.enc],
            enc_alg_auth=AuthCryptAlg.A256CBC_HS512_ECDH_1PU_A256KW,
            protect_sender_id=(args.mode == "anoncrypt-authcrypt"),
            forward=False,  # direct exchange, no mediator in the loop
        )
        frm = own_did if args.mode in ("authcrypt", "anoncrypt-authcrypt") else None
        sign_frm = own_did if args.mode == "anoncrypt-sign" else None
        result = await pack_encrypted(
            resolvers, message, to=to, frm=frm, sign_frm=sign_frm, pack_config=config
        )
        packed = result.packed_msg

        # Mediated inbound (FR-IX-05 x routing): wrap the envelope in routing-2.0 forward
        # layers with didcomm-python's own wrap_in_forward. Routing keys are the mediator DIDs,
        # outermost first (routing_keys[0] ends up as the outer layer).
        if args.route:
            mediator_dids = [load_did(path) for path in args.route.split(",")]
            fwd = await wrap_in_forward(
                resolvers, json.loads(packed), to=to, routing_keys=mediator_dids
            )
            packed = json.dumps(fwd.msg_encrypted.msg)
        return packed

    write_output(args.out, asyncio.get_event_loop().run_until_complete(run()))


# HISTORICAL NOTE (kept because the shape of this harness was argued over): through
# DataProofsDotnet.Jose 1.2.1 didcomm-dotnet emitted the Flattened JWS serialization, which
# didcomm-python 0.3.2 cannot parse (`validate_jws` requires a "signatures" array) even though
# the DIDComm v2.1 spec says recipients MUST process both forms. This driver carried a
# lossless Flattened -> General re-serialization to get standalone signed envelopes through.
# DataProofsDotnet.Jose 1.3.0 emits General at every signer count, so the shim never fired
# again and was REMOVED deliberately: leaving it in place would silently absorb a regression
# back to Flattened and report a green cell for an envelope the counterpart cannot actually
# read. The harness now hands didcomm-python the exact wire bytes didcomm-dotnet produces.


def cmd_unpack(args):
    resolvers = load_resolvers(args.identity)
    if args.infile == "-":
        packed = sys.stdin.read()
    else:
        with open(args.infile, encoding="utf-8") as f:
            packed = f.read()

    result = asyncio.get_event_loop().run_until_complete(unpack(resolvers, packed))
    md = result.metadata

    # Normalize to InteropCli's metadata vocabulary. For protect_sender envelopes both alg
    # slots are populated; report the (inner) authcrypt algorithms, like the flags do.
    enc = kw = None
    if md.enc_alg_auth is not None:
        kw, enc = md.enc_alg_auth.value.alg, md.enc_alg_auth.value.enc
    elif md.enc_alg_anon is not None:
        kw, enc = md.enc_alg_anon.value.alg, md.enc_alg_anon.value.enc

    output = {
        "message": result.message.as_dict(),
        "metadata": {
            "encrypted": md.encrypted,
            "authenticated": md.authenticated,
            "non_repudiation": md.non_repudiation,
            "anonymous_sender": md.anonymous_sender,
            "enc": enc,
            "kw": kw,
            "sig_alg": md.sign_alg.value if md.sign_alg else None,
            "signer_kid": md.sign_from,
            "sender_kid": md.encrypted_from,
            "recipient_kid": None,  # didcomm-python reports the full target list, not the hit
        },
    }
    write_output(args.out, json.dumps(output, indent=2, default=str))


def cmd_unwrap_forward(args):
    """One mediator hop: unpack the forward envelope addressed to --identity, emit the onward envelope."""
    resolvers = load_resolvers(args.identity)
    if args.infile == "-":
        packed = sys.stdin.read()
    else:
        with open(args.infile, encoding="utf-8") as f:
            packed = f.read()

    result = asyncio.get_event_loop().run_until_complete(
        unpack_forward(resolvers, packed, decrypt_by_all_keys=False)
    )
    print(f"forward hop unwrapped; next: {result.forward_msg.body.next}", file=sys.stderr)
    write_output(args.out, json.dumps(result.forwarded_msg))


# --- FR-IX-06: verify the published didcomm-dotnet vectors with didcomm-python -------------


class StaticDidResolver(DIDResolver):
    """Resolver over the fixture diddocs (spec Appendix B actors), loaded verbatim."""

    def __init__(self, diddocs_dir: str):
        self.docs = {}
        # Sorted, not raw glob order: two fixture documents share an id (bob.json and
        # bob-with-routing.json both describe did:example:bob), so the last one loaded wins.
        # glob order is filesystem order and can differ between machines -- sorting makes the
        # winner deterministic AND identical to the JVM driver's loader (InteropPeer.java
        # staticResolver), so the two legs cannot silently resolve different documents.
        for path in sorted(glob.glob(os.path.join(diddocs_dir, "*.json"))):
            with open(path, encoding="utf-8") as f:
                doc = DIDDoc.deserialize(json.load(f))
            self.docs[doc.id] = doc

    async def resolve(self, did):
        return self.docs.get(str(did))


def cmd_verify_fixture(args):
    """
    FR-IX-06: unpack one published didcomm-dotnet vector with didcomm-python, resolving the
    spec Appendix A/B actors from the fixtures tree, and diff the recovered plaintext against
    the expected payload. The vector is handed to didcomm-python byte-for-byte as published --
    no reshaping -- so a passing row means an external implementation really can read the file
    we ship.
    """
    secrets = []
    for path in args.secrets.split(","):
        with open(path, encoding="utf-8") as f:
            for jwk in json.load(f):
                secrets.append(
                    Secret(
                        kid=jwk["kid"],
                        type=VerificationMethodType.JSON_WEB_KEY_2020,
                        verification_material=VerificationMaterial(
                            format=VerificationMaterialFormat.JWK, value=json.dumps(jwk)
                        ),
                    )
                )
    resolvers = ResolversConfig(
        secrets_resolver=SecretsResolverInMemory(secrets),
        did_resolver=StaticDidResolver(args.diddocs),
    )

    with open(args.packed, encoding="utf-8") as f:
        packed = f.read()
    with open(args.expected, encoding="utf-8") as f:
        expected = json.load(f)

    result = asyncio.get_event_loop().run_until_complete(unpack(resolvers, packed))
    actual = result.message.as_dict()

    failures = [
        f"message.{key}: expected {expected[key]!r}, got {actual.get(key)!r}"
        for key in ("id", "type", "body", "from", "to", "created_time")
        if key in expected and actual.get(key) != expected[key]
    ]
    if failures:
        for failure in failures:
            print(f"VERIFY FAIL: {failure}", file=sys.stderr)
        sys.exit(1)
    print(f"verified {os.path.basename(args.packed)} with didcomm-python", file=sys.stderr)


def expected_metadata(mode: str, enc: str, sig_alg: str = "EdDSA") -> dict:
    """The metadata every implementation must report for a matrix cell (subset-asserted)."""
    # Note: BOTH implementations report authenticated=True when ANY layer binds a sender
    # identity -- a verified signature counts, not just an authcrypt layer (didcomm-python
    # unpack.py `is_signed` branch; didcomm-dotnet EnvelopeReader #23) -- so signed modes
    # expect authenticated=True even without authcrypt.
    if mode == "plaintext":
        return {"encrypted": False, "authenticated": False, "non_repudiation": False}
    if mode == "signed":
        return {"encrypted": False, "authenticated": True, "non_repudiation": True, "sig_alg": sig_alg}
    if mode == "anoncrypt":
        return {
            "encrypted": True, "authenticated": False, "anonymous_sender": True,
            "non_repudiation": False, "enc": enc, "kw": "ECDH-ES+A256KW",
        }
    if mode == "authcrypt":
        return {
            "encrypted": True, "authenticated": True, "anonymous_sender": False,
            "non_repudiation": False, "enc": "A256CBC-HS512", "kw": "ECDH-1PU+A256KW",
        }
    if mode == "anoncrypt-sign":
        return {
            "encrypted": True, "authenticated": True, "anonymous_sender": True,
            "non_repudiation": True, "sig_alg": sig_alg, "enc": enc,
        }
    if mode == "anoncrypt-authcrypt":
        # enc/kw are per-layer and the two implementations report different layers; the
        # flag triple below is the composition's fingerprint, so assert exactly that.
        return {"encrypted": True, "authenticated": True, "anonymous_sender": True}
    raise ValueError(f"unknown mode {mode}")


def cmd_assert(args):
    with open(args.expected, encoding="utf-8") as f:
        expected = json.load(f)
    with open(args.unpacked, encoding="utf-8") as f:
        unpacked = json.load(f)

    failures = []
    actual_message = unpacked["message"]
    for key in ("id", "type", "body", "from", "to", "created_time"):
        if key in expected and actual_message.get(key) != expected[key]:
            failures.append(f"message.{key}: expected {expected[key]!r}, got {actual_message.get(key)!r}")

    actual_md = unpacked["metadata"]
    for key, value in expected_metadata(args.mode, args.enc, args.sig_alg).items():
        if actual_md.get(key) != value:
            failures.append(f"metadata.{key}: expected {value!r}, got {actual_md.get(key)!r}")

    if failures:
        for failure in failures:
            print(f"ASSERT FAIL: {failure}", file=sys.stderr)
        sys.exit(1)


def write_output(path, content: str):
    if path in (None, "-"):
        print(content)
    else:
        with open(path, "w", encoding="utf-8") as f:
            f.write(content)
            if not content.endswith("\n"):
                f.write("\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("mint")
    p.add_argument("--out", required=True)
    p.add_argument("--method", default="peer", choices=("peer", "key"))
    p.add_argument("--key-crv", dest="key_crv", default="X25519", choices=("X25519", "Ed25519"))
    p.add_argument("--ka-crv", dest="ka_crv", default="X25519", choices=("X25519", "P-256"))
    p.add_argument("--ka-count", dest="ka_count", type=int, default=1)
    p.add_argument("--sig-crv", dest="sig_crv", default="Ed25519", choices=("Ed25519", "P-256", "secp256k1"))
    p.add_argument("--route", help="comma-separated mediator identity files -> .S routingKeys")
    p.set_defaults(func=cmd_mint)

    p = sub.add_parser("gen-message")
    p.add_argument("--to", required=True, help="recipient DID(s) or identity file(s), comma-separated")
    p.add_argument("--from", dest="frm", help="sender DID or identity file (omit for anonymous)")
    p.add_argument("--out")
    p.set_defaults(func=cmd_gen_message)

    p = sub.add_parser("pack")
    p.add_argument("--identity", required=True)
    p.add_argument("--to", required=True, help="recipient DID or identity file")
    p.add_argument("--mode", required=True, choices=MODES)
    p.add_argument("--enc", default="A256CBC-HS512", choices=sorted(ANON_ALG_BY_ENC))
    p.add_argument("--message", required=True)
    p.add_argument("--route", help="comma-separated mediator identity files (forward wrap, outermost first)")
    p.add_argument("--out")
    p.set_defaults(func=cmd_pack)

    p = sub.add_parser("unpack")
    p.add_argument("--identity", required=True)
    p.add_argument("--in", dest="infile", default="-")
    p.add_argument("--out")
    p.set_defaults(func=cmd_unpack)

    p = sub.add_parser("unwrap-forward")
    p.add_argument("--identity", required=True, help="the mediator's identity file")
    p.add_argument("--in", dest="infile", default="-")
    p.add_argument("--out")
    p.set_defaults(func=cmd_unwrap_forward)

    p = sub.add_parser("verify-fixture")
    p.add_argument("--packed", required=True, help="a published packed/didcomm-dotnet vector")
    p.add_argument("--diddocs", required=True, help="directory of spec Appendix B diddocs")
    p.add_argument("--secrets", required=True, help="comma-separated Appendix A secrets files")
    p.add_argument("--expected", required=True, help="the expected plaintext payload file")
    p.set_defaults(func=cmd_verify_fixture)

    p = sub.add_parser("assert")
    p.add_argument("--mode", required=True, choices=MODES)
    p.add_argument("--enc", default="A256CBC-HS512")
    p.add_argument("--sig-alg", dest="sig_alg", default="EdDSA")
    p.add_argument("--expected", required=True, help="the payload file the packer was given")
    p.add_argument("--unpacked", required=True, help="the {message, metadata} unpack output")
    p.set_defaults(func=cmd_assert)

    args = parser.parse_args()
    try:
        args.func(args)
    except Exception as ex:  # noqa: BLE001 -- CLI boundary: report and exit nonzero
        print(f"interop_peer: {type(ex).__name__}: {ex}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
