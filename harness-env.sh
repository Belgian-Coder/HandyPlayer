# Source from the repository root, after the caller has changed directory there.
# Match harness-env.ps1: share packages by host OS/architecture, isolate mutable state.
HANDY_ROOT=$(pwd -P)
HANDY_COMMON=$(git rev-parse --path-format=absolute --git-common-dir) || return 1
HANDY_PRIMARY=$(dirname "$HANDY_COMMON")
case "$(uname -s)" in
    Darwin) HANDY_OS=macos ;;
    Linux) HANDY_OS=linux ;;
    *) echo 'Use PowerShell for native Windows builds.' >&2; return 1 ;;
esac
case "$(uname -m)" in
    x86_64|amd64) HANDY_ARCH=x64 ;;
    arm64|aarch64) HANDY_ARCH=arm64 ;;
    *) echo 'Unsupported build host architecture.' >&2; return 1 ;;
esac
export NUGET_PACKAGES="$HANDY_PRIMARY/.harness-cache/$HANDY_OS-$HANDY_ARCH/packages"
export DOTNET_CLI_HOME="$HANDY_ROOT/.harness/dotnet"
export NUGET_HTTP_CACHE_PATH="$HANDY_ROOT/.harness/nuget-http"
export NUGET_PLUGINS_CACHE_PATH="$HANDY_ROOT/.harness/nuget-plugins"
export TMPDIR="$HANDY_ROOT/.harness/tmp"
export TMP="$TMPDIR" TEMP="$TMPDIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
case "${HANDY_MAX_PARALLELISM:-2}" in
    [1-9]|[1-5][0-9]|6[0-4]) HANDY_MAX_PARALLELISM="${HANDY_MAX_PARALLELISM:-2}" ;;
    *) echo 'HANDY_MAX_PARALLELISM must be 1..64.' >&2; return 1 ;;
esac
mkdir -p "$NUGET_PACKAGES" "$DOTNET_CLI_HOME" "$NUGET_HTTP_CACHE_PATH" \
    "$NUGET_PLUGINS_CACHE_PATH" "$TMPDIR" || return 1

# MTP adds a 46-byte pipe name and slash; macOS permits 103 UTF-8 bytes.
HANDY_PIPE_PROJECT="$HANDY_PRIMARY"
if [ -n "${BUILD_RELEASE_PROJECT_ROOT:-}" ]; then
    # The shared executor supplies its validated outer root for independent clones.
    # Native Python already hosts that executor; isolate this read-only JSON check.
    python3 -I -B -c 'import json, pathlib, subprocess, sys
r=pathlib.Path(sys.argv[1]); c=pathlib.Path(sys.argv[2]); m=r/".request-release-owner.json"
assert r.is_absolute() and r.resolve()==r and (r==c or r in c.parents), "Invalid build project root hint"
assert not m.is_symlink() and json.loads(m.read_text())=={"owner":"request-release","version":1,"project":"HandyPlayer"}, "Unrelated build project owner"
assert pathlib.Path(subprocess.check_output(["git","-C",str(r),"rev-parse","--show-toplevel"],text=True).strip())==r, "Build project hint is not the exact Git root"
origin=lambda p: subprocess.check_output(["git","-C",str(p),"config","--get","remote.origin.url"],text=True).strip()
o=origin(r); assert o and o==origin(c), "Build project root must have the same canonical origin as this checkout"' "$BUILD_RELEASE_PROJECT_ROOT" "$HANDY_ROOT" || return 1
    HANDY_PIPE_PROJECT="$BUILD_RELEASE_PROJECT_ROOT"
fi
HANDY_PIPE_DIR="$TMPDIR"
HANDY_PIPE_HASH=""
if [ "$(printf '%s' "$HANDY_PIPE_DIR" | LC_ALL=C wc -c | tr -d ' ')" -gt 56 ]; then
    if command -v sha256sum >/dev/null 2>&1; then
        HANDY_PIPE_HASH=$(printf '%s' "$HANDY_ROOT|$HANDY_OS|$HANDY_ARCH" | sha256sum | cut -d ' ' -f1)
    else
        HANDY_PIPE_HASH=$(printf '%s' "$HANDY_ROOT|$HANDY_OS|$HANDY_ARCH" | shasum -a 256 | cut -d ' ' -f1) || return 1
    fi
    [ "${#HANDY_PIPE_HASH}" -eq 64 ] || { echo 'Could not fingerprint the test socket namespace.' >&2; return 1; }
    HANDY_PIPE_DIR="$HANDY_PIPE_PROJECT/.harness/i/${HANDY_PIPE_HASH:0:8}"
    [ "$(printf '%s' "$HANDY_PIPE_DIR" | LC_ALL=C wc -c | tr -d ' ')" -le 56 ] || {
        echo 'Project root is too long for Unix test sockets; use a shorter native project root.' >&2; return 1;
    }
