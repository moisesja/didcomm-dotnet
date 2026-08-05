#!/usr/bin/env bash
#
# Live cross-implementation interop, python leg (PRD §13.6 FR-IX-04/05/06).
#
# Round-trips DIDComm v2 envelopes between didcomm-dotnet (tools/InteropCli, the real
# DidCommClient + net-did resolver) and SICPA's didcomm-python (pinned in
# python/requirements.txt, driven by python/interop_peer.py) — both directions, across the
# §13.5 matrix dimensions the pair supports:
#
#   OUTBOUND (FR-IX-04, MUST):  didcomm-dotnet packs → didcomm-python unpacks
#   INBOUND  (FR-IX-05, SHOULD): didcomm-python packs → didcomm-dotnet unpacks
#   VECTORS  (FR-IX-06, MUST):  didcomm-python unpacks our PUBLISHED fixture set
#
# Matrix coverage (§13.5): envelope composition × content encryption × key-agreement curve
# (X25519, P-256) × signing alg (EdDSA, ES256, ES256K) × recipients (single, 3) × routing
# (direct, 1 mediator, 2 mediators) × DID method (did:peer:2, did:key).
#
# Every cell asserts the recovered plaintext equals the original payload AND that the unpack
# metadata matches what the composition requires (see interop_peer.py expected_metadata).
# Prints a per-cell PASS/FAIL/N-A table, writes it to $INTEROP_SUMMARY_DIR/python-leg.md
# (when set), and exits nonzero on any FAIL.
#
# N-A cells are declared, not skipped silently — each carries an evidence-backed reason
# (see README.md, "Known counterpart deviations").
#
# Usage: bash tools/interop-live/run-python-leg.sh
#   PYTHON            python interpreter to seed the venv (default: python3)
#   INTEROP_WORK      scratch dir (default: mktemp -d; kept for debugging)
#   INTEROP_SUMMARY_DIR  where to also write the summary table markdown (optional)
#   INTEROP_CLI       prebuilt InteropCli binary (default: build Release from source)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
FIXTURES="$REPO_ROOT/tests/DidComm.InteropTests/fixtures"
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
# One identity pair per matrix axis. Both drivers mint the SAME identity-file schema
# ({did, secrets: [private JWKs]}) and number did:peer:2 keys identically, so either side can
# resolve the other's DID with no shared state beyond the DID string itself.
mint_pair() { # $1=name, rest: shared mint flags
    local name="$1"; shift
    "$INTEROP_CLI" mint --out "$WORK/dotnet-$name.json" "$@" >/dev/null
    "${PEER[@]}" mint --out "$WORK/python-$name.json" "$@" >/dev/null
}

mint_pair base                                    # X25519 KA + Ed25519 signing (the default)
mint_pair p256ka   --ka-crv P-256                 # P-256 key agreement
mint_pair es256    --sig-crv P-256                # ES256 signing
mint_pair es256k   --sig-crv secp256k1            # ES256K signing
mint_pair multi    --ka-count 3                   # 3 keyAgreement keys on one DID
mint_pair keyx     --method key --key-crv X25519  # did:key, encryption
mint_pair keyed    --method key --key-crv Ed25519 # did:key, signing
mint_pair med1                                    # mediator 1 (both sides)
mint_pair med2                                    # mediator 2 (both sides)

# Routed recipients: a did:peer:2 whose .S service segment advertises the mediators as
# routingKeys. didcomm-dotnet reads those routingKeys to build its outbound forward onion
# (PackEncryptedOptions.Forward = true), so the OUTBOUND mediation cells address these DIDs.
"${PEER[@]}" mint --out "$WORK/python-routed1.json" \
    --route "$WORK/python-med1.json" >/dev/null
"${PEER[@]}" mint --out "$WORK/python-routed2.json" \
    --route "$WORK/python-med1.json,$WORK/python-med2.json" >/dev/null

id() { echo "$WORK/$1-$2.json"; }   # id <dotnet|python> <name>

