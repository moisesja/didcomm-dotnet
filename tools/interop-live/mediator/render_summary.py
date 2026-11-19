#!/usr/bin/env python3
"""
Summary of the mediator leg (run-mediator-leg.sh): the cells table, the observations table and
the totals, into mediator-leg.md; every observation record, as data, into
mediator-observations.json.

  obs     write one observation record:
          {"id", "e": [], "i": [], "title", "value", "method", "gateable": false}
  render  read the leg's cells.tsv (id, sabotage, result, detail, seconds, title), the records
          in --obs-dir and the wallet's own observations (--wallet-obs, when present), and write
          --md and --json. Prints the totals line(s) on stdout.

Observations are never gating: they describe a digest-pinned third party, so they are data in
a separate table and never change the leg's exit code. Stdlib only; run with -I.
"""

import argparse
import glob
import itertools
import json
import os
import sys

# Observations the leg cannot measure in this topology, recorded as such (design §4).
STATIC = [
    {"id": "O-19", "e": ["E-37"], "i": [], "title": "burst tail; Push-Failed-but-arrived",
     "value": "not applicable against Identus: its pushes are never taken as pushes by our client (DD-13; reached by polling)",
     "method": "—", "gateable": False},
    {"id": "O-20", "e": ["E-18", "E-22", "E-52"], "i": ["I-4"],
     "title": "an external client's reaction to our mediator",
     "value": "not applicable: no external client in the harness; I-4 (keylist-update without return_route) is emulated in R-1",
     "method": "—", "gateable": False},
]


def split_ids(text):
    return [part.strip() for part in (text or "").split(",") if part.strip()]


def cmd_obs(args):
    value = args.value
    if args.value_file:
        try:
            with open(args.value_file, encoding="utf-8") as f:
                value = json.load(f)
        except (OSError, ValueError) as ex:
            value = f"not measured ({type(ex).__name__}: {ex})"
    record = {
        "id": args.id,
        "e": split_ids(args.e),
        "i": split_ids(args.i),
        "title": args.title,
        "value": value,
        "method": args.method,
        "gateable": False,
    }
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(record, f, indent=2)
        f.write("\n")


def normalize(record):
    """A wallet record in the agreed schema; missing fields filled, gateable forced false."""
    if not isinstance(record, dict) or "id" not in record:
        return None
    out = {
        "id": str(record["id"]),
        "e": record.get("e") if isinstance(record.get("e"), list) else split_ids(record.get("e")),
        "i": record.get("i") if isinstance(record.get("i"), list) else split_ids(record.get("i")),
        "title": record.get("title", ""),
        "value": record.get("value"),
        "method": record.get("method", "MediatorWallet observe"),
        "gateable": False,
    }
    return out


def wallet_records(path):
    if not path or not os.path.exists(path):
        return []
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, ValueError) as ex:
        return [{"id": "O-wallet", "e": [], "i": [], "title": "MediatorWallet observe output",
                 "value": f"unreadable ({type(ex).__name__}: {ex})", "method": "MediatorWallet observe", "gateable": False}]
    if isinstance(data, dict):
        for key in ("observations", "records"):
            if isinstance(data.get(key), list):
                data = data[key]
                break
        else:
            observed = data.get("observed")
            if isinstance(observed, list):
                data = observed
            elif isinstance(observed, dict):
                data = [dict(v, id=k) if isinstance(v, dict) and "id" not in v else v if isinstance(v, dict) else {"id": k, "value": v}
                        for k, v in observed.items()]
            else:
                data = []
    return [r for r in (normalize(item) for item in data if isinstance(data, list)) if r]


def id_key(record):
    """O-n records in numeric order (O-5, O-5-host, O-10 …), then the others (I-…) by name."""
    rid = record["id"]
    prefix, _, rest = rid.partition("-")
    digits = "".join(itertools.takewhile(str.isdigit, rest))
    return (0 if prefix == "O" and digits else 1, int(digits) if digits else 0, rid)


def compact(value, limit=420):
    text = value if isinstance(value, str) else json.dumps(value, separators=(", ", ": "), default=str)
    text = text.replace("|", "/").replace("\n", " ")
    return text if len(text) <= limit else text[: limit - 1] + "…"


