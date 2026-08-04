#!/usr/bin/env python3
"""
SICPA didcomm-python counterpart for the live cross-implementation harness (PRD S13.6,
FR-IX-04/05).

Drives `didcomm` (sicpa-dlab/didcomm-python, pinned in requirements.txt) directly with the
same CLI shape as tools/InteropCli, so run-python-leg.sh can round-trip envelopes both ways
over did:peer:

  mint         mint a did:peer:2 (X25519 keyAgreement + Ed25519 authentication) and write
               {did, secrets: [private JWKs]} -- the identity-file schema InteropCli uses.
  gen-message  emit a fresh C.1-style "lets_do_lunch" payload (the harness feeds the SAME
               payload file to whichever side packs, then diffs it against the unpack).
  pack         pack a payload with didcomm-python (plaintext | signed | anoncrypt | authcrypt
               | anoncrypt-sign | anoncrypt-authcrypt) and print the envelope.
  unpack       unpack an envelope with didcomm-python and print {message, metadata} with the
               metadata keys normalized to InteropCli's vocabulary (enc / kw / sig_alg / ...).
  assert       compare an unpack output ({message, metadata}, from EITHER implementation)
               against the original payload + the metadata the mode/enc cell requires.

did:peer:2 handling is implemented here against the did:peer spec rather than through the
(2022-era) `peerdid` package: keys are decoded from the DID's .Ez/.Vz multibase(multicodec)
segments and named #key-N in order of appearance -- the numbering the current did:peer spec
defines and net-did emits -- so kids agree byte-for-byte across both implementations.
Service (.S) segments are ignored: the harness runs direct, without mediators (forward=False).

stdout carries only the artifact; diagnostics go to stderr. Exit 0 on success, 1 on failure.
"""

import argparse
import asyncio
import base64
import json
import sys
import time
import uuid

import base58
from authlib.jose import OKPKey

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
from didcomm.secrets.secrets_resolver import Secret
from didcomm.secrets.secrets_resolver_in_memory import SecretsResolverInMemory
from didcomm.unpack import unpack

# multicodec varint prefixes for raw public keys (multicodec table: x25519-pub, ed25519-pub)
MULTICODEC_X25519_PUB = bytes([0xEC, 0x01])
MULTICODEC_ED25519_PUB = bytes([0xED, 0x01])

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


# --- did:peer:2 (spec-conformant, hand-rolled: see module docstring) -----------------------


def encode_key_segment(purpose: str, multicodec_prefix: bytes, raw_public_key: bytes) -> str:
    """One did:peer:2 key segment: '.<purpose>z<base58btc(multicodec + raw key)>'."""
    return "." + purpose + "z" + base58.b58encode(multicodec_prefix + raw_public_key).decode("ascii")


def resolve_peer_did_2(did: str) -> DIDDoc:
    """
    Decode a did:peer:2 into a didcomm-python DIDDoc. Keys are named #key-N in order of
    appearance across ALL key segments (the did:peer spec's transform); each becomes a
    JsonWebKey2020 verification method so both OKP curves flow through one code path.
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
            continue  # service segments are irrelevant here: the harness runs without mediators
        if purpose not in ("E", "V"):
            raise ValueError(f"unsupported did:peer:2 purpose '{purpose}' in {did}")
        if not encoded.startswith("z"):
            raise ValueError(f"unsupported multibase '{encoded[:1]}' in {did} (base58btc 'z' expected)")

        decoded = base58.b58decode(encoded[1:])
        prefix, raw = decoded[:2], decoded[2:]
        if prefix == MULTICODEC_X25519_PUB:
            crv = "X25519"
        elif prefix == MULTICODEC_ED25519_PUB:
            crv = "Ed25519"
        else:
            raise ValueError(f"unsupported multicodec prefix {prefix.hex()} in {did}")

        key_index += 1
        kid = f"{did}#key-{key_index}"
        verification_methods.append(
            {
                "id": kid,
                "type": VerificationMethodType.JSON_WEB_KEY_2020,
                "controller": did,
                "publicKeyJwk": {"kty": "OKP", "crv": crv, "x": b64url_no_pad(raw)},
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


class PeerDID2Resolver(DIDResolver):
    async def resolve(self, did):
        return resolve_peer_did_2(str(did))


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
        secrets_resolver=SecretsResolverInMemory(secrets), did_resolver=PeerDID2Resolver()
    )


def load_did(did_or_identity_path: str) -> str:
    """Accept a DID directly or the counterpart's identity file (mirrors InteropCli --to)."""
    if did_or_identity_path.startswith("did:"):
        return did_or_identity_path
    with open(did_or_identity_path, encoding="utf-8") as f:
        return json.load(f)["did"]


# --- subcommands ---------------------------------------------------------------------------


