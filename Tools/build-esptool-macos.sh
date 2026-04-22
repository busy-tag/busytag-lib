#!/usr/bin/env bash
# Builds esptool as a PyInstaller --onedir bundle for macOS.
#
# PyInstaller --onefile extracts Python + dylibs to /tmp at launch, which
# macOS Gatekeeper rejects with "'Python' Not Opened" because the extracted
# copies have no notarization ticket. --onedir keeps everything on disk inside
# the app bundle, so each component can be signed and notarized at build time.
#
# Note: the App Store upload script (mac_appstore_upload.sh) removes this
# bundle before submission because App Store rejects embedded Python3.framework.
# This build is used by Release (direct download) and AppStoreLocal only.
#
# Output: busytag-lib/Tools/macos/esptool/ (directory)
#   ├── esptool                (bootloader executable)
#   └── _internal/             (Python runtime + libs + esptool package)
#
# Usage:
#   ./busytag-lib/Tools/build-esptool-macos.sh
#   ESPTOOL_VERSION=4.8.1 ./busytag-lib/Tools/build-esptool-macos.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="$SCRIPT_DIR/macos/esptool"

ESPTOOL_VERSION="${ESPTOOL_VERSION:-4.8.1}"
PYTHON_BIN="${PYTHON:-python3}"

BUILD_DIR="$(mktemp -d -t esptool-onedir-XXXXXX)"
trap 'rm -rf "$BUILD_DIR"' EXIT

echo "=== esptool --onedir build ==="
echo "esptool version: $ESPTOOL_VERSION"
echo "Python:          $("$PYTHON_BIN" --version)"
echo "Build dir:       $BUILD_DIR"
echo "Output:          $OUTPUT_DIR"
echo

echo "[1/4] Creating venv..."
"$PYTHON_BIN" -m venv "$BUILD_DIR/venv"
# shellcheck disable=SC1091
source "$BUILD_DIR/venv/bin/activate"
pip install --quiet --upgrade pip

echo "[2/4] Installing esptool==$ESPTOOL_VERSION and pyinstaller..."
pip install --quiet "esptool==$ESPTOOL_VERSION" pyinstaller

# Write a minimal entry script. We don't use the installed esptool.py because
# it manipulates sys.modules in a way that causes RecursionError when frozen
# (PyInstaller names the frozen script "esptool" — the same name as the
# package — and esptool.py's `del sys.modules["esptool"]` + `import esptool`
# loops on itself).
ENTRY_SCRIPT="$BUILD_DIR/run_esptool.py"
cat > "$ENTRY_SCRIPT" <<'PY'
import esptool
if __name__ == "__main__":
    esptool._main()
PY

echo "[3/4] Running PyInstaller..."
cd "$BUILD_DIR"
pyinstaller \
    --onedir \
    --name esptool \
    --noconfirm \
    --clean \
    --log-level WARN \
    --collect-all esptool \
    "$ENTRY_SCRIPT"

if [ ! -x "$BUILD_DIR/dist/esptool/esptool" ]; then
    echo "ERROR: PyInstaller did not produce dist/esptool/esptool"
    exit 1
fi

echo "[4/4] Installing into $OUTPUT_DIR..."
rm -rf "$OUTPUT_DIR"
mkdir -p "$(dirname "$OUTPUT_DIR")"
mv "$BUILD_DIR/dist/esptool" "$OUTPUT_DIR"
chmod +x "$OUTPUT_DIR/esptool"

# Keep PyInstaller's ad-hoc signatures so the binary runs on dev machines.
# The csproj copy target and mac_notarization_script.sh re-sign with the
# Developer ID certificate before shipping.

echo
echo "Verifying output..."
set +e
"$OUTPUT_DIR/esptool" version
VERIFY_EXIT=$?
set -e
if [ "$VERIFY_EXIT" -ne 0 ]; then
    echo "ERROR: rebuilt esptool exited $VERIFY_EXIT"
    exit 1
fi

BYTES=$(du -sh "$OUTPUT_DIR" | awk '{print $1}')
FILES=$(find "$OUTPUT_DIR" -type f | wc -l | tr -d ' ')
echo
echo "Done. $FILES files, $BYTES total."
echo "Output: $OUTPUT_DIR"
