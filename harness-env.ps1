# Shared environment initialization for the existing validation and release entry points.
# Only immutable NuGet packages are shared; mutable state stays in this checkout.
$commonDir = & git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir 2>$null
if ($LASTEXITCODE -ne 0) { throw 'A Git checkout is required to locate the project cache.' }
$primaryRoot = Split-Path $commonDir -Parent
$platform = if ([Environment]::OSVersion.Platform -eq 'Win32NT') { 'windows' } elseif ($IsMacOS) { 'macos' } else { 'linux' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$state = Join-Path $PSScriptRoot '.harness'
$env:NUGET_PACKAGES = Join-Path $primaryRoot ".harness-cache/$platform-$architecture/packages"
$env:DOTNET_CLI_HOME = Join-Path $state 'dotnet'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $state 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $state 'nuget-plugins'
$env:TEMP = $env:TMP = $env:TMPDIR = Join-Path $state 'tmp'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
foreach ($directory in @($env:NUGET_PACKAGES, $env:DOTNET_CLI_HOME,
    $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_PLUGINS_CACHE_PATH, $env:TEMP)) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}

function New-HandyOutputRoot([string]$OutputDirectory, [switch]$AllowExisting) {
    if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) {
        throw 'Output directory must be an absolute path inside this checkout.'
    }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $rootPrefix = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $output.StartsWith($rootPrefix, $comparison)) {
        throw 'Output directory must stay inside this checkout.'
    }
    & git -C $PSScriptRoot check-ignore --quiet -- $output
    if ($LASTEXITCODE -ne 0) { throw 'Output directory must be Git-ignored.' }
    $parent = $PSScriptRoot
    foreach ($part in $output.Substring($rootPrefix.Length).Split([IO.Path]::DirectorySeparatorChar)) {
        $parent = Join-Path $parent $part
        $existing = Get-Item -LiteralPath $parent -Force -ErrorAction SilentlyContinue
        if ($existing -and ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Output must not use a link/junction: $parent"
        }
    }
    if (Test-Path -LiteralPath $output) {
        if ($AllowExisting -and (Test-Path -LiteralPath $output -PathType Container)) { return $output }
        throw "Output already exists; retain it and select a fresh path: $output"
    }
    [IO.Directory]::CreateDirectory((Split-Path $output -Parent)) | Out-Null
    New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
    return $output
}

function New-HandyReleaseRoot([ValidateSet('win-x64', 'linux-x64', 'osx-arm64')][string]$Rid) {
    # The adapter supplies a fresh, ignored path; ordinary publishes retain random roots.
    if ($env:HANDY_RELEASE_OUTPUT) { return New-HandyOutputRoot $env:HANDY_RELEASE_OUTPUT }
    # Reject redirected ancestors before making a unique, task-owned output root.
    $parent = $PSScriptRoot
    foreach ($part in @('publish', 'releases', $Rid)) {
        $parent = Join-Path $parent $part
        if ((Test-Path -LiteralPath $parent) -and
            ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Release output must not use a link/junction: $parent"
        }
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    $output = Join-Path $parent ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
    return $output
}
