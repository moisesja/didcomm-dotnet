#!/usr/bin/env python3
"""
didcomm-python counterpart of the mediator leg (tools/interop-live/run-mediator-leg.sh, #98).

The python side of every cell that needs one: a sender that wraps forwards for a mediator, and
a recipient that enrols with a mediator (Coordinate Mediation 2.0) and picks its messages up
(Message Pickup 3.0) by itself, with messages built here by hand and authcrypted with
didcomm-python 0.3.2. The DID, identity-file and unpack helpers come from
../../python/interop_peer.py (imported, never changed), so kids, identity files and the
{message, metadata} unpack schema are the same as in the python leg.

  okp-keys     write a fresh per-run X25519 + Ed25519 key pair for the Identus container as an
               env file (KEY_AGREEMENT_D/X, KEY_AUTHENTICATION_D/X; base64url, no padding).
  did-check    check a mediator's did:peer:2 against those keys and the endpoints it was
               started with: one E key = KEY_AGREEMENT_X, one V key = KEY_AUTHENTICATION_X,
               services after the keys, service 1 = the http endpoint, service 2 = the ws one.
  free-port    print a TCP port that is free on 127.0.0.1 right now.
  mint-routed  mint a did:peer:2 whose DIDCommMessaging service `uri` is a mediator's DID (the
               shape DidCommServiceBuilder.ForMediation builds), for a python recipient.
  forward      authcrypt a message for --to and wrap it in one forward per --via mediator
               (didcomm-python wrap_in_forward, first mediator outermost). The wrapping is
               explicit because didcomm-python 0.3.2 does not wrap on its own for a service
               whose `uri` is a DID without routingKeys.
  post         POST one envelope with its exact DIDComm media type; check the HTTP status.
  post-many    POST several envelopes at once (one thread each, released together).
  cm2-enrol    Coordinate Mediation 2.0 by hand: mediate-request, keylist-update (add), and
               keylist-query, each checked (type, thid, from, to, results).
  pickup3      Message Pickup 3.0 by hand: status-request until the expected count is
               queued, delivery-request, unpack each attachment, messages-received, status 0.

Every hand-built request carries `id`, `type`, `from`, exactly one `to`, `created_time`
(seconds) and `return_route: all`, except where a cm2-enrol --variant leaves one out on
purpose. Every HTTP request goes through urllib with an explicit ProxyHandler: empty (no
proxy, whatever the environment says) unless --proxy names one.

stdout carries only the artifact (a DID, a port, or a one-line JSON result); diagnostics go
to stderr. Exit 0 on success, 1 on a failed check or error, 2 on usage errors.
"""

import argparse
import asyncio
import base64
import binascii
import json
import os
import socket
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "..", "python")))

import base58  # noqa: E402  (pinned in python/requirements.txt)
from authlib.jose import OKPKey  # noqa: E402

import interop_peer as ip  # noqa: E402  (../../python/interop_peer.py, unchanged)
from didcomm.common.algorithms import AuthCryptAlg  # noqa: E402
from didcomm.pack_encrypted import PackEncryptedConfig, pack_encrypted  # noqa: E402
from didcomm.protocols.routing.forward import wrap_in_forward  # noqa: E402
from didcomm.unpack import unpack  # noqa: E402

CM2 = "https://didcomm.org/coordinate-mediation/2.0/"
PICKUP3 = "https://didcomm.org/messagepickup/3.0/"

MEDIA_TYPES = {
    "encrypted": "application/didcomm-encrypted+json",
    "signed": "application/didcomm-signed+json",
    "plain": "application/didcomm-plain+json",
}

# One event loop for the whole process. interop_peer's helpers call
# asyncio.get_event_loop(), which then returns this loop (asyncio.run would close its loop and
# leave none behind for them).
LOOP = asyncio.new_event_loop()
asyncio.set_event_loop(LOOP)


def run(coro):
    return LOOP.run_until_complete(coro)


def log(text: str):
    print(f"mediator_peer: {text}", file=sys.stderr)


class CheckFailed(Exception):
    """A check of the cell failed (exit 1, message on stderr)."""


def write_json(path, value):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(value, f, indent=2, sort_keys=False, default=str)
        f.write("\n")


def read_text(path_or_value: str) -> str:
    """A value given inline, or @file for a file's (stripped) contents."""
    if path_or_value.startswith("@"):
        with open(path_or_value[1:], encoding="utf-8") as f:
            return f.read().strip()
    return path_or_value


