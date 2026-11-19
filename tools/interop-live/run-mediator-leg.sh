#!/usr/bin/env bash
#
# Live interop, mediator leg (#98; PRD §12 Phase 7 exit criteria: an external-mediator leg green
# nightly against a pinned image; FR-MED-*, FR-PICK-*, FR-ROUTE-04, FR-TRN-13, FR-IX-08).
#
# Runs the Hyperledger Identus mediator v1.2.1 (digest-pinned, tools/interop-live/mediator/
# images.env) in Docker and drives it with three counterparts of our own:
#
#   MediatorWallet        a wallet built on DidComm.Mediator.Client only (no mediator package,
#                         no ASP.NET Core): enrols, registers, fetches, acknowledges
#   InteropCli            packs as a sender (FR-ROUTE-04 wrapping) and, as mediator-serve, runs a
#                         didcomm-dotnet mediator for the reverse cells
#   mediator_peer.py      didcomm-python 0.3.2: wraps forwards, and enrols / picks up as a
#                         recipient with Coordinate Mediation 2.0 and Message Pickup 3.0 by hand
#
# A second Identus instance is reachable only through a Squid forward proxy (deny by default),
# for the TrustedProxy cells and the "process proxy ignored" cell.
#
# Cells (gating unless noted; README.md "Mediator leg" has the full list): B-0 wallet boundary,
# M-1 dotnet recipient via Identus, M-2 burst of 12, M-3 dotnet sender to a python recipient,
# M-4 HTTP via TrustedProxy, M-5 WebSocket via TrustedProxy (transport level), M-6 process proxy
# ignored, R-1 python recipient via the dotnet mediator, R-2 the wallet against the dotnet
# mediator (attribution control). WebSocket exchanges against Identus are observations, never
# gating (Identus ignores binary frames; owner decision O-98-1). Observations are recorded in a
# separate table and in mediator-observations.json; they never fail the leg.
#
# Usage: bash tools/interop-live/run-mediator-leg.sh [--negative-controls | --up]
#   (default)            run every cell; exit 0 when all passed, 1 on any FAIL or set-up failure
#   --negative-controls  run each cell's sabotaged variants instead; exit 0 only when every one
#                        of them FAILED (a sabotage that passes means the cell cannot fail)
#   --up                 set everything up, print the endpoints, wait for Enter, tear down
#                        (development only; refused when CI=true)
# Exit codes: 0 as above · 1 a FAIL or a set-up failure · 2 environment unsuitable before
# anything ran (no Docker CLI or daemon, no curl / dotnet, Python outside CPython 3.9-3.12).
#
# Environment:
#   PYTHON               interpreter to seed the venv (required under GitHub Actions; locally
#                        python3 with a warning); CPython 3.9-3.12 only
#   INTEROP_SUMMARY_DIR  where mediator-leg.md, mediator-observations.json (and, on failure,
#                        mediator-logs/) are copied
#   INTEROP_CLI          prebuilt InteropCli binary (default: build Release from source)
#   MEDIATOR_LEG_OWNER   adds the label org.didcomm-dotnet.owner=<value> to every Docker object
# Development only (each refused when CI=true):
#   MEDIATOR_LEG_WORK    scratch dir (default: mktemp -d; kept for debugging; a venv in it is reused,
#                        and each run writes only into a new WORK/runs/<run id>)
#   MEDIATOR_LEG_IMAGES_ENV  images file to use instead of mediator/images.env
#   MEDIATOR_LEG_ONLY    comma-separated cell ids to run (e.g. M-3,R-1)
#   MEDIATOR_LEG_DEBUG_FIRST_PORT            first host port tried for Identus (port-clash test)
#   MEDIATOR_LEG_DEBUG_IDENTUS_MONGO_PASSWORD  wrong Mongo password for Identus (readiness test)
#   MEDIATOR_LEG_DEBUG_WATCHDOG              seconds for the leg watchdog (its test)
#
# The leg watchdog (a hang guard on the whole leg) is 1500 s for the cells and 900 s for
# --negative-controls, so both in one CI job stay inside its 60 minutes together with the
# python and jvm legs (about 3 minutes in CI).
#
# bash 3.2 compatible (macOS /bin/bash): no associative arrays, no {fd} redirections, no
# `wait -n`, no timeout(1). No fixed sleeps: every wait re-checks the exact condition it needs,
# with at most 0.2 s between checks, and real time is only a hang guard (a background sleep).

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
MED="$SCRIPT_DIR/mediator"
INITDB_BLOB="5d9bd69def43d27dc835a55ec936fe0c2de91cc7" # git blob of initdb.js at Identus v1.2.1

say() { echo "mediator leg: $*" >&2; }

MODE=gate
for arg in "$@"; do
    case "$arg" in
        --negative-controls) MODE=nc ;;
        --up) MODE=up ;;
        -h|--help) awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) say "unknown argument '$arg' (see --help)"; exit 2 ;;
    esac
done

if [ "${CI:-}" = "true" ]; then
    for var in MEDIATOR_LEG_ONLY MEDIATOR_LEG_WORK MEDIATOR_LEG_IMAGES_ENV MEDIATOR_LEG_DEBUG_FIRST_PORT \
               MEDIATOR_LEG_DEBUG_IDENTUS_MONGO_PASSWORD MEDIATOR_LEG_DEBUG_WATCHDOG; do
        if [ -n "${!var:-}" ]; then
            say "$var is a development setting and is refused when CI=true"
            exit 2
        fi
    done
    if [ "$MODE" = up ]; then say "--up is for local development and is refused when CI=true"; exit 2; fi
fi

# The leg's own stdout and stderr, kept on fds 8 and 9: the EXIT trap may run while a cell's
# output is redirected to its log, and its messages belong on the leg's stderr.
exec 8>&1 9>&2

# WORK holds what a later run may reuse (the venv) and one directory per run, RUN: every file a
# run writes or waits for (identities, keys, the mediator's FIFO and ready file, cell records,
# logs, the summary) lives in RUN, which must not exist yet, so nothing an earlier run left in
# the same WORK can be taken for this run's own.
RUN_ID="${GITHUB_RUN_ID:-local}-$$-$RANDOM"
WORK="${MEDIATOR_LEG_WORK:-$(mktemp -d "${TMPDIR:-/tmp}/didcomm-interop-mediator.XXXXXX")}"
RUN="$WORK/runs/$RUN_ID"
mkdir -p "$WORK/runs" || { say "cannot create $WORK/runs"; exit 2; }
mkdir "$RUN" 2>/dev/null || { say "the run directory $RUN already exists or cannot be made; refusing to reuse it"; exit 2; }
mkdir -p "$RUN/logs" "$RUN/cells" "$RUN/obs"
echo "work dir: $WORK (this run: $RUN)" >&2
SUMMARY="$RUN/mediator-leg.md"
OBS_JSON="$RUN/mediator-observations.json"
CELLS_TSV="$RUN/cells.tsv"
: > "$CELLS_TSV"

LEG_LABEL="org.didcomm-dotnet.interop-leg=mediator-$RUN_ID"
PREFIX="dcmed-$RUN_ID"
NET="$PREFIX-net"
DOCKER_LABELS=(--label "$LEG_LABEL")
[ -n "${MEDIATOR_LEG_OWNER:-}" ] && DOCKER_LABELS+=(--label "org.didcomm-dotnet.owner=$MEDIATOR_LEG_OWNER")

case "$MODE" in nc) LEG_WATCHDOG_S=900 ;; up) LEG_WATCHDOG_S=1800 ;; *) LEG_WATCHDOG_S=1500 ;; esac
LEG_WATCHDOG_S="${MEDIATOR_LEG_DEBUG_WATCHDOG:-$LEG_WATCHDOG_S}"

# The cells this mode runs, in order. "<id>|<sabotage>" (sabotage empty = the real cell). In
# --negative-controls mode M-1's "Identus stopped after the 202" runs last: it stops Identus.
GATE_PLAN="B-0| M-1| M-2| M-3| M-4| M-5| M-6| M-8| R-1| R-2|"
NC_PLAN="B-0|forbid-client M-1|pin-v3 M-1|unregistered-next M-1|wrong-message M-2|expect-13 M-3|no-forward M-4|no-proxy M-5|no-proxy M-5|ws-wrong-path M-6|with-trusted-proxy M-6|no-proxy-env M-6|proxy-in-b M-6|squid-request-in-window M-8|no-relax M-8|fetch-no-relax R-2|expect-v2 R-2|ws-wrong-path R-1|no-created-time R-1|v2-disabled M-1|identus-stopped"
case "$MODE" in nc) PLAN="$NC_PLAN" ;; *) PLAN="$GATE_PLAN" ;; esac
if [ -n "${MEDIATOR_LEG_ONLY:-}" ]; then
    filtered=""
    for entry in $PLAN; do
        case ",$MEDIATOR_LEG_ONLY," in *",${entry%%|*},"*) filtered="$filtered $entry" ;; esac
    done
    PLAN="$filtered"
    say "development run: only ${MEDIATOR_LEG_ONLY} (MEDIATOR_LEG_ONLY)"
