#!/usr/bin/env bash
#
# Live cross-implementation interop, python leg (PRD §13.6 FR-IX-04/05).
#
# Round-trips DIDComm v2 envelopes over did:peer:2 between didcomm-dotnet (tools/InteropCli,
# the real DidCommClient + net-did resolver) and SICPA's didcomm-python (pinned in
# python/requirements.txt, driven by python/interop_peer.py) — both directions, for every
# §13.5 composition the pair supports:
#
#   OUTBOUND (FR-IX-04, MUST):  didcomm-dotnet packs → didcomm-python unpacks
#   INBOUND  (FR-IX-05, SHOULD): didcomm-python packs → didcomm-dotnet unpacks
#
# Every cell asserts the recovered plaintext equals the original payload AND that the unpack
# metadata matches what the composition requires (see interop_peer.py expected_metadata).
# Prints a per-cell PASS/FAIL/N-A table, writes it to $INTEROP_SUMMARY_DIR/python-leg.md
# (when set), and exits nonzero on any FAIL.
#
# N-A cells are declared, not skipped silently — each carries a reason (see README.md,
# "Known counterpart deviations").
#
# Usage: bash tools/interop-live/run-python-leg.sh
#   PYTHON            python interpreter to seed the venv (default: python3)
#   INTEROP_WORK      scratch dir (default: mktemp -d; kept for debugging)
#   INTEROP_SUMMARY_DIR  where to also write the summary table markdown (optional)
#   INTEROP_CLI       prebuilt InteropCli binary (default: build Release from source)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
WORK="${INTEROP_WORK:-$(mktemp -d "${TMPDIR:-/tmp}/didcomm-interop-python.XXXXXX")}"
mkdir -p "$WORK"
echo "work dir: $WORK" >&2

# ── counterpart venv (pinned) ─────────────────────────────────────────────────────────────
VENV="$WORK/venv"
if [ ! -x "$VENV/bin/python" ]; then
    "$PYTHON" -m venv "$VENV"
    "$VENV/bin/pip" install --quiet --disable-pip-version-check \
        -r "$SCRIPT_DIR/python/requirements.txt"
fi
PEER=("$VENV/bin/python" "$SCRIPT_DIR/python/interop_peer.py")
echo "didcomm-python: $("$VENV/bin/pip" show didcomm | grep -i ^version)" >&2

# ── didcomm-dotnet CLI ────────────────────────────────────────────────────────────────────
if [ -z "${INTEROP_CLI:-}" ]; then
    dotnet build "$REPO_ROOT/tools/InteropCli/InteropCli.csproj" -c Release --nologo -v quiet >&2
    INTEROP_CLI="$REPO_ROOT/tools/InteropCli/bin/Release/net10.0/InteropCli"
fi

# ── identities ────────────────────────────────────────────────────────────────────────────
DOTNET_ID="$WORK/dotnet-id.json"
PY_ID="$WORK/python-id.json"
"$INTEROP_CLI" mint --out "$DOTNET_ID" >/dev/null
"${PEER[@]}" mint --out "$PY_ID" >/dev/null

# ── matrix cells: "<mode>;<enc>;<needs_from>" ─────────────────────────────────────────────
# enc applies to the anoncrypt layer; authcrypt is A256CBC-HS512 by spec profile (both libs).
CELLS=(
    "plaintext;-;yes"
    "signed;-;yes"
    "anoncrypt;A256CBC-HS512;no"
    "anoncrypt;A256GCM;no"
    "anoncrypt;XC20P;no"
    "authcrypt;A256CBC-HS512;yes"
    "anoncrypt-sign;A256CBC-HS512;yes"
    "anoncrypt-sign;XC20P;yes"
    "anoncrypt-authcrypt;A256CBC-HS512;yes"
)

# Declared N-A cells (counterpart limitations — see README.md for the conformance analysis).
# didcomm-python only parses General JWS ("recipients MUST be able to process both forms",
# spec §Message Signing — the counterpart is the nonconformant side). Standalone signed is
# rescued by a lossless Flattened→General re-serialization in interop_peer.py, but the inner
# JWS of anoncrypt(sign) is inside the ciphertext where no wire-level shim can reach it.
na_reason() { # $1=direction $2=mode
    if [ "$1" = "outbound" ] && [ "$2" = "anoncrypt-sign" ]; then
        echo "didcomm-python rejects the spec-valid Flattened inner JWS didcomm-dotnet emits (FR-SIG-02)"
    fi
}

