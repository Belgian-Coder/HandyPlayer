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