fi
plan_count() { set -- $PLAN; echo $#; }
plan_needs() { # $1=cell id → 0 if the plan runs it
    local entry
    for entry in $PLAN; do [ "${entry%%|*}" = "$1" ] && return 0; done
    return 1
}
plan_needs_wallet() { plan_needs B-0 || plan_needs M-1 || plan_needs M-2 || plan_needs M-4 || plan_needs M-5 || plan_needs M-6 || plan_needs M-8 || plan_needs R-2; }

# ── hang guards and waits ─────────────────────────────────────────────────────────────────
# deadline_guard <seconds> <pid>: a background watchdog that TERMs (then, 5 s later, KILLs) the
# pid once the deadline passes. Its stdio is /dev/null, so it never holds a $(…) pipe open.
deadline_guard() {
    ( sleep "$1" & s=$!
      trap 'kill "$s" 2>/dev/null; exit 0' TERM
      wait "$s"
      kill -TERM "$2" 2>/dev/null
      sleep 5 & s=$!
      wait "$s"
      kill -KILL "$2" 2>/dev/null ) </dev/null >/dev/null 2>&1 8>&- 9>&- &
    GUARD_PID=$!
}
stop_guard() { kill -TERM "$1" 2>/dev/null; wait "$1" 2>/dev/null; return 0; }

# with_deadline <seconds> <command…>: run a simple command under a hang guard; its exit code
# (143 when the guard had to stop it). macOS has no timeout(1).
with_deadline() {
    local secs="$1"; shift
    "$@" &
    local pid=$!
    deadline_guard "$secs" "$pid"
    local guard=$GUARD_PID rc
    wait "$pid"; rc=$?
    stop_guard "$guard"
    return "$rc"
}

# wait_for <seconds> <what> <check…>: re-run the check until it succeeds (exit 0); exit 3 from
# the check gives up at once (the thing waited for died). At most 0.2 s between checks; the
# seconds are a hang guard (a background sleep that raises a flag), never a wait for a result.
# On failure WAIT_WHAT says what was not reached.
wait_for() {
    local secs="$1" what="$2"; shift 2
    local flag="$RUN/.deadline.$$.$RANDOM$RANDOM" rc
    ( sleep "$secs" & s=$!
      trap 'kill "$s" 2>/dev/null; exit 0' TERM
      wait "$s"
      : > "$flag" ) </dev/null >/dev/null 2>&1 8>&- 9>&- &
    local guard=$!
    while :; do
        "$@"; rc=$?
        [ "$rc" = 0 ] && break
        [ "$rc" = 3 ] && break
        if [ -e "$flag" ]; then rc=1; break; fi
        sleep 0.2
    done
    stop_guard "$guard"
    if [ "$rc" = 0 ]; then return 0; fi
    if [ "$rc" = 3 ]; then WAIT_WHAT="$what (gave up: it stopped)"; else WAIT_WHAT="$what (not within ${secs} s)"; fi
    return 1
}

# ── summary writing (also used by the early exits) ───────────────────────────────────────
VPY=""            # the venv interpreter, once it exists
DOCKER_OK=0       # 1 once the daemon answered: cleanup then removes our labelled objects
LEG_FAILED=0
SUMMARY_WRITTEN=0 # 1 once mediator-leg.md holds this run's outcome
CELLS_STARTED=0
HEADER=""
copy_summary() {
    if [ -n "${INTEROP_SUMMARY_DIR:-}" ]; then
        mkdir -p "$INTEROP_SUMMARY_DIR"
        # Both files always come from this run (an earlier run's JSON must not stand next to this
        # run's summary): a run that recorded nothing says so.
        [ -f "$OBS_JSON" ] || printf '{"mode": "%s", "cells": [], "observations": [], "note": "this run recorded no observations"}\n' "$MODE" >"$OBS_JSON"
        [ -f "$SUMMARY" ] && cp "$SUMMARY" "$INTEROP_SUMMARY_DIR/mediator-leg.md"
        cp "$OBS_JSON" "$INTEROP_SUMMARY_DIR/mediator-observations.json"
    fi
    return 0
}
summary_header() {
    echo "## Live interop — mediator leg (didcomm-dotnet ↔ Hyperledger Identus mediator, didcomm-python)"
    echo
}
env_unsuitable() { # exit 2: nothing ran
    say "$*"
    { summary_header; echo "**Leg not run (environment unsuitable: $*)** — cells not run: $(plan_count)"; } > "$SUMMARY"
    SUMMARY_WRITTEN=1
    copy_summary
    exit 2
}
# redact: stdin → stdout with this run's private values replaced (Identus logs its per-run private
# keys at start-up; the Mongo root password is per run). Used for every log or log tail that
# leaves the work dir.
redact() {
    local args=() value
    for value in $(cat "$RUN/identus-keys.env" "$RUN/identus-px-keys.env" "$RUN/mongo-root.env" 2>/dev/null |
                   grep -E '^(KEY_[A-Z]+_D|MONGO_INITDB_ROOT_PASSWORD)=' | cut -d= -f2); do
        args+=(-e "s/$value/<redacted>/g")
    done
    if [ "${#args[@]}" -gt 0 ]; then sed "${args[@]}"; else cat; fi
}
setup_fail() { # $1=step $2=reason → exit 1
    local reason; reason="$(printf '%s' "$2" | redact | tr '\n\t|' '  /' | cut -c1-400)"
    say "set-up failed at $1: $reason"
    { summary_header; echo "**Leg aborted at set-up ($1: $reason)** — cells not run: $(plan_count)"; } > "$SUMMARY"
    SUMMARY_WRITTEN=1
    LEG_FAILED=1
    exit 1
}

# ── cleanup ───────────────────────────────────────────────────────────────────────────────
SERVE_PID=""
WATCHDOG_PID=""
# The dotnet mediator (started later, for R-1 / R-2) stops on stdin EOF: close fd 7, wait 15 s,
# then SIGTERM, then SIGKILL.
serve_gone() { ! kill -0 "$SERVE_PID" 2>/dev/null; }
stop_serve() {
    [ -n "$SERVE_PID" ] || return 0
    { exec 7>&-; } 2>/dev/null
    if ! wait_for 15 "mediator-serve exit after stdin EOF" serve_gone; then
        kill -TERM "$SERVE_PID" 2>/dev/null
        wait_for 5 "mediator-serve exit after SIGTERM" serve_gone || kill -KILL "$SERVE_PID" 2>/dev/null
    fi
    wait "$SERVE_PID" 2>/dev/null
    SERVE_PID=""
    return 0
}
# render_summary: the cells table, observations and totals → mediator-leg.md (+ JSON); the
# totals line(s) in $RUN/totals.txt.
render_summary() {
    "$VPY" -I "$MED/render_summary.py" render --mode "$MODE" --cells "$CELLS_TSV" --obs-dir "$RUN/obs" \
        --wallet-obs "$RUN/cells/observations/wallet-observations.json" --md "$SUMMARY" --json "$OBS_JSON" \
        --header "$HEADER" >"$RUN/totals.txt" 2>>"$RUN/logs/render.log"
}
cleanup() {
    local rc=$?
    # No second INT/TERM may stop the removal half-way; EXIT is cleared so the exit below ends it.
    trap - EXIT
    trap '' INT TERM
    exec 1>&8 2>&9
    [ -n "$WATCHDOG_PID" ] && stop_guard "$WATCHDOG_PID"
    [ -s "$RUN/logs/watchdog.log" ] && cat "$RUN/logs/watchdog.log" >&2
    stop_serve
    # Whatever still runs under the leg (a command a signal or the watchdog cut short, and its
    # hang guard) stops now, so nothing outlives the leg.
    command -v pkill >/dev/null 2>&1 && pkill -TERM -P $$ 2>/dev/null
    if [ "$DOCKER_OK" = 1 ]; then
        local id name
        for id in $(docker ps -aq --filter "label=$LEG_LABEL" 2>/dev/null); do
            name="$(docker inspect -f '{{.Name}}' "$id" 2>/dev/null | sed 's#^/##')"
            docker logs "$id" >"$RUN/logs/${name:-$id}.log" 2>&1
            case "$name" in *-squid)
                docker exec "$id" cat /var/log/squid/harness-access.log >"$RUN/logs/$name.access.log" 2>/dev/null ;;
            esac
        done
        local ids; ids="$(docker ps -aq --filter "label=$LEG_LABEL" 2>/dev/null)"
        [ -n "$ids" ] && docker rm -f -v $ids >/dev/null 2>&1
        local nets; nets="$(docker network ls -q --filter "label=$LEG_LABEL" 2>/dev/null)"
        [ -n "$nets" ] && docker network rm $nets >/dev/null 2>&1
        if [ -n "$(docker ps -aq --filter "label=$LEG_LABEL" 2>/dev/null)$(docker network ls -q --filter "label=$LEG_LABEL" 2>/dev/null)" ]; then
            say "WARNING: objects labelled $LEG_LABEL are left behind (docker ps -a / network ls --filter label=$LEG_LABEL)"
        fi
    fi
    [ "$rc" = 0 ] || LEG_FAILED=1
    if [ "$SUMMARY_WRITTEN" = 0 ]; then
        # Stopped before its summary (a signal, the watchdog, a crash): say so, with the cells that
        # completed, so the artifact never shows a stale or missing outcome.
        if [ "$CELLS_STARTED" = 1 ] && [ -n "$VPY" ] && render_summary; then
            echo >>"$SUMMARY"
            echo "**Leg stopped (exit $rc) before it finished** — the table lists only the cells that completed." >>"$SUMMARY"
        else
            { summary_header; echo "**Leg stopped (exit $rc) during set-up** — cells not run: $(plan_count)"; } >"$SUMMARY"
        fi
    fi
    if [ "$LEG_FAILED" = 1 ] && [ -n "${INTEROP_SUMMARY_DIR:-}" ]; then
        # The uploaded logs go out with this run's private values redacted.
        # One directory per run, so logs of an earlier failed run in the same summary dir stay apart.
        local file logs="$INTEROP_SUMMARY_DIR/mediator-logs/$RUN_ID"
        mkdir -p "$logs"
        for file in "$RUN"/logs/*.log "$RUN"/serve-*/stderr.log; do
            [ -f "$file" ] || continue
            redact <"$file" >"$logs/$(basename "$(dirname "$file")")-$(basename "$file")"
        done
        rmdir "$logs" "$INTEROP_SUMMARY_DIR/mediator-logs" 2>/dev/null # nothing to upload (e.g. Docker absent)
    fi
    copy_summary
    exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# ── pre-flight (exit 2: environment unsuitable) ───────────────────────────────────────────
command -v docker >/dev/null 2>&1 || env_unsuitable "no docker CLI on PATH"
command -v curl >/dev/null 2>&1 || env_unsuitable "no curl on PATH"
command -v dotnet >/dev/null 2>&1 || env_unsuitable "no dotnet on PATH"
command -v git >/dev/null 2>&1 || env_unsuitable "no git on PATH"
DOCKER_SERVER="$(with_deadline 30 docker info --format '{{.ServerVersion}}' 2>"$RUN/logs/docker-info.err")"
case $? in
    0) ;;
    143) env_unsuitable "the Docker daemon did not answer within 30 s" ;;
    *) env_unsuitable "the Docker daemon is unreachable ($(tail -1 "$RUN/logs/docker-info.err" 2>/dev/null))" ;;
esac
[ -n "$DOCKER_SERVER" ] || env_unsuitable "the Docker daemon gave no version"
DOCKER_OK=1
say "Docker server $DOCKER_SERVER"

# Interpreter guard, as run-python-leg.sh: the pinned didcomm-python 0.3.2 stack imports only on
# CPython 3.9-3.12 (pydantic v1 breaks at import on 3.13+). Under GitHub Actions PYTHON must name
# the interpreter the workflow installed (#120: never whatever python3 is first on PATH).
if [ -z "${PYTHON:-}" ]; then
    [ "${GITHUB_ACTIONS:-}" = "true" ] && env_unsuitable "PYTHON is not set (under GitHub Actions the workflow must pass the setup-python interpreter)"
    PYTHON=python3
    say "warning: PYTHON is not set; using python3 (set PYTHON=python3.12 when python3 is 3.13+)"
fi
VENV="$WORK/venv"
check_python() { # $1=interpreter → exit 2 unless it is CPython 3.9-3.12
    local version found
    version="$("$1" -c 'import sys; print("%d.%d" % sys.version_info[:2])' 2>/dev/null)" || version=""
    case "$version" in
        3.9|3.10|3.11|3.12) return ;;
    esac
    found="Python $version"
    [ -n "$version" ] || found="not runnable"
    echo "mediator leg: '$1' is $found; the pinned didcomm-python 0.3.2 stack imports only on CPython 3.9-3.12 (pydantic v1 breaks at import on 3.13+)." >&2
    echo "Rerun with a supported interpreter, e.g. PYTHON=python3.12 bash tools/interop-live/run-mediator-leg.sh" >&2
    env_unsuitable "Python '$1' is $found, outside CPython 3.9-3.12"
}
if [ -x "$VENV/bin/python" ]; then check_python "$VENV/bin/python"; else check_python "$PYTHON"; fi

LEFTOVERS="$(docker ps -a --filter label=org.didcomm-dotnet.interop-leg --format '{{.Names}}' 2>/dev/null | grep '^dcmed-' || true)"
[ -n "$LEFTOVERS" ] && say "warning: containers of an earlier mediator leg are still present (not touched): $(echo $LEFTOVERS)"

# Leg watchdog: the whole leg is a hang guard of LEG_WATCHDOG_S seconds (see the header).
# It TERMs the leg, then the leg's children, so a hung foreground command returns and the EXIT
# trap's cleanup runs (it never KILLs the leg: that would skip the cleanup).
leg_watchdog() {
    ( sleep "$2" & s=$!
      trap 'kill "$s" 2>/dev/null; exit 0' TERM
      wait "$s"
      echo "mediator leg: the $2 s watchdog fired; stopping the leg" >"$RUN/logs/watchdog.log"
      kill -TERM "$1" 2>/dev/null
      command -v pkill >/dev/null 2>&1 && pkill -TERM -P "$1" ) </dev/null >/dev/null 2>&1 8>&- 9>&- &
    WATCHDOG_PID=$!
}
[ "$MODE" != up ] && leg_watchdog $$ "$LEG_WATCHDOG_S"

# ── set-up: venv, tools, files ────────────────────────────────────────────────────────────
STEP_T0=$SECONDS
if [ ! -x "$VENV/bin/python" ]; then
    "$PYTHON" -m venv "$VENV" || setup_fail venv "python -m venv failed"
    "$VENV/bin/pip" install --quiet --disable-pip-version-check -r "$SCRIPT_DIR/python/requirements.txt" ||
        setup_fail venv "pip install -r python/requirements.txt failed"
fi
VPY="$VENV/bin/python"
PEER=("$VPY" "$MED/python/mediator_peer.py")
IPEER=("$VPY" "$SCRIPT_DIR/python/interop_peer.py")
PY_VERSION="$("$VPY" -c 'import platform, sys; print(platform.python_implementation(), platform.python_version(), "from", sys.base_prefix)')"
DIDCOMM_PY_VERSION="$("$VENV/bin/pip" show didcomm 2>/dev/null | awk '/^Version/{print $2}')"
say "python: $PY_VERSION; didcomm-python $DIDCOMM_PY_VERSION"

json_get() { # $1=file $2=dotted.path → the value (JSON for objects/lists), empty when absent
    "$VPY" -I -c 'import json, sys
try:
    d = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(1)
for k in sys.argv[2].split("."):
    if isinstance(d, list) and k.isdigit() and int(k) < len(d):
        d = d[int(k)]
    elif isinstance(d, dict):
        d = d.get(k)
    else:
        d = None
print("" if d is None else json.dumps(d) if isinstance(d, (dict, list)) else str(d).lower() if isinstance(d, bool) else d)' "$1" "$2" 2>/dev/null
}