# ── per-cell execution ────────────────────────────────────────────────────────────────────
run_cell() { # $1=direction $2=mode $3=enc $4=needs_from → sets CELL_RESULT
    local direction="$1" mode="$2" enc="$3" needs_from="$4"
    local enc_arg="$enc"
    [ "$enc_arg" = "-" ] && enc_arg="A256CBC-HS512"
    local tag="$direction-$mode-$enc_arg"
    local msg="$WORK/$tag.msg.json" env="$WORK/$tag.env.json" un="$WORK/$tag.unpacked.json"
    local log="$WORK/$tag.log"

    local from_args=()
    if [ "$direction" = "outbound" ]; then
        [ "$needs_from" = "yes" ] && from_args=(--from "$DOTNET_ID")
        if "${PEER[@]}" gen-message --to "$PY_ID" "${from_args[@]+"${from_args[@]}"}" --out "$msg" 2>>"$log" &&
           "$INTEROP_CLI" pack --identity "$DOTNET_ID" --to "$PY_ID" --mode "$mode" --enc "$enc_arg" --message "$msg" --out "$env" 2>>"$log" &&
           "${PEER[@]}" unpack --identity "$PY_ID" --in "$env" --out "$un" 2>>"$log" &&
           "${PEER[@]}" assert --mode "$mode" --enc "$enc_arg" --expected "$msg" --unpacked "$un" 2>>"$log"; then
            CELL_RESULT="PASS"
        else
            CELL_RESULT="FAIL"
        fi
    else
        [ "$needs_from" = "yes" ] && from_args=(--from "$PY_ID")
        if "${PEER[@]}" gen-message --to "$DOTNET_ID" "${from_args[@]+"${from_args[@]}"}" --out "$msg" 2>>"$log" &&
           "${PEER[@]}" pack --identity "$PY_ID" --to "$DOTNET_ID" --mode "$mode" --enc "$enc_arg" --message "$msg" --out "$env" 2>>"$log" &&
           "$INTEROP_CLI" unpack --identity "$DOTNET_ID" --in "$env" --out "$un" 2>>"$log" &&
           "${PEER[@]}" assert --mode "$mode" --enc "$enc_arg" --expected "$msg" --unpacked "$un" 2>>"$log"; then
            CELL_RESULT="PASS"
        else
            CELL_RESULT="FAIL"
        fi
    fi
    if [ "$CELL_RESULT" = "FAIL" ]; then
        echo "--- $tag failed; log: ---" >&2
        cat "$log" >&2 || true
    fi
}

# ── run the matrix ────────────────────────────────────────────────────────────────────────
SUMMARY="$WORK/python-leg.md"
{
    echo "## Live interop — python leg (didcomm-dotnet ↔ didcomm-python $("$VENV/bin/pip" show didcomm 2>/dev/null | awk '/^Version/{print $2}'))"
    echo
    echo "| composition | enc | outbound (dotnet packs → python unpacks) | inbound (python packs → dotnet unpacks) |"
    echo "|---|---|---|---|"
} > "$SUMMARY"

FAILURES=0
NOTES=()
for cell in "${CELLS[@]}"; do
    IFS=';' read -r mode enc needs_from <<< "$cell"
    row=()
    for direction in outbound inbound; do
        reason="$(na_reason "$direction" "$mode")"
        if [ -n "$reason" ]; then
            row+=("N-A*")
            NOTES+=("$direction $mode: $reason")
        else
            run_cell "$direction" "$mode" "$enc" "$needs_from"
            row+=("$CELL_RESULT")
            [ "$CELL_RESULT" = "FAIL" ] && FAILURES=$((FAILURES + 1))
        fi
    done
    printf '| %s | %s | %s | %s |\n' "$mode" "$enc" "${row[0]}" "${row[1]}" >> "$SUMMARY"
    printf '%-22s %-14s outbound=%-5s inbound=%s\n' "$mode" "$enc" "${row[0]}" "${row[1]}"
done

if [ "${#NOTES[@]}" -gt 0 ]; then
    {
        echo
        for note in "${NOTES[@]}"; do echo "- \`N-A*\` $note"; done
    } >> "$SUMMARY"
fi

if [ -n "${INTEROP_SUMMARY_DIR:-}" ]; then
    mkdir -p "$INTEROP_SUMMARY_DIR"
    cp "$SUMMARY" "$INTEROP_SUMMARY_DIR/python-leg.md"
fi

echo
if [ "$FAILURES" -gt 0 ]; then
    echo "python leg: $FAILURES cell(s) FAILED (details above; work dir kept at $WORK)" >&2
    exit 1
fi
echo "python leg: all runnable cells passed (summary: $SUMMARY)"