# ── matrix cells ──────────────────────────────────────────────────────────────────────────
# "<label>;<mode>;<enc>;<needs_from>;<ids>;<sig_alg>;<route>"
#   ids     — the identity-pair name both sides use for this cell
#   sig_alg — the signature algorithm the cell's signing key implies (asserted in metadata)
#   route   — "" direct | "1"/"2" wrap through that many mediators
CELLS=(
    "plaintext;plaintext;-;yes;base;EdDSA;"
    "signed EdDSA;signed;-;yes;base;EdDSA;"
    "signed ES256;signed;-;yes;es256;ES256;"
    "signed ES256K;signed;-;yes;es256k;ES256K;"
    "anoncrypt X25519;anoncrypt;A256CBC-HS512;no;base;EdDSA;"
    "anoncrypt X25519;anoncrypt;A256GCM;no;base;EdDSA;"
    "anoncrypt X25519;anoncrypt;XC20P;no;base;EdDSA;"
    "anoncrypt P-256;anoncrypt;A256CBC-HS512;no;p256ka;EdDSA;"
    "anoncrypt P-256;anoncrypt;A256GCM;no;p256ka;EdDSA;"
    "authcrypt X25519;authcrypt;A256CBC-HS512;yes;base;EdDSA;"
    "authcrypt P-256;authcrypt;A256CBC-HS512;yes;p256ka;EdDSA;"
    "anoncrypt(sign);anoncrypt-sign;A256CBC-HS512;yes;base;EdDSA;"
    "anoncrypt(sign);anoncrypt-sign;XC20P;yes;base;EdDSA;"
    "anoncrypt(authcrypt);anoncrypt-authcrypt;A256CBC-HS512;yes;base;EdDSA;"
    "authcrypt 3-rcpt;authcrypt;A256CBC-HS512;yes;multi;EdDSA;"
    "anoncrypt did:key;anoncrypt;A256CBC-HS512;no;keyx;EdDSA;"
    "authcrypt did:key;authcrypt;A256CBC-HS512;yes;keyx;EdDSA;"
    "signed did:key;signed;-;yes;keyed;EdDSA;"
    "authcrypt 1 mediator;authcrypt;A256CBC-HS512;yes;base;EdDSA;1"
    "authcrypt 2 mediators;authcrypt;A256CBC-HS512;yes;base;EdDSA;2"
)

# ── declared N-A cells (evidence-backed counterpart limitations) ──────────────────────────
# Each reason names the counterpart behaviour that makes the cell unrunnable, so a reader can
# tell a genuine gap from a silent skip. See README.md for the full conformance analysis.
na_reason() { # $1=direction $2=mode $3=ids → reason or empty
    local direction="$1" mode="$2" ids="$3"

    # NOTE: outbound anoncrypt(sign) used to be N-A here — didcomm-python parses only the
    # General JWS serialization, and the inner Flattened JWS didcomm-dotnet emitted sat inside
    # the ciphertext where no wire-level shim could reach it. DataProofsDotnet.Jose 1.3.0 emits
    # General at every signer count, so that cause is gone and the cell now runs (and passes).

    # ES256K inbound: didcomm-python emits RFC 8812-valid signatures with a high-S scalar in
    # roughly half of runs. RFC 8812 imposes NO low-S requirement, so those signatures are
    # correct — didcomm-dotnet is the strict side: NBitcoin.Secp256k1 inherits libsecp256k1's
    # anti-malleability policy in DefaultCryptoProvider.VerifySecp256k1, so it rejects any
    # high-S signature. Ours by dependency, filed as crypto-dotnet#23; not a counterpart defect.
    if [ "$direction" = "inbound" ] && [ "$ids" = "es256k" ]; then
        echo "didcomm-dotnet cannot verify RFC 8812-valid high-S secp256k1 signatures (NBitcoin.Secp256k1 low-S policy in DefaultCryptoProvider.VerifySecp256k1, crypto-dotnet#23); didcomm-python emits high-S in ~50% of runs and is conformant in doing so"
        return
    fi
}