if [ -z "${INTEROP_CLI:-}" ]; then
    # 8>&- 9>&-: the build servers dotnet leaves running must not inherit the leg's output (fds 8, 9),
    # or whoever reads the leg's output would wait for them.
    dotnet build "$REPO_ROOT/tools/InteropCli/InteropCli.csproj" -c Release --nologo -v quiet >"$RUN/logs/build-interopcli.log" 2>&1 8>&- 9>&- ||
        setup_fail "build InteropCli" "$(grep -m1 -E 'error' "$RUN/logs/build-interopcli.log")"
    INTEROP_CLI="$REPO_ROOT/tools/InteropCli/bin/Release/net10.0/InteropCli"
fi
WALLET_PROJECT="$MED/MediatorWallet/MediatorWallet.csproj"
WALLET_BIN_DIR="$MED/MediatorWallet/bin/Release/net10.0"
WALLET_BIN="$WALLET_BIN_DIR/MediatorWallet"
if plan_needs_wallet; then
    # Built here, not in run-all.sh, so a wallet build break fails this leg only.
    dotnet build "$WALLET_PROJECT" -c Release --nologo -v quiet >"$RUN/logs/build-wallet.log" 2>&1 8>&- 9>&- ||
        setup_fail "build MediatorWallet" "$(grep -m1 -E 'error|MSB' "$RUN/logs/build-wallet.log")"
fi

[ "$(git hash-object "$MED/identus/initdb.js")" = "$INITDB_BLOB" ] ||
    setup_fail initdb.js "mediator/identus/initdb.js is not the vendored Identus v1.2.1 file (git blob $INITDB_BLOB)"

IMAGES_ENV="${MEDIATOR_LEG_IMAGES_ENV:-$MED/images.env}"
[ -f "$IMAGES_ENV" ] || setup_fail images "no images file at $IMAGES_ENV"
IDENTUS_IMAGE="" MONGO_IMAGE="" SQUID_IMAGE=""
REF_RE='^[a-z0-9][a-z0-9./_-]*:[A-Za-z0-9._-]+@sha256:[0-9a-f]{64}$'
while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in ''|'#'*) continue ;; esac
    key="${line%%=*}" value="${line#*=}"
    [[ $value =~ $REF_RE ]] || setup_fail images "$key in $IMAGES_ENV is not pinned as repo:tag@sha256:<digest> ($value)"
    case "$key" in
        IDENTUS_IMAGE) IDENTUS_IMAGE="$value" ;;
        MONGO_IMAGE) MONGO_IMAGE="$value" ;;
        SQUID_IMAGE) SQUID_IMAGE="$value" ;;
        *) setup_fail images "unknown key $key in $IMAGES_ENV" ;;
    esac
done < "$IMAGES_ENV"
for ref in "$IDENTUS_IMAGE" "$MONGO_IMAGE" "$SQUID_IMAGE"; do
    [ -n "$ref" ] || setup_fail images "$IMAGES_ENV must name IDENTUS_IMAGE, MONGO_IMAGE and SQUID_IMAGE"
done

# Pull by tag@digest (3 attempts, each ≤ 600 s), then require the pinned digest in RepoDigests.
pull_verified() {
    local ref="$1" digest="${1##*@}" attempt
    for attempt in 1 2 3; do
        if with_deadline 600 docker pull --quiet "$ref" >>"$RUN/logs/pull.log" 2>&1; then
            if docker image inspect --format '{{json .RepoDigests}}' "$ref" 2>/dev/null | grep -F "@$digest\"" >/dev/null; then
                return 0
            fi
            echo "RepoDigests of $ref do not list $digest" >>"$RUN/logs/pull.log"
            return 1
        fi
        say "pull attempt $attempt of $ref failed"
    done
    return 1
}
for ref in "$IDENTUS_IMAGE" "$MONGO_IMAGE" "$SQUID_IMAGE"; do
    pull_verified "$ref" || setup_fail pull "pull failed: $ref ($(tail -1 "$RUN/logs/pull.log" 2>/dev/null))"
done
say "images pulled and digest-verified ($((SECONDS - STEP_T0)) s so far)"

random_hex() { "$VPY" -I -c 'import secrets; print(secrets.token_hex(16))'; }

# Identities the cells share: the python sender, a dotnet sender, the dotnet mediator.
"${IPEER[@]}" mint --out "$RUN/python-base.json" >/dev/null 2>>"$RUN/logs/mint.log" || setup_fail mint "python mint failed"
"$INTEROP_CLI" mint --out "$RUN/dotnet-base.json" >/dev/null 2>>"$RUN/logs/mint.log" || setup_fail mint "InteropCli mint failed"
# The dotnet mediator's DID names an https endpoint: FR-MED-07 checks only the scheme at start-up;
# nothing dials it (mediator-serve listens on 127.0.0.1 and reports the real URL in ready.json).
"$INTEROP_CLI" mint --out "$RUN/dotnet-medsvc.json" --endpoint https://mediator.invalid/didcomm >/dev/null 2>>"$RUN/logs/mint.log" ||
    setup_fail mint "InteropCli mint (mediator) failed"

# ── containers ────────────────────────────────────────────────────────────────────────────
docker network create "${DOCKER_LABELS[@]}" "$NET" >/dev/null 2>"$RUN/logs/network.err" ||
    setup_fail network "$(tail -1 "$RUN/logs/network.err")"

# Mongo root credentials are per run; the application user (admin/admin in database mediator)
# is the one the vendored initdb.js creates. Mongo has no host port: only Identus reaches it.
umask 077
printf 'MONGO_INITDB_ROOT_USERNAME=admin\nMONGO_INITDB_ROOT_PASSWORD=%s\nMONGO_INITDB_DATABASE=mediator\n' "$(random_hex)" >"$RUN/mongo-root.env"
umask 022
MONGO_EVAL='const n = db.getCollectionNames(); print(["user.account", "messages", "messages.outbound"].every(c => n.includes(c)) ? "ready" : "missing")'
start_mongo() { # $1=role (alias on the network)
    docker run -d --name "$PREFIX-$1" "${DOCKER_LABELS[@]}" --network "$NET" --network-alias "$1" \
        --tmpfs /data/db --tmpfs /data/configdb --env-file "$RUN/mongo-root.env" \
        -v "$MED/identus/initdb.js:/docker-entrypoint-initdb.d/initdb.js:ro" \
        "$MONGO_IMAGE" --auth >/dev/null 2>"$RUN/logs/$1.run.err" ||
        setup_fail "$1" "docker run failed: $(tail -1 "$RUN/logs/$1.run.err")"
}
container_state() { docker inspect -f '{{.State.Status}}' "$1" 2>/dev/null; }
mongo_ready() { # $1=container
    case "$(container_state "$1")" in running) ;; exited|dead) return 3 ;; *) return 1 ;; esac
    docker logs "$1" 2>&1 | grep -F 'MongoDB init process complete; ready for start up.' >/dev/null || return 1
    [ "$(with_deadline 20 docker exec "$1" mongosh --quiet \
        "mongodb://admin:admin@127.0.0.1:27017/mediator?authSource=mediator" --eval "$MONGO_EVAL" 2>/dev/null)" = ready ]
}
start_mongo mongo
start_mongo mongo-px

# Identus instances: per-run keys (never the compose demo keys), kept out of `ps` in an env file.
"${PEER[@]}" okp-keys --out "$RUN/identus-keys.env" 2>>"$RUN/logs/mint.log" || setup_fail keys "okp-keys failed"
"${PEER[@]}" okp-keys --out "$RUN/identus-px-keys.env" 2>>"$RUN/logs/mint.log" || setup_fail keys "okp-keys failed"
IDENTUS_MONGO_PASSWORD="${MEDIATOR_LEG_DEBUG_IDENTUS_MONGO_PASSWORD:-admin}"
identus_env() { # $1=keys file $2=mongo alias $3=SERVICE_ENDPOINTS
    IDENTUS_ARGS=(--env-file "$1" -e "SERVICE_ENDPOINTS=$3" -e MONGODB_USER=admin
        -e "MONGODB_PASSWORD=$IDENTUS_MONGO_PASSWORD" -e MONGODB_PROTOCOL=mongodb -e "MONGODB_HOST=$2"
        -e MONGODB_PORT=27017 -e MONGODB_DB_NAME=mediator)
}

wait_for 120 "mongo ready (init complete, collections created)" mongo_ready "$PREFIX-mongo" ||
    setup_fail mongo "$WAIT_WHAT; log tail: $(docker logs --tail 5 "$PREFIX-mongo" 2>&1 | tail -3)"
wait_for 120 "mongo-px ready" mongo_ready "$PREFIX-mongo-px" ||
    setup_fail mongo-px "$WAIT_WHAT; log tail: $(docker logs --tail 5 "$PREFIX-mongo-px" 2>&1 | tail -3)"

# The DID embeds the endpoint, so the host port is chosen before the container starts; a port
# taken in between is retried with a new one (at most 5 attempts).
IDENTUS="$PREFIX-identus"
PORT_RETRIES=0
attempt=1
while :; do
    if [ "$attempt" = 1 ] && [ -n "${MEDIATOR_LEG_DEBUG_FIRST_PORT:-}" ]; then
        IDENTUS_PORT="$MEDIATOR_LEG_DEBUG_FIRST_PORT"
    else
        IDENTUS_PORT="$("${PEER[@]}" free-port)" || setup_fail identus "free-port failed"
    fi
    identus_env "$RUN/identus-keys.env" mongo "http://127.0.0.1:$IDENTUS_PORT;ws://127.0.0.1:$IDENTUS_PORT/ws"
    if docker run -d --name "$IDENTUS" "${DOCKER_LABELS[@]}" --network "$NET" \
        -p "127.0.0.1:$IDENTUS_PORT:8080" "${IDENTUS_ARGS[@]}" "$IDENTUS_IMAGE" >/dev/null 2>"$RUN/logs/identus.run.err"; then
        break
    fi
    docker rm -f -v "$IDENTUS" >/dev/null 2>&1
    if grep -Eqi 'port is already allocated|address already in use|ports are not available' "$RUN/logs/identus.run.err" && [ "$attempt" -lt 5 ]; then
        say "host port $IDENTUS_PORT is taken; retrying with another (attempt $attempt of 5)"
        PORT_RETRIES=$((PORT_RETRIES + 1))
        attempt=$((attempt + 1))
        continue
    fi
    setup_fail identus "docker run failed (attempt $attempt): $(tail -1 "$RUN/logs/identus.run.err")"
done
IDENTUS_URL="http://127.0.0.1:$IDENTUS_PORT"

IDENTUS_PX="$PREFIX-identus-px"
identus_env "$RUN/identus-px-keys.env" mongo-px "http://identus-px:8080;ws://identus-px:8080/ws"
docker run -d --name "$IDENTUS_PX" "${DOCKER_LABELS[@]}" --network "$NET" --network-alias identus-px \
    "${IDENTUS_ARGS[@]}" "$IDENTUS_IMAGE" >/dev/null 2>"$RUN/logs/identus-px.run.err" ||
    setup_fail identus-px "docker run failed: $(tail -1 "$RUN/logs/identus-px.run.err")"

direct_get() { # $1=path on the directly reachable Identus → body on stdout
    curl -s --noproxy '*' --max-time 10 "$IDENTUS_URL$1"
}
px_get() { # $1=path on the proxy-only Identus, read from inside its container
    with_deadline 20 docker exec "$IDENTUS_PX" curl -s --max-time 10 "http://localhost:8080$1"
}
HEALTH_CODE=""
identus_healthy() {
    case "$(container_state "$IDENTUS")" in running) ;; exited|dead) return 3 ;; *) return 1 ;; esac
    HEALTH_CODE="$(curl -s -o /dev/null -w '%{http_code}' --noproxy '*' --max-time 5 "$IDENTUS_URL/health")"
    [ "$HEALTH_CODE" = 200 ]
}
PX_HEALTH_CODE=""
identus_px_healthy() {
    case "$(container_state "$IDENTUS_PX")" in running) ;; exited|dead) return 3 ;; *) return 1 ;; esac
    PX_HEALTH_CODE="$(with_deadline 20 docker exec "$IDENTUS_PX" curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://localhost:8080/health)"
    [ "$PX_HEALTH_CODE" = 200 ]
}
wait_for 180 "Identus /health 200" identus_healthy ||
    setup_fail identus "mediator never ready: $WAIT_WHAT; last /health code ${HEALTH_CODE:-none} ($(container_state "$IDENTUS")); log tail: $(docker logs --tail 4 "$IDENTUS" 2>&1 | cut -c1-160 | tr '\n' ' ')"