def cmd_mint(args):
    kx = OKPKey.generate_key("X25519", is_private=True).as_dict(is_private=True)
    auth = OKPKey.generate_key("Ed25519", is_private=True).as_dict(is_private=True)

    did = (
        "did:peer:2"
        + encode_key_segment("E", MULTICODEC_X25519_PUB, b64url_decode(kx["x"]))
        + encode_key_segment("V", MULTICODEC_ED25519_PUB, b64url_decode(auth["x"]))
    )
    kx["kid"] = f"{did}#key-1"
    auth["kid"] = f"{did}#key-2"

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump({"did": did, "secrets": [kx, auth]}, f, indent=2)
        f.write("\n")
    print(f"minted {did}", file=sys.stderr)
    print(did)


def cmd_gen_message(args):
    message = {
        "id": str(uuid.uuid4()),
        "type": "http://example.com/protocols/lets_do_lunch/1.0/proposal",
        "to": [load_did(args.to)],
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
        return result.packed_msg

    write_output(args.out, asyncio.get_event_loop().run_until_complete(run()))


def normalize_flattened_jws(packed: str) -> str:
    """
    KNOWN DEVIATION (documented in tools/interop-live/README.md): didcomm-python 0.3.2 only
    parses the General JWS serialization (`validate_jws` requires a "signatures" array), but
    the DIDComm v2.1 spec says "Either the General or Flattened form of a JWS is valid.
    Message recipients MUST be able to process both forms." didcomm-dotnet emits the
    spec-blessed Flattened form (PRD FR-SIG-02: "Flattened is sufficient"), so before handing
    a STANDALONE signed envelope to didcomm-python we reshape Flattened -> General -- a
    lossless RFC 7515 re-serialization (payload, protected header, and signature bytes are
    byte-identical; all verification below is still didcomm-python's). The same gap makes the
    outbound anoncrypt(sign) cell N/A: the inner Flattened JWS sits inside the ciphertext
    where no transport-level normalization can reach it.
    """
    try:
        env = json.loads(packed)
    except ValueError:
        return packed
    if not (isinstance(env, dict) and "payload" in env and "signature" in env and "signatures" not in env):
        return packed

    signature = {"signature": env["signature"]}
    if "protected" in env:
        signature["protected"] = env["protected"]
    if "header" in env:
        signature["header"] = env["header"]
    print("note: normalized spec-valid Flattened JWS to General for didcomm-python", file=sys.stderr)
    return json.dumps({"payload": env["payload"], "signatures": [signature]})


def cmd_unpack(args):
    resolvers = load_resolvers(args.identity)
    if args.infile == "-":
        packed = sys.stdin.read()
    else:
        with open(args.infile, encoding="utf-8") as f:
            packed = f.read()
    packed = normalize_flattened_jws(packed)

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


def expected_metadata(mode: str, enc: str) -> dict:
    """The metadata every implementation must report for a matrix cell (subset-asserted)."""
    # Note: BOTH implementations report authenticated=True when ANY layer binds a sender
    # identity -- a verified signature counts, not just an authcrypt layer (didcomm-python
    # unpack.py `is_signed` branch; didcomm-dotnet EnvelopeReader #23) -- so signed modes
    # expect authenticated=True even without authcrypt.
    if mode == "plaintext":
        return {"encrypted": False, "authenticated": False, "non_repudiation": False}
    if mode == "signed":
        return {"encrypted": False, "authenticated": True, "non_repudiation": True, "sig_alg": "EdDSA"}
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
            "non_repudiation": True, "sig_alg": "EdDSA", "enc": enc,
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
    for key, value in expected_metadata(args.mode, args.enc).items():
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
    p.set_defaults(func=cmd_mint)

    p = sub.add_parser("gen-message")
    p.add_argument("--to", required=True, help="recipient DID or identity file")
    p.add_argument("--from", dest="frm", help="sender DID or identity file (omit for anonymous)")
    p.add_argument("--out")
    p.set_defaults(func=cmd_gen_message)

    p = sub.add_parser("pack")
    p.add_argument("--identity", required=True)
    p.add_argument("--to", required=True, help="recipient DID or identity file")
    p.add_argument("--mode", required=True, choices=MODES)
    p.add_argument("--enc", default="A256CBC-HS512", choices=sorted(ANON_ALG_BY_ENC))
    p.add_argument("--message", required=True)
    p.add_argument("--out")
    p.set_defaults(func=cmd_pack)

    p = sub.add_parser("unpack")
    p.add_argument("--identity", required=True)
    p.add_argument("--in", dest="infile", default="-")
    p.add_argument("--out")
    p.set_defaults(func=cmd_unpack)

    p = sub.add_parser("assert")
    p.add_argument("--mode", required=True, choices=MODES)
    p.add_argument("--enc", default="A256CBC-HS512")
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