# --- one plain-language line per observation (the table); the JSON keeps the data ----------


def _g(value, *path, default=None):
    for key in path:
        if isinstance(value, dict):
            value = value.get(key)
        elif isinstance(value, list) and isinstance(key, int) and -len(value) <= key < len(value):
            value = value[key]
        else:
            return default
    return default if value is None else value


def _short_type(uri):
    return str(uri).rsplit("/", 1)[-1] if uri else "nothing"


def _proto(uri):
    return "/".join(str(uri).rstrip("/").split("/")[-2:]) if uri else "nothing"


def _shown(value):
    return "absent" if value is None else json.dumps(value)


def _yes(flag, yes="yes", no="no"):
    return yes if flag else no


def _answers(value):
    answers = _g(value, "answers", default=[])
    return [a for a in answers if isinstance(a, dict)]


def _exception(outcome):
    exception = _g(outcome, "exception", default={})
    kind = str(exception.get("type", "")).rsplit(".", 1)[-1]
    return kind or _g(outcome, "outcome", default="?")


def s_o0(v):
    return (f"Identus opened our authcrypt request: {_yes(v.get('mediator_opened_our_authcrypt'))}; "
            f"our anoncrypt request: {_yes(v.get('mediator_opened_our_anoncrypt'))}. We opened its answer: "
            f"{_yes(v.get('we_opened_the_mediator_answer'))} (authenticated: {_yes(v.get('mediator_answer_authenticated'))}).")


def s_o1(v):
    attempt = v.get("attempt") or {}
    before = _g(v, "identus_ws_binary_errors_logged", "before")
    after = _g(v, "identus_ws_binary_errors_logged", "after")
    rose = before is not None and after is not None and after > before
    return (f"Our WebSocket call (defaults) ended in {_g(attempt, 'outcome', default='?')} "
            f"({_exception(attempt)}, {_g(attempt, 'elapsed_ms', default='?')} ms); Identus "
            f"{'logged our frame as an unexpected Binary frame' if rose else 'logged no Binary-frame error'}.")


def s_o2(v):
    status = v.get("status_request_text_frame") or {}
    push = v.get("push") or {}
    answer = push.get("answer") or {}
    return (f"Text frames are answered ({_short_type(_g(status, 'answer', 'type'))}, "
            f"{_g(status, 'frames', default='?')} frame, created_time {_yes(_g(status, 'answer', 'created_time_present'), 'stamped', 'not stamped')}). "
            f"Live push: a {_short_type(answer.get('type'))} in {push.get('frames', '?')} {push.get('frame_type', '?')} frame, "
            f"thid {_yes(answer.get('thid'), 'set', 'absent')}, to {answer.get('to')}, created_time "
            f"{_yes(answer.get('created_time_present'), 'stamped', 'not stamped')}; our client cannot take it as a push (O-1).")


def s_o2c(v):
    burst = v.get("burst_after_close") or []
    unanswered = sum(1 for b in burst if b == "no response")
    return (f"After a live socket closed with live mode on, {v.get('answered_after_close', '?')} sequential forwards "
            f"were answered; of a burst of {len(burst)} concurrent forwards, {unanswered} got no HTTP response.")


def s_o3(v):
    rounds = v.get("fetch_limit_1") or []
    failed = [f for r in rounds for f in (r.get("failed") or [])]
    return (f"No lease: two delivery-requests without an ack returned the same "
            f"{_g(v, 'raw_first', 'attachments', default='?')} message(s) "
            f"({_yes(v.get('same_ids_twice_no_lease'), 'same ids', 'different ids')}). With limit 1 and a junk "
            f"forward queued, FetchAsync took {len(rounds)} round(s); {len(failed)} item(s) did not open ({', '.join(sorted(set(failed))) or 'none'}).")


def s_answers(field, describe):
    def summarize(v):
        answers = _answers(v)
        return describe(answers) if answers else "no answers recorded"
    return summarize