wait_for 180 "Identus (proxy-only) /health 200" identus_px_healthy ||
    setup_fail identus-px "mediator never ready: $WAIT_WHAT; last /health code ${PX_HEALTH_CODE:-none}; log tail: $(docker logs --tail 4 "$IDENTUS_PX" 2>&1 | cut -c1-160 | tr '\n' ' ')"

IDENTUS_VERSION="$(direct_get /version)"
[ "$IDENTUS_VERSION" = 1.2.1 ] || setup_fail identus "/version is '$IDENTUS_VERSION', expected 1.2.1"
[ "$(px_get /version)" = 1.2.1 ] || setup_fail identus-px "/version is not 1.2.1"
direct_get /did >"$RUN/identus.did"
px_get /did >"$RUN/identus-px.did"
"${PEER[@]}" did-check --did "@$RUN/identus.did" --keys "$RUN/identus-keys.env" \
    --expect-http "$IDENTUS_URL" --expect-ws "ws://127.0.0.1:$IDENTUS_PORT/ws" --out "$RUN/identus.did-check.json" \
    >/dev/null 2>"$RUN/logs/did-check.err" || setup_fail identus "/did: $(tail -1 "$RUN/logs/did-check.err")"
"${PEER[@]}" did-check --did "@$RUN/identus-px.did" --keys "$RUN/identus-px-keys.env" \
    --expect-http http://identus-px:8080 --expect-ws ws://identus-px:8080/ws \
    >/dev/null 2>"$RUN/logs/did-check-px.err" || setup_fail identus-px "/did: $(tail -1 "$RUN/logs/did-check-px.err")"
IDENTUS_DID="$(cat "$RUN/identus.did")"
PX_DID="$(cat "$RUN/identus-px.did")"

SQUID="$PREFIX-squid"
# The image declares /var/log/squid and /var/spool/squid as volumes: tmpfs mounts (owned by its
# proxy user, uid 13) keep a run from leaving anonymous, unlabelled volumes behind.
docker run -d --name "$SQUID" "${DOCKER_LABELS[@]}" --network "$NET" -p 127.0.0.1::3128 \
    --tmpfs /var/log/squid:uid=13,gid=13,mode=0755 --tmpfs /var/spool/squid:uid=13,gid=13,mode=0755 \
    -v "$MED/squid/squid.conf:/etc/squid/squid.conf:ro" "$SQUID_IMAGE" >/dev/null 2>"$RUN/logs/squid.run.err" ||
    setup_fail squid "docker run failed: $(tail -1 "$RUN/logs/squid.run.err")"
SQUID_PORT="$(docker port "$SQUID" 3128/tcp 2>/dev/null | head -1 | sed 's/.*://')"
[ -n "$SQUID_PORT" ] || setup_fail squid "no host port for 3128"
SQUID_URL="http://127.0.0.1:$SQUID_PORT"
SQUID_CODE=""
squid_routes() {
    case "$(container_state "$SQUID")" in running) ;; exited|dead) return 3 ;; *) return 1 ;; esac
    SQUID_CODE="$(curl -s -o /dev/null -w '%{http_code}' --noproxy '' -x "$SQUID_URL" --max-time 5 http://identus-px:8080/health)"
    [ "$SQUID_CODE" = 200 ]
}
wait_for 60 "squid routes to identus-px" squid_routes ||
    setup_fail squid "squid not routing: $WAIT_WHAT; last code ${SQUID_CODE:-none}; log tail: $(docker logs --tail 3 "$SQUID" 2>&1 | tr '\n' ' ')"
code="$(curl -s -o /dev/null -w '%{http_code}' --noproxy '' -x "$SQUID_URL" --max-time 5 "$IDENTUS_URL/health")"
[ "$code" = 403 ] || setup_fail squid "squid must deny 127.0.0.1 with 403 (the premise of M-6), got $code"
code="$(curl -s -o /dev/null -w '%{http_code}' --noproxy '' -p -x "$SQUID_URL" --max-time 5 http://identus-px:8080/health)"
[ "$code" = 200 ] || setup_fail squid "a CONNECT tunnel to identus-px:8080 did not work (got $code; M-5 needs it)"
say "Identus $IDENTUS_VERSION at $IDENTUS_URL; proxy-only Identus behind squid at $SQUID_URL ($((SECONDS - STEP_T0)) s so far)"

squid_log() { docker exec "$SQUID" cat /var/log/squid/harness-access.log 2>/dev/null; }
squid_lines() { squid_log | wc -l | tr -d ' '; }
squid_window() { # $1=line count at the window's start → the lines after it, markers left out
    squid_log | tail -n "+$(($1 + 1))" | grep -v '/marker-' || true
}
squid_has() { squid_log | grep -F -- "$1" >/dev/null; }
squid_barrier() { # a marker request through squid, then wait until squid has logged it
    local marker="marker-$(random_hex)"
    curl -s -o /dev/null --noproxy '' -x "$SQUID_URL" --max-time 5 "http://identus-px:8080/$marker"
    wait_for 30 "squid logged the marker request" squid_has "/$marker"
}
binary_frames() { # $1=container → how many "WS Error (type:Binary)" lines it has logged
    docker logs "$1" 2>&1 | grep -c 'WS Error (type:Binary)' || true
}
identus_logged() { # $1=container $2=what PickupExecuter logged (StatusRequest, LiveModeChange, …) → count
    docker logs "$1" 2>&1 | grep -cE "PickupExecuter.* $2 *\$" || true
}
identus_logged_above() { [ "$(identus_logged "$1" "$2")" -gt "$3" ]; }

# ── the dotnet mediator (mediator-serve), for R-1 and R-2 ─────────────────────────────────
SERVE_HTTP="" SERVE_WS="" SERVE_DID="" SERVE_DIR=""
# The ready file counts only when this very process wrote it: its pid must be SERVE_PID (the
# directory is new for each run as well, so no earlier run's file can be there).
serve_ready() {
    kill -0 "$SERVE_PID" 2>/dev/null || return 3
    [ -s "$SERVE_DIR/ready.json" ] && [ "$(json_get "$SERVE_DIR/ready.json" pid)" = "$SERVE_PID" ] &&
        [ -n "$(json_get "$SERVE_DIR/ready.json" http)" ] && [ -n "$(json_get "$SERVE_DIR/ready.json" ws)" ] &&
        [ -n "$(json_get "$SERVE_DIR/ready.json" did)" ]
}
start_serve() { # $1=tag, rest: extra mediator-serve options
    local tag="$1"; shift
    SERVE_DIR="$RUN/serve-$tag"
    mkdir "$SERVE_DIR" || return 1 # new: a server's FIFO, ready file and logs are never reused
    mkfifo "$SERVE_DIR/stdin" || return 1
    # stdin is a FIFO held open on fd 7: the mediator stops when the leg closes it or dies. Its own
    # lifetime limit is the leg watchdog's, so it never stops before the leg would.
    "$INTEROP_CLI" mediator-serve --mediator "$RUN/dotnet-medsvc.json" --ready-file "$SERVE_DIR/ready.json" \
        --max-lifetime "$LEG_WATCHDOG_S" "$@" <"$SERVE_DIR/stdin" >"$SERVE_DIR/stdout.log" 2>"$SERVE_DIR/stderr.log" 8>&- 9>&- &
    SERVE_PID=$!
    exec 7>"$SERVE_DIR/stdin"
    if ! wait_for 60 "mediator-serve ready.json" serve_ready; then
        say "mediator-serve did not start: $WAIT_WHAT; log: $(tail -3 "$SERVE_DIR/stderr.log" | tr '\n' ' ')"
        return 1
    fi
    SERVE_HTTP="$(json_get "$SERVE_DIR/ready.json" http)"
    SERVE_WS="$(json_get "$SERVE_DIR/ready.json" ws)"
    SERVE_DID="$(json_get "$SERVE_DIR/ready.json" did)"
}

if plan_needs R-1 || plan_needs R-2 || [ "$MODE" = up ]; then
    start_serve main || setup_fail mediator-serve "mediator-serve dies or never wrote ready.json (log: $(tail -2 "$SERVE_DIR/stderr.log" 2>/dev/null | tr '\n' ' '))"
fi
say "set-up done in $((SECONDS - STEP_T0)) s"

if [ "$MODE" = up ]; then
    cat >"$RUN/up.env" <<EOF
IDENTUS_URL=$IDENTUS_URL
IDENTUS_DID=$IDENTUS_DID
PX_DID=$PX_DID
SQUID_URL=$SQUID_URL
SERVE_HTTP=$SERVE_HTTP
SERVE_DID=$SERVE_DID
WORK=$WORK
RUN=$RUN
EOF
    cat "$RUN/up.env"
    { summary_header; echo "**--up (development): set up and torn down; no cells run**"; } >"$SUMMARY"
    SUMMARY_WRITTEN=1
    say "up: press Enter (or close stdin) to tear everything down"
    read -r _ || true
    exit 0
fi

# ── cell plumbing ─────────────────────────────────────────────────────────────────────────
C=""                # the running cell's directory
CELL_DETAIL=""
cfail() { CELL_DETAIL="$*"; echo "CELL FAIL: $*"; return 1; }
step() { echo "--- $* ($(date -u +%H:%M:%S))"; }

# wallet <name> <command> <args…>: run a MediatorWallet command (stdout JSON → $C/<name>.json),
# 0 only when it exited 0 and reported ok. WALLET_RC holds its exit code.
wallet() {
    local name="$1"; shift
    local rc
    with_deadline 300 wallet_exec "$@" --log-out "$C/$name.log.jsonl" >"$C/$name.json"; rc=$?
    WALLET_RC=$rc
    echo "wallet $1 → exit $rc: $(head -c 600 "$C/$name.json")"
    [ "$rc" = 0 ] && [ "$(json_get "$C/$name.json" ok)" = true ]
}
wallet_error() { # $1=name → a one-line reason from the wallet's JSON: step, type, problem code, message
    "$VPY" -I -c 'import json, sys
try:
    r = json.load(open(sys.argv[1]))
except (OSError, ValueError):
    r = {}
e = r.get("error") or {}
failed = [s.get("name") for s in r.get("steps") or [] if not s.get("ok")]
kind = str(e.get("type") or "").rsplit(".", 1)[-1]
inner = "/".join(str(i).rsplit(".", 1)[-1] for i in e.get("inner") or [])
text = "exit %s" % sys.argv[2]
if failed:
    text += " at " + str(failed[-1])
if kind:
    text += ": " + kind + (" (inner " + inner + ")" if inner else "")
if e.get("problem_code"):
    text += " [problem code %s]" % e["problem_code"]
if e.get("message"):
    text += ": " + str(e["message"])
print(text)' "$C/$1.json" "$WALLET_RC"
}
# wallet_failure_is <name> <step> <exception type> <inner type> <text>: 0 when the command failed
# at that step with that exception (type names match by suffix), an inner exception of that type,
# and a message containing <text>.
wallet_failure_is() {
    "$VPY" -I -c 'import json, sys
r = json.load(open(sys.argv[1]))
e = r.get("error") or {}
failed = [s.get("name") for s in r.get("steps") or [] if not s.get("ok")]
ok = (r.get("ok") is False and failed[-1:] == [sys.argv[2]]
      and str(e.get("type", "")).endswith(sys.argv[3])
      and any(str(i).endswith(sys.argv[4]) for i in e.get("inner") or [])
      and sys.argv[5] in str(e.get("message", "")))
sys.exit(0 if ok else 1)' "$C/$1.json" "$2" "$3" "$4" "$5" 2>/dev/null
}
# assert_message <mode> <expected> <unpacked>: interop_peer assert; on failure CELL_DETAIL names
# the first mismatch.
assert_message() {
    local out rc
    out="$(ipeer assert --mode "$1" --expected "$2" --unpacked "$3" 2>&1)"; rc=$?
    [ -n "$out" ] && echo "$out"
    [ "$rc" = 0 ] || cfail "assert: $(echo "$out" | grep -m1 'ASSERT FAIL' | sed 's/^ASSERT FAIL: //')"
}
peer() { "${PEER[@]}" "$@"; }
ipeer() { "${IPEER[@]}" "$@"; }

