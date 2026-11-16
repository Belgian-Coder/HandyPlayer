# Shared environment initialization for the existing validation and release entry points.
# NuGet packages and their coordination locks share one cache scope. Other
# mutable state stays in this checkout.
. "$PSScriptRoot/eng/Project-Root.ps1"
$primaryRoot = Get-HandyCacheProjectRoot $PSScriptRoot
$platform = if ([Environment]::OSVersion.Platform -eq 'Win32NT') { 'windows' } elseif ($IsMacOS) { 'macos' } else { 'linux' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$state = Join-Path $PSScriptRoot '.harness'
$env:NUGET_PACKAGES = Join-Path $primaryRoot ".harness-cache/$platform-$architecture/packages"
$env:NUGET_SCRATCH = Join-Path $primaryRoot ".harness-cache/$platform-$architecture/scratch"
$env:DOTNET_CLI_HOME = Join-Path $state 'dotnet'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $primaryRoot ".harness-cache/$platform-$architecture/http"
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $state 'nuget-plugins'
$env:TEMP = $env:TMP = $env:TMPDIR = Join-Path $state 'tmp'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$directories = @($env:NUGET_PACKAGES, $env:NUGET_SCRATCH, $env:DOTNET_CLI_HOME,
    $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_PLUGINS_CACHE_PATH, $env:TEMP)
foreach ($directory in $directories) { Assert-HandyProjectStateDirectory $directory }
foreach ($directory in $directories) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}

function Get-HandyPipeDirectory([string]$CheckoutRoot, [string]$PrimaryRoot, [string]$Platform, [string]$Architecture) {
    # MTP appends MONITORTOHOST_<32 hex digits>; macOS permits 103 UTF-8 bytes.
    $local = "$CheckoutRoot/.harness/tmp"
    if ([Text.Encoding]::UTF8.GetByteCount($local) + 47 -le 103) {
        return [pscustomobject]@{ Path = $local; Fingerprint = $null }
    }
    $identity = [Text.Encoding]::UTF8.GetBytes("$CheckoutRoot|$Platform|$Architecture")
    $fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($identity)).ToLowerInvariant()
    $short = "$PrimaryRoot/.harness/i/$($fingerprint.Substring(0, 8))"
    if ([Text.Encoding]::UTF8.GetByteCount($short) + 47 -gt 103) {
        throw 'Project root is too long for Unix test sockets; use a shorter native project root.'
    }
    return [pscustomobject]@{ Path = $short; Fingerprint = $fingerprint }
}

function Initialize-HandyPipeDirectory($Selection) {
    $directoryExisted = Test-Path -LiteralPath $Selection.Path
    $cursor = [IO.Path]::GetFullPath($Selection.Path)
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Test sockets must not use a link/junction: $cursor" }
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($parent) { $parent.FullName } else { $null }
    }
    [IO.Directory]::CreateDirectory($Selection.Path) | Out-Null
    if ($Selection.Fingerprint) {
        $marker = Join-Path $Selection.Path '.owner'
        $existing = Get-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
        if ($existing) {
            if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                [IO.File]::ReadAllText($marker) -cne $Selection.Fingerprint) {
                throw 'Test socket namespace belongs to another checkout/platform/architecture; preserved.'
            }
        } else {
            if ($directoryExisted) { throw 'Unknown existing test socket namespace has no owner marker; preserved.' }
            $stream = [IO.File]::Open($marker, 'CreateNew', 'Write', 'None')
            try {
                $bytes = [Text.Encoding]::ASCII.GetBytes($Selection.Fingerprint)
                $stream.Write($bytes, 0, $bytes.Length)
            } finally { $stream.Dispose() }
        }
    }
    return $Selection.Path
}

if (-not $IsWindows) {
    $pipeSelection = Get-HandyPipeDirectory $PSScriptRoot $primaryRoot $platform $architecture
    if ($env:TESTINGPLATFORM_PIPE_DIRECTORY -and $env:TESTINGPLATFORM_PIPE_DIRECTORY -cne $pipeSelection.Path) {
        $explicit = [IO.Path]::GetFullPath($env:TESTINGPLATFORM_PIPE_DIRECTORY)
        if (-not $explicit.StartsWith(($PSScriptRoot + '/'), [StringComparison]::Ordinal)) {
            throw 'Explicit test pipe directory must be checkout-local or match the owned fallback.'
        }
        if ([Text.Encoding]::UTF8.GetByteCount($explicit) + 47 -gt 103) { throw 'Explicit test pipe directory exceeds the Unix socket byte limit.' }
        $pipeSelection = [pscustomobject]@{ Path = $explicit; Fingerprint = $null }
    }
    $env:TESTINGPLATFORM_PIPE_DIRECTORY = Initialize-HandyPipeDirectory $pipeSelection
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
