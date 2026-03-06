#!/bin/bash
# Publish HandyPlayer for macOS ARM64 (self-contained, no .NET required)

CONFIGURATION="${1:-Release}"
APP_NAME="HandyPlayer"
RID="osx-arm64"
PUBLISH_DIR="publish/$RID"
APP_BUNDLE="publish/$APP_NAME.app"
CSPROJ="src/HandyPlaylistPlayer.App/HandyPlaylistPlayer.App.csproj"

# Read version from csproj (sed works on both GNU and BSD/macOS)
VERSION=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$CSPROJ" | head -1)
VERSION="${VERSION:-1.0.0}"
ARCHIVE_NAME="$APP_NAME-$VERSION-mac-arm64"

echo "========================================="
echo "Building $APP_NAME v$VERSION for macOS ARM64"
echo "========================================="

# Clean previous output
rm -rf "$PUBLISH_DIR" "$APP_BUNDLE"

# Publish self-contained
dotnet publish src/HandyPlaylistPlayer.App \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained \
    -o "$PUBLISH_DIR"

if [ $? -ne 0 ]; then
    echo "ERROR: dotnet publish failed!"
    exit 1
fi

echo ""
echo "--- Creating .app bundle ---"

# Create .app bundle structure
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# Copy published files into the bundle
cp -R "$PUBLISH_DIR/"* "$APP_BUNDLE/Contents/MacOS/"
chmod +x "$APP_BUNDLE/Contents/MacOS/$APP_NAME"

# --- Bundle libmpv and ALL its dependencies from Homebrew ---
BREW_PREFIX=$(brew --prefix 2>/dev/null || echo "")
MACOS_DIR="$APP_BUNDLE/Contents/MacOS"

