#!/usr/bin/env bash
#
# Live cross-implementation interop, JVM leg (PRD §13.6 FR-IX-04/05/06).
#
# Round-trips DIDComm v2 envelopes between didcomm-dotnet (tools/InteropCli, the real
# DidCommClient + net-did resolver) and SICPA's didcomm-jvm (org.didcommx:didcomm 0.3.2,
# pinned by SHA-256 in jvm/fetch-deps.sh, driven by jvm/src/InteropPeer.java — plain
# javac/java, no Gradle/Maven) — both directions, across the §13.5 matrix dimensions the pair
# supports:
#
#   OUTBOUND (FR-IX-04, MUST):  didcomm-dotnet packs → didcomm-jvm unpacks
#   INBOUND  (FR-IX-05, SHOULD): didcomm-jvm packs → didcomm-dotnet unpacks
#   VECTORS  (FR-IX-06, MUST):  didcomm-jvm unpacks our PUBLISHED fixture set
#
# Matrix coverage (§13.5): envelope composition × content encryption × key-agreement curve
# (X25519, P-256) × signing alg (EdDSA, ES256, ES256K) × recipients (single, 3) × routing
# (direct, 1 mediator, 2 mediators) × DID method (did:peer:2, did:key).
#
# Every cell asserts the recovered plaintext equals the original payload AND that the unpack
# metadata matches what the composition requires. Prints a per-cell PASS/FAIL/N-A table,
# writes it to $INTEROP_SUMMARY_DIR/jvm-leg.md (when set), and exits nonzero on any FAIL.
#
# N-A cells are declared, not skipped silently — each carries an evidence-backed reason (see
# README.md, "Known counterpart deviations"), and an N-A is retired as soon as its stated
# cause is gone. Cells that execute and FAIL stay red rather than being reclassified: when the
# outbound signed family broke on didcomm-dotnet's own JWS `kid` placement it was left red
# until DataProofsDotnet.Jose 1.3.0 fixed it upstream, because the defect was ours, not a
# counterpart limitation.
#
# Usage: bash tools/interop-live/run-jvm-leg.sh
#   JAVA_HOME         JDK to use (default: java/javac on PATH; validated on 17 and 25)
#   INTEROP_WORK      scratch dir (default: mktemp -d; kept for debugging)
#   INTEROP_SUMMARY_DIR  where to also write the summary table markdown (optional)
#   INTEROP_CLI       prebuilt InteropCli binary (default: build Release from source)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
FIXTURES="$REPO_ROOT/tests/DidComm.InteropTests/fixtures"
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
# One identity pair per matrix axis. All three drivers mint the SAME identity-file schema
# ({did, secrets: [private JWKs]}) and number did:peer:2 keys identically, so either side can
# resolve the other's DID with no shared state beyond the DID string itself.
mint_pair() { # $1=name, rest: shared mint flags
    local name="$1"; shift
    "$INTEROP_CLI" mint --out "$WORK/dotnet-$name.json" "$@" >/dev/null
    "${PEER[@]}" mint --out "$WORK/jvm-$name.json" "$@" >/dev/null
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
"${PEER[@]}" mint --out "$WORK/jvm-routed1.json" \
    --route "$WORK/jvm-med1.json" >/dev/null
"${PEER[@]}" mint --out "$WORK/jvm-routed2.json" \
    --route "$WORK/jvm-med1.json,$WORK/jvm-med2.json" >/dev/null

id() { echo "$WORK/$1-$2.json"; }   # id <dotnet|jvm> <name>

# ── matrix cells ──────────────────────────────────────────────────────────────────────────
# "<label>;<mode>;<enc>;<needs_from>;<ids>;<sig_alg>;<route>"  (identical shape to the python leg)
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
# HISTORY: the outbound `signed` and `anoncrypt(sign)` families used to be declared here.
# Neither is any more — both root causes were didcomm-dotnet's and both are fixed upstream
# (dataproofs-dotnet#17 kid-disjointness, #25 kid placement; DataProofsDotnet.Jose 1.3.0 also
# emits the General JWS serialization at every signer count). Those six cells now execute and
# pass. See README.md, "Known didcomm-dotnet defect (fixed upstream)".
na_reason() { # $1=direction $2=mode $3=ids → reason or empty
    local direction="$1" mode="$2" ids="$3"

    # secp256k1 is absent from the JDK, not from DIDComm: JDK 16 removed it from SunEC, and
    # didcomm-jvm 0.3.2 bundles no BouncyCastle, so the counterpart stack cannot do secp256k1
    # at all on any JDK >= 16 (validated on 25). ES256/EdDSA are unaffected — this is a curve
    # availability gap in the JRE, nothing about the ES256K envelope format. Both directions
    # are declared, each reproduced verbatim:
    #
    #   inbound  (didcomm-jvm signs)  — "UnsupportedAlgorithm: The algorithm Unsupported
    #       signature algorithm is not supported", caused by "java.security.SignatureException:
    #       Curve not supported: java.security.spec.ECParameterSpec@..".
    #   outbound (didcomm-jvm verifies) — the same missing curve makes nimbus's ECDSAVerifier
    #       CATCH that SignatureException and return false rather than throw, so didcomm-jvm
    #       reports a perfectly valid signature as "MalformedMessageException: Invalid
    #       signature" (JWS.kt:81) — a silent false negative that looks like tampering. This is
    #       not about our bytes: the VENDORED SPEC vector
    #       packed/spec/test-signed-didcomm-message-alice-key-3.json fails identically, while
    #       its key-1 (EdDSA) and key-2 (ES256) siblings verify clean through the same path.
    if [ "$ids" = "es256k" ]; then
        echo "secp256k1 was removed from SunEC in JDK 16 and didcomm-jvm 0.3.2 ships no BouncyCastle, so the counterpart JRE can neither sign nor verify ES256K (java.security.SignatureException: Curve not supported; on verify nimbus swallows it and reports \"Invalid signature\")"
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

    local dotnet_id jvm_id
    dotnet_id="$(id dotnet "$ids")"
    jvm_id="$(id jvm "$ids")"

    # Multi-recipient cells address three distinct DIDs; every other cell addresses one.
    local to_jvm="$jvm_id"
    if [ "$ids" = "multi" ]; then
        to_jvm="$(id jvm multi),$(id jvm base),$(id jvm keyx)"
    fi
    # Outbound mediation targets the routed jvm DID (whose .S segment carries routingKeys);
    # inbound mediation has the jvm side wrap explicitly for the dotnet-side mediators.
    local route_arg=""
    if [ -n "$route" ]; then
        to_jvm="$(id jvm "routed$route")"
        route_arg="$(id dotnet med1)"
        [ "$route" = "2" ] && route_arg="$(id dotnet med1),$(id dotnet med2)"
    fi

    local from_args=()
    local result=0
    if [ "$direction" = "outbound" ]; then
        [ "$needs_from" = "yes" ] && from_args=(--from "$dotnet_id")
        local forward_args=()
        [ -n "$route" ] && forward_args=(--forward true)
        "${PEER[@]}" gen-message --to "$to_jvm" "${from_args[@]+"${from_args[@]}"}" --out "$msg" 2>>"$log" &&
        "$INTEROP_CLI" pack --identity "$dotnet_id" --to "$to_jvm" --mode "$mode" \
            --enc "$enc_arg" --message "$msg" --out "$env" \
            "${forward_args[@]+"${forward_args[@]}"}" 2>>"$log" || result=1

        # Mediated: didcomm-dotnet built the forward onion; the jvm-side mediators peel it.
        if [ "$result" = 0 ] && [ -n "$route" ]; then
            "${PEER[@]}" unwrap-forward --identity "$(id jvm med1)" --in "$env" \
                --out "$WORK/$tag.hop1.json" 2>>"$log" || result=1
            if [ "$result" = 0 ] && [ "$route" = "2" ]; then
                "${PEER[@]}" unwrap-forward --identity "$(id jvm med2)" \
                    --in "$WORK/$tag.hop1.json" --out "$WORK/$tag.hop2.json" 2>>"$log" || result=1
                env="$WORK/$tag.hop2.json"
            else
                env="$WORK/$tag.hop1.json"
            fi
        fi

        if [ "$result" = 0 ]; then
            local unpack_id="$jvm_id"
            [ -n "$route" ] && unpack_id="$(id jvm "routed$route")"
            "${PEER[@]}" unpack --identity "$unpack_id" --in "$env" --out "$un" 2>>"$log" &&
            "${PEER[@]}" assert --mode "$mode" --enc "$enc_arg" --sig-alg "$sig_alg" \
                --expected "$msg" --unpacked "$un" 2>>"$log" || result=1
        fi
    else
        [ "$needs_from" = "yes" ] && from_args=(--from "$jvm_id")
        local route_flag=()
        [ -n "$route" ] && route_flag=(--route "$route_arg")
        "${PEER[@]}" gen-message --to "$dotnet_id" "${from_args[@]+"${from_args[@]}"}" --out "$msg" 2>>"$log" &&
        "${PEER[@]}" pack --identity "$jvm_id" --to "$dotnet_id" --mode "$mode" \
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
# Every fixture in packed/didcomm-dotnet/ is unpacked by didcomm-jvm against the spec Appendix
# A secrets + Appendix B DID documents vendored in the fixtures tree. This is the FR-IX-06
# acceptance criterion ("a published fixture set decrypts/verifies with at least one external
# impl") executed rather than asserted.

# Declared n/a for a single published vector, on the same terms as the matrix's na_reason:
# an exact name match, never a family or a wildcard, and a falsifiable stated cause.
vector_na_reason() { # $1=vector basename → reason or empty
    # The counterpart JRE cannot do secp256k1 AT ALL: JDK 16 removed it from SunEC and
    # didcomm-jvm 0.3.2 ships no BouncyCastle. On verify, nimbus's ECDSAVerifier CATCHES the
    # resulting "java.security.SignatureException: Curve not supported" and returns false
    # instead of throwing, so didcomm-jvm reports a valid ES256K signature as
    # "MalformedMessageException: Invalid signature" (JWS.kt:81).
    #
    # The CONTROL is what makes this falsifiable rather than a convenient excuse: the
    # DIDComm-published spec vector packed/spec/test-signed-didcomm-message-alice-key-3.json
    # — not our bytes — fails identically through the same code path, while its key-1 (EdDSA)
    # and key-2 (ES256) siblings verify clean. If a BouncyCastle provider is ever added, the
    # counterpart fixes this, or the leg moves to a JDK that still carries secp256k1, this
    # vector MUST start passing and this n/a must be deleted. A green run with this n/a still
    # in place is a bug in the harness, not a pass.
    if [ "$1" = "signed-es256k" ]; then
        echo "JDK 16 removed secp256k1 from SunEC and didcomm-jvm 0.3.2 ships no BouncyCastle, so nimbus swallows SignatureException: Curve not supported and reports a valid ES256K signature as invalid; control: the spec's own packed/spec/test-signed-didcomm-message-alice-key-3.json fails identically while its EdDSA/ES256 siblings verify clean"
        return
    fi
}

verify_vectors() {
    VECTOR_ROWS=()
    VECTOR_FAILURES=0
    VECTOR_NA=0
    VECTOR_NOTES=()
    local packed_dir="$FIXTURES/packed/didcomm-dotnet"
    if [ ! -d "$packed_dir" ]; then
        # FR-IX-06 is a MUST. A missing vector directory means the fixtures submodule was
        # not checked out (clone without --recurse-submodules, or a pin without that dir):
        # the external-verification step cannot run, so the leg must FAIL loudly rather than
        # pass with the check silently skipped and the tally quietly dropping ~16 cells.
        echo "FR-IX-06 vectors absent at $packed_dir — fixtures submodule not checked out." >&2
        echo "Run 'git submodule update --init' (CI/release checkouts use --recurse-submodules)." >&2
        VECTOR_FAILURES=1
        return
    fi

    # A present-but-empty vector directory is the same failure in different clothes: the leg
    # would go green having verified nothing. Refuse it explicitly (this also stops the
    # unmatched glob from reaching verify-fixture as a literal '*.json' path).
    local vectors=("$packed_dir"/*.json)
    if [ ! -e "${vectors[0]}" ]; then
        echo "FR-IX-06: no *.json vectors in $packed_dir — refusing to pass the leg without executing the MUST-level vector verification." >&2
        VECTOR_FAILURES=1
        return
    fi

    for vector in "${vectors[@]}"; do
        local name; name="$(basename "$vector" .json)"
        local log="$WORK/vector-$name.log"
        local reason; reason="$(vector_na_reason "$name")"
        if [ -n "$reason" ]; then
            VECTOR_ROWS+=("$name|N-A*|$reason")
            VECTOR_NA=$((VECTOR_NA + 1))
            VECTOR_NOTES+=("vector $name: $reason")
            continue
        fi
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
SUMMARY="$WORK/jvm-leg.md"
{
    echo "## Live interop — JVM leg (didcomm-dotnet ↔ didcomm-jvm 0.3.2)"
    echo
    echo "| composition | enc | routing | outbound (dotnet packs → jvm unpacks) | inbound (jvm packs → dotnet unpacks) |"
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
echo "FR-IX-06: verifying published didcomm-dotnet vectors with didcomm-jvm"
verify_vectors
{
    echo
    echo "### FR-IX-06 — didcomm-jvm verifying the published \`source: didcomm-dotnet\` vectors"
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
# Declared-n/a vectors are counted as n/a, not as executed — same bookkeeping as the matrix,
# so the tally never claims coverage the run did not actually have.
EXECUTED=$((EXECUTED + ${#VECTOR_ROWS[@]} - VECTOR_NA))
NA_COUNT=$((NA_COUNT + VECTOR_NA))
if [ "${#VECTOR_NOTES[@]}" -gt 0 ]; then NOTES+=("${VECTOR_NOTES[@]}"); fi

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
    cp "$SUMMARY" "$INTEROP_SUMMARY_DIR/jvm-leg.md"
fi

echo
echo "jvm leg — executed: $EXECUTED · passed: $((EXECUTED - FAILURES)) · failed: $FAILURES · declared n/a: $NA_COUNT"
if [ "$FAILURES" -gt 0 ]; then
    echo "jvm leg: $FAILURES cell(s) FAILED (details above; work dir kept at $WORK)" >&2
    exit 1
fi
echo "jvm leg: all runnable cells passed (summary: $SUMMARY)"