oob_invitation() { # $1=mediator DID $2=out: an OOB 2.0 invitation as Identus serves one
    "$VPY" -I -c 'import json, sys, uuid
json.dump({"id": str(uuid.uuid4()), "type": "https://didcomm.org/out-of-band/2.0/invitation",
           "from": sys.argv[1], "body": {"goal_code": "request-mediate", "goal": "RequestMediate",
           "accept": ["didcomm/v2"]}}, open(sys.argv[2], "w"))' "$1" "$2"
}

# enrol_wallet <name> <invitation> <mediator DID> <extra args…>: MediatorWallet enrol into $C/<name>
enrol_wallet() {
    local name="$1" invitation="$2" mediator="$3"; shift 3
    wallet "$name" enrol --identity-dir "$C/$name" --invitation "$invitation" --expect-mediator-did "$mediator" \
        --out "$C/$name.state.json" "$@"
}

# python_forward <to DID> <via DID> <tag>: a fresh authcrypted message from python-base, wrapped
# for the mediator → $C/<tag>.msg.json, .fwd.json, .inner.json
python_forward() {
    ipeer gen-message --to "$1" --from "$RUN/python-base.json" --out "$C/$3.msg.json" &&
    peer forward --identity "$RUN/python-base.json" --to "$1" --via "$2" --message "$C/$3.msg.json" \
        --out "$C/$3.fwd.json" --inner-out "$C/$3.inner.json" --meta-out "$C/$3.fwd.meta.json"
}

# ── cells ─────────────────────────────────────────────────────────────────────────────────
# Each cell function takes its sabotage ("" for the real cell), works in $C, and returns 0 on
# PASS; on FAIL it sets CELL_DETAIL (cfail).

cell_B0() { # wallet boundary (E-36)
    local forbid=() problems=""
    [ "$1" = forbid-client ] && forbid=(--forbid DidComm.Mediator.Client)
    # Both checks always run (the control must turn both red), and the cell reports both.
    step "compiled references of the wallet (MediatorWallet boundary)"
    wallet boundary boundary --out "$C/refs.json" ${forbid[@]+"${forbid[@]}"} ||
        problems="MediatorWallet boundary: $(wallet_error boundary)"
    step "package graph, framework references, runtimeconfig, deps.json, output, compiled references"
    if [ -s "$C/refs.json" ]; then
        "$VPY" -I "$MED/check_boundary.py" --project-dir "$MED/MediatorWallet" --bin-dir "$WALLET_BIN_DIR" \
            --refs "$C/refs.json" ${forbid[@]+"${forbid[@]}"} >"$C/check.txt" 2>&1 ||
            problems="${problems:+$problems; }check_boundary: $(grep -m1 '^FAIL' "$C/check.txt")"
        cat "$C/check.txt"
    else
        problems="${problems:+$problems; }check_boundary: no refs.json to read"
    fi
    [ -z "$problems" ] || { cfail "$problems"; return 1; }
}

cell_M1() { # dotnet recipient via Identus over HTTP (E-30, E-51)
    local sab="$1" pin=()
    [ "$sab" = pin-v3 ] && pin=(--pin V3)
    step "Identus /version, /did, /invitation"
    [ "$(direct_get /version)" = 1.2.1 ] || { cfail "/version is not 1.2.1"; return 1; }
    direct_get /did >"$C/did.txt"
    peer did-check --did "@$C/did.txt" --keys "$RUN/identus-keys.env" --expect-http "$IDENTUS_URL" \
        --expect-ws "ws://127.0.0.1:$IDENTUS_PORT/ws" || { cfail "/did failed did-check"; return 1; }
    direct_get /invitation >"$C/invitation.json"
    step "wallet enrols (no version pinned: Discover Features → Coordinate Mediation 2.0)"
    enrol_wallet w "$C/invitation.json" "$IDENTUS_DID" --expect-version 2.0 --allow-hosts 127.0.0.1 \
        ${pin[@]+"${pin[@]}"} || { cfail "enrol: $(wallet_error w)"; return 1; }
    local routed; routed="$(json_get "$C/w.state.json" routedDid)"
    [ -n "$routed" ] || { cfail "state.json has no routedDid"; return 1; }
    local to="$routed"
    if [ "$sab" = unregistered-next ]; then
        to="$(ipeer mint --out "$C/stranger.json")" || { cfail "mint"; return 1; }
    fi
    step "python forwards a fresh authcrypted message to the routed DID via Identus"
    python_forward "$to" "$IDENTUS_DID" m || { cfail "python forward failed"; return 1; }
    peer post --url "$IDENTUS_URL" --in "$C/m.fwd.json" --expect-status 202 --meta-out "$C/m.post.json" \
        --body-out "$C/m.post.body" || { cfail "forward not accepted: HTTP $(json_get "$C/m.post.json" status), expected 202"; return 1; }
    if [ "$sab" = identus-stopped ]; then
        step "sabotage: stop Identus after the 202"
        docker stop "$IDENTUS" >/dev/null
    fi
    step "wallet fetches until drained (status ≥ 1 first; final status 0)"
    wallet fetch fetch --state "$C/w.state.json" --expect-count 1 --out-dir "$C/fetch" --allow-hosts 127.0.0.1 ||
        { cfail "fetch: $(wallet_error fetch)"; return 1; }
    local expected="$C/m.msg.json"
    if [ "$sab" = wrong-message ]; then
        ipeer gen-message --to "$routed" --from "$RUN/python-base.json" --out "$C/other.msg.json" ||
            { cfail "gen-message failed"; return 1; }
        expected="$C/other.msg.json"
    fi
    step "assert the unpacked message and its authcrypt metadata"
    assert_message authcrypt "$expected" "$C/fetch/item-1.unpacked.json" || return 1
}

cell_M2() { # burst of 12 via Identus, fetched with limit 5 (E-35; Identus ignores limit)
    local expect=12
    [ "$1" = expect-13 ] && expect=13
    direct_get /invitation >"$C/invitation.json"
    enrol_wallet w "$C/invitation.json" "$IDENTUS_DID" --expect-version 2.0 --allow-hosts 127.0.0.1 ||
        { cfail "enrol: $(wallet_error w)"; return 1; }
    local routed; routed="$(json_get "$C/w.state.json" routedDid)"
    step "python wraps 12 forwards"
    mkdir -p "$C/burst"
    local i=1
    while [ "$i" -le 12 ]; do
        python_forward "$routed" "$IDENTUS_DID" "b$i" || { cfail "python forward $i failed"; return 1; }
        cp "$C/b$i.fwd.json" "$C/burst/fwd-$i.json"
        i=$((i + 1))
    done
    step "12 posts at once"
    peer post-many --url "$IDENTUS_URL" --in-dir "$C/burst" --concurrency 12 --expect-status 202 \
        --meta-out "$C/burst.json" || { cfail "not every forward was accepted with 202"; return 1; }
    step "wallet fetches with limit 5 until drained, expecting $expect"
    wallet fetch fetch --state "$C/w.state.json" --expect-count "$expect" --limit 5 --out-dir "$C/fetch" \
        --allow-hosts 127.0.0.1 || { cfail "fetch: $(wallet_error fetch)"; return 1; }
    step "the 12 fetched messages are exactly the 12 sent"
    "$VPY" -I -c 'import glob, json, os, sys
d = sys.argv[1]
sent = sorted(json.load(open(os.path.join(d, "b%d.msg.json" % i)))["id"] for i in range(1, 13))
got = sorted(json.load(open(p))["message"]["id"] for p in glob.glob(os.path.join(d, "fetch", "item-*.unpacked.json")))
if sent != got:
    print("ASSERT FAIL: fetched %d item(s), %d sent; unequal ids" % (len(got), len(sent)))
    sys.exit(1)
print("12 unique items, equal to the 12 sent")' "$C" || { cfail "the fetched items are not the 12 sent"; return 1; }
}

cell_M3() { # dotnet sender via Identus to a python recipient (CM 2.0 + Pickup 3.0 by hand)
    local forward=(--forward true)
    [ "$1" = no-forward ] && forward=()
    step "python recipient enrols at Identus (Coordinate Mediation 2.0) and registers P2"
    ipeer mint --out "$C/py-w2.json" >/dev/null || { cfail "mint"; return 1; }
    peer mint-routed --service-uri "$IDENTUS_DID" --out "$C/p2.json" >/dev/null || { cfail "mint-routed"; return 1; }
    peer cm2-enrol --identity "$C/py-w2.json" --mediator "$IDENTUS_DID" --url "$IDENTUS_URL" \
        --register "$C/p2.json" --out "$C/enrol.json" || { cfail "cm2-enrol at Identus failed"; return 1; }
    step "InteropCli packs for P2 (its service uri is the Identus DID: one forward for Identus, FR-ROUTE-04)"
    ipeer gen-message --to "$C/p2.json" --from "$RUN/dotnet-base.json" --out "$C/m.msg.json" ||
        { cfail "gen-message failed"; return 1; }
    "$INTEROP_CLI" pack --identity "$RUN/dotnet-base.json" --to "$C/p2.json" --mode authcrypt \
        --message "$C/m.msg.json" --out "$C/m.fwd.json" ${forward[@]+"${forward[@]}"} || { cfail "InteropCli pack failed"; return 1; }
    peer post --url "$IDENTUS_URL" --in "$C/m.fwd.json" --expect-status 202 --meta-out "$C/m.post.json" ||
        { cfail "forward not accepted: HTTP $(json_get "$C/m.post.json" status)"; return 1; }
    step "python picks up (Pickup 3.0 by hand; Identus answers messages-received with nothing)"
    peer pickup3 --identity "$C/py-w2.json" --mediator "$IDENTUS_DID" --url "$IDENTUS_URL" --recipient "$C/p2.json" \
        --expect-count 1 --expect-ack-status no --out-dir "$C/pick" --out "$C/pick.json" ||
        { cfail "pickup3: $(json_get "$C/pick.json" error)"; return 1; }
    assert_message authcrypt "$C/m.msg.json" "$C/pick/item-1.unpacked.json" || return 1
}

cell_M4() { # HTTP through TrustedProxy (E-47, HTTP half)
    local proxy=(--trusted-proxy "$SQUID_URL")
    [ "$1" = no-proxy ] && proxy=()
    local start; start="$(squid_lines)"
    px_get /invitation >"$C/invitation.json"
    step "wallet enrols with the proxy-only Identus through TrustedProxy (both transports)"
    enrol_wallet w "$C/invitation.json" "$PX_DID" --expect-version 2.0 --allow-hosts identus-px \
        --resolve-dns-names false ${proxy[@]+"${proxy[@]}"} || { cfail "enrol through the proxy: $(wallet_error w)"; return 1; }
    local routed; routed="$(json_get "$C/w.state.json" routedDid)"
    python_forward "$routed" "$PX_DID" m || { cfail "python forward failed"; return 1; }
    peer post --url http://identus-px:8080 --proxy "$SQUID_URL" --in "$C/m.fwd.json" --expect-status 202 \
        --meta-out "$C/m.post.json" || { cfail "forward through squid: HTTP $(json_get "$C/m.post.json" status)"; return 1; }
    wallet fetch fetch --state "$C/w.state.json" --expect-count 1 --out-dir "$C/fetch" --allow-hosts identus-px \
        --resolve-dns-names false ${proxy[@]+"${proxy[@]}"} || { cfail "fetch through the proxy: $(wallet_error fetch)"; return 1; }
    assert_message authcrypt "$C/m.msg.json" "$C/fetch/item-1.unpacked.json" || return 1
    # Not gated: identus-px has no route but squid (no host port; its name resolves only on the
    # leg's network), so the passing flow is the proof. The window is kept as evidence, with the
    # wallet's requests told from python's by User-Agent.
    step "squid's log for the cell window (evidence)"
    squid_barrier && squid_window "$start" >"$C/squid-window.log"
    cat "$C/squid-window.log" 2>/dev/null
    echo "wallet POSTs relayed by squid in the window: $(grep ' POST http://identus-px:8080/ ' "$C/squid-window.log" 2>/dev/null | grep -vc 'Python-urllib' || true)"
}

