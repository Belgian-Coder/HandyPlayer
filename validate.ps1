#Requires -Version 7
<#
.SYNOPSIS
    Single validation entry point for HandyPlayer: restore, build (warnings as errors), and run the hermetic test suite.
    CI runs exactly this script, so a green local run means a green CI run on the same OS.

.EXAMPLE
    pwsh ./validate.ps1
.EXAMPLE
    pwsh ./validate.ps1 -Coverage
.EXAMPLE
    # Also run the live Handy device tests (needs HANDY_CONNECTION_KEY and an online device)
    pwsh ./validate.ps1 -IncludeLiveDevice
#>
param(
    [string]$Configuration = "Release",
    # Collect Cobertura coverage via coverlet.MTP into TestResults/.
    [switch]$Coverage,
    # Reuse the existing build output (e.g. right after an IDE build).
    [switch]$NoBuild,
    [switch]$IncludeLiveDevice
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$solution = "HandyPlaylistPlayer.sln"
$results = Join-Path $PSScriptRoot "TestResults"

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $Command
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host ("    {0} ok in {1:n1}s" -f $Name, $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
}

if (-not $NoBuild) {
    Invoke-Step "restore" { dotnet restore $solution --nologo }
    Invoke-Step "build" { dotnet build $solution -c $Configuration --no-restore --nologo -warnaserror }
}

$testArgs = @(
    "--solution", $solution, "-c", $Configuration, "--no-build",
    "--results-directory", $results,
    "--report-xunit-trx"
)
if (-not $IncludeLiveDevice) { $testArgs += @("--filter-not-trait", "Category=LiveDevice") }
if ($Coverage) { $testArgs += @("--coverlet", "--coverlet-output-format", "cobertura") }

Invoke-Step "test" { dotnet test @testArgs }

Write-Host "Validation passed. Test results: $results" -ForegroundColor Green
