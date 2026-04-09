#!/bin/sh
set -e

SRC="enet.c"
BUILD_DIR="build"

IOS_DIR="$BUILD_DIR/ios"
XROS_DIR="$BUILD_DIR/visionos"
XRSIM_DIR="$BUILD_DIR/visionos-sim"

mkdir -p "$IOS_DIR" "$XROS_DIR" "$XRSIM_DIR"

########################################
# 1) iOS (device)
########################################
IOS_SDKROOT="$(xcrun --sdk iphoneos --show-sdk-path)"

xcrun --sdk iphoneos clang \
  -c "$SRC" \
  -target arm64-apple-ios \
  -isysroot "$IOS_SDKROOT" \
  -o "$IOS_DIR/enet.o"

xcrun --sdk iphoneos libtool \
  -static "$IOS_DIR/enet.o" \
  -o "$IOS_DIR/libenet.a"

########################################
# 2) visionOS (device)
########################################
XROS_SDKROOT="$(xcrun --sdk xros --show-sdk-path)"

xcrun --sdk xros clang \
  -c "$SRC" \
  -target arm64-apple-xros1.0 \
  -isysroot "$XROS_SDKROOT" \
  -o "$XROS_DIR/enet.o"

xcrun --sdk xros libtool \
  -static "$XROS_DIR/enet.o" \
  -o "$XROS_DIR/libenet.a"

########################################
# 3) visionOS Simulator
########################################
XRSIM_SDKROOT="$(xcrun --sdk xrsimulator --show-sdk-path)"

xcrun --sdk xrsimulator clang \
  -c "$SRC" \
  -target arm64-apple-xros1.0-simulator \
  -isysroot "$XRSIM_SDKROOT" \
  -o "$XRSIM_DIR/enet.o"

xcrun --sdk xrsimulator libtool \
  -static "$XRSIM_DIR/enet.o" \
  -o "$XRSIM_DIR/libenet.a"

########################################
# Cleanup
########################################
rm -f \
  "$IOS_DIR/enet.o" \
  "$XROS_DIR/enet.o" \
  "$XRSIM_DIR/enet.o"

echo "Build complete:"
echo "  $IOS_DIR/libenet.a"
echo "  $XROS_DIR/libenet.a"
echo "  $XRSIM_DIR/libenet.a"