cell_M5() { # WebSocket through TrustedProxy (E-47, WebSocket half)
    local proxy=(--trusted-proxy "$SQUID_URL") target=auto expected=ws://identus-px:8080/ws
    [ "$1" = no-proxy ] && proxy=()
    # ws-wrong-path: the tunnel opens, but the handshake asks for a path with no WebSocket behind
    # it: the CONNECT check passes and the call must fail.
    if [ "$1" = ws-wrong-path ]; then target=ws://identus-px:8080/no-websocket-here; expected="$target"; fi
    px_get /invitation >"$C/invitation.json"
    enrol_wallet w "$C/invitation.json" "$PX_DID" --expect-version 2.0 --allow-hosts identus-px \
        --resolve-dns-names false --trusted-proxy "$SQUID_URL" || { cfail "enrol through the proxy: $(wallet_error w)"; return 1; }
    local start; start="$(squid_lines)"
    step "a status over the WebSocket transport, through TrustedProxy (RequireCreatedTime off: Identus does not stamp its answers)"
    wallet ws ws-attempt --state "$C/w.state.json" --ws-endpoint "$target" --timeout 10 --allow-hosts identus-px \
        --resolve-dns-names false --relax-created-time ${proxy[@]+"${proxy[@]}"}
    local endpoint; endpoint="$(json_get "$C/ws.json" observed.ws_endpoint)"
    [ "$endpoint" = "$expected" ] || { cfail "ws-attempt used '${endpoint:-no endpoint}', expected $expected"; return 1; }
    step "squid logs the CONNECT tunnel once it closes"
    tunnel_logged() { squid_window "$start" | grep -F 'CONNECT identus-px:8080 200 TCP_TUNNEL' >/dev/null; }
    wait_for 30 "squid CONNECT identus-px:8080 200 TCP_TUNNEL line" tunnel_logged || { cfail "$WAIT_WHAT"; return 1; }
    squid_window "$start" >"$C/squid-window.log"
    step "the call itself: answered, opened and matched"
    ws_call_ok "WebSocket call through the proxy" || return 1
}

# ws_call_ok <label>: $C/ws.json's call ended ok (an answer opened and matched to the request);
# otherwise CELL_DETAIL says how it ended, and why an answer was dropped when one was.
ws_call_ok() {
    local outcome kind dropped
    outcome="$(json_get "$C/ws.json" observed.attempt.outcome)"
    if [ "$WALLET_RC" = 0 ] && [ "$outcome" = ok ]; then
        echo "$1: ok ($(json_get "$C/ws.json" observed.attempt.elapsed_ms) ms, status counts $(json_get "$C/ws.json" observed.attempt.message_count))"
        return 0
    fi
    kind="$(json_get "$C/ws.json" observed.attempt.exception.type)"
    dropped="$("$VPY" -I -c 'import json, sys
try:
    d = (json.load(open(sys.argv[1])).get("observed") or {}).get("dropped") or []
except (OSError, ValueError):
    d = []
print(", ".join(sorted({"answer (replay admission: %s)" % x.get("admission") for x in d})))' "$C/ws.json")"
    cfail "$1: ${outcome:-not made} (${kind##*.}${dropped:+; our client dropped: $dropped})"
}

cell_M6() { # process proxy variables are ignored (E-48)
    local sab="$1" proxy=() noproxy=() proxy_b=()
    [ "$sab" = with-trusted-proxy ] && proxy=(--trusted-proxy "$SQUID_URL")
    # proxy-in-b: TrustedProxy on the (b) enrolment only, so (b) succeeds and must turn the cell red.
    [ "$sab" = proxy-in-b ] && proxy_b=(--trusted-proxy "$SQUID_URL")
    [ "$sab" = no-proxy-env ] && noproxy=(NO_PROXY=127.0.0.1 no_proxy=127.0.0.1)
    local envp=(env -u NO_PROXY -u no_proxy HTTP_PROXY="$SQUID_URL" HTTPS_PROXY="$SQUID_URL" ALL_PROXY="$SQUID_URL"
        http_proxy="$SQUID_URL" https_proxy="$SQUID_URL" all_proxy="$SQUID_URL" ${noproxy[@]+"${noproxy[@]}"})
    local start
    start="$(squid_lines)"
    direct_get /invitation >"$C/invitation.json"
    step "(a) the M-1 flow against 127.0.0.1 with every proxy variable pointing at squid (which denies 127.0.0.1)"
    WALLET_ENV=("${envp[@]}")
    enrol_wallet w "$C/invitation.json" "$IDENTUS_DID" --expect-version 2.0 --allow-hosts 127.0.0.1 \
        --expect-env-proxy "$SQUID_URL" ${proxy[@]+"${proxy[@]}"}
    local rc=$?
    WALLET_ENV=()
    [ "$WALLET_RC" = 3 ] && { cfail "precondition: the runtime would not route 127.0.0.1 through the proxy variables ($(wallet_error w))"; return 1; }
    [ "$rc" = 0 ] || { cfail "enrol with proxy variables set: $(wallet_error w)"; return 1; }
    local routed; routed="$(json_get "$C/w.state.json" routedDid)"
    python_forward "$routed" "$IDENTUS_DID" m || { cfail "python forward failed"; return 1; }
    peer post --url "$IDENTUS_URL" --in "$C/m.fwd.json" --expect-status 202 --meta-out "$C/m.post.json" ||
        { cfail "forward not accepted"; return 1; }
    WALLET_ENV=("${envp[@]}")
    wallet fetch fetch --state "$C/w.state.json" --expect-count 1 --out-dir "$C/fetch" --allow-hosts 127.0.0.1 \
        --expect-env-proxy "$SQUID_URL" ${proxy[@]+"${proxy[@]}"}
    rc=$?
    WALLET_ENV=()
    [ "$rc" = 0 ] || { cfail "fetch with proxy variables set: $(wallet_error fetch)"; return 1; }
    assert_message authcrypt "$C/m.msg.json" "$C/fetch/item-1.unpacked.json" || return 1
    step "(a) a WebSocket call to Identus succeeds: it went direct (squid answers 127.0.0.1 with 403)"
    WALLET_ENV=("${envp[@]}")
    wallet ws ws-attempt --state "$C/w.state.json" --ws-endpoint auto --timeout 10 --allow-hosts 127.0.0.1 \
        --relax-created-time --expect-env-proxy "$SQUID_URL" ${proxy[@]+"${proxy[@]}"}
    WALLET_ENV=()
    ws_call_ok "(a) WebSocket call with the proxy variables set" || return 1
    step "(b) enrolment with the proxy-only Identus fails: the variables are not used even where they would help"
    px_get /invitation >"$C/px-invitation.json"
    WALLET_ENV=("${envp[@]}")
    enrol_wallet px "$C/px-invitation.json" "$PX_DID" --expect-version 2.0 --allow-hosts identus-px \
        --resolve-dns-names false --request-timeout 10 ${proxy[@]+"${proxy[@]}"} ${proxy_b[@]+"${proxy_b[@]}"}
    rc=$?
    WALLET_ENV=()
    if [ "$rc" = 0 ]; then cfail "(b) enrolment with identus-px succeeded: a proxy was used"; return 1; fi
    # Only a direct connection attempt counts: the first request (request-mediation) failing with
    # HttpRequestException over a SocketException for identus-px, the name only squid can resolve.
    wallet_failure_is px request-mediation HttpRequestException SocketException identus-px ||
        { cfail "(b) enrolment with identus-px failed, but not with a direct-connect error: $(wallet_error px)"; return 1; }
    echo "(b) enrolment with identus-px failed with a direct-connect error, as required: $(wallet_error px)"
    if [ "$sab" = squid-request-in-window ]; then
        step "sabotage: one request through squid inside the window"
        curl -s -o /dev/null --noproxy '' -x "$SQUID_URL" --max-time 5 http://identus-px:8080/health
    fi
    step "(c) squid saw nothing in the window"
    squid_barrier || { cfail "$WAIT_WHAT"; return 1; }
    squid_window "$start" >"$C/squid-window.log"
    cat "$C/squid-window.log"
    [ ! -s "$C/squid-window.log" ] || { cfail "(c) squid logged $(wc -l <"$C/squid-window.log" | tr -d ' ') line(s) in the window, expected none"; return 1; }
}

cell_M8() { # dotnet recipient via Identus over WebSocket (O-98-1 = 1a: text frames)
    # Identus answers over a WebSocket without created_time, so the wallet sets
    # ReplayProtectionOptions.RequireCreatedTime = false (--relax-created-time; the cell checks it
    # took effect). no-relax / fetch-no-relax leave it on for the status / the fetch.
    local relax=(--relax-created-time) fetch_relax=(--relax-created-time)
    [ "$1" = no-relax ] && relax=()
    [ "$1" = fetch-no-relax ] && fetch_relax=()
    direct_get /invitation >"$C/invitation.json"
    enrol_wallet w "$C/invitation.json" "$IDENTUS_DID" --expect-version 2.0 --allow-hosts 127.0.0.1 ||
        { cfail "enrol: $(wallet_error w)"; return 1; }
    step "a status over the WebSocket transport, to the first ws service of the Identus DID"
    wallet ws ws-attempt --state "$C/w.state.json" --ws-endpoint auto --timeout 10 --allow-hosts 127.0.0.1 \
        ${relax[@]+"${relax[@]}"}
    local endpoint; endpoint="$(json_get "$C/ws.json" observed.ws_endpoint)"
    [ "$endpoint" = "ws://127.0.0.1:$IDENTUS_PORT/ws" ] || { cfail "ws-attempt used '${endpoint:-no endpoint}'"; return 1; }
    ws_call_ok "status over WebSocket" || return 1
    [ "$(json_get "$C/ws.json" observed.require_created_time_in_effect)" = false ] ||
        { cfail "RequireCreatedTime was not off in the client's replay admission"; return 1; }
    step "python forwards one message to the routed DID via Identus (HTTP)"
    local routed; routed="$(json_get "$C/w.state.json" routedDid)"
    python_forward "$routed" "$IDENTUS_DID" m || { cfail "python forward failed"; return 1; }
    peer post --url "$IDENTUS_URL" --in "$C/m.fwd.json" --expect-status 202 --meta-out "$C/m.post.json" ||
        { cfail "forward not accepted: HTTP $(json_get "$C/m.post.json" status)"; return 1; }
    # Every call of the fetch goes over the WebSocket. Its quiet acknowledgement waits for a status
    # Identus never sends on a WebSocket (logged, not an error), so a short request timeout keeps
    # the cell fast; the client is unchanged.
    step "the wallet fetches over WebSocket until drained"
    wallet fetch fetch --state "$C/w.state.json" --expect-count 1 --out-dir "$C/fetch" --ws-endpoint auto \
        --request-timeout 3 --wait-timeout 15 --allow-hosts 127.0.0.1 ${fetch_relax[@]+"${fetch_relax[@]}"} ||
        { cfail "fetch over WebSocket: $(wallet_error fetch)"; return 1; }
    assert_message authcrypt "$C/m.msg.json" "$C/fetch/item-1.unpacked.json" || return 1
}

cell_R1() { # python recipient via the dotnet mediator (reverse)
    local sab="$1" variant=()
    [ "$sab" = no-created-time ] && variant=(--variant no-created-time)
    if [ "$sab" = v2-disabled ]; then
        stop_serve
        start_serve v2-off --enable-v2 false || { cfail "mediator-serve --enable-v2 false did not start"; return 1; }
    fi
    local url="$SERVE_HTTP" med="$SERVE_DID"
    for n in py-r1 p py-r1b pb py-r1c pc; do ipeer mint --out "$C/$n.json" >/dev/null || { cfail "mint"; return 1; }; done
    step "mediate-request → mediate-grant, keylist-update → success, keylist-query (CM 2.0 by hand)"
    peer cm2-enrol --identity "$C/py-r1.json" --mediator "$med" --url "$url" --register "$C/p.json" \
        --require-created-time --require-pagination --out "$C/enrol.json" ${variant[@]+"${variant[@]}"} ||
        { cfail "cm2-enrol: $(json_get "$C/enrol.json" error)"; return 1; }
    step "variant: a mediate-request with no body (the spec's example) is granted"
    peer cm2-enrol --identity "$C/py-r1b.json" --mediator "$med" --url "$url" --register "$C/pb.json" \
        --variant spec-example --require-created-time --out "$C/enrol-spec.json" || { cfail "body-less mediate-request"; return 1; }
    step "variant: a keylist-update without return_route gets 202 and is still applied"
    peer cm2-enrol --identity "$C/py-r1c.json" --mediator "$med" --url "$url" --register "$C/pc.json" \
        --variant no-return-route-on-update --require-created-time --out "$C/enrol-noroute.json" ||
        { cfail "keylist-update without return_route"; return 1; }
    step "python forwards via the dotnet mediator, then picks up (Pickup 3.0 by hand)"
    python_forward "$C/p.json" "$RUN/dotnet-medsvc.json" m || { cfail "python forward failed"; return 1; }
    peer post --url "$url" --in "$C/m.fwd.json" --expect-status 202 --meta-out "$C/m.post.json" ||
        { cfail "forward not accepted: HTTP $(json_get "$C/m.post.json" status)"; return 1; }
    peer pickup3 --identity "$C/py-r1.json" --mediator "$med" --url "$url" --recipient "$C/p.json" \
        --expect-count 1 --expect-ack-status yes --out-dir "$C/pick" --out "$C/pick.json" ||
        { cfail "pickup3: $(json_get "$C/pick.json" error)"; return 1; }
    assert_message authcrypt "$C/m.msg.json" "$C/pick/item-1.unpacked.json" || return 1
}

