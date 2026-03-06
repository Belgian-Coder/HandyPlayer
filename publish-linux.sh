#!/bin/bash
# Publish HandyPlayer for Linux x64 (self-contained, portable, no .NET required)
set -e

CONFIGURATION="${1:-Release}"
APP_NAME="HandyPlayer"
VERSION="1.0.0"
RID="linux-x64"
PORTABLE_DIR="publish/$APP_NAME-$VERSION-linux-x64"
PUBLISH_DIR="publish/$RID-build"

echo "Building $APP_NAME v$VERSION for Linux x64..."

# Clean previous output
rm -rf "$PUBLISH_DIR" "$PORTABLE_DIR"

# Publish self-contained
dotnet publish src/HandyPlaylistPlayer.App \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained \
    -o "$PUBLISH_DIR"

# Create portable directory structure
mkdir -p "$PORTABLE_DIR"
cp -R "$PUBLISH_DIR/"* "$PORTABLE_DIR/"
chmod +x "$PORTABLE_DIR/$APP_NAME"

# Copy icon
if [ -f "images/h2-winegold-2a-balanced-minimal.png" ]; then
    cp "images/h2-winegold-2a-balanced-minimal.png" "$PORTABLE_DIR/app-icon.png"
fi

# Create .desktop file (users can copy to ~/.local/share/applications/)
cat > "$PORTABLE_DIR/$APP_NAME.desktop" << DESKTOP
[Desktop Entry]
Type=Application
Name=Handy Player
Comment=Media player with Handy device synchronization
Exec=INSTALL_DIR/$APP_NAME
Icon=INSTALL_DIR/app-icon.png
Terminal=false
Categories=AudioVideo;Player;
StartupWMClass=$APP_NAME
DESKTOP

# Create install helper script
cat > "$PORTABLE_DIR/install.sh" << 'INSTALL'
#!/bin/bash
# Optional installer — copies to /opt and creates desktop/launcher entries
# Run without arguments for user install, or with sudo for system-wide install
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APP_NAME="HandyPlayer"

if [ "$(id -u)" -eq 0 ]; then
    INSTALL_DIR="/opt/$APP_NAME"
    DESKTOP_DIR="/usr/share/applications"
    BIN_LINK="/usr/local/bin/$APP_NAME"
else
    INSTALL_DIR="$HOME/.local/opt/$APP_NAME"
    DESKTOP_DIR="$HOME/.local/share/applications"
    BIN_LINK="$HOME/.local/bin/$APP_NAME"
    mkdir -p "$HOME/.local/opt" "$DESKTOP_DIR" "$HOME/.local/bin"
fi

echo "Installing $APP_NAME to $INSTALL_DIR..."

# Copy files
rm -rf "$INSTALL_DIR"
cp -R "$SCRIPT_DIR" "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/$APP_NAME"
rm -f "$INSTALL_DIR/install.sh"

# Create symlink for PATH access
ln -sf "$INSTALL_DIR/$APP_NAME" "$BIN_LINK"

# Fix up .desktop file with actual install path
sed -i "s|INSTALL_DIR|$INSTALL_DIR|g" "$INSTALL_DIR/$APP_NAME.desktop"
cp "$INSTALL_DIR/$APP_NAME.desktop" "$DESKTOP_DIR/$APP_NAME.desktop"

echo ""
echo "Installed! You can:"
echo "  - Launch from your application menu"
echo "  - Run from terminal: $APP_NAME"
echo ""
echo "To uninstall: rm -rf $INSTALL_DIR $BIN_LINK $DESKTOP_DIR/$APP_NAME.desktop"
INSTALL
chmod +x "$PORTABLE_DIR/install.sh"

# Clean build dir
rm -rf "$PUBLISH_DIR"

echo ""
echo "Build complete!"
echo "  Portable: $PORTABLE_DIR/"

# Create tar.gz for distribution (portable)
TAR_PATH="publish/$APP_NAME-$VERSION-linux-x64.tar.gz"
rm -f "$TAR_PATH"

echo ""
echo "Creating portable archive: $TAR_PATH"
tar -czf "$TAR_PATH" -C publish "$(basename "$PORTABLE_DIR")"

TAR_SIZE=$(du -sh "$TAR_PATH" | cut -f1)
echo "  Archive: $TAR_PATH ($TAR_SIZE)"

# --- .deb package ---
DEB_NAME="${APP_NAME,,}-${VERSION}-linux-x64"
DEB_ROOT="publish/$DEB_NAME"
DEB_PATH="publish/$DEB_NAME.deb"
rm -rf "$DEB_ROOT" "$DEB_PATH"

echo ""
echo "Creating .deb package: $DEB_PATH"

INSTALL_DIR_DEB="/opt/$APP_NAME"

# Create deb directory structure
mkdir -p "$DEB_ROOT/DEBIAN"
mkdir -p "$DEB_ROOT/opt/$APP_NAME"
mkdir -p "$DEB_ROOT/usr/share/applications"
mkdir -p "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps"
mkdir -p "$DEB_ROOT/usr/bin"

