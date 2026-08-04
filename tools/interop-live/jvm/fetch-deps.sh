#!/usr/bin/env bash
#
# Fetch the pinned didcomm-jvm counterpart stack for the live interop harness (FR-IX-04/05)
# into jvm/libs/ — no Gradle/Maven needed, so the leg runs anywhere curl + a JDK exist.
# Every jar is pinned by version AND SHA-256; a hash mismatch aborts the run, keeping the
# nightly reproducible. didcomm-0.3.2.jar is sicpa-dlab/didcomm-jvm (it shades the patched
# nimbus-jose-jwt with ECDH-1PU and io.ipfs.multibase); tink/protobuf/gson/varint are its
# declared runtime closure (see its POM); kotlin-stdlib is the Kotlin runtime it compiles
# against (1.9.24 is consumer-compatible with the 1.5-era compiler didcomm-jvm used).
#
# Usage: bash tools/interop-live/jvm/fetch-deps.sh [libs-dir]

set -euo pipefail

LIBS="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/libs}"
mkdir -p "$LIBS"
MAVEN="https://repo1.maven.org/maven2"

# artifact-path|sha256
PINS=(
    "org/didcommx/didcomm/0.3.2/didcomm-0.3.2.jar|1c4315080f732f80e6eecf508f9ebcd955e40bc6bc3b3a18381308d2362485ea"
    "com/google/crypto/tink/tink/1.6.1/tink-1.6.1.jar|6cd3feff51ec5d5fbfa299381cd806bf247a805794d6f7ffb924c9e0d8b8f0d6"
    "com/google/protobuf/protobuf-java/3.14.0/protobuf-java-3.14.0.jar|bc9d7feab02531e15322980647379afb039fc2dccbb8f79588d1e8652efa1e00"
    "com/google/code/gson/gson/2.8.6/gson-2.8.6.jar|c8fb4839054d280b3033f800d1f5a97de2f028eb8ba2eb458ad287e536f3f25f"
    "com/zmannotes/varint/1.0.0/varint-1.0.0.jar|b3bd8cee89c3718fea72eb0a37d9fd641aca4a14da35b98ed6895313a6b45bd7"
    "org/jetbrains/kotlin/kotlin-stdlib/1.9.24/kotlin-stdlib-1.9.24.jar|858b902696da9cf585ab9d98ffc1c2712269828354dfe9107e3711b084a36468"
)

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
    else shasum -a 256 "$1" | awk '{print $1}'; fi
}

for pin in "${PINS[@]}"; do
    path="${pin%%|*}"
    expected="${pin##*|}"
    jar="$LIBS/$(basename "$path")"
    if [ ! -f "$jar" ] || [ "$(sha256_of "$jar")" != "$expected" ]; then
        echo "fetching $(basename "$path")" >&2
        curl -fsSL "$MAVEN/$path" -o "$jar"
    fi
    actual="$(sha256_of "$jar")"
    if [ "$actual" != "$expected" ]; then
        echo "SHA-256 mismatch for $(basename "$path"): expected $expected, got $actual" >&2
        exit 1
    fi
done
echo "jvm deps ready in $LIBS" >&2