def s_o4(answers):
    same = sum(1 for a in answers if a.get("thid_is_request_id"))
    others = sorted({str(a.get("step")) for a in answers if not a.get("thid_is_request_id")})
    return (f"thid is the request id on {same} of {len(answers)} answers"
            + (f"; not on: {', '.join(others)}." if others else "."))


def s_o5(answers):
    stamped = sum(1 for a in answers if a.get("created_time_present"))
    dated = [a.get("date_minus_our_clock_s") for a in answers if isinstance(a.get("date_minus_our_clock_s"), (int, float))]
    skew = f"; HTTP Date header minus our clock: {min(dated)}..{max(dated)} s" if dated else ""
    return f"created_time on {stamped} of {len(answers)} answers and pushes{skew}."


def s_o6(answers):
    kinds = sorted({str(a.get("to")) for a in answers})
    return f"to on the {len(answers)} answers: {', '.join(kinds)} (by role)."


def s_i11(answers):
    prior = sum(1 for a in answers if a.get("from_prior_present"))
    mediator = sum(1 for a in answers if a.get("from_is_mediator"))
    members = sorted({m for a in answers for m in (a.get("top_level_members") or [])})
    return (f"from_prior on {prior} of {len(answers)} answers; from is the mediator on {mediator}; top-level members seen: "
            f"{', '.join(members)}.")


def s_o4a(v):
    exchanges = v.get("exchanges") or []
    kinds = [f"{_short_type(_g(e, 'record', 'request_type'))}→{_short_type(_g(e, 'record', 'answer', 'type'))}" for e in exchanges]
    return f"{len(exchanges)} exchanges recorded ({', '.join(kinds)}); routing_did is a JSON {v.get('routing_did_json_kind', '?')}."


def s_o5host(v):
    return f"The Identus container's clock is {v.get('offset_ms', '?')} ms from the host's (± {v.get('uncertainty_ms', '?')} ms)."


def s_o7(v):
    if not isinstance(v, dict):
        return str(v)
    return (f"QueryRecipientsAsync(limit 1) returned {v.get('dids', '?')} DIDs and "
            f"{'pagination ' + json.dumps(v.get('pagination')) if v.get('pagination') else 'no pagination'}: Identus ignores paginate.")


def s_o8(v):
    return (f"limit 5 with {v.get('queued', '?')} queued returned {v.get('limit_5_returned', '?')}; one messages-received "
            f"naming {_g(v, 'messages_received_all_ids', 'ids', default='?')} ids (HTTP "
            f"{_g(v, 'messages_received_all_ids', 'wire', 'http_status', default='?')}) removed "
            f"{v.get('removed_by_that_one_messages_received', '?')}; status after it: {v.get('status_after', '?')}.")


def s_o9(v):
    ack = v.get("ack_async") or {}
    return (f"A raw messages-received got HTTP {_g(v, 'raw_messages_received', 'http_status', default='?')} after "
            f"{_g(v, 'raw_messages_received', 'elapsed_ms', default='?')} ms with no answer. AckAsync: "
            f"{_g(ack, 'outcome', default='?')}"
            + (f" ({_exception(ack)})" if _g(ack, 'outcome') not in (None, 'ok') else
               f" (status {_g(ack, 'message_count', default=_g(ack, 'status_count', default='?'))})")
            + f" in {_g(ack, 'elapsed_ms', default='?')} ms; status afterwards {v.get('status_after_ack_async', '?')}.")


def s_o10(v):
    parts = []
    for source in v if isinstance(v, list) else []:
        encodings = source.get("encodings") or []
        if not encodings:
            parts.append(f"{source.get('source')}: not measured")
            continue
        e = encodings[0]
        parts.append(f"{source.get('source')}: {e.get('form')}, {'padded' if e.get('padded') else 'unpadded'}, "
                     f"strict unpadded-base64url decoder {'accepts' if e.get('strict_url_unpadded_accepts') else 'rejects'}")
    return "; ".join(parts) + ". The alphabet cannot be told from this content (no sextet 62/63)." if parts else "not measured"