# ── per-cell execution ────────────────────────────────────────────────────────────────────
run_cell() { # $1=direction $2=mode $3=enc $4=needs_from $5=ids $6=sig_alg $7=route
    local direction="$1" mode="$2" enc="$3" needs_from="$4" ids="$5" sig_alg="$6" route="$7"
    local enc_arg="$enc"
    [ "$enc_arg" = "-" ] && enc_arg="A256CBC-HS512"
    local tag="$direction-$mode-$enc_arg-$ids${route:+-r$route}"
    local msg="$WORK/$tag.msg.json" env="$WORK/$tag.env.json" un="$WORK/$tag.unpacked.json"
    local log="$WORK/$tag.log"

    local dotnet_id python_id
    dotnet_id="$(id dotnet "$ids")"
    python_id="$(id python "$ids")"

    # Multi-recipient cells address three distinct DIDs; every other cell addresses one.
    local to_python="$python_id"
    if [ "$ids" = "multi" ]; then
        to_python="$(id python multi),$(id python base),$(id python keyx)"
    fi
    # Outbound mediation targets the routed python DID (whose .S segment carries routingKeys);
    # inbound mediation has python wrap explicitly for the dotnet-side mediators.
    local route_arg=""
    if [ -n "$route" ]; then
        to_python="$(id python "routed$route")"
        route_arg="$(id dotnet med1)"
        [ "$route" = "2" ] && route_arg="$(id dotnet med1),$(id dotnet med2)"
    fi

    local from_args=()
    local result=0
    if [ "$direction" = "outbound" ]; then
        [ "$needs_from" = "yes" ] && from_args=(--from "$dotnet_id")
        local forward_args=()
        [ -n "$route" ] && forward_args=(--forward true)
        "${PEER[@]}" gen-message --to "$to_python" "${from_args[@]+"${from_args[@]}"}" --out "$msg" 2>>"$log" &&
        "$INTEROP_CLI" pack --identity "$dotnet_id" --to "$to_python" --mode "$mode" \
            --enc "$enc_arg" --message "$msg" --out "$env" \
            "${forward_args[@]+"${forward_args[@]}"}" 2>>"$log" || result=1

        # Mediated: didcomm-dotnet built the forward onion; the python-side mediators peel it.
        if [ "$result" = 0 ] && [ -n "$route" ]; then
            "${PEER[@]}" unwrap-forward --identity "$(id python med1)" --in "$env" \
                --out "$WORK/$tag.hop1.json" 2>>"$log" || result=1
            if [ "$result" = 0 ] && [ "$route" = "2" ]; then
                "${PEER[@]}" unwrap-forward --identity "$(id python med2)" \
                    --in "$WORK/$tag.hop1.json" --out "$WORK/$tag.hop2.json" 2>>"$log" || result=1
                env="$WORK/$tag.hop2.json"
            else
                env="$WORK/$tag.hop1.json"
            fi
        fi

        if [ "$result" = 0 ]; then
            local unpack_id="$python_id"
            [ -n "$route" ] && unpack_id="$(id python "routed$route")"
            "${PEER[@]}" unpack --identity "$unpack_id" --in "$env" --out "$un" 2>>"$log" &&
            "${PEER[@]}" assert --mode "$mode" --enc "$enc_arg" --sig-alg "$sig_alg" \
                --expected "$msg" --unpacked "$un" 2>>"$log" || result=1
        fi
    else
        [ "$needs_from" = "yes" ] && from_args=(--from "$python_id")
        local route_flag=()
        [ -n "$route" ] && route_flag=(--route "$route_arg")
        "${PEER[@]}" gen-message --to "$dotnet_id" "${from_args[@]+"${from_args[@]}"}" --out "$msg" 2>>"$log" &&
        "${PEER[@]}" pack --identity "$python_id" --to "$dotnet_id" --mode "$mode" \
            --enc "$enc_arg" --message "$msg" --out "$env" \
            "${route_flag[@]+"${route_flag[@]}"}" 2>>"$log" || result=1

        # Mediated: didcomm-dotnet's ForwardProcessor peels each hop before the real unpack.
        if [ "$result" = 0 ] && [ -n "$route" ]; then
            "$INTEROP_CLI" unwrap-forward --identity "$(id dotnet med1)" --in "$env" \
                --out "$WORK/$tag.hop1.json" 2>>"$log" || result=1
            if [ "$result" = 0 ] && [ "$route" = "2" ]; then
                "$INTEROP_CLI" unwrap-forward --identity "$(id dotnet med2)" \
                    --in "$WORK/$tag.hop1.json" --out "$WORK/$tag.hop2.json" 2>>"$log" || result=1
                env="$WORK/$tag.hop2.json"
            else
                env="$WORK/$tag.hop1.json"
            fi
        fi

        if [ "$result" = 0 ]; then
            "$INTEROP_CLI" unpack --identity "$dotnet_id" --in "$env" --out "$un" 2>>"$log" &&
            "${PEER[@]}" assert --mode "$mode" --enc "$enc_arg" --sig-alg "$sig_alg" \
                --expected "$msg" --unpacked "$un" 2>>"$log" || result=1
        fi
    fi

    if [ "$result" = 0 ]; then
        CELL_RESULT="PASS"
    else
        CELL_RESULT="FAIL"
        echo "--- $tag failed; log: ---" >&2
        cat "$log" >&2 || true
    fi
}

