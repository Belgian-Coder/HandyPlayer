# HandyPlayer

Cross-platform desktop app for controlling **The Handy** using Funscript playback with an embedded video player, playlists, auto-pairing, and network share support.

Built with **.NET 10**, **Avalonia UI**, and **libmpv** (via P/Invoke).

**Important!**
This project was created in 6 days with the use of LLMs, to help me test out some methodologies and explore some of the limits of current models.
No plans on maintaining this applicatino in the future, feel free to adapt and redistribute.


## Features

### Video Playback

- **Embedded video player** powered by libmpv (MP4, MKV, WebM, and more)
- Standard transport controls with icon buttons: play/pause, seek forward/backward, next/previous, fullscreen toggle
- **Fullscreen mode** — double-click video, press `F11`/`F`, or click the fullscreen button
  - Auto-hiding controls: transport and seekbar appear on mouse movement and hide after a configurable timeout (default 3 seconds)
  - Mouse cursor auto-hides in fullscreen
  - Queue panel stays visible on the right side for track selection
  - Exit via `Escape`, double-click, or fullscreen toggle button
  - First-use hint shows "Press Escape or double-click to exit"
- Volume control with keyboard shortcuts (`Up`/`Down` arrows, `M` to mute)
- Funscript heatmap visualization below the seekbar
- **Script movement visualizer** — collapsible strip on the left of the video showing a live dot that tracks the funscript device position in real time (toggle via the trend-line button in transport controls, only visible when a script is loaded)
- Seek slider with drag support
- Configurable seek step (default 15s, change in Settings)
- Time display (current position / duration)
- **Seek preview** — floating timestamp tooltip above the slider while dragging
- **Resume playback** — remembers last position per file, resumes on next play (skips if near start/end)
- **A-B Loop** — mark two points and loop that section. Set A (`[`), set B (`]`), clear (`\`). Visual gold markers on seekbar heatmap.
- **Playback speed** — 0.25x to 2.0x via mpv. Funscript timestamps and device sync scale proportionally.
- **Random scene sampler** — "Shuffle Scenes" mode that jumps to random positions within the current video every N seconds
- **Warm-up ramp** — configurable ramp-up period at start of playback, gradually scaling device intensity from 0% to 100%
- **Video equalizer** — live brightness, contrast, saturation, gamma, and hue adjustments via collapsible EQ panel
- **Audio/subtitle track selection** — choose audio and subtitle tracks from the Tracks panel; displays track ID, title, language, and codec
- **Audio EQ** — 5-band equalizer for audio frequency adjustment
- **Gapless queue transitions** — pre-parses next queue item's script + pre-uploads to Handy hosting (HSSP) before current track ends (configurable prepare window, default 30s)

### Device Control

- **Handy API** (Cloud) — two protocol modes selectable in Settings:
  - **HSSP** (server-synced): Uploads script to Handy hosting, server handles all timing. Periodic resync with drift detection (soft every 10s, hard on >500ms drift). Best for reliability — no continuous network traffic during playback. Override controls do **not** apply (server plays the raw script).
  - **HDSP** (direct streaming): Client sends positions in real-time via the tick engine at 10ms intervals. Override controls (range, invert, speed limit) apply in real-time. Higher network traffic but full control over transforms.
  - SHA-256 script caching to avoid re-uploads (HSSP)
  - Server time sync with 30-round RTD measurements and IQR outlier rejection
  - Exponential backoff retry on network errors
  - Auto-detected latency offset from network RTT on connect
  - **Adaptive offset** — periodic RTT probing (every 30s) automatically adjusts sync offset when network conditions change
  - **Connection health indicator** — real-time RTT, protocol, and connection quality display with colored dot (green/yellow/red based on latency)
  - **Auto-reconnect** — automatically reconnects on connection drop with exponential backoff (up to 5 retries)
  - **HSSP playback rate** — speed changes (0.5x-2.0x) are synced to the device when using HSSP protocol
- **Intiface Central** (Bluetooth/Local) — Streaming mode via Buttplug protocol
  - Real-time position streaming at 10ms tick rate
  - Override controls apply in real-time
  - No cloud dependency; works fully offline
- **Sync calibration** — Visual calibration tool to fine-tune latency offset
- **Emergency Stop** — Red button + `Escape` hotkey, immediately stops all device output

### Library Management

- Add multiple library root folders (local drives, SMB shares, mapped network drives)
- Automatic recursive scanning of video and script files
- Resilient scanning: tolerates network disconnects, permission errors, timeouts
- Search and filter by filename or folder path
- Per-root scan status tracking
- Double-click any item to play
- Right-click context menu: play, mark watched/unwatched, delete from disk
- **Multi-select** — Checkbox selection for bulk operations (add to queue, mark watched/unwatched, delete)
- **Grid view** — toggle between list view and card-style grid view with file metadata
- **Script preview** — Funscript heatmap shown when selecting a paired item in the library
- **Watched tracking** — items auto-mark as watched after reaching a configurable threshold (default 90%); toggle via context menu
- **Duplicate detection** — scan library for duplicate files by partial SHA-256 hash (first 1MB + file size). Results grouped by hash.
- **Missing files panel** — detect and fix broken file paths; relocate individual files or bulk-remove missing entries
- **Delete from disk** — removes video + paired funscript with confirmation dialog

### Auto-Pairing

Automatically matches video files with their corresponding funscript files:

1. **Exact match** (same folder, same basename) - Confidence: 100%
2. **Normalized match** (same folder, tags stripped) - Confidence: 90%
3. **Cross-folder match** (any folder, normalized name) - Confidence: 70%

Normalization strips brackets, known tags (1080p, 4k, x264, hevc, 60fps, etc.), replaces separators, and lowercases for fuzzy matching.

Manual pairings override auto-pairing and are never replaced.

Per-video script offset can be saved to a pairing for automatic recall on future plays.

### Playlists & Queue

- Create, rename, and delete playlists
- **Static playlists** — manually curated with drag-drop reordering
- **Folder playlists** — auto-populate from a folder path
- **Smart playlists** — filter-based auto-playlists (e.g., "all unwatched", "all paired", "multiple scripts")
- Add media items to playlists with ordering
- **Play All** — sequential playback
- **Shuffle Play** — Fisher-Yates randomized order
- **Continuous Shuffle** — wraps around with re-shuffle on loop
- **Repeat modes** — repeat all, repeat one
- Queue with **drag-drop reorder** — drag items to rearrange, visual drop indicator and ghost label during drag
- **Now-playing highlight** — currently playing item shown in bold in the queue list
- **Queue sort** — sort by title, duration, or watched status via the sort flyout menu
- **Next unwatched** — jump to the next unwatched item in the queue
- **Thumbnail tooltips** — hover over queue items to see a video thumbnail preview
- **Save queue as playlist** — save current queue contents as a new playlist
- **Queue persistence** — queue state is saved on exit and restored on launch
- Queue total duration display in header
- Next/Previous navigation with history tracking
- Auto-advance to next track on media end
- **M3U import/export** — export playlists as M3U files, import M3U playlists with automatic library matching

### Movement Controls (Override Panel)

Access the override panel from the player controls or press `O`. These controls apply in real-time for HDSP and Intiface modes. In HSSP mode, only Offset affects sync timing.

- **Movement Range** — Upper and lower limits (0-100) controlling the device's travel range
- **Latency Offset** — Compensate for network/device latency (-500ms to +500ms), with +/-10ms and +/-50ms nudge buttons. **Save to Video** button persists offset to the current video's script pairing for automatic recall.
- **Invert** — Reverse the direction of all device motion
- **Speed Limit** — Maximum position change per second (prevents overly fast movements)
- **Intensity** — Scale movement amplitude around midpoint without changing range limits (e.g., 50% halves all movements)
- **Edging throttle** — automatically moderate high-intensity script sequences. Configurable speed threshold and reduction factor.
- **Smoothing** — exponential moving average on positions to reduce jitter from noisy scripts

**Presets:**
- Save current settings as named presets (includes advanced fields: smoothing, gamma, tick rate)
- Load presets from the dropdown
- **Preset-playlist association** — auto-apply a preset when a specific playlist starts
- Reset to defaults with one click

### Pattern Mode

Generate device patterns without loading a video:

- **Pattern Types**: Sine, Sawtooth, Square, Triangle, Random
- **Configurable Parameters**: Frequency (Hz), amplitude range (min/max), duration
- **Random Seeds**: Reproducible random patterns with optional seed input
- Useful for testing device settings and calibrating overrides

### Statistics

- Watch count per video
- Total library size and paired percentage
- Recently played history with timestamps
- Playback duration tracking

### Settings

Organized into tabbed sections:

- **Connection** — Backend selection, connection key, protocol mode
- **Calibration** — Visual sync calibration tool with offset adjustment
- **Video Engine** — VO backend (GPU stable / GPU-Next modern, restart required), AI upscaling preset (None · CAS Sharpen · FSR 1.0 · NIS · RAVU Lite · FSRCNNX · NLMeans · Anime4K Fast · Anime4K Quality · MetalFX Spatial macOS · Auto Fast · Auto Quality), hardware decode, GPU rendering API, demuxer cache, upscaling filter, audio loudness normalization, ICC color profile, tone mapping, max volume, pitch correction, video sync, dithering. Most settings apply immediately; VO backend and AI upscaling require a full restart. Incompatible presets (e.g. MetalFX on Windows) are automatically disabled in the UI and rejected on load.
- **Player Controls** — toggle visibility of individual transport bar buttons (nav, fullscreen, A-B loop, speed, queue, overrides, EQ, tracks, volume)
- **Appearance** — theme palette (5 dark presets: Dark Navy, AMOLED Black, Dark Gray, Dracula, Slate; applies instantly), accent color selection from presets or any custom hex color (`#RRGGBB`)
- **Library** — Library root folder management, database cache clearing
- **Keys** — Customizable keyboard shortcuts with click-to-rebind
- **Playback** — Configurable watched threshold (10-100%, default 90%), gapless prepare window (seconds before end to pre-load next), fullscreen auto-hide timeout, and thumbnail dimensions (width × height)
- **Data** — Import/export all settings as JSON (V3 format: appearance, MPV settings, playback settings, player control visibility, device settings, keybindings, library roots, playlists, and presets)

### Keyboard Shortcuts

All shortcuts are customizable in Settings > Keys. Defaults:

| Key | Action |
|---|---|
| `Space` | Play / Pause |
| `Escape` | Exit fullscreen (if fullscreen) / Emergency Stop |
| `F11` / `F` | Toggle fullscreen |
| `Left Arrow` | Seek backward (configurable step, default 15s) |
| `Right Arrow` | Seek forward (configurable step, default 15s) |
| `N` | Next track |
| `P` | Previous track |
| `+` | Nudge offset +10ms |
| `-` | Nudge offset -10ms |
| `O` | Toggle override panel |
| `[` | Set A-B loop point A |
| `]` | Set A-B loop point B |
| `\` | Clear A-B loop |
| `Up Arrow` | Volume up |
| `Down Arrow` | Volume down |
| `M` | Toggle mute |
| `?` | Show shortcut help overlay |

---

## Supported Formats

### Container Formats (Video)

The library scanner indexes files with these extensions: `.mp4`, `.mkv`, `.webm`, `.avi`, `.wmv`, `.mov`, `.m4v`, `.flv`

Since playback is powered by **libmpv** (which uses FFmpeg internally), virtually any container/codec combination that FFmpeg supports will play correctly — the extension list above only controls which files appear in the library.

### Video Codecs

All codecs supported by FFmpeg/libmpv, including:

| Codec | Notes |
|---|---|
| H.264 / AVC | Most common; hardware decode supported on all platforms |
| H.265 / HEVC | 4K/HDR content; hardware decode on modern GPUs |
| VP8, VP9 | WebM containers; hardware decode on newer GPUs |
| AV1 | Next-gen codec; hardware decode on latest GPUs (Intel 12th+, NVIDIA RTX 30+, AMD RX 7000+) |
| MPEG-2, MPEG-4 | Legacy formats |
| WMV / VC-1 | Windows Media |
| Theora | Ogg containers |

Hardware decoding (`--hwdec=auto`) is enabled by default and can be configured in Settings > Video Engine.

### Audio Codecs

All codecs supported by FFmpeg/libmpv, including:

| Codec | Notes |
|---|---|
| AAC | Most common in MP4 containers |
| MP3 | Universal support |
| FLAC | Lossless audio |
| Opus | Modern, efficient codec (WebM/MKV) |
| Vorbis | Ogg containers |
| AC3 / E-AC3 | Dolby Digital surround |
| DTS | DTS surround sound |
| PCM / WAV | Uncompressed audio |
| TrueHD, DTS-HD MA | Lossless surround (MKV containers) |

### Script Format

`.funscript` — JSON-based haptic script format containing timestamped position actions (0-100 range). Automatically paired with video files by filename matching.

---

## Installation

### Pre-Built Packages (Recommended)

Download the latest release for your platform. No .NET SDK or runtime installation required — everything is bundled.

#### Windows 11 (x64)

**Portable (zip):**
1. Download `HandyPlayer-1.0.0-win-x64.zip`
2. Extract to a folder of your choice (e.g. `C:\HandyPlayer\`)
3. Run `HandyPlayer.exe`

**Installer (msi):**
1. Download `HandyPlayer-1.0.0-win-x64.msi`
2. Run the MSI installer and follow the prompts
3. Launch from the Start menu

To build yourself:
```powershell
./publish-win.ps1                  # portable zip + MSI installer (if WiX is installed)
```

#### macOS (Apple Silicon)

Requires macOS 12.0 (Monterey) or later.

**Portable (zip):**
1. Download `HandyPlayer-1.0.0-mac-arm64.zip`
2. Extract and drag `HandyPlayer.app` to `/Applications`
3. On first launch, right-click → "Open" → click "Open" in the dialog (Gatekeeper requires this once for unsigned apps)

**Installer (dmg):**
1. Download `HandyPlayer-1.0.0-mac-arm64.dmg`
2. Open the DMG and drag `HandyPlayer` to the `Applications` folder
3. On first launch, right-click → "Open" → click "Open" in the dialog

If macOS blocks the app with "Apple could not verify", go to **System Settings → Privacy & Security** and click **Open Anyway**.

> **Intel Mac users:** Pre-built packages are Apple Silicon (ARM64) only. Intel Macs can run them via Rosetta 2, or you can build from source with `dotnet publish -r osx-x64` (see Development Setup for libmpv prerequisites).

To build yourself:
```bash
chmod +x publish-mac.sh
./publish-mac.sh                   # portable zip + DMG installer
```

#### Linux (x64)

**Portable (tar.gz):**
1. Download `HandyPlayer-1.0.0-linux-x64.tar.gz`
2. Extract: `tar -xzf HandyPlayer-1.0.0-linux-x64.tar.gz`
3. Run: `./HandyPlayer-1.0.0-linux-x64/HandyPlayer`
4. Optional: run `./install.sh` to install to `~/.local/opt` with desktop entry

**Debian/Ubuntu (.deb):**
1. Download `handyplayer-1.0.0-linux-x64.deb`
2. Install: `sudo dpkg -i handyplayer-1.0.0-linux-x64.deb`
3. Launch from application menu or run `HandyPlayer`
4. Uninstall: `sudo dpkg -r handyplayer`

To build yourself:
```bash
chmod +x publish-linux.sh
./publish-linux.sh                 # portable tar.gz + .deb package (+ AppImage if appimagetool is installed)
```

### Using Intiface Central (optional)

If you want to use the Intiface/Buttplug backend instead of the direct Handy API:

1. Download Intiface Central from [https://intiface.com/central/](https://intiface.com/central/)
2. Install and launch Intiface Central
3. In Intiface, scan for your device
4. In HandyPlayer Settings, select "Intiface" as the backend and enter the WebSocket URL (default: `ws://localhost:12345`)

---

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Git](https://git-scm.com/)
- **libmpv** — the native video playback library (platform-specific, see below)

#### Windows

Download `libmpv-2.dll` and place it in the project:

```powershell
# Download from https://sourceforge.net/projects/mpv-player-windows/files/libmpv/
# Extract and copy the DLL into the project:
mkdir -p src/HandyPlaylistPlayer.Media.Mpv/runtimes/win-x64/native
cp libmpv-2.dll src/HandyPlaylistPlayer.Media.Mpv/runtimes/win-x64/native/
```

> **Tip:** Pre-built releases include libmpv already — this step is only needed for development builds.

#### macOS

Install libmpv via Homebrew, then copy the dylib into the project so the build can bundle it:

```bash
brew install mpv

# Apple Silicon (M1/M2/M3/M4)
mkdir -p src/HandyPlaylistPlayer.Media.Mpv/runtimes/osx-arm64/native
cp /opt/homebrew/lib/libmpv.2.dylib src/HandyPlaylistPlayer.Media.Mpv/runtimes/osx-arm64/native/

# Intel Mac (if applicable)
mkdir -p src/HandyPlaylistPlayer.Media.Mpv/runtimes/osx-x64/native
cp /usr/local/lib/libmpv.2.dylib src/HandyPlaylistPlayer.Media.Mpv/runtimes/osx-x64/native/
```

#### Linux

Install libmpv from your distribution's package manager, then copy it into the project:

```bash
# Debian / Ubuntu
sudo apt install libmpv-dev

# Fedora
sudo dnf install mpv-libs-devel

# Arch
sudo pacman -S mpv

# Copy into project (x64)
mkdir -p src/HandyPlaylistPlayer.Media.Mpv/runtimes/linux-x64/native
cp /usr/lib/x86_64-linux-gnu/libmpv.so.2 src/HandyPlaylistPlayer.Media.Mpv/runtimes/linux-x64/native/
# Path may vary by distro — use: find /usr -name "libmpv.so*" to locate it
```

### Clone, Build & Run

```bash
git clone <repository-url>
cd Handy
dotnet restore
dotnet build
dotnet run --project src/HandyPlaylistPlayer.App
```

> **macOS (Apple Silicon):** After building, you must ad-hoc sign the native libraries or macOS will reject them:
> ```bash
> find src/HandyPlaylistPlayer.App/bin -name "*.dylib" -exec codesign --force --sign - {} \;
> dotnet run --project src/HandyPlaylistPlayer.App
> ```
> This only needs to be done once after a clean build. The `publish-mac.sh` script handles this automatically.

### Run Tests

```bash
dotnet test
```

9 of the 271 tests require a physical Handy device and are skipped automatically when no device is connected.

---

## Building Release Packages

Each platform has a publish script that produces a portable archive and an installer package. All builds are **self-contained** — no .NET runtime required on the target machine.

### Windows x64

```powershell
./publish-win.ps1
```

**Outputs:**
| File | Type | Description |
|---|---|---|
| `publish/HandyPlayer-1.0.0-win-x64.zip` | Portable | Extract and run `HandyPlayer.exe` |
| `publish/HandyPlayer-1.0.0-win-x64.msi` | Installer | Standard Windows MSI, installs to Program Files with Start Menu shortcut |

The MSI installer requires [WiX Toolset v5](https://wixtoolset.org/). One-time setup:
```powershell
dotnet tool install --global wix
wix extension add WixToolset.UI.wixext
```

If WiX is not installed, the script still produces the portable zip and skips the MSI.

### macOS ARM64

```bash
chmod +x publish-mac.sh
./publish-mac.sh
```

**Outputs:**
| File | Type | Description |
|---|---|---|
| `publish/HandyPlayer-1.0.0-mac-arm64.zip` | Portable | Extract and drag `.app` to `/Applications` |
| `publish/HandyPlayer-1.0.0-mac-arm64.dmg` | Installer | Drag-to-Applications disk image |

Note: macOS Gatekeeper will block unsigned apps on first launch. Users must right-click the app and select "Open".

### Linux x64

```bash
chmod +x publish-linux.sh
./publish-linux.sh
```

**Outputs:**
| File | Type | Description |
|---|---|---|
| `publish/HandyPlayer-1.0.0-linux-x64.tar.gz` | Portable | Extract and run `./HandyPlayer`. Includes `install.sh` for desktop integration |
| `publish/handyplayer-1.0.0-linux-x64.deb` | Installer | Debian/Ubuntu package, installs to `/opt/HandyPlayer` |
| `publish/HandyPlayer-1.0.0-linux-x64.AppImage` | AppImage | Single-file executable (only if `appimagetool` is installed) |

The portable `install.sh` supports both user and system-wide install:
```bash
./install.sh                       # user install (~/.local/opt)
sudo ./install.sh                  # system install (/opt)
```

### Manual Publish (any platform)

```bash
dotnet publish src/HandyPlaylistPlayer.App -c Release -r <RID> --self-contained -o publish/<output>
```

Common runtime identifiers: `win-x64`, `osx-arm64`, `linux-x64`

---

## Getting Started

### Try the Demo Content

The `demo/` folder includes 3 short test videos with matching funscript files — no need to find your own content first:

1. In **Settings**, under "Library Folders", click **Add Folder** and select the `demo/` directory
2. Go to the **Library** page and click **Scan All**
3. Double-click any demo video to play — the script pairs automatically

| Demo | Motion Pattern |
|------|---------------|
| Demo_SlowWave | Gentle vertical drift with varying speed |
| Demo_FastPulse | Quick vertical strokes with varying amplitude |
| Demo_Ramp | Builds intensity up then back down |
| Demo_Scanner | Glowing horizontal sweep with synced script |

### 1. Configure Your Device

1. Launch the app
2. Go to **Settings** (bottom of the sidebar)
3. Select your backend:
   - **Handy API**: Enter your Handy connection key (found in the Handy app or at [handyfeeling.com](https://www.handyfeeling.com/))
   - **Intiface**: Enter the WebSocket URL (requires Intiface Central running)
4. Click **Save**

### 2. Add Library Folders

1. In **Settings**, under "Library Folders", click **Add Folder**
2. Select the folder containing your videos and/or scripts
3. Repeat for additional folders (including network shares)

### 3. Scan Your Library

1. Go to the **Library** page
2. Click **Scan All** - the app will index all video and script files
3. Auto-pairing runs automatically after scanning

### 4. Play Content

1. In the **Library**, double-click a video to start playback
2. If a matching script was found, device control starts automatically
3. Use the transport controls or keyboard shortcuts to control playback

### 5. Adjust Device Settings

1. Click the **Overrides** button in the player controls (or press `O`)
2. Adjust the movement range sliders to set upper and lower limits
3. Use the offset nudge buttons to fine-tune latency compensation
4. Toggle **Invert** or adjust **Speed Limit** as needed

### 6. Create Playlists

1. Go to the **Playlists** page
2. Enter a name and click **Create**
3. Use **Play All** or **Shuffle Play** to start playback

---

## Project Structure

```
src/
  HandyPlaylistPlayer.App/            # UI layer (Avalonia + MVVM)
  HandyPlaylistPlayer.Core/           # Vertical slice features, CQRS dispatching, runtime services
    Dispatching/                       # IDispatcher, command/query infrastructure, validators
    Features/                          # Feature slices (Library, Playback, Queue, Device, etc.)
    Runtime/                           # Stateful singleton services (PlaybackCoordinator, etc.)
  HandyPlaylistPlayer.Media.Mpv/      # Video player adapter (libmpv P/Invoke)
  HandyPlaylistPlayer.Devices.HandyApi/  # Handy REST API client
  HandyPlaylistPlayer.Devices.Intiface/  # Buttplug/Intiface client
  HandyPlaylistPlayer.Storage/         # SQLite database layer
tests/
  HandyPlaylistPlayer.Tests/           # Unit tests (271 tests, 9 require hardware)
```

For detailed technical documentation, architecture diagrams, and design patterns, see [TECHNICAL.md](TECHNICAL.md).

---

## Tech Stack

| Component | Technology |
|---|---|
| Framework | .NET 10 |
| UI | Avalonia 11.2 (cross-platform XAML) |
| MVVM | CommunityToolkit.Mvvm |
| Video Player | libmpv (P/Invoke, ships as native DLL) |
| Database | SQLite (Microsoft.Data.Sqlite) |
| Device Control | Handy API v2, Buttplug 5.0 |
| Logging | Serilog |
| Testing | xUnit v3, NSubstitute |

---

## License

This project is for personal use.