def s_o11(v):
    cases = v.get("cases") or []
    parts = [f"{c.get('case')}: delivered {c.get('against_sender_inner', '?')} to the sender's inner envelope, please_ack "
             f"{_shown(c.get('delivery_please_ack'))}, return_route {_shown(c.get('delivery_return_route'))}" for c in cases]
    return ("; ".join(parts) + ".") if parts else "no cases recorded"


def s_o11p(v):
    return (f"Delivered to python {v.get('delivered_vs_python_inner_envelope', '?')} to python's inner envelope; please_ack "
            f"{_shown(v.get('delivery_please_ack'))}, return_route {_shown(v.get('delivery_return_route'))}; messages-received got HTTP "
            f"{_g(v, 'ack', 'status', default='?')} after {_g(v, 'ack', 'elapsed_ms', default='?')} ms, no status.")


def s_o12(v):
    return (f"First post: {_short_type(v.get('first_answer_type'))}; the same envelope again: "
            f"{_short_type(v.get('second_answer_type'))} {v.get('second_problem_code') or ''}".rstrip() + ".")


def s_o13(v):
    return (f"keylist-update with updates: 5 got HTTP {_g(v, 'keylist_update_updates_5', 'wire', 'http_status', default='?')} "
            f"after {_g(v, 'keylist_update_updates_5', 'wire', 'elapsed_ms', default='?')} ms and no answer; an unparseable "
            f"body got HTTP {_g(v, 'unparseable_body', 'http_status', default='?')}.")


def s_o14(v):
    return (f"HTTP {v.get('status', '?')} in {v.get('elapsed_ms', '?')} ms with a {v.get('envelope', '?')} "
            f"{_short_type(v.get('type'))} {v.get('code') or ''}".rstrip() + ".")


def s_o15(v):
    disclosed = [f"{_proto(d.get('id'))} roles {d.get('roles')}" for d in (_g(v, 'client_queries', 'disclosures', default=[]) or [])]
    return (f"Our client's two queries disclosed {', '.join(disclosed) or 'nothing'}; match '*' got HTTP "
            f"{_g(v, 'match_all', 'wire', 'http_status', default='?')} and no disclosure.")


def s_o16(v):
    status = v.get("keylist_update_http_status")
    if v.get("updated"):
        return f"keylist-update answered: {json.dumps(v.get('updated'))}."
    return f"The keylist-update got HTTP {status} after {v.get('elapsed_ms', '?')} ms and no keylist-update-response."


def s_o21(v):
    jars = " ".join(j for j in str(v.get("scala_did_jars", "")).split() if j.startswith("app.fmgp.did_3"))
    return (f"Identus {v.get('identus_version')} ({v.get('image_architecture')}), {jars or 'scala-did jars not listed'}; "
            f"Docker {v.get('docker_server')}; runner {str(v.get('runner_image')).strip() or 'local'}; {v.get('cpython')}; "
            f"didcomm-python {v.get('didcomm_python')}; port retries {v.get('port_retries')}. Image refs in the JSON.")


SUMMARIZERS = {
    "O-0": s_o0, "O-1": s_o1, "O-2": s_o2, "O-2c": s_o2c, "O-3": s_o3,
    "O-4": s_answers("answers", s_o4), "O-4-answers": s_o4a, "O-5": s_answers("answers", s_o5),
    "O-5-host": s_o5host, "O-6": s_answers("answers", s_o6), "O-7": s_o7, "O-8": s_o8, "O-9": s_o9,
    "O-10": s_o10, "O-11": s_o11, "O-11-python": s_o11p, "O-12": s_o12, "O-13": s_o13, "O-14": s_o14,
    "O-15": s_o15, "O-16": s_o16, "O-21": s_o21, "I-11-13-15": s_answers("answers", s_i11),
}


def plain(record):
    """One sentence for the table. Anything unexpected falls back to a pointer at the JSON."""
    value = record.get("value")
    if isinstance(value, str):
        return value
    if isinstance(value, dict) and set(value) == {"error"}:
        return f"not measured: {value['error']}"
    summarize = SUMMARIZERS.get(record["id"])
    if summarize is None:
        return "recorded; see mediator-observations.json"
    try:
        return summarize(value)
    except Exception as ex:  # noqa: BLE001 -- a summary must never break the leg's summary
        return f"recorded; see mediator-observations.json (no summary: {type(ex).__name__})"