if [ -n "$BREW_PREFIX" ] && [ -f "$BREW_PREFIX/lib/libmpv.2.dylib" ]; then
    echo ""
    echo "--- Bundling libmpv and dependencies ---"

    # Use an iterative queue approach (avoids subshell issues with pipe+recursion)
    QUEUE_FILE=$(mktemp)
    DONE_FILE=$(mktemp)
    BUNDLED=0

    # Seed the queue with libmpv
    echo "$BREW_PREFIX/lib/libmpv.2.dylib" > "$QUEUE_FILE"

    while [ -s "$QUEUE_FILE" ]; do
        # Take the next library from the queue
        NEXT_QUEUE=$(mktemp)

        while IFS= read -r src; do
            name=$(basename "$src")
            dest="$MACOS_DIR/$name"

            # Skip if already processed
            if grep -qx "$name" "$DONE_FILE" 2>/dev/null; then
                continue
            fi
            echo "$name" >> "$DONE_FILE"

            # Copy the library
            echo "  Bundling: $name"
            cp "$src" "$dest"
            chmod 644 "$dest"
            BUNDLED=$((BUNDLED + 1))

            # Set its install name to @executable_path/
            install_name_tool -id "@executable_path/$name" "$dest" 2>/dev/null || true

            # Find Homebrew dependencies and queue them
            otool -L "$dest" | tail -n +2 | awk '{print $1}' > "$MACOS_DIR/_deps_tmp" 2>/dev/null
            while IFS= read -r dep; do
                case "$dep" in
                    /opt/homebrew/*|/usr/local/Cellar/*|/usr/local/opt/*|/usr/local/lib/*)
                        dep_name=$(basename "$dep")
                        # Fix the reference to use @executable_path
                        install_name_tool -change "$dep" "@executable_path/$dep_name" "$dest" 2>/dev/null || true
                        # Queue this dependency if not yet processed and file exists
                        if ! grep -qx "$dep_name" "$DONE_FILE" 2>/dev/null && [ -f "$dep" ]; then
                            echo "$dep" >> "$NEXT_QUEUE"
                        fi
                        ;;
                esac
            done < "$MACOS_DIR/_deps_tmp"
            rm -f "$MACOS_DIR/_deps_tmp"

        done < "$QUEUE_FILE"

        mv "$NEXT_QUEUE" "$QUEUE_FILE"
    done

    rm -f "$QUEUE_FILE" "$DONE_FILE"
    echo "  Bundled $BUNDLED libraries total"
else
    echo ""
    echo "Note: Homebrew libmpv not found — app will use system-installed mpv."
    echo "  Install with: brew install mpv"
fi

# Ad-hoc sign all native libraries (required on Apple Silicon)
echo ""
echo "--- Signing ---"
find "$APP_BUNDLE/Contents/MacOS" -name "*.dylib" -exec codesign --force --sign - {} \;
codesign --force --sign - "$APP_BUNDLE/Contents/MacOS/$APP_NAME"
echo "  Signed all binaries"

# Generate .icns from PNG using macOS native tools
# Try multiple icon source locations
ICON_PNG=""
for candidate in \
    "images/h2-winegold-2a-balanced-minimal.png" \
    "src/HandyPlaylistPlayer.App/Assets/app-icon.png" \
    "Assets/app-icon.png"; do
    if [ -f "$candidate" ]; then
        ICON_PNG="$candidate"
        break
    fi
done

echo ""
echo "--- App icon ---"
if [ -n "$ICON_PNG" ]; then
    echo "  Source: $ICON_PNG"
    ICONSET_DIR=$(mktemp -d)/app-icon.iconset
    mkdir -p "$ICONSET_DIR"

    sips -z 16 16     "$ICON_PNG" --out "$ICONSET_DIR/icon_16x16.png"      > /dev/null 2>&1
    sips -z 32 32     "$ICON_PNG" --out "$ICONSET_DIR/icon_16x16@2x.png"   > /dev/null 2>&1
    sips -z 32 32     "$ICON_PNG" --out "$ICONSET_DIR/icon_32x32.png"      > /dev/null 2>&1
    sips -z 64 64     "$ICON_PNG" --out "$ICONSET_DIR/icon_32x32@2x.png"   > /dev/null 2>&1
    sips -z 128 128   "$ICON_PNG" --out "$ICONSET_DIR/icon_128x128.png"    > /dev/null 2>&1
    sips -z 256 256   "$ICON_PNG" --out "$ICONSET_DIR/icon_128x128@2x.png" > /dev/null 2>&1
    sips -z 256 256   "$ICON_PNG" --out "$ICONSET_DIR/icon_256x256.png"    > /dev/null 2>&1
    sips -z 512 512   "$ICON_PNG" --out "$ICONSET_DIR/icon_256x256@2x.png" > /dev/null 2>&1
    sips -z 512 512   "$ICON_PNG" --out "$ICONSET_DIR/icon_512x512.png"    > /dev/null 2>&1
    sips -z 1024 1024 "$ICON_PNG" --out "$ICONSET_DIR/icon_512x512@2x.png" > /dev/null 2>&1

    iconutil -c icns "$ICONSET_DIR" -o "$APP_BUNDLE/Contents/Resources/app-icon.icns"
    rm -rf "$(dirname "$ICONSET_DIR")"
    echo "  Generated: app-icon.icns"
else
    echo "  WARNING: No icon PNG found. Tried:"
    echo "    images/h2-winegold-2a-balanced-minimal.png"
    echo "    src/HandyPlaylistPlayer.App/Assets/app-icon.png"
    echo "  Copy an icon PNG to one of these locations and re-run."
fi

# Create Info.plist (with dynamic version)
cat > "$APP_BUNDLE/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>HandyPlayer</string>
    <key>CFBundleDisplayName</key>
    <string>Handy Player</string>
    <key>CFBundleIdentifier</key>
    <string>com.handyplayer.app</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleExecutable</key>
    <string>HandyPlayer</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>CFBundleIconFile</key>
    <string>app-icon</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSHumanReadableCopyright</key>
    <string>HandyPlayer</string>
</dict>
</plist>
PLIST

echo ""
echo "--- App bundle ready ---"
echo "  $APP_BUNDLE"
echo "  To test: open $APP_BUNDLE"

# --- Create ZIP ---
echo ""
echo "--- Creating ZIP ---"
ZIP_NAME="$ARCHIVE_NAME.zip"
ZIP_PATH="publish/$ZIP_NAME"
rm -f "$ZIP_PATH"

(cd publish && zip -r -y "$ZIP_NAME" "$APP_NAME.app")

if [ -f "$ZIP_PATH" ]; then
    SIZE=$(du -sh "$ZIP_PATH" | cut -f1)
    echo "  $ZIP_PATH ($SIZE)"
else
    echo "  WARNING: ZIP creation failed"
fi

# --- Create DMG ---
echo ""
echo "--- Creating DMG ---"
DMG_NAME="$ARCHIVE_NAME.dmg"
DMG_PATH="publish/$DMG_NAME"
DMG_STAGING="publish/dmg-staging"
rm -f "$DMG_PATH"
rm -rf "$DMG_STAGING"

mkdir -p "$DMG_STAGING"
cp -R "$APP_BUNDLE" "$DMG_STAGING/"
ln -s /Applications "$DMG_STAGING/Applications"

hdiutil create -volname "$APP_NAME" \
    -srcfolder "$DMG_STAGING" \
    -ov -format UDZO \
    "$DMG_PATH"

rm -rf "$DMG_STAGING"

if [ -f "$DMG_PATH" ]; then
    SIZE=$(du -sh "$DMG_PATH" | cut -f1)
    echo "  $DMG_PATH ($SIZE)"
else
    echo "  WARNING: DMG creation failed"
fi

# Clean intermediate publish dir
rm -rf "$PUBLISH_DIR"

echo ""
echo "========================================="
echo "Build outputs:"
[ -f "$ZIP_PATH" ] && echo "  Portable:  $ZIP_PATH"
[ -f "$DMG_PATH" ] && echo "  Installer: $DMG_PATH"
echo "========================================="