cell_R2() { # attribution control: the same wallet against the dotnet mediator (CM 3.0)
    local version=3.0 ws="$SERVE_WS"
    [ "$1" = expect-v2 ] && version=2.0
    # ws-wrong-path: a path with no WebSocket endpoint behind it, so the gated WebSocket call fails.
    [ "$1" = ws-wrong-path ] && ws="${SERVE_WS%/didcomm}/no-websocket-here"
    oob_invitation "$SERVE_DID" "$C/invitation.json"
    enrol_wallet w "$C/invitation.json" "$SERVE_DID" --expect-version "$version" --endpoint-override "$SERVE_HTTP" \
        --allow-hosts 127.0.0.1 || { cfail "enrol with the dotnet mediator: $(wallet_error w)"; return 1; }
    local routed; routed="$(json_get "$C/w.state.json" routedDid)"
    python_forward "$routed" "$RUN/dotnet-medsvc.json" m || { cfail "python forward failed"; return 1; }
    peer post --url "$SERVE_HTTP" --in "$C/m.fwd.json" --expect-status 202 --meta-out "$C/m.post.json" ||
        { cfail "forward not accepted"; return 1; }
    wallet fetch fetch --state "$C/w.state.json" --expect-count 1 --explicit-ack --out-dir "$C/fetch" \
        --allow-hosts 127.0.0.1 || { cfail "fetch + AckAsync: $(wallet_error fetch)"; return 1; }
    assert_message authcrypt "$C/m.msg.json" "$C/fetch/item-1.unpacked.json" || return 1
    step "the one gated WebSocket exchange: our client and our mediator (status over ws)"
    wallet ws ws-attempt --state "$C/w.state.json" --ws-endpoint "$ws" --timeout 10 --allow-hosts 127.0.0.1
    local outcome; outcome="$(json_get "$C/ws.json" observed.attempt.outcome)"
    [ "$WALLET_RC" = 0 ] && [ "$outcome" = ok ] ||
        { cfail "WebSocket call to our mediator: ${outcome:-not made} ($(json_get "$C/ws.json" observed.attempt.exception.type)) at $ws"; return 1; }
    echo "WebSocket call to our mediator: ok, status counts $(json_get "$C/ws.json" observed.attempt.message_count)"
}

cell_title() {
    case "$1" in
        B-0) echo "wallet references only DidComm.Mediator.Client (E-36)" ;;
        M-1) echo "dotnet recipient via Identus over HTTP: CM 2.0 negotiated, register, python forward, fetch, status 0 (E-30, E-51)" ;;
        M-2) echo "burst of 12 via Identus, fetched with limit 5 until drained (E-35)" ;;
        M-3) echo "dotnet sender via Identus to a python recipient (CM 2.0 + Pickup 3.0 by hand)" ;;
        M-4) echo "HTTP through TrustedProxy to a proxy-only Identus (E-47)" ;;
        M-5) echo "WebSocket through TrustedProxy: CONNECT tunnel and an answered status (E-47)" ;;
        M-6) echo "process proxy variables ignored (E-48)" ;;
        M-8) echo "dotnet recipient via Identus over WebSocket: a status, then a fetch of a python-forwarded message (E-53)" ;;
        R-1) echo "python recipient via the dotnet mediator (CM 2.0 + Pickup 3.0 by hand)" ;;
        R-2) echo "control: the same wallet against the dotnet mediator (CM 3.0, AckAsync, a WebSocket status call)" ;;
    esac
}

# wallet_exec: only ever the command of with_deadline (it runs in that background subshell and
# replaces it, so the hang guard's signal reaches the wallet itself). WALLET_ENV, when set,
# prefixes `env …` (M-6's proxy variables).
WALLET_ENV=()
wallet_exec() { exec ${WALLET_ENV[@]+"${WALLET_ENV[@]}"} "$WALLET_BIN" "$@"; }

# nc_expect <id> <sabotage>: the extended regex the FAIL's detail must match. A sabotaged cell that
# fails for another reason proves nothing about the check it targets: a broken control.
nc_expect() {
    case "$1|$2" in
        "B-0|forbid-client") echo "no-forbidden-reference.*check_boundary: FAIL .*forbidden \['DidComm\.Mediator\.Client'\]" ;;
        "M-1|pin-v3") echo "at request-mediation: MediationProtocolException \[problem code e\.p\.msg\.unsupported\]" ;;
        "M-1|unregistered-next") echo "forward not accepted: HTTP 200, expected 202" ;;
        "M-1|wrong-message") echo "^assert: message\.id: expected" ;;
        "M-1|identus-stopped") echo "^fetch: exit 1 at wait-for-status: HttpRequestException.*Connection refused" ;;
        "M-2|expect-13") echo "at wait-for-status: TimeoutException.*counted 12 message\(s\), not 13" ;;
        "M-3|no-forward") echo "^pickup3: status counted 0 after .* expected 1" ;;
        "M-4|no-proxy") echo "at request-mediation: HttpRequestException \(inner SocketException\).*connects directly.*identus-px" ;;
        "M-5|no-proxy") echo "^squid CONNECT identus-px:8080 200 TCP_TUNNEL line \(not within" ;;
        "M-5|ws-wrong-path") echo "^WebSocket call through the proxy: error" ;;
        "M-6|with-trusted-proxy") echo "^enrol with proxy variables set: .*status 403" ;;
        "M-6|no-proxy-env") echo "^precondition: .*PreconditionException" ;;
        "M-6|proxy-in-b") echo "^\(b\) enrolment with identus-px succeeded" ;;
        "M-6|squid-request-in-window") echo "^\(c\) squid logged 1 line\(s\) in the window" ;;
        "M-8|no-relax") echo "^status over WebSocket: timeout \(TimeoutException; our client dropped: .*MissingCreatedTime" ;;
        "M-8|fetch-no-relax") echo "^fetch over WebSocket: exit 1 at wait-for-status: TimeoutException" ;;
        "R-1|no-created-time"|"R-1|v2-disabled") echo "^cm2-enrol: expected HTTP 200 with a reply, got 202" ;;
        "R-2|expect-v2") echo "negotiated-version-is-expected" ;;
        "R-2|ws-wrong-path") echo "^WebSocket call to our mediator: (error|timeout)" ;;
        *) echo "(no expected detail declared)" ;;
    esac
}

run_cell() { # $1=id $2=sabotage
    local id="$1" sab="$2" fn rc t0=$SECONDS
    fn="cell_$(echo "$id" | tr -d '-')"
    C="$RUN/cells/$id${sab:+-nc-$sab}"
    mkdir -p "$C"
    CELL_DETAIL=""
    "$fn" "$sab" >"$C/cell.log" 2>&1
    rc=$?
    local result
    if [ -z "$sab" ]; then
        if [ "$rc" = 0 ]; then result=PASS; else result=FAIL; LEG_FAILED=1; fi
    else
        # A sabotaged cell must FAIL, and for the reason its sabotage causes.
        local expect; expect="$(nc_expect "$id" "$sab")"
        if [ "$rc" = 0 ]; then
            result="PASS (control broken)"; LEG_FAILED=1; CELL_DETAIL="the sabotage did not make the cell fail"
        elif printf '%s\n' "$CELL_DETAIL" | grep -E -- "$expect" >/dev/null; then
            result="FAIL (as required)"
        else
            result="FAIL (wrong reason)"; LEG_FAILED=1
            CELL_DETAIL="expected /$expect/, got: $CELL_DETAIL"
        fi
    fi
    local detail; detail="$(printf '%s' "$CELL_DETAIL" | tr '\t\n|' '  /' | cut -c1-300)"
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$sab" "$result" "$detail" "$((SECONDS - t0))" "$(cell_title "$id")" >>"$CELLS_TSV"
    printf '%-4s %-22s %-22s %4ss  %s\n' "$id" "${sab:--}" "$result" "$((SECONDS - t0))" "$detail"
    if [ "$result" = FAIL ] || [ "$result" = "PASS (control broken)" ] || [ "$result" = "FAIL (wrong reason)" ]; then
        echo "--- $id${sab:+ ($sab)} log ($C/cell.log): ---" >&2
        cat "$C/cell.log" >&2
    fi
}

# ── run ───────────────────────────────────────────────────────────────────────────────────
HEADER="didcomm-dotnet @ $(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown) · Identus $IDENTUS_VERSION ($IDENTUS_IMAGE) · didcomm-python $DIDCOMM_PY_VERSION on $PY_VERSION · Docker $DOCKER_SERVER"
CELLS_STARTED=1
echo
echo "mediator leg ($MODE): $(plan_count) cell run(s)"
for entry in $PLAN; do
    run_cell "${entry%%|*}" "${entry#*|}"
done