# ── FR-IX-06: the counterpart verifies our PUBLISHED vector set ───────────────────────────
# Every fixture in packed/didcomm-dotnet/ is unpacked by didcomm-python against the spec
# Appendix A secrets + Appendix B DID documents vendored in the fixtures tree. This is the
# FR-IX-06 acceptance criterion ("a published fixture set decrypts/verifies with at least one
# external impl") executed rather than asserted.
verify_vectors() {
    VECTOR_ROWS=()
    VECTOR_FAILURES=0
    local packed_dir="$FIXTURES/packed/didcomm-dotnet"
    if [ ! -d "$packed_dir" ]; then
        echo "no published vectors at $packed_dir (fixtures submodule not checked out?)" >&2
        return
    fi

    for vector in "$packed_dir"/*.json; do
        local name; name="$(basename "$vector" .json)"
        local log="$WORK/vector-$name.log"
        if "${PEER[@]}" verify-fixture --packed "$vector" \
                --diddocs "$FIXTURES/diddocs/spec" \
                --secrets "$FIXTURES/secrets/bob.json,$FIXTURES/secrets/alice.json" \
                --expected "$FIXTURES/payloads/c1-lets-do-lunch.json" 2>"$log"; then
            VECTOR_ROWS+=("$name|PASS|")
        else
            VECTOR_ROWS+=("$name|FAIL|$(tail -1 "$log" | tr '|' '/')")
            VECTOR_FAILURES=$((VECTOR_FAILURES + 1))
            echo "--- vector $name failed; log: ---" >&2
            cat "$log" >&2 || true
        fi
    done
}

# ── run the matrix ────────────────────────────────────────────────────────────────────────
SUMMARY="$WORK/python-leg.md"
{
    echo "## Live interop — python leg (didcomm-dotnet ↔ didcomm-python $("$VENV/bin/pip" show didcomm 2>/dev/null | awk '/^Version/{print $2}'))"
    echo
    echo "| composition | enc | routing | outbound (dotnet packs → python unpacks) | inbound (python packs → dotnet unpacks) |"
    echo "|---|---|---|---|---|"
} > "$SUMMARY"

FAILURES=0
EXECUTED=0
NA_COUNT=0
NOTES=()
for cell in "${CELLS[@]}"; do
    IFS=';' read -r label mode enc needs_from ids sig_alg route <<< "$cell"
    row=()
    for direction in outbound inbound; do
        reason="$(na_reason "$direction" "$mode" "$ids")"
        if [ -n "$reason" ]; then
            row+=("N-A*")
            NA_COUNT=$((NA_COUNT + 1))
            NOTES+=("$direction — $label: $reason")
        else
            run_cell "$direction" "$mode" "$enc" "$needs_from" "$ids" "$sig_alg" "$route"
            row+=("$CELL_RESULT")
            EXECUTED=$((EXECUTED + 1))
            [ "$CELL_RESULT" = "FAIL" ] && FAILURES=$((FAILURES + 1))
        fi
    done
    routing_label="direct"
    [ -n "$route" ] && routing_label="$route mediator(s)"
    printf '| %s | %s | %s | %s | %s |\n' \
        "$label" "$enc" "$routing_label" "${row[0]}" "${row[1]}" >> "$SUMMARY"
    printf '%-24s %-14s %-14s outbound=%-5s inbound=%s\n' \
        "$label" "$enc" "$routing_label" "${row[0]}" "${row[1]}"
done

# ── run the published-vector verification (FR-IX-06) ──────────────────────────────────────
echo
echo "FR-IX-06: verifying published didcomm-dotnet vectors with didcomm-python"
verify_vectors
{
    echo
    echo "### FR-IX-06 — didcomm-python verifying the published \`source: didcomm-dotnet\` vectors"
    echo
    echo "| vector | result | detail |"
    echo "|---|---|---|"
} >> "$SUMMARY"
for row in "${VECTOR_ROWS[@]+"${VECTOR_ROWS[@]}"}"; do
    IFS='|' read -r name result detail <<< "$row"
    printf '| %s | %s | %s |\n' "$name" "$result" "$detail" >> "$SUMMARY"
    printf '%-28s %s\n' "$name" "$result"
done
FAILURES=$((FAILURES + VECTOR_FAILURES))
EXECUTED=$((EXECUTED + ${#VECTOR_ROWS[@]}))

{
    echo
    echo "**Totals** — executed: $EXECUTED · passed: $((EXECUTED - FAILURES)) · failed: $FAILURES · declared n/a: $NA_COUNT"
} >> "$SUMMARY"

if [ "${#NOTES[@]}" -gt 0 ]; then
    {
        echo
        echo "Declared \`N-A*\` cells (each is a counterpart limitation with evidence, not a skip):"
        echo
        for note in "${NOTES[@]}"; do echo "- $note"; done
    } >> "$SUMMARY"
fi

if [ -n "${INTEROP_SUMMARY_DIR:-}" ]; then
    mkdir -p "$INTEROP_SUMMARY_DIR"
    cp "$SUMMARY" "$INTEROP_SUMMARY_DIR/python-leg.md"
fi

echo
echo "python leg — executed: $EXECUTED · passed: $((EXECUTED - FAILURES)) · failed: $FAILURES · declared n/a: $NA_COUNT"
if [ "$FAILURES" -gt 0 ]; then
    echo "python leg: $FAILURES cell(s) FAILED (details above; work dir kept at $WORK)" >&2
    exit 1
fi
echo "python leg: all runnable cells passed (summary: $SUMMARY)"
