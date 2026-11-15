#Requires -Version 7
<#
.SYNOPSIS
    Restores the locked Windows libmpv build into this checkout without replacing unknown files.
#>
param(
    [switch]$Plan,
    # Optional existing archive; its size and SHA-256 must match the lock.
    [string]$ArchivePath,
    [string]$EvidencePath,
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$lock = Get-Content -LiteralPath (Join-Path $ProjectRoot 'eng/native-dependencies.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1) { throw 'Unsupported native dependency lock schema.' }
if ($Plan) {
    $lock.'win-x64' | ConvertTo-Json -Depth 5
    exit 0
}
if (-not $IsWindows) { throw 'This native dependency restore requires native Windows.' }
Set-Location -LiteralPath $ProjectRoot
. (Join-Path $ProjectRoot 'harness-env.ps1')
. (Join-Path $ProjectRoot 'eng/Native-Dependency.ps1')
$candidates = if ($ArchivePath) { @((Resolve-Path -LiteralPath $ArchivePath).Path) } else { @() }
$receipt = Restore-HandyNativeDependency -CheckoutRoot $ProjectRoot -Specification $lock.'win-x64' `
    -ArchiveCandidates $candidates -EvidencePath $EvidencePath
$receipt | ConvertTo-Json -Depth 5