# ── observations (data, never gating; gate mode only) ─────────────────────────────────────
obs() { # obs <id> <title> <method> <e,…> <i,…> <value file>
    "$VPY" -I "$MED/render_summary.py" obs --out "$RUN/obs/$1.json" --id "$1" --title "$2" --method "$3" \
        --e "$4" --i "$5" --value-file "$6" 2>>"$RUN/logs/obs.log"
}
run_observations() {
    C="$RUN/cells/observations"
    mkdir -p "$C"
    {
        step "O-21 provenance"
        "$VPY" -I -c 'import json, sys; json.dump(dict(zip(sys.argv[2::2], sys.argv[3::2])), open(sys.argv[1], "w"), indent=2)' "$C/o21.json" \
            identus_version "$IDENTUS_VERSION" \
            identus_image "$IDENTUS_IMAGE" mongo_image "$MONGO_IMAGE" squid_image "$SQUID_IMAGE" \
            image_architecture "$(docker image inspect -f '{{.Os}}/{{.Architecture}}' "$IDENTUS_IMAGE" 2>/dev/null)" \
            scala_did_jars "$(docker exec "$IDENTUS" ls /opt/docker/lib 2>/dev/null | grep -E '^app\.fmgp\.did|^dev\.zio\.zio-http' | tr '\n' ' ')" \
            docker_server "$DOCKER_SERVER" docker_client "$(docker version --format '{{.Client.Version}}' 2>/dev/null)" \
            runner_image "${ImageOS:-local} ${ImageVersion:-}" cpython "$PY_VERSION" didcomm_python "$DIDCOMM_PY_VERSION" \
            port_retries "$PORT_RETRIES"
        obs O-21 "provenance: Identus /version, scala-did jars, image refs and architecture as run, Docker, runner image, CPython, didcomm-python" "bash" "E-42,E-50" "" "$C/o21.json"

        step "O-5 clock offset between the host and the Identus container"
        "$VPY" -I -c 'import json, subprocess, sys, time
before = time.time_ns()
out = subprocess.run(["docker", "exec", sys.argv[1], "date", "+%s%N"], capture_output=True, text=True).stdout.strip()
after = time.time_ns()
c = int(out) if out.isdigit() else None
json.dump({"container_ns": c, "host_ns_before": before, "host_ns_after": after,
           "offset_ms": None if c is None else round((c - (before + after) / 2) / 1e6, 1),
           "uncertainty_ms": round((after - before) / 2e6, 1)}, open(sys.argv[2], "w"), indent=2)' "$IDENTUS" "$C/o5.json"
        obs O-5-host "clock offset, host side: the Identus container's clock vs the host's (docker exec date, midpoint of the call)" "bash" "E-23" "I-12" "$C/o5.json"

        step "python recipient at Identus for O-10, O-11, O-14"
        ipeer mint --out "$C/py-w.json" >/dev/null && peer mint-routed --service-uri "$IDENTUS_DID" --out "$C/p.json" >/dev/null &&
        peer cm2-enrol --identity "$C/py-w.json" --mediator "$IDENTUS_DID" --url "$IDENTUS_URL" --register "$C/p.json" --out "$C/enrol.json" &&
        python_forward "$C/p.json" "$IDENTUS_DID" o11 &&
        peer post --url "$IDENTUS_URL" --in "$C/o11.fwd.json" --expect-status 202 --meta-out "$C/o11.post.json" &&
        peer pickup3 --identity "$C/py-w.json" --mediator "$IDENTUS_DID" --url "$IDENTUS_URL" --recipient "$C/p.json" \
            --expect-count 1 --expect-ack-status no --out-dir "$C/pick" --out "$C/pick.json"
        "$VPY" -I -c 'import json, os, sys
d = sys.argv[1]
inner = open(os.path.join(d, "o11.inner.json"), "rb").read().strip()
try:
    delivered = open(os.path.join(d, "pick", "item-1.envelope.json"), "rb").read().strip()
except OSError:
    delivered = None
pick = json.load(open(os.path.join(d, "pick.json"))) if os.path.exists(os.path.join(d, "pick.json")) else {}
same = None if delivered is None else "byte-equal" if inner == delivered else "JSON-equal" if json.loads(inner) == json.loads(delivered) else "different"
json.dump({"delivered_vs_python_inner_envelope": same,
           "delivery_please_ack": (pick.get("delivery_shape") or {}).get("please_ack"),
           "delivery_return_route": (pick.get("delivery_shape") or {}).get("return_route"),
           "ack": pick.get("ack")}, open(os.path.join(d, "o11.json"), "w"), indent=2)' "$C"
        obs O-11-python "Identus delivery to a python recipient: the attachment vs python's inner envelope; please_ack / return_route on the delivery; the unanswered messages-received" "pickup3 + forward --inner-out" "E-31,E-54" "" "$C/o11.json"
        "$VPY" -I -c 'import json, os, sys
items = []
for label, path in (("Identus delivery (M-3, python sees it)", sys.argv[1]), ("dotnet mediator delivery (R-1, python sees it)", sys.argv[2]), ("Identus delivery (observation run)", sys.argv[3])):
    try:
        pick = json.load(open(path))
        items.append({"source": label, "encodings": [i.get("encoding") for i in pick.get("items", [])]})
    except (OSError, ValueError):
        items.append({"source": label, "encodings": None})
json.dump(items, open(sys.argv[4], "w"), indent=2)' "$RUN/cells/M-3/pick.json" "$RUN/cells/R-1/pick.json" "$C/pick.json" "$C/o10.json"
        obs O-10 "delivery attachment encoding (alphabet, padding, strict decoders) as python sees it" "pickup3 meta" "E-22" "" "$C/o10.json"

        step "O-7 one page of one recipient (the shipped client, in M-1's enrol)"
        if [ -f "$RUN/cells/M-1/w.json" ]; then
            json_get "$RUN/cells/M-1/w.json" observed.query_limit_1 >"$C/o7.json"
        else
            echo '"not measured: M-1 did not run"' >"$C/o7.json"
        fi
        obs O-7 "QueryRecipientsAsync(Limit 1, Offset 0) with two DIDs registered: DIDs returned, pagination" "shipped MediationClient (M-1 enrol)" "E-35" "I-3" "$C/o7.json"

        step "O-16 a second account adds a DID the M-1 wallet registered"
        local taken; taken="$(json_get "$RUN/cells/M-1/w.state.json" routedDid)"
        if [ -n "$taken" ]; then
            ipeer mint --out "$C/py-second.json" >/dev/null &&
            peer cm2-enrol --identity "$C/py-second.json" --mediator "$IDENTUS_DID" --url "$IDENTUS_URL" --register "$taken" \
                --expect-update-result any --out "$C/o16.enrol.json"
            "$VPY" -I -c 'import json, sys
try:
    record = json.load(open(sys.argv[1]))
except (OSError, ValueError) as ex:
    record = {"error": str(ex)}
step = next((s for s in record.get("steps", []) if s.get("name") == "keylist-update"), {})
json.dump({"keylist_update_http_status": step.get("status"), "elapsed_ms": step.get("elapsed_ms"),
           "body_bytes": step.get("body_bytes"), "reply_type": (step.get("reply") or {}).get("type"),
           "updated": record.get("updated"), "cm2_enrol_error": record.get("error")}, open(sys.argv[2], "w"), indent=2)' \
                "$C/o16.enrol.json" "$C/o16.json"
        else
            echo '"not measured: M-1 did not run"' >"$C/o16.json"
        fi
        obs O-16 "a second account adds a DID another account registered: the keylist-update result (FCFS, ADV-1)" "python cm2-enrol (CM 2.0 by hand)" "E-59" "" "$C/o16.json"

        step "O-14 forward to an unregistered next"
        stranger="$(ipeer mint --out "$C/stranger.json")"
        python_forward "$stranger" "$IDENTUS_DID" o14 &&
        peer post --url "$IDENTUS_URL" --in "$C/o14.fwd.json" --expect-status 200,202 --meta-out "$C/o14.post.json" --body-out "$C/o14.body"
        "$VPY" -I -c 'import base64, json, os, sys
meta = json.load(open(sys.argv[1]))
out = {"status": meta.get("status"), "elapsed_ms": meta.get("elapsed_ms"), "content_type": meta.get("content_type")}
try:
    env = json.load(open(sys.argv[2]))
    out["envelope"] = "signed" if "signatures" in env else "encrypted" if "ciphertext" in env else "plaintext"
    if "payload" in env:
        p = env["payload"]
        body = json.loads(base64.urlsafe_b64decode(p + "=" * (-len(p) % 4)))
        out["type"] = body.get("type")
        out["code"] = (body.get("body") or {}).get("code")
except (OSError, ValueError):
    out["envelope"] = None
json.dump(out, open(sys.argv[3], "w"), indent=2)' "$C/o14.post.json" "$C/o14.body" "$C/o14.json"
        obs O-14 "forward naming an unregistered next: HTTP status and answer" "post meta" "" "I-9" "$C/o14.json"

        if [ -x "$WALLET_BIN" ]; then
            step "observer wallet at Identus: O-1 (WebSocket with the defaults), O-1b (the live stream), then the raw probe (MediatorWallet observe)"
            direct_get /invitation >"$C/invitation.json"
            if enrol_wallet obs "$C/invitation.json" "$IDENTUS_DID" --expect-version 2.0 --allow-hosts 127.0.0.1; then
                local before_bin before_status
                before_bin="$(binary_frames "$IDENTUS")"
                before_status="$(identus_logged "$IDENTUS" StatusRequest)"
                # The defaults: RequireCreatedTime on. Identus answers (its log counts the
                # status-request) without created_time, so our client's replay admission drops it.
                wallet o1 ws-attempt --state "$C/obs.state.json" --ws-endpoint auto --timeout 10 --allow-hosts 127.0.0.1
                wait_for 30 "Identus logged the status-request" identus_logged_above "$IDENTUS" StatusRequest "$before_status"
                "$VPY" -I -c 'import json, sys
try:
    observed = json.load(open(sys.argv[1])).get("observed") or {}
except (OSError, ValueError):
    observed = {}
binary = int(sys.argv[4]) - int(sys.argv[3])
answered = int(sys.argv[6]) - int(sys.argv[5])
dropped = observed.get("dropped") or []
if binary > 0:
    cause = "binary frame ignored by Identus"
elif dropped:
    cause = "answer dropped by our replay admission (" + ", ".join(sorted({str(d.get("admission")) for d in dropped})) + ")"
elif answered > 0:
    cause = "Identus answered; no drop recorded"
else:
    cause = "unknown"
json.dump({"attempt": observed.get("attempt"), "require_created_time_in_effect": observed.get("require_created_time_in_effect"),
           "dropped": dropped, "identus_binary_frame_errors_added": binary, "identus_status_requests_logged_added": answered,
           "cause": cause}, open(sys.argv[7], "w"), indent=2)' \
                    "$C/o1.json" "$WALLET_RC" "$before_bin" "$(binary_frames "$IDENTUS")" \
                    "$before_status" "$(identus_logged "$IDENTUS" StatusRequest)" "$C/o1.value.json"
                obs O-1 "our client over WebSocket with the defaults (RequireCreatedTime on) against Identus: outcome and cause" "ws-attempt + Identus log" "E-53,E-14" "" "$C/o1.value.json"

                step "O-1b ReceiveLiveAsync against Identus over WebSocket (RequireCreatedTime off), with a wallet of its own"
                direct_get /invitation >"$C/live-invitation.json"
                if enrol_wallet live "$C/live-invitation.json" "$IDENTUS_DID" --expect-version 2.0 --allow-hosts 127.0.0.1; then
                    local live_routed lm0 dr0 mr0 live_job posted=no
                    live_routed="$(json_get "$C/live.state.json" routedDid)"
                    python_forward "$live_routed" "$IDENTUS_DID" live1 && python_forward "$live_routed" "$IDENTUS_DID" live2
                    lm0="$(identus_logged "$IDENTUS" LiveModeChange)"
                    dr0="$(identus_logged "$IDENTUS" DeliveryRequest)"
                    mr0="$(identus_logged "$IDENTUS" MessagesReceived)"
                    wallet o1b live --state "$C/live.state.json" --ws-endpoint auto --expect-count 2 --guard 120 \
                        --relax-created-time --allow-hosts 127.0.0.1 &
                    live_job=$!
                    # The two forwards go in once Identus has turned live mode on for the stream.
                    if wait_for 30 "Identus logged the live-mode change" identus_logged_above "$IDENTUS" LiveModeChange "$lm0"; then
                        peer post --url "$IDENTUS_URL" --in "$C/live1.fwd.json" --expect-status 202 --meta-out "$C/live1.post.json" &&
                        peer post --url "$IDENTUS_URL" --in "$C/live2.fwd.json" --expect-status 202 --meta-out "$C/live2.post.json" &&
                        posted=yes
                    fi
                    wait "$live_job"
                    "$VPY" -I -c 'import json, sys
try:
    observed = json.load(open(sys.argv[1])).get("observed") or {}
except (OSError, ValueError):
    observed = {}
json.dump({"forwards_posted_after_live_on": sys.argv[2] == "yes", "stream": observed.get("live"),
           "require_created_time_in_effect": observed.get("require_created_time_in_effect"),
           "exception": observed.get("exception"),
           "identus_logged_added": {"LiveModeChange": int(sys.argv[4]) - int(sys.argv[3]),
                                    "DeliveryRequest": int(sys.argv[6]) - int(sys.argv[5]),
                                    "MessagesReceived": int(sys.argv[8]) - int(sys.argv[7])}},
          open(sys.argv[9], "w"), indent=2)' \
                        "$C/o1b.json" "$posted" "$lm0" "$(identus_logged "$IDENTUS" LiveModeChange)" \
                        "$dr0" "$(identus_logged "$IDENTUS" DeliveryRequest)" "$mr0" "$(identus_logged "$IDENTUS" MessagesReceived)" \
                        "$C/o1b.value.json"
                    obs O-1b "ReceiveLiveAsync against Identus over WebSocket (RequireCreatedTime off): live mode on, two forwards, what the stream yields and when, its messages-received" "MediatorWallet live + Identus log" "E-5,E-37,E-54" "" "$C/o1b.value.json"
                fi
                # Triggers (the wallet's contract): NAME.forward.json, a forward to Identus for the
                # observer's routed DID, and NAME.inner.json, the envelope inside it; the probe posts
                # each once and compares the delivered attachment with the inner envelope (O-11).
                mkdir -p "$C/triggers"
                local routed; routed="$(json_get "$C/obs.state.json" routedDid)"
                python_forward "$routed" "$IDENTUS_DID" trig &&
                    cp "$C/trig.fwd.json" "$C/triggers/python.forward.json" && cp "$C/trig.inner.json" "$C/triggers/python.inner.json"
                wallet observe observe --state "$C/obs.state.json" --triggers "$C/triggers" --out "$C/wallet-observations.json" \
                    --allow-hosts 127.0.0.1
            fi
        fi
    } >"$C/cell.log" 2>&1
    return 0
}
if [ "$MODE" = gate ] && [ -z "${MEDIATOR_LEG_ONLY:-}" ]; then
    run_observations
fi

# ── summary ───────────────────────────────────────────────────────────────────────────────
if render_summary; then
    SUMMARY_WRITTEN=1
else
    say "render_summary failed (see $RUN/logs/render.log)"
    LEG_FAILED=1
fi
echo
cat "$RUN/totals.txt" 2>/dev/null
if [ "$LEG_FAILED" = 1 ]; then
    say "FAILED (run dir kept at $RUN)"
    exit 1
fi
say "all cells as required (summary: $SUMMARY)"
exit 0
