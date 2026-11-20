#!/usr/bin/env bash
# Builds the Cafe Manager release bundle: dist/CafeManager-<version>-win-x64.zip
# Env: SKIP_TESTS=1 skips the test suite; CAFE_ALLOW_MISSING_DOCS=1 allows a bundle without docs/INSTALL.md and docs/USER-GUIDE.md.
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$(pwd)"

if [ -d /usr/local/share/dotnet ]; then
  export PATH="/usr/local/share/dotnet:$PATH"
fi

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)"
if [ -z "$VERSION" ]; then
  echo "ERROR: no <Version> found in Directory.Build.props" >&2
  exit 1
fi
BUNDLE="CafeManager-${VERSION}-win-x64"
STAGE="dist/stage"
ZIP="dist/${BUNDLE}.zip"
echo "== Cafe Manager release ${VERSION} =="

echo "[a] Tests"
if [ "${SKIP_TESTS:-0}" = "1" ]; then
  echo "WARNING: SKIP_TESTS=1 - the test suite was NOT run. Do not ship this bundle." >&2
else
  dotnet test Cafe.sln -c Release --nologo -v minimal
fi

echo "[b] Cleaning ${STAGE} and ${ZIP}"
rm -rf "$STAGE" "$ZIP"
mkdir -p "$STAGE/$BUNDLE"

echo "[c] Publishing the server (win-x64, self-contained)"
dotnet publish src/Cafe.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false \
  --nologo -v minimal -o "$STAGE/$BUNDLE/Server"

echo "[d] Publishing the agent (win-x64, self-contained)"
AGENT_MODE="self-contained"
if ! dotnet publish src/Cafe.Agent -c Release -r win-x64 --self-contained true --nologo -v minimal -o "$STAGE/$BUNDLE/Agent"; then
  echo "Self-contained agent publish failed; retrying framework-dependent."
  rm -rf "$STAGE/$BUNDLE/Agent"
  dotnet publish src/Cafe.Agent -c Release -r win-x64 --self-contained false --nologo -v minimal -o "$STAGE/$BUNDLE/Agent"
  AGENT_MODE="framework-dependent"
  NOTE="Agent is framework-dependent: needs the .NET 10 Desktop Runtime on each gaming PC"
  echo "NOTE: $NOTE"
  echo "$NOTE" > "$STAGE/$BUNDLE/Agent/FRAMEWORK-DEPENDENT.txt"
fi
echo "Agent mode: $AGENT_MODE"

echo "[e] Publishing the agent simulator for this OS"
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RID="osx-arm64" ;;
  Darwin-x86_64) RID="osx-x64" ;;
  Linux-x86_64) RID="linux-x64" ;;
  Linux-aarch64 | Linux-arm64) RID="linux-arm64" ;;
  *) echo "ERROR: unsupported OS/arch for the simulator: $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac
dotnet publish src/Cafe.AgentSim -c Release -r "$RID" --self-contained false --nologo -v minimal \
  -o "$STAGE/$BUNDLE/tools/AgentSim-$RID"

echo "[f] Adding scripts, docs and README"
cp deploy/windows/*.ps1 "$STAGE/$BUNDLE/"
mkdir -p "$STAGE/$BUNDLE/docs"
if [ ! -f docs/INSTALL.md ] || [ ! -f docs/USER-GUIDE.md ]; then
  if [ "${CAFE_ALLOW_MISSING_DOCS:-0}" = "1" ]; then
    echo "WARNING: docs/INSTALL.md or docs/USER-GUIDE.md is missing (allowed by CAFE_ALLOW_MISSING_DOCS=1)." >&2
  else
    echo "ERROR: docs/INSTALL.md and docs/USER-GUIDE.md are required (set CAFE_ALLOW_MISSING_DOCS=1 to skip)." >&2
    exit 1
  fi
fi
if ls docs/*.md >/dev/null 2>&1; then
  cp docs/*.md "$STAGE/$BUNDLE/docs/"
fi
cp build/bundle-README.txt "$STAGE/$BUNDLE/README.txt"

echo "[g] Safety scan"
BAD="$(find "$STAGE" \( -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' \) -print)"
if [ -n "$BAD" ]; then
  echo "ERROR: database files in the bundle:" >&2
  echo "$BAD" >&2
  exit 1
fi
while IFS= read -r f; do
  if grep -Eq '"SharedKey"[[:space:]]*:[[:space:]]*"[^"]+"' "$f"; then
    echo "ERROR: $f contains a non-empty SharedKey" >&2
    exit 1
  fi
done < <(find "$STAGE" -name 'agentsettings.json' -print)
while IFS= read -r d; do
  if [ -n "$(find "$d" -type f -print -quit)" ]; then
    echo "ERROR: log files in the bundle: $d" >&2
    exit 1
  fi
done < <(find "$STAGE" -type d -name logs -print)

echo "[h] Zipping ${ZIP}"
mkdir -p dist
if command -v zip >/dev/null 2>&1; then
  (cd "$STAGE" && zip -r -X -q "../${BUNDLE}.zip" "$BUNDLE")
elif command -v ditto >/dev/null 2>&1; then
  ditto -c -k --keepParent "$STAGE/$BUNDLE" "$ZIP"
else
  echo "ERROR: neither zip nor ditto is available" >&2
  exit 1
fi

echo "[i] Result"
echo "Zip:    $ROOT/$ZIP"
echo "Size:   $(du -h "$ZIP" | cut -f1)"
echo "Agent:  $AGENT_MODE"
unzip -l "$ZIP" | head -n 40
echo "SHA-256: $(shasum -a 256 "$ZIP" | cut -d' ' -f1)"
