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
        lines += ["Negative controls: each row is a cell run with one sabotage; every row must FAIL.", "",
                  "| cell | sabotage | result | s | detail |", "|---|---|---|---|---|"]
        for c in cells:
            lines.append(f"| {c['id']} | {c['sabotage']} | {c['result']} | {c['seconds']} | {c['detail']} |")
        broken = [c for c in cells if not c["result"].startswith("FAIL")]
        totals = (f"**Negative controls** — sabotaged runs: {len(cells)} · failed as required: "
                  f"{len(cells) - len(broken)} · passed (control broken): {len(broken)}")
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
                  "the full records are in `mediator-observations.json`.", "",
                  "| id | E / I | what | observed | method |", "|---|---|---|---|---|"]
        for r in records:
            refs = ", ".join(r["e"] + r["i"]) or "—"
            lines.append(f"| {r['id']} | {refs} | {compact(r['title'], 200)} | {compact(r['value'])} | {compact(r['method'], 80)} |")

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