fi
if [ -n "${TESTINGPLATFORM_PIPE_DIRECTORY:-}" ] && [ "$TESTINGPLATFORM_PIPE_DIRECTORY" != "$HANDY_PIPE_DIR" ]; then
    case "$TESTINGPLATFORM_PIPE_DIRECTORY" in
        "$HANDY_ROOT/"*) ;;
        *) echo 'Explicit test pipe directory must be checkout-local or match the owned fallback.' >&2; return 1 ;;
    esac
    [ "$(printf '%s' "$TESTINGPLATFORM_PIPE_DIRECTORY" | LC_ALL=C wc -c | tr -d ' ')" -le 56 ] || {
        echo 'Explicit test pipe directory exceeds the Unix socket byte limit.' >&2; return 1;
    }
    case "$TESTINGPLATFORM_PIPE_DIRECTORY/" in */../*|*/./*|*//*) echo 'Explicit test pipe directory must use a normalized path.' >&2; return 1 ;; esac
    HANDY_PIPE_DIR="$TESTINGPLATFORM_PIPE_DIRECTORY"
    HANDY_PIPE_HASH=""
fi
HANDY_PIPE_PARENT="$HANDY_PIPE_DIR"
while [ "$HANDY_PIPE_PARENT" != / ]; do
    [ ! -L "$HANDY_PIPE_PARENT" ] || { echo 'Test sockets must not use a symlink.' >&2; return 1; }
    HANDY_PIPE_PARENT=$(dirname "$HANDY_PIPE_PARENT")
done
HANDY_PIPE_EXISTED=0
[ ! -e "$HANDY_PIPE_DIR" ] || HANDY_PIPE_EXISTED=1
mkdir -p "$HANDY_PIPE_DIR" || return 1
if [ -n "$HANDY_PIPE_HASH" ]; then
    HANDY_PIPE_MARKER="$HANDY_PIPE_DIR/.owner"
    [ ! -L "$HANDY_PIPE_MARKER" ] || { echo 'Test socket owner must not be a symlink.' >&2; return 1; }
    if [ -e "$HANDY_PIPE_MARKER" ]; then
        [ "$(cat "$HANDY_PIPE_MARKER")" = "$HANDY_PIPE_HASH" ] || {
            echo 'Test socket namespace belongs to another checkout/platform/architecture; preserved.' >&2; return 1;
        }
    else
        [ "$HANDY_PIPE_EXISTED" -eq 0 ] || { echo 'Unknown existing test socket namespace has no owner marker; preserved.' >&2; return 1; }
        (set -C; printf '%s' "$HANDY_PIPE_HASH" > "$HANDY_PIPE_MARKER") || return 1
    fi
fi
export TESTINGPLATFORM_PIPE_DIRECTORY="$HANDY_PIPE_DIR"

handy_release_begin() {
    # Fresh output prevents cleanup from replacing another release's evidence.
    case "$1" in win-x64|linux-x64|osx-arm64) ;; *) return 1 ;; esac
    local part parent="$HANDY_ROOT"
    if [ -n "${HANDY_RELEASE_OUTPUT:-}" ]; then
        case "$HANDY_RELEASE_OUTPUT" in
            "$HANDY_ROOT/"*) ;;
            *) echo 'Output must be an absolute path inside this checkout.' >&2; return 1 ;;
        esac
        local relative="${HANDY_RELEASE_OUTPUT#"$HANDY_ROOT/"}"
        local parts
        IFS=/ read -r -a parts <<< "$relative"
        for part in "${parts[@]}"; do
            case "$part" in ''|.|..) echo 'Output must use a normalized path.' >&2; return 1 ;; esac
            parent="$parent/$part"
            [ ! -L "$parent" ] || { echo "Output must not use a symlink: $parent" >&2; return 1; }
        done
        git check-ignore --quiet -- "$HANDY_RELEASE_OUTPUT" || { echo 'Output must be Git-ignored.' >&2; return 1; }
        [ ! -e "$HANDY_RELEASE_OUTPUT" ] || { echo 'Output already exists; choose a fresh path.' >&2; return 1; }
        mkdir -p "$(dirname "$HANDY_RELEASE_OUTPUT")" || return 1
        mkdir "$HANDY_RELEASE_OUTPUT" || return 1
        RELEASE_ROOT="$HANDY_RELEASE_OUTPUT"
        trap 'echo "Cleanup checkpoint: prior releases/caches retained; failed-run evidence retained on errors. Output: $RELEASE_ROOT"' EXIT
        return 0
    fi
    for part in publish releases "$1"; do
        parent="$parent/$part"
        [ ! -L "$parent" ] || { echo "Release output must not use a symlink: $parent" >&2; return 1; }
        mkdir -p "$parent" || return 1
    done
    RELEASE_ROOT=$(mktemp -d "$parent/run.XXXXXXXX") || return 1
    trap 'echo "Cleanup checkpoint: prior releases/caches retained; failed-run evidence retained on errors. Output: $RELEASE_ROOT"' EXIT
}