def read_cells(path):
    cells = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 6:
                continue
            cid, sabotage, result, detail, seconds, title = parts[:6]
            cells.append({"id": cid, "sabotage": sabotage, "result": result, "detail": detail,
                          "seconds": int(seconds) if seconds.isdigit() else None, "title": title})
    return cells


def cmd_render(args):
    cells = read_cells(args.cells)
    records = []
    for path in sorted(glob.glob(os.path.join(args.obs_dir, "*.json"))):
        try:
            with open(path, encoding="utf-8") as f:
                record = normalize(json.load(f))
            if record:
                records.append(record)
        except (OSError, ValueError):
            continue
    if args.mode == "gate":
        records += wallet_records(args.wallet_obs)
        seen = {r["id"] for r in records}
        records += [r for r in STATIC if r["id"] not in seen]
    records.sort(key=id_key)

    lines = ["## Live interop — mediator leg (didcomm-dotnet ↔ Hyperledger Identus mediator, didcomm-python)", ""]
    if args.header:
        lines += [args.header, ""]
    if args.mode == "nc":
        lines += ["Negative controls: each row is a cell run with one sabotage. Every row must FAIL, and for the reason "
                  "the sabotage causes (its expected detail); a FAIL for another reason is a broken control.", "",
                  "| cell | sabotage | result | s | detail |", "|---|---|---|---|---|"]
        for c in cells:
            lines.append(f"| {c['id']} | {c['sabotage']} | {c['result']} | {c['seconds']} | {c['detail']} |")
        broken = [c for c in cells if c["result"] != "FAIL (as required)"]
        totals = (f"**Negative controls** — sabotaged runs: {len(cells)} · failed as required: "
                  f"{len(cells) - len(broken)} · broken (passed, or failed for another reason): {len(broken)}")
    else:
        lines += ["| cell | what | result | s | detail |", "|---|---|---|---|---|"]
        for c in cells:
            lines.append(f"| {c['id']} | {c['title']} | {c['result']} | {c['seconds']} | {c['detail']} |")
        failed = [c for c in cells if c["result"] != "PASS"]
        totals = (f"**Totals** — executed: {len(cells)} · passed: {len(cells) - len(failed)} · "
                  f"failed: {len(failed)} · declared n/a: 0")
    lines += ["", totals]

    if args.mode == "gate" and records:
        lines += ["", "### Observations (recorded, not gated)", "",
                  "Data about the pinned Identus image and our client against it. None of these rows can fail the leg; "
                  "each row is one line of what was seen, and the full records are in `mediator-observations.json`.", "",
                  "| id | E / I | what | observed | method |", "|---|---|---|---|---|"]
        for r in records:
            refs = ", ".join(r["e"] + r["i"]) or "—"
            lines.append(f"| {r['id']} | {refs} | {compact(r['title'], 200)} | {compact(plain(r), 600)} | {compact(r['method'], 80)} |")

    with open(args.md, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    with open(args.json, "w", encoding="utf-8") as f:
        json.dump({"mode": args.mode, "header": args.header, "cells": cells, "observations": records}, f, indent=2, default=str)
        f.write("\n")
    print(totals.replace("**", ""))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("obs")
    p.add_argument("--out", required=True)
    p.add_argument("--id", required=True)
    p.add_argument("--title", required=True)
    p.add_argument("--method", required=True)
    p.add_argument("--e", default="")
    p.add_argument("--i", default="")
    p.add_argument("--value-file", dest="value_file")
    p.add_argument("--value")
    p.set_defaults(func=cmd_obs)

    p = sub.add_parser("render")
    p.add_argument("--mode", choices=("gate", "nc", "up"), default="gate")
    p.add_argument("--cells", required=True)
    p.add_argument("--obs-dir", dest="obs_dir", required=True)
    p.add_argument("--wallet-obs", dest="wallet_obs")
    p.add_argument("--md", required=True)
    p.add_argument("--json", required=True)
    p.add_argument("--header", default="")
    p.set_defaults(func=cmd_render)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    sys.exit(main())