# Copy application files
cp -R "$PORTABLE_DIR/"* "$DEB_ROOT/opt/$APP_NAME/"
rm -f "$DEB_ROOT/opt/$APP_NAME/install.sh"
rm -f "$DEB_ROOT/opt/$APP_NAME/$APP_NAME.desktop"
chmod +x "$DEB_ROOT/opt/$APP_NAME/$APP_NAME"

# Copy icon
if [ -f "images/h2-winegold-2a-balanced-minimal.png" ]; then
    cp "images/h2-winegold-2a-balanced-minimal.png" "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps/handyplayer.png"
fi

# Create symlink for PATH access
ln -sf "$INSTALL_DIR_DEB/$APP_NAME" "$DEB_ROOT/usr/bin/$APP_NAME"

# Create .desktop file
cat > "$DEB_ROOT/usr/share/applications/$APP_NAME.desktop" << DESKTOP_DEB
[Desktop Entry]
Type=Application
Name=Handy Player
Comment=Media player with Handy device synchronization
Exec=$INSTALL_DIR_DEB/$APP_NAME
Icon=handyplayer
Terminal=false
Categories=AudioVideo;Player;
StartupWMClass=$APP_NAME
DESKTOP_DEB

# Calculate installed size in KB
INSTALLED_SIZE=$(du -sk "$DEB_ROOT" | cut -f1)

# Create control file
cat > "$DEB_ROOT/DEBIAN/control" << CONTROL
Package: handyplayer
Version: $VERSION
Section: video
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_SIZE
Maintainer: HandyPlayer
Description: Media player with Handy device synchronization
 Cross-platform media player for controlling The Handy using
 Funscript playback with an embedded video player, playlists,
 auto-pairing, and network share support.
CONTROL

# Build .deb
dpkg-deb --build --root-owner-group "$DEB_ROOT" "$DEB_PATH" 2>/dev/null || \
    dpkg-deb --build "$DEB_ROOT" "$DEB_PATH"

rm -rf "$DEB_ROOT"

DEB_SIZE=$(du -sh "$DEB_PATH" | cut -f1)
echo "  Package: $DEB_PATH ($DEB_SIZE)"

# --- AppImage (optional, requires appimagetool) ---
APPIMAGE_PATH="publish/$APP_NAME-$VERSION-linux-x64.AppImage"
rm -f "$APPIMAGE_PATH"

if command -v appimagetool &> /dev/null; then
    echo ""
    echo "Creating AppImage: $APPIMAGE_PATH"

    APPDIR="publish/$APP_NAME.AppDir"
    rm -rf "$APPDIR"
    mkdir -p "$APPDIR/usr/bin"
    mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"

    # Copy application
    cp -R "$PORTABLE_DIR/"* "$APPDIR/usr/bin/"
    rm -f "$APPDIR/usr/bin/install.sh" "$APPDIR/usr/bin/$APP_NAME.desktop"
    chmod +x "$APPDIR/usr/bin/$APP_NAME"

    # Icon
    if [ -f "images/h2-winegold-2a-balanced-minimal.png" ]; then
        cp "images/h2-winegold-2a-balanced-minimal.png" "$APPDIR/handyplayer.png"
        cp "images/h2-winegold-2a-balanced-minimal.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/handyplayer.png"
    fi

    # Desktop file
    cat > "$APPDIR/$APP_NAME.desktop" << DESKTOP_AI
[Desktop Entry]
Type=Application
Name=Handy Player
Comment=Media player with Handy device synchronization
Exec=$APP_NAME
Icon=handyplayer
Terminal=false
Categories=AudioVideo;Player;
StartupWMClass=$APP_NAME
DESKTOP_AI

    # AppRun entry point
    cat > "$APPDIR/AppRun" << 'APPRUN'
#!/bin/bash
SELF=$(readlink -f "$0")
HERE=${SELF%/*}
exec "$HERE/usr/bin/HandyPlayer" "$@"
APPRUN
    chmod +x "$APPDIR/AppRun"

    ARCH=x86_64 appimagetool "$APPDIR" "$APPIMAGE_PATH"
    rm -rf "$APPDIR"

    AI_SIZE=$(du -sh "$APPIMAGE_PATH" | cut -f1)
    echo "  AppImage: $APPIMAGE_PATH ($AI_SIZE)"
else
    echo ""
    echo "Skipping AppImage (appimagetool not found). Install from https://appimage.github.io/"
fi

echo ""
echo "Build outputs:"
echo "  Portable:  $TAR_PATH ($TAR_SIZE)"
echo "  Installer: $DEB_PATH ($DEB_SIZE)"
if [ -f "$APPIMAGE_PATH" ]; then
    echo "  AppImage:  $APPIMAGE_PATH ($AI_SIZE)"
fi
echo ""
echo "Portable:  Extract tar.gz and run ./$APP_NAME"
echo "Install:   sudo dpkg -i $DEB_PATH"
echo "Uninstall: sudo dpkg -r handyplayer"
