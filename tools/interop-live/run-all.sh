#!/usr/bin/env bash
#
# Live cross-implementation interop, all legs (PRD §13.6 FR-IX-04/05/08).
#
# Runs the python leg (didcomm-dotnet ↔ sicpa-dlab/didcomm-python) and the JVM leg
# (didcomm-dotnet ↔ sicpa-dlab/didcomm-jvm), builds InteropCli once and shares it, and
# produces a combined markdown summary. A leg failure does not stop the other leg; the exit
# code is nonzero if ANY leg failed. The nightly workflow (.github/workflows/interop-live.yml)
# uploads $INTEROP_SUMMARY_DIR as its artifact.
#
# Usage: bash tools/interop-live/run-all.sh
#   INTEROP_SUMMARY_DIR  where the per-leg + combined summaries land
#                        (default: a fresh temp dir, path printed at the end)

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
export INTEROP_SUMMARY_DIR="${INTEROP_SUMMARY_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/didcomm-interop-summary.XXXXXX")}"
mkdir -p "$INTEROP_SUMMARY_DIR"

# Build the didcomm-dotnet side once; both legs reuse it.
set -e
dotnet build "$REPO_ROOT/tools/InteropCli/InteropCli.csproj" -c Release --nologo -v quiet >&2
export INTEROP_CLI="$REPO_ROOT/tools/InteropCli/bin/Release/net10.0/InteropCli"
set +e

OVERALL=0
declare -a LEG_STATUS

echo "=== python leg ==="
bash "$SCRIPT_DIR/run-python-leg.sh"
if [ $? -eq 0 ]; then LEG_STATUS+=("python: PASS"); else LEG_STATUS+=("python: FAIL"); OVERALL=1; fi

echo
echo "=== jvm leg ==="
bash "$SCRIPT_DIR/run-jvm-leg.sh"
if [ $? -eq 0 ]; then LEG_STATUS+=("jvm: PASS"); else LEG_STATUS+=("jvm: FAIL"); OVERALL=1; fi

COMBINED="$INTEROP_SUMMARY_DIR/summary.md"
{
    echo "# Live cross-implementation interop (FR-IX-04/05)"
    echo
    echo "didcomm-dotnet @ $(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown) — $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    echo
    for status in "${LEG_STATUS[@]}"; do echo "- $status"; done
    echo
    for leg in python-leg jvm-leg; do
        if [ -f "$INTEROP_SUMMARY_DIR/$leg.md" ]; then
            cat "$INTEROP_SUMMARY_DIR/$leg.md"
            echo
        else
            echo "## $leg: no summary produced (leg aborted before the matrix ran)"
            echo
        fi
    done
} > "$COMBINED"

echo
echo "=== combined summary: $COMBINED ==="
for status in "${LEG_STATUS[@]}"; do echo "$status"; done
exit "$OVERALL"
