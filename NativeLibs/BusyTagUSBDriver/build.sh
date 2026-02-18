#!/bin/bash
# Build BusyTagUSBDriver.framework for Mac Catalyst (arm64 + x86_64 universal)
#
# Usage: ./build.sh
# Output: ../../Platforms/MacCatalyst/BusyTagUSBDriver.framework/

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
FRAMEWORK_NAME="BusyTagUSBDriver"
BUILD_DIR="$SCRIPT_DIR/build"
OUTPUT_DIR="$SCRIPT_DIR/../../Platforms/MacCatalyst"
FRAMEWORK_DIR="$OUTPUT_DIR/$FRAMEWORK_NAME.framework"
SDK_PATH=$(xcrun --show-sdk-path --sdk macosx)
MIN_IOS_VERSION="15.0"
SOURCES="$SCRIPT_DIR/Sources/USBDeviceManager.swift $SCRIPT_DIR/Sources/CInterface.swift"

echo "=== Building $FRAMEWORK_NAME for Mac Catalyst ==="
echo "SDK: $SDK_PATH"
echo "Output: $FRAMEWORK_DIR"

# Clean previous build
rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR/arm64"
mkdir -p "$BUILD_DIR/x86_64"
mkdir -p "$FRAMEWORK_DIR/Headers"
mkdir -p "$FRAMEWORK_DIR/Modules"

# Build for arm64 Mac Catalyst
echo ""
echo "--- Compiling arm64 ---"
swiftc \
    -target arm64-apple-ios${MIN_IOS_VERSION}-macabi \
    -sdk "$SDK_PATH" \
    -emit-library \
    -emit-module \
    -module-name "$FRAMEWORK_NAME" \
    -o "$BUILD_DIR/arm64/lib${FRAMEWORK_NAME}.dylib" \
    -Xlinker -install_name -Xlinker "@rpath/${FRAMEWORK_NAME}.framework/${FRAMEWORK_NAME}" \
    $SOURCES \
    -framework IOKit \
    -framework CoreFoundation \
    -O

# Build for x86_64 Mac Catalyst
echo ""
echo "--- Compiling x86_64 ---"
swiftc \
    -target x86_64-apple-ios${MIN_IOS_VERSION}-macabi \
    -sdk "$SDK_PATH" \
    -emit-library \
    -emit-module \
    -module-name "$FRAMEWORK_NAME" \
    -o "$BUILD_DIR/x86_64/lib${FRAMEWORK_NAME}.dylib" \
    -Xlinker -install_name -Xlinker "@rpath/${FRAMEWORK_NAME}.framework/${FRAMEWORK_NAME}" \
    $SOURCES \
    -framework IOKit \
    -framework CoreFoundation \
    -O

# Create universal binary
echo ""
echo "--- Creating universal binary ---"
lipo -create \
    "$BUILD_DIR/arm64/lib${FRAMEWORK_NAME}.dylib" \
    "$BUILD_DIR/x86_64/lib${FRAMEWORK_NAME}.dylib" \
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
echo "--- Signing framework ---"
codesign --force --sign - "$FRAMEWORK_DIR/$FRAMEWORK_NAME"

# Clean up build intermediates
rm -rf "$BUILD_DIR"

echo ""
echo "=== Build complete ==="
echo "Framework: $FRAMEWORK_DIR"
file "$FRAMEWORK_DIR/$FRAMEWORK_NAME"
otool -L "$FRAMEWORK_DIR/$FRAMEWORK_NAME"