def did_of(entry: str) -> str:
    """A DID, @file holding one, or an identity file ({did, secrets})."""
    return ip.load_did(read_text(entry))


# --- HTTP ----------------------------------------------------------------------------------


def opener_for(proxy):
    """urllib opener with an explicit ProxyHandler: none, or exactly the one given."""
    proxies = {"http": proxy, "https": proxy} if proxy else {}
    return urllib.request.build_opener(urllib.request.ProxyHandler(proxies))


def media_type_of(body: bytes) -> str:
    envelope = json.loads(body)
    if "ciphertext" in envelope:
        return MEDIA_TYPES["encrypted"]
    if "signatures" in envelope or "payload" in envelope:
        return MEDIA_TYPES["signed"]
    return MEDIA_TYPES["plain"]


def http_post(url: str, body: bytes, proxy=None, timeout=30.0, content_type=None) -> dict:
    """POST and return {status, elapsed_ms, content_type, date, body} (never raises on HTTP status)."""
    request = urllib.request.Request(
        url,
        data=body,
        method="POST",
        headers={"Content-Type": content_type or media_type_of(body)},
    )
    started = time.monotonic()
    try:
        with opener_for(proxy).open(request, timeout=timeout) as response:
            status, headers, payload = response.status, response.headers, response.read()
    except urllib.error.HTTPError as error:
        status, headers, payload = error.code, error.headers, error.read()
    elapsed_ms = int((time.monotonic() - started) * 1000)
    return {
        "status": status,
        "elapsed_ms": elapsed_ms,
        "content_type": headers.get("Content-Type") if headers else None,
        "date": headers.get("Date") if headers else None,
        "body": payload,
    }


def parse_statuses(text: str) -> set:
    return {int(part) for part in text.split(",") if part.strip()}


# --- okp-keys / did-check / free-port / mint-routed ----------------------------------------


