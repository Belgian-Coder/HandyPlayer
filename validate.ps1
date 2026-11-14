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
    [switch]$IncludeLiveDevice,
    [ValidateSet('All', 'Unit', 'UI')]
    [string]$Suite = 'All',
    # xUnit MTP class filter (fully qualified name, * wildcard supported).
    [string]$Class,
    # Use only after a successful restore for this checkout/configuration.
    [switch]$NoRestore,
    # Print the exact command plan without invoking dotnet or creating state.
    [switch]$Plan
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$solution = "HandyPlaylistPlayer.sln"
$results = Join-Path $PSScriptRoot "TestResults"
$target = switch ($Suite) {
    'Unit' { 'tests/HandyPlaylistPlayer.Tests/HandyPlaylistPlayer.Tests.csproj' }
    'UI' { 'tests/HandyPlaylistPlayer.UiTests/HandyPlaylistPlayer.UiTests.csproj' }
    default { $solution }
}
if (-not $Plan) { . "$PSScriptRoot/harness-env.ps1" }

function Invoke-Step([string]$Name, [string[]]$Arguments) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    if ($Plan) {
        Write-Host ('dotnet ' + (($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '))
        return
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host ("    {0} ok in {1:n1}s" -f $Name, $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
}

if (-not $NoBuild) {
    if (-not $NoRestore) { Invoke-Step 'restore' @('restore', $target, '--nologo') }
    Invoke-Step 'build' @('build', $target, '-c', $Configuration, '--no-restore', '--nologo', '-warnaserror')
}

$testArgs = @(
    'test', $(if ($Suite -eq 'All') { '--solution' } else { '--project' }), $target,
    "-c", $Configuration, "--no-build",
    "--results-directory", $results,
    '--minimum-expected-tests', '1',
    "--report-xunit-trx"
)
if (-not $IncludeLiveDevice) { $testArgs += @("--filter-not-trait", "Category=LiveDevice") }
if ($Coverage) { $testArgs += @("--coverlet", "--coverlet-output-format", "cobertura") }
if ($Class) { $testArgs += @('--filter-class', $Class) }

Invoke-Step "test" $testArgs

if ($Plan) { Write-Host 'Plan only; validation was not run.' }
else { Write-Host "Validation passed ($Suite). Test results: $results" -ForegroundColor Green }
