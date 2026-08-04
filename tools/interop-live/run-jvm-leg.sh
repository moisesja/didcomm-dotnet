#!/usr/bin/env bash
#
# Live cross-implementation interop, JVM leg (PRD §13.6 FR-IX-04/05).
#
# Round-trips DIDComm v2 envelopes over did:peer:2 between didcomm-dotnet (tools/InteropCli,
# the real DidCommClient + net-did resolver) and SICPA's didcomm-jvm (org.didcommx:didcomm
# 0.3.2, pinned by SHA-256 in jvm/fetch-deps.sh, driven by jvm/src/InteropPeer.java — plain
# javac/java, no Gradle/Maven) — both directions, for every §13.5 composition the pair
# supports:
#
#   OUTBOUND (FR-IX-04, MUST):  didcomm-dotnet packs → didcomm-jvm unpacks
#   INBOUND  (FR-IX-05, SHOULD): didcomm-jvm packs → didcomm-dotnet unpacks
#
# Every cell asserts the recovered plaintext equals the original payload AND that the unpack
# metadata matches what the composition requires. Prints a per-cell PASS/FAIL/N-A table,
# writes it to $INTEROP_SUMMARY_DIR/jvm-leg.md (when set), and exits nonzero on any FAIL.
#
# N-A cells are declared, not skipped silently — each carries a reason (see README.md,
# "Known counterpart deviations" and "Known didcomm-dotnet defects").
#
# Usage: bash tools/interop-live/run-jvm-leg.sh
#   JAVA_HOME         JDK to use (default: java/javac on PATH; validated on 17 and 25)
#   INTEROP_WORK      scratch dir (default: mktemp -d; kept for debugging)
#   INTEROP_SUMMARY_DIR  where to also write the summary table markdown (optional)
#   INTEROP_CLI       prebuilt InteropCli binary (default: build Release from source)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
WORK="${INTEROP_WORK:-$(mktemp -d "${TMPDIR:-/tmp}/didcomm-interop-jvm.XXXXXX")}"
mkdir -p "$WORK"
echo "work dir: $WORK" >&2

JAVA_BIN="${JAVA_HOME:+$JAVA_HOME/bin/}java"
JAVAC_BIN="${JAVA_HOME:+$JAVA_HOME/bin/}javac"

# ── counterpart stack (pinned) + runner build ─────────────────────────────────────────────
LIBS="$SCRIPT_DIR/jvm/libs"
bash "$SCRIPT_DIR/jvm/fetch-deps.sh" "$LIBS"
OUT="$WORK/jvm-classes"
mkdir -p "$OUT"
"$JAVAC_BIN" -cp "$LIBS/*" -d "$OUT" "$SCRIPT_DIR/jvm/src/InteropPeer.java"
PEER=("$JAVA_BIN" -cp "$OUT:$LIBS/*" InteropPeer)
echo "didcomm-jvm: org.didcommx:didcomm:0.3.2 on $("$JAVA_BIN" -version 2>&1 | head -1)" >&2

# ── didcomm-dotnet CLI ────────────────────────────────────────────────────────────────────
if [ -z "${INTEROP_CLI:-}" ]; then
    dotnet build "$REPO_ROOT/tools/InteropCli/InteropCli.csproj" -c Release --nologo -v quiet >&2
    INTEROP_CLI="$REPO_ROOT/tools/InteropCli/bin/Release/net10.0/InteropCli"
fi

# ── identities ────────────────────────────────────────────────────────────────────────────
DOTNET_ID="$WORK/dotnet-id.json"
JVM_ID="$WORK/jvm-id.json"
"$INTEROP_CLI" mint --out "$DOTNET_ID" >/dev/null
"${PEER[@]}" mint --out "$JVM_ID" >/dev/null

# ── matrix cells: "<mode>;<enc>;<needs_from>" ─────────────────────────────────────────────
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

# Declared N-A cells (see README.md for the full conformance analysis).
# didcomm-jvm cannot verify didcomm-dotnet's signed envelopes at all today: the emitted JWS
# duplicates `kid` into both the protected and unprotected headers (an RFC 7515 §7.2
# disjointness violation originating in DataProofsDotnet.Jose's JwsBuilder — nimbus enforces
# it), while didcomm-jvm additionally REQUIRES the kid in the unprotected per-signature
# header, so no lossless post-sign transform exists. Fix belongs upstream in
# dataproofs-dotnet (emit kid only in the unprotected header, the spec C.2 shape).
na_reason() { # $1=direction $2=mode
    if [ "$1" = "outbound" ] && { [ "$2" = "signed" ] || [ "$2" = "anoncrypt-sign" ]; }; then
        echo "didcomm-dotnet's JWS kid-in-both-headers layout (RFC 7515 §7.2 violation, from DataProofsDotnet.Jose) is rejected by nimbus/didcomm-jvm"
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
        if "${PEER[@]}" gen-message --to "$JVM_ID" "${from_args[@]+"${from_args[@]}"}" --out "$msg" 2>>"$log" &&
           "$INTEROP_CLI" pack --identity "$DOTNET_ID" --to "$JVM_ID" --mode "$mode" --enc "$enc_arg" --message "$msg" --out "$env" 2>>"$log" &&
           "${PEER[@]}" unpack --identity "$JVM_ID" --in "$env" --out "$un" 2>>"$log" &&
           "${PEER[@]}" assert --mode "$mode" --enc "$enc_arg" --expected "$msg" --unpacked "$un" 2>>"$log"; then
            CELL_RESULT="PASS"
        else
            CELL_RESULT="FAIL"
        fi
    else
        [ "$needs_from" = "yes" ] && from_args=(--from "$JVM_ID")
        if "${PEER[@]}" gen-message --to "$DOTNET_ID" "${from_args[@]+"${from_args[@]}"}" --out "$msg" 2>>"$log" &&
           "${PEER[@]}" pack --identity "$JVM_ID" --to "$DOTNET_ID" --mode "$mode" --enc "$enc_arg" --message "$msg" --out "$env" 2>>"$log" &&
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
SUMMARY="$WORK/jvm-leg.md"
{
    echo "## Live interop — JVM leg (didcomm-dotnet ↔ didcomm-jvm 0.3.2)"
    echo
    echo "| composition | enc | outbound (dotnet packs → jvm unpacks) | inbound (jvm packs → dotnet unpacks) |"
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
    cp "$SUMMARY" "$INTEROP_SUMMARY_DIR/jvm-leg.md"
fi

echo
if [ "$FAILURES" -gt 0 ]; then
    echo "jvm leg: $FAILURES cell(s) FAILED (details above; work dir kept at $WORK)" >&2
    exit 1
fi
echo "jvm leg: all runnable cells passed (summary: $SUMMARY)"