def cmd_okp_keys(args):
    agreement = OKPKey.generate_key("X25519", is_private=True).as_dict(is_private=True)
    authentication = OKPKey.generate_key("Ed25519", is_private=True).as_dict(is_private=True)
    lines = [
        f"KEY_AGREEMENT_D={agreement['d']}",
        f"KEY_AGREEMENT_X={agreement['x']}",
        f"KEY_AUTHENTICATION_D={authentication['d']}",
        f"KEY_AUTHENTICATION_X={authentication['x']}",
    ]
    fd = os.open(args.out, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    log(f"per-run mediator keys written to {args.out}")


def read_env_file(path: str) -> dict:
    values = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                values[key] = value
    return values


def b64url_json(segment: str) -> dict:
    return json.loads(ip.b64url_decode(segment).decode("utf-8"))


def service_uri(service: dict):
    endpoint = service.get("s")
    if isinstance(endpoint, dict):
        return endpoint.get("uri")
    return endpoint


def service_accept(service: dict):
    endpoint = service.get("s")
    if isinstance(endpoint, dict):
        return endpoint.get("a", endpoint.get("accept"))
    return service.get("a", service.get("accept"))


def cmd_did_check(args):
    did = did_of(args.did)
    keys = read_env_file(args.keys)
    failures = []
    if not did.startswith("did:peer:2."):
        raise CheckFailed(f"not a did:peer:2: {did}")

    elements = [segment for segment in did[len("did:peer:2"):].split(".") if segment]
    agreement, authentication, services, order = [], [], [], []
    for segment in elements:
        purpose, encoded = segment[0], segment[1:]
        order.append(purpose)
        if purpose == "S":
            services.append(b64url_json(encoded))
            continue
        if purpose not in ("E", "V") or not encoded.startswith("z"):
            failures.append(f"unexpected element '{segment[:12]}…'")
            continue
        jwk = ip.decode_multicodec_key(base58.b58decode(encoded[1:]), did)
        (agreement if purpose == "E" else authentication).append(jwk)

    if order != sorted(order, key=lambda p: p == "S"):
        failures.append(f"services must come after keys (element order {''.join(order)})")
    if [k.get("crv") for k in agreement] != ["X25519"]:
        failures.append(f"expected one X25519 agreement key, got {[k.get('crv') for k in agreement]}")
    elif agreement[0]["x"] != keys.get("KEY_AGREEMENT_X"):
        failures.append("agreement key is not KEY_AGREEMENT_X of the per-run keys")
    if [k.get("crv") for k in authentication] != ["Ed25519"]:
        failures.append(f"expected one Ed25519 authentication key, got {[k.get('crv') for k in authentication]}")
    elif authentication[0]["x"] != keys.get("KEY_AUTHENTICATION_X"):
        failures.append("authentication key is not KEY_AUTHENTICATION_X of the per-run keys")

    expected = [args.expect_http] + ([args.expect_ws] if args.expect_ws else [])
    uris = [service_uri(service) for service in services]
    if uris[: len(expected)] != expected:
        failures.append(f"services advertise {uris}, expected {expected} first")
    for index, service in enumerate(services):
        if service.get("t") not in ("dm", "DIDCommMessaging"):
            failures.append(f"service {index + 1} type is {service.get('t')!r}, not DIDCommMessaging")
        accept = service_accept(service)
        if accept is not None and "didcomm/v2" not in accept:
            failures.append(f"service {index + 1} does not accept didcomm/v2 ({accept})")

    report = {"did": did, "elements": "".join(order), "services": services, "ok": not failures}
    if args.out:
        write_json(args.out, report)
    if failures:
        raise CheckFailed("did-check: " + "; ".join(failures))
    print(json.dumps({"ok": True, "elements": "".join(order), "service_uris": uris}))


def cmd_free_port(args):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        print(s.getsockname()[1])


def cmd_mint_routed(args):
    """did:peer:2 with X25519 + Ed25519 and one service whose uri is the mediator's DID."""
    mediator_did = did_of(args.service_uri)
    agreement = ip.generate_private_jwk("X25519")
    authentication = ip.generate_private_jwk("Ed25519")
    service = {"t": "dm", "s": {"uri": mediator_did, "accept": ["didcomm/v2"]}}
    did = (
        "did:peer:2"
        + ip.encode_key_segment("E", ip.multicodec_prefix(agreement), ip.multicodec_bytes(agreement))
        + ip.encode_key_segment("V", ip.multicodec_prefix(authentication), ip.multicodec_bytes(authentication))
        + ".S"
        + ip.b64url_no_pad(json.dumps(service, separators=(",", ":")).encode("utf-8"))
    )
    secrets = [agreement, authentication]
    for index, jwk in enumerate(secrets, start=1):
        jwk["kid"] = f"{did}#key-{index}"
    write_json(args.out, {"did": did, "secrets": secrets})
    log(f"minted {did} (service uri = {mediator_did})")
    print(did)


# --- forward -------------------------------------------------------------------------------


def auth_config() -> PackEncryptedConfig:
    # forward=False: the wrapping is always explicit (see the module docstring).
    return PackEncryptedConfig(enc_alg_auth=AuthCryptAlg.A256CBC_HS512_ECDH_1PU_A256KW, forward=False)


def cmd_forward(args):
    resolvers = ip.load_resolvers(args.identity)
    own_did = ip.load_did(args.identity)
    to = did_of(args.to)
    encrypt_to = did_of(args.encrypt_to) if args.encrypt_to else to
    via = [did_of(entry) for entry in args.via.split(",")]
    with open(args.message, encoding="utf-8") as f:
        message = json.load(f)

    async def build():
        inner = await pack_encrypted(resolvers, message, to=encrypt_to, frm=own_did, pack_config=auth_config())
        inner_json = json.loads(inner.packed_msg)
        wrapped = await wrap_in_forward(resolvers, inner_json, to=to, routing_keys=via)
        return inner.packed_msg, wrapped

    inner_packed, wrapped = run(build())
    ip.write_output(args.out, json.dumps(wrapped.msg_encrypted.msg))
    if args.inner_out:
        ip.write_output(args.inner_out, inner_packed)
    if args.meta_out:
        write_json(args.meta_out, {
            "outer_forward_id": wrapped.msg.id,
            "next": to,
            "via": via,
            "inner_message_id": message.get("id"),
        })
    log(f"forward {wrapped.msg.id}: next {to}, via {', '.join(via)}")


# --- post / post-many ----------------------------------------------------------------------


def post_one(url, path, proxy, timeout, body_out=None):
    with open(path, "rb") as f:
        body = f.read().strip()
    result = http_post(url, body, proxy=proxy, timeout=timeout)
    payload = result.pop("body")
    result["in"] = path
    result["body_bytes"] = len(payload)
    result["body_file"] = None
    if body_out and payload:
        with open(body_out, "wb") as f:
            f.write(payload)
        result["body_file"] = body_out
    result["body_head"] = payload[:300].decode("utf-8", "replace") if payload else ""
    return result


def cmd_post(args):
    expected = parse_statuses(args.expect_status)
    result = post_one(args.url, args.infile, args.proxy, args.timeout, args.body_out)
    if args.meta_out:
        write_json(args.meta_out, result)
    log(f"POST {args.url} → {result['status']} in {result['elapsed_ms']} ms")
    if result["status"] not in expected:
        raise CheckFailed(
            f"POST {args.url}: HTTP {result['status']}, expected {sorted(expected)}; "
            f"body: {result['body_head']!r}"
        )


def cmd_post_many(args):
    expected = parse_statuses(args.expect_status)
    paths = [p for p in args.infile.split(",") if p]
    if args.in_dir:
        paths += sorted(
            os.path.join(args.in_dir, name) for name in os.listdir(args.in_dir) if name.endswith(".json")
        )
    if not paths:
        raise CheckFailed("post-many: no envelopes given")
    results = [None] * len(paths)
    gate = threading.Semaphore(max(1, args.concurrency))
    start = threading.Event()

    def worker(index, path):
        start.wait()
        with gate:
            try:
                results[index] = post_one(args.url, path, args.proxy, args.timeout)
            except Exception as ex:  # noqa: BLE001 -- recorded, checked below
                results[index] = {"in": path, "status": None, "error": f"{type(ex).__name__}: {ex}"}

    threads = [threading.Thread(target=worker, args=(i, p), daemon=True) for i, p in enumerate(paths)]
    for thread in threads:
        thread.start()
    started = time.monotonic()
    start.set()
    for thread in threads:
        thread.join()
    summary = {
        "count": len(paths),
        "concurrency": args.concurrency,
        "wall_ms": int((time.monotonic() - started) * 1000),
        "statuses": [r["status"] for r in results],
        "posts": results,
    }
    if args.meta_out:
        write_json(args.meta_out, summary)
    log(f"POST ×{len(paths)} (concurrency {args.concurrency}) → {summary['statuses']} in {summary['wall_ms']} ms")
    wrong = [r for r in results if r["status"] not in expected]
    if wrong:
        raise CheckFailed(
            f"post-many: {len(wrong)} of {len(paths)} answered outside {sorted(expected)}: "
            + "; ".join(f"{os.path.basename(r['in'])} → {r.get('status')} {r.get('error', '')}" for r in wrong[:3])
        )


# --- hand-built protocol messages ----------------------------------------------------------

OMIT = object()


def build_request(type_, frm, to, body=OMIT, created_time=True, return_route=True):
    message = {"id": str(uuid.uuid4()), "type": type_, "from": frm, "to": [to]}
    if created_time:
        message["created_time"] = int(time.time())
    if return_route:
        message["return_route"] = "all"
    if body is not OMIT:
        message["body"] = body
    return message


class Exchange:
    """One identity talking to one mediator endpoint over HTTP, authcrypted both ways."""

    def __init__(self, identity_path, mediator_did, url, proxy, timeout):
        self.resolvers = ip.load_resolvers(identity_path)
        self.me = ip.load_did(identity_path)
        self.mediator = mediator_did
        self.url = url
        self.proxy = proxy
        self.timeout = timeout
        self.steps = []

    def send(self, name, message, expect_reply=True):
        """Authcrypt, POST, and (when a reply is expected) unpack the reply. Returns the step."""
        packed = run(pack_encrypted(self.resolvers, message, to=self.mediator, frm=self.me, pack_config=auth_config()))
        result = http_post(self.url, packed.packed_msg.encode("utf-8"), proxy=self.proxy, timeout=self.timeout)
        payload = result.pop("body")
        step = {
            "name": name,
            "request_id": message["id"],
            "request_type": message["type"],
            "request_keys": sorted(message),
            "status": result["status"],
            "elapsed_ms": result["elapsed_ms"],
            "content_type": result["content_type"],
            "date": result["date"],
            "body_bytes": len(payload),
            "reply": None,
            "ok": True,
            "detail": "",
        }
        self.steps.append(step)
        if not expect_reply:
            if result["status"] != 202 or payload:
                step["ok"] = False
                step["detail"] = f"expected HTTP 202 with no body, got {result['status']} ({len(payload)} bytes)"
            return step
        if result["status"] != 200 or not payload:
            step["ok"] = False
            step["detail"] = (
                f"expected HTTP 200 with a reply, got {result['status']} "
                f"({len(payload)} bytes: {payload[:200].decode('utf-8', 'replace')!r})"
            )
            return step
        try:
            unpacked = run(unpack(self.resolvers, payload.decode("utf-8")))
        except Exception as ex:  # noqa: BLE001 -- recorded as the step's failure
            step["ok"] = False
            step["detail"] = f"reply did not unpack: {type(ex).__name__}: {ex}"
            return step
        reply = unpacked.message.as_dict()
        md = unpacked.metadata
        step["reply"] = reply
        step["reply_metadata"] = {
            "encrypted": md.encrypted,
            "authenticated": md.authenticated,
            "anonymous_sender": md.anonymous_sender,
            "sender_kid": md.encrypted_from,
        }
        step["reply_shape"] = {
            "type": reply.get("type"),
            "thid": reply.get("thid"),
            "thid_is_request_id": reply.get("thid") == message["id"],
            "from": reply.get("from"),
            "to": reply.get("to"),
            "created_time_present": "created_time" in reply,
            "top_level_keys_after_unpack": sorted(reply),  # didcomm-python's as_dict adds `typ`
        }
        return step

    def expect(self, step, ok, detail):
        if step["ok"] and not ok:
            step["ok"] = False
            step["detail"] = detail
        return ok

    def check_reply(self, step, expected_type, require_created_time):
        """Shared checks on a protocol reply: exact type, thid, from, to, created_time."""
        if not step["ok"]:
            return False
        reply = step["reply"]
        return (
            self.expect(step, reply.get("type") == expected_type,
                        f"reply type {reply.get('type')!r}, expected {expected_type!r}"
                        + problem_text(reply))
            and self.expect(step, reply.get("thid") == step["request_id"],
                            f"reply thid {reply.get('thid')!r} is not the request id {step['request_id']!r}")
            and self.expect(step, reply.get("from") == self.mediator,
                            f"reply from {reply.get('from')!r} is not the mediator")
            and self.expect(step, reply.get("to") == [self.me],
                            f"reply to {reply.get('to')!r} is not [{self.me}]")
            and self.expect(step, (not require_created_time) or isinstance(reply.get("created_time"), int),
                            "reply has no integer created_time")
        )


def problem_text(reply: dict) -> str:
    if str(reply.get("type", "")).endswith("/problem-report"):
        body = reply.get("body") or {}
        return f" (problem report {body.get('code')!r}: {body.get('comment')!r})"
    return ""


def finish(command, args, exchange, extra, failure=None):
    failed = [s for s in exchange.steps if not s["ok"]]
    result = {"command": command, "ok": not failed and failure is None, "steps": exchange.steps}
    result.update(extra)
    if not result["ok"]:
        result["error"] = failure or failed[0]["detail"]
    if args.out:
        write_json(args.out, result)
    for step in exchange.steps:
        log(f"{command} {step['name']}: HTTP {step['status']} in {step['elapsed_ms']} ms"
            + ("" if step["ok"] else f" — FAIL: {step['detail']}"))
    if not result["ok"]:
        raise CheckFailed(f"{command}: " + (failure or failed[0]["detail"]))
    print(json.dumps({"command": command, "ok": True}))


# --- cm2-enrol -----------------------------------------------------------------------------


def cmd_cm2_enrol(args):
    mediator = did_of(args.mediator)
    expected_routing_did = did_of(args.expect_routing_did) if args.expect_routing_did else mediator
    register = [did_of(entry) for entry in args.register.split(",") if entry]
    exchange = Exchange(args.identity, mediator, args.url, args.proxy, args.timeout)
    me = exchange.me
    extra = {"variant": args.variant, "mediator": mediator, "registered": register}

    # 1. mediate-request → mediate-grant
    request = build_request(
        CM2 + "mediate-request", me, mediator,
        body=OMIT if args.variant == "spec-example" else {},
        created_time=args.variant != "no-created-time",
    )
    step = exchange.send("mediate-request", request)
    if exchange.check_reply(step, CM2 + "mediate-grant", args.require_created_time):
        routing_did = (step["reply"].get("body") or {}).get("routing_did")
        extra["routing_did_kind"] = type(routing_did).__name__
        extra["routing_did"] = routing_did
        exchange.expect(step, isinstance(routing_did, str),
                        f"routing_did is a {type(routing_did).__name__}, not a string (CM 2.0)")
        exchange.expect(step, routing_did == expected_routing_did,
                        f"routing_did {routing_did!r} is not {expected_routing_did!r}")
    if not step["ok"]:
        return finish("cm2-enrol", args, exchange, extra)

    # 2. keylist-update (add) → keylist-update-response, or 202 when return_route is left out
    no_route = args.variant == "no-return-route-on-update"
    update = build_request(
        CM2 + "keylist-update", me, mediator,
        body={"updates": [{"recipient_did": did, "action": "add"} for did in register]},
        return_route=not no_route,
    )
    step = exchange.send("keylist-update", update, expect_reply=not no_route)
    if not no_route and exchange.check_reply(step, CM2 + "keylist-update-response", args.require_created_time):
        updated = (step["reply"].get("body") or {}).get("updated")
        got = sorted(
            (u.get("recipient_did"), u.get("action"), u.get("result")) for u in (updated or []) if isinstance(u, dict)
        )
        extra["updated"] = [{"recipient_did": d, "action": a, "result": r} for d, a, r in got]
        if args.expect_update_result != "any":
            want = sorted((did, "add", args.expect_update_result) for did in register)
            exchange.expect(step, got == want, f"updated {got}, expected {want}")
    if not step["ok"]:
        return finish("cm2-enrol", args, exchange, extra)

    # 3. keylist-query → keylist naming every registered DID
    query = build_request(CM2 + "keylist-query", me, mediator, body={})
    step = exchange.send("keylist-query", query)
    if exchange.check_reply(step, CM2 + "keylist", args.require_created_time):
        body = step["reply"].get("body") or {}
        keys = [k.get("recipient_did") for k in body.get("keys") or [] if isinstance(k, dict)]
        extra["keylist"] = keys
        extra["pagination"] = body.get("pagination")
        missing = [did for did in register if did not in keys]
        exchange.expect(step, not missing, f"keylist misses {missing} (has {keys})")
        if args.require_pagination:
            pagination = body.get("pagination")
            exchange.expect(
                step,
                isinstance(pagination, dict) and all(k in pagination for k in ("count", "offset", "remaining")),
                f"keylist pagination {pagination!r} lacks count/offset/remaining",
            )
    finish("cm2-enrol", args, exchange, extra)


# --- pickup3 -------------------------------------------------------------------------------


def classify_base64(value: str) -> dict:
    """Alphabet and padding of a delivered attachment, and which strict decoders accept it."""
    url_chars = any(c in value for c in "-_")
    std_chars = any(c in value for c in "+/")
    # ASCII JSON whose strings are base64url never encodes a sextet of 62 or 63, so neither
    # alphabet's two distinguishing characters may appear: then the alphabet cannot be told.
    info = {
        "alphabet": "mixed" if url_chars and std_chars else "url" if url_chars else "std" if std_chars
        else "indistinguishable (no sextet 62/63 in this content)",
        "padded": value.endswith("="),
        "length_mod_4": len(value) % 4,
    }
    try:
        base64.b64decode(value, validate=True)
        info["strict_std_padded_accepts"] = True
    except (binascii.Error, ValueError):
        info["strict_std_padded_accepts"] = False
    try:
        if "+" in value or "/" in value or "=" in value:
            raise ValueError("not unpadded base64url")
        base64.b64decode(value.replace("-", "+").replace("_", "/") + "=" * (-len(value) % 4), validate=True)
        info["strict_url_unpadded_accepts"] = True
    except (binascii.Error, ValueError):
        info["strict_url_unpadded_accepts"] = False
    return info


def attachment_bytes(attachment: dict) -> tuple:
    data = attachment.get("data") or {}
    if "base64" in data:
        value = data["base64"]
        info = {"form": "base64", **classify_base64(value)}
        normalized = value.replace("-", "+").replace("_", "/").rstrip("=")
        return base64.b64decode(normalized + "=" * (-len(normalized) % 4), validate=True), info
    if "json" in data:
        return json.dumps(data["json"]).encode("utf-8"), {"form": "json"}
    raise CheckFailed(f"attachment {attachment.get('id')!r} carries neither base64 nor json data: {sorted(data)}")


def cmd_pickup3(args):
    mediator = did_of(args.mediator)
    recipient = ip.load_did(args.recipient)
    exchange = Exchange(args.identity, mediator, args.url, args.proxy, args.timeout)
    me = exchange.me
    os.makedirs(args.out_dir, exist_ok=True)
    extra = {"mediator": mediator, "recipient": recipient, "items": [], "status_polls": 0}

    def status_request(name):
        request = build_request(PICKUP3 + "status-request", me, mediator, body={"recipient_did": recipient})
        step = exchange.send(name, request)
        count = None
        if exchange.check_reply(step, PICKUP3 + "status", False):
            count = (step["reply"].get("body") or {}).get("message_count")
            exchange.expect(step, isinstance(count, int), f"status message_count is {count!r}")
        return step, count

    # 1. status-request until at least --expect-count messages are queued (the exact record
    #    the delivery step reads); --wait-timeout is a hang guard on the monotonic clock only.
    deadline = time.monotonic() + args.wait_timeout
    while True:
        step, count = status_request("status-request")
        extra["status_polls"] += 1
        if not step["ok"]:
            return finish("pickup3", args, exchange, extra)
        if count >= args.expect_count:
            break
        if time.monotonic() >= deadline:
            return finish("pickup3", args, exchange, extra,
                          f"status counted {count} after {args.wait_timeout} s, expected {args.expect_count}")
        exchange.steps.pop()  # only the last poll is kept in the record; status_polls counts them
        time.sleep(0.2)  # pacing inside the condition loop
    extra["queued_before_delivery"] = count

    # 2. delivery-request → delivery with exactly --expect-count attachments
    request = build_request(
        PICKUP3 + "delivery-request", me, mediator, body={"limit": args.limit, "recipient_did": recipient}
    )
    step = exchange.send("delivery-request", request)
    if not exchange.check_reply(step, PICKUP3 + "delivery", False):
        return finish("pickup3", args, exchange, extra)
    reply = step["reply"]
    attachments = reply.get("attachments") or []
    extra["delivery_shape"] = {
        "thid": reply.get("thid"),
        "please_ack": reply.get("please_ack"),
        "return_route": reply.get("return_route"),
        "body": reply.get("body"),
        "attachment_count": len(attachments),
    }
    step["reply"] = {k: v for k, v in reply.items() if k != "attachments"}  # bulky; items below
    if not exchange.expect(step, len(attachments) == args.expect_count,
                           f"delivery carried {len(attachments)} attachment(s), expected {args.expect_count}"):
        return finish("pickup3", args, exchange, extra)

    ids = []
    for index, attachment in enumerate(attachments, start=1):
        raw, info = attachment_bytes(attachment)
        envelope_path = os.path.join(args.out_dir, f"item-{index}.envelope.json")
        with open(envelope_path, "wb") as f:
            f.write(raw)
        unpacked_path = os.path.join(args.out_dir, f"item-{index}.unpacked.json")
        try:
            ip.cmd_unpack(argparse.Namespace(identity=args.recipient, infile=envelope_path, out=unpacked_path))
        except Exception as ex:  # noqa: BLE001 -- the cell's failure
            return finish("pickup3", args, exchange, extra,
                          f"delivered item {index} ({attachment.get('id')!r}) did not unpack: {type(ex).__name__}: {ex}")
        ids.append(attachment.get("id"))
        extra["items"].append({"id": attachment.get("id"), "envelope": envelope_path,
                               "unpacked": unpacked_path, "encoding": info})

    # 3. messages-received → status (count 0), or no answer (202) when the mediator sends none
    ack = build_request(PICKUP3 + "messages-received", me, mediator, body={"message_id_list": ids})
    if args.expect_ack_status == "yes":
        step = exchange.send("messages-received", ack)
        if exchange.check_reply(step, PICKUP3 + "status", False):
            count = (step["reply"].get("body") or {}).get("message_count")
            exchange.expect(step, count == 0, f"status after messages-received counts {count!r}, expected 0")
    else:
        step = exchange.send("messages-received", ack, expect_reply=False)
    extra["ack"] = {"status": step["status"], "elapsed_ms": step["elapsed_ms"],
                    "answered_with_status": step.get("reply") is not None}
    if not step["ok"]:
        return finish("pickup3", args, exchange, extra)

    # 4. a separate status-request: nothing left for the recipient
    step, count = status_request("final status-request")
    if step["ok"]:
        exchange.expect(step, count == 0, f"final status counts {count!r}, expected 0")
    extra["final_count"] = count
    finish("pickup3", args, exchange, extra)


# --- CLI -----------------------------------------------------------------------------------


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("okp-keys")
    p.add_argument("--out", required=True, help="env file for docker run --env-file (mode 0600)")
    p.set_defaults(func=cmd_okp_keys)

    p = sub.add_parser("did-check")
    p.add_argument("--did", required=True, help="the DID, or @file holding it")
    p.add_argument("--keys", required=True, help="the env file okp-keys wrote")
    p.add_argument("--expect-http", dest="expect_http", required=True)
    p.add_argument("--expect-ws", dest="expect_ws")
    p.add_argument("--out", help="write the decoded report here")
    p.set_defaults(func=cmd_did_check)

    p = sub.add_parser("free-port")
    p.set_defaults(func=cmd_free_port)

    p = sub.add_parser("mint-routed")
    p.add_argument("--service-uri", dest="service_uri", required=True, help="mediator DID (or @file / identity file)")
    p.add_argument("--out", required=True)
    p.set_defaults(func=cmd_mint_routed)

    p = sub.add_parser("forward")
    p.add_argument("--identity", required=True, help="the sender's identity file")
    p.add_argument("--to", required=True, help="final recipient: the forward's `next` (DID, @file or identity file)")
    p.add_argument("--via", required=True, help="mediator DIDs / identity files, comma-separated, outermost first")
    p.add_argument("--message", required=True, help="plaintext message file (gen-message)")
    p.add_argument("--out", required=True, help="the outermost forward envelope")
    p.add_argument("--inner-out", dest="inner_out", help="also write the envelope inside the forward")
    p.add_argument("--encrypt-to", dest="encrypt_to", help="encrypt the inner envelope for this DID instead of --to")
    p.add_argument("--meta-out", dest="meta_out")
    p.set_defaults(func=cmd_forward)

    p = sub.add_parser("post")
    p.add_argument("--url", required=True)
    p.add_argument("--in", dest="infile", required=True)
    p.add_argument("--proxy", help="forward proxy URL (default: none, whatever the environment says)")
    p.add_argument("--expect-status", dest="expect_status", required=True, help="e.g. 202, or 200,202")
    p.add_argument("--meta-out", dest="meta_out")
    p.add_argument("--body-out", dest="body_out")
    p.add_argument("--timeout", type=float, default=30.0)
    p.set_defaults(func=cmd_post)

    p = sub.add_parser("post-many")
    p.add_argument("--url", required=True)
    p.add_argument("--in", dest="infile", default="", help="envelope files, comma-separated")
    p.add_argument("--in-dir", dest="in_dir", help="also every *.json in this directory")
    p.add_argument("--proxy")
    p.add_argument("--concurrency", type=int, default=4)
    p.add_argument("--expect-status", dest="expect_status", required=True)
    p.add_argument("--meta-out", dest="meta_out")
    p.add_argument("--timeout", type=float, default=30.0)
    p.set_defaults(func=cmd_post_many)

    p = sub.add_parser("cm2-enrol")
    p.add_argument("--identity", required=True, help="the recipient's identity file (python mint)")
    p.add_argument("--mediator", required=True, help="the mediator's DID (or @file / identity file)")
    p.add_argument("--url", required=True, help="the mediator's HTTP endpoint")
    p.add_argument("--register", required=True, help="DIDs / identity files to add, comma-separated")
    p.add_argument("--out", required=True)
    p.add_argument("--variant", default="default",
                   choices=("default", "spec-example", "no-return-route-on-update", "no-created-time"))
    p.add_argument("--expect-routing-did", dest="expect_routing_did", help="default: the mediator DID")
    p.add_argument("--require-created-time", dest="require_created_time", action="store_true")
    p.add_argument("--require-pagination", dest="require_pagination", action="store_true")
    p.add_argument("--expect-update-result", dest="expect_update_result", default="success",
                   help="the result every keylist-update entry must have (default success); any: record only")
    p.add_argument("--proxy")
    p.add_argument("--timeout", type=float, default=30.0)
    p.set_defaults(func=cmd_cm2_enrol)

    p = sub.add_parser("pickup3")
    p.add_argument("--identity", required=True, help="the enrolled identity (talks to the mediator)")
    p.add_argument("--mediator", required=True)
    p.add_argument("--url", required=True)
    p.add_argument("--recipient", required=True, help="identity file of the registered DID the messages are for")
    p.add_argument("--expect-count", dest="expect_count", type=int, required=True)
    p.add_argument("--expect-ack-status", dest="expect_ack_status", choices=("yes", "no"), default="yes",
                   help="yes: messages-received is answered with a status; no: with HTTP 202 and nothing")
    p.add_argument("--limit", type=int, default=10)
    p.add_argument("--out-dir", dest="out_dir", required=True)
    p.add_argument("--out", "--meta-out", dest="out", help="the step record (JSON)")
    p.add_argument("--wait-timeout", dest="wait_timeout", type=float, default=30.0)
    p.add_argument("--proxy")
    p.add_argument("--timeout", type=float, default=30.0)
    p.set_defaults(func=cmd_pickup3)

    args = parser.parse_args()
    try:
        args.func(args)
    except CheckFailed as failure:
        print(f"ASSERT FAIL: {failure}", file=sys.stderr)
        sys.exit(1)
    except Exception as ex:  # noqa: BLE001 -- CLI boundary: report and exit nonzero
        print(f"mediator_peer: {type(ex).__name__}: {ex}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
