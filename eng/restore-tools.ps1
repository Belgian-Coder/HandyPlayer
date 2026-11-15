#Requires -Version 7
<#
.SYNOPSIS
    Restores the pinned WiX command-line tool into this checkout on explicit request.
#>
param(
    [switch]$Plan,
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
$version = '6.0.2'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$toolDirectory = [IO.Path]::GetFullPath((Join-Path $ProjectRoot '.harness/tools/wix-6.0.2'))

if ($Plan) {
    [pscustomobject]@{
        tool = 'wix'
        version = $version
        toolPath = $toolDirectory
        command = "dotnet tool install wix --version $version --tool-path `"$toolDirectory`""
    } | ConvertTo-Json -Compress
    return
}

if (-not $IsWindows) { throw 'The WiX tool restore requires native Windows.' }
if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) { throw "Project root does not exist: $ProjectRoot" }
. (Join-Path $PSScriptRoot 'Windows-Tools.ps1')
Assert-HandyNoReparseAncestors $toolDirectory
$wixPath = Get-HandyWixPath $ProjectRoot

# Validate before the shared environment helper creates checkout-local state.
Set-Location -LiteralPath $ProjectRoot
. (Join-Path $ProjectRoot 'harness-env.ps1')

function Get-InstalledHandyWixVersion([string]$Directory) {
    $lines = @(& dotnet tool list --tool-path $Directory 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Cannot identify the existing WiX tool target; preserving it: $Directory" }
    $entries = @(
        foreach ($line in $lines) {
            if ($line -match '^\s*(\S+)\s+(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)\s+(\S+)') {
                [pscustomobject]@{ Id = $Matches[1]; Version = $Matches[2] }
            }
        }
    )
    if ($entries.Count -ne 1 -or $entries[0].Id -ine 'wix') {
        throw "Existing WiX tool target is unknown; preserving it: $Directory"
    }
    return $entries[0].Version
}

if ($wixPath) {
    $installedVersion = Get-InstalledHandyWixVersion $toolDirectory
    if ($installedVersion -notmatch '^6\.0\.2(?:\+[0-9A-Za-z.-]+)?$') {
        throw "Existing WiX version '$installedVersion' does not match pinned version $version; preserving it."
    }
    [pscustomobject]@{ status = 'reused'; tool = 'wix'; version = $installedVersion; path = $wixPath } | ConvertTo-Json -Compress
    return
}

& dotnet tool install wix --version $version --tool-path $toolDirectory
if ($LASTEXITCODE -ne 0) { throw "WiX $version restore failed with exit code $LASTEXITCODE; retaining any partial files at $toolDirectory." }
$wixPath = Get-HandyWixPath $ProjectRoot
if (-not $wixPath) { throw "WiX restore returned success without creating wix.exe; retaining restore evidence at $toolDirectory." }
$installedVersion = Get-InstalledHandyWixVersion $toolDirectory
if ($installedVersion -notmatch '^6\.0\.2(?:\+[0-9A-Za-z.-]+)?$') {
    throw "WiX restore produced version '$installedVersion' instead of pinned version $version; retaining files at $toolDirectory."
}
[pscustomobject]@{ status = 'installed'; tool = 'wix'; version = $installedVersion; path = $wixPath } | ConvertTo-Json -Compress
