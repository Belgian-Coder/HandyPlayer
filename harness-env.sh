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
mkdir -p "$NUGET_PACKAGES" "$DOTNET_CLI_HOME" "$NUGET_HTTP_CACHE_PATH" \
    "$NUGET_PLUGINS_CACHE_PATH" "$TMPDIR" || return 1
