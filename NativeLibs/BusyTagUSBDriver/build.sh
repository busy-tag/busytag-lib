#!/bin/bash
# Build BusyTagUSBDriver for Mac Catalyst (.framework) and plain macOS (.dylib)
#
# Usage: ./build.sh
# Output:
#   ../../Platforms/MacCatalyst/BusyTagUSBDriver.framework/  (for MAUI/Catalyst apps)
#   ../../Platforms/macOS/libBusyTagUSBDriver.dylib           (for .NET console apps)

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
FRAMEWORK_NAME="BusyTagUSBDriver"
BUILD_DIR="$SCRIPT_DIR/build"
SDK_PATH=$(xcrun --show-sdk-path --sdk macosx)
MIN_IOS_VERSION="15.0"
MIN_MACOS_VERSION="12.0"
SOURCES="$SCRIPT_DIR/Sources/USBDeviceManager.swift $SCRIPT_DIR/Sources/CInterface.swift"

# ============================================================
# Part 1: Mac Catalyst framework (for MAUI apps)
# ============================================================

CATALYST_OUTPUT_DIR="$SCRIPT_DIR/../../Platforms/MacCatalyst"
FRAMEWORK_DIR="$CATALYST_OUTPUT_DIR/$FRAMEWORK_NAME.framework"

echo "=== Building $FRAMEWORK_NAME for Mac Catalyst ==="
echo "SDK: $SDK_PATH"
echo "Output: $FRAMEWORK_DIR"

# Clean previous build
rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR/catalyst-arm64"
mkdir -p "$BUILD_DIR/catalyst-x86_64"
mkdir -p "$FRAMEWORK_DIR/Headers"
mkdir -p "$FRAMEWORK_DIR/Modules"

# Build for arm64 Mac Catalyst
echo ""
echo "--- Compiling arm64 (Catalyst) ---"
swiftc \
    -target arm64-apple-ios${MIN_IOS_VERSION}-macabi \
    -sdk "$SDK_PATH" \
    -emit-library \
    -emit-module \
    -module-name "$FRAMEWORK_NAME" \
    -o "$BUILD_DIR/catalyst-arm64/lib${FRAMEWORK_NAME}.dylib" \
    -Xlinker -install_name -Xlinker "@rpath/${FRAMEWORK_NAME}.framework/${FRAMEWORK_NAME}" \
    $SOURCES \
    -framework IOKit \
    -framework CoreFoundation \
    -O

# Build for x86_64 Mac Catalyst
echo ""
echo "--- Compiling x86_64 (Catalyst) ---"
swiftc \
    -target x86_64-apple-ios${MIN_IOS_VERSION}-macabi \
    -sdk "$SDK_PATH" \
    -emit-library \
    -emit-module \
    -module-name "$FRAMEWORK_NAME" \
    -o "$BUILD_DIR/catalyst-x86_64/lib${FRAMEWORK_NAME}.dylib" \
    -Xlinker -install_name -Xlinker "@rpath/${FRAMEWORK_NAME}.framework/${FRAMEWORK_NAME}" \
    $SOURCES \
    -framework IOKit \
    -framework CoreFoundation \
    -O

# Create universal binary for Catalyst
echo ""
echo "--- Creating Catalyst universal binary ---"
lipo -create \
    "$BUILD_DIR/catalyst-arm64/lib${FRAMEWORK_NAME}.dylib" \
    "$BUILD_DIR/catalyst-x86_64/lib${FRAMEWORK_NAME}.dylib" \
    -output "$FRAMEWORK_DIR/$FRAMEWORK_NAME"

# Copy headers
cp "$SCRIPT_DIR/Headers/${FRAMEWORK_NAME}.h" "$FRAMEWORK_DIR/Headers/"

# Create module map
cat > "$FRAMEWORK_DIR/Modules/module.modulemap" << 'MODULEMAP'
framework module BusyTagUSBDriver {
    umbrella header "BusyTagUSBDriver.h"
    export *
    module * { export * }
}
MODULEMAP

# Copy Info.plist
cp "$SCRIPT_DIR/Info.plist" "$FRAMEWORK_DIR/"

# Sign the framework (ad-hoc for development)
echo ""
echo "--- Signing Catalyst framework ---"
codesign --force --sign - "$FRAMEWORK_DIR/$FRAMEWORK_NAME"

echo ""
echo "=== Catalyst build complete ==="
file "$FRAMEWORK_DIR/$FRAMEWORK_NAME"

# ============================================================
# Part 2: Plain macOS dylib (for .NET console apps / CLI)
# ============================================================

MACOS_OUTPUT_DIR="$SCRIPT_DIR/../../Platforms/macOS"
DYLIB_PATH="$MACOS_OUTPUT_DIR/lib${FRAMEWORK_NAME}.dylib"

echo ""
echo "=== Building $FRAMEWORK_NAME for plain macOS ==="
echo "Output: $DYLIB_PATH"

mkdir -p "$BUILD_DIR/macos-arm64"
mkdir -p "$BUILD_DIR/macos-x86_64"
mkdir -p "$MACOS_OUTPUT_DIR"

# Build for arm64 macOS
echo ""
echo "--- Compiling arm64 (macOS) ---"
swiftc \
    -target arm64-apple-macos${MIN_MACOS_VERSION} \
    -sdk "$SDK_PATH" \
    -emit-library \
    -emit-module \
    -module-name "$FRAMEWORK_NAME" \
    -o "$BUILD_DIR/macos-arm64/lib${FRAMEWORK_NAME}.dylib" \
    -Xlinker -install_name -Xlinker "@loader_path/lib${FRAMEWORK_NAME}.dylib" \
    $SOURCES \
    -framework IOKit \
    -framework CoreFoundation \
    -O

# Build for x86_64 macOS
echo ""
echo "--- Compiling x86_64 (macOS) ---"
swiftc \
    -target x86_64-apple-macos${MIN_MACOS_VERSION} \
    -sdk "$SDK_PATH" \
    -emit-library \
    -emit-module \
    -module-name "$FRAMEWORK_NAME" \
    -o "$BUILD_DIR/macos-x86_64/lib${FRAMEWORK_NAME}.dylib" \
    -Xlinker -install_name -Xlinker "@loader_path/lib${FRAMEWORK_NAME}.dylib" \
    $SOURCES \
    -framework IOKit \
    -framework CoreFoundation \
    -O

# Create universal binary for macOS
echo ""
echo "--- Creating macOS universal binary ---"
lipo -create \
    "$BUILD_DIR/macos-arm64/lib${FRAMEWORK_NAME}.dylib" \
    "$BUILD_DIR/macos-x86_64/lib${FRAMEWORK_NAME}.dylib" \
    -output "$DYLIB_PATH"

# Sign the dylib (ad-hoc for development)
echo ""
echo "--- Signing macOS dylib ---"
codesign --force --sign - "$DYLIB_PATH"

# Clean up build intermediates
rm -rf "$BUILD_DIR"

echo ""
echo "=== macOS build complete ==="
file "$DYLIB_PATH"
otool -L "$DYLIB_PATH"

echo ""
echo "=== All builds complete ==="
